using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser.Real;
using Microsoft.Playwright;

namespace Crawldad.Tests.Integration;

/// <summary>Shared real-Chromium harness: one Playwright driver + one local-adapter browser, plus factories for a
/// local <c>run-server</c> (Browserless target) and a local CDP endpoint (Browserbase target). Serialized via the
/// collection below so Chromium/driver processes never contend; every "remote" backend is a loopback server here.</summary>
[SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposed via IAsyncLifetime.DisposeAsync, which xUnit invokes for the collection fixture; the type intentionally does not implement IDisposable because teardown is async.")]
public sealed class RealChromiumFixture : IAsyncLifetime
{
    private PlaywrightProvider _provider = null!;

    /// <summary>The shared Playwright driver.</summary>
    internal IPlaywrightProvider Provider => _provider;

    /// <summary>The shared local adapter (its own headless browser, a cross-run cache, and the throttle).</summary>
    internal LocalChromiumBackend LocalBackend { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _provider = new PlaywrightProvider();
        await _provider.GetAsync(CancellationToken.None);
        LocalBackend = new LocalChromiumBackend(_provider, new InMemoryAssetCache(), new ThrottleGate(TimeProvider.System));
    }

    public async Task DisposeAsync()
    {
        await LocalBackend.DisposeAsync();
        await _provider.DisposeAsync();
    }

    /// <summary>Starts a local Playwright <c>run-server</c> on <c>/chromium/playwright</c> (the Browserless native path shape).</summary>
    internal static async Task<RunServerHandle> StartRunServerAsync()
    {
        var port = Net.FreePort();
        var baseDir = AppContext.BaseDirectory;
        var psi = new ProcessStartInfo(Path.Combine(baseDir, ".playwright", "node", "linux-x64", "node"))
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(baseDir, ".playwright", "package", "cli.js"));
        psi.ArgumentList.Add("run-server");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add("/chromium/playwright");

        var process = Process.Start(psi)!;
        var ready = new TaskCompletionSource();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data?.Contains("Listening on", StringComparison.Ordinal) == true)
            {
                ready.TrySetResult();
            }
        };
        process.BeginOutputReadLine();
        await Task.WhenAny(ready.Task, Task.Delay(15000));
        return new RunServerHandle(process, $"ws://127.0.0.1:{port}/chromium/playwright");
    }

    /// <summary>Launches a headless Chromium exposing a CDP endpoint (the Browserbase connectOverCDP target).</summary>
    internal async Task<CdpChromium> LaunchCdpChromiumAsync()
    {
        var port = Net.FreePort();
        var playwright = await _provider.GetAsync(CancellationToken.None);
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = [$"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1"],
        });

        var url = $"http://127.0.0.1:{port}";
        using var client = new HttpClient();
        for (var i = 0; i < 50; i++)
        {
            try
            {
                if ((await client.GetAsync(new Uri(url + "/json/version"))).IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
                // endpoint not up yet
            }

            await Task.Delay(100);
        }

        return new CdpChromium(browser, url);
    }
}

/// <summary>A running local Playwright server; disposal kills it.</summary>
internal sealed class RunServerHandle(Process process, string wsBase) : IDisposable
{
    /// <summary>The native connect base URL (matching the Browserless <c>/chromium/playwright</c> path shape).</summary>
    public string WsBase => wsBase;

    public void Dispose()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        process.Dispose();
    }
}

/// <summary>A locally launched CDP endpoint; disposal closes the browser.</summary>
internal sealed class CdpChromium(IBrowser browser, string endpoint) : IAsyncDisposable
{
    /// <summary>The CDP HTTP endpoint to connect over.</summary>
    public string Endpoint => endpoint;

    public ValueTask DisposeAsync() => browser.DisposeAsync();
}

/// <summary>Serializes every real-Chromium test onto the one shared harness.</summary>
[CollectionDefinition(Name)]
public sealed class RealChromiumCollection : ICollectionFixture<RealChromiumFixture>
{
    public const string Name = "real-chromium";
}
