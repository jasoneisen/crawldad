using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The <c>fill.secret</c> action, white-box against the record/replay fake: the secret is resolved from the vault
/// AT ACTION TIME, registered into the run's <see cref="IRunSecretScope"/> for scrubbing, and typed straight into the field —
/// never bound to a scope var or routed through an expression. The <c>Filled</c> event carries only the ref name, never the secret.</summary>
public class RunSecretFillTests
{
    private const string _capHome = "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement";
    private const string _dateField = "#ctl00_PlaceHolderMain_generalSearchForm_txtGSStartDate";

    private static string Inputs(string extra = "") =>
        $$"""{ "backend": { "adapter": "fake", "options": { "fixture": "caphome-search" } }{{extra}} }""";

    private static string Payload(string steps, string result = "null") =>
        $$"""
        { "name": "login", "config": { "backend": "input.backend" },
          "inputs": { "backend": { "type": "backend" }, "loginPw": { "type": "secretRef" } },
          "steps": {{steps}}, "result": "{{result}}" }
        """;

    private static string FillSteps(string secretRef = "input.loginPw") =>
        $$"""[ { "goto": { "url": "{{_capHome}}" } }, { "fill": { "selector": "{{_dateField}}", "secret": "{{secretRef}}" } } ]""";

    private static SingleSecretVaultRegistry Vault(string reference, string secret) =>
        new(
            SecretVaults.Config,
            new MapSecretStore(new Dictionary<string, string>(StringComparer.Ordinal) { [reference] = secret }));

    [Fact]
    public async Task Resolves_registers_and_types_the_secret_into_the_field()
    {
        var scope = new AmbientRunSecretScope();
        var payload = Payload(FillSteps(), result: $"attr({{ css: '{_dateField}' }}, 'value')");

        using (scope.Begin())
        {
            var (outcome, _, _) = await Runner.RunWithObserverAsync(
                payload, Inputs(""", "loginPw": "vault-ref" """), secretStores: Vault("vault-ref", "S3cr3tP@ss"), secretScope: scope);

            outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
            outcome.Result!.Value.GetString().ShouldBe("S3cr3tP@ss");
            scope.FormSecrets.ShouldContain("S3cr3tP@ss");             // registered for exact-match scrubbing (the lower form floor)
        }
    }

