using RagAssistant.Web.Client.Chat;

namespace RagAssistant.Web.Tests.Chat;

// Spec 013: the client reads the spec 012 answer stream and ignores what it does not know.
public class AnswerEventParserTests
{
    [Test]
    public async Task Parse_questionAccepted_readsTheIds()
    {
        var id = Guid.NewGuid();
        var parsed = AnswerEventParser.Parse(
            "question.accepted", $$"""{"conversationId":"{{id}}","userMessageId":"{{id}}","assistantMessageId":"{{id}}"}""");

        await Assert.That(parsed).IsEqualTo(new QuestionAccepted(id, id, id));
    }

    [Test]
    public async Task Parse_retrievalCompleted_readsTheSources()
    {
        var parsed = AnswerEventParser.Parse(
            "retrieval.completed",
            $$"""{"outcome":"sufficient","sources":[{"marker":1,"passageId":"{{Guid.Empty}}","documentId":"{{Guid.Empty}}","documentTitle":"Policy","versionNumber":3,"sectionPath":["A","B"],"pageStart":4,"pageEnd":null}]}""");

        var completed = (RetrievalCompleted)parsed!;
        await Assert.That(completed.Sources.Count).IsEqualTo(1);
        await Assert.That(completed.Sources[0].SectionPath).IsEquivalentTo(["A", "B"]);
        await Assert.That(completed.Sources[0].PageStart).IsEqualTo(4);
    }

    [Test]
    public async Task Parse_answerDelta_readsTheText()
    {
        await Assert.That(AnswerEventParser.Parse("answer.delta", """{"text":"Head [1"}""")).IsEqualTo(new AnswerDelta("Head [1"));
    }

    [Test]
    public async Task Parse_error_readsRetryable()
    {
        var parsed = AnswerEventParser.Parse("error", """{"code":"chat.model_timeout","message":"Too slow","retryable":true}""");

        await Assert.That(parsed).IsEqualTo(new AnswerError("chat.model_timeout", "Too slow", true));
    }

    [Test]
    [Arguments("heartbeat", "")]
    [Arguments("retrieval.unknown", "{}")]
    [Arguments("something.new", """{"a":1}""")]
    [Arguments("answer.delta", "not json")]
    public async Task Parse_unknownOrMalformed_isIgnored(string type, string data)
    {
        await Assert.That(AnswerEventParser.Parse(type, data)).IsNull();
    }
}
