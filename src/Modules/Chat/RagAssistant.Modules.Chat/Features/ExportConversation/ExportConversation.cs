using System.Globalization;
using System.Text;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Documents;
using Conversations = RagAssistant.Modules.Chat.Features.GetConversation.GetConversation;

namespace RagAssistant.Modules.Chat.Features.ExportConversation;

/// <summary>
/// A conversation as a Markdown file (spec 020): questions and answers in order, each answer's sources as a numbered list with their
/// state today. Passage text is not exported, so nothing beyond what the answer itself says leaves the app.
/// </summary>
internal static class ExportConversation
{
    public sealed record Query(string UserId, Guid ConversationId) : IRequest<Result<ExportFile>>;

    public sealed record ExportFile(string FileName, string Markdown);

    internal sealed class Handler(ChatDbContext db, IDocumentSources sources, TimeProvider clock) : IRequestHandler<Query, Result<ExportFile>>
    {
        public async ValueTask<Result<ExportFile>> Handle(Query query, CancellationToken cancellationToken)
        {
            var conversation = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == query.ConversationId && c.OwnerSub == query.UserId)
                .Include(c => c.Messages.OrderBy(m => m.Sequence))
                .ThenInclude(m => m.Citations.OrderBy(s => s.Marker))
                .AsSplitQuery()
                .FirstOrDefaultAsync(cancellationToken);
            if (conversation is null)
            {
                return ChatErrors.ConversationNotFound;
            }

            var states = await Conversations.Handler.SourceStatesAsync(sources, conversation, query.UserId, cancellationToken);
            var exportedAt = clock.GetUtcNow();
            var title = conversation.Title ?? "New conversation";

            var md = new StringBuilder();
            md.Append("# ").Append(title).Append("\n\n");
            md.Append(CultureInfo.InvariantCulture, $"Started {Date(conversation.CreatedAt)}, exported {Date(exportedAt)} from RagAssistant.\n");

            var number = 0;
            foreach (var message in conversation.Messages)
            {
                if (message.Role == MessageRole.User)
                {
                    number++;
                    md.Append(CultureInfo.InvariantCulture, $"\n## Question {number}\n\n").Append(message.Text).Append('\n');
                    continue;
                }

                md.Append("\n### Answer");
                if (StatusNote(message) is { } note)
                {
                    md.Append(" (").Append(note).Append(')');
                }

                md.Append("\n\n");
                if (message.Text.Length > 0)
                {
                    md.Append(message.Text).Append('\n');
                }

                if (message.Citations.Count > 0)
                {
                    md.Append("\nSources:\n\n");
                    foreach (var citation in message.Citations)
                    {
                        var state = states.GetValueOrDefault(Conversations.Handler.Cited(citation), SourceState.Current);
                        md.Append(SourceList.Line(citation, state)).Append('\n');
                    }
                }
            }

            return new ExportFile($"{Slug(title)}-{exportedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.md", md.ToString());
        }

        private static string Date(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        private static string? StatusNote(Message answer) => answer.Status switch
        {
            AnswerStatus.Partial => "partial",
            AnswerStatus.Refused when answer.RefusalReason == RefusalReason.NoEvidence => "refused: the documents do not answer this",
            AnswerStatus.Refused => "refused: the sources did not support an answer",
            AnswerStatus.Failed => $"failed: {ChatErrors.StreamMessage(answer.ErrorCode ?? ChatErrors.InternalCode)}",
            AnswerStatus.Cancelled => "cancelled",
            AnswerStatus.Pending => "in progress",
            _ => null,
        };
    }

    /// <summary>ASCII letters and digits joined by hyphens, accents folded; "conversation" when nothing is left.</summary>
    internal static string Slug(string title)
    {
        var slug = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormD))
        {
            if (slug.Length >= 60)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(char.ToLowerInvariant(c));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var result = slug.ToString().Trim('-');
        return result.Length == 0 ? "conversation" : result;
    }
}

/// <summary>
/// One line per cited source, as the export writes it. The web client's "Copy answer with sources" writes the same lines
/// (<c>Web.Client/Chat/SourceListText.cs</c>), so keep both in step.
/// </summary>
internal static class SourceList
{
    public static string Line(CitationSnapshot c, SourceState state)
    {
        if (state == SourceState.Unavailable)
        {
            return string.Create(CultureInfo.InvariantCulture, $"- [{c.Marker}] Source no longer available");
        }

        var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"- [{c.Marker}] {c.DocumentTitle}"));
        if (c.SectionPath.Length > 0)
        {
            line.Append(" — ").Append(string.Join(" › ", c.SectionPath));
        }

        line.Append(CultureInfo.InvariantCulture, $" (version {c.VersionNumber}");
        if (c.PageStart is { } start)
        {
            line.Append(c.PageEnd is { } end && end > start
                ? string.Create(CultureInfo.InvariantCulture, $", pages {start}–{end}")
                : string.Create(CultureInfo.InvariantCulture, $", page {start}"));
        }

        line.Append(')');
        if (StateNote(state) is { } note)
        {
            line.Append(" — ").Append(note);
        }

        return line.ToString();
    }

    private static string? StateNote(SourceState state) => state switch
    {
        SourceState.NewerUnchanged => "newer version, passage unchanged",
        SourceState.NewerChanged => "changed in a newer version",
        SourceState.Archived => "archived",
        _ => null,
    };
}
