using CodeEvolver.Api;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeEvolver.Api.Tests;

public sealed class EvolutionLifecycleTests
{
    [Fact]
    public void Validate_AcceptsCurrentRepository()
    {
        var repositoryPath = RepositoryDiscovery.FindRoot(AppContext.BaseDirectory);

        Assert.NotNull(repositoryPath);
        var errors = new CreateEvolutionRequest(repositoryPath, "Improve repository input", ".", "main").Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsDirectoryThatIsNotGitRepository()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var errors = new CreateEvolutionRequest(directory.FullName, "Improve tests", ".", "main").Validate();

            Assert.Equal("RepositoryPath must be the root of a Git repository.", errors[nameof(CreateEvolutionRequest.RepositoryPath)].Single());
        }
        finally
        {
            directory.Delete(true);
        }
    }

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
        Assert.All(completed.Events.Where(entry => entry.Type != EvolutionEventTypes.EvolutionStarted && entry.Type.EndsWith(".started")), entry =>
        {
            Assert.NotNull(entry.Prompt);
            Assert.NotEmpty(entry.Logs);
            Assert.NotNull(entry.StartedAt);
            Assert.NotNull(entry.CompletedAt);
        });
        Assert.NotNull(completed.StartedAt);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public void AgentOutputParser_ReadsFinalResponseAndPlan()
    {
        const string output = """
            {"type":"model.call_start","data":{}}
            {"type":"assistant.message","data":{"content":"```json\n[{\"title\":\"Fix polling\",\"description\":\"Persist live agent progress.\"}]\n```"}}
            """;

        var response = AgentOutputParser.GetFinalResponse(output);
        var workItems = AgentOutputParser.GetWorkItems(response!);

        Assert.Single(workItems);
        Assert.Equal("Fix polling", workItems[0].Title);
        Assert.Equal("Persist live agent progress.", workItems[0].Description);
        Assert.Equal("Agent is reasoning.", AgentOutputParser.GetProgress("{\"type\":\"model.call_start\",\"data\":{}}"));
    }

    [Fact]
    public void AgentOutputParser_IgnoresUnexpectedJsonShape()
    {
        Assert.Null(AgentOutputParser.GetProgress("{}"));
        Assert.Null(AgentOutputParser.GetFinalResponse("""
            {}
            {"type":"assistant.message"}
            {"type":"assistant.message","data":null}
            """));
    }

    [Fact]
    public void ResolveExecutable_FindsWindowsCommandShim()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "copilot"), "not executable on Windows");
            var shim = Path.Combine(directory.FullName, "copilot.cmd");
            File.WriteAllText(shim, "@echo off");

            var resolved = CopilotAgentRunner.ResolveExecutable(
                "copilot",
                directory.FullName,
                isWindows: true,
                pathExtensions: ".EXE;.CMD");

            Assert.Equal(shim, resolved);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void ResolveExecutable_FindsConfiguredWindowsCommandShim()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var tools = Directory.CreateDirectory(Path.Combine(directory.FullName, "tools"));
            var shim = Path.Combine(tools.FullName, "copilot.cmd");
            File.WriteAllText(shim, "@echo off");

            var resolved = CopilotAgentRunner.ResolveExecutable(
                Path.Combine("tools", "copilot"),
                "",
                isWindows: true,
                pathExtensions: ".EXE;.CMD",
                currentDirectory: directory.FullName);

            Assert.Equal(shim, resolved);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void ResolveCommand_BypassesWindowsNpmShim()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var shim = Path.Combine(directory.FullName, "copilot.cmd");
            var node = Path.Combine(directory.FullName, "node.exe");
            var loader = Path.Combine(directory.FullName, "node_modules", "@github", "copilot", "npm-loader.js");
            Directory.CreateDirectory(Path.GetDirectoryName(loader)!);
            File.WriteAllText(shim, "@echo off");
            File.WriteAllText(node, "");
            File.WriteAllText(loader, "");

            var command = CopilotAgentRunner.ResolveCommand(shim, isWindows: true);

            Assert.Equal(node, command.FileName);
            Assert.Equal([loader], command.PrefixArguments);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void MergePrompt_CanDisableRepositoryPublishing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Copilot:PublishChanges"] = "false"
            })
            .Build();
        var runner = new CopilotAgentRunner(configuration, NullLogger<CopilotAgentRunner>.Instance);
        var evolution = new Evolution
        {
            RepositoryPath = ".",
            Direction = "Verify self-hosting",
            Scope = "README.md",
            TargetBranch = "main"
        };

        var prompt = runner.GetPrompt(evolution, EvolutionEventTypes.ChangeMerged);

        Assert.Contains("Do not commit, push, or open a pull request.", prompt);
        var arguments = runner.BuildArguments(prompt);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Contains("--deny-tool=shell(git commit)", arguments);
        Assert.Contains("--deny-tool=shell(git push)", arguments);
        Assert.Contains("--deny-tool=shell(gh pr create)", arguments);
    }

    [Fact]
    public void RedactSecrets_RemovesConfiguredValues()
    {
        var values = new Dictionary<string, string?>
        {
            ["TOKEN"] = "top-secret",
            ["EMPTY"] = ""
        };

        var redacted = CopilotAgentRunner.RedactSecrets(
            "stderr contained top-secret",
            "TOKEN,EMPTY",
            name => values.GetValueOrDefault(name));

        Assert.Equal("stderr contained ***", redacted);
    }

    [Fact]
    public async Task TerminateProcess_StopsRunningProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        await CopilotAgentRunner.TerminateProcessAsync(process);

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task EventMonitor_RunsEachPlannedWorkItemSeparately()
    {
        var store = new MemoryEvolutionStore();
        var coordinator = new EvolutionCoordinator(store);
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(".", "Improve tests", "tests", "main"), CancellationToken.None);
        await coordinator.StartAsync(evolution.Id, CancellationToken.None);
        var runner = new MultipleWorkItemRunner();
        var monitor = new EventMonitor(store, runner, NullLogger<EventMonitor>.Instance);
        while (await store.ClaimNextEventAsync(CancellationToken.None) is { } claimed)
            await monitor.ProcessAsync(claimed.Evolution, claimed.Event, CancellationToken.None);

        var completed = await store.GetAsync(evolution.Id, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(2, runner.WorkerPrompts.Count);
        Assert.Contains("First item", runner.WorkerPrompts[0]);
        Assert.Contains("Second item", runner.WorkerPrompts[1]);
        Assert.All(completed.WorkItems, item => Assert.Equal("completed", item.Status));
        Assert.Equal(2, completed.Events.Count(item => item.Type == EvolutionEventTypes.WorkItemStarted));
    }

    [Fact]
    public async Task EventMonitor_RecoversInterruptedProcessingEvent()
    {
        var store = new MemoryEvolutionStore();
        var coordinator = new EvolutionCoordinator(store);
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(".", "Improve tests", "tests", "main"), CancellationToken.None);
        await coordinator.StartAsync(evolution.Id, CancellationToken.None);
        var claimed = await store.ClaimNextEventAsync(CancellationToken.None);
        Assert.NotNull(claimed);
        var monitor = new EventMonitor(store, new LocalAgentRunner(), NullLogger<EventMonitor>.Instance);

        await monitor.RecoverInterruptedEventsAsync(CancellationToken.None);

        var recovered = await store.GetAsync(evolution.Id, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(EvolutionStatus.Failed, recovered.Status);
        Assert.Contains("interrupted", recovered.Error);
        Assert.Equal(EvolutionEventStatus.Failed, recovered.Events.Single(item => item.Id == claimed.Value.Event.Id).Status);
    }

    [Fact]
    public async Task EventMonitor_PersistsFailureWhenCancellationTokenIsCancelled()
    {
        var store = new MemoryEvolutionStore(honorCancellation: true);
        var coordinator = new EvolutionCoordinator(store);
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(".", "Improve tests", "tests", "main"), CancellationToken.None);
        await coordinator.StartAsync(evolution.Id, CancellationToken.None);
        var claimed = await store.ClaimNextEventAsync(CancellationToken.None);
        Assert.NotNull(claimed);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var monitor = new EventMonitor(store, new LocalAgentRunner(), NullLogger<EventMonitor>.Instance);

        await monitor.ProcessAsync(claimed.Value.Evolution, claimed.Value.Event, cancellation.Token);

        var failed = await store.GetAsync(evolution.Id, CancellationToken.None);
        Assert.NotNull(failed);
        Assert.Equal(EvolutionStatus.Failed, failed.Status);
        Assert.Equal(EvolutionEventStatus.Failed, failed.Events.Single(item => item.Id == claimed.Value.Event.Id).Status);
    }

    [Fact]
    public async Task JsonStore_SerializesConcurrentInstances()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var environment = new TestWebHostEnvironment { ContentRootPath = directory.FullName };
            var stores = new[] { new JsonEvolutionStore(environment), new JsonEvolutionStore(environment) };
            var evolutions = Enumerable.Range(0, 20)
                .Select(index => new Evolution
                {
                    RepositoryPath = ".",
                    Direction = $"Evolution {index}",
                    Scope = ".",
                    TargetBranch = "main"
                })
                .ToArray();

            await Task.WhenAll(evolutions.Select((evolution, index) =>
                stores[index % stores.Length].SaveAsync(evolution, CancellationToken.None)));

            var saved = await stores[0].ListAsync(CancellationToken.None);
            Assert.Equal(evolutions.Select(item => item.Id).Order(), saved.Select(item => item.Id).Order());
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Start_RestartsStoppedEvolutionFromCleanState()
    {
        var store = new MemoryEvolutionStore();
        var coordinator = new EvolutionCoordinator(store);
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(".", "Improve tests", "tests", "main"), CancellationToken.None);

        await coordinator.StartAsync(evolution.Id, CancellationToken.None);
        evolution = await store.GetAsync(evolution.Id, CancellationToken.None)!;
        evolution!.Status = EvolutionStatus.Stopped;
        evolution.Summary = "Old summary";
        evolution.Error = "Old error";
        evolution.WorkItems.Add(new WorkItem { Title = "Stale item", Description = "Should be cleared" });
        evolution.Events.Add(new EvolutionEvent { Type = EvolutionEventTypes.ScanCompleted, Status = EvolutionEventStatus.Completed });
        await store.SaveAsync(evolution, CancellationToken.None);

        var restarted = await coordinator.StartAsync(evolution.Id, CancellationToken.None);

        Assert.NotNull(restarted);
        Assert.Equal(EvolutionStatus.Running, restarted.Status);
        Assert.Empty(restarted.WorkItems);
        Assert.Null(restarted.Summary);
        Assert.Null(restarted.Error);
        Assert.DoesNotContain(restarted.Events, entry => entry.Type == EvolutionEventTypes.ScanCompleted);
        Assert.Contains(restarted.Events, entry => entry.Type == EvolutionEventTypes.ScanStarted);
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

    [Fact]
    public async Task Stop_DuringProcessing_WaitsForActiveEvent()
    {
        var store = new MemoryEvolutionStore();
        var coordinator = new EvolutionCoordinator(store);
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(".", "Improve tests", "tests", "main"), CancellationToken.None);
        await coordinator.StartAsync(evolution.Id, CancellationToken.None);
        await store.ClaimNextEventAsync(CancellationToken.None);

        var stopping = await coordinator.StopAsync(evolution.Id, CancellationToken.None);

        Assert.NotNull(stopping);
        Assert.Equal(EvolutionStatus.StopRequested, stopping.Status);
        Assert.DoesNotContain(stopping.Events, entry => entry.Type == EvolutionEventTypes.EvolutionStopped);
    }

    [Fact]
    public async Task UpdateAndDelete_NonRunningEvolution()
    {
        var store = new MemoryEvolutionStore();
        var coordinator = new EvolutionCoordinator(store);
        var repositoryPath = RepositoryDiscovery.FindRoot(AppContext.BaseDirectory)!;
        var evolution = await coordinator.CreateAsync(new CreateEvolutionRequest(repositoryPath, "Original", ".", "main"), CancellationToken.None);

        var updated = await coordinator.UpdateAsync(evolution.Id, new CreateEvolutionRequest(repositoryPath, "Updated", "src", "develop"), CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Direction);
        Assert.Equal("src", updated.Scope);
        Assert.True(await coordinator.DeleteAsync(evolution.Id, CancellationToken.None));
        Assert.Null(await store.GetAsync(evolution.Id, CancellationToken.None));
    }

    private sealed class MemoryEvolutionStore : IEvolutionStore
    {
        private readonly bool honorCancellation;
        private readonly List<Evolution> evolutions = [];

        public MemoryEvolutionStore(bool honorCancellation = false)
        {
            this.honorCancellation = honorCancellation;
        }

        public Task<IReadOnlyList<Evolution>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Evolution>>(evolutions);
        public Task<Evolution?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(evolutions.SingleOrDefault(item => item.Id == id));
        public Task<Evolution> SaveAsync(Evolution evolution, CancellationToken cancellationToken)
        {
            if (honorCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (!evolutions.Contains(evolution)) evolutions.Add(evolution);
            return Task.FromResult(evolution);
        }
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(evolutions.RemoveAll(item => item.Id == id) > 0);
        public Task<(Evolution Evolution, EvolutionEvent Event)?> ClaimNextEventAsync(CancellationToken cancellationToken)
        {
            var evolution = evolutions.FirstOrDefault(item => item.Status == EvolutionStatus.Running);
            var evolutionEvent = evolution?.Events.FirstOrDefault(entry => entry.Status == EvolutionEventStatus.Pending);
            if (evolution is null || evolutionEvent is null) return Task.FromResult<(Evolution, EvolutionEvent)?>(null);
            evolutionEvent.Status = EvolutionEventStatus.Processing;
            evolutionEvent.StartedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<(Evolution, EvolutionEvent)?>((evolution, evolutionEvent));
        }
    }

    private sealed class MultipleWorkItemRunner : IAgentRunner
    {
        public List<string> WorkerPrompts { get; } = [];

        public string GetPrompt(Evolution evolution, string eventType)
        {
            if (eventType != EvolutionEventTypes.WorkItemStarted) return eventType;
            var title = evolution.WorkItems.First(item => item.Status == "working").Title;
            WorkerPrompts.Add(title);
            return title;
        }

        public Task<AgentResult> RunAsync(Evolution evolution, string eventType, Func<string, CancellationToken, Task>? reportProgress, CancellationToken cancellationToken)
        {
            IReadOnlyList<WorkItem> workItems = eventType == EvolutionEventTypes.PlanStarted
                ? [new WorkItem { Title = "First item", Description = "First" }, new WorkItem { Title = "Second item", Description = "Second" }]
                : [];
            return Task.FromResult(new AgentResult(eventType, workItems, []));
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "CodeEvolver.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
