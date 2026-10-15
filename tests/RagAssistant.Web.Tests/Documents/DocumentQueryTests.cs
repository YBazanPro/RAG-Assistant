using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Documents;

// Spec 008, AC-17: the URL state and what is sent to the API.
public class DocumentQueryTests
{
    [Test]
    public async Task FromRaw_validValues_roundTripToTheQueryString()
    {
        var query = DocumentQuery.FromRaw("a", "failed", "FR", null, ["x", "y"], "2026-01-01", "2026-02-01", "title", "2");

        await Assert.That(query.ToQueryString()).IsEqualTo("q=a&status=failed&language=fr&tags=x&tags=y&effectiveFrom=2026-01-01&effectiveTo=2026-02-01&sort=title&page=2");
    }

    [Test]
    [Arguments("status", "foo")]
    [Arguments("page", "0")]
    [Arguments("page", "abc")]
    [Arguments("sort", "bogus")]
    [Arguments("language", "eng")]
    [Arguments("effectiveFrom", "2026-13-40")]
    public async Task FromRaw_invalidValue_isDropped(string field, string value)
    {
        var query = DocumentQuery.FromRaw(
            null,
            field == "status" ? value : null,
            field == "language" ? value : null,
            null,
            null,
            field == "effectiveFrom" ? value : null,
            null,
            field == "sort" ? value : null,
            field == "page" ? value : null);

        await Assert.That(query.ToQueryString()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FromRaw_fromAfterTo_dropsBothDates()
    {
        var query = DocumentQuery.FromRaw(null, null, null, null, null, "2026-05-02", "2026-05-01", null, null);

        await Assert.That(query.EffectiveFrom).IsNull();
        await Assert.That(query.EffectiveTo).IsNull();
    }

    [Test]
    public async Task WithFirstPage_resetsThePage()
    {
        var query = DocumentQuery.FromRaw(null, null, null, null, null, null, null, null, "4").WithFirstPage();

        await Assert.That(query.Page).IsEqualTo(1);
    }
}
