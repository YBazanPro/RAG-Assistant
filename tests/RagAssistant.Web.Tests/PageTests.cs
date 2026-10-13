using System.Net;
using RagAssistant.Web.Tests.Fixtures;

namespace RagAssistant.Web.Tests;

[ClassDataSource<WebAppFactory>(Shared = SharedType.PerTestSession)]
public class PageTests(WebAppFactory factory)
{
    [Test]
    [Arguments(Persona.KnowledgeSeeker, false)]
    [Arguments(Persona.ContentOwner, false)]
    [Arguments(Persona.Maintainer, true)]
    public async Task Navigation_shows_home_chat_documents_and_admin_only_to_the_maintainer(Persona persona, bool sees_admin)
    {
        var client = await factory.CreateSignedInAsync(persona);

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(html).Contains(">Home</a>");
        await Assert.That(html).Contains(">Chat</a>");
        await Assert.That(html).Contains(">Documents</a>");
        await Assert.That(html.Contains(">Admin</a>", StringComparison.Ordinal)).IsEqualTo(sees_admin);
        await Assert.That(html).DoesNotContain("href=\"knowledge\"");
        await Assert.That(html).DoesNotContain(">Knowledge</a>");
    }

    [Test]
    public async Task Navigation_for_a_user_with_no_known_role_shows_home_only()
    {
        var client = await factory.CreateSignedInAsync(Persona.NoRole);

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(html).Contains(">Home</a>");
        await Assert.That(html).DoesNotContain(">Chat</a>");
        await Assert.That(html).DoesNotContain("Ask a question");
    }

    [Test]
    [Arguments(Persona.KnowledgeSeeker, "/admin")]
    [Arguments(Persona.ContentOwner, "/admin")]
    [Arguments(Persona.NoRole, "/chat")]
    [Arguments(Persona.NoRole, "/documents")]
    public async Task Page_without_the_required_role_returns_403_with_access_denied(Persona persona, string path)
    {
        var client = await factory.CreateSignedInAsync(persona);

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(html).Contains("Access denied");
        await Assert.That(html).Contains("Back to Home");
    }

    [Test]
    public async Task Admin_page_is_available_to_the_maintainer()
    {
        var client = await factory.CreateSignedInAsync(Persona.Maintainer);

        var response = await client.GetAsync(new Uri("/admin", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("Nothing to review yet");
        await Assert.That(html).Contains("<title>Admin · RagAssistant</title>");
    }

    [Test]
    public async Task Unknown_route_returns_404_with_the_not_found_view()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/does-not-exist", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(html).Contains("Page not found");
    }

    [Test]
    public async Task Home_shows_the_display_name_a_sign_out_button_and_the_ask_button()
    {
        var client = await factory.CreateSignedInAsync(Persona.ContentOwner);

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(html).Contains("ContentOwner Dev");
        await Assert.That(html).Contains("Sign out");
        await Assert.That(html).Contains("Ask a question");
        await Assert.That(html).Contains("<title>Home · RagAssistant</title>");
    }

    [Test]
    public async Task Layout_has_landmarks_and_a_skip_link_before_everything_else()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(html).Contains("<header");
        await Assert.That(html).Contains("<main id=\"main-content\"");
        await Assert.That(html).Contains("aria-label=\"Main\"");
        await Assert.That(html.IndexOf("skip-link", StringComparison.Ordinal)).IsLessThan(html.IndexOf("<header", StringComparison.Ordinal));
    }

    // A re-executed status page carries no persisted authentication state: if WebAssembly took it over, the client
    // would see an anonymous user and bounce a signed-in user to sign-in again.
    [Test]
    [Arguments(Persona.KnowledgeSeeker, "/admin")]
    [Arguments(Persona.KnowledgeSeeker, "/does-not-exist")]
    public async Task Status_pages_are_static_and_never_start_webassembly(Persona persona, string path)
    {
        var client = await factory.CreateSignedInAsync(persona);

        var html = await (await client.GetAsync(new Uri(path, UriKind.Relative))).Content.ReadAsStringAsync();

        await Assert.That(html).DoesNotContain("\"type\":\"webassembly\"");
        await Assert.That(html).DoesNotContain("Blazor-WebAssembly-Component-State");
    }

    [Test]
    public async Task Normal_pages_still_start_webassembly_with_the_authentication_state_persisted()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var html = await client.GetStringAsync(new Uri("/chat", UriKind.Relative));

        await Assert.That(html).Contains("\"type\":\"webassembly\"");
        await Assert.That(html).Contains("Blazor-WebAssembly-Component-State");
    }
}
