namespace Crawldad.Api.Infrastructure.Security;

/// <summary>Knobs for the console-write guard (issue #119 PR5): a per-<c>(email, tenant)</c> sliding-window rate limit on
/// console-authenticated writes, bound from <c>Crawldad:ConsoleWrite</c>. This is <b>abuse insurance, not throttling</b> —
/// the defaults are deliberately generous (a human dashboard, or the portal on a human's behalf, never approaches them);
/// exceeding the window is a <c>429</c> so a compromised console can't hammer the write surface unbounded. Keying on the
/// <b>human email + tenant</b> (never the shared portal identity) means one noisy actor can't starve every tenant.</summary>
public sealed class ConsoleWriteOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Crawldad:ConsoleWrite";

    /// <summary>The maximum console writes one <c>(email, tenant)</c> may make within <see cref="WindowSeconds"/> before a
    /// <c>429</c>. Generous by design (abuse insurance). Default 240.</summary>
    public int PermitLimit { get; init; } = 240;

    /// <summary>The sliding window, in seconds, the <see cref="PermitLimit"/> is counted over. Default 60.</summary>
    public int WindowSeconds { get; init; } = 60;

    /// <summary>The sliding window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}
