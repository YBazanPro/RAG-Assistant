using System.Text;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Modules.Documents.Storage;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.DownloadVersionFile;

/// <summary>The original of a version: the stored PDF, or the normalized text of a text format (spec 010).</summary>
internal static partial class DownloadVersionFile
{
    public const string PdfContentType = "application/pdf";
    public const string TextContentType = "text/plain; charset=utf-8";

    public static readonly Error FileMissing = Error.NotFound("documents.file_missing", "The original file is no longer available.");

    public sealed record Query(Guid DocumentId, int Number, string ViewerSub) : IRequest<Result<Response>>;

    /// <summary>Exactly one of <paramref name="Stream"/> and <paramref name="Text"/> is set. The caller owns the stream.</summary>
    public sealed record Response(string FileName, string ContentType, bool Inline, Stream? Stream, byte[]? Text);

    internal sealed partial class Handler(DocumentsDbContext db, IOriginalFileStore store, ILogger<Handler> logger)
        : IRequestHandler<Query, Result<Response>>
    {
        public async ValueTask<Result<Response>> Handle(Query query, CancellationToken cancellationToken)
        {
            if (!await db.Documents.AsNoTracking().ReadableBy(query.ViewerSub).AnyAsync(d => d.Id == query.DocumentId, cancellationToken))
            {
                return DocumentErrors.NotFound;
            }

            var version = await db.Versions.AsNoTracking()
                .Where(v => v.DocumentId == query.DocumentId && v.Number == query.Number)
                .Select(v => new { v.Id, v.SourceFormat, v.OriginalFileName, v.Content, v.StorageKey })
                .FirstOrDefaultAsync(cancellationToken);
            if (version is null)
            {
                return DocumentErrors.VersionNotFound;
            }

            if (version.SourceFormat != SourceFormat.Pdf)
            {
                return new Response(version.OriginalFileName, TextContentType, Inline: false, Stream: null, Encoding.UTF8.GetBytes(version.Content ?? string.Empty));
            }

            var stream = version.StorageKey is null ? null : store.OpenRead(version.StorageKey);
            if (stream is null)
            {
                LogMissing(logger, query.DocumentId, version.Id);
                return FileMissing;
            }

            return new Response(version.OriginalFileName, PdfContentType, Inline: true, stream, Text: null);
        }

        [LoggerMessage(Level = LogLevel.Error, Message = "The original file of document {DocumentId} version {VersionId} is missing from the store")]
        private static partial void LogMissing(ILogger logger, Guid documentId, Guid versionId);
    }
}
