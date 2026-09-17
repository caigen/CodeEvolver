using System.Diagnostics;

namespace CodeEvolver.Api;

public static class RepositoryDiscovery
{
    public static string? FindRoot(string startPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath)) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    public static async Task<string?> SelectDirectoryAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The native repository picker is currently available on Windows only.");

        const string script = "Add-Type -AssemblyName System.Windows.Forms; " +
            "$dialog = [System.Windows.Forms.FolderBrowserDialog]::new(); " +
            "$dialog.Description = 'Select cloned Git repository'; " +
            "$dialog.ShowNewFolderButton = $false; " +
            "if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($dialog.SelectedPath) }";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-STA");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not open the repository picker.");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The repository picker failed: {error}");
            return string.IsNullOrWhiteSpace(output) ? null : Path.GetFullPath(output);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }
}