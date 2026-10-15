using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Storage;

namespace RagAssistant.Modules.Documents.Common;

/// <summary>What an upload request carried: the small text fields, the number of file parts and the first file.</summary>
internal sealed class UploadParts : IAsyncDisposable
{
    private readonly Dictionary<string, List<string>> _fields = new(StringComparer.Ordinal);

    public int FileCount { get; set; }

    public UploadedFile? File { get; set; }

    public string? Field(string name) => _fields.TryGetValue(name, out var values) ? values[0] : null;

    public string[] Values(string name) => _fields.TryGetValue(name, out var values) ? [.. values] : [];

    public void Add(string name, string value)
    {
        if (!_fields.TryGetValue(name, out var values))
        {
            _fields[name] = values = [];
        }

        values.Add(value);
    }

    /// <summary>Discards a staged file the handler did not commit.</summary>
    public ValueTask DisposeAsync() => File?.Staged?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// Reads an upload's multipart body part by part, without ever holding a whole PDF in memory: the file goes straight to the store's
/// staging area while it is hashed. Model binding would buffer the body, and the body-size limit must be set before it is touched.
/// CSRF is covered by the BFF's X-Requested-With check; the API only takes bearer tokens.
/// </summary>
internal static class UploadForm
{
    // Room for the metadata fields and multipart framing on top of the largest allowed file.
    private const int FormOverheadBytes = 64 * 1024;
    private const int MaxBoundaryLength = 70;
    private const int FieldBudgetBytes = 64 * 1024;
    private const int ChunkSize = 16 * 1024;

    /// <returns>The parts, or null when the body is not a well-formed multipart/form-data request (reported like a missing file).</returns>
    public static async Task<UploadParts?> ReadAsync(
        HttpRequest request, ImportOptions options, IOriginalFileStore store, CancellationToken cancellationToken)
    {
        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = (long)options.MaxPdfSizeBytes + FormOverheadBytes;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !"multipart/form-data".Equals(mediaType.MediaType.Value, StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 and <= MaxBoundaryLength } boundary)
        {
            return null;
        }

        var parts = new UploadParts();
        try
        {
            var reader = new MultipartReader(boundary, request.Body);
            var fieldBudget = FieldBudgetBytes;
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (section.GetContentDispositionHeader() is not { } disposition)
                {
                    continue;
                }

                var fileName = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : disposition.FileName.Value;
                if (fileName is not null)
                {
                    parts.FileCount++;
                    // Only the first file is kept; the rest are counted (exactly one is required) and skipped unread.
                    parts.File ??= await ReceiveFileAsync(
                        HeaderUtilities.RemoveQuotes(fileName).Value ?? string.Empty, section, options, store, cancellationToken);
                }
                else
                {
                    var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty;
                    var value = await ReadFieldAsync(section.Body, fieldBudget, cancellationToken);
                    fieldBudget -= Encoding.UTF8.GetByteCount(value);
                    parts.Add(name, value);
                }
            }
        }
        catch (Exception ex) when (IsMalformedBody(ex, request))
        {
            await parts.DisposeAsync();
            return null;
        }
        catch
        {
            await parts.DisposeAsync();
            throw;
        }

        return parts;
    }

    // The multipart reader reports a body that ends early (e.g. no parts at all) as a plain IOException. A request that is too large
    // (BadHttpRequestException is an IOException too) or a client that went away is not a malformed form and must keep propagating.
    private static bool IsMalformedBody(Exception exception, HttpRequest request) =>
        exception is InvalidDataException
        || (exception is IOException and not BadHttpRequestException && !request.HttpContext.RequestAborted.IsCancellationRequested);

    private static async Task<UploadedFile> ReceiveFileAsync(
        string clientFileName, MultipartSection section, ImportOptions options, IOriginalFileStore store, CancellationToken cancellationToken)
    {
        // The limit depends on the format, which comes from the extension, so it is known before the first byte is read.
        if (!DocumentMetadata.TryGetFormat(DocumentMetadata.SanitizeFileName(clientFileName), out var format))
        {
            return new UploadedFile(clientFileName, section.ContentType, 0, default, null, TooLarge: false);
        }

        if (format == SourceFormat.Pdf)
        {
            var staged = await store.StageAsync(section.Body, options.MaxPdfSizeBytes, cancellationToken);
            return new UploadedFile(clientFileName, section.ContentType, staged?.Length ?? 0, default, staged, TooLarge: staged is null);
        }

        var max = options.MaxFileSizeBytes;
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];
        int read;
        while ((read = await section.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return new UploadedFile(clientFileName, section.ContentType, max + 1L, default, null, TooLarge: true);
            }

            buffer.Write(chunk, 0, read);
        }

        return new UploadedFile(clientFileName, section.ContentType, buffer.Length, buffer.ToArray(), null, TooLarge: false);
    }

    private static async Task<string> ReadFieldAsync(Stream body, int budget, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > budget)
            {
                throw new InvalidDataException("The form fields are too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
