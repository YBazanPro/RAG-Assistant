using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RagAssistant.Web.Tests.Fixtures;

namespace RagAssistant.Web.Tests.Documents;

// Spec 010, AC-27: uploads through the real BFF (on Kestrel, so body limits apply) to a real stub API.
[ClassDataSource<WebAppFactory>(Shared = SharedType.PerTestSession)]
[NotInParallel]
public class UploadBffTests(WebAppFactory factory)
{
    private const int OneMiB = 1024 * 1024;

    /// <summary>A host with real Kestrel limits, signed in as the persona; the in-process test server has no body-size limit to test.</summary>
    private static async Task<(WebApplicationFactory<Program> App, HttpClient Client)> KestrelAsync(
        WebAppFactory factory, Persona persona, params (string Key, string Value)[] settings)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
        var port = FreePort();
        app.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        app.StartServer();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        });
        client.Timeout = TimeSpan.FromMinutes(2);
        var signIn = await client.GetAsync(new Uri($"/test/sign-in?persona={persona}&lifetime=3600", UriKind.Relative));
        signIn.EnsureSuccessStatusCode();
        return (app, client);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Multipart body of <paramref name="size"/> pseudo-random bytes, produced as it is sent; the hash is known up front.</summary>
    private sealed class GeneratedFileContent(long size, Task? stubSawBytes = null) : HttpContent
    {
        public string Sha256 { get; } = Hash(size);

        private static byte[] Chunk()
        {
            var chunk = new byte[64 * 1024];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = (byte)((i * 31) + 7);
            }

            return chunk;
        }

        private static string Hash(long size)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunk = Chunk();
            for (var remaining = size; remaining > 0; remaining -= chunk.Length)
            {
                hash.AppendData(chunk, 0, (int)Math.Min(chunk.Length, remaining));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var chunk = Chunk();
            var first = true;
            for (var remaining = size; remaining > 0; remaining -= chunk.Length)
            {
                await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), cancellationToken);
                if (first && stubSawBytes is not null)
                {
                    // Not one more byte until the API has seen the first ones: only possible if the BFF forwards while receiving.
                    await stream.FlushAsync(cancellationToken);
                    await stubSawBytes.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }

                first = false;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = size;
            return true;
        }
    }

    /// <summary>The file part is raw bytes; hashing the whole multipart body at the stub would also hash framing, so send it as the body.</summary>
    private static HttpRequestMessage Upload(string path, HttpContent content, string? testId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = content };
        request.Headers.Add("X-Requested-With", "ragassistant");
        if (testId is not null)
        {
            request.Headers.Add("X-Test-Id", testId);
        }

        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    /// <summary>
    /// A body over the limit is refused with 413, but the server may close the connection while the client is still sending,
    /// in which case the client sees the reset instead of the response. Either way the body was not accepted.
    /// </summary>
    private static async Task<bool> IsRefusedAsTooLargeAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            try
            {
                using var response = await client.SendAsync(request);
                return response.StatusCode == HttpStatusCode.RequestEntityTooLarge;
            }
            catch (HttpRequestException)
            {
                return true;
            }
        }
    }

    [Test]
    [Arguments("/api/documents")]
    [Arguments("/api/documents/00000000-0000-0000-0000-0000000000a1/versions")]
    public async Task Upload_fortyMiB_reachesTheApiIntactAndIncrementally(string path)
    {
        var (app, client) = await KestrelAsync(factory, Persona.ContentOwner);
        await using var _ = app;
        using var __ = client;
        var testId = Guid.NewGuid().ToString("N");
        using var content = new GeneratedFileContent(40L * OneMiB, factory.Api.FirstUploadBytes(testId));

        using var response = await client.SendAsync(Upload(path, content, testId));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("length").GetInt64()).IsEqualTo(40L * OneMiB);
        await Assert.That(body.GetProperty("sha256").GetString()).IsEqualTo(content.Sha256);
        await Assert.That(factory.Api.AuthorizationFor(path)).IsEqualTo("Bearer access-token-ContentOwner");
    }

    [Test]
    public async Task Upload_otherRoutes_keepTheDefaultBodyLimit()
    {
        var (app, client) = await KestrelAsync(factory, Persona.ContentOwner);
        await using var _ = app;
        using var __ = client;
        using var content = new GeneratedFileContent(40L * OneMiB);

        await Assert.That(await IsRefusedAsTooLargeAsync(client, Upload("/api/echo/big", content))).IsTrue();
    }

    [Test]
    public async Task Upload_aBodyOverTheUploadLimit_isRefusedWith413()
    {
        var (app, client) = await KestrelAsync(factory, Persona.ContentOwner, ("Bff:UploadMaxBodyBytes", (5 * OneMiB).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await using var _ = app;
        using var __ = client;
        using var content = new GeneratedFileContent(8L * OneMiB);

        await Assert.That(await IsRefusedAsTooLargeAsync(client, Upload("/api/documents", content))).IsTrue();
    }

    [Test]
    public async Task Upload_aBodyJustOverTheLimit_getsAProblemDetails413()
    {
        var (app, client) = await KestrelAsync(factory, Persona.ContentOwner, ("Bff:UploadMaxBodyBytes", "65536"));
        await using var _ = app;
        using var __ = client;
        // Small enough to sit in the socket buffers: the client has finished sending when the server answers and closes.
        using var content = new GeneratedFileContent(100 * 1024);

        using var response = await client.SendAsync(Upload("/api/documents", content));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).IsEqualTo("bff.request_too_large");
    }

    [Test]
    public async Task Upload_withoutTheCsrfHeader_isRefused()
    {
        var client = await factory.CreateSignedInAsync(Persona.ContentOwner);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/documents", UriKind.Relative)) { Content = new ByteArrayContent([1, 2, 3]) };

        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Upload_withoutASession_isRefusedBeforeTheBodyIsForwarded()
    {
        var client = factory.CreateBrowser();
        using var request = Upload("/api/documents", new ByteArrayContent([1, 2, 3]));

        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Reads_ofTheUploadPaths_stayOnTheCatchAllRoute()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        using var response = await client.GetAsync(new Uri("/api/documents?page=1", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task File_rangeRequest_passesThroughAs206()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/documents/{Guid.NewGuid()}/versions/1/file", UriKind.Relative))
        {
            Headers = { Range = new RangeHeaderValue(0, 99) },
        };

        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.PartialContent);
        await Assert.That(await response.Content.ReadAsByteArrayAsync()).IsEquivalentTo(StubApi.SampleFile.Take(100).ToArray());
        await Assert.That(response.Content.Headers.ContentRange?.ToString()).IsEqualTo($"bytes 0-99/{StubApi.SampleFile.Length}");
    }

    [Test]
    public async Task File_download_carriesASingleNosniffHeader()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        using var response = await client.GetAsync(new Uri($"/api/documents/{Guid.NewGuid()}/versions/1/file", UriKind.Relative));

        await Assert.That(response.Headers.GetValues("X-Content-Type-Options").ToArray()).IsEquivalentTo(["nosniff"]);
    }

    [Test]
    public async Task File_download_isStreamedWithoutBuffering()
    {
        var id = Guid.NewGuid();
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await client.GetAsync(
            new Uri($"/api/documents/{id}/versions/2/file", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var head = new byte[1024];
        await stream.ReadExactlyAsync(head, cts.Token);

        // The API holds the rest back until released, so the first kilobyte arriving now proves nothing is buffered.
        await Assert.That(head).IsEquivalentTo(StubApi.SampleFile.Take(1024).ToArray());
        factory.Api.ReleaseIngestion(id);
        var rest = new MemoryStream();
        await stream.CopyToAsync(rest, cts.Token);
        await Assert.That(rest.Length).IsEqualTo(StubApi.SampleFile.Length - 1024);
    }
}
