using CodeEvolver.Api;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeEvolver.Api.Tests;

public sealed class DurableWorkflowTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("ce-tests-");
    private JsonEvolutionStore Store => new(new TestEnvironment(directory.FullName));
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task ThirtyCyclesRetainAllCompletedWorkAndEvents()
    {
        var store = Store;
        var evolution = await CreateStarted(store, cycles: 30);
        await Drain(store);
        var saved = (await store.GetAsync(evolution.Id, Token))!;
        Assert.Equal(EvolutionStatus.Completed, saved.Status);
        Assert.Equal(30, saved.Cycle);
        Assert.Equal(30, saved.WorkItems.Count);
        Assert.Equal(30, saved.Events.Count(e => e.Type == EvolutionEventTypes.GateStarted));
        Assert.All(saved.Events, e => Assert.Equal(EvolutionEventStatus.Completed, e.Status));
    }

    [Fact]
    public async Task FiftyCyclesStayWithinBudget()
    {
        var store = Store;
        var evolution = await CreateStarted(store, cycles: 50);
        await Drain(store);
        var saved = (await store.GetAsync(evolution.Id, Token))!;
        Assert.Equal(50, saved.Cycle);
        Assert.Equal(50, saved.WorkItems.Count);
        Assert.Null(await store.ClaimNextEventAsync(Token));
    }

    [Fact]
    public async Task PauseDuringWorkPersistsNextCheckpointAndResumeKeepsHistory()
    {
        var store = Store;
        var evolution = await CreateStarted(store);
        var claim = (await store.ClaimNextEventAsync(Token))!.Value;
        await new EvolutionCoordinator(Store).PauseAsync(evolution.Id, Token);
        await Monitor(store).ProcessAsync(claim.Evolution, claim.Event, Token);
        var paused = (await Store.GetAsync(evolution.Id, Token))!;
        Assert.Equal(EvolutionStatus.Paused, paused.Status);
        Assert.Contains(paused.Events, e => e.Type == EvolutionEventTypes.PlanStarted && e.Status == EvolutionEventStatus.Pending);
        Assert.Null(await Store.ClaimNextEventAsync(Token));
        await new EvolutionCoordinator(Store).ResumeAsync(evolution.Id, Token);
        await Drain(Store);
        Assert.Equal(EvolutionStatus.Completed, (await Store.GetAsync(evolution.Id, Token))!.Status);
    }

    [Fact]
    public async Task ServicePauseSurvivesNewStoreAndDoesNotOverwriteProgress()
    {
        var evolution = await CreateStarted(Store);
        await Store.ServiceAsync(s => s.DispatchPaused = true, Token);
        Assert.Null(await Store.ClaimNextEventAsync(Token));
        Assert.True((await Store.ServiceAsync(null, Token)).DispatchPaused);
        await Store.ServiceAsync(s => s.DispatchPaused = false, Token);
        Assert.Equal(evolution.Id, (await Store.ClaimNextEventAsync(Token))!.Value.Evolution.Id);
    }

    [Fact]
    public async Task AtomicClaimsDoNotDoubleDispatchAndRepositoryStaysReserved()
    {
        await CreateStarted(Store);
        await CreateStarted(Store);
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Store.ClaimNextEventAsync(Token)));
        Assert.Single(claims, c => c.HasValue);
        var claim = claims.Single(c => c.HasValue)!.Value;
        await new EvolutionCoordinator(Store).PauseAsync(claim.Evolution.Id, Token);
        await Monitor(Store).ProcessAsync(claim.Evolution, claim.Event, Token);
        Assert.Null(await Store.ClaimNextEventAsync(Token));
        await new EvolutionCoordinator(Store).StopAsync(claim.Evolution.Id, Token);
        Assert.NotNull(await Store.ClaimNextEventAsync(Token));
    }

    [Fact]
    public async Task StaleWorkerCannotAppendAfterLeaseReplacement()
    {
        var evolution = await CreateStarted(Store);
        var old = (await Store.ClaimNextEventAsync(Token))!.Value;
        await Store.MutateAsync(evolution.Id, e => e.Events.Single(x => x.Id == old.Event.Id).LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1), Token);
        Assert.Null(await Store.ClaimNextEventAsync(Token));
        await Store.MutateAsync(evolution.Id, e => e.Events.Single(x => x.Id == old.Event.Id).NotBefore = DateTimeOffset.UtcNow.AddSeconds(-1), Token);
        var current = (await Store.ClaimNextEventAsync(Token))!.Value;
        Assert.NotEqual(old.Event.LeaseOwner, current.Event.LeaseOwner);
        await Assert.ThrowsAsync<LeaseLostException>(() => Store.UpdateClaimAsync(evolution.Id, old.Event.Id, old.Event.LeaseOwner!, (_, e) => e.Logs.Add("stale"), Token));
        Assert.DoesNotContain("stale", (await Store.GetAsync(evolution.Id, Token))!.Events.Single(e => e.Id == old.Event.Id).Logs);
    }

    [Fact]
    public async Task ExpiredWriteLeaseRequiresHumanRetryAndRetainsCompletedScan()
    {
        var evolution = await CreateStarted(Store);
        for (var i = 0; i < 2; i++) { var c = (await Store.ClaimNextEventAsync(Token))!.Value; await Monitor(Store).ProcessAsync(c.Evolution, c.Event, Token); }
        var write = (await Store.ClaimNextEventAsync(Token))!.Value;
        Assert.Equal(EvolutionEventTypes.WorkItemStarted, write.Event.Type);
        await Store.MutateAsync(evolution.Id, e => e.Events.Single(x => x.Id == write.Event.Id).LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1), Token);
        Assert.Null(await Store.ClaimNextEventAsync(Token));
        Assert.Equal(EvolutionStatus.Failed, (await Store.GetAsync(evolution.Id, Token))!.Status);
        await new EvolutionCoordinator(Store).RetryAsync(evolution.Id, Token);
        await Drain(Store);
        var saved = (await Store.GetAsync(evolution.Id, Token))!;
        Assert.Single(saved.Events, e => e.Type == EvolutionEventTypes.ScanStarted);
        Assert.Equal(EvolutionStatus.Completed, saved.Status);
    }

    [Fact]
    public async Task PublicationNeedsFreshApprovalEveryCycle()
    {
        var evolution = await CreateStarted(Store, 2, publish: true);
        await Drain(Store);
        Assert.Equal(EvolutionStatus.AwaitingApproval, (await Store.GetAsync(evolution.Id, Token))!.Status);
        await new EvolutionCoordinator(Store).ApproveAsync(evolution.Id, Token);
        await Drain(Store);
        var second = (await Store.GetAsync(evolution.Id, Token))!;
        Assert.Equal(2, second.Cycle);
        Assert.Equal(EvolutionStatus.AwaitingApproval, second.Status);
        Assert.Null(second.ApprovedCycle);
        await new EvolutionCoordinator(Store).ApproveAsync(evolution.Id, Token);
        await Drain(Store);
        Assert.Equal(EvolutionStatus.Completed, (await Store.GetAsync(evolution.Id, Token))!.Status);
    }

    [Fact]
    public async Task ConcurrentProgressAndStopNeverLoseHumanCommand()
    {
        var evolution = await CreateStarted(Store);
        var claim = (await Store.ClaimNextEventAsync(Token))!.Value;
        await Task.WhenAll((Task)new EvolutionCoordinator(Store).StopAsync(evolution.Id, Token),
            Store.UpdateClaimAsync(evolution.Id, claim.Event.Id, claim.Event.LeaseOwner!, (_, e) => e.Logs.Add("progress"), Token));
        await Monitor(Store).ProcessAsync(claim.Evolution, claim.Event, Token);
        var saved = (await Store.GetAsync(evolution.Id, Token))!;
        Assert.Equal(EvolutionStatus.Stopped, saved.Status);
        Assert.Contains("progress", saved.Events.Single(e => e.Id == claim.Event.Id).Logs);
        Assert.DoesNotContain(saved.Events, e => e.Status == EvolutionEventStatus.Pending);
    }

    [Fact]
    public async Task StaleAggregateSaveIsRejected()
    {
        var evolution = await CreateStarted(Store);
        var stale = (await Store.GetAsync(evolution.Id, Token))!;
        await new EvolutionCoordinator(Store).PauseAsync(evolution.Id, Token);
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.SaveAsync(stale, Token));
    }

    [Theory]
    [InlineData("{\"passed\":true,\"commands\":[\"dotnet test: passed\"]}", true)]
    [InlineData("{\"passed\":false,\"commands\":[\"test failed\"]}", false)]
    [InlineData("{\"passed\":true,\"commands\":[]}", false)]
    [InlineData("All tests look fine", false)]
    public void GateRequiresStructuredEvidence(string detail, bool expected) => Assert.Equal(expected, CopilotAgentRunner.HasPassingGate(detail));

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void InvalidCycleBudgetRejected(int cycles) => Assert.Contains(nameof(CreateEvolutionRequest.MaxCycles),
        new CreateEvolutionRequest(RepositoryDiscovery.FindRoot(AppContext.BaseDirectory)!, "test", ".", "main", cycles).Validate().Keys);

    internal static EventMonitor Monitor(IEvolutionStore store) => new(store, new LocalAgentRunner(), NullLogger<EventMonitor>.Instance);
    internal static async Task Drain(IEvolutionStore store)
    {
        var count = 0;
        while (await store.ClaimNextEventAsync(Token) is { } claim)
        {
            Assert.True(++count <= 400, "Unexpected unbounded workflow.");
            await Monitor(store).ProcessAsync(claim.Evolution, claim.Event, Token);
        }
    }
    private Task<Evolution> CreateStarted(IEvolutionStore store, int cycles = 1, bool publish = false) => CreateStartedAsync(store, directory.FullName, cycles, publish);
    internal static async Task<Evolution> CreateStartedAsync(IEvolutionStore store, string repository, int cycles = 1, bool publish = false)
    {
        var coordinator = new EvolutionCoordinator(store);
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(repository, "Improve tests", ".", "main", cycles, 0, publish), Token);
        return (await coordinator.StartAsync(evolution.Id, Token))!;
    }
    public void Dispose() => directory.Delete(true);
    internal sealed class TestEnvironment(string path) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = path;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}