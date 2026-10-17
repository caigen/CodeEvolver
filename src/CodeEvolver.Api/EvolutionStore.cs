using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeEvolver.Api;

public interface IEvolutionStore
{
    Task<IReadOnlyList<Evolution>> ListAsync(CancellationToken cancellationToken);
    Task<Evolution?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Evolution> SaveAsync(Evolution evolution, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<(Evolution Evolution, EvolutionEvent Event)?> ClaimNextEventAsync(CancellationToken cancellationToken);
    Task<Evolution?> MutateAsync(Guid id, Action<Evolution> mutation, CancellationToken cancellationToken);
    Task<Evolution> UpdateClaimAsync(Guid id, Guid eventId, string owner, Action<Evolution, EvolutionEvent> mutation, CancellationToken cancellationToken);
    Task<ServiceState> ServiceAsync(Action<ServiceState>? mutation, CancellationToken cancellationToken);
}

    public abstract class TransactionalEvolutionStore : IEvolutionStore
    {
        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        public const int LeaseSeconds = 60;
        protected abstract Task<T> TransactionAsync<T>(Func<StoreState, T> action, bool write, CancellationToken cancellationToken);

        public Task<IReadOnlyList<Evolution>> ListAsync(CancellationToken cancellationToken) =>
            TransactionAsync<IReadOnlyList<Evolution>>(state => state.Evolutions.OrderByDescending(e => e.CreatedAt).ToArray(), false, cancellationToken);

        public Task<Evolution?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            TransactionAsync(state => state.Evolutions.SingleOrDefault(e => e.Id == id), false, cancellationToken);

        public Task<Evolution> SaveAsync(Evolution evolution, CancellationToken cancellationToken) => TransactionAsync(state =>
        {
            var existing = state.Evolutions.FindIndex(e => e.Id == evolution.Id);
            if (existing >= 0 && state.Evolutions[existing].Version != evolution.Version)
                throw new StoreConflictException("Evolution changed; refresh before saving.");
            Touch(evolution);
            if (existing < 0) state.Evolutions.Add(evolution); else state.Evolutions[existing] = evolution;
            return evolution;
        }, true, cancellationToken);

        public Task<Evolution?> MutateAsync(Guid id, Action<Evolution> mutation, CancellationToken cancellationToken) => TransactionAsync(state =>
        {
            var evolution = state.Evolutions.SingleOrDefault(e => e.Id == id);
            if (evolution is null) return null;
            mutation(evolution);
            Touch(evolution);
            ReleaseTerminalReservations(state);
            return evolution;
        }, true, cancellationToken);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => TransactionAsync(state =>
        {
            var evolution = state.Evolutions.SingleOrDefault(e => e.Id == id);
            if (evolution is null) return false;
            if (evolution.Status is not (EvolutionStatus.Draft or EvolutionStatus.Stopped or EvolutionStatus.Completed))
                throw new StoreConflictException("Stop the evolution before deleting it.");
            state.Evolutions.Remove(evolution);
            ReleaseTerminalReservations(state);
            return true;
        }, true, cancellationToken);

        public Task<ServiceState> ServiceAsync(Action<ServiceState>? mutation, CancellationToken cancellationToken) => TransactionAsync(state =>
        {
            mutation?.Invoke(state.Service);
            return state.Service;
        }, mutation is not null, cancellationToken);

        public Task<Evolution> UpdateClaimAsync(Guid id, Guid eventId, string owner, Action<Evolution, EvolutionEvent> mutation,
            CancellationToken cancellationToken) => TransactionAsync(state =>
        {
            var evolution = state.Evolutions.SingleOrDefault(e => e.Id == id) ?? throw new LeaseLostException();
            var entry = evolution.Events.SingleOrDefault(e => e.Id == eventId);
            if (entry is null || entry.Status != EvolutionEventStatus.Processing || entry.LeaseOwner != owner || entry.LeaseUntil <= DateTimeOffset.UtcNow)
                throw new LeaseLostException();
            mutation(evolution, entry);
            Touch(evolution);
            ReleaseTerminalReservations(state);
            return evolution;
        }, true, cancellationToken);

