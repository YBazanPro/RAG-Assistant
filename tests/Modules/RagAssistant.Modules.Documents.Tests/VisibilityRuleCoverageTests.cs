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

    // Maintainer views that must not use ReadableBy (spec 017, Authorization): maintainers have no bypass for content, so these
    // return aggregates, metadata or shared-only titles by construction. They may never read passages.
    private static readonly Dictionary<string, string> MetadataOnly = new(StringComparer.Ordinal)
    {
        ["GetHealth.cs"] = "counts only; identifies no private document",
        ["ListFailures.cs"] = "title, owner and error of a failed version; no retry for private documents",
        ["ListPrivateDocuments.cs"] = "title, owner, status and dates of private documents; nothing else",
        ["DocumentTitles.cs"] = "filters on Visibility.Shared itself and takes no viewer",
    };

    // Spec 020: citation source states must see documents the viewer lost access to, to call them "unavailable". The rule is
    // applied inline to decide that state; titles and passage text never leave, only a state per cited passage.
    private static readonly Dictionary<string, string> StateOnly = new(StringComparer.Ordinal)
    {
        ["DocumentSources.cs"] = "applies Shared-or-CreatedBy inline; returns states, never titles or text",
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

    [GeneratedRegex(@"\bdb\.Passages\b")]
    private static partial Regex PassageAccess();

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
            if (MetadataOnly.ContainsKey(name))
            {
                if (PassageAccess().IsMatch(text))
                {
                    offenders.Add($"{name} (a metadata-only view must not read passages)");
                }

                continue;
            }

            if (StateOnly.ContainsKey(name))
            {
                if (!text.Contains("d.Visibility == Visibility.Shared || d.CreatedBy == viewerSub", StringComparison.Ordinal))
                {
                    offenders.Add($"{name} (a state-only view must still apply the rule inline)");
                }

                continue;
            }

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
