namespace Crawldad.Contracts.Payloads;

/// <summary>One reason a payload was rejected at save: a JSON-Pointer <see cref="Path"/>, a stable <see cref="Code"/>
/// (a JSON-Schema keyword or semantic slug like <c>undefined_reference</c>/<c>unknown_function</c>/<c>wrong_arity</c>/<c>syntax_error</c>),
/// and a human-readable <see cref="Message"/>.</summary>
public sealed record PayloadValidationError(string Path, string Code, string Message);

/// <summary>The <c>POST /payloads</c> 400 body: the full list of validation errors — a malformed payload never becomes
/// executable. Either a JSON Schema violation or a semantic-pass failure produces one or more of these.</summary>
public sealed record PayloadValidationProblem(IReadOnlyList<PayloadValidationError> Errors);
