namespace RagAssistant.Evaluation.Run;

/// <summary>
/// With <c>Evaluation:JudgeTrace=true</c>, appends every request and response body exchanged with the judge model to
/// <c>.data/evaluation/judge-trace.log</c>, to see why an evaluator could not read a score.
/// </summary>
internal sealed class JudgeTraceHandler(bool enabled) : DelegatingHandler
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
        await response.Content.LoadIntoBufferAsync(cancellationToken);
        var reply = await response.Content.ReadAsStringAsync(cancellationToken);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var file = RepositoryPaths.Resolve(".data/evaluation/judge-trace.log");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.AppendAllTextAsync(file, $"--> {request.Method} {request.RequestUri}\n{body}\n<-- {(int)response.StatusCode}\n{reply}\n\n", cancellationToken);
        }
        finally
        {
            Gate.Release();
        }

        return response;
    }
}
