using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagAssistant.Evaluation.Api;
using RagAssistant.Evaluation.Corpus;

namespace RagAssistant.Evaluation.Commands;

/// <summary>
/// <c>seed</c> (spec 018): brings the sample corpus into the running app, idempotently. A manifest document is found by its title
/// among the documents tagged <c>sample</c>; a missing one is imported, an archived one restored, a changed last file uploaded as a
/// new version and different metadata saved. Unchanged content is recognised by the API's duplicate guard. Nothing is deleted.
/// </summary>
internal sealed class SeedCommand(RagAssistantApi api, IOptions<EvaluationOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private EvaluationOptions Settings => options.Value;

    public async Task<SeedSummary> RunAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var corpusPath = RepositoryPaths.Resolve(Settings.CorpusPath);
        var manifest = CorpusManifest.Load(corpusPath);
        var owner = Settings.OwnerUser;
        var existing = await api.FindByTagAsync(owner, Settings.SampleTag, cancellationToken);
        var summary = new SeedSummary();

        foreach (var document in manifest.Documents)
        {
            var files = document.Versions.Select((path, i) => Load(corpusPath, document, path, i + 1)).ToList();
            var matches = existing.Where(d => string.Equals(d.Title, document.Title, StringComparison.Ordinal)).ToList();
            if (matches.Count > 1)
            {
                throw new HarnessException($"Several documents tagged '{Settings.SampleTag}' are titled '{document.Title}'; archive the extra ones.");
            }

            var outcome = matches.Count == 0
                ? await ImportAsync(document, files, cancellationToken)
                : await UpdateAsync(document, matches[0], files[^1], cancellationToken);
            summary.Add(outcome);
            await output.WriteLineAsync($"{outcome,-10} {document.Key}");
        }

        await output.WriteLineAsync(
            $"Seeded {manifest.Documents.Count} documents: {summary.Imported} imported, {summary.Updated} updated, {summary.Unchanged} unchanged.");
        return summary;
    }

    private async Task<SeedOutcome> ImportAsync(ManifestDocument document, List<SourceFile> files, CancellationToken cancellationToken)
    {
        var accepted = (await api.ImportAsync(Settings.OwnerUser, files[0], Fields(document), cancellationToken)).Require($"import '{document.Key}'");
        await WaitForIndexingAsync(document, accepted, cancellationToken);
        foreach (var file in files.Skip(1))
        {
            var next = (await api.UploadVersionAsync(Settings.OwnerUser, accepted.DocumentId, file, cancellationToken)).Require($"upload a version of '{document.Key}'");
            await WaitForIndexingAsync(document, next, cancellationToken);
        }

        return SeedOutcome.Imported;
    }

    private async Task<SeedOutcome> UpdateAsync(ManifestDocument document, DocumentSummary found, SourceFile last, CancellationToken cancellationToken)
    {
        var changed = false;
        if (found.Status == "archived")
        {
            (await api.RestoreAsync(Settings.OwnerUser, found.Id, cancellationToken)).Require($"restore '{document.Key}'");
            changed = true;
        }

        var upload = await api.UploadVersionAsync(Settings.OwnerUser, found.Id, last, cancellationToken);
        if (upload.IsSuccess)
        {
            await WaitForIndexingAsync(document, upload.Value!, cancellationToken);
            changed = true;
        }
        else if (!IsSameContent(upload, found.Id))
        {
            upload.Require($"upload a version of '{document.Key}'");
        }

        var current = (await api.GetAsync(Settings.OwnerUser, found.Id, cancellationToken)).Require($"read '{document.Key}'");
        var wanted = Metadata(document);
        if (!Matches(current, wanted))
        {
            (await api.UpdateAsync(Settings.OwnerUser, found.Id, current.Revision, wanted, cancellationToken)).Require($"update the metadata of '{document.Key}'");
            changed = true;
        }

        return changed ? SeedOutcome.Updated : SeedOutcome.Unchanged;
    }

    /// <summary>The API refuses a version identical to the latest one (spec 009); for the same document that means "unchanged".</summary>
    private static bool IsSameContent(ApiResponse<Accepted> response, Guid documentId) =>
        response.Status == HttpStatusCode.Conflict
        && response.Code == "documents.duplicate_content"
        && response.Problem is { } problem
        && problem.TryGetProperty("existingDocumentId", out var existing)
        && existing.ValueKind == JsonValueKind.String
        && existing.GetGuid() == documentId;

    private async Task WaitForIndexingAsync(ManifestDocument document, Accepted accepted, CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + Settings.IndexingTimeout;
        while (true)
        {
            var current = (await api.GetAsync(Settings.OwnerUser, accepted.DocumentId, cancellationToken)).Require($"read '{document.Key}'");
            if (current.LatestRun is { } run && run.Id == accepted.IngestionRunId)
            {
                switch (run.Status)
                {
                    case "succeeded":
                        return;
                    case "failed":
                        throw new HarnessException($"Ingestion of '{document.Key}' failed at {run.Stage}: {run.ErrorCode} {run.ErrorMessage}".TrimEnd());
                }
            }

            if (clock.GetUtcNow() > deadline)
            {
                throw new HarnessException($"'{document.Key}' was not indexed within {Settings.IndexingTimeout}. Is the embedding model pulled?");
            }

            await Task.Delay(PollInterval, clock, cancellationToken);
        }
    }

    private static SourceFile Load(string corpusPath, ManifestDocument document, string relativePath, int number)
    {
        var path = Path.Combine(corpusPath, relativePath);
        if (document.IsPdf)
        {
            return new SourceFile($"{document.Key}-v{number}.pdf", "application/pdf", PdfRenderer.Render(File.ReadAllText(path, Encoding.UTF8)));
        }

        var contentType = Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "text/markdown";
        return new SourceFile(Path.GetFileName(path), contentType, File.ReadAllBytes(path));
    }

    private IEnumerable<KeyValuePair<string, string>> Fields(ManifestDocument document)
    {
        var metadata = Metadata(document);
        yield return new("title", metadata.Title);
        yield return new("ownerName", metadata.OwnerName);
        yield return new("language", metadata.Language);
        yield return new("visibility", metadata.Visibility!);
        foreach (var tag in metadata.Tags)
        {
            yield return new("tags", tag);
        }

        if (metadata.EffectiveDate is { } date)
        {
            yield return new("effectiveDate", date);
        }
    }

    private MetadataUpdate Metadata(ManifestDocument document) => new(
        document.Title,
        document.OwnerName,
        document.Language,
        [.. document.Tags.Append(Settings.SampleTag).Select(t => t.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        document.EffectiveDate,
        null,
        document.Visibility);

    private static bool Matches(DocumentDetail current, MetadataUpdate wanted)
    {
        var language = wanted.Language == "detect"
            ? current.LanguageSource == "detect"
            : current.LanguageSource == "owner" && current.Language == wanted.Language;
        return language
            && current.Title == wanted.Title
            && current.OwnerName == wanted.OwnerName
            && current.Visibility == wanted.Visibility
            && current.Tags.Order(StringComparer.Ordinal).SequenceEqual(wanted.Tags)
            && current.EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == wanted.EffectiveDate;
    }
}

internal enum SeedOutcome
{
    Imported,
    Updated,
    Unchanged,
}

internal sealed class SeedSummary
{
    public int Imported { get; private set; }

    public int Updated { get; private set; }

    public int Unchanged { get; private set; }

    public void Add(SeedOutcome outcome)
    {
        switch (outcome)
        {
            case SeedOutcome.Imported:
                Imported++;
                break;
            case SeedOutcome.Updated:
                Updated++;
                break;
            default:
                Unchanged++;
                break;
        }
    }
}
