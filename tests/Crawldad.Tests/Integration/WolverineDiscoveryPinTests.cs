using System.Reflection;
using Crawldad.Api;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Crawldad.Tests.Integration;

/// <summary>Configuration guard for <c>HostConfiguration.ConfigureWolverine</c>'s
/// <c>options.ApplicationAssembly = typeof(HostConfiguration).Assembly</c>.
/// <para><c>AddWolverine</c> constructs <c>WolverineOptions</c> with no assembly name, leaving
/// <c>ApplicationAssembly</c> null until bootstrap. Left null, Wolverine walks the call stack for the first assembly that
/// is not <c>System*</c>/<c>Microsoft*</c>/a test runner/<c>JasperFx*</c>/dynamic — falling back to
/// <c>Assembly.GetEntryAssembly()</c>, i.e. testhost — and caches the answer in the <b>process-wide static</b>
/// <c>WolverineOptions.RememberedApplicationAssembly</c> that every later host in the process reuses; a divergent host
/// only gets a logged warning (GH-3521). In a test process that is order-dependent, and it decides everything:
/// <c>HandlerGraph</c> and Wolverine.Http's <c>HttpGraph</c> both discover from the single collection it fills
/// (<c>HandlerGraph.Discovery.Assemblies</c>, surfaced as <c>options.Assemblies</c>), so one wrong answer is zero message
/// handlers <b>and</b> zero HTTP endpoints. CI run 33843310346 hit precisely that on a docs-only PR whose product code
/// was identical to two green runs: <c>IndeterminateRoutesException</c> for <see cref="StartRun"/>, 383 failures, every
/// Wolverine.Http route 404. The suite is serial, so it was stack-walk ordering nondeterminism, not a thread race.</para>
/// <para>Nothing else in the suite pins this: when the heuristic happens to land on <c>Crawldad.Api</c> — which is the
/// usual outcome — every other test is green and the latent nondeterminism is invisible. A pure read of the compiled
/// configuration, no async wait.</para></summary>
[Collection(IntegrationCollection.Name)]
public class WolverineDiscoveryPinTests(AppFixture fixture)
{
    private static Assembly ApiAssembly => typeof(HostConfiguration).Assembly;

    private WolverineRuntime Runtime =>
        fixture.Host.Services.GetRequiredService<IWolverineRuntime>().ShouldBeOfType<WolverineRuntime>();

    [Fact]
    public void The_application_assembly_is_pinned_to_the_api_and_not_stack_walked() =>
        Runtime.Options.ApplicationAssembly.ShouldBeSameAs(ApiAssembly);

    [Fact]
    public void Discovery_scans_the_api_assembly()
    {
        // options.Assemblies IS HandlerGraph.Discovery.Assemblies — the one collection the ApplicationAssembly setter
        // fills and that HttpGraph reads too, so asserting it covers BOTH graphs at their shared root. (HttpGraph itself
        // is built by MapWolverineEndpoints and is not resolvable from DI, so there is no clean way to enumerate HTTP
        // routes here; the suite's Alba `POST /runs` 202 coverage exercises that side end to end.)
        Runtime.Options.Assemblies.ShouldContain(ApiAssembly);

        // The failure mode is discovery pointing at the TEST assembly (or testhost) instead — which is what the stack
        // walk actually produced in CI. It must not be a discovery root.
        Runtime.Options.Assemblies.ShouldNotContain(typeof(WolverineDiscoveryPinTests).Assembly);
    }

    [Theory]
    [InlineData(typeof(StartRun))]          // the exact message CI reported as having no subscribers or local handlers
    [InlineData(typeof(ExecuteRun))]
    [InlineData(typeof(PromoteQueued))]
    [InlineData(typeof(QueueWaitDeadline))]
    [InlineData(typeof(RunFinalized))]
    [InlineData(typeof(DeliverWebhook))]
    public void Every_durable_message_resolved_a_handler_chain_from_that_assembly(Type messageType) =>
        // The consequence, not just the setting: a wrong pin leaves the handler graph empty and every one of these
        // throws IndeterminateRoutesException at publish time instead.
        Runtime.Handlers.ChainFor(messageType).ShouldNotBeNull();
}
