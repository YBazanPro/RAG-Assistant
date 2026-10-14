using System.Net.Sockets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Options;

namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>Embeds inputs in sequential batches, retrying transient failures with exponential backoff.</summary>
internal sealed partial class PassageEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<IngestionOptions> ingestion,
    IOptions<EmbeddingOptions> embeddings,
    TimeProvider clock,
    ILogger<PassageEmbedder> logger)
{
    /// <summary>Returns one vector per input, in order. Throws <see cref="IngestionException"/> on exhausted retries or a bad response.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var batchSize = ingestion.Value.EmbeddingBatchSize;
        var vectors = new List<float[]>(inputs.Count);
        for (var offset = 0; offset < inputs.Count; offset += batchSize)
        {
            var batch = inputs.Skip(offset).Take(batchSize).ToArray();
            vectors.AddRange(await EmbedBatchAsync(batch, cancellationToken));
        }

        return vectors;
    }

    /// <summary>
    /// The model that actually produces the vectors, as the provider reports it; the configured name is only the fallback.
    /// Recorded on the version, so it cannot drift from what the Host is wired to.
    /// </summary>
    public string ModelId => generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId ?? embeddings.Value.Model;

    /// <summary>One tiny call: false while the model is unreachable or not yet pulled, whatever way the client reports it.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ingestion.Value.EmbeddingTimeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await generator.GenerateAsync(["ping"], cancellationToken: linked.Token);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A probe has one question: does the model answer? Provider clients differ in how they say "not yet", so any failure
            // other than the caller's own cancellation means "not available" and the run stays queued (spec 007, model readiness).
            return false;
        }
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(string[] batch, CancellationToken cancellationToken)
    {
        var options = ingestion.Value;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(options.EmbeddingTimeout, clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var result = await generator.GenerateAsync(batch, cancellationToken: linked.Token);
                return Validate(result, batch.Length);
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                // Filtered to transient provider failures; anything else is an unexpected error handled by the run processor (AC-17).
                if (attempt >= options.EmbeddingMaxRetries)
                {
                    LogGivingUp(logger, attempt + 1, ex.GetType().Name);
                    throw new IngestionException(
                        IngestionErrorCodes.EmbeddingUnavailable,
                        "The embedding model did not respond after several attempts.");
                }

                LogRetrying(logger, attempt + 1, ex.GetType().Name);
                await Task.Delay(options.EmbeddingRetryBaseDelay * Math.Pow(2, attempt), clock, cancellationToken);
            }
        }
    }

    private IReadOnlyList<float[]> Validate(GeneratedEmbeddings<Embedding<float>> result, int expectedCount)
    {
        var dimensions = embeddings.Value.Dimensions;
        if (result.Count != expectedCount || result.Any(e => e.Vector.Length != dimensions))
        {
            throw new IngestionException(
                IngestionErrorCodes.EmbeddingDimensionMismatch,
                $"The embedding model returned vectors that do not match the expected count ({expectedCount}) or size ({dimensions}).");
        }

        return [.. result.Select(e => e.Vector.ToArray())];
    }

    // A timeout cancels the linked token without the caller's token being cancelled; a caller cancellation must propagate.
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException or IOException or SocketException or TimeoutException => true,
        _ => false,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding batch failed on attempt {Attempt} ({ErrorType}); retrying.")]
    private static partial void LogRetrying(ILogger logger, int attempt, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Embedding batch failed on attempt {Attempt} ({ErrorType}); giving up.")]
    private static partial void LogGivingUp(ILogger logger, int attempt, string errorType);
}
