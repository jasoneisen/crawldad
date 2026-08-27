using Crawldad.Api.Infrastructure.Browser;

namespace Crawldad.Api.Features.Runs.Interpreter;

/// <summary>The record-mode hook the interpreter drives while executing a live run, banking each page state (URL +
/// serialised DOM, via the same <see cref="IPageHandle.ContentAsync"/> path a <c>capture</c> node uses) and the
/// interaction transitions it performs into a replayable manifest. Null on every ordinary run — the interpreter's
/// behaviour is then byte-identical — and non-null only under <c>POST /fixtures/{name}/record</c>.</summary>
internal interface IFixtureRecorder
{
    /// <summary>A <c>goto</c> settled on <paramref name="url"/>: snapshots the landing DOM as a state carrying that
    /// <c>gotoUrl</c> (the first navigation is the initial state).</summary>
    ValueTask OnNavigatedAsync(string url, IPageHandle page, CancellationToken ct);

    /// <summary>A <c>click</c> is about to fire: settles the current DOM as the transition's from-state and opens a
    /// transition on <paramref name="cssSelector"/>. A non-CSS selector (<paramref name="cssSelector"/> null) or an
    /// in-frame click (<paramref name="inFrame"/>) is unrecordable in v1 and fails the record run classified.</summary>
    ValueTask OnClickAsync(string? cssSelector, bool inFrame, IPageHandle page, CancellationToken ct);

    /// <summary>Arms the emit a <c>waitForRequest</c> trigger's click records (so replay's postback wait matches).</summary>
    void SetPendingEmit(string urlPrefix, string? method);

    /// <summary>Disarms the pending emit once the <c>waitForRequest</c> completes.</summary>
    void ClearPendingEmit();

    /// <summary>Fails the record run classified for an operation the recorder cannot capture in v1 (a <c>download</c>),
    /// naming it — so an unrecordable session is a clear typed failure, never a silently incomplete set.</summary>
    void RejectUnrecordable(string operation);

    /// <summary>Discards everything banked so far — called at the start of each retry attempt so a re-run records only
    /// the final successful pass, not a merge of a failed attempt's states/transitions.</summary>
    void Reset();

    /// <summary>The run succeeded: settles the final DOM (closing the last open transition) so the manifest is complete.</summary>
    ValueTask FinalizeAsync(IPageHandle page, CancellationToken ct);
}
