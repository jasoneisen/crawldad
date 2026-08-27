using System.Net;
using System.Text.Json;
using Crawldad.Client;
using Crawldad.Contracts.Browsers;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Webhooks;

namespace Crawldad.Tests.Client;

/// <summary>Verifies the client maps every API error shape to the right typed exception — a <see cref="RunRejection"/>
/// body, a <see cref="PayloadValidationProblem"/>, an RFC 7807 validation problem, a bare 401/404, and the opaque
/// problem-details / non-JSON fallbacks — never a raw <see cref="HttpRequestException"/> for an API-level rejection.</summary>
public class CrawldadClientErrorMappingTests
{
    private static JsonElement JsonElementOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly JsonElement _payload = JsonElementOf("""{ "name": "x" }""");

    [Fact]
    public async Task Missing_key_is_a_401_unauthorized()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.Unauthorized)));

        var ex = await Should.ThrowAsync<CrawldadUnauthorizedException>(() => client.GetRunAsync(Guid.NewGuid()));
        ex.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task Unknown_run_is_a_404_not_found_with_a_default_message()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.NotFound)));

        var ex = await Should.ThrowAsync<CrawldadNotFoundException>(() => client.GetRunAsync(Guid.NewGuid()));
        ex.StatusCode.ShouldBe(404);
        ex.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_404_with_a_text_body_surfaces_that_body()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.Text(HttpStatusCode.NotFound, "screenshot no longer available")));

        var ex = await Should.ThrowAsync<CrawldadNotFoundException>(() => client.GetRunScreenshotAsync(Guid.NewGuid(), "abc.png"));
        ex.Message.ShouldBe("screenshot no longer available");
    }

    [Fact]
    public async Task A_400_run_rejection_maps_to_run_rejected()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(
            () => ClientTestHarness.Json(new RunRejection("unknown_payload", "no such payload"), HttpStatusCode.BadRequest)));

        var ex = await Should.ThrowAsync<CrawldadRunRejectedException>(() => client.CreateInlineRunAsync(_payload));
        ex.StatusCode.ShouldBe(400);
        ex.Code.ShouldBe("unknown_payload");
        ex.Rejection.Message.ShouldBe("no such payload");
    }

    [Fact]
    public async Task A_429_queue_depth_rejection_maps_to_run_rejected()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(
            () => ClientTestHarness.Json(new RunRejection("queue_depth_exceeded", "full"), HttpStatusCode.TooManyRequests)));

        var ex = await Should.ThrowAsync<CrawldadRunRejectedException>(() => client.CreateInlineRunAsync(_payload));
        ex.StatusCode.ShouldBe(429);
        ex.Code.ShouldBe("queue_depth_exceeded");
    }

    [Fact]
    public async Task A_409_on_erase_maps_to_run_rejected()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(
            () => ClientTestHarness.Json(new RunRejection("run_still_active", "cancel it first"), HttpStatusCode.Conflict)));

        var ex = await Should.ThrowAsync<CrawldadRunRejectedException>(() => client.EraseRunAsync(Guid.NewGuid()));
        ex.StatusCode.ShouldBe(409);
        ex.Code.ShouldBe("run_still_active");
    }

    [Fact]
    public async Task A_single_payload_validation_error_maps_to_payload_invalid()
    {
        var problem = new PayloadValidationProblem([new PayloadValidationError("/steps", "syntax_error", "bad step")]);
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.Json(problem, HttpStatusCode.BadRequest)));

        var ex = await Should.ThrowAsync<CrawldadPayloadInvalidException>(() => client.SavePayloadAsync(_payload));
        ex.Errors.ShouldHaveSingleItem().Code.ShouldBe("syntax_error");
        ex.Message.ShouldContain("bad step");
    }

    [Fact]
    public async Task Multiple_payload_validation_errors_map_with_a_count_message()
    {
        var problem = new PayloadValidationProblem(
        [
            new PayloadValidationError("/a", "unknown_function", "m1"),
            new PayloadValidationError("/b", "wrong_arity", "m2"),
        ]);
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.Json(problem, HttpStatusCode.BadRequest)));

        var ex = await Should.ThrowAsync<CrawldadPayloadInvalidException>(() => client.SavePayloadAsync(_payload));
        ex.Errors.Count.ShouldBe(2);
        ex.Message.ShouldContain("2 validation errors");
    }

    [Fact]
    public async Task An_rfc7807_validation_problem_maps_to_validation_exception()
    {
        const string Body = """{"type":"about:blank","title":"One or more validation errors occurred.","status":400,"errors":{"name":["bad slug"]}}""";
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, Body)));

        var ex = await Should.ThrowAsync<CrawldadValidationException>(
            () => client.RegisterWebhookAsync("Bad", new RegisterWebhookRequest("https://x", "secret0123456789")));
        ex.StatusCode.ShouldBe(400);
        ex.Errors["name"].ShouldContain("bad slug");
        ex.Message.ShouldContain("bad slug");
    }

    [Fact]
    public async Task An_empty_validation_problem_still_maps_with_a_default_message()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, """{"errors":{}}""")));

        var ex = await Should.ThrowAsync<CrawldadValidationException>(
            () => client.RegisterBrowserAsync("n", new RegisterBrowserRequest("browserless", "apiKey", "secret0123456789")));
        ex.Errors.ShouldBeEmpty();
        ex.Message.ShouldBe("The request failed validation.");
    }

    [Fact]
    public async Task A_500_problem_details_with_detail_maps_to_the_api_exception_using_the_detail()
    {
        const string Body = """{"title":"Server Error","status":500,"detail":"the database was unreachable"}""";
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.InternalServerError, Body)));

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.GetQueueStatsAsync());
        ex.StatusCode.ShouldBe(500);
        ex.Message.ShouldBe("the database was unreachable");
        ex.ResponseBody.ShouldBe(Body);
    }

    [Fact]
    public async Task A_500_problem_details_with_only_a_title_uses_the_title()
    {
        const string Body = """{"title":"Server Error","status":500}""";
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.InternalServerError, Body)));

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.GetQueueStatsAsync());
        ex.Message.ShouldBe("Server Error");
    }

    [Fact]
    public async Task A_non_json_500_falls_back_to_a_generic_message_with_the_body()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.Text(HttpStatusCode.InternalServerError, "boom")));

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.GetQueueStatsAsync());
        ex.Message.ShouldContain("500");
        ex.ResponseBody.ShouldBe("boom");
    }

    [Fact]
    public async Task An_unrecognized_json_object_falls_back_to_the_api_exception()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, """{"foo":"bar"}""")));

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.GetQueueStatsAsync());
        ex.StatusCode.ShouldBe(400);
        ex.Message.ShouldContain("400");
    }

    [Fact]
    public async Task An_errors_field_that_is_a_scalar_falls_back_to_the_api_exception()
    {
        // "errors" present but neither an array (payload problem) nor an object (validation problem) — falls through.
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, """{"errors":"nope"}""")));

        await Should.ThrowAsync<CrawldadApiException>(() => client.GetQueueStatsAsync());
    }

    [Fact]
    public async Task A_non_object_json_body_falls_back_to_the_api_exception()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, "[1,2,3]")));

        await Should.ThrowAsync<CrawldadApiException>(() => client.GetQueueStatsAsync());
    }

    [Fact]
    public async Task An_empty_error_body_yields_a_null_response_body()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.InternalServerError)));

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.GetQueueStatsAsync());
        ex.ResponseBody.ShouldBeNull();
    }

    [Fact]
    public async Task A_success_with_a_null_body_is_a_clear_api_exception()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.OK, "null")));

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.GetQueueStatsAsync());
        ex.Message.ShouldContain("empty response body");
    }

    [Fact]
    public async Task An_accepted_run_start_with_a_null_body_is_a_clear_api_exception()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.Accepted, "null")));

        await Should.ThrowAsync<CrawldadApiException>(() => client.CreateInlineRunAsync(_payload, async: true));
    }

    [Fact]
    public async Task A_completed_run_start_with_a_null_body_is_a_clear_api_exception()
    {
        var client = ClientTestHarness.ClientFor(ClientTestHarness.Always(() => ClientTestHarness.JsonRaw(HttpStatusCode.OK, "null")));

        await Should.ThrowAsync<CrawldadApiException>(() => client.CreateInlineRunAsync(_payload));
    }
}
