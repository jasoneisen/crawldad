namespace Crawldad.Contracts;

/// <summary>The /health liveness response. A wire type so the boot smoke test can round-trip it as JSON.</summary>
public sealed record HealthStatus(string Status);
