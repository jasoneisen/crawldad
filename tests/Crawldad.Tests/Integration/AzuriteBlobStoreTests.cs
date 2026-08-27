using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Azure.Storage.Blobs;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Api.Infrastructure.Storage.Azure;
using Crawldad.Tests.Support;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Crawldad.Tests.Integration;

/// <summary>The durable <see cref="AzureBlobStore"/> against the <b>Azurite emulator</b> — the same
/// <see cref="BlobStoreContract"/> matrix the filesystem adapter runs, with zero live third-party traffic. Azurite
/// is excluded from the coverage gate; when unreachable the tests no-op instead of blocking the build.</summary>
[Collection(AzuriteCollection.Name)]
public class AzuriteBlobStoreTests(AzuriteFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Download_matrix_holds_against_azurite()
    {
        if (Store() is not { } store)
        {
            return;
        }

        await BlobStoreContract.AssertDownloadContractAsync(store, store);
    }

    [Fact]
    public async Task Screenshot_matrix_holds_against_azurite()
    {
        if (Store() is not { } store)
        {
            return;
        }

        await BlobStoreContract.AssertScreenshotContractAsync(store, store);
    }

    [Fact]
    public async Task Retention_matrix_holds_against_azurite()
    {
        if (Store() is not { } store)
        {
            return;
        }

        await BlobStoreContract.AssertRetentionContractAsync(store, store, store);
    }

    // A fresh, uniquely-named container per store so runs never collide; null (with a logged reason) when Azurite is absent.
    private AzureBlobStore? Store()
    {
        if (fixture.ConnectionString is not { } connectionString)
        {
            output.WriteLine($"Azurite not available — skipping. {fixture.SkipReason}");
            return null;
        }

        return new AzureBlobStore(Options.Create(new StorageOptions
        {
            Provider = StorageOptions.AzureProvider,
            Azure = new AzureStorageOptions { ConnectionString = connectionString, Container = "crawldad-test-" + Guid.NewGuid().ToString("N") },
        }));
    }
}

/// <summary>Establishes an Azurite blob endpoint: a configured connection string (CI) or a locally launched cached
/// container, with its published port probed across candidate hosts. Any failure leaves
/// <see cref="ConnectionString"/> null with a <see cref="SkipReason"/>, so a missing emulator never breaks the build.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    /// <summary>The environment variable a CI job / operator sets to a ready Azurite blob connection string (the service path).</summary>
    public const string ConnectionVar = "CRAWLDAD_AZURITE_CONNECTION";

    private const string _image = "mcr.microsoft.com/azure-storage/azurite:latest";

    // Azurite's well-known, public development account key (not a secret) — the standard emulator credential.
    private const string _devAccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private string? _containerId;

    /// <summary>A reachable Azurite blob connection string, or null when the emulator is unavailable.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Why the emulator was skipped (surfaced in test output when unavailable).</summary>
    public string SkipReason { get; private set; } = "";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The emulator is optional; any failure (docker, network, Azure SDK) must degrade to a clean skip, never fail the build.")]
    public async Task InitializeAsync()
    {
        try
        {
            ConnectionString = await EstablishAsync();
        }
        catch (Exception ex)
        {
            ConnectionString = null;
            SkipReason = ex.Message;
        }

        if (ConnectionString is null && string.IsNullOrEmpty(SkipReason))
        {
            SkipReason = "no Azurite endpoint (set CRAWLDAD_AZURITE_CONNECTION or make docker + the azurite image available).";
        }
    }

    public async Task DisposeAsync()
    {
        if (_containerId is not null)
        {
            await DockerAsync(["stop", _containerId], TimeSpan.FromSeconds(20));
        }
    }

    private async Task<string?> EstablishAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVar);
        if (!string.IsNullOrWhiteSpace(configured) && await VerifyAsync(configured))
        {
            return configured;
        }

        if ((await DockerAsync(["image", "inspect", _image], TimeSpan.FromSeconds(20))).Exit != 0)
        {
            return null; // no docker / image — not available locally
        }

        var run = await DockerAsync(
            ["run", "-d", "--rm", "-p", "10000", _image, "azurite-blob", "--blobHost", "0.0.0.0", "--blobPort", "10000", "--skipApiVersionCheck"],
            TimeSpan.FromSeconds(30));
        if (run.Exit != 0)
        {
            return null;
        }

        _containerId = run.Output.Trim();
        var port = await MappedPortAsync(_containerId);
        if (port is null)
        {
            return null;
        }

        // From inside the devcontainer the docker host's published port is reachable via the bridge gateway; try the usual candidates.
        foreach (var host in (string[])["172.17.0.1", "127.0.0.1", "host.docker.internal"])
        {
            var connectionString = BuildConnectionString(host, port.Value);
            if (await VerifyAsync(connectionString))
            {
                return connectionString;
            }
        }

        return null; // started but not reachable from here
    }

    private static string BuildConnectionString(string host, int port) =>
        $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={_devAccountKey};" +
        $"BlobEndpoint=http://{host}:{port.ToString(CultureInfo.InvariantCulture)}/devstoreaccount1;";

    // A few short attempts so container startup latency doesn't read as "unreachable".
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any failure to reach Azurite on this candidate host means 'not reachable here' — retry, then fall through to skip.")]
    private static async Task<bool> VerifyAsync(string connectionString)
    {
        var client = new BlobServiceClient(connectionString);
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.GetBlobContainerClient("crawldad-probe").CreateIfNotExistsAsync(cancellationToken: cts.Token);
                return true;
            }
            catch (Exception)
            {
                await Task.Delay(500);
            }
        }

        return false;
    }

    private static async Task<int?> MappedPortAsync(string containerId)
    {
        var result = await DockerAsync(["port", containerId, "10000/tcp"], TimeSpan.FromSeconds(10));
        if (result.Exit != 0)
        {
            return null;
        }

        // e.g. "0.0.0.0:49153" (possibly multiple lines) — take the port off the first mapping.
        var first = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var colon = first?.LastIndexOf(':') ?? -1;
        return colon >= 0 && int.TryParse(first!.AsSpan(colon + 1), CultureInfo.InvariantCulture, out var port) ? port : null;
    }

    private static async Task<(int Exit, string Output)> DockerAsync(string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, stdout);
    }
}

/// <summary>Shares the one Azurite endpoint (and its container lifetime) across the suite.</summary>
[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "azurite";
}
