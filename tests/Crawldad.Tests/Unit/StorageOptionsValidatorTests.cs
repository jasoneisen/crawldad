using Crawldad.Web.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The boot-time storage-options guard (CD-2): a valid configuration passes, and each misconfiguration that would break a
/// sweep or leave a durable provider mis-targeted fails startup with a specific, aggregated message.
/// </summary>
public class StorageOptionsValidatorTests
{
    private static readonly StorageOptionsValidator _validator = new();

    private static ValidateOptionsResult Validate(StorageOptions options) => _validator.Validate(name: null, options);

    [Fact]
    public void The_default_filesystem_configuration_is_valid() =>
        Validate(new StorageOptions()).Succeeded.ShouldBeTrue(); // Provider=filesystem, Root defaulted, positive TTLs/interval

    [Fact]
    public void The_fake_provider_skips_the_durable_provider_checks() =>
        // No Root / connection string is required for the in-memory provider, so an otherwise-empty config still validates.
        Validate(new StorageOptions
        {
            Provider = StorageOptions.FakeProvider,
            FileSystem = new FileSystemStorageOptions { Root = "" },
            Azure = new AzureStorageOptions { ConnectionString = "", Container = "" },
        }).Succeeded.ShouldBeTrue();

    [Fact]
    public void The_azure_provider_with_its_defaults_is_valid() =>
        Validate(new StorageOptions { Provider = StorageOptions.AzureProvider }).Succeeded.ShouldBeTrue();

    [Fact]
    public void A_filesystem_provider_without_a_root_fails()
    {
        var result = Validate(new StorageOptions
        {
            Provider = StorageOptions.FileSystemProvider,
            FileSystem = new FileSystemStorageOptions { Root = "  " },
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("FileSystem:Root");
    }

    [Fact]
    public void An_azure_provider_without_a_connection_string_or_container_fails()
    {
        var result = Validate(new StorageOptions
        {
            Provider = StorageOptions.AzureProvider,
            Azure = new AzureStorageOptions { ConnectionString = "", Container = "" },
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Azure:ConnectionString");
        result.FailureMessage.ShouldContain("Azure:Container");
    }

    [Fact]
    public void A_non_positive_sweep_interval_fails()
    {
        var result = Validate(new StorageOptions
        {
            Provider = StorageOptions.FakeProvider,
            Retention = new RetentionOptions { SweepInterval = TimeSpan.Zero },
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Retention:SweepInterval");
    }

    [Fact]
    public void Negative_retention_ttls_fail()
    {
        var result = Validate(new StorageOptions
        {
            Provider = StorageOptions.FakeProvider,
            Retention = new RetentionOptions
            {
                DownloadTtl = TimeSpan.FromDays(-1),
                ScreenshotTtl = TimeSpan.FromMinutes(-1),
            },
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Retention:DownloadTtl");
        result.FailureMessage.ShouldContain("Retention:ScreenshotTtl");
    }

    [Fact]
    public void A_zero_ttl_is_allowed_as_the_disable_switch() =>
        Validate(new StorageOptions
        {
            Provider = StorageOptions.FakeProvider,
            Retention = new RetentionOptions { DownloadTtl = TimeSpan.Zero, ScreenshotTtl = TimeSpan.Zero },
        }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Null_options_is_rejected() =>
        Should.Throw<ArgumentNullException>(() => Validate(null!));
}
