using JasperFx;
using JasperFx.Events;
using Marten.Exceptions;

namespace Crawldad.Tests.Unit;

/// <summary>The exception-hierarchy contract behind the queued-run claim path's error handling. Two different failures
/// can come out of a run stream's guarded append, and they are on <b>unrelated</b> branches of the exception tree:
/// <list type="bullet">
/// <item><description><see cref="EventStreamUnexpectedMaxEventIdException"/> — the OPTIMISTIC append conflict
/// (<c>AppendOptimistic</c>, the running-cancel breadcrumb in <c>CancelRunEndpoint</c>): a concurrent writer advanced the
/// stream past the pinned version. It derives from <see cref="ConcurrencyException"/>.</description></item>
/// <item><description><see cref="StreamLockedException"/> — the EXCLUSIVE append failing to take the lock at all
/// (<c>AppendExclusive</c>, <c>RunQueue.TryClaimTerminalAsync</c>): the <c>FOR UPDATE</c> read blocked past the command
/// timeout, or the transaction hit SQLSTATE 25P02. It derives from <see cref="MartenException"/> and <b>not</b> from
/// <see cref="ConcurrencyException"/>.</description></item>
/// </list>
/// That second fact is the trap: a <c>catch (ConcurrencyException)</c> — or any framework mapping keyed on it — silently
/// does not cover the exclusive-lock path, which is why <c>POST /runs/{id}/cancel</c> catches
/// <see cref="StreamLockedException"/> by name for its <c>409 run_claim_contended</c>, and why
/// <c>HostConfiguration.ConfigureWolverine</c> registers a retry policy on that exact type for the
/// <c>PromoteQueued</c>/<c>QueueWaitDeadline</c> handlers. If Marten ever reparents the type, both of those become
/// dead code — so this fails loudly rather than letting the coverage stay green over an unreachable branch.
/// <para>A pure reflection test: no host, no database, no threads (reference #14 — a race arm is asserted by
/// construction, never by racing).</para></summary>
public class StreamLockedExceptionContractTests
{
    [Fact]
    public void The_two_handled_exception_types_are_unrelated()
    {
        // Neither is assignable to the other, in either direction — they meet only at System.Exception.
        typeof(StreamLockedException).IsAssignableTo(typeof(EventStreamUnexpectedMaxEventIdException)).ShouldBeFalse();
        typeof(EventStreamUnexpectedMaxEventIdException).IsAssignableTo(typeof(StreamLockedException)).ShouldBeFalse();
    }

    [Fact]
    public void A_stream_lock_failure_is_NOT_a_JasperFx_concurrency_exception() =>
        // The trap. `catch (ConcurrencyException)` does not see this type.
        typeof(StreamLockedException).IsAssignableTo(typeof(ConcurrencyException)).ShouldBeFalse();

    [Fact]
    public void A_stream_lock_failure_IS_a_Marten_exception() =>
        typeof(StreamLockedException).IsAssignableTo(typeof(MartenException)).ShouldBeTrue();

    [Fact]
    public void An_optimistic_append_conflict_IS_a_JasperFx_concurrency_exception() =>
        // The sibling arm, asserted so the contrast above is a real distinction and not two vacuous truths.
        typeof(EventStreamUnexpectedMaxEventIdException).IsAssignableTo(typeof(ConcurrencyException)).ShouldBeTrue();

    [Fact]
    public void A_constructed_stream_lock_failure_names_its_stream_and_keeps_the_cause()
    {
        // The shape the tests construct to drive the handled branches (Marten builds exactly this in
        // Events.AppendExclusive's catch): the stream id in the message, the underlying database fault as InnerException.
        var streamId = Guid.NewGuid();
        var cause = new InvalidOperationException("held");

        var locked = new StreamLockedException(streamId, cause);

        locked.Message.ShouldContain(streamId.ToString());
        locked.InnerException.ShouldBeSameAs(cause);
    }
}
