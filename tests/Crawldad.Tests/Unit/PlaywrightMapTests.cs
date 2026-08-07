using Crawldad.Web.Infrastructure.Browser.Real;
using Microsoft.Playwright;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The <see cref="PlaywrightMap"/> option translations (§9). Pure — every enum arm is asserted here so the real
/// wrappers need only exercise the common path.
/// </summary>
public class PlaywrightMapTests
{
    [Fact]
    public void Timeout_maps_value_and_null()
    {
        PlaywrightMap.Timeout(360000).ShouldBe(360000f);
        PlaywrightMap.Timeout(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("load", WaitUntilState.Load)]
    [InlineData("domcontentloaded", WaitUntilState.DOMContentLoaded)]
    [InlineData("networkidle", WaitUntilState.NetworkIdle)]
    [InlineData("commit", WaitUntilState.Commit)]
    public void WaitUntil_maps_each_known_state(string input, WaitUntilState expected) =>
        PlaywrightMap.WaitUntil(input).ShouldBe(expected);

    [Fact]
    public void WaitUntil_is_null_for_absent_or_unknown()
    {
        PlaywrightMap.WaitUntil(null).ShouldBeNull();
        PlaywrightMap.WaitUntil("bogus").ShouldBeNull();
    }

    [Theory]
    [InlineData("load", LoadState.Load)]
    [InlineData("domcontentloaded", LoadState.DOMContentLoaded)]
    [InlineData("networkidle", LoadState.NetworkIdle)]
    [InlineData("bogus", LoadState.Load)] // default arm
    public void LoadState_maps_each_state(string input, LoadState expected) =>
        PlaywrightMap.LoadState(input).ShouldBe(expected);

    [Theory]
    [InlineData("visible", WaitForSelectorState.Visible)]
    [InlineData("hidden", WaitForSelectorState.Hidden)]
    [InlineData("attached", WaitForSelectorState.Attached)]
    [InlineData("detached", WaitForSelectorState.Detached)]
    [InlineData("bogus", WaitForSelectorState.Visible)] // default arm
    public void WaitForState_maps_each_state(string input, WaitForSelectorState expected) =>
        PlaywrightMap.WaitForState(input).ShouldBe(expected);
}
