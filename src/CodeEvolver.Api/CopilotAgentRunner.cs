using System.Diagnostics;
using System.Text;

namespace CodeEvolver.Api;

public sealed class CopilotAgentRunner(IConfiguration configuration, ILogger<CopilotAgentRunner> logger) : IAgentRunner
{
    public string GetPrompt(Evolution evolution, string eventType) => BuildPrompt(evolution, eventType);

    public async Task<AgentResult> RunAsync(Evolution evolution, string eventType, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(evolution.RepositoryPath))
            throw new DirectoryNotFoundException($"Repository path '{evolution.RepositoryPath}' does not exist.");

        var prompt = BuildPrompt(evolution, eventType);
        var startInfo = new ProcessStartInfo
        {
            FileName = configuration["Agent:Copilot:Executable"] ?? "copilot",
            WorkingDirectory = evolution.RepositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--prompt");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--allow-all-tools");
        startInfo.ArgumentList.Add("--no-ask-user");
        startInfo.ArgumentList.Add("--no-color");
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--stream");
        startInfo.ArgumentList.Add("off");
        startInfo.ArgumentList.Add("--max-ai-credits");
        startInfo.ArgumentList.Add(configuration["Agent:Copilot:MaxAiCredits"] ?? "1");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Copilot CLI failed ({process.ExitCode}): {error}");
        if (!string.IsNullOrEmpty(error)) logger.LogWarning("Copilot CLI: {Error}", error);

        var detail = string.IsNullOrWhiteSpace(output) ? $"Copilot completed {eventType}." : output;
        var logs = new List<string>();
        if (!string.IsNullOrWhiteSpace(output)) logs.Add(output);
        if (!string.IsNullOrWhiteSpace(error)) logs.Add($"stderr: {error}");
        return new AgentResult(detail, [], logs);
    }

    private static string BuildPrompt(Evolution evolution, string eventType)
    {
        var context = $"Evolution direction: {evolution.Direction}\nScope: {evolution.Scope}\nTarget PR branch: {evolution.TargetBranch}\n";
        return eventType switch
        {
            EvolutionEventTypes.ScanStarted => context + "Inspect the scoped code. Do not edit files. Return a concise technical scan with risks and relevant paths.",
            EvolutionEventTypes.PlanStarted => context + "Plan one agent-world day of at most five focused work items. Do not edit files. Return a concise numbered plan.",
            EvolutionEventTypes.WorkItemStarted => context + "Implement the direction end to end within scope. Run focused checks after the first edit. Do not modify HumanDesign. Finish all feasible work and summarize edits and validation.",
            EvolutionEventTypes.ReviewStarted => context + "Review the current uncommitted changes against the direction. Fix concrete defects you find, then rerun focused validation. Do not modify HumanDesign.",
            EvolutionEventTypes.GateStarted => context + "Run the repository's build and unit tests for the changed scope. Fix only failures caused by this evolution. Return exact pass/fail results. Do not modify HumanDesign.",
            EvolutionEventTypes.ChangeMerged => context + $"Create a commit for the validated changes, push an evolution branch named evolution/{evolution.Id:N}, and open a pull request targeting {evolution.TargetBranch} when repository credentials and tooling allow it. Never force push. Return the commit and PR URL, or clearly explain what external prerequisite is missing.",
            _ => context + $"Process lifecycle event {eventType} and report the result."
        };
    }
}