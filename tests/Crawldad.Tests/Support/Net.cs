using System.Net;
using System.Net.Sockets;

namespace Crawldad.Tests.Support;

/// <summary>Loopback networking helpers for the real-browser tests.</summary>
internal static class Net
{
    /// <summary>Grabs a free loopback TCP port (the listener is closed immediately; a brief race window is acceptable in tests).</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
