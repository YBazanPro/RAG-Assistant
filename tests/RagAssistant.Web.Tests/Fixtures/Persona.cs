using RagAssistant.Security;

namespace RagAssistant.Web.Tests.Fixtures;

/// <summary>The signed-in users of the realm file, plus a user whose token carries no known role.</summary>
public enum Persona
{
    NoRole,
    KnowledgeSeeker,
    ContentOwner,
    Maintainer,
}

public static class PersonaRoles
{
    // Composite roles expanded, as Keycloak emits them in the flat "roles" claim.
    public static string[] For(Persona persona) => persona switch
    {
        Persona.KnowledgeSeeker => [RoleNames.KnowledgeSeeker],
        Persona.ContentOwner => [RoleNames.ContentOwner, RoleNames.KnowledgeSeeker],
        Persona.Maintainer => [RoleNames.Maintainer, RoleNames.ContentOwner, RoleNames.KnowledgeSeeker],
        _ => [],
    };
}
