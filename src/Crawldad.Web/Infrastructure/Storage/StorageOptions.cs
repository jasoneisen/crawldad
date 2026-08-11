namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>The durable-storage knobs, bound from <c>Crawldad:Storage</c>. <see cref="Provider"/> selects the blob
/// backend for both seams; the durable default is <see cref="FileSystemProvider"/>, so an unconfigured production
/// host stores blobs on disk rather than losing them in memory. Tests set <see cref="FakeProvider"/> for a hermetic suite.</summary>
public sealed class StorageOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Crawldad:Storage";

    /// <summary>The in-memory testing provider: the <c>"fake"</c> download sink + the in-memory screenshot store (no durability).</summary>
    public const string FakeProvider = "fake";

    /// <summary>The durable local-filesystem provider (the hermetic default) — blobs on disk, tenant-partitioned.</summary>
    public const string FileSystemProvider = "filesystem";

    /// <summary>The durable Azure Blob provider (the Azure deployment target) — blobs in Azure Storage, tenant-prefixed.</summary>
    public const string AzureProvider = "azure";

    /// <summary>Which blob backend to use (<see cref="FakeProvider"/> / <see cref="FileSystemProvider"/> / <see cref="AzureProvider"/>).
    /// Durable by default so a production host never silently uses non-durable storage.</summary>
    public string Provider { get; init; } = FileSystemProvider;

    /// <summary>The local-filesystem provider's settings.</summary>
    public FileSystemStorageOptions FileSystem { get; init; } = new();

    /// <summary>The Azure Blob provider's settings.</summary>
    public AzureStorageOptions Azure { get; init; } = new();

    /// <summary>The retention/lifecycle policy the janitor enforces over whichever durable provider is selected.</summary>
    public RetentionOptions Retention { get; init; } = new();
}

/// <summary>The local-filesystem blob store's settings (<c>Crawldad:Storage:FileSystem</c>).</summary>
public sealed class FileSystemStorageOptions
{
    /// <summary>The base directory blobs live under (<c>{Root}/{tenant}/{downloads|screenshots}/…</c>). Defaults to a temp
    /// path so dev/test opt-in works out of the box; a production host sets an explicit persistent path (see appsettings).</summary>
    public string Root { get; init; } = Path.Combine(Path.GetTempPath(), "crawldad", "blobs");
}

/// <summary>The Azure Blob store's settings (<c>Crawldad:Storage:Azure</c>).</summary>
public sealed class AzureStorageOptions
{
    /// <summary>The connection string. Defaults to the Azurite emulator's well-known development string; a production host
    /// sets a real account connection string (or a managed-identity-backed one) via configuration/secrets.</summary>
    public string ConnectionString { get; init; } = "UseDevelopmentStorage=true";

    /// <summary>The container all tenants' blobs share (partitioned by a <c>{tenant}/…</c> blob-name prefix). Must be a
    /// valid Azure container name (lowercase alphanumerics + hyphens).</summary>
    public string Container { get; init; } = "crawldad-blobs";
}

/// <summary>The retention/lifecycle policy: how long each blob category is kept before the scheduled janitor deletes
/// it, and how often it sweeps. A TTL of <see cref="System.TimeSpan.Zero"/> (or negative) disables sweeping for that
/// category (retain indefinitely). Screenshots default to a shorter window than downloads because they can show PII.</summary>
public sealed class RetentionOptions
{
    /// <summary>Whether the retention janitor runs at all. Disable to retain everything indefinitely (e.g. under an external
    /// storage-lifecycle rule).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long a downloaded attachment is kept. <see cref="System.TimeSpan.Zero"/> or less disables its sweep.</summary>
    public TimeSpan DownloadTtl { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How long a failure screenshot is kept — shorter, because it can show PII. Zero or less disables its sweep.</summary>
    public TimeSpan ScreenshotTtl { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long an async run's stored result body (<c>RunProgress.ResultJson</c>/<c>PartialJson</c>) is kept
    /// before the sweep nulls it. Defaults to the <b>7-day PII-grade window</b> — the same as screenshots, not the 30-day
    /// download window: a stored async result can carry scraped page content, and it is only a poll convenience (the
    /// synchronous path returns the result inline and never persists it), so it is aged out conservatively. Zero or less
    /// disables the result sweep (retain stored results indefinitely). Swept by <c>RunResultRetentionSweep</c>, not a
    /// blob store, since <c>RunProgress</c> is a Marten document.</summary>
    public TimeSpan ResultTtl { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How often the janitor sweeps for expired blobs. Must be positive.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The TTL for a blob category, or <see langword="null"/> when sweeping is disabled for it (TTL ≤ 0).</summary>
    /// <param name="kind">The blob category.</param>
    /// <returns>The positive TTL, or null to retain that category indefinitely.</returns>
    public TimeSpan? TtlFor(BlobKind kind)
    {
        var ttl = kind == BlobKind.Download ? DownloadTtl : ScreenshotTtl;
        return ttl > TimeSpan.Zero ? ttl : null;
    }

    /// <summary>The stored-result retention TTL, or <see langword="null"/> when the result sweep is disabled
    /// (<see cref="ResultTtl"/> ≤ 0 — retain stored results indefinitely). The same ≤0-disables convention as
    /// <see cref="TtlFor"/>, so <c>RunResultRetentionSweep</c> reads it exactly as the janitor reads a blob category.</summary>
    public TimeSpan? ResultTtlOrNull => ResultTtl > TimeSpan.Zero ? ResultTtl : null;
}
