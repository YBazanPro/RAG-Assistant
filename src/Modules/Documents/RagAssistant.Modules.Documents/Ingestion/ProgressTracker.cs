namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>
/// Decides which progress counts become events (spec 010): one whenever <c>floor(done * 10 / total)</c> grows, never at the end of the
/// stage, so a stage has at most nine and a handful of batches may have none.
/// </summary>
internal sealed class ProgressTracker(int total)
{
    private int _lastStep;

    public bool ShouldReport(int done)
    {
        if (total <= 0 || done >= total)
        {
            return false;
        }

        var step = (int)((long)done * 10 / total);
        if (step <= _lastStep)
        {
            return false;
        }

        _lastStep = step;
        return true;
    }
}
