using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Common;

/// <summary>The API spelling of the retrieval stages (spec 019), the same as the diagnostic search endpoint: lowercase words.</summary>
internal static class RetrievalNames
{
    public static string ToApi(this RetrievalMode mode) => mode == RetrievalMode.Hybrid ? "hybrid" : "vector";

    public static string ToApi(this RerankStatus status) => status switch
    {
        RerankStatus.Applied => "applied",
        RerankStatus.Skipped => "skipped",
        _ => "off",
    };

    public static string ToApi(this AdmittedBy admitted) => admitted switch
    {
        AdmittedBy.Keyword => "keyword",
        AdmittedBy.Both => "both",
        _ => "vector",
    };
}
