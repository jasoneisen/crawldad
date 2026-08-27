using System.Collections.ObjectModel;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;

namespace Crawldad.Client;

/// <summary>The base type for every error the Crawldad API surfaces to the client. Carries the HTTP
/// <see cref="StatusCode"/> so a caller can branch on it without catching individual subtypes. Concrete subtypes map
/// the API's typed rejection/problem bodies; a transport-level failure (DNS, socket) still surfaces as the underlying
/// <see cref="HttpRequestException"/>, never as one of these.</summary>
public class CrawldadException : Exception
{
    /// <summary>Initializes the exception with the HTTP status and message.</summary>
    /// <param name="statusCode">The HTTP status code the API returned.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    public CrawldadException(int statusCode, string message)
        : base(message) => StatusCode = statusCode;

    /// <summary>The HTTP status code the API returned (e.g. <c>400</c>, <c>404</c>, <c>429</c>).</summary>
    public int StatusCode { get; }
}

/// <summary>A request was made without a valid API key — HTTP <c>401</c>. Check <see cref="CrawldadClientOptions.ApiKey"/>.</summary>
public sealed class CrawldadUnauthorizedException : CrawldadException
{
    /// <summary>Initializes the unauthorized exception.</summary>
    /// <param name="statusCode">The HTTP status code (<c>401</c>).</param>
    /// <param name="message">A human-readable description.</param>
    public CrawldadUnauthorizedException(int statusCode, string message)
        : base(statusCode, message)
    {
    }
}

/// <summary>The addressed run, payload, webhook, fixture, browser, screenshot, or revision does not exist for this
/// tenant — HTTP <c>404</c>. There is deliberately no cross-tenant existence oracle, so a foreign resource is a
/// not-found exactly like an unknown one.</summary>
public sealed class CrawldadNotFoundException : CrawldadException
{
    /// <summary>Initializes the not-found exception.</summary>
    /// <param name="statusCode">The HTTP status code (<c>404</c>).</param>
    /// <param name="message">A human-readable description.</param>
    public CrawldadNotFoundException(int statusCode, string message)
        : base(statusCode, message)
    {
    }
}

/// <summary>A run control-surface rejection — a typed <see cref="RunRejection"/> body carrying a stable
/// <see cref="Code"/>. Distinct from a run <em>failure</em> (a started-then-faulted run is still HTTP <c>200</c>): this
/// is a request that never started a run. Seen as <c>400</c> (an unrunnable pinned-payload reference or
/// <c>inline_not_replayable</c>), <c>429</c> (<c>queue_depth_exceeded</c>), or <c>409</c> (<c>run_still_active</c> on
/// erase).</summary>
public sealed class CrawldadRunRejectedException : CrawldadException
{
    /// <summary>Initializes the rejection exception from the typed body.</summary>
    /// <param name="statusCode">The HTTP status code (<c>400</c>/<c>409</c>/<c>429</c>).</param>
    /// <param name="rejection">The typed rejection body.</param>
    public CrawldadRunRejectedException(int statusCode, RunRejection rejection)
        : base(statusCode, rejection.Message) => Rejection = rejection;

    /// <summary>The typed rejection body (code + message).</summary>
    public RunRejection Rejection { get; }

    /// <summary>The stable rejection slug, e.g. <c>queue_depth_exceeded</c>, <c>inline_not_replayable</c>,
    /// <c>run_still_active</c>, <c>unknown_payload</c>, <c>payload_archived</c>, <c>unknown_revision</c>.</summary>
    public string Code => Rejection.Code;
}

/// <summary>A managed-payload save/revise/rename/archive was rejected because the payload is invalid or archived —
/// HTTP <c>400</c> with a <see cref="PayloadValidationProblem"/>. <see cref="Errors"/> lists every JSON-Schema and
/// semantic-pass violation, each with a JSON-Pointer path and a stable code.</summary>
public sealed class CrawldadPayloadInvalidException : CrawldadException
{
    /// <summary>Initializes the payload-invalid exception from the typed problem body.</summary>
    /// <param name="statusCode">The HTTP status code (<c>400</c>).</param>
    /// <param name="problem">The validation problem body.</param>
    public CrawldadPayloadInvalidException(int statusCode, PayloadValidationProblem problem)
        : base(statusCode, Describe(problem)) => Problem = problem;

    /// <summary>The full validation problem body.</summary>
    public PayloadValidationProblem Problem { get; }

    /// <summary>The individual validation errors (path + code + message).</summary>
    public IReadOnlyList<PayloadValidationError> Errors => Problem.Errors;

    private static string Describe(PayloadValidationProblem problem) =>
        problem.Errors.Count == 1
            ? $"The payload was rejected: {problem.Errors[0].Message}"
            : $"The payload was rejected with {problem.Errors.Count} validation errors.";
}

/// <summary>A request body or route key failed boundary validation — HTTP <c>400</c> in the RFC 7807
/// <c>ValidationProblemDetails</c> shape (an <c>errors</c> map of field → messages). Covers a bad webhook/browser/
/// fixture name slug, an invalid webhook URL or secret, and unknown subscribed event types.</summary>
public sealed class CrawldadValidationException : CrawldadException
{
    /// <summary>Initializes the validation exception from the problem-details error map.</summary>
    /// <param name="statusCode">The HTTP status code (<c>400</c>).</param>
    /// <param name="errors">The field → messages validation map.</param>
    public CrawldadValidationException(int statusCode, IReadOnlyDictionary<string, string[]> errors)
        : base(statusCode, Describe(errors)) => Errors = errors;

    /// <summary>The validation errors keyed by field name (each value is the list of messages for that field).</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    private static string Describe(IReadOnlyDictionary<string, string[]> errors)
    {
        var first = errors.SelectMany(static kvp => kvp.Value).FirstOrDefault();
        return first is null ? "The request failed validation." : $"The request failed validation: {first}";
    }

    /// <summary>Builds a read-only error map from raw field/message pairs (used by the response mapper).</summary>
    internal static IReadOnlyDictionary<string, string[]> Freeze(IEnumerable<KeyValuePair<string, string[]>> errors) =>
        new ReadOnlyDictionary<string, string[]>(errors.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal));
}

/// <summary>Any other unexpected API response — an unmapped status (e.g. a <c>500</c> problem-details), or a body that
/// did not match one of the typed rejection shapes. <see cref="ResponseBody"/> carries the raw text for diagnostics.</summary>
public sealed class CrawldadApiException : CrawldadException
{
    /// <summary>Initializes the fallback API exception.</summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="message">A human-readable description (a problem-details title/detail when available).</param>
    /// <param name="responseBody">The raw response body, if any.</param>
    public CrawldadApiException(int statusCode, string message, string? responseBody)
        : base(statusCode, message) => ResponseBody = responseBody;

    /// <summary>The raw response body the API returned, when one was present.</summary>
    public string? ResponseBody { get; }
}
