using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Contracts.Fixtures;
using Crawldad.Contracts.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace Crawldad.Api.Features.Fixtures;

/// <summary><c>POST /fixtures/{name}/record</c>: execute a payload against its own configured backend while banking each
/// settled page state and interaction into the named tenant fixture set, so a later <c>POST /runs</c> can replay against
/// it deterministically. Runs the interpreter inline (a record-once setup step, not a queued run) — a failed record run
/// is HTTP 200 with the classified failure and no set persisted, exactly like a failed <c>POST /runs</c>. On success the
/// set is stored (replacing any prior set of that name) and the recorded summary + the run's own result are returned.</summary>
public static class RecordFixtureEndpoint
{
    [WolverinePost("/fixtures/{name}/record")]
    public static async Task<IResult> Handle(
        string name,
        RecordFixtureRequest request,
        IFixtureStore store,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        ISecretStoreRegistry secretStores,
        [FromServices] TenantContext tenant,
        IOptions<RunLimitsOptions> limitsOptions,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!FixtureNameRules.IsValidName(name))
        {
            return FixtureProblems.InvalidName();
        }

        var input = JsonValues.FromJson(request.Inputs) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        // Scrub every persisted manifest URL with the SAME credential redaction the run timeline applies to a Navigated
        // URL (exact registered secrets + apiKey/token/signingKey params) — the run's secret scope is live only here, so a
        // secret-bearing goto/postback URL is redacted before it can land in the FixtureSet doc or be read back via GET.
        var recorder = new FixtureRecorder(scrubber.Scrub);
        var runId = Guid.NewGuid();

        // The recorder threads through the interpreter exactly like the synchronous run path (its own secret scope spans
        // the connect + program), but with no observer/screenshots — recording is a self-contained inline pass.
        using var secretHandle = secretScope.Begin();
        var outcome = await new RunInterpreter(
            request.Payload, input, registry, sinks, clock, tenant.TenantId,
            limits: limitsOptions.Value.ToRunLimits(), secretStores: secretStores, secretScope: secretScope, recorder: recorder)
            .RunAsync(ct);

        if (outcome.Status != RunStatus.Succeeded)
        {
            // A record run that failed (a divergence, an unrecordable operation, or any run failure) persists no set. A
            // record run never cancels (no observer), so a non-success is always a Failed outcome carrying a failure.
            var failure = RunEventScrubber.ScrubFailure(outcome.Failure!, scrubber);
            return Results.Ok(new RecordFixtureResponse(runId, outcome.Status, null, null, failure, outcome.Stats));
        }

        RecordedFixture recorded;
        try
        {
            recorded = recorder.Build();
        }
        catch (CrawldadFailureException ex)
        {
            // The run succeeded but recorded nothing replayable (e.g. it never navigated) — a classified 200 failure, not a 500.
            return Results.Ok(new RecordFixtureResponse(
                runId, RunStatus.Failed, null, null,
                new RunFailureDetail("terminal", ex.Code, ex.Message, new RunStepRef(0, "record")), outcome.Stats));
        }

        var set = new FixtureSet
        {
            Id = name,
            ManifestJson = recorded.ManifestJson,
            Pages = new Dictionary<string, string>(recorded.Pages, StringComparer.Ordinal),
            PageCount = recorded.PageCount,
            TransitionCount = recorded.TransitionCount,
            TotalBytes = recorded.TotalBytes,
            RunId = runId,
            CreatedAt = clock.GetUtcNow(),
        };
        var summary = await store.SaveAsync(tenant.TenantId, set, ct);

        return Results.Ok(new RecordFixtureResponse(
            runId, RunStatus.Succeeded, summary, scrubber.ScrubJson(outcome.Result), null, outcome.Stats));
    }
}
