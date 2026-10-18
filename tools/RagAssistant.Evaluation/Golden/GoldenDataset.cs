using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RagAssistant.Evaluation.Golden;

/// <summary>
/// <c>samples/golden/questions.json</c> (spec 018): the questions, their expected behaviour and the evidence they should be
/// answered from, named by manifest document key and a verbatim quote.
/// </summary>
internal sealed record GoldenDataset(string? Description, IReadOnlyList<GoldenCase> Cases)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static (GoldenDataset Dataset, string Hash) Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new HarnessException($"No golden dataset at {path}.");
        }

        var bytes = File.ReadAllBytes(path);
        var dataset = JsonSerializer.Deserialize<GoldenDataset>(bytes, JsonOptions)
            ?? throw new HarnessException($"{path} is empty.");
        var duplicates = dataset.Cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new HarnessException($"Duplicate case ids in the golden dataset: {string.Join(", ", duplicates)}.");
        }

        return (dataset, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

/// <param name="Turns">The questions asked in order in one conversation; the expectations are about the last one.</param>
internal sealed record GoldenCase(string Id, string Category, string QuestionLanguage, IReadOnlyList<string> Turns, Expectation Expected, string? Note = null);

/// <param name="Behaviour"><c>answer</c> (complete), <c>partial</c> or <c>refusal</c> (refused).</param>
/// <param name="AnswerLanguage">The language the answer must be written in, when the case asks for one; otherwise the question's.</param>
internal sealed record Expectation(string Behaviour, IReadOnlyList<ExpectedEvidence> Evidence, string? AnswerLanguage = null);

internal sealed record ExpectedEvidence(string Document, string Quote);

/// <summary>Spec 018: Unicode NFC and collapsed whitespace on both sides, so re-chunking or PDF line breaks do not break a quote.</summary>
internal static partial class Evidence
{
    public static string Normalize(string text) => Whitespace().Replace(text.Normalize(NormalizationForm.FormC), " ").Trim();

    public static bool Contains(string passageText, string quote) =>
        Normalize(passageText).Contains(Normalize(quote), StringComparison.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
