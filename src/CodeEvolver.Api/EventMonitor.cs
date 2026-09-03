namespace CodeEvolver.Api;

public sealed class EventMonitor(IEvolutionStore store, IAgentRunner agentRunner, ILogger<EventMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = await store.ClaimNextEventAsync(stoppingToken);
            if (claimed is null) { await Task.Delay(500, stoppingToken); continue; }
            await ProcessAsync(claimed.Value.Evolution, claimed.Value.Event, stoppingToken);
        }
    }

    public async Task ProcessAsync(Evolution evolution, EvolutionEvent currentEvent, CancellationToken cancellationToken)
    {
        try
        {
            currentEvent.Prompt = agentRunner.GetPrompt(evolution, currentEvent.Type);
            currentEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} Dispatching {currentEvent.Type}.");
            await store.SaveAsync(evolution, cancellationToken);
            var result = await agentRunner.RunAsync(evolution, currentEvent.Type, cancellationToken);
            currentEvent.Status = EvolutionEventStatus.Completed;
            currentEvent.Detail = result.Detail;
            currentEvent.Logs.AddRange(result.Logs);
            currentEvent.Logs.Add($"{DateTimeOffset.UtcNow:O} Completed {currentEvent.Type}.");
            currentEvent.CompletedAt = DateTimeOffset.UtcNow;
            var latest = await store.GetAsync(evolution.Id, cancellationToken);
            if (latest?.Status == EvolutionStatus.StopRequested)
            {
                var eventLogs = currentEvent.Logs.ToArray();
                var persistedEvent = latest.Events.Single(entry => entry.Id == currentEvent.Id);
                persistedEvent.Status = currentEvent.Status;
                persistedEvent.Detail = currentEvent.Detail;
                persistedEvent.Prompt = currentEvent.Prompt;
                persistedEvent.Logs.Clear();
                persistedEvent.Logs.AddRange(eventLogs);
                persistedEvent.StartedAt = currentEvent.StartedAt;
                persistedEvent.CompletedAt = currentEvent.CompletedAt;
                latest.Status = EvolutionStatus.Stopped;
                latest.CompletedAt = DateTimeOffset.UtcNow;
                latest.Events.Add(EvolutionCoordinator.CompletedEvent(EvolutionEventTypes.EvolutionStopped));
                await store.SaveAsync(latest, cancellationToken);
                return;
            }
            foreach (var workItem in result.WorkItems.Take(5)) evolution.WorkItems.Add(workItem);
            foreach (var eventType in NextEvents(currentEvent.Type))
                evolution.Events.Add(eventType.EndsWith(".completed", StringComparison.Ordinal)
                    ? EvolutionCoordinator.CompletedEvent(eventType, result.Detail)
                    : new EvolutionEvent { Type = eventType });
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
            evolution.Status = EvolutionStatus.Failed;
            evolution.Error = exception.Message;
            evolution.CompletedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(evolution, cancellationToken);
        }
    }

    private static IEnumerable<string> NextEvents(string eventType) => eventType switch
    {
        EvolutionEventTypes.ScanStarted => [EvolutionEventTypes.ScanCompleted, EvolutionEventTypes.PlanStarted],
        EvolutionEventTypes.PlanStarted => [EvolutionEventTypes.PlanCompleted, EvolutionEventTypes.WorkItemStarted],
        EvolutionEventTypes.WorkItemStarted => [EvolutionEventTypes.WorkItemCompleted, EvolutionEventTypes.ReviewStarted],
        EvolutionEventTypes.ReviewStarted => [EvolutionEventTypes.ReviewCompleted, EvolutionEventTypes.GateStarted],
        EvolutionEventTypes.GateStarted => [EvolutionEventTypes.GateCompleted, EvolutionEventTypes.ChangeMerged],
        _ => []
    };
}

public sealed record AgentResult(string Detail, IReadOnlyList<WorkItem> WorkItems, IReadOnlyList<string> Logs);
public interface IAgentRunner
{
    string GetPrompt(Evolution evolution, string eventType);
    Task<AgentResult> RunAsync(Evolution evolution, string eventType, CancellationToken cancellationToken);
}

public sealed class LocalAgentRunner : IAgentRunner
{
    public string GetPrompt(Evolution evolution, string eventType) =>
        $"Local simulation for {eventType}: {evolution.Direction} (scope: {evolution.Scope}).";

    public Task<AgentResult> RunAsync(Evolution evolution, string eventType, CancellationToken cancellationToken)
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