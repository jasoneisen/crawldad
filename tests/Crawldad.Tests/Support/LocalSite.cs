using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;

namespace Crawldad.Tests.Support;

/// <summary>A tiny in-process HTTP origin on loopback for the real-Chromium tests: real HTTP so Playwright's
/// routing/interception is exercised honestly. Routes register via <see cref="Map"/>; <see cref="Hits"/> lets a test
/// prove the route cache or resource blocker prevented a fetch. Serves any method (doubles as the Browserbase stub).</summary>
internal sealed class LocalSite : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, Response> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);

    public LocalSite()
    {
        Port = Net.FreePort();
        BaseUrl = $"http://127.0.0.1:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The loopback port this origin listens on.</summary>
    public int Port { get; }

    /// <summary>The origin base URL (with trailing slash).</summary>
    public string BaseUrl { get; }

    /// <summary>The absolute URL for a path on this origin.</summary>
    /// <param name="path">An absolute path beginning with <c>/</c>.</param>
    public string Url(string path) => BaseUrl.TrimEnd('/') + path;

    /// <summary>How many requests this origin has served for <paramref name="path"/>.</summary>
    public int Hits(string path) => _hits.GetValueOrDefault(path);

    /// <summary>Registers a route.</summary>
    /// <param name="cacheControl">Optional <c>Cache-Control</c> header (e.g. <c>no-store</c> to force re-requests).</param>
    public LocalSite Map(string path, string contentType, string body, string? cacheControl = null, int status = 200)
    {
        _routes[path] = new Response(contentType, Encoding.UTF8.GetBytes(body), cacheControl, status);
        return this;
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break; // listener stopped
            }
            catch (ObjectDisposedException)
            {
                break; // listener closed
            }

            Serve(context);
        }
    }

    private void Serve(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        _hits.AddOrUpdate(path, 1, static (_, n) => n + 1);
        try
        {
            if (_routes.TryGetValue(path, out var response))
            {
                context.Response.StatusCode = response.Status;
                context.Response.ContentType = response.ContentType;
                if (response.CacheControl is not null)
                {
                    context.Response.AddHeader("Cache-Control", response.CacheControl);
                }

                context.Response.OutputStream.Write(response.Body);
            }
            else
            {
                context.Response.StatusCode = 404;
            }

            context.Response.Close();
        }
        catch (HttpListenerException)
        {
            // client went away mid-response — irrelevant to the test
        }
        catch (IOException)
        {
            // ditto
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    private sealed record Response(string ContentType, byte[] Body, string? CacheControl, int Status);
}
