using Crawldad.Portal.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Portal;

/// <summary>The Development-only startup seeder: a complete <c>Portal:DevTenantLink</c> section writes one link
/// through the store (encrypted, decryptable), a missing/partial section is a no-op, and stop is a no-op.</summary>
[Collection(PortalCollection.Name)]
public class DevTenantLinkSeederTests(PortalFixture fixture)
{
    private static DevTenantLinkSeeder SeederFor(IPortalTenantLinkStore store, DevTenantLinkOptions options) =>
        new(store, Options.Create(options), NullLogger<DevTenantLinkSeeder>.Instance);

    [Fact]
    public async Task Seeds_a_complete_link_through_the_store()
    {
        var store = new RecordingLinkStore();

        await SeederFor(store, new DevTenantLinkOptions { Email = "dev@example.com", TenantId = "t1", ApiKey = "sk_dev" })
            .StartAsync(CancellationToken.None);

        store.Upserts.ShouldHaveSingleItem().ShouldBe(("dev@example.com", "t1", "sk_dev"));
    }

    [Theory]
    [InlineData(null, "t", "k")] // no email
    [InlineData("e", null, "k")] // no tenant
    [InlineData("e", "t", null)] // no key
    [InlineData("  ", "t", "k")] // blank email
    public async Task Skips_when_any_field_is_missing(string? email, string? tenantId, string? apiKey)
    {
        var store = new RecordingLinkStore();

        await SeederFor(store, new DevTenantLinkOptions { Email = email, TenantId = tenantId, ApiKey = apiKey })
            .StartAsync(CancellationToken.None);

        store.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stop_is_a_no_op()
    {
        await SeederFor(new RecordingLinkStore(), new DevTenantLinkOptions()).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Seeding_through_the_real_store_writes_an_encrypted_decryptable_link()
    {
        var email = $"seed-{Guid.NewGuid():N}@example.com";
        var store = fixture.App.Services.GetRequiredService<IPortalTenantLinkStore>();

        await SeederFor(store, new DevTenantLinkOptions { Email = email, TenantId = "seed-tenant", ApiKey = "sk_SEED_LEAKME" })
            .StartAsync(CancellationToken.None);

        var link = await store.GetAsync(email);
        link.ShouldNotBeNull();
        link.TenantId.ShouldBe("seed-tenant");
        link.ProtectedApiKey.ShouldNotBeNull(); // the dev seeder always seeds a stored-key link
        var protector = PortalTenancy.ApiKeyProtector(fixture.App.Services.GetRequiredService<IDataProtectionProvider>());
        protector.Unprotect(link.ProtectedApiKey).ShouldBe("sk_SEED_LEAKME");
    }

    private sealed class RecordingLinkStore : IPortalTenantLinkStore
    {
        public List<(string Email, string TenantId, string ApiKey)> Upserts { get; } = [];

        public Task<PortalTenantLink?> GetAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<PortalTenantLink?>(null);

        public Task<PortalTenantLink> UpsertAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default)
        {
            Upserts.Add((email, tenantId, apiKey));
            return Task.FromResult(new PortalTenantLink { Email = email, TenantId = tenantId, ProtectedApiKey = "cipher" });
        }

        public Task<PortalTenantLink> UpsertKeylessAsync(string email, string tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(); // the dev seeder always seeds a stored-key link
    }
}
