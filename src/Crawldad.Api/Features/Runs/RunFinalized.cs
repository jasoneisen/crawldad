namespace Crawldad.Api.Features.Runs;

/// <summary>The durable "a run reached a terminal disposition" signal, published (tenant-scoped) after a run's terminal
/// event commits. A lightweight notification — just the run id — that downstream subscribers react to <b>off the run's
/// execution path</b>, so a slow reaction never touches run execution. Consumed today by the webhook fan-out
/// (<c>Features/Webhooks/RunFinalizedHandler</c>). Published post-commit like <c>PromoteQueued</c>, so delivery is
/// at-least-once; the subscriber derives everything from the committed run state, so a duplicate is harmless.</summary>
public sealed record RunFinalized(Guid RunId);
