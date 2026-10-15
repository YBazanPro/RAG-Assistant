using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Documents;

// Spec 008, AC-27 and AC-28 (the stream logic; the component is covered in DocumentDetailTests).
public class IngestionWatcherTests
{
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, timeout.Token);
        }
    }

    [Test]
    public async Task Watch_stagesInOrder_ignoresHeartbeatsAndFinishesOnTerminalEvent()
    {
        var source = new FakeEventSource().EnqueueEvents(
            Samples.Stage("queued", "completed", "1"),
            Samples.Heartbeat(),
            Samples.Stage("extracting", "started", "2"),
            Samples.Stage("embedding", "started", "3"),
            Samples.Stage("completed", "completed", "4"));
        var watcher = new IngestionWatcher(source, new ManualTimeProvider());
        var stages = new List<string>();

        var outcome = await watcher.WatchAsync(Samples.DocId, e => { stages.Add(e.Stage); return Task.CompletedTask; }, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(WatchOutcome.Finished);
        await Assert.That(stages.SequenceEqual(["queued", "extracting", "embedding", "completed"])).IsTrue();
        await Assert.That(source.Opened).IsEqualTo(1);
    }

    [Test]
    public async Task Watch_failedEvent_isTerminal()
    {
        var source = new FakeEventSource().EnqueueEvents(Samples.Stage("embedding", "failed", "9", "boom"));
        var watcher = new IngestionWatcher(source, new ManualTimeProvider());

        var outcome = await watcher.WatchAsync(Samples.DocId, _ => Task.CompletedTask, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(WatchOutcome.Finished);
    }

    [Test]
    public async Task Watch_droppedStream_reconnectsWithLastEventIdAfter1s2sAnd4s_thenReportsLost()
    {
        var clock = new ManualTimeProvider();
        var source = new FakeEventSource()
            .EnqueueEvents(Samples.Stage("extracting", "started", "7"))
            .EnqueueFailure(new HttpRequestException("down"))
            .EnqueueFailure(new HttpRequestException("down"))
            .EnqueueFailure(new HttpRequestException("down"));
        var watcher = new IngestionWatcher(source, clock);

        var run = watcher.WatchAsync(Samples.DocId, _ => Task.CompletedTask, CancellationToken.None);

        await WaitForAsync(() => source.Opened == 1 && clock.ActiveTimers == 1);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(source.Opened).IsEqualTo(1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForAsync(() => source.Opened == 2 && clock.ActiveTimers == 1);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(source.Opened).IsEqualTo(2);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => source.Opened == 3 && clock.ActiveTimers == 1);

        clock.Advance(TimeSpan.FromSeconds(4));
        var outcome = await run;

        await Assert.That(outcome).IsEqualTo(WatchOutcome.Lost);
        await Assert.That(source.Opened).IsEqualTo(4);
        await Assert.That(source.LastEventIds[1]).IsEqualTo("7");
        await Assert.That(source.LastEventIds[3]).IsEqualTo("7");
    }

    [Test]
    public async Task Watch_unauthorized_reportsSessionExpiredWithoutRetrying()
    {
        var source = new FakeEventSource().EnqueueFailure(new IngestionStreamException(401));
        var watcher = new IngestionWatcher(source, new ManualTimeProvider());

        var outcome = await watcher.WatchAsync(Samples.DocId, _ => Task.CompletedTask, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(WatchOutcome.SessionExpired);
        await Assert.That(source.Opened).IsEqualTo(1);
    }

    [Test]
    public async Task Watch_cancelled_closesTheStream()
    {
        var source = new FakeEventSource().EnqueueHang();
        var watcher = new IngestionWatcher(source, new ManualTimeProvider());
        using var cts = new CancellationTokenSource();

        var run = watcher.WatchAsync(Samples.DocId, _ => Task.CompletedTask, cts.Token);
        await WaitForAsync(() => source.Opened == 1);
        await cts.CancelAsync();

        await Assert.That(async () => await run).Throws<OperationCanceledException>();
        await WaitForAsync(() => source.Closed == 1);
    }
}
