using CodeEvolver.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeEvolver.Api.Tests;

public sealed class EvolutionLifecycleTests
{
    [Fact]
    public async Task EventMonitor_ProcessesLifecycleToCompletion()
    {
        var store = new MemoryEvolutionStore();
        var coordinator = new EvolutionCoordinator(store);
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(".", "Improve tests", "tests", "main"), CancellationToken.None);
        await coordinator.StartAsync(evolution.Id, CancellationToken.None);
        var monitor = new EventMonitor(store, new LocalAgentRunner(), NullLogger<EventMonitor>.Instance);
        while (await store.ClaimNextEventAsync(CancellationToken.None) is { } claimed)
            await monitor.ProcessAsync(claimed.Evolution, claimed.Event, CancellationToken.None);

        var completed = await store.GetAsync(evolution.Id, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(EvolutionStatus.Completed, completed.Status);
        Assert.Single(completed.WorkItems);
        Assert.Contains(completed.Events, entry => entry.Type == EvolutionEventTypes.ChangeMerged);
        Assert.All(completed.Events, entry => Assert.Equal(EvolutionEventStatus.Completed, entry.Status));
    }

    [Fact]
    public async Task Stop_CancelsPendingWork()
    {
        var store = new MemoryEvolutionStore();
        var coordinator = new EvolutionCoordinator(store);
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(".", "Improve tests", "tests", "main"), CancellationToken.None);
        await coordinator.StartAsync(evolution.Id, CancellationToken.None);
        var stopped = await coordinator.StopAsync(evolution.Id, CancellationToken.None);
        Assert.NotNull(stopped);
        Assert.Equal(EvolutionStatus.Stopped, stopped.Status);
        Assert.Contains(stopped.Events, entry => entry.Status == EvolutionEventStatus.Cancelled);
        Assert.Contains(stopped.Events, entry => entry.Type == EvolutionEventTypes.EvolutionStopped);
    }

    private sealed class MemoryEvolutionStore : IEvolutionStore
    {
        private readonly List<Evolution> evolutions = [];
        public Task<IReadOnlyList<Evolution>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Evolution>>(evolutions);
        public Task<Evolution?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(evolutions.SingleOrDefault(item => item.Id == id));
        public Task<Evolution> SaveAsync(Evolution evolution, CancellationToken cancellationToken)
        {
            if (!evolutions.Contains(evolution)) evolutions.Add(evolution);
            return Task.FromResult(evolution);
        }
        public Task<(Evolution Evolution, EvolutionEvent Event)?> ClaimNextEventAsync(CancellationToken cancellationToken)
        {
            var evolution = evolutions.FirstOrDefault(item => item.Status == EvolutionStatus.Running);
            var evolutionEvent = evolution?.Events.FirstOrDefault(entry => entry.Status == EvolutionEventStatus.Pending);
            if (evolution is null || evolutionEvent is null) return Task.FromResult<(Evolution, EvolutionEvent)?>(null);
            evolutionEvent.Status = EvolutionEventStatus.Processing;
            return Task.FromResult<(Evolution, EvolutionEvent)?>((evolution, evolutionEvent));
        }
    }
}
