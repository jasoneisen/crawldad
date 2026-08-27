namespace Crawldad.Api.Features.Billing;

/// <summary>Thrown by a session call when the payment provider is not configured (no credentials) or not yet wired (the
/// production stub before the Stripe SDK lands). The billing endpoints translate it into a friendly
/// <c>503 billing_not_configured</c> — never a 500 — and never log its message alongside any secret. A single
/// message-carrying constructor is its whole purpose (mirrors NotLinkedException).</summary>
public sealed class BillingNotConfiguredException(string message) : Exception(message);
