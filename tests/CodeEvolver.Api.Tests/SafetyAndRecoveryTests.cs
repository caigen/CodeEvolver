using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeEvolver.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeEvolver.Api.Tests;

public sealed class SafetyAndRecoveryTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("ce-safety-");
    private JsonEvolutionStore Store => new(new DurableWorkflowTests.TestEnvironment(directory.FullName));

    [Fact]
    public void ChildEnvironmentDoesNotInheritApplicationSecrets()
    {
        var start = new ProcessStartInfo();
        start.Environment["ConnectionStrings__CodeEvolver"] = "test-only-database-secret";
        start.Environment["Security__ApiKey"] = "test-only-key";
        start.Environment["UNRELATED_SECRET"] = "test-only-other-secret";
        start.Environment["PATH"] = "test-path";
        start.Environment["CUSTOM_BUILD_SETTING"] = "safe-build-setting";
        CopilotAgentRunner.SanitizeEnvironment(start, "CUSTOM_BUILD_SETTING,ConnectionStrings__CodeEvolver");
        Assert.False(start.Environment.ContainsKey("ConnectionStrings__CodeEvolver"));
        Assert.False(start.Environment.ContainsKey("Security__ApiKey"));
        Assert.False(start.Environment.ContainsKey("UNRELATED_SECRET"));
        Assert.Equal("test-path", start.Environment["PATH"]);
        Assert.Equal("safe-build-setting", start.Environment["CUSTOM_BUILD_SETTING"]);
    }

    [Fact]
    public void ReadOnlyRolesDenyWritingAndShellTools()
    {
        var runner = new CopilotAgentRunner(new ConfigurationBuilder().Build(), NullLogger<CopilotAgentRunner>.Instance);
        var arguments = runner.BuildArguments("scan", false, true);
        Assert.Contains("--deny-tool=shell", arguments);
        Assert.Contains("--deny-tool=write", arguments);
        Assert.Contains("--deny-tool=edit", arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
    }

    [Fact]
    public async Task SafetyViolationIsNeverAutomaticallyRetried()
    {
        var evolution = await DurableWorkflowTests.CreateStartedAsync(Store, directory.FullName);
        var claim = (await Store.ClaimNextEventAsync(default))!.Value;
        var runner = new StubRunner((_, _) => throw new AgentSafetyException("Protected files changed"));
        await new EventMonitor(Store, runner, NullLogger<EventMonitor>.Instance).ProcessAsync(claim.Evolution, claim.Event, default);
        Assert.Equal(EvolutionStatus.Failed, (await Store.GetAsync(evolution.Id, default))!.Status);
        Assert.Null(await Store.ClaimNextEventAsync(default));
    }

    [Fact]
    public async Task FailedGateNeverSchedulesFinalization()
    {
        var evolution = new Evolution { RepositoryPath = directory.FullName, Direction = "test", Scope = ".", TargetBranch = "main", Cycle = 1, Status = EvolutionStatus.Running };
        evolution.Events.Add(new EvolutionEvent { Type = EvolutionEventTypes.GateStarted, Cycle = 1 });
        await Store.SaveAsync(evolution, default);
        var claim = (await Store.ClaimNextEventAsync(default))!.Value;
        var runner = new StubRunner((_, _) => Task.FromResult(new AgentResult("Tests failed", [], [], GatePassed: false)));
        await new EventMonitor(Store, runner, NullLogger<EventMonitor>.Instance).ProcessAsync(claim.Evolution, claim.Event, default);
        var saved = (await Store.GetAsync(evolution.Id, default))!;
        Assert.Equal(EvolutionStatus.Failed, saved.Status);
        Assert.DoesNotContain(saved.Events, e => e.Type == EvolutionEventTypes.ChangeMerged);
    }

    [Fact]
    public async Task ThirdReadOnlyFailureRequiresManualRetry()
    {
        var evolution = await DurableWorkflowTests.CreateStartedAsync(Store, directory.FullName);
        var runner = new StubRunner((_, _) => throw new TimeoutException("Test timeout"));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var claim = (await Store.ClaimNextEventAsync(default))!.Value;
            await new EventMonitor(Store, runner, NullLogger<EventMonitor>.Instance).ProcessAsync(claim.Evolution, claim.Event, default);
            await Store.MutateAsync(evolution.Id, e => e.Events.Single(x => x.Id == claim.Event.Id).NotBefore = null, default);
        }
        var saved = (await Store.GetAsync(evolution.Id, default))!;
        Assert.Equal(EvolutionStatus.Failed, saved.Status);
        Assert.Equal(3, saved.Events.Single(e => e.Type == EvolutionEventTypes.ScanStarted).Attempts);
    }

    [Fact]
    public async Task ShutdownDuringWritePreservesAmbiguousCheckpoint()
    {
        var evolution = new Evolution { RepositoryPath = directory.FullName, Direction = "test", Scope = ".", TargetBranch = "main", Cycle = 1, Status = EvolutionStatus.Running };
        evolution.WorkItems.Add(new WorkItem { Title = "change", Description = "test", Cycle = 1 });
        evolution.Events.Add(new EvolutionEvent { Type = EvolutionEventTypes.WorkItemStarted, Cycle = 1 });
        await Store.SaveAsync(evolution, default);
        var claim = (await Store.ClaimNextEventAsync(default))!.Value;
        using var cancellation = new CancellationTokenSource();
        var runner = new StubRunner((_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
        await new EventMonitor(Store, runner, NullLogger<EventMonitor>.Instance).ProcessAsync(claim.Evolution, claim.Event, cancellation.Token);
        Assert.Equal(EvolutionStatus.Failed, (await Store.GetAsync(evolution.Id, default))!.Status);
        Assert.Null(await Store.ClaimNextEventAsync(default));
    }

    [Fact]
    public async Task AnalyzerSurvivesNewStoreAndDoesNotPersistRawRows()
    {
        Directory.CreateDirectory(Path.Combine(directory.FullName, ".git"));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("user,amount\nSENSITIVE_ROW_VALUE,12\n"));
        var file = new FormFile(stream, 0, stream.Length, "file", "sample.csv");
        var analysis = await new DataAnalyzer(Store).StartAsync(file, directory.FullName, default);
        var saved = (await Store.GetAsync(analysis.Id, default))!;
        Assert.DoesNotContain("SENSITIVE_ROW_VALUE", JsonSerializer.Serialize(saved));
        await DurableWorkflowTests.Drain(Store);
        var restored = await new DataAnalyzer(Store).GetAsync(analysis.Id, default);
        Assert.Equal(DataAnalysisStatus.Completed, restored!.Status);
        Assert.Equal(4, restored.Events.Count);
        Assert.NotEmpty(restored.KeyPoints);
    }

    [Fact]
    public async Task ImportIsIdempotentAndNeverStartsInterruptedWork()
    {
        var evolution = new Evolution { RepositoryPath = directory.FullName, Direction = "legacy", Scope = ".", TargetBranch = "main", Status = EvolutionStatus.Running, PublishChanges = true };
        evolution.Events.Add(new EvolutionEvent { Type = EvolutionEventTypes.WorkItemStarted, Status = EvolutionEventStatus.Processing });
        var path = Path.Combine(directory.FullName, "legacy.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { evolution }, TransactionalEvolutionStore.JsonOptions));
        Assert.Equal(1, await LegacyImport.ImportAsync(path, Store, default));
        Assert.Equal(0, await LegacyImport.ImportAsync(path, Store, default));
        var saved = (await Store.GetAsync(evolution.Id, default))!;
        Assert.False(saved.PublishChanges);
        Assert.Equal(EvolutionStatus.Failed, saved.Status);
        Assert.Equal(1, saved.Cycle);
        Assert.Null(await Store.ClaimNextEventAsync(default));
    }

    private sealed class StubRunner(Func<Evolution, CancellationToken, Task<AgentResult>> run) : IAgentRunner
    {
        public string GetPrompt(Evolution evolution, string eventType) => "Test prompt";
        public Task<AgentResult> RunAsync(Evolution evolution, string eventType, Func<string, CancellationToken, Task>? progress, CancellationToken token) => run(evolution, token);
    }
    public void Dispose() => directory.Delete(true);
}