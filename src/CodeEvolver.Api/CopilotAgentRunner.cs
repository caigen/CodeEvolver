using System.Diagnostics;
using System.Security.Cryptography;
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
        var readOnly = configuration.GetValue("Agent:Copilot:ReadOnly", false);
        var workspaceFingerprint = readOnly
            ? await CaptureGitFingerprintAsync(evolution.RepositoryPath, [], cancellationToken)
            : null;
        var humanDesignFingerprint = await CaptureGitFingerprintAsync(
            evolution.RepositoryPath,
            ["HumanDesign"],
            cancellationToken);
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
        foreach (var argument in BuildArguments(prompt)) startInfo.ArgumentList.Add(argument);
        var secretEnvironmentVariables = configuration["Agent:Copilot:SecretEnvironmentVariables"];

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(configuration.GetValue("Agent:Copilot:TimeoutMinutes", 20)));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var errorOutput = new StringBuilder();
        string? lastProgress = null;
        var lastActivity = $"Waiting for the first streamed event for {eventType}.";
        var lastOutputAt = DateTimeOffset.UtcNow;
        using var progressGate = new SemaphoreSlim(1, 1);

        async Task ReportProgressAsync(string message)
        {
            message = RedactSecrets(message, secretEnvironmentVariables);
            if (reportProgress is null) return;
            await progressGate.WaitAsync(linkedCancellation.Token);
            try
            {
                if (string.Equals(message, lastProgress, StringComparison.Ordinal)) return;
                lastProgress = message;
                await reportProgress(message, linkedCancellation.Token);
            }
            finally
            {
                progressGate.Release();
            }
        }

        try
        {
            process.Start();
            lastActivity = $"Started Copilot process {process.Id} for {eventType}.";
            await ReportProgressAsync(lastActivity);
            var errorTask = ReadStandardErrorAsync();
            var readTask = process.StandardOutput.ReadLineAsync(linkedCancellation.Token).AsTask();
            while (true)
            {
                var completedTask = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(15), linkedCancellation.Token));
                if (completedTask != readTask)
                {
                    var quietFor = DateTimeOffset.UtcNow - lastOutputAt;
                    await ReportProgressAsync($"GitHub Copilot process {process.Id} is running {eventType}; no CLI event for {Math.Max(1, (int)quietFor.TotalSeconds)}s. Last activity: {lastActivity}");
                    continue;
                }

                var line = await readTask;
                if (line is null) break;
                output.AppendLine(line);
                lastOutputAt = DateTimeOffset.UtcNow;
                var progress = AgentOutputParser.GetProgress(line);
                if (progress is not null)
                {
                    lastActivity = progress;
                    await ReportProgressAsync(progress);
                }
                readTask = process.StandardOutput.ReadLineAsync(linkedCancellation.Token).AsTask();
            }
            await process.WaitForExitAsync(linkedCancellation.Token);
            await errorTask;
            var error = RedactSecrets(errorOutput.ToString().Trim(), secretEnvironmentVariables);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Copilot CLI failed ({process.ExitCode}): {error}");
            if (!string.IsNullOrEmpty(error)) logger.LogWarning("Copilot CLI: {Error}", error);

            var detail = AgentOutputParser.GetFinalResponse(output.ToString());
            if (detail is null)
                throw new InvalidOperationException($"Copilot CLI completed {eventType} without a recognizable final response.");
            detail = RedactSecrets(detail, secretEnvironmentVariables);
            var finalHumanDesignFingerprint = await CaptureGitFingerprintAsync(
                evolution.RepositoryPath,
                ["HumanDesign"],
                cancellationToken);
            if (!string.Equals(humanDesignFingerprint, finalHumanDesignFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException($"Copilot modified the protected HumanDesign directory during {eventType}.");
            if (readOnly)
            {
                var finalFingerprint = await CaptureGitFingerprintAsync(evolution.RepositoryPath, [], cancellationToken);
                if (!string.Equals(workspaceFingerprint, finalFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Copilot modified the repository during read-only {eventType}.");
            }
            var workItems = eventType == EvolutionEventTypes.PlanStarted ? AgentOutputParser.GetWorkItems(detail) : [];
            var logs = string.IsNullOrWhiteSpace(error) ? Array.Empty<string>() : [$"stderr: {error}"];
            return new AgentResult(detail, workItems, logs);

            async Task ReadStandardErrorAsync()
            {
                while (await process.StandardError.ReadLineAsync(linkedCancellation.Token) is { } line)
                {
                    errorOutput.AppendLine(line);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    lastOutputAt = DateTimeOffset.UtcNow;
                    lastActivity = AgentOutputParser.FormatCliLog(line);
                    await ReportProgressAsync(lastActivity);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessAsync(process);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"Copilot exceeded the {configuration.GetValue("Agent:Copilot:TimeoutMinutes", 20)} minute limit for {eventType}.");
            throw;
        }
    }

    internal IReadOnlyList<string> BuildArguments(string prompt)
    {
        var arguments = new List<string>
        {
            "--prompt", prompt, "--allow-all-tools", "--no-ask-user", "--no-color",
            "--output-format", "json", "--stream", "on"
        };
        if (!configuration.GetValue("Agent:Copilot:PublishChanges", true))
        {
            arguments.Add("--disable-builtin-mcps");
            arguments.Add("--deny-tool=shell(git commit)");
            arguments.Add("--deny-tool=shell(git push)");
            arguments.Add("--deny-tool=shell(gh pr create)");
        }
        var secretEnvironmentVariables = configuration["Agent:Copilot:SecretEnvironmentVariables"];
        if (!string.IsNullOrWhiteSpace(secretEnvironmentVariables))
            arguments.Add($"--secret-env-vars={secretEnvironmentVariables}");
        var maxAiCredits = configuration["Agent:Copilot:MaxAiCredits"];
        if (!string.IsNullOrWhiteSpace(maxAiCredits))
        {
            arguments.Add("--max-ai-credits");
            arguments.Add(maxAiCredits);
        }
        return arguments;
    }

    private string BuildPrompt(Evolution evolution, string eventType)
    {
        var context = $"Evolution direction: {evolution.Direction}\nScope: {evolution.Scope}\nTarget PR branch: {evolution.TargetBranch}\n";
        if (configuration.GetValue("Agent:Copilot:ReadOnly", false))
            context += "Read-only mode is enforced. Do not modify, create, delete, move, stage, or commit files.\n";
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
        string? pathExtensions = null,
        string? currentDirectory = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var extensions = windows && !Path.HasExtension(executable)
            ? (pathExtensions ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];
        if (Path.IsPathFullyQualified(executable) ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var basePath = currentDirectory ?? Environment.CurrentDirectory;
            foreach (var extension in extensions)
            {
                var candidate = Path.GetFullPath(executable + extension.ToLowerInvariant(), basePath);
                if (File.Exists(candidate)) return candidate;
            }
            return executable;
        }

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
        if (!windows || (!resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
                         !resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            return new AgentCommand(resolved, []);

        var searchDirectories = new[] { Path.GetDirectoryName(resolved)! }
            .Concat((pathEnvironment ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => directory.Trim('"')))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var npmLoader = searchDirectories
            .Select(directory => Path.Combine(directory, "node_modules", "@github", "copilot", "npm-loader.js"))
            .FirstOrDefault(File.Exists);
        if (npmLoader is null) return new AgentCommand(resolved, []);

        var loaderDirectory = Path.GetDirectoryName(npmLoader)!;
        var npmBinDirectory = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(loaderDirectory)))!;
        var bundledNode = Path.Combine(npmBinDirectory, "node.exe");
        var node = File.Exists(bundledNode)
            ? bundledNode
            : ResolveExecutable("node", pathEnvironment, isWindows: true, pathExtensions);
        return new AgentCommand(node, [npmLoader]);
    }

    internal static string RedactSecrets(string value, string? variableNames, Func<string, string?>? getEnvironmentVariable = null)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(variableNames)) return value;
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        foreach (var variableName in variableNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var secret = getEnvironmentVariable(variableName);
            if (!string.IsNullOrEmpty(secret))
                value = value.Replace(secret, "***", StringComparison.Ordinal);
        }
        return value;
    }

    internal static async Task TerminateProcessAsync(Process process)
    {
        if (!process.HasExited) process.Kill(true);
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(cleanupTimeout.Token);
    }

    private static async Task<string> CaptureGitFingerprintAsync(
        string repositoryPath,
        IReadOnlyList<string> pathspecs,
        CancellationToken cancellationToken)
    {
        var statusArguments = new List<string> { "status", "--porcelain=v1", "--untracked-files=all" };
        var diffArguments = new List<string> { "diff", "--binary", "HEAD", "--" };
        if (pathspecs.Count > 0)
        {
            statusArguments.Add("--");
            statusArguments.AddRange(pathspecs);
            diffArguments.AddRange(pathspecs);
        }
        var status = await RunGitAsync(repositoryPath, statusArguments, cancellationToken);
        var diff = await RunGitAsync(repositoryPath, diffArguments, cancellationToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(status + '\0' + diff)));
    }

    private static async Task<string> RunGitAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git failed while checking read-only workspace state ({process.ExitCode}): {error}");
        return await outputTask;
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
    public static string FormatCliLog(string line) => FormatProgress("Copilot CLI", line);

    public static string? GetProgress(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                return null;
            var type = typeElement.GetString();
            var data = root.TryGetProperty("data", out var value) ? value : default;
            if (type == "model.call_start") return "GitHub Copilot is reasoning.";
            if (type == "model.call_end") return "GitHub Copilot finished reasoning.";
            if (type == "assistant.turn_start") return "GitHub Copilot started a work turn.";
            if (type == "assistant.turn_end") return "GitHub Copilot finished a work turn.";
            if (type == "assistant.intent") return FormatProgress("GitHub Copilot intent", FindString(data, "intent"));
            if (type is "assistant.reasoning" or "assistant.reasoning_delta") return "GitHub Copilot reported a reasoning update.";
            if (type is "assistant.message_start" or "assistant.message_delta" or "assistant.streaming_delta")
                return "GitHub Copilot is composing its response.";
            if (type == "assistant.message") return FormatProgress("GitHub Copilot response", FindString(data, "content"));
            if (type == "tool.execution_progress")
                return FormatProgress("Tool progress", FindString(data, "progressMessage"));
            if (type == "tool.execution_start")
            {
                var name = FindString(data, "toolName") ?? FindString(data, "name") ?? "repository tool";
                return FormatProgress($"Using {name}", FindToolDetail(data));
            }
            if (type == "tool.execution_complete")
            {
                var succeeded = data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
                var detail = succeeded ? FindString(data, "content") : FindString(data, "message");
                return FormatProgress(succeeded ? "Tool completed" : "Tool failed", detail);
            }
            if (type is "session.error" or "session.warning" or "session.info")
                return FormatProgress($"Copilot {type[8..]}", FindEventDetail(data));
            if (type == "session.start") return FormatProgress("Copilot session started", FindEventDetail(data));
            if (type == "session.task_complete") return FormatProgress("Copilot task completed", FindEventDetail(data));
            if (type?.StartsWith("subagent.", StringComparison.Ordinal) == true)
                return FormatProgress($"Copilot {type.Replace('.', ' ')}", FindEventDetail(data));
            if (!string.IsNullOrWhiteSpace(type))
                return FormatProgress($"Copilot event {type}", FindEventDetail(data));
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(line) ? null : FormatProgress("Copilot output", line);
        }
        return string.IsNullOrWhiteSpace(line) ? null : "Copilot emitted an untyped event.";
    }

    private static string? FindToolDetail(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("arguments", out var arguments)) return null;
        foreach (var propertyName in new[] { "description", "explanation", "path", "filePath", "query", "command" })
        {
            var detail = FindString(arguments, propertyName);
            if (!string.IsNullOrWhiteSpace(detail)) return detail;
        }
        return null;
    }

    private static string? FindEventDetail(JsonElement data)
    {
        foreach (var propertyName in new[] { "message", "description", "title", "status", "reason", "model", "name" })
        {
            var detail = FindString(data, propertyName);
            if (!string.IsNullOrWhiteSpace(detail)) return detail;
        }
        return null;
    }

    private static string FormatProgress(string label, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return $"{label}.";
        var singleLine = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (singleLine.Length > 240) singleLine = $"{singleLine[..237]}...";
        return $"{label}: {singleLine}";
    }

    public static string? GetFinalResponse(string jsonLines)
    {
        string? response = null;
        string? taskSummary = null;
        var streamedResponse = new StringBuilder();
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "assistant.message" &&
                    root.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                    response = content.GetString();
                else if (root.TryGetProperty("type", out type) &&
                         type.ValueKind == JsonValueKind.String &&
                         type.GetString() == "assistant.message_delta" &&
                         root.TryGetProperty("data", out data) &&
                         data.ValueKind == JsonValueKind.Object &&
                         data.TryGetProperty("deltaContent", out var delta) &&
                         delta.ValueKind == JsonValueKind.String)
                    streamedResponse.Append(delta.GetString());
                else if (root.TryGetProperty("type", out type) &&
                         type.ValueKind == JsonValueKind.String &&
                         type.GetString() == "session.task_complete" &&
                         root.TryGetProperty("data", out data) &&
                         data.ValueKind == JsonValueKind.Object &&
                         data.TryGetProperty("summary", out var summary) &&
                         summary.ValueKind == JsonValueKind.String)
                    taskSummary = summary.GetString();
            }
            catch (JsonException) { }
        }
        var finalResponse = !string.IsNullOrWhiteSpace(response)
            ? response
            : !string.IsNullOrWhiteSpace(taskSummary)
                ? taskSummary
                : streamedResponse.ToString();
        return string.IsNullOrWhiteSpace(finalResponse) ? null : finalResponse.Trim();
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