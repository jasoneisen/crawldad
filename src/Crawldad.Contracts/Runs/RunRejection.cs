namespace Crawldad.Contracts.Runs;

/// <summary>
/// The <c>POST /runs</c> 400 body when a pinned-payload request references something unrunnable (§14.2): the payload id
/// is unknown, the pinned revision does not exist, or the payload is archived. Distinct from a run <em>failure</em> (a
/// run that started and faulted is HTTP 200 with a <c>failure</c>, §10): this is a request-level rejection, so no run is
/// ever started. <see cref="Code"/> is a stable slug (<c>unknown_payload</c>/<c>unknown_revision</c>/<c>payload_archived</c>).
/// </summary>
/// <param name="Code">The stable rejection slug.</param>
/// <param name="Message">A human-readable description.</param>
public sealed record RunRejection(string Code, string Message);
