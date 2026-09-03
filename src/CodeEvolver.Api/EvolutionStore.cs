using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeEvolver.Api;

public interface IEvolutionStore
{
    Task<IReadOnlyList<Evolution>> ListAsync(CancellationToken cancellationToken);
    Task<Evolution?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Evolution> SaveAsync(Evolution evolution, CancellationToken cancellationToken);
    Task<(Evolution Evolution, EvolutionEvent Event)?> ClaimNextEventAsync(CancellationToken cancellationToken);
}

public sealed class JsonEvolutionStore(IWebHostEnvironment environment) : IEvolutionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(environment.ContentRootPath, "data", "evolutions.json");

    public async Task<IReadOnlyList<Evolution>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return (await ReadUnsafeAsync(cancellationToken)).OrderByDescending(item => item.CreatedAt).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<Evolution?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return (await ReadUnsafeAsync(cancellationToken)).SingleOrDefault(item => item.Id == id); }
        finally { gate.Release(); }
    }

    public async Task<Evolution> SaveAsync(Evolution evolution, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var evolutions = await ReadUnsafeAsync(cancellationToken);
            var index = evolutions.FindIndex(item => item.Id == evolution.Id);
            evolution.UpdatedAt = DateTimeOffset.UtcNow;
            if (index < 0) evolutions.Add(evolution); else evolutions[index] = evolution;
            await WriteUnsafeAsync(evolutions, cancellationToken);
            return evolution;
        }
        finally { gate.Release(); }
    }

    public async Task<(Evolution Evolution, EvolutionEvent Event)?> ClaimNextEventAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var evolutions = await ReadUnsafeAsync(cancellationToken);
            var evolution = evolutions.FirstOrDefault(item => item.Status == EvolutionStatus.Running && item.Events.Any(entry => entry.Status == EvolutionEventStatus.Pending));
            var evolutionEvent = evolution?.Events.FirstOrDefault(entry => entry.Status == EvolutionEventStatus.Pending);
            if (evolution is null || evolutionEvent is null) return null;
            evolutionEvent.Status = EvolutionEventStatus.Processing;
            evolution.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteUnsafeAsync(evolutions, cancellationToken);
            return (evolution, evolutionEvent);
        }
        finally { gate.Release(); }
    }

    private async Task<List<Evolution>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<Evolution>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteUnsafeAsync(List<Evolution> evolutions, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, evolutions, JsonOptions, cancellationToken);
        File.Move(temporaryPath, path, true);
    }
}