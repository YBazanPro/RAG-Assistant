namespace RagAssistant.Shared.Common.Security;

/// <summary>Realm role names as they appear in the flat "roles" claim (see the Keycloak realm file).</summary>
public static class RoleNames
{
    public const string KnowledgeSeeker = "knowledge-seeker";
    public const string ContentOwner = "content-owner";
    public const string Maintainer = "maintainer";
}
