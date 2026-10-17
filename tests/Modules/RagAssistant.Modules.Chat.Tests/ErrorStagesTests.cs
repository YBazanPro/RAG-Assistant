using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Tests;

// Spec 017, Metrics: one mapping from error code to pipeline stage.
internal sealed class ErrorStagesTests
{
    [Test]
    [Arguments("retrieval.embedding_unavailable", "retrieval")]
    [Arguments("chat.model_unavailable", "generation")]
    [Arguments("chat.model_timeout", "generation")]
    [Arguments("chat.internal", "persistence")]
    [Arguments("chat.interrupted", "persistence")]
    [Arguments("something.new", "persistence")]
    public async Task StageOf_mapsCodesToStages(string code, string stage)
    {
        await Assert.That(ErrorStages.StageOf(code)).IsEqualTo(stage);
    }

    [Test]
    [Arguments(RewriteFallback.Timeout, "rewrite.timeout")]
    [Arguments(RewriteFallback.ModelError, "rewrite.model_error")]
    [Arguments(RewriteFallback.Invalid, "rewrite.invalid")]
    public async Task Of_aRewriteFallbackIsARewriteError(RewriteFallback fallback, string code)
    {
        var trace = new RequestTrace { OwnerSub = "u", QuestionText = "q", RewriteFallbackReason = fallback, FinalStatus = AnswerStatus.Complete };

        var errors = ErrorStages.Of(trace).ToList();

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).IsEqualTo(("rewrite", code));
    }

    [Test]
    public async Task Of_aFallbackAndAFinalErrorCountBoth()
    {
        var trace = new RequestTrace
        {
            OwnerSub = "u",
            QuestionText = "q",
            RewriteFallbackReason = RewriteFallback.Timeout,
            ErrorCode = "chat.model_timeout",
            FinalStatus = AnswerStatus.Failed,
        };

        var errors = ErrorStages.Of(trace).Select(e => e.Stage).ToList();

        await Assert.That(errors.SequenceEqual(["rewrite", "generation"])).IsTrue();
    }

    [Test]
    public async Task Of_aCleanRequestHasNoErrors()
    {
        var trace = new RequestTrace { OwnerSub = "u", QuestionText = "q", FinalStatus = AnswerStatus.Complete };

        await Assert.That(ErrorStages.Of(trace).Any()).IsFalse();
    }
}
