using System.Net;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RagAssistant.Integration.Tests.Documents;

/// <summary>PDFs for upload tests: real ones built with PdfPig, and cheap padded ones when only the size matters.</summary>
internal static class TestPdfs
{
    /// <summary>A one-page text-layer PDF; the text makes every call a different file.</summary>
    public static byte[] WithText(string? text = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(text ?? $"Policy text {Guid.NewGuid()}", 12, new PdfPoint(25, 700), font);
        return builder.Build();
    }

    /// <summary>Starts like a PDF and is exactly <paramref name="totalBytes"/> long; not parseable, so only for size and storage tests.</summary>
    public static byte[] Padded(int totalBytes)
    {
        var header = Encoding.ASCII.GetBytes($"%PDF-1.7\n% {Guid.NewGuid()}\n");
        var bytes = new byte[totalBytes];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    public static MultipartFormDataContent Form(
        byte[] pdf, string fileName = "report.pdf", string? title = null, string ownerName = "Finance")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(title ?? DocumentUploads.UniqueTitle()), "title");
        form.Add(new StringContent(ownerName), "ownerName");
        return form;
    }

    /// <summary>Generates a PDF-shaped body of the given size on the fly, so a test can send a huge file without holding it.</summary>
    public sealed class GeneratedPdfContent(long totalBytes, CancellationTokenSource? abortAfterFirstChunk = null) : HttpContent
    {
        private const int ChunkSize = 64 * 1024;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var chunk = new byte[ChunkSize];
            var header = Encoding.ASCII.GetBytes($"%PDF-1.7\n% {Guid.NewGuid()}\n");
            header.CopyTo(chunk, 0);

            var remaining = totalBytes;
            var first = true;
            while (remaining > 0)
            {
                var count = (int)Math.Min(ChunkSize, remaining);
                await stream.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
                remaining -= count;
                if (first)
                {
                    first = false;
                    chunk.AsSpan(0, header.Length).Clear();
                    if (abortAfterFirstChunk is not null)
                    {
                        // A client that vanishes mid-upload: cancel the request, then fail the body.
                        await abortAfterFirstChunk.CancelAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = totalBytes;
            return true;
        }
    }
}
