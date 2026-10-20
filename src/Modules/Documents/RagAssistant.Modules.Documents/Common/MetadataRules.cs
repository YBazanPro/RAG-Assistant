using FluentValidation;

namespace RagAssistant.Modules.Documents.Common;

/// <summary>The metadata field rules shared by import and metadata edits (spec 006 / spec 009).</summary>
internal static class MetadataRules
{
    public const int MaxTitleLength = 200;
    public const int MaxOwnerNameLength = 100;
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
    public const int MaxSourceLength = 500;

    public static void AddMetadataRules<T>(
        this AbstractValidator<T> validator,
        Func<T, string?> title,
        Func<T, string?> ownerName,
        Func<T, string?> language,
        Func<T, IReadOnlyList<string>?> tags,
        Func<T, string?> effectiveDate,
        Func<T, string?> source)
    {
        validator.RuleFor(c => title(c)).Cascade(CascadeMode.Stop)
            .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Title is required.")
            .Must(t => t!.Trim().Length <= MaxTitleLength).WithMessage($"Title must be at most {MaxTitleLength} characters.")
            .OverridePropertyName("Title");

        validator.RuleFor(c => ownerName(c)).Cascade(CascadeMode.Stop)
            .Must(o => !string.IsNullOrWhiteSpace(o)).WithMessage("Owner name is required.")
            .Must(o => o!.Trim().Length <= MaxOwnerNameLength).WithMessage($"Owner name must be at most {MaxOwnerNameLength} characters.")
            .OverridePropertyName("OwnerName");

        validator.RuleFor(c => language(c))
            .Must(DocumentMetadata.IsValidLanguageInput)
            .WithMessage("Language must be 'detect' or a two-letter ISO 639-1 code such as 'en'.")
            .OverridePropertyName("Language");

        validator.RuleFor(c => tags(c)).Cascade(CascadeMode.Stop)
            .Must(t => DocumentMetadata.NormalizeTags(t).All(x => x.Length > 0))
            .WithMessage("Tags must not be empty.")
            .Must(t => DocumentMetadata.NormalizeTags(t).All(x => x.Length <= MaxTagLength))
            .WithMessage($"Each tag must be at most {MaxTagLength} characters.")
            .Must(t => DocumentMetadata.NormalizeTags(t).Length <= MaxTags)
            .WithMessage($"At most {MaxTags} tags are allowed.")
            .OverridePropertyName("Tags");

        validator.RuleFor(c => effectiveDate(c))
            .Must(d => DocumentMetadata.TryParseDate(d, out _))
            .WithMessage($"Effective date must use the format {DocumentMetadata.DateFormat}.")
            .OverridePropertyName("EffectiveDate");

        validator.RuleFor(c => source(c))
            .Must(s => s is null || s.Trim().Length <= MaxSourceLength)
            .WithMessage($"Source must be at most {MaxSourceLength} characters.")
            .OverridePropertyName("Source");
    }
}
