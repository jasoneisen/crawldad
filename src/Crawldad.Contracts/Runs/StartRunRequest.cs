using System.Text.Json;

namespace Crawldad.Contracts.Runs;

/// <summary>
/// The <c>POST /runs</c> body and Wolverine command (§10, Deliverable 4): an <b>inline</b> payload document plus the
/// run inputs bound to its declared <c>inputs</c>. Both are carried as raw <see cref="JsonElement"/> — the payload is
/// interpreted, not modelled as a DTO (its node polymorphism lives in the interpreter), and inputs are converted to
/// the run's value model.
/// </summary>
/// <param name="Payload">The inline Crawldad payload object (<c>crawldad</c>/<c>inputs</c>/<c>config</c>/<c>vars</c>/<c>steps</c>/<c>result</c>).</param>
/// <param name="Inputs">The input bindings object. A <c>backend</c> input takes the wire shape
/// <c>{ "adapter": "fake", "options": { "fixture": "caphome-search" } }</c> (optionally a <c>credentialRef</c>); a
/// missing/undefined value means no inputs.</param>
public sealed record StartRunRequest(JsonElement Payload, JsonElement Inputs);
