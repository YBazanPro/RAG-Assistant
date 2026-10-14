using FluentValidation.Results;
using RagAssistant.Modules.Documents.Features.ImportDocument;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class ImportDocumentValidatorTests
{
    private static readonly ImportDocument.Validator Validator = new();

    private static ImportDocument.Command Valid() => new(
        Title: "Budget Policy",
        OwnerName: "Finance",
        Language: null,
        Tags: null,
        EffectiveDate: null,
        Source: null,
        FileCount: 1,
        File: null,
        UserId: "sub-1");

    private static string[] FieldsWithErrors(ValidationResult result) =>
        [.. result.Errors.Select(e => e.PropertyName).Distinct()];

    private static async Task AssertFailsOnlyOn(ValidationResult result, string field)
    {
        var fields = FieldsWithErrors(result);

        await Assert.That(fields.Length).IsEqualTo(1);
        await Assert.That(fields[0]).IsEqualTo(field);
    }

    [Test]
    public async Task Validate_minimalValidCommand_passes()
    {
        await Assert.That(Validator.Validate(Valid()).IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_allOptionalFieldsAtTheirLimits_passes()
    {
        var command = Valid() with
        {
            Title = new string('t', 200),
            OwnerName = new string('o', 100),
            Language = "FR",
            Tags = [.. Enumerable.Range(0, 20).Select(i => $"tag{i}")],
            EffectiveDate = "2026-01-01",
            Source = new string('s', 500),
        };

        await Assert.That(Validator.Validate(command).IsValid).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Validate_missingTitle_failsOnTitle(string? title)
    {
        var result = Validator.Validate(Valid() with { Title = title });

        await AssertFailsOnlyOn(result, "Title");
    }

    [Test]
    public async Task Validate_titleOver200_failsOnTitle()
    {
        var result = Validator.Validate(Valid() with { Title = new string('x', 201) });

        await AssertFailsOnlyOn(result, "Title");
    }

    [Test]
    [Arguments(null)]
    [Arguments(" ")]
    public async Task Validate_missingOwner_failsOnOwnerName(string? owner)
    {
        var result = Validator.Validate(Valid() with { OwnerName = owner });

        await AssertFailsOnlyOn(result, "OwnerName");
    }

    [Test]
    public async Task Validate_ownerOver100_failsOnOwnerName()
    {
        var result = Validator.Validate(Valid() with { OwnerName = new string('x', 101) });

        await AssertFailsOnlyOn(result, "OwnerName");
    }

    [Test]
    [Arguments("english")]
    [Arguments("en-us")]
    [Arguments("x1")]
    public async Task Validate_invalidLanguage_failsOnLanguage(string language)
    {
        var result = Validator.Validate(Valid() with { Language = language });

        await AssertFailsOnlyOn(result, "Language");
    }

    [Test]
    public async Task Validate_21DistinctTags_failsOnTags()
    {
        var tags = Enumerable.Range(0, 21).Select(i => $"tag{i}").ToArray();

        var result = Validator.Validate(Valid() with { Tags = tags });

        await AssertFailsOnlyOn(result, "Tags");
    }

    [Test]
    public async Task Validate_21TagsThatCollapseToFewer_passes()
    {
        var tags = Enumerable.Range(0, 21).Select(i => i % 2 == 0 ? "Budget" : " budget ").ToArray();

        await Assert.That(Validator.Validate(Valid() with { Tags = tags }).IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_tagOver50_failsOnTags()
    {
        var result = Validator.Validate(Valid() with { Tags = [new string('x', 51)] });

        await AssertFailsOnlyOn(result, "Tags");
    }

    [Test]
    public async Task Validate_blankTag_failsOnTags()
    {
        var result = Validator.Validate(Valid() with { Tags = ["ok", "  "] });

        await AssertFailsOnlyOn(result, "Tags");
    }

    [Test]
    [Arguments("31/01/2026")]
    [Arguments("2026-02-30")]
    [Arguments("soon")]
    public async Task Validate_badEffectiveDate_failsOnEffectiveDate(string date)
    {
        var result = Validator.Validate(Valid() with { EffectiveDate = date });

        await AssertFailsOnlyOn(result, "EffectiveDate");
    }

    [Test]
    public async Task Validate_sourceOver500_failsOnSource()
    {
        var result = Validator.Validate(Valid() with { Source = new string('x', 501) });

        await AssertFailsOnlyOn(result, "Source");
    }
}
