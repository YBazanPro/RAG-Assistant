namespace RagAssistant.Evaluation;

/// <summary>Finds the repository root (the folder holding <c>RagAssistant.slnx</c>), so the tool works from any working directory inside it.</summary>
internal static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));

    private static string FindRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RagAssistant.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException("Run the evaluation tool from inside the RagAssistant repository: RagAssistant.slnx was not found.");
    }
}
