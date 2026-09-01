namespace CodeEvolver.Api;

public enum EvolutionStatus { Draft, Running, StopRequested, Stopped, Completed, Failed }
public enum EvolutionEventStatus { Pending, Processing, Completed, Failed, Cancelled }

public static class EvolutionEventTypes
{
    public const string EvolutionStarted = "evolution.started";
    public const string EvolutionStopped = "evolution.stopped";
    public const string ScanStarted = "scan.started";
    public const string ScanCompleted = "scan.completed";
    public const string PlanStarted = "plan.started";
    public const string PlanCompleted = "plan.completed";
    public const string WorkItemStarted = "work-item.started";
    public const string WorkItemCompleted = "work-item.completed";
    public const string ReviewStarted = "review.started";
    public const string ReviewCompleted = "review.completed";
    public const string GateStarted = "gate.started";
    public const string GateCompleted = "gate.completed";
    public const string ChangeMerged = "change.merged";
}

public sealed class Evolution
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string RepositoryPath { get; init; }
    public required string Direction { get; init; }
    public required string Scope { get; init; }
    public required string TargetBranch { get; init; }
    public EvolutionStatus Status { get; set; } = EvolutionStatus.Draft;
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<WorkItem> WorkItems { get; init; } = [];
    public List<EvolutionEvent> Events { get; init; } = [];
}

public sealed class WorkItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string Status { get; set; } = "planned";
    public string? Result { get; set; }
}

public sealed class EvolutionEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Type { get; init; }
    public EvolutionEventStatus Status { get; set; } = EvolutionEventStatus.Pending;
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed record CreateEvolutionRequest(string RepositoryPath, string Direction, string Scope, string TargetBranch)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(RepositoryPath), RepositoryPath);
        AddRequired(errors, nameof(Direction), Direction);
        AddRequired(errors, nameof(Scope), Scope);
        AddRequired(errors, nameof(TargetBranch), TargetBranch);
        return errors;
    }

    private static void AddRequired(Dictionary<string, string[]> errors, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) errors[name] = [$"{name} is required."];
    }
}