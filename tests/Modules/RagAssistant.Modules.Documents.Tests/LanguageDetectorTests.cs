using RagAssistant.SharedKernel.Common.Text;

namespace RagAssistant.Modules.Documents.Tests;

// The detector lives in the shared kernel (spec 018); Documents is its first caller, so its tests live here.
internal sealed class LanguageDetectorTests
{
    private const string EnglishParagraph =
        "Employees who travel for work must book their trips through the approved agency. The manager approves each request "
        + "before it is booked, and any expense above the limit needs a written exception from the finance team.";

    private const string FrenchParagraph =
        "Les salariés qui se déplacent pour le travail doivent réserver leurs voyages auprès de l'agence agréée. Le responsable "
        + "approuve chaque demande avant la réservation, et toute dépense au-delà du plafond exige une dérogation écrite.";

    private const string FrenchWithoutAccents =
        "Les salaries qui se deplacent pour le travail doivent reserver leurs voyages aupres de l'agence agreee. Le responsable "
        + "approuve chaque demande avant la reservation, et toute depense au-dela du plafond exige une derogation de la direction.";

    private const string EnglishPolicyWithLoanWords =
        "The café on the ground floor is open to all staff. Each team may book it for a meeting, and the résumé of every "
        + "candidate who visits the office should be shared with the hiring manager before the interview starts.";

    [Test]
    public async Task Detect_englishParagraph_isConfidentEnglish()
    {
        await Assert.That(LanguageDetector.Detect(EnglishParagraph)).IsEqualTo(new LanguageDetection("en", true));
    }

    [Test]
    public async Task Detect_frenchParagraph_isConfidentFrench()
    {
        await Assert.That(LanguageDetector.Detect(FrenchParagraph)).IsEqualTo(new LanguageDetection("fr", true));
    }

    [Test]
    public async Task Detect_frenchWithoutAccents_isConfidentFrench()
    {
        await Assert.That(LanguageDetector.Detect(FrenchWithoutAccents)).IsEqualTo(new LanguageDetection("fr", true));
    }

    [Test]
    public async Task Detect_englishWithFrenchLoanWords_isConfidentEnglish()
    {
        await Assert.That(LanguageDetector.Detect(EnglishPolicyWithLoanWords)).IsEqualTo(new LanguageDetection("en", true));
    }

    [Test]
    public async Task Detect_fewerThanMinimumWords_isUncertainWithGuess()
    {
        var result = LanguageDetector.Detect("Les congés payés de la société");

        await Assert.That(result).IsEqualTo(new LanguageDetection("fr", false));
    }

    [Test]
    public async Task Detect_shortQuestionWithLowerMinimum_isConfident()
    {
        var result = LanguageDetector.Detect("VPN pour les prestataires ?", minWords: 3);

        await Assert.That(result).IsEqualTo(new LanguageDetection("fr", true));
    }

    [Test]
    public async Task Detect_balancedMix_isUncertain()
    {
        var result = LanguageDetector.Detect(EnglishParagraph + "\n\n" + FrenchParagraph);

        await Assert.That(result.IsConfident).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("12345 — 2026-10-18 ; BX-204")]
    public async Task Detect_noSignal_isUnknown(string? text)
    {
        await Assert.That(LanguageDetector.Detect(text)).IsEqualTo(LanguageDetection.Unknown);
    }

    [Test]
    public async Task Detect_textBeyondLimit_readsOnlyTheStart()
    {
        var text = FrenchParagraph + new string(' ', LanguageDetector.MaxCharacters) + string.Concat(Enumerable.Repeat(EnglishParagraph, 50));

        await Assert.That(LanguageDetector.Detect(text)).IsEqualTo(new LanguageDetection("fr", true));
    }
}
