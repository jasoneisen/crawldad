using Crawldad.Contracts.Payloads;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Features.Payloads;

/// <summary>Shared <c>400</c> responses for the payload mutation endpoints, surfaced in the uniform
/// <see cref="PayloadValidationProblem"/> shape every <c>/payloads</c> endpoint returns.</summary>
internal static class PayloadProblems
{
    /// <summary>The payload is archived and cannot be revised, renamed, or re-archived.</summary>
    public static IResult Archived() => Results.BadRequest(new PayloadValidationProblem(
        [new PayloadValidationError("", "payload_archived", "the payload is archived and cannot be modified")]));
}
