namespace RagAssistant.Evaluation;

/// <summary>
/// The <c>Evaluation</c> section: appsettings.json, then user secrets, then <c>RAGEVAL_</c> environment variables, then the command
/// line (<c>--Evaluation:ApiBaseUrl=...</c>). Relative paths are resolved from the repository root.
/// </summary>
internal sealed class EvaluationOptions
{
    public const string Section = "Evaluation";

    public Uri ApiBaseUrl { get; set; } = new("https://localhost:7043/");

    public Uri TokenEndpoint { get; set; } = new("https://localhost:8180/realms/ragassistant/protocol/openid-connect/token");

    public string ClientId { get; set; } = "ragassistant-dev";

    /// <summary>The committed dev realm: passwords that are not configured are read from it, never written in code.</summary>
    public string RealmFile { get; set; } = "src/RagAssistant.AppHost/Realms/ragassistant-realm.json";

    public string CorpusPath { get; set; } = "samples/corpus";

    public string DatasetPath { get; set; } = "samples/golden/questions.json";

    /// <summary>Git-ignored; one folder per run.</summary>
    public string ReportsPath { get; set; } = ".data/evaluation";

    /// <summary>Every seeded document carries this tag; harness conversations are scoped to it (spec 018).</summary>
    public string SampleTag { get; set; } = "sample";

    public string OwnerUser { get; set; } = "owner";

    public string? OwnerPassword { get; set; }

    public string MaintainerUser { get; set; } = "maintainer";

    public string? MaintainerPassword { get; set; }

    public TimeSpan IndexingTimeout { get; set; } = TimeSpan.FromMinutes(15);
}
