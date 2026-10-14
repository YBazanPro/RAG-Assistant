using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class DocumentStatusRulesTests
{
    [Test]
    [Arguments(DocumentStatus.Archived, VersionStatus.Indexed, "archived")]
    [Arguments(DocumentStatus.Archived, VersionStatus.Queued, "archived")]
    [Arguments(DocumentStatus.Active, VersionStatus.Queued, "queued")]
    [Arguments(DocumentStatus.Active, VersionStatus.Processing, "processing")]
    [Arguments(DocumentStatus.Active, VersionStatus.Failed, "failed")]
    [Arguments(DocumentStatus.Active, VersionStatus.Indexed, "active")]
    public async Task Derive_statusAndLatestVersion_followsPrecedence(
        DocumentStatus status, VersionStatus latest, string expected)
    {
        await Assert.That(DocumentStatusRules.Derive(status, latest)).IsEqualTo(expected);
    }

    [Test]
    public async Task Derive_noVersion_isActiveUnlessArchived()
    {
        await Assert.That(DocumentStatusRules.Derive(DocumentStatus.Active, null)).IsEqualTo("active");
        await Assert.That(DocumentStatusRules.Derive(DocumentStatus.Archived, null)).IsEqualTo("archived");
    }
}
