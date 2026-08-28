using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Crawldad.Portal.Auth;

/// <summary>Selects and registers the portal's <see cref="IEmailSender"/> from <c>Crawldad:Portal:Email</c>, plus the
/// options boot-guard. The selection is all-or-nothing, exactly mirroring the DataProtection module's config-gated shape:
/// <list type="bullet">
/// <item>Fully configured (a <see cref="PostmarkEmailOptions.ServerToken"/> AND a <see cref="PostmarkEmailOptions.FromAddress"/>)
/// ⇒ <see cref="PostmarkEmailSender"/> in <b>any</b> environment (so a real token smoke-tests locally too).</item>
/// <item>Unconfigured in Development ⇒ <see cref="LoggingEmailSender"/> (logs the code, the one place it is ever logged).</item>
/// <item>Unconfigured elsewhere ⇒ the fail-closed <see cref="UnconfiguredEmailSender"/> (refuses to send).</item>
/// <item>HALF-configured (one of the pair) ⇒ boot fails via the <c>ValidateOnStart</c> guard below.</item>
/// </list>
/// So an unconfigured host keeps the skeleton's exact behaviour, and a provider is opt-in by config alone.</summary>
internal static class EmailModule
{
    /// <summary>Registers the email options + boot guard, then the environment/config-selected <see cref="IEmailSender"/>.</summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">The host configuration (the provider is read from <c>Crawldad:Portal:Email</c>).</param>
    /// <param name="environment">The host environment (only consulted when the provider is unconfigured).</param>
    public static void AddEmailSending(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // The knobs + boot guard: a half-configured provider fails at startup rather than silently going fail-closed.
        services.AddOptions<PostmarkEmailOptions>().BindConfiguration(PostmarkEmailOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PostmarkEmailOptions>, PostmarkEmailOptionsValidator>();

        // The selection is a registration-time decision, so read the section directly (IOptions isn't available yet) —
        // the same indexer idiom the DataProtection module uses to pick its provider. "Configured" is BOTH secrets
        // present; a half-set pair is rejected loudly by the boot validator above.
        var hasToken = !string.IsNullOrWhiteSpace(configuration[$"{PostmarkEmailOptions.Section}:ServerToken"]);
        var hasFrom = !string.IsNullOrWhiteSpace(configuration[$"{PostmarkEmailOptions.Section}:FromAddress"]);

        if (hasToken && hasFrom)
        {
            // Fully configured ⇒ Postmark, in ANY environment. The named client presets Postmark's base address and a
            // tight timeout; the per-request server-token header + body are set by the sender. IEmailSender stays a
            // singleton, like the other two senders — the sender resolves a pooled client from the factory per send (as
            // WorkspaceLinker does).
            services.AddHttpClient(PostmarkEmailSender.HttpClientName, client =>
            {
                client.BaseAddress = PostmarkEmailSender.ApiBaseAddress;
                // A hung Postmark must not stall the login send path for HttpClient's 100s default — the OTP request is
                // synchronous behind the user's click. On timeout the client throws, so the send still fails closed
                // (surfaces an error) instead of blocking.
                client.Timeout = PostmarkEmailSender.SendTimeout;
            });
            services.AddSingleton<IEmailSender, PostmarkEmailSender>();
        }
        else if (environment.IsDevelopment())
        {
            // Unconfigured Development ⇒ log the code so a developer can complete the flow without a mail provider.
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            // Unconfigured elsewhere ⇒ fail closed: never silently succeed, never log a real code.
            services.AddSingleton<IEmailSender, UnconfiguredEmailSender>();
        }
    }
}
