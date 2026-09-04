using Crawldad.Api.Features.Runs;
using Marten.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Crawldad.Tests.Integration;

/// <summary>Configuration guard for <c>HostConfiguration.ConfigureWolverine</c>'s
/// <c>options.Policies.OnException&lt;StreamLockedException&gt;().RetryWithCooldown(…)</c>.
/// <para><see cref="RunQueue.TryClaimTerminalAsync"/> serialises a queued run's three competing terminal writers on the
/// run stream's exclusive lock. When that lock cannot be taken, Marten raises <see cref="StreamLockedException"/> — a
/// <c>MartenException</c>, unrelated to <c>JasperFx.ConcurrencyException</c>
/// (<c>Crawldad.Tests.Unit.StreamLockedExceptionContractTests</c>), so nothing generic maps it. Two of the three callers
/// are durable message handlers (<see cref="PromoteQueued"/>, <see cref="QueueWaitDeadline"/>); without this policy
/// Wolverine's zero-retry default dead-letters their envelope, and because <see cref="PromoteQueued"/> is re-triggered
/// only by a later run reaching terminal or by <c>RunRecoveryService</c> at boot, a single-slot tenant's queued run can
/// then sit unqueued-but-unpromoted until the next restart. Nothing else in the suite would go red if the line were
/// deleted — the failure needs a contended lock to appear at all — hence this test.</para>
/// <para>The third caller, <c>POST /runs/{id}/cancel</c>, is deliberately NOT covered by it: Wolverine.Http's
/// <c>HttpChain</c> does not implement <c>IWithFailurePolicies</c>, so an endpoint has no failure policies. That path
/// catches the exception itself and returns <c>409 run_claim_contended</c>
/// (<c>SlotQueueTests.A_cancel_that_cannot_take_the_queued_runs_stream_lock_is_a_409_not_a_500</c>).</para>
/// <para>A pure read of the compiled configuration — no async wait, no lock contention, no threads.</para></summary>
[Collection(IntegrationCollection.Name)]
public class StreamLockRetryPolicyTests(AppFixture fixture)
{
    // The exception as Marten actually builds it in Events.AppendExclusive's catch — constructed, never raced (#14).
    private static StreamLockedException Locked() => new(Guid.NewGuid(), new InvalidOperationException("held"));

    // `options.Policies` IS the WolverineOptions instance, and its IWithFailurePolicies.Failures is the GLOBAL
    // HandlerGraph.Failures collection — a policy registered there is combined with every chain's own rules at runtime,
    // so this is where a global OnException lands (it is NOT visible on an individual HandlerChain.Failures).
    private FailureRuleCollection GlobalFailureRules =>
        fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Options.Policies.Failures;

    [Fact]
    public void A_contended_stream_lock_is_retried_on_a_bounded_schedule_before_dead_lettering()
    {
        var rule = GlobalFailureRules.Where(r => r.Match.Matches(Locked())).ToList().ShouldHaveSingleItem();

        // Three attempts, then no further slot — so the fourth failure falls through to Wolverine's default
        // dead-letter rather than retrying forever against a lock something else is holding.
        rule.Select(slot => slot.Attempt).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void The_first_attempt_retries_rather_than_dead_lettering()
    {
        var rule = GlobalFailureRules.Where(r => r.Match.Matches(Locked())).ToList().ShouldHaveSingleItem();
        var envelope = new Envelope(new PromoteQueued()) { Attempts = 1 };

        rule.TryCreateContinuation(Locked(), envelope, out var continuation).ShouldBeTrue();

        // Description is Wolverine's own public diagnostic string for a continuation. Asserted loosely (does it RETRY?)
        // rather than on the exact rendering, so a wording change upstream cannot fail this, but swapping
        // RetryWithCooldown for a requeue/discard/dead-letter policy would.
        continuation.ShouldBeAssignableTo<IContinuationSource>()!.Description.ShouldContain("Retry", Case.Insensitive);
    }

    [Fact]
    public void The_policy_is_scoped_to_the_stream_lock_and_is_not_a_catch_all()
    {
        var rules = GlobalFailureRules.ToList();

        // An unrelated fault must still take Wolverine's default handling — a blanket retry would silently paper over
        // real bugs in every handler in the app.
        rules.ShouldNotContain(r => r.Match.Matches(new InvalidOperationException("unrelated")));

        // ...and the sibling arm stays unmapped on purpose: the OPTIMISTIC append conflict is retried in-place by
        // CancelRunEndpoint's own re-read loop, not by a Wolverine failure policy.
        rules.ShouldNotContain(r => r.Match.Matches(
            new JasperFx.Events.EventStreamUnexpectedMaxEventIdException(Guid.NewGuid(), aggregateType: null, expected: 1, actual: 2)));
    }

    [Theory]
    [InlineData(typeof(PromoteQueued))]     // strands a tenant's queue drain if its envelope is dead-lettered
    [InlineData(typeof(QueueWaitDeadline))] // strands a queued run past its max wait if its envelope is dead-lettered
    public void The_handlers_the_policy_protects_are_wired_as_local_queues(Type messageType) =>
        // The policy only matters because these two reach TryClaimTerminalAsync from a durable local queue, where
        // failure policies apply at all. If one ever stopped routing here, the retry above would be protecting nothing.
        fixture.Host.Services.GetRequiredService<IWolverineRuntime>()
            .Endpoints.LocalQueueForMessageType(messageType).ShouldNotBeNull();
}
