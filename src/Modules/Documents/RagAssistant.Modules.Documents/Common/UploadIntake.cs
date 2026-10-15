using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Pdf;
using RagAssistant.Modules.Documents.Storage;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Common;

/// <summary>
/// The first file part of an upload, as <see cref="UploadForm"/> received it: text formats are held in memory (they are small),
/// a PDF is already on disk in the store's staging area. A part over its size limit is flagged, not stored.
/// </summary>
internal sealed record UploadedFile(string FileName, string? ContentType, long Length, ReadOnlyMemory<byte> Text, StagedFile? Staged, bool TooLarge);

/// <summary>A file that passed the import rules: normalized text or a staged PDF, its hash and the facts stored on the version.</summary>
internal sealed record AcceptedContent(
    string? Content, string Hash, SourceFormat Format, string FileName, string? ContentType, int SizeBytes, StagedFile? Staged);

/// <summary>The file rules shared by import and "upload a new version" (spec 006 / spec 009 / spec 010).</summary>
internal static class UploadIntake
{
    private const int MaxFileNameLength = 255;
    private const int MaxContentTypeLength = 200;
    private const int PdfHeaderWindow = 1024;
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    public static async Task<Result<AcceptedContent>> ReadAsync(
        int fileCount, UploadedFile? file, ImportOptions options, IPdfProbe probe, CancellationToken cancellationToken)
    {
        if (fileCount != 1 || file is null)
        {
            return Reject("documents.file_required", "Exactly one file is required.");
        }

        var fileName = DocumentMetadata.SanitizeFileName(file.FileName);
        if (!DocumentMetadata.TryGetFormat(fileName, out var format))
        {
            return Reject("documents.unsupported_extension", "Only .md, .markdown, .txt and .pdf files are supported.");
        }

        var maxBytes = DocumentMetadata.MaxBytes(format, options);
        if (file.TooLarge || file.Length > maxBytes)
        {
            return Reject("documents.file_too_large", $"The file must be at most {maxBytes} bytes.");
        }

        var contentType = file.ContentType is null ? null : Truncate(file.ContentType, MaxContentTypeLength);
        return format == SourceFormat.Pdf
            ? await AcceptPdfAsync(file, Truncate(fileName, MaxFileNameLength), contentType, probe, cancellationToken)
            : AcceptText(file, format, Truncate(fileName, MaxFileNameLength), contentType);
    }

    private static async Task<Result<AcceptedContent>> AcceptPdfAsync(
        UploadedFile file, string fileName, string? contentType, IPdfProbe probe, CancellationToken cancellationToken)
    {
        if (file.Staged is not { } staged)
        {
            return Reject("documents.file_required", "Exactly one file is required.");
        }

        var header = new byte[PdfHeaderWindow];
        int read;
        await using (var stream = staged.OpenRead())
        {
            read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        }

        if (header.AsSpan(0, read).IndexOf(PdfMagic) < 0)
        {
            return Reject("documents.invalid_pdf", "The file is not a PDF document.");
        }

        // Scanned and garbled PDFs are turned away now, before anything is stored (spec 010, OCR detection).
        PdfProbeResult verdict;
        await using (var stream = staged.OpenRead())
        {
            verdict = await probe.ProbeAsync(stream, cancellationToken);
        }

        switch (verdict.Kind)
        {
            case PdfKind.ScannedNeedsOcr:
                return Reject("documents.requires_ocr", "This PDF has no text layer (it looks scanned). OCR is not supported yet.");
            case PdfKind.UnusableText:
                return Reject("documents.text_layer_unusable", "The text layer of this PDF is not readable text.");
        }

        return new AcceptedContent(null, staged.Sha256, SourceFormat.Pdf, fileName, contentType, (int)staged.Length, staged);
    }

    private static Result<AcceptedContent> AcceptText(UploadedFile file, SourceFormat format, string fileName, string? contentType)
    {
        if (!DocumentText.TryDecode(file.Text.Span, out var decoded))
        {
            return Reject("documents.invalid_utf8", "The file must be valid UTF-8 text.");
        }

        var content = DocumentText.Normalize(decoded);
        return string.IsNullOrWhiteSpace(content)
            ? Reject("documents.empty_content", "The file has no content.")
            : new AcceptedContent(content, DocumentText.Hash(content), format, fileName, contentType, file.Text.Length, null);
    }

    private static Result<AcceptedContent> Reject(string code, string message) =>
        Result.Failure<AcceptedContent>(
            Error.Validation(code, message),
            new Dictionary<string, string[]> { ["file"] = [message] });

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
