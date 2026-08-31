using System.Net;
using Crawldad.Portal.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>HTTP-level proof of the render-mode wiring and the circuit-safe tenant resolution during prerender: the
/// static-SSR pages carry no interactive-server component marker while the live-trace page does (so exactly one page
/// opts into the circuit), and the live page resolves the signed-in tenant through the AuthenticationStateProvider on
/// the prerender pass — a linked user is streamed, an unlinked one gets the friendly not-linked state.</summary>
[Collection(PortalCollection.Name)]
public class LiveRenderModeTests(PortalFixture fixture)
{
    private static readonly Guid _runId = new("2a7c5e19-0000-0000-0000-000000000042");

    // The Blazor Web App interactive-server boundary marker the framework emits around an @rendermode InteractiveServer
    // component; static-SSR components emit no such marker. There is no separate blazor.server.js in the Web App model —
    // blazor.web.js is the single loader, so this component marker is what distinguishes an interactive page from a static
    // one (the observable stand-in for "the live page bootstraps a server circuit, the static pages do not").
    private const string _serverMarker = "\"type\":\"server\"";

    private static string NewEmail() => $"live-{Guid.NewGuid():N}@example.com";

    private static async Task<string> GetHtmlAsync(HttpClient client, string route) =>
        await (await client.GetAsync(PortalHttp.Rel(route))).Content.ReadAsStringAsync();

    [Fact]
    public async Task Only_the_live_page_bootstraps_an_interactive_server_circuit()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, email);

        var staticHtml = await GetHtmlAsync(client, "/app/runs");
        var liveHtml = await GetHtmlAsync(client, $"/app/live/{_runId}");

        // The static-SSR page renders no interactive-server component marker...
        staticHtml.ShouldNotContain(_serverMarker);
        // ...while the live page opts a component into @rendermode InteractiveServer, so the framework emits the marker.
        liveHtml.ShouldContain(_serverMarker);
    }

    [Fact]
    public async Task The_live_page_resolves_the_tenant_from_auth_state_on_the_prerender_pass()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, email);

        // No active workspace: the circuit-safe resolver reads the signed-in user (via AuthenticationStateProvider), finds no
        // selection, and the page renders the friendly empty state — never a client with no credential.
        var unlinked = await GetHtmlAsync(client, $"/app/live/{_runId}");
        unlinked.ShouldContain("No workspace yet");

        // Give the account an active workspace, then the same prerender resolves it (console-mode) and streams (a connecting
        // state, not the empty state). Prerender only RESOLVES the workspace — the SSE stream starts on the circuit — so no
        // live API is needed here.
        await fixture.App.Services.GetRequiredService<IPortalWorkspaceSelectionStore>()
            .SetAsync(email, "tenant-live");

        var linked = await GetHtmlAsync(client, $"/app/live/{_runId}");
        linked.ShouldNotContain("No workspace yet"); // the resolver found the active workspace on the prerender pass
        linked.ShouldContain("Connecting to the live trace");
    }
}
