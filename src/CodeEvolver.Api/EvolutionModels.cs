namespace CodeEvolver.Api;

public enum EvolutionStatus { Draft, Running, StopRequested, Stopped, Completed, Failed, PauseRequested, Paused, AwaitingApproval }
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
    public long Version { get; set; }
    public string Kind { get; init; } = "evolution";
    public string? FileName { get; init; }
    public string? DataProfile { get; init; }
    public List<string> KeyPoints { get; set; } = [];
    public int Cycle { get; set; }
    public int MaxCycles { get; set; } = 1;
    public int CycleDelaySeconds { get; set; } = 30;
    public bool PublishChanges { get; set; }
    public int? ApprovedCycle { get; set; }
    public string? ApprovalCheckpoint { get; set; }
    public string? ResumeCheckpoint { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public required string RepositoryPath { get; set; }
    public required string Direction { get; set; }
    public required string Scope { get; set; }
    public required string TargetBranch { get; set; }
    public EvolutionStatus Status { get; set; } = EvolutionStatus.Draft;
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<WorkItem> WorkItems { get; init; } = [];
    public List<EvolutionEvent> Events { get; init; } = [];
}

public sealed class WorkItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Cycle { get; set; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string Status { get; set; } = "planned";
    public string? Result { get; set; }
}

public sealed class EvolutionEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Cycle { get; set; }
    public string? AgentRole { get; set; }
    public int Attempts { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public required string Type { get; init; }
    public EvolutionEventStatus Status { get; set; } = EvolutionEventStatus.Pending;
    public string? Detail { get; set; }
    public string? Prompt { get; set; }
    public List<string> Logs { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Saved evolution policy; each cycle includes scan, plan, implementation, review and gate.</summary>
public sealed record CreateEvolutionRequest(string RepositoryPath, string Direction, string Scope, string TargetBranch,
    int MaxCycles = 1, int CycleDelaySeconds = 30, bool PublishChanges = false)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(RepositoryPath), RepositoryPath);
        AddRequired(errors, nameof(Direction), Direction);
        AddRequired(errors, nameof(Scope), Scope);
        AddRequired(errors, nameof(TargetBranch), TargetBranch);
        if (MaxCycles is < 1 or > 50) errors[nameof(MaxCycles)] = ["MaxCycles must be between 1 and 50."];
        if (CycleDelaySeconds is < 0 or > 86400) errors[nameof(CycleDelaySeconds)] = ["CycleDelaySeconds must be between 0 and 86400."];
        if (Direction?.Length > 16000) errors[nameof(Direction)] = ["Direction must be at most 16000 characters."];
        if (!string.IsNullOrWhiteSpace(TargetBranch) &&
            (TargetBranch.StartsWith('-') || TargetBranch.Contains("..") || TargetBranch.Any(char.IsWhiteSpace) ||
             TargetBranch.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) >= 0))
            errors[nameof(TargetBranch)] = ["TargetBranch must be a valid Git branch name."];
        if (!errors.ContainsKey(nameof(RepositoryPath)))
        {
            try
            {
            var repositoryPath = Path.GetFullPath(RepositoryPath.Trim());
            if (!Directory.Exists(repositoryPath))
                errors[nameof(RepositoryPath)] = ["RepositoryPath must be an existing directory."];
            else if (!Directory.Exists(Path.Combine(repositoryPath, ".git")) && !File.Exists(Path.Combine(repositoryPath, ".git")))
                errors[nameof(RepositoryPath)] = ["RepositoryPath must be the root of a Git repository."];
            if (!string.IsNullOrWhiteSpace(Scope))
            {
                var scopedPath = Path.GetFullPath(Path.Combine(repositoryPath, Scope));
                var relative = Path.GetRelativePath(repositoryPath, scopedPath);
                if (Path.IsPathRooted(Scope) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                    errors[nameof(Scope)] = ["Scope must stay inside the repository."];
            }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors[nameof(RepositoryPath)] = ["RepositoryPath or Scope is not a valid path."];
            }
        }
        return errors;
    }

    private static void AddRequired(Dictionary<string, string[]> errors, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) errors[name] = [$"{name} is required."];
    }
}