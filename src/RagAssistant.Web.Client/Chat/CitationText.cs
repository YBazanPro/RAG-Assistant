using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Chat;

/// <summary>The wording of a citation in its three places: the list, the accessible name and the copied reference.</summary>
public static class CitationText
{
    public static string Section(Citation c) => string.Join(" › ", c.SectionPath);

    public static string Version(Citation c) => $"version {c.VersionNumber}";

    public static string? Pages(Citation c) => c.PageStart is { } start ? Formatting.PageLabel(start, c.PageEnd ?? start) : null;

    /// <summary>"Source 1: Budget Exception Policy, Approval › Thresholds, page 4".</summary>
    public static string AccessibleName(Citation c)
    {
        var parts = new List<string> { c.DocumentTitle };
        if (c.SectionPath.Count > 0)
        {
            parts.Add(Section(c));
        }

        if (c.PageStart is { } start)
        {
            parts.Add(c.PageEnd is { } end && end > start ? $"pages {start}–{end}" : $"page {start}");
        }

        if (c.Language is { } language)
        {
            parts.Add($"in {LanguageName(language)}");
        }

        // Spec 020: the state is part of the name, so a screen reader hears it with the citation, not as a separate badge.
        if (SourceStates.Badge(c.SourceState) is { } state)
        {
            parts.Add(state.ToLowerInvariant());
        }

        return $"Source {c.Marker}: {string.Join(", ", parts)}";
    }

    /// <summary>"English", "French", or the code itself for another language; the UI stays English (spec 018).</summary>
    public static string LanguageName(string code) => code switch
    {
        "en" => "English",
        "fr" => "French",
        _ => code,
    };

    /// <summary>"Budget Exception Policy — Approval › Thresholds (version 3, p. 4)".</summary>
    public static string Reference(Citation c)
    {
        var where = Pages(c) is { } pages ? $"{Version(c)}, {pages}" : Version(c);
        var title = c.SectionPath.Count > 0 ? $"{c.DocumentTitle} — {Section(c)}" : c.DocumentTitle;
        return $"{title} ({where})";
    }

    /// <summary>The route of the document detail page, focused on the cited passage of the cited version.</summary>
    public static string DocumentLink(Citation c) => $"documents/{c.DocumentId}?version={c.VersionNumber}&passage={c.PassageId}";

    /// <summary>The document detail page at its active version (spec 020, "Open current version").</summary>
    public static string CurrentDocumentLink(Citation c) => $"documents/{c.DocumentId}";

    public static string OriginalFileLink(Citation c) => $"api/documents/{c.DocumentId}/versions/{c.VersionNumber}/file#page={c.PageStart}";
}
