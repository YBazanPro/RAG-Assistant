using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class DocumentMetadataTests
{
    [Test]
    [Arguments("a.md", true, SourceFormat.Markdown)]
    [Arguments("a.MARKDOWN", true, SourceFormat.Markdown)]
    [Arguments("a.txt", true, SourceFormat.PlainText)]
    [Arguments("notes.TXT", true, SourceFormat.PlainText)]
    [Arguments("a.PDF", true, SourceFormat.Pdf)]
    [Arguments("a.docx", false, default(SourceFormat))]
    [Arguments("README", false, default(SourceFormat))]
    [Arguments("a.md.exe", false, default(SourceFormat))]
    public async Task TryGetFormat_fileName_mapsExtension(string fileName, bool supported, SourceFormat expected)
    {
        var result = DocumentMetadata.TryGetFormat(fileName, out var format);

        await Assert.That(result).IsEqualTo(supported);
        await Assert.That(format).IsEqualTo(expected);
    }

    [Test]
    [Arguments("policy.md", "policy.md")]
    [Arguments("../../etc/policy.md", "policy.md")]
    [Arguments(@"C:\docs\policy.md", "policy.md")]
    [Arguments("a/b\\c.txt", "c.txt")]
    public async Task SanitizeFileName_withPath_keepsLastSegment(string input, string expected)
    {
        await Assert.That(DocumentMetadata.SanitizeFileName(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task NormalizeTags_mixedCaseWhitespaceAndDuplicates_trimsLowercasesAndDedupes()
    {
        var tags = DocumentMetadata.NormalizeTags([" Budget ", "budget", "FINANCE"]);
        string[] expected = ["budget", "finance"];

        await Assert.That(tags).IsEquivalentTo(expected);
    }

    [Test]
    public async Task NormalizeTags_null_returnsEmpty()
    {
        await Assert.That(DocumentMetadata.NormalizeTags(null)).IsEmpty();
    }

    [Test]
    [Arguments(null, "en")]
    [Arguments("", "en")]
    [Arguments("  ", "en")]
    [Arguments("EN", "en")]
    [Arguments(" Fr ", "fr")]
    public async Task NormalizeLanguage_input_defaultsAndLowercases(string? input, string expected)
    {
        await Assert.That(DocumentMetadata.NormalizeLanguage(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("en", true)]
    [Arguments("fr", true)]
    [Arguments("english", false)]
    [Arguments("en-us", false)]
    [Arguments("e", false)]
    [Arguments("x1", false)]
    [Arguments("é", false)]
    public async Task IsValidLanguage_normalizedCode_requiresTwoAsciiLetters(string code, bool expected)
    {
        await Assert.That(DocumentMetadata.IsValidLanguage(code)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("", true)]
    [Arguments("2026-01-31", true)]
    [Arguments("2026-02-30", false)]
    [Arguments("31/01/2026", false)]
    [Arguments("2026-1-5", false)]
    [Arguments("yesterday", false)]
    public async Task TryParseDate_input_acceptsOnlyIsoDates(string? input, bool expected)
    {
        await Assert.That(DocumentMetadata.TryParseDate(input, out _)).IsEqualTo(expected);
    }

    [Test]
    public async Task TryParseDate_validDate_returnsDateOnly()
    {
        var parsed = DocumentMetadata.TryParseDate("2026-01-31", out var date);

        await Assert.That(parsed).IsTrue();
        await Assert.That(date).IsEqualTo(new DateOnly(2026, 1, 31));
    }
}
