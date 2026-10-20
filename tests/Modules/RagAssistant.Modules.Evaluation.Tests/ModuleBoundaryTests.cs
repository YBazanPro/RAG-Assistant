namespace RagAssistant.Modules.Evaluation.Tests;

internal sealed class ModuleBoundaryTests
{
    // Spec 020: the Evaluation module stores reports and has no contract with the other modules.
    [Test]
    public async Task EvaluationAssembly_referencesNoOtherRagAssistantProjectThanShared()
    {
        var internalReferences = typeof(EvaluationModule).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("RagAssistant.", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(internalReferences).IsEquivalentTo(["RagAssistant.SharedKernel"]);
    }
}
