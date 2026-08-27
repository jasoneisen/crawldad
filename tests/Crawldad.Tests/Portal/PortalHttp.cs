using AngleSharp.Html.Parser;

namespace Crawldad.Tests.Portal;

/// <summary>Helpers for driving the SSR login form over HTTP: pull the framework antiforgery token out of a
/// rendered page and build the matching form post.</summary>
internal static class PortalHttp
{
    private static readonly HtmlParser _parser = new();

    /// <summary>A relative request URI (satisfies CA2234's Uri-overload preference; the client sets BaseAddress).</summary>
    internal static Uri Rel(string path) => new(path, UriKind.Relative);

    internal static string ExtractAntiforgeryToken(string html)
    {
        var doc = _parser.ParseDocument(html);
        return doc.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value")
            ?? throw new InvalidOperationException("No antiforgery token found in the rendered page.");
    }

    /// <summary>Drives the full request→verify flow, leaving the auth cookie in the client's jar. Returns the
    /// final redirect response.</summary>
    internal static async Task<HttpResponseMessage> SignInAsync(HttpClient client, CapturingEmailSender email, string address)
    {
        var requestToken = ExtractAntiforgeryToken(await client.GetStringAsync(Rel("/login")));
        var requestResp = await client.PostAsync(Rel("/login"), LoginForm(requestToken, address, "request"));
        var code = email.LastCodeFor(Crawldad.Portal.Auth.PortalAuthService.NormalizeEmail(address));
        var verifyToken = ExtractAntiforgeryToken(await requestResp.Content.ReadAsStringAsync());
        return await client.PostAsync(Rel("/login"), LoginForm(verifyToken, address, "verify", code));
    }

    internal static FormUrlEncodedContent LoginForm(
        string token, string email, string step, string? code = null, string? returnUrl = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Step"] = step,
        };
        if (code is not null)
        {
            fields["Input.Code"] = code;
        }
        if (returnUrl is not null)
        {
            fields["Input.ReturnUrl"] = returnUrl;
        }

        return new FormUrlEncodedContent(fields);
    }

    /// <summary>An antiforgery-only form post (for the sign-out endpoint).</summary>
    internal static FormUrlEncodedContent TokenForm(string token, string? returnUrl = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = token,
        };
        if (returnUrl is not null)
        {
            fields["returnUrl"] = returnUrl;
        }

        return new FormUrlEncodedContent(fields);
    }
}
