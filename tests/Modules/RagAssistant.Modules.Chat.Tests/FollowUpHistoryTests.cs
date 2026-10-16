using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Tests;

// Spec 014, AC-3: the history window the follow-up rewrite sees.
public class FollowUpHistoryTests
{
    private static Message Question(int sequence, string text) =>
        new() { Id = Guid.NewGuid(), Role = MessageRole.User, Sequence = sequence, Text = text };

    private static Message Answer(Message question, string text, AnswerStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Role = MessageRole.Assistant,
        Sequence = question.Sequence + 1,
        Text = text,
        Status = status,
        ReplyToMessageId = question.Id,
    };

    [Test]
    public async Task Build_fiveExchangesAndAWindowOfThree_keepsTheLastThreeOldestFirst()
    {
        var messages = new List<Message>();
        for (var i = 1; i <= 5; i++)
        {
            var q = Question((i * 2) - 1, $"Question {i}");
            messages.AddRange([q, Answer(q, $"Answer {i} [1].", AnswerStatus.Complete)]);
        }

        var history = FollowUpHistory.Build(messages, 3, 1000);

        await Assert.That(history.Select(e => e.Question)).IsEquivalentTo(["Question 3", "Question 4", "Question 5"]);
        await Assert.That(history[0].Answer).IsEqualTo("Answer 3.");
    }

    [Test]
    public async Task Build_refusedFailedAndCancelledAnswers_contributeOnlyTheirQuestion()
    {
        var refused = Question(1, "Refused one");
        var failed = Question(3, "Failed one");
        var cancelled = Question(5, "Cancelled one");

        var history = FollowUpHistory.Build(
            [refused, Answer(refused, "", AnswerStatus.Refused), failed, Answer(failed, "", AnswerStatus.Failed), cancelled, Answer(cancelled, "", AnswerStatus.Cancelled)],
            3,
            1000);

        await Assert.That(history.All(e => e.Answer is null)).IsTrue();
        await Assert.That(history.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Build_longAnswer_isCutToTheLimit()
    {
        var q = Question(1, "Q");

        var history = FollowUpHistory.Build([q, Answer(q, new string('a', 500), AnswerStatus.Partial)], 3, 100);

        await Assert.That(history.Single().Answer!.Length).IsEqualTo(101);
        await Assert.That(history.Single().Answer!).EndsWith("…");
    }
}
