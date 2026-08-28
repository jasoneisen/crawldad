using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The boot guard for the console-write knobs (issue #119 PR5): generous defaults are valid (the section may be
/// omitted), and a non-positive limit or window is rejected rather than silently disabling the guard.</summary>
public class ConsoleWriteOptionsValidatorTests
{
    private static readonly ConsoleWriteOptionsValidator _validator = new();

    [Fact]
    public void The_generous_defaults_are_valid() =>
        _validator.Validate(null, new ConsoleWriteOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void A_non_positive_permit_limit_is_rejected()
    {
        var result = _validator.Validate(null, new ConsoleWriteOptions { PermitLimit = 0 });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("PermitLimit");
    }

    [Fact]
    public void A_non_positive_window_is_rejected()
    {
        var result = _validator.Validate(null, new ConsoleWriteOptions { WindowSeconds = 0 });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("WindowSeconds");
    }
}
