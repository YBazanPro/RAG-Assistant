using System.Text.RegularExpressions;

namespace RagAssistant.Modules.Documents.Tests;

// Spec 016, AC-24: every request path in the Documents module that reads documents, versions or passages goes through ReadableBy.
public sealed partial class VisibilityRuleCoverageTests
{
    // Files that reach the tables only after a call that applies the rule; the value says which.
    private static readonly Dictionary<string, string> GuardedElsewhere = new(StringComparer.Ordinal)
    {
        ["ReprocessDocument.cs"] = "NewVersionFlow.GuardAsync",
    };

    private static readonly string[] Folders = ["Features", "Retrieval", "Common"];

    private static string ModuleFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RagAssistant.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("RagAssistant.slnx was not found above the test binaries."),
            "src", "Modules", "Documents", "RagAssistant.Modules.Documents");
    }

    [GeneratedRegex(@"\bdb\.(Documents|Versions|Passages)\b")]
    private static partial Regex TableAccess();

    [Test]
    public async Task RequestPaths_thatReadDocumentTables_applyTheVisibilityRule()
    {
        var module = ModuleFolder();
        var files = Folders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(module, folder), "*.cs", SearchOption.AllDirectories));

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file);
            if (!TableAccess().IsMatch(text) && !text.Contains("Filtered(", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            var applies = text.Contains("ReadableBy(", StringComparison.Ordinal)
                || (GuardedElsewhere.TryGetValue(name, out var guard) && text.Contains(guard, StringComparison.Ordinal));
            if (!applies)
            {
                offenders.Add(name);
            }
        }

        await Assert.That(offenders).IsEmpty();
    }
}
