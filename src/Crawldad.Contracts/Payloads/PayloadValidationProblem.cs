namespace Crawldad.Contracts.Payloads;

/// <summary>
/// One reason a payload was rejected at save (§12): a JSON-Pointer <see cref="Path"/> into the document, a stable
/// <see cref="Code"/> (a JSON-Schema keyword, or a semantic slug such as <c>undefined_reference</c>/<c>unknown_function</c>/
/// <c>wrong_arity</c>/<c>syntax_error</c>), and a human-readable <see cref="Message"/>.
/// </summary>
/// <param name="Path">JSON Pointer to the offending location (empty for the document root).</param>
/// <param name="Code">The stable failure slug.</param>
/// <param name="Message">The human-readable description.</param>
public sealed record PayloadValidationError(string Path, string Code, string Message);

/// <summary>
/// The <c>POST /payloads</c> 400 body: the full list of validation errors (§12 — a malformed payload never becomes
/// executable). Either a JSON Schema violation or a semantic-pass failure produces one or more of these.
/// </summary>
/// <param name="Errors">Every validation error found (path + code + message per error).</param>
public sealed record PayloadValidationProblem(IReadOnlyList<PayloadValidationError> Errors);
