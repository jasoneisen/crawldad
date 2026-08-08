namespace Crawldad.Contracts.Runs;

/// <summary>
/// A <c>POST /runs</c> request-level rejection — distinct from a run <em>failure</em> (a run that started and faulted is HTTP
/// 200 with a <c>failure</c>, §10): no run is ever started. Surfaced at two status codes:
/// <list type="bullet">
/// <item><b>400</b> when a pinned-payload request references something unrunnable (§14.2): the payload id is unknown, the
/// pinned revision does not exist, or the payload is archived (<c>unknown_payload</c>/<c>unknown_revision</c>/<c>payload_archived</c>).</item>
/// <item><b>429</b> when the tenant's admission queue is already at its per-tier depth (CD-16, docs/PRODUCT.md §Pv.3):
/// <c>queue_depth_exceeded</c>. This is the <em>only</em> 429 from admission — at the concurrent-run cap a run queues (202)
/// rather than being rejected, so the former <c>concurrent_runs_exceeded</c> rejection no longer occurs.</item>
/// </list>
/// <see cref="Code"/> is a stable slug the caller branches on.
/// </summary>
/// <param name="Code">The stable rejection slug.</param>
/// <param name="Message">A human-readable description.</param>
public sealed record RunRejection(string Code, string Message);
