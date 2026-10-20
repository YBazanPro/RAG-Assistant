namespace RagAssistant.Web.Client.Chat;

/// <summary>How a cited source stands today (spec 020), as the API spells it, and the words the UI uses for it.</summary>
public static class SourceStates
{
    public const string Current = "current";
    public const string NewerUnchanged = "newer-unchanged";
    public const string NewerChanged = "newer-changed";
    public const string Archived = "archived";
    public const string Unavailable = "unavailable";

    /// <summary>The badge next to a citation; null for a current source, which needs none.</summary>
    public static string? Badge(string state) => state switch
    {
        NewerUnchanged => "Newer version",
        NewerChanged => "Changed in newer version",
        Archived => "Archived",
        Unavailable => "No longer available",
        _ => null,
    };

    /// <summary>What the source preview says above the snapshot; null when nothing changed.</summary>
    public static string? PreviewNotice(string state) => state switch
    {
        NewerUnchanged => "A newer version contains this passage unchanged.",
        NewerChanged => "This source has changed since the answer.",
        Archived => "The document has been archived since the answer.",
        _ => null,
    };

    /// <summary>A newer version exists, so the preview offers to open it.</summary>
    public static bool HasNewerVersion(string state) => state is NewerUnchanged or NewerChanged;

    /// <summary>The note after a source line in a copied answer; the export writes the same notes.</summary>
    public static string? ListNote(string state) => state switch
    {
        NewerUnchanged => "newer version, passage unchanged",
        NewerChanged => "changed in a newer version",
        Archived => "archived",
        _ => null,
    };
}
