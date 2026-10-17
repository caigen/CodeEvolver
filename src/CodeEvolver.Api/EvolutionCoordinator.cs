namespace CodeEvolver.Api;

public sealed class EvolutionCoordinator(IEvolutionStore store)
{
    public Task<Evolution> CreateAsync(CreateEvolutionRequest request, CancellationToken cancellationToken) =>
        store.SaveAsync(new Evolution
        {
            RepositoryPath = Path.GetFullPath(request.RepositoryPath.Trim()),
            Direction = request.Direction.Trim(), Scope = request.Scope.Trim(), TargetBranch = request.TargetBranch.Trim(),
            MaxCycles = request.MaxCycles, CycleDelaySeconds = request.CycleDelaySeconds, PublishChanges = request.PublishChanges
        }, cancellationToken);

    public Task<Evolution?> UpdateAsync(Guid id, CreateEvolutionRequest request, CancellationToken token) => store.MutateAsync(id, evolution =>
    {
        if (evolution.Status != EvolutionStatus.Draft) throw new StoreConflictException("Only drafts can be edited. Create a new evolution to change a saved run's policy.");
        evolution.RepositoryPath = Path.GetFullPath(request.RepositoryPath.Trim());
        evolution.Direction = request.Direction.Trim();
        evolution.Scope = request.Scope.Trim();
        evolution.TargetBranch = request.TargetBranch.Trim();
        evolution.MaxCycles = request.MaxCycles;
        evolution.CycleDelaySeconds = request.CycleDelaySeconds;
        evolution.PublishChanges = request.PublishChanges;
    }, token);

    public Task<bool> DeleteAsync(Guid id, CancellationToken token) => store.DeleteAsync(id, token);

    public Task<Evolution?> StartAsync(Guid id, CancellationToken token) => store.MutateAsync(id, evolution =>
    {
        if (evolution.Status == EvolutionStatus.Running) return;
        if (evolution.Status != EvolutionStatus.Draft) throw new StoreConflictException("Use resume or retry to retain checkpoints; completed/stopped runs cannot be restarted.");
        evolution.Status = EvolutionStatus.Running;
        evolution.Cycle = 1;
        evolution.StartedAt = DateTimeOffset.UtcNow;
        evolution.Events.Add(CompletedEvent(EvolutionEventTypes.EvolutionStarted, cycle: evolution.Cycle));
        Enqueue(evolution, EvolutionEventTypes.ScanStarted);
    }, token);

    public Task<Evolution?> PauseAsync(Guid id, CancellationToken token) => store.MutateAsync(id, evolution =>
    {
        if (evolution.Status is EvolutionStatus.Paused or EvolutionStatus.PauseRequested) return;
        if (evolution.Status is not (EvolutionStatus.Running or EvolutionStatus.AwaitingApproval)) throw new StoreConflictException("Only active evolutions can be paused.");
        evolution.Status = evolution.Events.Any(e => e.Status == EvolutionEventStatus.Processing) ? EvolutionStatus.PauseRequested : EvolutionStatus.Paused;
        evolution.Events.Add(CompletedEvent("control.pause", cycle: evolution.Cycle));
    }, token);

    public Task<Evolution?> ResumeAsync(Guid id, CancellationToken token) => store.MutateAsync(id, evolution =>
    {
        if (evolution.Status == EvolutionStatus.Running) return;
        if (evolution.Status != EvolutionStatus.Paused) throw new StoreConflictException("Only paused evolutions can resume.");
        evolution.Status = evolution.ApprovalCheckpoint is null ? EvolutionStatus.Running : EvolutionStatus.AwaitingApproval;
        evolution.Events.Add(CompletedEvent("control.resume", cycle: evolution.Cycle));
    }, token);

    public Task<Evolution?> ApproveAsync(Guid id, CancellationToken token) => store.MutateAsync(id, evolution =>
    {
        if (evolution.Status != EvolutionStatus.AwaitingApproval || evolution.ApprovalCheckpoint != "publish")
            throw new StoreConflictException("No publication is awaiting approval.");
        evolution.ApprovedCycle = evolution.Cycle;
        evolution.ApprovalCheckpoint = null;
        evolution.Status = EvolutionStatus.Running;
        evolution.Events.Add(CompletedEvent("control.approve", "Publication approved for this cycle only.", evolution.Cycle));
    }, token);

    public Task<Evolution?> RetryAsync(Guid id, CancellationToken token) => store.MutateAsync(id, evolution =>
    {
        if (evolution.Status != EvolutionStatus.Failed) throw new StoreConflictException("Only failed evolutions can be retried.");
        var failed = evolution.Events.LastOrDefault(e => e.Status == EvolutionEventStatus.Failed)
            ?? throw new StoreConflictException("No failed checkpoint to retry.");
        failed.Status = EvolutionEventStatus.Pending;
        failed.LeaseOwner = null;
        failed.LeaseUntil = null;
        failed.NotBefore = null;
        failed.CompletedAt = null;
        foreach (var item in evolution.WorkItems.Where(w => w.Cycle == evolution.Cycle && w.Status is "working" or "failed")) item.Status = "planned";
        evolution.Error = null;
        evolution.CompletedAt = null;
        evolution.Status = EvolutionStatus.Running;
        if (failed.Type == EvolutionEventTypes.ChangeMerged && evolution.PublishChanges)
        {
            evolution.ApprovedCycle = null;
            evolution.ApprovalCheckpoint = "publish";
            evolution.Status = EvolutionStatus.AwaitingApproval;
        }
        evolution.Events.Add(CompletedEvent("control.retry", "Human acknowledged possible partial side effects and requested checkpoint retry.", evolution.Cycle));
    }, token);

    public Task<Evolution?> StopAsync(Guid id, CancellationToken token) => store.MutateAsync(id, evolution =>
    {
        if (evolution.Status is EvolutionStatus.Completed or EvolutionStatus.Stopped or EvolutionStatus.StopRequested) return;
        var active = evolution.Events.Any(e => e.Status == EvolutionEventStatus.Processing);
        evolution.Status = active ? EvolutionStatus.StopRequested : EvolutionStatus.Stopped;
        evolution.CompletedAt = active ? null : DateTimeOffset.UtcNow;
        foreach (var entry in evolution.Events.Where(e => e.Status == EvolutionEventStatus.Pending))
        {
            entry.Status = EvolutionEventStatus.Cancelled;
            entry.CompletedAt = DateTimeOffset.UtcNow;
        }
        evolution.Events.Add(CompletedEvent(active ? "control.stop" : EvolutionEventTypes.EvolutionStopped, cycle: evolution.Cycle));
    }, token);

    internal static void Enqueue(Evolution evolution, string type) => evolution.Events.Add(new EvolutionEvent { Type = type, Cycle = evolution.Cycle });
    internal static EvolutionEvent CompletedEvent(string type, string? detail = null, int cycle = 0) => new()
    {
        Type = type, Cycle = cycle, Status = EvolutionEventStatus.Completed, Detail = detail,
        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow
    };
}