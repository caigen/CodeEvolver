using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeEvolver.Api;

public sealed class CopilotAgentRunner(IConfiguration configuration, ILogger<CopilotAgentRunner> logger) : IAgentRunner
{
    public string GetPrompt(Evolution evolution, string eventType) => BuildPrompt(evolution, eventType);

    public async Task<AgentResult> RunAsync(Evolution evolution, string eventType, Func<string, CancellationToken, Task>? reportProgress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(evolution.RepositoryPath))
            throw new DirectoryNotFoundException($"Repository path '{evolution.RepositoryPath}' does not exist.");

        var prompt = BuildPrompt(evolution, eventType);
        var command = ResolveCommand(configuration["Agent:Copilot:Executable"] ?? "copilot");
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = evolution.RepositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in command.PrefixArguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--prompt");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--allow-all-tools");
        startInfo.ArgumentList.Add("--no-ask-user");
        startInfo.ArgumentList.Add("--no-color");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--stream");
        startInfo.ArgumentList.Add("on");
        var secretEnvironmentVariables = configuration["Agent:Copilot:SecretEnvironmentVariables"];
        if (!string.IsNullOrWhiteSpace(secretEnvironmentVariables))
            startInfo.ArgumentList.Add($"--secret-env-vars={secretEnvironmentVariables}");
        var maxAiCredits = configuration["Agent:Copilot:MaxAiCredits"];
        if (!string.IsNullOrWhiteSpace(maxAiCredits))
        {
            startInfo.ArgumentList.Add("--max-ai-credits");
            startInfo.ArgumentList.Add(maxAiCredits);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(configuration.GetValue("Agent:Copilot:TimeoutMinutes", 20)));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        try
        {
            process.Start();
            var errorTask = process.StandardError.ReadToEndAsync(linkedCancellation.Token);
            while (await process.StandardOutput.ReadLineAsync(linkedCancellation.Token) is { } line)
            {
                output.AppendLine(line);
                var progress = AgentOutputParser.GetProgress(line);
                if (progress is not null && reportProgress is not null)
                    await reportProgress(progress, linkedCancellation.Token);
            }
            await process.WaitForExitAsync(linkedCancellation.Token);
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Copilot CLI failed ({process.ExitCode}): {error}");
            if (!string.IsNullOrEmpty(error)) logger.LogWarning("Copilot CLI: {Error}", error);

            var detail = AgentOutputParser.GetFinalResponse(output.ToString()) ?? $"Copilot completed {eventType}.";
            var workItems = eventType == EvolutionEventTypes.PlanStarted ? AgentOutputParser.GetWorkItems(detail) : [];
            var logs = string.IsNullOrWhiteSpace(error) ? Array.Empty<string>() : [$"stderr: {error}"];
            return new AgentResult(detail, workItems, logs);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(true);
            throw new TimeoutException($"Copilot exceeded the {configuration.GetValue("Agent:Copilot:TimeoutMinutes", 20)} minute limit for {eventType}.");
        }
    }

    private string BuildPrompt(Evolution evolution, string eventType)
    {
        var context = $"Evolution direction: {evolution.Direction}\nScope: {evolution.Scope}\nTarget PR branch: {evolution.TargetBranch}\n";
        return eventType switch
        {
            EvolutionEventTypes.ScanStarted => context + "Inspect the scoped code. Do not edit files. Return a concise technical scan with risks and relevant paths.",
            EvolutionEventTypes.PlanStarted => context + "Plan at most five independent work items. Each item must be implementable and validated in at most 15 minutes. Do not edit files. Return only a JSON array where every object has string properties title and description.",
            EvolutionEventTypes.WorkItemStarted => context + BuildWorkItemPrompt(evolution),
            EvolutionEventTypes.ReviewStarted => context + "Review the current uncommitted changes against the direction. Fix concrete defects you find, then rerun focused validation. Do not modify HumanDesign.",
            EvolutionEventTypes.GateStarted => context + "Run the repository's build and unit tests for the changed scope. Fix only failures caused by this evolution. Return exact pass/fail results. Do not modify HumanDesign.",
            EvolutionEventTypes.ChangeMerged when configuration.GetValue("Agent:Copilot:PublishChanges", true) =>
                context + $"Create a commit for the validated changes, push an evolution branch named evolution/{evolution.Id:N}, and open a pull request targeting {evolution.TargetBranch} when repository credentials and tooling allow it. Never force push. Return the commit and PR URL, or clearly explain what external prerequisite is missing.",
            EvolutionEventTypes.ChangeMerged =>
                context + "Do not commit, push, or open a pull request. Leave validated changes in the working tree and summarize their status.",
            _ => context + $"Process lifecycle event {eventType} and report the result."
        };
    }

    internal static string ResolveExecutable(
        string executable,
        string? pathEnvironment = null,
        bool? isWindows = null,
        string? pathExtensions = null)
    {
        if (Path.IsPathFullyQualified(executable) ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
            return executable;

        var windows = isWindows ?? OperatingSystem.IsWindows();
        var extensions = windows && !Path.HasExtension(executable)
            ? (pathExtensions ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];
        foreach (var directory in (pathEnvironment ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), executable + extension.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }

        return executable;
    }

    internal static AgentCommand ResolveCommand(
        string executable,
        string? pathEnvironment = null,
        bool? isWindows = null,
        string? pathExtensions = null)
    {
        var resolved = ResolveExecutable(executable, pathEnvironment, isWindows, pathExtensions);
        var windows = isWindows ?? OperatingSystem.IsWindows();
        if (!windows || !resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return new AgentCommand(resolved, []);

        var directory = Path.GetDirectoryName(resolved)!;
        var npmLoader = Path.Combine(directory, "node_modules", "@github", "copilot", "npm-loader.js");
        if (!File.Exists(npmLoader)) return new AgentCommand(resolved, []);

        var bundledNode = Path.Combine(directory, "node.exe");
        var node = File.Exists(bundledNode)
            ? bundledNode
            : ResolveExecutable("node", pathEnvironment, isWindows: true, pathExtensions);
        return new AgentCommand(node, [npmLoader]);
    }

    private static string BuildWorkItemPrompt(Evolution evolution)
    {
        var workItem = evolution.WorkItems.FirstOrDefault(item => item.Status is "working" or "planned");
        return workItem is null
            ? "Implement the direction within 15 minutes. Run focused validation and summarize the result. Do not modify HumanDesign."
            : $"Implement only this work item within 15 minutes:\nTitle: {workItem.Title}\nDescription: {workItem.Description}\nRun focused checks after the first edit. Do not modify HumanDesign. Finish and summarize edits and validation.";
    }
}

internal sealed record AgentCommand(string FileName, IReadOnlyList<string> PrefixArguments);

public static class AgentOutputParser
{
    public static string? GetProgress(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "model.call_start") return "Agent is reasoning.";
            if (type == "assistant.turn_start") return "Agent started a work turn.";
            if (type?.Contains("tool", StringComparison.OrdinalIgnoreCase) == true)
            {
                var data = root.TryGetProperty("data", out var value) ? value : default;
                var name = FindString(data, "toolName") ?? FindString(data, "name") ?? "repository tool";
                return $"Using {name}.";
            }
        }
        catch (JsonException) { }
        return null;
    }

    public static string? GetFinalResponse(string jsonLines)
    {
        string? response = null;
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("type").GetString() == "assistant.message" &&
                    root.GetProperty("data").TryGetProperty("content", out var content))
                    response = content.GetString();
            }
            catch (JsonException) { }
        }
        return string.IsNullOrWhiteSpace(response) ? null : response.Trim();
    }

    public static IReadOnlyList<WorkItem> GetWorkItems(string response)
    {
        try
        {
            var start = response.IndexOf('[');
            var end = response.LastIndexOf(']');
            if (start < 0 || end <= start) return [];
            using var document = JsonDocument.Parse(response[start..(end + 1)]);
            return document.RootElement.EnumerateArray()
                .Take(5)
                .Select(item => new WorkItem
                {
                    Title = item.GetProperty("title").GetString()?.Trim() ?? "Untitled work item",
                    Description = item.GetProperty("description").GetString()?.Trim() ?? ""
                })
                .ToArray();
        }
        catch (JsonException) { return []; }
        catch (InvalidOperationException) { return []; }
        catch (KeyNotFoundException) { return []; }
    }

    private static string? FindString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, propertyName);
                if (nested is not null) return nested;
            }
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
            var nested = FindString(property.Value, propertyName);
            if (nested is not null) return nested;
        }
        return null;
    }
}