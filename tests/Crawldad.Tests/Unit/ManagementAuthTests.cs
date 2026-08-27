using Crawldad.Api.Features.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Unit;

/// <summary>The management-key primitives: a constant-time key compare (correct across equal, unequal, and
/// different-length inputs) and the bearer-header reader. (All keys here are synthetic test values.)</summary>
public class ManagementAuthTests
{
    [Fact]
    public void Equal_keys_compare_equal() =>
        ManagementAuth.ConstantTimeEquals("ck_test_same-management-key", "ck_test_same-management-key").ShouldBeTrue();

    [Fact]
    public void Different_keys_of_the_same_length_are_not_equal() =>
        ManagementAuth.ConstantTimeEquals("ck_test_aaaaaaaaaaaa", "ck_test_bbbbbbbbbbbb").ShouldBeFalse();

    [Fact]
    public void Different_length_keys_are_not_equal() => // the hash-then-compare is length-independent (no early-out leak)
        ManagementAuth.ConstantTimeEquals("short-key", "a-considerably-longer-management-key").ShouldBeFalse();

    [Fact]
    public void A_prefix_of_the_key_is_not_equal() =>
        ManagementAuth.ConstantTimeEquals("ck_test_abc", "ck_test_abcdef").ShouldBeFalse();

    [Fact]
    public void Two_empty_strings_compare_equal() =>
        ManagementAuth.ConstantTimeEquals("", "").ShouldBeTrue();

    [Fact]
    public void Rejects_a_null_presented_key() =>
        Should.Throw<ArgumentNullException>(() => ManagementAuth.ConstantTimeEquals(null!, "x"));

    [Fact]
    public void Rejects_a_null_configured_key() =>
        Should.Throw<ArgumentNullException>(() => ManagementAuth.ConstantTimeEquals("x", null!));

    [Fact]
    public void Reads_a_bearer_key()
    {
        ManagementAuth.TryReadBearer(RequestWith("Bearer ck_test_the-key"), out var key).ShouldBeTrue();
        key.ShouldBe("ck_test_the-key");
    }

    [Fact]
    public void Rejects_a_missing_authorization_header()
    {
        ManagementAuth.TryReadBearer(RequestWith(null), out var key).ShouldBeFalse();
        key.ShouldBe("");
    }

    [Fact]
    public void Rejects_a_present_but_empty_bearer_value() =>
        ManagementAuth.TryReadBearer(RequestWith("Bearer   "), out _).ShouldBeFalse();

    [Fact]
    public void Rejects_a_non_bearer_scheme() =>
        ManagementAuth.TryReadBearer(RequestWith("Basic dXNlcjpwYXNz"), out _).ShouldBeFalse();

    private static HttpRequest RequestWith(string? authorization)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context.Request;
    }
}
