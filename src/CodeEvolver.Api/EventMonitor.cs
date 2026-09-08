namespace CodeEvolver.Api;

public sealed class EventMonitor(IEvolutionStore store, IAgentRunner agentRunner, ILogger<EventMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedEventsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = await store.ClaimNextEventAsync(stoppingToken);
            if (claimed is null) { await Task.Delay(500, stoppingToken); continue; }
            await ProcessAsync(claimed.Value.Evolution, claimed.Value.Event, stoppingToken);
        }
    }

    public async Task RecoverInterruptedEventsAsync(CancellationToken cancellationToken)
    {
        var evolutions = await store.ListAsync(cancellationToken);
        foreach (var evolution in evolutions.Where(item => item.Status is EvolutionStatus.Running or EvolutionStatus.StopRequested))
        {
            var interruptedEvents = evolution.Events.Where(entry => entry.Status == EvolutionEventStatus.Processing).ToArray();
            if (interruptedEvents.Length == 0) continue;
            const string message = "The agent phase was interrupted when the Code Evolver service stopped. Start the evolution again to retry from a clean state.";
            foreach (var interruptedEvent in interruptedEvents)
            {
                interruptedEvent.Status = EvolutionEventStatus.Failed;
                interruptedEvent.Detail = message;
                interruptedEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} {message}");
                interruptedEvent.CompletedAt = DateTimeOffset.UtcNow;
            }
            evolution.Status = EvolutionStatus.Failed;
            evolution.Error = message;
            evolution.CompletedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(evolution, cancellationToken);
        }
    }

    public async Task ProcessAsync(Evolution evolution, EvolutionEvent currentEvent, CancellationToken cancellationToken)
    {
        try
        {
            if (currentEvent.Type == EvolutionEventTypes.WorkItemStarted)
            {
                var nextWorkItem = evolution.WorkItems.FirstOrDefault(item => item.Status == "planned");
                if (nextWorkItem is not null) nextWorkItem.Status = "working";
            }
            currentEvent.Prompt = agentRunner.GetPrompt(evolution, currentEvent.Type);
            currentEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} Dispatching {currentEvent.Type}.");
            await store.SaveAsync(evolution, cancellationToken);
            var progressGate = new SemaphoreSlim(1, 1);
            var result = await agentRunner.RunAsync(evolution, currentEvent.Type, async (message, progressToken) =>
            {
                await progressGate.WaitAsync(progressToken);
                try
                {
                    var latestEvolution = await store.GetAsync(evolution.Id, progressToken);
                    var latestEvent = latestEvolution?.Events.SingleOrDefault(entry => entry.Id == currentEvent.Id);
                    if (latestEvolution is null || latestEvent is null) return;
                    latestEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} {message}");
                    await store.SaveAsync(latestEvolution, progressToken);
                }
                finally
                {
                    progressGate.Release();
                }
            }, cancellationToken);
            var latest = await store.GetAsync(evolution.Id, cancellationToken);
            if (latest is null) throw new InvalidOperationException($"Evolution {evolution.Id} disappeared while its agent was running.");
            evolution = latest;
            currentEvent = evolution.Events.Single(entry => entry.Id == currentEvent.Id);
            currentEvent.Status = EvolutionEventStatus.Completed;
            currentEvent.Detail = result.Detail;
            currentEvent.Logs.AddRange(result.Logs);
            currentEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} Completed {currentEvent.Type}.");
            currentEvent.CompletedAt = DateTimeOffset.UtcNow;
            if (latest?.Status == EvolutionStatus.StopRequested)
            {
                latest.Status = EvolutionStatus.Stopped;
                latest.CompletedAt = DateTimeOffset.UtcNow;
                latest.Events.Add(EvolutionCoordinator.CompletedEvent(EvolutionEventTypes.EvolutionStopped));
                await store.SaveAsync(latest, cancellationToken);
                return;
            }
            if (currentEvent.Type == EvolutionEventTypes.PlanStarted && result.WorkItems.Count == 0)
                throw new InvalidOperationException("The planning agent did not return any valid work items.");
            foreach (var workItem in result.WorkItems.Take(5)) evolution.WorkItems.Add(workItem);
            if (currentEvent.Type == EvolutionEventTypes.WorkItemStarted)
            {
                var workItem = evolution.WorkItems.FirstOrDefault(item => item.Status == "working");
                if (workItem is not null)
                {
                    workItem.Status = "completed";
                    workItem.Result = result.Detail;
                }
            }
            foreach (var eventType in NextEvents(currentEvent.Type))
                evolution.Events.Add(eventType.EndsWith(".completed", StringComparison.Ordinal)
                    ? EvolutionCoordinator.CompletedEvent(eventType, result.Detail)
                    : new EvolutionEvent { Type = eventType });
            if (currentEvent.Type == EvolutionEventTypes.PlanStarted)
                evolution.Events.Add(new EvolutionEvent { Type = EvolutionEventTypes.WorkItemStarted });
            if (currentEvent.Type == EvolutionEventTypes.WorkItemStarted)
                evolution.Events.Add(new EvolutionEvent
                {
                    Type = evolution.WorkItems.Any(item => item.Status == "planned")
                        ? EvolutionEventTypes.WorkItemStarted
                        : EvolutionEventTypes.ReviewStarted
                });
            if (currentEvent.Type == EvolutionEventTypes.ChangeMerged)
            {
                evolution.Status = EvolutionStatus.Completed;
                evolution.Summary = result.Detail;
                evolution.CompletedAt = DateTimeOffset.UtcNow;
            }
            await store.SaveAsync(evolution, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Event {EventType} failed for evolution {EvolutionId}", currentEvent.Type, evolution.Id);
            currentEvent.Status = EvolutionEventStatus.Failed;
            currentEvent.Detail = exception.Message;
            currentEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} Failed: {exception.Message}");
            currentEvent.CompletedAt = DateTimeOffset.UtcNow;
            var activeWorkItem = evolution.WorkItems.FirstOrDefault(item => item.Status == "working");
            if (activeWorkItem is not null)
            {
                activeWorkItem.Status = "failed";
                activeWorkItem.Result = exception.Message;
            }
            evolution.Status = EvolutionStatus.Failed;
            evolution.Error = exception.Message;
            evolution.CompletedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(evolution, cancellationToken);
        }
    }

    private static IEnumerable<string> NextEvents(string eventType) => eventType switch
    {
        EvolutionEventTypes.ScanStarted => [EvolutionEventTypes.ScanCompleted, EvolutionEventTypes.PlanStarted],
        EvolutionEventTypes.PlanStarted => [EvolutionEventTypes.PlanCompleted],
        EvolutionEventTypes.WorkItemStarted => [EvolutionEventTypes.WorkItemCompleted],
        EvolutionEventTypes.ReviewStarted => [EvolutionEventTypes.ReviewCompleted, EvolutionEventTypes.GateStarted],
        EvolutionEventTypes.GateStarted => [EvolutionEventTypes.GateCompleted, EvolutionEventTypes.ChangeMerged],
        _ => []
    };
}

