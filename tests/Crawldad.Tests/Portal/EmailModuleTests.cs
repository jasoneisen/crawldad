using Crawldad.Portal.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Crawldad.Tests.Portal;

/// <summary>The portal's email-sender selection (<see cref="EmailModule.AddEmailSending"/>): the environment/config
/// matrix mirrors the DataProtection module's config-gated shape. Unconfigured ⇒ the dev logger in Development and the
/// fail-closed stub elsewhere (the skeleton's exact behaviour); fully configured ⇒ Postmark in <b>any</b> environment,
/// including the Development override so a real token smoke-tests sign-in locally. Built over a plain
/// <see cref="ServiceCollection"/> (no host), exactly like <c>DataProtectionModuleTests</c>.</summary>
public class EmailModuleTests
{
    private const string _token = "pm-test-token";
    private const string _from = "noreply@crawldad.dev";

    private static readonly (string Key, string Value) _tokenSetting = ($"{PostmarkEmailOptions.Section}:ServerToken", _token);
    private static readonly (string Key, string Value) _fromSetting = ($"{PostmarkEmailOptions.Section}:FromAddress", _from);

    private static ServiceProvider Build(string environment, params (string Key, string Value)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Key, s => (string?)s.Value, StringComparer.Ordinal);
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var services = new ServiceCollection();
        services.AddLogging(); // the sender + options setup resolve ILogger/IConfiguration
        services.AddSingleton<IConfiguration>(config);
        EmailModule.AddEmailSending(services, config, new FakeHostEnvironment(environment));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Development_unconfigured_registers_the_logging_sender()
    {
        using var sp = Build("Development");
        sp.GetRequiredService<IEmailSender>().ShouldBeOfType<LoggingEmailSender>();
    }

    [Fact]
    public void Production_unconfigured_fails_closed_with_the_unconfigured_sender()
    {
        using var sp = Build("Production");
        sp.GetRequiredService<IEmailSender>().ShouldBeOfType<UnconfiguredEmailSender>();
    }

    [Fact]
    public void Fully_configured_selects_postmark_even_in_development()
    {
        // The override: a real token/from in Development picks Postmark over the dev logger, so sign-in can be
        // smoke-tested locally against the live provider.
        using var sp = Build("Development", _tokenSetting, _fromSetting);
        sp.GetRequiredService<IEmailSender>().ShouldBeOfType<PostmarkEmailSender>();
    }

    [Fact]
    public void Fully_configured_selects_postmark_in_a_non_development_environment()
    {
        using var sp = Build("Production", _tokenSetting, _fromSetting);
        sp.GetRequiredService<IEmailSender>().ShouldBeOfType<PostmarkEmailSender>();
    }

    [Fact]
    public void Configured_presets_the_postmark_base_address_and_a_tight_timeout_on_the_named_client()
    {
        using var sp = Build("Staging", _tokenSetting, _fromSetting);

        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(PostmarkEmailSender.HttpClientName);

        client.BaseAddress.ShouldBe(PostmarkEmailSender.ApiBaseAddress);
        // A hung Postmark must not stall the synchronous login send for HttpClient's 100s default.
        client.Timeout.ShouldBe(PostmarkEmailSender.SendTimeout);
        PostmarkEmailSender.SendTimeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void AddEmailSending_rejects_null_arguments()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        var env = new FakeHostEnvironment("Development");

        Should.Throw<ArgumentNullException>(() => EmailModule.AddEmailSending(null!, config, env));
        Should.Throw<ArgumentNullException>(() => EmailModule.AddEmailSending(services, null!, env));
        Should.Throw<ArgumentNullException>(() => EmailModule.AddEmailSending(services, config, null!));
    }

    /// <summary>A minimal <see cref="IHostEnvironment"/> so the selection can be driven per environment name without a host.</summary>
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Crawldad.Portal.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
