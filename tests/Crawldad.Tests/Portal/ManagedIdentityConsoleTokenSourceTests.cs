using Azure.Core;
using Crawldad.Portal.Infrastructure.Security;

namespace Crawldad.Tests.Portal;

/// <summary>The production console token source (issue #119 PR4): it asks the injected Azure credential for a token scoped
/// to <c>{audience}/.default</c> and returns the token string. Exercised with a fake credential — no live managed identity.</summary>
public class ManagedIdentityConsoleTokenSourceTests
{
    [Fact]
    public async Task Acquires_a_token_for_the_audience_default_scope()
    {
        var credential = new RecordingCredential("the-access-token");
        var source = new ManagedIdentityConsoleTokenSource(credential, "api://crawldad-api-stg");

        var token = await source.GetTokenAsync(CancellationToken.None);

        token.ShouldBe("the-access-token");
        credential.LastScopes.ShouldBe(["api://crawldad-api-stg/.default"]);
    }

    [Fact]
    public void Rejects_bad_arguments()
    {
        Should.Throw<ArgumentNullException>(() => new ManagedIdentityConsoleTokenSource(null!, "api://x"));
        Should.Throw<ArgumentException>(() => new ManagedIdentityConsoleTokenSource(new RecordingCredential("t"), " "));
    }

    private sealed class RecordingCredential(string token) : TokenCredential
    {
        public string[]? LastScopes { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            LastScopes = requestContext.Scopes;
            return new AccessToken(token, DateTimeOffset.MaxValue);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            LastScopes = requestContext.Scopes;
            return new ValueTask<AccessToken>(new AccessToken(token, DateTimeOffset.MaxValue));
        }
    }
}