public sealed record AgentResult(string Detail, IReadOnlyList<WorkItem> WorkItems, IReadOnlyList<string> Logs);
public interface IAgentRunner
{
    string GetPrompt(Evolution evolution, string eventType);
    Task<AgentResult> RunAsync(Evolution evolution, string eventType, Func<string, CancellationToken, Task>? reportProgress, CancellationToken cancellationToken);
}

public sealed class LocalAgentRunner : IAgentRunner
{
    public string GetPrompt(Evolution evolution, string eventType) =>
        $"Local simulation for {eventType}: {evolution.Direction} (scope: {evolution.Scope}).";

    public Task<AgentResult> RunAsync(Evolution evolution, string eventType, Func<string, CancellationToken, Task>? reportProgress, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkItem> workItems = eventType == EvolutionEventTypes.PlanStarted
            ? [new WorkItem { Title = evolution.Direction, Description = $"Improve {evolution.Scope} in {evolution.RepositoryPath}." }]
            : [];
        var detail = eventType switch
        {
            EvolutionEventTypes.ScanStarted => $"Scanned scope '{evolution.Scope}'.",
            EvolutionEventTypes.PlanStarted => $"Planned {workItems.Count} work item(s).",
            EvolutionEventTypes.WorkItemStarted => "Applied the planned repository change.",
            EvolutionEventTypes.ReviewStarted => "Reviewed the change against the evolution direction.",
            EvolutionEventTypes.GateStarted => "Build and test gate passed.",
            EvolutionEventTypes.ChangeMerged => $"Change is ready for branch '{evolution.TargetBranch}'.",
            _ => $"Processed {eventType}."
        };
        return Task.FromResult(new AgentResult(detail, workItems, [$"Local agent: {detail}"]));
    }
}