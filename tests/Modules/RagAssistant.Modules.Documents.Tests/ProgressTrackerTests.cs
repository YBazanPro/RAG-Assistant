using RagAssistant.Modules.Documents.Ingestion;

namespace RagAssistant.Modules.Documents.Tests;

// Spec 010 (progress events): at most nine per stage, strictly increasing, never at the end.
internal sealed class ProgressTrackerTests
{
    private static List<int> Reported(int total, int step)
    {
        var tracker = new ProgressTracker(total);
        var reported = new List<int>();
        for (var done = step; done <= total + step; done += step)
        {
            var clamped = Math.Min(done, total);
            if (tracker.ShouldReport(clamped))
            {
                reported.Add(clamped);
            }

            if (clamped == total)
            {
                break;
            }
        }

        return reported;
    }

    [Test]
    public async Task ShouldReport_threeHundredPassagesInBatchesOf16_reportsAtMostNineIncreasingCountsBelowTheTotal()
    {
        var reported = Reported(300, 16);

        await Assert.That(reported.Count).IsGreaterThan(0);
        await Assert.That(reported.Count).IsLessThanOrEqualTo(9);
        await Assert.That(reported.OrderBy(x => x).ToArray()).IsEquivalentTo(reported.ToArray());
        await Assert.That(reported.Distinct().Count()).IsEqualTo(reported.Count);
        await Assert.That(reported.All(x => x < 300)).IsTrue();
    }

    [Test]
    public async Task ShouldReport_threeBatches_reportsTwiceAtMost()
    {
        var reported = Reported(48, 16);

        await Assert.That(reported).IsEquivalentTo([16, 32]);
    }

    [Test]
    public async Task ShouldReport_fiftyPagesOneAtATime_reportsAtMostNine()
    {
        var reported = Reported(50, 1);

        await Assert.That(reported.Count).IsEqualTo(9);
        await Assert.That(reported).IsEquivalentTo([5, 10, 15, 20, 25, 30, 35, 40, 45]);
    }

    [Test]
    public async Task ShouldReport_aSinglePassageOrPage_neverReports()
    {
        var tracker = new ProgressTracker(1);

        await Assert.That(tracker.ShouldReport(1)).IsFalse();
    }

    [Test]
    public async Task ShouldReport_emptyTotal_neverReports()
    {
        var tracker = new ProgressTracker(0);

        await Assert.That(tracker.ShouldReport(0)).IsFalse();
    }

    [Test]
    public async Task ShouldReport_sameStepTwice_reportsOnce()
    {
        var tracker = new ProgressTracker(100);

        await Assert.That(tracker.ShouldReport(10)).IsTrue();
        await Assert.That(tracker.ShouldReport(11)).IsFalse();
        await Assert.That(tracker.ShouldReport(19)).IsFalse();
        await Assert.That(tracker.ShouldReport(20)).IsTrue();
    }
}
