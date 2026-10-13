namespace RagAssistant.Web.Client.Security;

/// <summary>Adds the header the BFF requires on state-changing calls; a cross-site form cannot add it.</summary>
public sealed class RequestedWithHandler : DelegatingHandler
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "ragassistant";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(HeaderName, HeaderValue);
        return base.SendAsync(request, cancellationToken);
    }
}
