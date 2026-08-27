using System.Security.Cryptography;
using System.Text;
using Crawldad.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Constant-time comparison of a presented management key against the configured one. Both sides are hashed to a
/// fixed 32-byte digest first, then compared with <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte},
/// ReadOnlySpan{byte})"/> — so the compare is constant-time regardless of the inputs' lengths (a raw
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> short-circuits on a
/// length mismatch, leaking length). No early-out reveals how much of the key matched.</summary>
internal static class ManagementAuth
{
    /// <summary>Whether <paramref name="presented"/> equals <paramref name="configured"/>, in constant time.</summary>
    public static bool ConstantTimeEquals(string presented, string configured)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(configured);
        Span<byte> a = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> b = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), a);
        SHA256.HashData(Encoding.UTF8.GetBytes(configured), b);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    // Reads the bearer key from Authorization: Bearer <key>. A present-but-empty value reads as absent.
    private const string _bearerPrefix = "Bearer ";

    internal static bool TryReadBearer(HttpRequest request, out string key)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(_bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            key = authorization[_bearerPrefix.Length..].Trim();
            return key.Length > 0;
        }

        key = "";
        return false;
    }
}

/// <summary>The management-endpoint guard: rejects any request that does not present the configured management key with a
/// constant-time-compared <c>Authorization: Bearer</c>. Runs before every management handler; a failure is a bare
/// <c>401</c> that never reveals whether the tenant/route exists. The group is only mapped when a key is configured, so
/// the compared key is always non-empty here.</summary>
internal sealed class ManagementKeyFilter(IOptions<ManagementOptions> options) : IEndpointFilter
{
    private readonly ManagementOptions _options = options.Value;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!ManagementAuth.TryReadBearer(context.HttpContext.Request, out var presented)
            || !ManagementAuth.ConstantTimeEquals(presented, _options.ApiKey))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