    [Fact]
    public async Task The_trace_event_carries_the_ref_name_never_the_secret()
    {
        const string Secret = "SUP3RSECRET-canary-value";
        var scope = new AmbientRunSecretScope();

        using (scope.Begin())
        {
            var (outcome, observer, _) = await Runner.RunWithObserverAsync(
                Payload(FillSteps()), Inputs(""", "loginPw": "vault-ref" """), secretStores: Vault("vault-ref", Secret), secretScope: scope);

            outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
            observer.Events.OfType<Filled>().ShouldHaveSingleItem().Target.ShouldBe("secret:loginPw");
            // The resolved secret is structurally absent from EVERY emitted trace event (not merely scrubbed after the fact).
            observer.Events.ShouldAllBe(e => !JsonSerializer.Serialize(e, e.GetType()).Contains(Secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_fill_secret_re_resolves_from_the_vault_on_a_checkpoint_resume()
    {
        // A checkpoint before the fill: on a resumed segment the fill.secret re-executes, so it must re-resolve from the
        // vault rather than restore a persisted value — the resolved secret is never in the checkpoint state or the ResumeState.
        const string Secret = "R3solvedOnResume-canary";
        var vault = new CountingVault(Secret);
        var registry = new SingleSecretVaultRegistry(SecretVaults.Config, vault);
        var inputs = Inputs(""", "loginPw": "vault-ref" """);
        var payload = $$"""
            { "name": "login", "config": { "backend": "input.backend" },
              "inputs": { "backend": { "type": "backend" }, "loginPw": { "type": "secretRef" } },
              "vars": { "i": 0 },
              "steps": [
                { "goto": { "url": "{{_capHome}}" } },
                { "loop": { "maxIterations": 3, "while": "i < 2", "do": [
                    { "checkpoint": { "name": "cp", "cursor": "i", "resume": [] } },
                    { "fill": { "selector": "{{_dateField}}", "secret": "input.loginPw" } },
                    { "set": { "var": "i", "value": "i + 1" } }
                ] } }
              ],
              "result": "'done'" }
            """;

        // Fresh run: resolves the fill from the vault and yields the checkpoints; a snapshot holds the loop cursor/vars, NOT the secret.
        var freshScope = new AmbientRunSecretScope();
        ResumeState resume;
        using (freshScope.Begin())
        {
            var (fresh, observer, _) = await Runner.RunWithObserverAsync(payload, inputs, secretStores: registry, secretScope: freshScope);
            fresh.Status.ShouldBe(RunStatus.Succeeded, fresh.Failure?.Code);
            vault.Calls.ShouldBeGreaterThan(0);
            var cp = observer.Checkpoints[0];
            JsonSerializer.Serialize(cp).ShouldNotContain(Secret);
            resume = new ResumeState(cp.Name, cp.Sequence, cp.StepIndex, cp.Cursor, cp.Vars);
        }

        // Resume with a reset vault + a fresh scope: the fill.secret in the resumed segment MUST re-resolve from the vault —
        // it cannot be restored from durable state, because the ResumeState it restores from carries no secret.
        vault.Reset();
        JsonSerializer.Serialize(resume).ShouldNotContain(Secret);
        var resumeScope = new AmbientRunSecretScope();
        using (resumeScope.Begin())
        {
            var (resumed, observer2, _) = await Runner.RunWithObserverAsync(payload, inputs, resume: resume, secretStores: registry, secretScope: resumeScope);
            resumed.Status.ShouldBe(RunStatus.Succeeded, resumed.Failure?.Code);
            vault.Calls.ShouldBeGreaterThan(0);            // (a) RE-RESOLVED from the vault on resume
            resumeScope.FormSecrets.ShouldContain(Secret); // registered fresh in the resumed run's scope
            observer2.Checkpoints.ShouldAllBe(c => !JsonSerializer.Serialize(c).Contains(Secret, StringComparison.Ordinal)); // (b) still no secret at rest
        }
    }

    [Fact]
    public async Task A_secretRef_input_is_absent_from_the_expression_scope()
    {
        // Even on the inline path (no save-time walk), a secretRef is never placed in the eval scope, so an expression
        // reading it resolves to null — the secret's reference cannot be surfaced through `input`.
        var scope = new AmbientRunSecretScope();
        var payload = Payload("[ ]", result: "coalesce(input.loginPw, '<absent>')");

        using (scope.Begin())
        {
            var (outcome, _, _) = await Runner.RunWithObserverAsync(
                payload, Inputs(""", "loginPw": "vault-ref" """), secretStores: Vault("vault-ref", "S"), secretScope: scope);

            outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
            outcome.Result!.Value.GetString().ShouldBe("<absent>");
        }
    }

    [Theory]
    [InlineData("")]                         // declared but not supplied → no reference value at all
    [InlineData(""", "loginPw": "" """)]     // supplied but empty → an empty reference is not resolvable
    public async Task A_missing_or_empty_secretRef_input_is_a_terminal_fail_fast(string extra)
    {
        var scope = new AmbientRunSecretScope();
        using (scope.Begin())
        {
            var (outcome, _, _) = await Runner.RunWithObserverAsync(
                Payload(FillSteps()), Inputs(extra), secretStores: Vault("vault-ref", "S"), secretScope: scope);

            outcome.Status.ShouldBe(RunStatus.Failed);
            outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.SecretRefMissing);
        }
    }

    [Fact]
    public async Task An_unresolvable_secretRef_fails_fast_naming_only_the_reference()
    {
        const string Secret = "vaulted-canary-secret";
        var scope = new AmbientRunSecretScope();
        using (scope.Begin())
        {
            // The supplied reference has no secret in the vault (the vault holds a different key).
            var (outcome, _, _) = await Runner.RunWithObserverAsync(
                Payload(FillSteps()), Inputs(""", "loginPw": "absent-ref" """), secretStores: Vault("other-ref", Secret), secretScope: scope);

            outcome.Status.ShouldBe(RunStatus.Failed);
            outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.SecretUnresolved);
            outcome.Failure.Message.ShouldContain("absent-ref");     // names the safe reference
            outcome.Failure.Message.ShouldNotContain(Secret);        // never the secret
        }
    }

    [Theory]
    [InlineData("input.backend")]         // a valid input reference, but not a secretRef-typed input
    [InlineData("input.loginPw + 'x'")]   // not a bare reference at all (a computed expression)
    public async Task A_fill_secret_that_is_not_a_secretRef_reference_is_terminal_at_run_time(string secretRef)
    {
        // Inline payloads skip the save-time walk, so the interpreter enforces the rule at action time.
        var scope = new AmbientRunSecretScope();
        using (scope.Begin())
        {
            var (outcome, _, _) = await Runner.RunWithObserverAsync(
                Payload(FillSteps(secretRef)), Inputs(""", "loginPw": "vault-ref" """), secretStores: Vault("vault-ref", "S"), secretScope: scope);

            outcome.Status.ShouldBe(RunStatus.Failed);
            outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.FillSecretNotSecretRef);
        }
    }

    [Fact]
    public async Task A_fill_secret_without_a_configured_vault_is_terminal()
    {
        var scope = new AmbientRunSecretScope();
        using (scope.Begin())
        {
            // secretStores omitted (null): the defensive terminal path (both real run paths always wire the registry).
            var (outcome, _, _) = await Runner.RunWithObserverAsync(
                Payload(FillSteps()), Inputs(""", "loginPw": "vault-ref" """), secretScope: scope);

            outcome.Status.ShouldBe(RunStatus.Failed);
            outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.UnknownSecretVault);
        }
    }

    [Fact]
    public async Task A_fill_secret_whose_vault_kind_is_unregistered_is_terminal()
    {
        // The registry is present but has no `config` adapter (e.g. a not-yet-built vault kind) — a clean unknown_secret_vault.
        var registry = new SingleSecretVaultRegistry(
            "azure-keyvault", new MapSecretStore(new Dictionary<string, string>(StringComparer.Ordinal) { ["vault-ref"] = "S" }));
        var scope = new AmbientRunSecretScope();
        using (scope.Begin())
        {
            var (outcome, _, _) = await Runner.RunWithObserverAsync(
                Payload(FillSteps()), Inputs(""", "loginPw": "vault-ref" """), secretStores: registry, secretScope: scope);

            outcome.Status.ShouldBe(RunStatus.Failed);
            outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.UnknownSecretVault);
        }
    }
}
