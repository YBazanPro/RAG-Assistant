namespace RagAssistant.Evaluation;

/// <summary>A precondition or step that stops the command with a clear message and a non-zero exit code (spec 018).</summary>
internal sealed class HarnessException(string message) : Exception(message);
