using System.Globalization;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.Home;

/// <summary>Suggested questions for the home page and an empty conversation (spec 020), drawn at random from readable documents.</summary>
internal static class GetSuggestions
{
    public const int DefaultCount = 6;
    public const int MaxCount = 12;

    /// <param name="Count">Raw text, so a malformed value surfaces as a field error.</param>
    public sealed record Query(string? Count, string ViewerSub) : IRequest<Result<IReadOnlyList<Suggestion>>>;

    public sealed record Suggestion(string Question, Guid DocumentId, string DocumentTitle, string Language);

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator() =>
            RuleFor(q => q.Count)
                .Must(c => c is null || (int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxCount))
                .WithMessage($"Count must be a whole number from 1 to {MaxCount}.");
    }

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<IReadOnlyList<Suggestion>>>
    {
        public async ValueTask<Result<IReadOnlyList<Suggestion>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var count = query.Count is null ? DefaultCount : int.Parse(query.Count, CultureInfo.InvariantCulture);

            // A few questions per document over a demo-sized corpus: drawing in memory keeps the query a plain projection.
            var rows = await db.Documents.AsNoTracking().ReadableBy(query.ViewerSub)
                .Where(d => d.Status == DocumentStatus.Active && d.ActiveVersionId != null)
                .Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.Language,
                    Questions = d.Versions
                        .Where(v => v.Id == d.ActiveVersionId
                            && (v.SuggestionsStatus == SuggestionsStatus.Ready || v.SuggestionsStatus == SuggestionsStatus.Edited))
                        .Select(v => v.SuggestedQuestions)
                        .FirstOrDefault(),
                })
                .ToListAsync(cancellationToken);

            var all = rows
                .SelectMany(r => (r.Questions ?? []).Select(q => new Suggestion(q, r.Id, r.Title, r.Language)))
                .ToArray();
            Random.Shared.Shuffle(all);
            return all.Take(count).ToList();
        }
    }
}
