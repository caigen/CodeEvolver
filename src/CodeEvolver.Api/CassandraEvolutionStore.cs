using Cassandra;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeEvolver.Api;

public sealed class CassandraEvolutionStore : IEvolutionStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ICluster cluster;
    private readonly Task<Cassandra.ISession> sessionTask;

    public CassandraEvolutionStore(IConfiguration configuration)
    {
        var contactPoints = configuration.GetSection("Storage:Cassandra:ContactPoints").Get<string[]>() ?? ["127.0.0.1"];
        var port = configuration.GetValue("Storage:Cassandra:Port", 9042);
        var keyspace = configuration["Storage:Cassandra:Keyspace"] ?? "code_evolver";
        cluster = Cluster.Builder().AddContactPoints(contactPoints).WithPort(port).Build();
        sessionTask = InitializeAsync(keyspace);
    }

    public async Task<IReadOnlyList<Evolution>> ListAsync(CancellationToken cancellationToken)
    {
        var session = await sessionTask.WaitAsync(cancellationToken);
        var rows = await session.ExecuteAsync(new SimpleStatement("SELECT payload FROM evolutions"));
        return rows.Select(row => Deserialize(row.GetValue<string>("payload")))
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public async Task<Evolution?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await sessionTask.WaitAsync(cancellationToken);
        var statement = await session.PrepareAsync("SELECT payload FROM evolutions WHERE id = ?");
        var row = (await session.ExecuteAsync(statement.Bind(id))).FirstOrDefault();
        return row is null ? null : Deserialize(row.GetValue<string>("payload"));
    }

    public async Task<Evolution> SaveAsync(Evolution evolution, CancellationToken cancellationToken)
    {
        var session = await sessionTask.WaitAsync(cancellationToken);
        evolution.UpdatedAt = DateTimeOffset.UtcNow;
        var statement = await session.PrepareAsync("INSERT INTO evolutions (id, created_at, payload) VALUES (?, ?, ?)");
        await session.ExecuteAsync(statement.Bind(evolution.Id, evolution.CreatedAt, JsonSerializer.Serialize(evolution, JsonOptions)));
        return evolution;
    }

    public async Task<(Evolution Evolution, EvolutionEvent Event)?> ClaimNextEventAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var evolution = (await ListAsync(cancellationToken)).FirstOrDefault(item =>
                item.Status == EvolutionStatus.Running && item.Events.Any(entry => entry.Status == EvolutionEventStatus.Pending));
            var evolutionEvent = evolution?.Events.FirstOrDefault(entry => entry.Status == EvolutionEventStatus.Pending);
            if (evolution is null || evolutionEvent is null) return null;
            evolutionEvent.Status = EvolutionEventStatus.Processing;
            await SaveAsync(evolution, cancellationToken);
            return (evolution, evolutionEvent);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (sessionTask.IsCompletedSuccessfully) await sessionTask.Result.ShutdownAsync();
        await cluster.ShutdownAsync();
        gate.Dispose();
    }

    private async Task<Cassandra.ISession> InitializeAsync(string keyspace)
    {
        var bootstrapSession = await cluster.ConnectAsync();
        await bootstrapSession.ExecuteAsync(new SimpleStatement(
            $"CREATE KEYSPACE IF NOT EXISTS {keyspace} WITH replication = {{'class':'SimpleStrategy','replication_factor':1}}"));
        await bootstrapSession.ShutdownAsync();
        var session = await cluster.ConnectAsync(keyspace);
        await session.ExecuteAsync(new SimpleStatement(
            "CREATE TABLE IF NOT EXISTS evolutions (id uuid PRIMARY KEY, created_at timestamp, payload text)"));
        return session;
    }

    private static Evolution Deserialize(string payload) =>
        JsonSerializer.Deserialize<Evolution>(payload, JsonOptions)
        ?? throw new InvalidDataException("Cassandra contained an invalid evolution payload.");
}