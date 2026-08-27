using Crawldad.Portal.Auth;
using Crawldad.Portal.Tenancy;
using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>The Marten-backed tenant-link store against the real portal store: round-trips a link by normalized
/// email, holds the API key as ciphertext at rest (never plaintext), and preserves createdAt while advancing
/// updatedAt on re-upsert.</summary>
[Collection(PortalCollection.Name)]
public class PortalTenantLinkStoreTests(PortalFixture fixture)
{
    private static string NewEmail() => $"link-{Guid.NewGuid():N}@example.com";

    private IPortalTenantLinkStore Store => fixture.App.Services.GetRequiredService<IPortalTenantLinkStore>();

    [Fact]
    public async Task Upsert_then_get_round_trips_by_normalized_email()
    {
        var email = NewEmail();
        await Store.UpsertAsync(email, "tenant-round", "sk_round_key");

        // Stored lower-invariant → a mixed-case lookup still finds it (the PortalUser identity rule).
        var got = await Store.GetAsync(email.ToUpperInvariant());

        got.ShouldNotBeNull();
        got.Email.ShouldBe(PortalAuthService.NormalizeEmail(email));
        got.TenantId.ShouldBe("tenant-round");
    }

    [Fact]
    public async Task Get_returns_null_when_no_link_exists()
    {
        (await Store.GetAsync(NewEmail())).ShouldBeNull();
    }

    [Fact]
    public async Task The_stored_document_holds_ciphertext_and_decrypts_back()
    {
        var email = NewEmail();
        const string apiKey = "sk_PLAINTEXT_at_rest_LEAKME";
        await Store.UpsertAsync(email, "tenant-secret", apiKey);

        await using var session = fixture.App.Store.QuerySession();
        var doc = await session.LoadAsync<PortalTenantLink>(PortalAuthService.NormalizeEmail(email));

        doc.ShouldNotBeNull();
        doc.ProtectedApiKey.ShouldNotBe(apiKey);        // encrypted, not plaintext
        doc.ProtectedApiKey.ShouldNotContain(apiKey);

        var protector = PortalTenancy.ApiKeyProtector(fixture.App.Services.GetRequiredService<IDataProtectionProvider>());
        protector.Unprotect(doc.ProtectedApiKey).ShouldBe(apiKey);
    }

    [Fact]
    public async Task Re_upsert_preserves_created_at_and_advances_updated_at()
    {
        var email = NewEmail();
        var t0 = fixture.App.Clock.GetUtcNow();
        var first = await Store.UpsertAsync(email, "tenant-a", "sk_a");
        first.CreatedAt.ShouldBe(t0);
        first.UpdatedAt.ShouldBe(t0);

        fixture.App.Clock.Advance(TimeSpan.FromMinutes(5));
        var t1 = fixture.App.Clock.GetUtcNow();
        var second = await Store.UpsertAsync(email, "tenant-b", "sk_b");

        second.CreatedAt.ShouldBe(t0); // preserved across the update
        second.UpdatedAt.ShouldBe(t1); // advanced to now
        second.TenantId.ShouldBe("tenant-b");
    }
}
