using System.Security.Cryptography;
using System.Text;

namespace CodeEvolver.Api;

public sealed class EventMonitor(IEvolutionStore store, IAgentRunner agentRunner, ILogger<EventMonitor> logger) : BackgroundService
{
    private readonly string workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await HeartbeatAsync(null, stoppingToken);
                var claimed = await store.ClaimNextEventAsync(stoppingToken);
                if (claimed is null) await Task.Delay(1000, stoppingToken);
                else await ProcessAsync(claimed.Value.Evolution, claimed.Value.Event, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Worker loop failed; retrying without losing persisted work.");
                await Task.Delay(3000, stoppingToken);
            }
        }
    }

    private Task<ServiceState> HeartbeatAsync(Guid? evolutionId, CancellationToken token) => store.ServiceAsync(state =>
    {
        state.Workers.RemoveAll(w => w.Id == workerId || w.LastSeenAt < DateTimeOffset.UtcNow.AddDays(-1));
        state.Workers.Add(new WorkerHeartbeat(workerId, DateTimeOffset.UtcNow, evolutionId));
    }, token);

    public async Task ProcessAsync(Evolution evolution, EvolutionEvent currentEvent, CancellationToken cancellationToken)
    {
        var owner = currentEvent.LeaseOwner ?? throw new LeaseLostException();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = MaintainLeaseAsync();
        try
        {
            // Local lock also prevents overlapping CLI writers if a lease expires during a DB outage.
            var lockDirectory = Path.Combine(Path.GetTempPath(), "code-evolver-repository-locks");
            Directory.CreateDirectory(lockDirectory);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TransactionalEvolutionStore.RepositoryKey(evolution.RepositoryPath))));
            await using var repositoryLock = new FileStream(Path.Combine(lockDirectory, hash + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            evolution = await store.UpdateClaimAsync(evolution.Id, currentEvent.Id, owner, (saved, entry) =>
            {
                if (entry.Type == EvolutionEventTypes.WorkItemStarted)
                {
                    var item = saved.WorkItems.FirstOrDefault(w => w.Cycle == saved.Cycle && w.Status == "planned");
                    if (item is not null) item.Status = "working";
                }
                entry.Prompt = agentRunner.GetPrompt(saved, entry.Type);
                entry.Logs.Add($"{DateTimeOffset.UtcNow:O} Dispatching {entry.Type}, attempt {entry.Attempts}, worker {workerId}.");
            }, execution.Token);
            var result = await agentRunner.RunAsync(evolution, currentEvent.Type, async (message, token) =>
            {
                await store.UpdateClaimAsync(evolution.Id, currentEvent.Id, owner,
                    (_, entry) => entry.Logs.Add($"{DateTimeOffset.UtcNow:O} {message}"), token);
            }, execution.Token);
            await store.UpdateClaimAsync(evolution.Id, currentEvent.Id, owner, (saved, entry) => Complete(saved, entry, result), execution.Token);
        }
        catch (LeaseLostException) { logger.LogWarning("Lease lost for {EvolutionId}; stale output discarded.", evolution.Id); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Phase failed for {EvolutionId}", evolution.Id);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await store.UpdateClaimAsync(evolution.Id, currentEvent.Id, owner, (saved, entry) =>
                {
                    var retry = exception is not AgentSafetyException && TransactionalEvolutionStore.IsReadOnlyPhase(entry.Type) && entry.Attempts < 3;
                    entry.Status = retry ? EvolutionEventStatus.Pending : EvolutionEventStatus.Failed;
                    entry.Detail = exception is OperationCanceledException ? "Worker interrupted; checkpoint preserved." : exception.Message;
                    entry.Logs.Add($"{DateTimeOffset.UtcNow:O} Attempt {entry.Attempts}: {entry.Detail}");
                    entry.CompletedAt = DateTimeOffset.UtcNow;
                    entry.NotBefore = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, entry.Attempts * 5));
                    entry.LeaseOwner = null;
                    entry.LeaseUntil = null;
                    if (saved.Status == EvolutionStatus.StopRequested)
                    {
                        entry.Status = EvolutionEventStatus.Cancelled;
                        saved.Status = EvolutionStatus.Stopped;
                        saved.CompletedAt = DateTimeOffset.UtcNow;
                    }
                    else if (!retry) { saved.Status = EvolutionStatus.Failed; saved.Error = entry.Detail; }
                    else if (saved.Status == EvolutionStatus.PauseRequested) saved.Status = EvolutionStatus.Paused;
                }, cleanup.Token);
            }
            catch (Exception persistenceError) { logger.LogWarning(persistenceError, "Failure checkpoint will be recovered on lease expiry."); }
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }

        async Task MaintainLeaseAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(heartbeatStop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    await store.UpdateClaimAsync(evolution.Id, currentEvent.Id, owner,
                        (_, entry) => entry.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(TransactionalEvolutionStore.LeaseSeconds), deadline.Token);
                    await HeartbeatAsync(evolution.Id, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Cannot renew lease; cancelling the owned CLI process.");
                await execution.CancelAsync();
            }
        }
    }

    internal static void Complete(Evolution evolution, EvolutionEvent entry, AgentResult result)
    {
        if (entry.Type == EvolutionEventTypes.PlanStarted && result.WorkItems.Count == 0)
            throw new InvalidOperationException("Planning agent returned no valid work items.");
        if (entry.Type == EvolutionEventTypes.GateStarted && !result.GatePassed)
            throw new InvalidOperationException("Gate did not return an explicit passing verdict. Publishing is blocked.");
        entry.Status = EvolutionEventStatus.Completed;
        entry.Detail = result.Detail;
        entry.Logs.AddRange(result.Logs);
        entry.CompletedAt = DateTimeOffset.UtcNow;
        entry.LeaseOwner = null;
        entry.LeaseUntil = null;
        if (entry.Type == EvolutionEventTypes.WorkItemStarted)
        {
            var item = evolution.WorkItems.FirstOrDefault(w => w.Cycle == evolution.Cycle && w.Status == "working");
            if (item is not null) { item.Status = "completed"; item.Result = result.Detail; }
        }
        if (evolution.Status == EvolutionStatus.StopRequested)
        {
            evolution.Status = EvolutionStatus.Stopped;
            evolution.CompletedAt = DateTimeOffset.UtcNow;
            evolution.Events.Add(EvolutionCoordinator.CompletedEvent(EvolutionEventTypes.EvolutionStopped, cycle: evolution.Cycle));
            return;
        }
        if (evolution.Kind == "analysis")
        {
            var phases = DataAnalyzer.CreateEvents().Select(e => e.Type).ToArray();
            var index = Array.IndexOf(phases, entry.Type);
            if (index == phases.Length - 1)
            {
                var recommendation = DataAnalyzer.ParseResult(result.Detail);
                evolution.Direction = recommendation.Direction;
                evolution.KeyPoints = recommendation.KeyPoints.ToList();
                evolution.Status = EvolutionStatus.Completed;
                evolution.CompletedAt = DateTimeOffset.UtcNow;
            }
            else EvolutionCoordinator.Enqueue(evolution, phases[index + 1]);
        }
        else switch (entry.Type)
        {
            case EvolutionEventTypes.ScanStarted:
                EvolutionCoordinator.Enqueue(evolution, EvolutionEventTypes.PlanStarted); break;
            case EvolutionEventTypes.PlanStarted:
                foreach (var item in result.WorkItems.Take(5)) { item.Cycle = evolution.Cycle; evolution.WorkItems.Add(item); }
                EvolutionCoordinator.Enqueue(evolution, EvolutionEventTypes.WorkItemStarted); break;
            case EvolutionEventTypes.WorkItemStarted:
                EvolutionCoordinator.Enqueue(evolution, evolution.WorkItems.Any(w => w.Cycle == evolution.Cycle && w.Status == "planned")
                    ? EvolutionEventTypes.WorkItemStarted : EvolutionEventTypes.ReviewStarted); break;
            case EvolutionEventTypes.ReviewStarted:
                EvolutionCoordinator.Enqueue(evolution, EvolutionEventTypes.GateStarted); break;
            case EvolutionEventTypes.GateStarted:
                EvolutionCoordinator.Enqueue(evolution, EvolutionEventTypes.ChangeMerged);
                if (evolution.PublishChanges)
                {
                    evolution.ApprovalCheckpoint = "publish";
                    if (evolution.Status != EvolutionStatus.PauseRequested) evolution.Status = EvolutionStatus.AwaitingApproval;
                }
                break;
            case EvolutionEventTypes.ChangeMerged:
                evolution.Summary = result.Detail;
                if (evolution.Cycle < evolution.MaxCycles)
                {
                    evolution.Cycle++;
                    evolution.ApprovedCycle = null;
                    evolution.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(evolution.CycleDelaySeconds);
                    EvolutionCoordinator.Enqueue(evolution, EvolutionEventTypes.ScanStarted);
                }
                else { evolution.Status = EvolutionStatus.Completed; evolution.CompletedAt = DateTimeOffset.UtcNow; }
                break;
        }
        if (evolution.Status == EvolutionStatus.PauseRequested) evolution.Status = EvolutionStatus.Paused;
    }
}

public sealed record AgentResult(string Detail, IReadOnlyList<WorkItem> WorkItems, IReadOnlyList<string> Logs, bool GatePassed = false);
public interface IAgentRunner
{
    string GetPrompt(Evolution evolution, string eventType);
    Task<AgentResult> RunAsync(Evolution evolution, string eventType, Func<string, CancellationToken, Task>? reportProgress, CancellationToken cancellationToken);
}

public sealed class LocalAgentRunner : IAgentRunner
{
    public string GetPrompt(Evolution evolution, string eventType) => $"SIMULATION: {eventType}, cycle {evolution.Cycle}: {evolution.Direction}";
    public Task<AgentResult> RunAsync(Evolution evolution, string eventType, Func<string, CancellationToken, Task>? reportProgress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WorkItem> items = eventType == EvolutionEventTypes.PlanStarted ? [new() { Title = evolution.Direction, Description = "Simulated work; no repository edits." }] : [];
        var detail = eventType == "analysis-summary.started" ? """{"direction":"Improve validation","keyPoints":["Simulated analysis"]}""" : $"SIMULATION completed {eventType}; no actual edits, tests or publishing.";
        return Task.FromResult(new AgentResult(detail, items, [], GatePassed: true));
    }
}