using System.Collections.Generic;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Api.Infrastructure.Storage.Azure;
using Crawldad.Api.Infrastructure.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Unit;

/// <summary>The storage DI wiring (<see cref="StorageModule"/>): the provider config value selects the blob backend for all
/// three seams (the keyed download sink, the screenshot store, the retention store) — the same registry idiom as the browser
/// backends. The azure branch resolves without any emulator, so this wiring is covered hermetically even though its I/O is exercised only against Azurite.</summary>
public class StorageModuleTests
{
    private static ServiceProvider Build(string? provider)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Crawldad:Storage:FileSystem:Root"] = Path.Combine(Path.GetTempPath(), "crawldad-smt", Guid.NewGuid().ToString("N")),
        };
        if (provider is not null)
        {
            settings["Crawldad:Storage:Provider"] = provider;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        StorageModule.AddStorage(services, config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_fake_provider_wires_the_in_memory_seams_and_no_retention_store()
    {
        using var sp = Build(StorageOptions.FakeProvider);

        sp.GetRequiredKeyedService<IDownloadSink>(StorageOptions.FakeProvider).ShouldBeOfType<FakeDownloadSink>();
        sp.GetRequiredService<IScreenshotStore>().ShouldBeOfType<InMemoryScreenshotStore>();
        sp.GetServices<IRetentionStore>().ShouldBeEmpty(); // ephemeral fakes need no sweeper → the janitor no-ops
        sp.GetRequiredService<IDownloadSinkRegistry>().TryResolve(StorageOptions.FakeProvider, out _).ShouldBeTrue();
    }

    [Fact]
    public void An_unset_provider_defaults_to_the_durable_filesystem_backend()
    {
        using var sp = Build(provider: null);

        sp.GetRequiredKeyedService<IDownloadSink>(StorageOptions.FileSystemProvider).ShouldBeOfType<FileSystemBlobStore>();
    }

    [Fact]
    public void The_filesystem_provider_shares_one_instance_across_all_three_seams()
    {
        using var sp = Build(StorageOptions.FileSystemProvider);

        var sink = sp.GetRequiredKeyedService<IDownloadSink>(StorageOptions.FileSystemProvider);
        var screenshots = sp.GetRequiredService<IScreenshotStore>();
        var retention = sp.GetRequiredService<IRetentionStore>();

        sink.ShouldBeOfType<FileSystemBlobStore>();
        screenshots.ShouldBeSameAs(sink);   // one store backs the sink, the screenshot store, and the sweeper
        retention.ShouldBeSameAs(sink);
    }

    [Fact]
    public void The_azure_provider_wires_the_azure_backend_without_touching_the_emulator()
    {
        using var sp = Build(StorageOptions.AzureProvider);

        var sink = sp.GetRequiredKeyedService<IDownloadSink>(StorageOptions.AzureProvider);
        sink.ShouldBeOfType<AzureBlobStore>();
        sp.GetRequiredService<IScreenshotStore>().ShouldBeSameAs(sink);
        sp.GetRequiredService<IRetentionStore>().ShouldBeSameAs(sink);
    }

    [Fact]
    public void An_unknown_provider_fails_fast_at_registration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Crawldad:Storage:Provider"] = "nope" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);

        var ex = Should.Throw<InvalidOperationException>(() => StorageModule.AddStorage(services, config));
        ex.Message.ShouldContain("nope");
    }

    [Fact]
    public void AddStorage_rejects_null_arguments()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Should.Throw<ArgumentNullException>(() => StorageModule.AddStorage(null!, config));
        Should.Throw<ArgumentNullException>(() => StorageModule.AddStorage(services, null!));
    }
}
