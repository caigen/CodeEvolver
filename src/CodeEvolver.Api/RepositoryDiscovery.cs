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
}