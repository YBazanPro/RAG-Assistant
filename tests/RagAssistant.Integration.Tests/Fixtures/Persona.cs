namespace RagAssistant.Integration.Tests.Fixtures;

public enum Persona
{
    Anonymous,
    KnowledgeSeeker,
    ContentOwner,
    Maintainer,
}

public enum TokenFlaw
{
    None,
    Expired,
    WrongKey,
    WrongIssuer,
    WrongAudience,
    NoRoles,
}
