using System.Net;
using System.Text;
using System.Text.Json;
using Crawldad.Portal.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for <see cref="PostmarkEmailSender"/> over a fake HTTP handler (no socket). A success POSTs a
/// well-formed Postmark request (endpoint, server-token header, PascalCase body carrying the code); a non-2xx response
/// and a transport failure each <b>throw</b> (fail-closed, the contract the OTP flow relies on) and log only
/// non-sensitive metadata (HTTP status + Postmark error code) — never the recipient, the token, or the code. The sender
/// never branches on the recipient, so it is inherently enumeration-safe.</summary>
public class PostmarkEmailSenderTests
{
    private const string _token = "pm-test-token";
    private const string _from = "noreply@crawldad.dev";
    private const string _recipient = "user@example.com";
    private const string _code = "ABC234";

    private static (PostmarkEmailSender Sender, CollectingLogger<PostmarkEmailSender> Logger) SenderFor(HttpMessageHandler handler)
    {
        var options = Options.Create(new PostmarkEmailOptions { ServerToken = _token, FromAddress = _from, MessageStream = "outbound" });
        var logger = new CollectingLogger<PostmarkEmailSender>();
        return (new PostmarkEmailSender(new StubHttpClientFactory(handler), options, logger), logger);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Sends_a_well_formed_postmark_request_and_succeeds_without_logging()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"ErrorCode\":0,\"Message\":\"OK\",\"MessageID\":\"id-1\"}"));
        var (sender, logger) = SenderFor(handler);

        await sender.SendOtpCodeAsync(_recipient, _code, CancellationToken.None);

        handler.Method.ShouldBe(HttpMethod.Post);
        handler.Path.ShouldBe("/email");                       // POST https://api.postmarkapp.com/email
        handler.ServerToken.ShouldBe(_token);                  // X-Postmark-Server-Token header

        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        root.GetProperty("From").GetString().ShouldBe(_from);
        root.GetProperty("To").GetString().ShouldBe(_recipient);
        root.GetProperty("Subject").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("TextBody").GetString()!.ShouldContain(_code); // the code rides the body, never a log
        root.GetProperty("MessageStream").GetString().ShouldBe("outbound");

        logger.Entries.ShouldBeEmpty(); // nothing logged on success — no PII, no code
    }

    [Fact]
    public async Task A_non_2xx_response_throws_and_logs_only_status_and_error_code()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.UnprocessableEntity, "{\"ErrorCode\":406,\"Message\":\"Inactive recipient\"}"));
        var (sender, logger) = SenderFor(handler);

        var ex = await Should.ThrowAsync<EmailDeliveryException>(
            () => sender.SendOtpCodeAsync(_recipient, _code, CancellationToken.None));

        ex.Message.ShouldContain("422");
        ex.Message.ShouldContain("406");

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("422");
        entry.Message.ShouldContain("406");
        entry.Message.ShouldNotContain(_recipient); // no recipient PII at warning+
        entry.Message.ShouldNotContain(_token);     // never the server token
        entry.Message.ShouldNotContain(_code);      // never the code
    }

    [Fact]
    public async Task A_non_2xx_response_with_an_unparseable_body_still_throws_with_an_unknown_error_code()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>502 Bad Gateway</html>", Encoding.UTF8, "text/html"),
        });
        var (sender, logger) = SenderFor(handler);

        var ex = await Should.ThrowAsync<EmailDeliveryException>(
            () => sender.SendOtpCodeAsync(_recipient, _code, CancellationToken.None));

        ex.Message.ShouldContain("502");
        ex.Message.ShouldContain("unknown"); // body wasn't Postmark JSON → no numeric error code to report
        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task A_non_2xx_body_that_is_the_json_null_literal_reports_an_unknown_error_code()
    {
        // A well-formed but content-free body (the JSON literal `null`) parses to a null document — the error-code read
        // must short-circuit to "unknown" rather than dereferencing null.
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "null"));
        var (sender, _) = SenderFor(handler);

        var ex = await Should.ThrowAsync<EmailDeliveryException>(
            () => sender.SendOtpCodeAsync(_recipient, _code, CancellationToken.None));

        ex.Message.ShouldContain("503");
        ex.Message.ShouldContain("unknown");
    }

    [Fact]
    public async Task A_transport_failure_throws_wrapping_the_cause_and_logs_without_pii()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var (sender, logger) = SenderFor(handler);

        var ex = await Should.ThrowAsync<EmailDeliveryException>(
            () => sender.SendOtpCodeAsync(_recipient, _code, CancellationToken.None));

        ex.InnerException.ShouldBeOfType<HttpRequestException>();
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldNotContain(_recipient);
        entry.Message.ShouldNotContain(_token);
    }

    [Fact]
    public async Task The_server_token_never_appears_in_the_thrown_exception()
    {
        // 401 / ErrorCode 10 is Postmark's "invalid server token" — precisely the failure most likely to tempt echoing
        // the token. Prove neither the message nor the full ToString() (which includes inner exceptions) carries it.
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Unauthorized, "{\"ErrorCode\":10,\"Message\":\"No Account token found\"}"));
        var (sender, _) = SenderFor(handler);

        var ex = await Should.ThrowAsync<EmailDeliveryException>(
            () => sender.SendOtpCodeAsync(_recipient, _code, CancellationToken.None));

        ex.Message.ShouldNotContain(_token);
        ex.ToString().ShouldNotContain(_token);
    }

    [Fact]
    public async Task Behaves_identically_regardless_of_the_recipient_so_it_cannot_enumerate_accounts()
    {
        // The sender takes no branch on the address: a "known" and an "unknown" recipient produce the same request
        // shape and the same success. (The observable enumeration-parity of the whole flow is asserted at the
        // PortalAuthService level; this pins the property at the sender.)
        var first = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"ErrorCode\":0,\"Message\":\"OK\"}"));
        var second = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"ErrorCode\":0,\"Message\":\"OK\"}"));
        var (senderA, _) = SenderFor(first);
        var (senderB, _) = SenderFor(second);

        await senderA.SendOtpCodeAsync("known@example.com", _code, CancellationToken.None);
        await senderB.SendOtpCodeAsync("unknown@example.com", _code, CancellationToken.None);

        first.Method.ShouldBe(second.Method);
        first.Path.ShouldBe(second.Path);
        first.ServerToken.ShouldBe(second.ServerToken);
        // Bodies differ only by the To field (each recipient's own address) — the subject/stream are identical.
        static (string? Subject, string? Stream) SubjectStream(string body)
        {
            using var doc = JsonDocument.Parse(body);
            return (doc.RootElement.GetProperty("Subject").GetString(), doc.RootElement.GetProperty("MessageStream").GetString());
        }

        SubjectStream(first.Body!).ShouldBe(SubjectStream(second.Body!));
    }

    /// <summary>A one-handler factory that presets Postmark's base address, exactly as <see cref="EmailModule"/> does
    /// on the named client — so the sender's relative <c>email</c> path resolves against it.</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = PostmarkEmailSender.ApiBaseAddress };
    }

    /// <summary>Records the method, path, server-token header, and body of the single request, then returns a scripted
    /// response — so a test can assert the wire shape after the call.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string? ServerToken { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            ServerToken = request.Headers.TryGetValues("X-Postmark-Server-Token", out var values) ? string.Join(",", values) : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw ex;
    }
}
