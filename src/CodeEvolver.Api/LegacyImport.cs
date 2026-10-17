using System.Text.Json;

namespace CodeEvolver.Api;

public static class LegacyImport
{
    public static async Task<int> ImportAsync(string path, IEvolutionStore store, CancellationToken token)
    {
        var json = await File.ReadAllTextAsync(path, token);
        var evolutions = json.TrimStart().StartsWith('[')
            ? JsonSerializer.Deserialize<List<Evolution>>(json, TransactionalEvolutionStore.JsonOptions) ?? []
            : JsonSerializer.Deserialize<StoreState>(json, TransactionalEvolutionStore.JsonOptions)?.Evolutions ?? [];
        var count = 0;
        foreach (var evolution in evolutions)
        {
            if (await store.GetAsync(evolution.Id, token) is not null) continue;
            evolution.Version = 0;
            evolution.Cycle = Math.Max(1, evolution.Cycle);
            evolution.MaxCycles = Math.Clamp(Math.Max(evolution.Cycle, evolution.MaxCycles), 1, 50);
            evolution.PublishChanges = false;
            evolution.ApprovedCycle = null;
            evolution.ApprovalCheckpoint = null;
            if (evolution.Status is EvolutionStatus.Running or EvolutionStatus.StopRequested or EvolutionStatus.PauseRequested or EvolutionStatus.AwaitingApproval)
                evolution.Status = EvolutionStatus.Paused;
            foreach (var entry in evolution.Events)
            {
                entry.Cycle = Math.Max(1, entry.Cycle);
                entry.LeaseOwner = null;
                entry.LeaseUntil = null;
                if (entry.Status == EvolutionEventStatus.Processing)
                {
                    entry.Status = EvolutionEventStatus.Failed;
                    evolution.Status = EvolutionStatus.Failed;
                    evolution.Error = "Imported interrupted phase; inspect the repository before retrying.";
                }
            }
            foreach (var item in evolution.WorkItems) item.Cycle = Math.Max(1, item.Cycle);
            evolution.Events.Add(EvolutionCoordinator.CompletedEvent("migration.imported", "Imported with publishing disabled; no job was automatically started.", evolution.Cycle));
            await store.SaveAsync(evolution, token);
            count++;
        }
        return count;
    }
}