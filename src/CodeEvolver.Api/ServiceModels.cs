namespace CodeEvolver.Api;

public sealed class ServiceState
{
    public bool DispatchPaused { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<WorkerHeartbeat> Workers { get; set; } = [];
    public Dictionary<string, Guid> RepositoryOwners { get; set; } = [];
    public List<ServiceCommand> Commands { get; set; } = [];
}

/// <summary>Persistent audit record of a scheduler command.</summary>
public sealed record ServiceCommand(string Action, DateTimeOffset At);
/// <summary>Last heartbeat from an independent worker process.</summary>
public sealed record WorkerHeartbeat(string Id, DateTimeOffset LastSeenAt, Guid? EvolutionId);
/// <summary>Controls dispatch; a pause drains rather than kills active agents.</summary>
public sealed record ServiceControlRequest(bool Paused);
/// <summary>Service control state and effective publication policy.</summary>
public sealed record ServiceResponse(bool DispatchPaused, bool WorkerOnline, IReadOnlyList<WorkerHeartbeat> Workers,
    bool PublishingEnabled, DateTimeOffset UpdatedAt);

public sealed class StoreConflictException(string message) : Exception(message);
public sealed class LeaseLostException() : Exception("The phase lease expired or belongs to another worker.");
public sealed class AgentSafetyException(string message) : Exception(message);

public sealed class StoreState
{
    public List<Evolution> Evolutions { get; set; } = [];
    public ServiceState Service { get; set; } = new();
}