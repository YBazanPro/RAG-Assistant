using System.Buffers;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Common;

internal sealed record UploadedFile(string FileName, string? ContentType, long Length, Stream Content);

/// <summary>A file that passed the import rules: normalized text, its hash and the facts stored on the version.</summary>
internal sealed record AcceptedContent(
    string Content, string Hash, SourceFormat Format, string FileName, string? ContentType, int SizeBytes);

/// <summary>The file rules shared by import and "upload a new version" (spec 006 / spec 009).</summary>
internal static class UploadIntake
{
    private const int MaxFileNameLength = 255;
    private const int MaxContentTypeLength = 200;

    public static async Task<Result<AcceptedContent>> ReadAsync(
        int fileCount, UploadedFile? file, int maxBytes, CancellationToken cancellationToken)
    {
        if (fileCount != 1 || file is null)
        {
            return Reject("documents.file_required", "Exactly one file is required.");
        }

        var fileName = DocumentMetadata.SanitizeFileName(file.FileName);
        if (!DocumentMetadata.TryGetFormat(fileName, out var format))
        {
            return Reject("documents.unsupported_extension", "Only .md, .markdown and .txt files are supported.");
        }

        if (file.Length > maxBytes)
        {
            return Reject("documents.file_too_large", $"The file must be at most {maxBytes} bytes.");
        }

        // One byte more than the limit tells an oversize stream apart from one that is exactly at the limit.
        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);
        string decoded;
        int read;
        try
        {
            read = await file.Content.ReadAtLeastAsync(buffer.AsMemory(0, maxBytes + 1), maxBytes + 1, throwOnEndOfStream: false, cancellationToken);
            if (read > maxBytes)
            {
                return Reject("documents.file_too_large", $"The file must be at most {maxBytes} bytes.");
            }

            if (!DocumentText.TryDecode(buffer.AsSpan(0, read), out decoded!))
            {
                return Reject("documents.invalid_utf8", "The file must be valid UTF-8 text.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var content = DocumentText.Normalize(decoded);
        if (string.IsNullOrWhiteSpace(content))
        {
            return Reject("documents.empty_content", "The file has no content.");
        }

        return new AcceptedContent(
            content,
            DocumentText.Hash(content),
            format,
            Truncate(fileName, MaxFileNameLength),
            file.ContentType is null ? null : Truncate(file.ContentType, MaxContentTypeLength),
            read);
    }

    private static Result<AcceptedContent> Reject(string code, string message) =>
        Result.Failure<AcceptedContent>(
            Error.Validation(code, message),
            new Dictionary<string, string[]> { ["file"] = [message] });

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
