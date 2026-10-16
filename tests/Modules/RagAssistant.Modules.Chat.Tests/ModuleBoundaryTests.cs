namespace RagAssistant.Modules.Chat.Tests;

internal sealed class ModuleBoundaryTests
{
    [Test]
    public async Task ChatAssembly_referencesNoOtherRagAssistantProjectThanShared()
    {
        var assembly = typeof(ChatModule).Assembly;

        var internalReferences = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("RagAssistant.", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(internalReferences.Length).IsEqualTo(1);
        await Assert.That(internalReferences[0]).IsEqualTo("RagAssistant.SharedKernel");
    }

    [Test]
    public async Task ChatAssembly_referencesNoModelProvider()
    {
        var providers = typeof(ChatModule).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.Contains("Ollama", StringComparison.OrdinalIgnoreCase) || n.Contains("OpenAI", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        await Assert.That(providers).IsEmpty();
    }
}
