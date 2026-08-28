using Crawldad.Api.Features.Tenancy;
using Crawldad.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Unit;

/// <summary>Unit cover for <see cref="PresentedApiKey.Read"/> — how the self-service key endpoints identify the key that
/// authenticated THIS request (to flag it <c>current</c> and refuse revoking it), mirroring the auth handler's
/// <c>Authorization: Bearer</c> → <c>X-Api-Key</c> precedence. All keys here are synthetic.</summary>
public class PresentedApiKeyTests
{
    private static HttpRequest RequestWith(params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context.Request;
    }

    [Fact]
    public void Reads_the_bearer_token_first()
    {
        PresentedApiKey.Read(RequestWith(("Authorization", "Bearer ck_test_abc"))).ShouldBe("ck_test_abc");
    }

    [Fact]
    public void Falls_back_to_the_api_key_header_when_there_is_no_bearer()
    {
        PresentedApiKey.Read(RequestWith((CrawldadAuthentication.ApiKeyHeader, "ck_test_xyz"))).ShouldBe("ck_test_xyz");
    }

    [Fact]
    public void An_empty_bearer_falls_through_to_the_api_key_header()
    {
        PresentedApiKey.Read(RequestWith(
            ("Authorization", "Bearer  "),
            (CrawldadAuthentication.ApiKeyHeader, "ck_test_xyz"))).ShouldBe("ck_test_xyz");
    }

    [Fact]
    public void An_empty_bearer_and_no_api_key_reads_as_absent()
    {
        PresentedApiKey.Read(RequestWith(("Authorization", "Bearer "))).ShouldBe("");
    }

    [Fact]
    public void No_credential_header_reads_as_absent()
    {
        PresentedApiKey.Read(RequestWith()).ShouldBe("");
    }
}
