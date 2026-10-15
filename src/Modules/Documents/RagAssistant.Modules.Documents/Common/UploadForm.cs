using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace RagAssistant.Modules.Documents.Common;

/// <summary>
/// Reads an upload's multipart form by hand: the body-size limit must be set before the body is touched, and model binding
/// would read it first. CSRF is covered by the BFF's X-Requested-With check; the API only takes bearer tokens.
/// </summary>
internal static class UploadForm
{
    // Room for the metadata fields and multipart framing on top of the largest allowed file.
    private const int FormOverheadBytes = 64 * 1024;

    public static async Task<IFormCollection?> ReadAsync(HttpRequest request, int maxFileSizeBytes, CancellationToken cancellationToken)
    {
        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = maxFileSizeBytes + FormOverheadBytes;
        }

        if (!request.HasFormContentType)
        {
            return null;
        }

        try
        {
            return await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            // A malformed multipart body is reported like a missing file.
            return null;
        }
    }

    public static UploadedFile? SingleFile(IFormCollection? form) =>
        form?.Files.Count == 1 ? new UploadedFile(form.Files[0].FileName, form.Files[0].ContentType, form.Files[0].Length, form.Files[0].OpenReadStream()) : null;

    public static string? Field(IFormCollection? form, string name) =>
        form is not null && form.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
