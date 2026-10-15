namespace RagAssistant.Integration.Tests.Fixtures;

internal static class SequenceAssertions
{
    /// <summary>Order-sensitive comparison that reads well inside <c>Assert.That(...).IsTrue()</c>.</summary>
    public static bool Same<T>(this IEnumerable<T> actual, params ReadOnlySpan<T> expected) => actual.SequenceEqual(expected.ToArray());
}
