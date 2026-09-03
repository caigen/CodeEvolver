namespace CodeEvolver.Api;

public sealed class EvolutionCoordinator(IEvolutionStore store)
{
    public Task<Evolution> CreateAsync(CreateEvolutionRequest request, CancellationToken cancellationToken) =>
        store.SaveAsync(new Evolution
        {
            RepositoryPath = Path.GetFullPath(request.RepositoryPath.Trim()),
            Direction = request.Direction.Trim(),
            Scope = request.Scope.Trim(),
            TargetBranch = request.TargetBranch.Trim()
        }, cancellationToken);

    public async Task<Evolution?> UpdateAsync(Guid id, CreateEvolutionRequest request, CancellationToken cancellationToken)
    {
        var evolution = await store.GetAsync(id, cancellationToken);
        if (evolution is null || evolution.Status == EvolutionStatus.Running) return evolution;
        evolution.RepositoryPath = Path.GetFullPath(request.RepositoryPath.Trim());
        evolution.Direction = request.Direction.Trim();
        evolution.Scope = request.Scope.Trim();
        evolution.TargetBranch = request.TargetBranch.Trim();
        evolution.Status = EvolutionStatus.Draft;
        evolution.Summary = null;
        evolution.Error = null;
        evolution.StartedAt = null;
        evolution.CompletedAt = null;
        evolution.WorkItems.Clear();
        evolution.Events.Clear();
        return await store.SaveAsync(evolution, cancellationToken);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => store.DeleteAsync(id, cancellationToken);

    public async Task<Evolution?> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        var evolution = await store.GetAsync(id, cancellationToken);
        if (evolution is null || evolution.Status is EvolutionStatus.Running or EvolutionStatus.StopRequested or EvolutionStatus.Completed) return evolution;
        evolution.Status = EvolutionStatus.Running;
        evolution.Error = null;
        evolution.StartedAt = DateTimeOffset.UtcNow;
        evolution.CompletedAt = null;
        evolution.Events.Add(CompletedEvent(EvolutionEventTypes.EvolutionStarted));
        evolution.Events.Add(new EvolutionEvent { Type = EvolutionEventTypes.ScanStarted });
        return await store.SaveAsync(evolution, cancellationToken);
    }

    public async Task<Evolution?> StopAsync(Guid id, CancellationToken cancellationToken)
    {
        var evolution = await store.GetAsync(id, cancellationToken);
        if (evolution is null) return null;
        if (evolution.Status == EvolutionStatus.Running)
        {
            var hasActiveEvent = evolution.Events.Any(entry => entry.Status == EvolutionEventStatus.Processing);
            evolution.Status = hasActiveEvent ? EvolutionStatus.StopRequested : EvolutionStatus.Stopped;
            evolution.CompletedAt = hasActiveEvent ? null : DateTimeOffset.UtcNow;
            foreach (var entry in evolution.Events.Where(entry => entry.Status == EvolutionEventStatus.Pending))
            {
                entry.Status = EvolutionEventStatus.Cancelled;
                entry.CompletedAt = DateTimeOffset.UtcNow;
            }
            if (!hasActiveEvent) evolution.Events.Add(CompletedEvent(EvolutionEventTypes.EvolutionStopped));
            await store.SaveAsync(evolution, cancellationToken);
        }
        return evolution;
    }

    internal static EvolutionEvent CompletedEvent(string type, string? detail = null) => new()
    {
        Type = type,
        Status = EvolutionEventStatus.Completed,
        Detail = detail,
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow
    };
}