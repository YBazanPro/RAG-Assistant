namespace RagAssistant.SharedKernel.Contracts.Documents;

/// <summary>How a cited source stands today compared with the version an answer cited (spec 020). Implemented by the Documents module.</summary>
public interface IDocumentSources
{
    /// <returns>A state for every requested passage. A document <paramref name="viewerSub"/> cannot read, or that no longer exists, is <see cref="SourceState.Unavailable"/>.</returns>
    Task<IReadOnlyDictionary<CitedPassage, SourceState>> GetStatesAsync(
        IReadOnlyCollection<CitedPassage> passages, string viewerSub, CancellationToken cancellationToken);
}

/// <param name="TextHash">The cited text's <see cref="Common.Text.PassageTextHash"/>; null when it is unknown, which never matches.</param>
public sealed record CitedPassage(Guid DocumentId, Guid VersionId, string? TextHash);

public enum SourceState
{
    /// <summary>The cited version is still the active one.</summary>
    Current,

    /// <summary>A newer active version holds a passage with the same normalised text.</summary>
    NewerUnchanged,

    /// <summary>A newer active version exists and none of its passages matches.</summary>
    NewerChanged,

    Archived,

    /// <summary>The viewer can no longer read the document (spec 016).</summary>
    Unavailable,
}
