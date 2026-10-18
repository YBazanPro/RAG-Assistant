using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common.Text;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>Which language a question is written in, and which one its answer is written in (spec 018).</summary>
internal static class QuestionLanguages
{
    /// <summary>Questions are short; documents need 20 words, a question 3 (spec 018, clarification 1).</summary>
    public const int MinWords = 3;

    /// <summary>
    /// The detector decides when it is sure; otherwise the conversation's previous question does, otherwise English. Only questions
    /// before <paramref name="beforeSequence"/> count, so a retry resolves the same way the first attempt did.
    /// </summary>
    public static async Task<(string Language, QuestionLanguageSource Source)> ResolveAsync(
        ChatDbContext db, Guid conversationId, int beforeSequence, string question, CancellationToken cancellationToken)
    {
        if (LanguageDetector.Detect(question, MinWords) is { IsConfident: true, Language: { } detected })
        {
            return (detected, QuestionLanguageSource.Detected);
        }

        var previous = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.User && m.Sequence < beforeSequence && m.QuestionLanguage != null)
            .OrderByDescending(m => m.Sequence)
            .Select(m => m.QuestionLanguage)
            .FirstOrDefaultAsync(cancellationToken);

        return previous is null
            ? (LanguageDetector.English, QuestionLanguageSource.Default)
            : (previous, QuestionLanguageSource.Conversation);
    }

    /// <summary>The rewrite's requested language as a known ISO 639-1 code, or null when it names none the runtime knows.</summary>
    public static string? NormalizeRequested(string? code)
    {
        var candidate = code?.Trim().ToLowerInvariant();
        if (candidate is not { Length: 2 } || !candidate.All(char.IsAsciiLetterLower))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(candidate, predefinedOnly: true).TwoLetterISOLanguageName == candidate ? candidate : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The English name the prompt uses ("French"), from the runtime's culture data.</summary>
    public static string Name(string code) => CultureInfo.GetCultureInfo(code).EnglishName;
}
