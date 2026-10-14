namespace RagAssistant.Modules.Documents.Tests;

internal sealed class ModuleBoundaryTests
{
    [Test]
    public async Task DocumentsAssembly_referencesNoOtherRagAssistantProjectThanShared()
    {
        var assembly = typeof(DocumentsModule).Assembly;

        var internalReferences = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("RagAssistant.", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(internalReferences.Length).IsEqualTo(1);
        await Assert.That(internalReferences[0]).IsEqualTo("RagAssistant.Shared");
    }
}
