using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Coverage for the Apply-On gate used by MODEL-level required-UDP enforcement
/// (<see cref="TableTypeMonitorService.AppliesOnUpdate"/>). Model-open is the
/// "Update" context, so Both/Update (and blank == Both) enforce, while Create-only
/// is skipped. Mirrors the UdpValidationEngine.ValidateAll Apply-On semantics.
/// </summary>
public class ModelRequiredUdpApplyOnTests
{
    [Theory]
    [InlineData("Update")]
    [InlineData("update")]
    [InlineData("  Update  ")]
    [InlineData("Both")]
    [InlineData("both")]
    [InlineData(null)]   // default == Both
    [InlineData("")]
    [InlineData("   ")]
    public void Enforced_on_update_context(string applyOn)
        => TableTypeMonitorService.AppliesOnUpdate(applyOn).Should().BeTrue();

    [Theory]
    [InlineData("Create")]
    [InlineData("create")]
    [InlineData(" Create ")]
    public void Skipped_when_create_only(string applyOn)
        => TableTypeMonitorService.AppliesOnUpdate(applyOn).Should().BeFalse();

    [Theory]
    [InlineData("Delete")]
    [InlineData("xyz")]
    public void Unknown_token_is_not_treated_as_update(string applyOn)
        => TableTypeMonitorService.AppliesOnUpdate(applyOn).Should().BeFalse();
}
