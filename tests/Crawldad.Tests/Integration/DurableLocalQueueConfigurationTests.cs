using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Crawldad.Tests.Integration;

/// <summary>Configuration guard for <c>HostConfiguration.ConfigureWolverine</c>'s
/// <c>options.Policies.UseDurableLocalQueues()</c>. Wolverine's local queues default to
/// <see cref="EndpointMode.BufferedInMemory"/>, so without that one line a queued envelope lives only in an in-process
/// channel: an unclean stop between enqueue and handle drops it silently, with no dead-letter row and no log. The policy
/// flips them to <see cref="EndpointMode.Durable"/>, backed by the Postgres message store that
/// <c>IntegrateWithWolverine()</c> registers.
/// <para>Nothing else in the suite pins the mode, so dropping the policy would stay green: the durability loss is masked
/// by <c>RunRecoveryService</c>, the startup scan that re-publishes <see cref="ExecuteRun"/> for every run still marked
/// running (see <c>DurableRunTests.Killed_run_resumes_from_the_last_checkpoint…</c>, whose own comment says the fresh
/// host's recovery scan re-drives it). Recovery covers a run that is mid-flight; it does not cover a lost
/// <see cref="PromoteQueued"/>, <see cref="QueueWaitDeadline"/>, <see cref="RunFinalized"/> or
/// <see cref="DeliverWebhook"/>. Hence this test — a pure read of the compiled configuration, no async wait.</para></summary>
[Collection(IntegrationCollection.Name)]
public class DurableLocalQueueConfigurationTests(AppFixture fixture)
{
    // The must-run cascade. Each of these is published post-commit onto a local queue, and each has a consequence
    // that nothing else recovers if the envelope evaporates.
    [Theory]
    [InlineData(typeof(StartRun))]           // starts the executor saga — losing one leaves an accepted run that never runs
    [InlineData(typeof(ExecuteRun))]         // drives the run — losing one strands it mid-flight until the recovery scan
    [InlineData(typeof(PromoteQueued))]      // drains the admission queue — losing one stalls it behind an already-freed slot
    [InlineData(typeof(QueueWaitDeadline))]  // expires a queued run — losing one leaves it queued past its max wait forever
    [InlineData(typeof(RunFinalized))]       // fans out the terminal signal — losing one drops the whole webhook fan-out
    [InlineData(typeof(DeliverWebhook))]     // one signed delivery — losing one drops it with no attempt row and no trace
    public void The_must_run_cascades_ride_durable_local_queues(Type messageType)
    {
        var runtime = fixture.Host.Services.GetRequiredService<IWolverineRuntime>();

        // LocalQueueForMessageType resolves the exact application queue this message routes to, already compiled at
        // host startup — so Mode is the durability the runtime actually applied, not a pre-policy default.
        var queue = runtime.Endpoints.LocalQueueForMessageType(messageType).ShouldNotBeNull();

        queue.Mode.ShouldBe(EndpointMode.Durable);
    }
}
