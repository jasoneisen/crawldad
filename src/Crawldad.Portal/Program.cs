namespace Crawldad.Portal;

/// <summary>Entry-point shell. All wiring lives in <see cref="PortalHost"/>; this file is excluded from coverage.
/// It is a namespaced <c>Crawldad.Portal.Program</c> (not a top-level/global <c>Program</c>) on purpose: the test
/// project also references Crawldad.Api, whose global <c>Program</c> the existing Alba fixtures resolve by simple
/// name — a second global <c>Program</c> would make that reference ambiguous. WebApplicationFactory locates this
/// assembly's entry point via this public non-static type instead (WebApplicationFactory&lt;Program&gt; needs a
/// type argument, so this is a sealed class, not a static class).</summary>
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddCrawldadPortal();

        var app = builder.Build();
        app.MapCrawldadPortal();

        app.Run();
    }
}
