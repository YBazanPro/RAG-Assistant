using System.Buffers;
using System.Collections.Frozen;

namespace RagAssistant.SharedKernel.Common.Text;

/// <summary>
/// Tells English from French (spec 018) by counting short function words, with French diacritics as a weaker signal. A text
/// statistic, not a business rule, so Documents (document language) and Chat (question language) share it. Pure and deterministic:
/// the same text always gives the same answer, which keeps the pipeline testable with fake chat clients.
/// </summary>
public static class LanguageDetector
{
    public const string English = "en";
    public const string French = "fr";

    /// <summary>Only the start of a long text is read; a document's language shows long before its end.</summary>
    public const int MaxCharacters = 20_000;

    /// <summary>A document needs this many words before a detection counts as confident (spec 018).</summary>
    public const int DefaultMinWords = 20;

    /// <summary>The winning language's share of the matched signals needed for a confident detection.</summary>
    public const double MinShare = 0.7;

    // Words that belong to one language only: "on", "as", "en" or "a" exist in both and would only blur the counts.
    private static readonly FrozenSet<string> EnglishWords = FrozenSet.Create(
        StringComparer.Ordinal,
        "the", "and", "of", "to", "is", "in", "that", "it", "for", "with", "are", "be", "by", "this", "an", "or", "not", "from",
        "at", "which", "have", "has", "was", "were", "will", "can", "must", "should", "if", "any", "all", "each", "may", "their",
        "they", "you", "your", "we", "our", "its", "when", "what", "how", "does", "do", "who", "where", "there", "these", "those");

    // Elided forms ("l'", "d'", "qu'") are split off at the apostrophe, so the single letters count as French words too.
    private static readonly FrozenSet<string> FrenchWords = FrozenSet.Create(
        StringComparer.Ordinal,
        "le", "la", "les", "des", "du", "de", "et", "est", "un", "une", "dans", "pour", "que", "qui", "sur", "par", "pas", "ne",
        "au", "aux", "avec", "ce", "cette", "ces", "sont", "être", "ou", "où", "sa", "ses", "leur", "leurs", "il", "elle", "nous",
        "vous", "doit", "peut", "tout", "tous", "quel", "quelle", "quels", "quelles", "comment", "pourquoi", "quand", "l", "d",
        "qu", "j", "c", "n", "mes", "mon", "ma", "notre", "nos", "votre", "vos", "été", "fait", "selon");

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> EnglishLookup = EnglishWords.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> FrenchLookup = FrenchWords.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly SearchValues<char> FrenchLetters = SearchValues.Create("éèêëàâçùûüîïôœ");

    /// <param name="minWords">
    /// Fewer words than this is never confident. Documents keep the default; a caller with short input of its own (a question)
    /// may lower it, accepting that fewer signals decide.
    /// </param>
    public static LanguageDetection Detect(string? text, int minWords = DefaultMinWords)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minWords);
        if (string.IsNullOrWhiteSpace(text))
        {
            return LanguageDetection.Unknown;
        }

        var span = text.AsSpan(0, Math.Min(text.Length, MaxCharacters));
        var words = 0;
        var english = 0.0;
        var french = 0.0;
        Span<char> buffer = stackalloc char[64];

        var start = -1;
        for (var i = 0; i <= span.Length; i++)
        {
            var isLetter = i < span.Length && char.IsLetter(span[i]);
            if (isLetter)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start < 0)
            {
                continue;
            }

            var word = span[start..i];
            start = -1;
            words++;
            if (word.Length > buffer.Length)
            {
                continue;
            }

            var lower = buffer[..word.Length];
            word.ToLowerInvariant(lower);
            if (EnglishLookup.Contains(lower))
            {
                english++;
            }
            else if (FrenchLookup.Contains(lower))
            {
                french++;
            }
            else if (lower.IndexOfAny(FrenchLetters) >= 0)
            {
                // Weaker than a function word: English text borrows "café" or "résumé".
                french += 0.5;
            }
        }

        var total = english + french;
        if (total == 0)
        {
            return LanguageDetection.Unknown;
        }

        var (language, winner) = french > english ? (French, french) : (English, english);
        var confident = english != french && words >= minWords && winner / total >= MinShare;
        return new LanguageDetection(language, confident);
    }
}

/// <param name="Language">The best guess (<c>en</c> or <c>fr</c>), or null when the text gave no signal at all.</param>
/// <param name="IsConfident">True when the guess can be acted on; an uncertain guess is only worth recording.</param>
public readonly record struct LanguageDetection(string? Language, bool IsConfident)
{
    public static LanguageDetection Unknown => new(null, false);
}
