using System.Security.Claims;

namespace RagAssistant.SharedKernel.Common.Security;

public static class ClaimsPrincipalExtensions
{
    public const string SubjectClaim = "sub";

    /// <summary>The user's stable identifier. Claims are not remapped, so it is the raw <c>sub</c> claim.</summary>
    public static string GetSubject(this ClaimsPrincipal user) =>
        user.FindFirst(SubjectClaim)?.Value
        ?? throw new InvalidOperationException("The authenticated user has no 'sub' claim.");
}
