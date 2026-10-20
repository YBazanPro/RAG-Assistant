using System.Text.Json;

namespace RagAssistant.Evaluation.Corpus;

/// <summary>
/// <c>samples/corpus/manifest.json</c> (spec 018): one entry per sample document, its metadata and its version files, oldest first.
/// A <c>pdf</c> document's version files are Markdown sources that <see cref="PdfRenderer"/> turns into PDFs at seed time, so no
/// PDF is committed.
/// </summary>
internal sealed record CorpusManifest(string? Description, IReadOnlyList<ManifestDocument> Documents)
{
    public static CorpusManifest Load(string corpusPath)
    {
        var path = Path.Combine(corpusPath, "manifest.json");
        if (!File.Exists(path))
        {
            throw new HarnessException($"No manifest at {path}.");
        }

        var manifest = JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new HarnessException($"{path} is empty.");
        manifest.Validate(corpusPath);
        return manifest;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private void Validate(string corpusPath)
    {
        var duplicates = Documents.GroupBy(d => d.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new HarnessException($"Duplicate manifest keys: {string.Join(", ", duplicates)}.");
        }

        foreach (var document in Documents)
        {
            if (document.Versions.Count == 0)
            {
                throw new HarnessException($"Manifest document '{document.Key}' has no version files.");
            }

            foreach (var version in document.Versions)
            {
                if (!File.Exists(Path.Combine(corpusPath, version)))
                {
                    throw new HarnessException($"Manifest document '{document.Key}' names a missing file: {version}.");
                }
            }
        }
    }
}

/// <param name="Language"><c>detect</c> or a two-letter code, sent as is.</param>
/// <param name="Format"><c>pdf</c> to render the version files as PDFs; otherwise the file is sent as it is.</param>
internal sealed record ManifestDocument(
    string Key,
    string Title,
    string OwnerName,
    string Language,
    IReadOnlyList<string> Tags,
    string? EffectiveDate,
    IReadOnlyList<string> Versions,
    string? Format = null,
    string Visibility = "shared")
{
    public bool IsPdf => string.Equals(Format, "pdf", StringComparison.OrdinalIgnoreCase);
}
