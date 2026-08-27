using Crawldad.Portal.Auth;

namespace Crawldad.Tests.Portal;

public class OtpMessagesTests
{
    [Fact]
    public void Sent_message_names_the_address()
    {
        var message = OtpMessages.DescribeRequest(RequestCodeOutcome.Sent, "user@example.com");

        message.ShouldContain("user@example.com");
    }

    [Fact]
    public void Rate_limited_message_is_polite_and_address_agnostic()
    {
        var message = OtpMessages.DescribeRequest(RequestCodeOutcome.RateLimited, "user@example.com");

        message.ShouldNotContain("user@example.com");
        message.ShouldContain("few minutes");
    }

    [Fact]
    public void Every_failure_outcome_has_a_message()
    {
        VerifyOutcome[] failures =
            [VerifyOutcome.InvalidCode, VerifyOutcome.Expired, VerifyOutcome.TooManyAttempts, VerifyOutcome.NoActiveChallenge];

        foreach (var outcome in failures)
        {
            OtpMessages.DescribeFailure(outcome).ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Failure_messages_are_distinct_per_outcome()
    {
        string[] messages =
        [
            OtpMessages.DescribeFailure(VerifyOutcome.InvalidCode),
            OtpMessages.DescribeFailure(VerifyOutcome.Expired),
            OtpMessages.DescribeFailure(VerifyOutcome.TooManyAttempts),
            OtpMessages.DescribeFailure(VerifyOutcome.NoActiveChallenge),
        ];

        messages.Distinct(StringComparer.Ordinal).Count().ShouldBe(4);
    }
}
