using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Browsers;
using FluentValidation;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Api.Features.Browsers;

/// <summary>Self-registration for the Browsers slice: the tenant-scoped <see cref="BrowserRegistration"/> credential
/// document, the register-request validator, the Data-Protection-backed credential store, and the tenant-scoped
/// connect resolver the backends resolve through. Mirrors the Payloads/Runs module shape.</summary>
public static class BrowsersModule
{
    /// <summary>Registers the plain, tenant-scoped <see cref="BrowserRegistration"/> document (secrets encrypted at rest;
    /// no event stream carries the secret). Multi-tenancy comes from the shared <c>AllDocumentsAreMultiTenanted</c> policy.</summary>
    public static void ConfigureMarten(StoreOptions options) => options.Schema.For<BrowserRegistration>();

    /// <summary>Registers the slice's services: the register validator, the encrypting credential store, and the connect
    /// resolver. Data Protection itself — the at-rest cipher these lean on, plus its persisted key ring — is wired
    /// host-wide by <see cref="Infrastructure.Security.DataProtectionModule"/>.</summary>
    public static void AddBrowsersServices(IServiceCollection services)
    {
        services.AddScoped<IValidator<RegisterBrowserRequest>, RegisterBrowserRequestValidator>();
        services.AddSingleton<IBrowserCredentialStore, MartenBrowserCredentialStore>();
        services.AddSingleton<IConnectCredentialResolver, BrowserCredentialResolver>();
    }
}
