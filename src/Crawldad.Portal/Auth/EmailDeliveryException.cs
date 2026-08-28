namespace Crawldad.Portal.Auth;

/// <summary>Thrown by <see cref="PostmarkEmailSender"/> when a sign-in code could not be handed to the email provider —
/// a non-2xx response or a transport failure. It exists so the OTP flow stays <b>fail-closed</b>: the sender must throw
/// rather than silently succeed (a user could never sign in yet believe a code was sent). The message carries only
/// non-sensitive delivery metadata (HTTP status, Postmark error code); it never contains the server token, the code, or
/// the recipient address.</summary>
internal sealed class EmailDeliveryException : Exception
{
    /// <summary>A non-2xx provider response — the message describes the failure with non-sensitive metadata only.</summary>
    public EmailDeliveryException(string message) : base(message)
    {
    }

    /// <summary>A transport-layer failure — <paramref name="innerException"/> is the underlying
    /// <see cref="System.Net.Http.HttpRequestException"/>.</summary>
    public EmailDeliveryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
