using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Crawldad.Tests.Support;

/// <summary>Drop-before-boot schema hygiene, so an integration host starts on a <b>provably empty</b> schema.
/// <para>A Marten/Wolverine host RECOVERS at startup: <see cref="Crawldad.Api.Features.Runs.RunRecoveryService"/>
/// re-publishes an <c>ExecuteRun</c> for every <c>Running</c> <c>RunProgress</c> row and a <c>PromoteQueued</c> for every
/// tenant holding a queued run, and Wolverine's durability agent replays whatever sits in
/// <c>wolverine_incoming_envelopes</c>. A post-boot <c>ResetAllMartenDataAsync()</c> is therefore too late: on a
/// long-lived developer Postgres the residue of a previously interrupted (or killed) run is resumed FIRST, takes the
/// cap-1 tenant slot, and the new host's first run comes back <c>queued</c> where the test expects <c>running</c>. Each
/// run burns off its own residue, so it reads as a flake; CI (a fresh Postgres per job) never sees it.</para>
/// <para>Dropping the schema before the host is built leaves nothing to recover. The host recreates it during startup —
/// Marten applies its schema on boot in Development (<c>HostConfiguration</c>) and otherwise creates objects on demand
/// (<c>AutoCreate.CreateOrUpdate</c>), and Wolverine auto-builds its message storage — so this replaces the discipline of
/// "always leave the schema clean" with a guarantee that does not depend on the previous run finishing.</para></summary>
public static partial class TestSchema
{
    /// <summary>Schemas a test may never drop, whatever it asks for: the API's production schema, the portal's, and
    /// Postgres's default. Belt-and-braces beside <see cref="TestSchemaName"/> — none of them match it either.</summary>
    private static readonly string[] _reservedSchemas = ["crawldad", "portal", "public"];

    // One resolution per host assembly for the whole test run: every host of that assembly reads the same file.
    private static readonly ConcurrentDictionary<Assembly, string> _connectionStrings = new();

    [GeneratedRegex("^crawldad_[a-z0-9_]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TestSchemaName();

    /// <summary>Throws unless <paramref name="schemaName"/> is a test schema — <c>crawldad_</c> plus lowercase
    /// alphanumerics/underscores — and not one of the reserved production/default names, so a misconfigured test can
    /// never drop real data.</summary>
    public static void EnsureDroppable(string schemaName)
    {
        if (_reservedSchemas.Contains(schemaName, StringComparer.OrdinalIgnoreCase) || !TestSchemaName().IsMatch(schemaName))
        {
            throw new ArgumentException(
                $"'{schemaName}' is not a droppable test schema: expected ^crawldad_[a-z0-9_]+$, and never one of {string.Join(", ", _reservedSchemas)}.",
                nameof(schemaName));
        }
    }

    /// <summary>Drops <paramref name="schemaName"/> and everything in it, on the Postgres that <paramref name="entryPoint"/>'s
    /// host boots against. Deliberately SYNCHRONOUS: the one place guaranteed to run before the host starts is the
    /// host-builder callback, which is itself synchronous — and Npgsql's synchronous API keeps this off any
    /// sync-over-async that could deadlock on xUnit's single-worker synchronization context.</summary>
    /// <param name="schemaName">The test schema, validated by <see cref="EnsureDroppable"/>.</param>
    /// <param name="entryPoint">The host's entry-point type, which selects whose connection string to resolve.</param>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "A schema name is an identifier, so it cannot be parameterised; EnsureDroppable has already constrained it to ^crawldad_[a-z0-9_]+$, which admits no injection payload.")]
    public static void Drop(string schemaName, Type entryPoint)
    {
        EnsureDroppable(schemaName);

        using var connection = new NpgsqlConnection(ConnectionStringFor(entryPoint));
        connection.Open();

        using var command = new NpgsqlCommand($"drop schema if exists \"{schemaName}\" cascade", connection);
        command.ExecuteNonQuery();
    }

    /// <summary>The <c>ConnectionStrings:marten</c> the host will itself resolve: read from the SAME appsettings.json the
    /// host boots from — its content root, taken from the <c>MvcTestingAppManifest.json</c> the test SDK emits and that
    /// WebApplicationFactory (hence Alba) uses to locate it — then overlaid with environment variables, so CI's
    /// <c>ConnectionStrings__marten</c> override wins here exactly as it does in the host. Resolving it through the host's
    /// own file means test and host can never disagree about which Postgres this drops from.</summary>
    private static string ConnectionStringFor(Type entryPoint) =>
        _connectionStrings.GetOrAdd(entryPoint.Assembly, static assembly =>
        {
            var manifestPath = Path.Combine(AppContext.BaseDirectory, "MvcTestingAppManifest.json");
            var contentRoots = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath)) ?? [];
            if (!contentRoots.TryGetValue(assembly.FullName!, out var contentRoot))
            {
                throw new InvalidOperationException($"'{manifestPath}' carries no content root for '{assembly.FullName}'.");
            }

            return new ConfigurationBuilder()
                       .SetBasePath(contentRoot)
                       .AddJsonFile("appsettings.json")
                       .AddEnvironmentVariables()
                       .Build()
                       .GetConnectionString("marten")
                   ?? throw new InvalidOperationException($"'{contentRoot}/appsettings.json' declares no ConnectionStrings:marten.");
        });
}
