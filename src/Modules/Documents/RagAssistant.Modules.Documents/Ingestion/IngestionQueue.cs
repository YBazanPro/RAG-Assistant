using System.Threading.Channels;

namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>Wake-up channel of run ids. Only a hint: the persisted run table is the source of truth, and claims are conditional updates.</summary>
internal sealed class IngestionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid runId) => _channel.Writer.TryWrite(runId);
}
