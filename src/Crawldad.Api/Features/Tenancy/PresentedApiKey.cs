using Crawldad.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Reads the raw API key presented on the current request — the same order the auth handler does
/// (<c>Authorization: Bearer</c> first, then <c>X-Api-Key</c>). The self-service key endpoints use it only to answer
/// "is this key the one authenticating the request?" by hashing it and comparing to a stored key hash; it never
/// re-authenticates, and the raw key is never logged, echoed, or put in a problem body. Returns <c>""</c> when no key is
/// present — which cannot happen on an authenticated request, but keeps callers branch-free: the empty string hashes to a
/// value that matches no real key, so it simply reads as "not the current key".</summary>
internal static class PresentedApiKey
{
    private const string _bearerPrefix = "Bearer ";

    /// <summary>The raw presented key, or <c>""</c> when neither credential header carries one.</summary>
    internal static string Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(_bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authorization[_bearerPrefix.Length..].Trim();
            if (bearer.Length > 0)
            {
                return bearer;
            }
        }

        var apiKey = request.Headers[CrawldadAuthentication.ApiKeyHeader].ToString();
        return string.IsNullOrWhiteSpace(apiKey) ? "" : apiKey;
    }
}
