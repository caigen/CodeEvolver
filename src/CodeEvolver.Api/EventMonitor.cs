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
            var result = await agentRunner.RunAsync(evolution, currentEvent.Type, cancellationToken);
            currentEvent.Status = EvolutionEventStatus.Completed;
            currentEvent.Detail = result.Detail;
            currentEvent.CompletedAt = DateTimeOffset.UtcNow;
            foreach (var workItem in result.WorkItems.Take(5)) evolution.WorkItems.Add(workItem);
            foreach (var eventType in NextEvents(currentEvent.Type))
                evolution.Events.Add(eventType.EndsWith(".completed", StringComparison.Ordinal)
                    ? EvolutionCoordinator.CompletedEvent(eventType, result.Detail)
                    : new EvolutionEvent { Type = eventType });
            if (currentEvent.Type == EvolutionEventTypes.ChangeMerged)
            {
                evolution.Status = EvolutionStatus.Completed;
                evolution.Summary = result.Detail;
            }
            await store.SaveAsync(evolution, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Event {EventType} failed for evolution {EvolutionId}", currentEvent.Type, evolution.Id);
            currentEvent.Status = EvolutionEventStatus.Failed;
            currentEvent.Detail = exception.Message;
            currentEvent.CompletedAt = DateTimeOffset.UtcNow;
            evolution.Status = EvolutionStatus.Failed;
            evolution.Error = exception.Message;
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

public sealed record AgentResult(string Detail, IReadOnlyList<WorkItem> WorkItems);
public interface IAgentRunner { Task<AgentResult> RunAsync(Evolution evolution, string eventType, CancellationToken cancellationToken); }

public sealed class LocalAgentRunner : IAgentRunner
{
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
        return Task.FromResult(new AgentResult(detail, workItems));
    }
}