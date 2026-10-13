namespace RagAssistant.Shared.Common.Security;

/// <summary>Authorization policy names applied by endpoint groups. Names only: the policies are defined in the Host.</summary>
public static class PolicyNames
{
    public const string KnowledgeSeeker = nameof(KnowledgeSeeker);
    public const string ContentOwner = nameof(ContentOwner);
    public const string Maintainer = nameof(Maintainer);
}