        public Task<(Evolution Evolution, EvolutionEvent Event)?> ClaimNextEventAsync(CancellationToken cancellationToken) =>
            TransactionAsync<(Evolution, EvolutionEvent)?>(state =>
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var evolution in state.Evolutions)
                {
                    var expired = evolution.Events.Where(e => e.Status == EvolutionEventStatus.Processing && (e.LeaseUntil is null || e.LeaseUntil <= now)).ToArray();
                    foreach (var entry in expired)
                    {
                        var safeRetry = IsReadOnlyPhase(entry.Type) && entry.Attempts < 3;
                        entry.Status = safeRetry ? EvolutionEventStatus.Pending : EvolutionEventStatus.Failed;
                        entry.Detail = "Interrupted phase: lease expired. Inspect repository changes before retrying a write phase.";
                        entry.Logs.Add($"{now:O} {entry.Detail}");
                        entry.LeaseOwner = null;
                        entry.LeaseUntil = null;
                        if (evolution.Status == EvolutionStatus.StopRequested)
                        {
                            entry.Status = EvolutionEventStatus.Cancelled;
                            evolution.Status = EvolutionStatus.Stopped;
                            evolution.CompletedAt = now;
                        }
                        else if (!safeRetry)
                        {
                            evolution.Status = EvolutionStatus.Failed;
                            evolution.Error = entry.Detail;
                        }
                        else if (evolution.Status == EvolutionStatus.PauseRequested) evolution.Status = EvolutionStatus.Paused;
                        Touch(evolution);
                    }
                }
                ReleaseTerminalReservations(state);
                if (state.Service.DispatchPaused) return null;
                foreach (var evolution in state.Evolutions.Where(e => e.Status == EvolutionStatus.Running && (e.NextRunAt is null || e.NextRunAt <= now)).OrderBy(e => e.UpdatedAt))
                {
                    if (evolution.Events.Any(e => e.Status == EvolutionEventStatus.Processing)) continue;
                    var entry = evolution.Events.FirstOrDefault(e => e.Status == EvolutionEventStatus.Pending);
                    if (entry is null || entry.NotBefore > now) continue;
                    var repository = RepositoryKey(evolution.RepositoryPath);
                    if (state.Service.RepositoryOwners.TryGetValue(repository, out var reserved) && reserved != evolution.Id) continue;
                    state.Service.RepositoryOwners[repository] = evolution.Id;
                    entry.Status = EvolutionEventStatus.Processing;
                    entry.StartedAt = now;
                    entry.CompletedAt = null;
                    entry.Attempts++;
                    entry.LeaseOwner = Guid.NewGuid().ToString("N");
                    entry.LeaseUntil = now.AddSeconds(LeaseSeconds);
                    entry.AgentRole = entry.Type.Split('.')[0];
                    evolution.NextRunAt = null;
                    Touch(evolution);
                    return (evolution, entry);
                }
                return null;
            }, true, cancellationToken);

        internal static bool IsReadOnlyPhase(string type) => type is EvolutionEventTypes.ScanStarted or EvolutionEventTypes.PlanStarted || type.StartsWith("data-", StringComparison.Ordinal) || type is "evolution-direction.started" or "analysis-summary.started";

        internal static string RepositoryKey(string path)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        }

        private static void Touch(Evolution evolution) { evolution.Version++; evolution.UpdatedAt = DateTimeOffset.UtcNow; }
        private static void ReleaseTerminalReservations(StoreState state)
        {
            foreach (var pair in state.Service.RepositoryOwners.ToArray())
                if (!state.Evolutions.Any(e => e.Id == pair.Value && e.Status is not (EvolutionStatus.Completed or EvolutionStatus.Stopped)))
                    state.Service.RepositoryOwners.Remove(pair.Key);
        }
    }

    /// <summary>Development/test-only store. Production hosts always register MySQL.</summary>
    public sealed class JsonEvolutionStore(IHostEnvironment environment) : TransactionalEvolutionStore
    {
        private readonly string path = Path.Combine(environment.ContentRootPath, "data", "evolutions.json");
        protected override async Task<T> TransactionAsync<T>(Func<StoreState, T> action, bool write, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var fileLock = await LockAsync(cancellationToken);
            var state = new StoreState();
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                state = json.TrimStart().StartsWith('[')
                    ? new StoreState { Evolutions = JsonSerializer.Deserialize<List<Evolution>>(json, JsonOptions) ?? [] }
                    : JsonSerializer.Deserialize<StoreState>(json, JsonOptions) ?? new();
            }
            var result = action(state);
            if (write)
            {
                var temporary = path + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
                File.Move(temporary, path, true);
            }
            return result;
        }

        private async Task<FileStream> LockAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { await Task.Delay(25, token); }
            }
        }
    }

