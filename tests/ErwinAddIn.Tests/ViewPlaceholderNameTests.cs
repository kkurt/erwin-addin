using System.Reflection;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Regression guard for the view name-commit deferral (2026-06-14). A freshly
/// dropped view wears erwin's "V/&lt;digits&gt;" placeholder; the add-in must
/// HOLD it until the user commits a real name instead of running the pipeline
/// (and popping "Naming standard applied") on the placeholder.
/// <para>
/// <see cref="TableTypeMonitorService.IsPlaceholderViewName"/> is the classifier
/// that gates the hold. The single most damaging regression is the inverse: a
/// REAL view name (e.g. "V_6" or the auto-suffixed "V_6_VVV") mis-classified as
/// a placeholder would be deferred forever and never validated. The predicate is
/// a private static pure helper (mirroring the private entity equivalent), so it
/// is exercised here via reflection - no production visibility change.
/// </para>
/// </summary>
public class ViewPlaceholderNameTests
{
    private static readonly MethodInfo? Predicate =
        typeof(TableTypeMonitorService).GetMethod(
            "IsPlaceholderViewName", BindingFlags.NonPublic | BindingFlags.Static);

    private static bool IsPlaceholder(string? name)
    {
        Predicate.Should().NotBeNull(
            "IsPlaceholderViewName must exist on TableTypeMonitorService (renamed?)");
        return (bool)Predicate!.Invoke(null, new object?[] { name })!;
    }

    [Theory]
    // erwin diagram auto-name: 'V', separator, digits-only tail. The raw
    // Properties("Name") gives a slash ("V/1") but the view.Name accessor the
    // scan actually uses renders it as an UNDERSCORE ("V_1") - BOTH must hold
    // (the underscore form is the one that bit the first live test).
    [InlineData("V_1")]
    [InlineData("V_6")]
    [InlineData("V_42")]
    [InlineData("V/1")]
    [InlineData("V/4")]
    [InlineData("V/100")]
    // Model Explorer / pre-commit placeholders.
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<default>")]
    [InlineData("<default> view")]
    // Auto-applied fail placeholder (Phase-2H parity).
    [InlineData("PLEASE_CHANGE_IT")]
    [InlineData("PLEASE CHANGE IT_VVV")]
    public void Placeholder_names_are_held(string? name)
    {
        IsPlaceholder(name).Should().BeTrue();
    }

    [Theory]
    // Real user names - MUST NOT be deferred (the dangerous inverse regression).
    [InlineData("CustomerView")]
    [InlineData("V_6_VVV")]             // 'V_'+digit then more = committed + suffix
    [InlineData("V_1View")]             // the screenshotted auto-applied result
    [InlineData("V_Sales")]             // 'V_' prefix but non-digit tail = real
    [InlineData("V/2_FOO")]             // separator + digits then more = real
    [InlineData("E/41")]                // entity placeholder, not a view one
    [InlineData("MyReportingView")]
    [InlineData("V")]                   // too short to be a 'V<sep><n>' auto-name
    [InlineData("V_")]                  // no digit tail
    [InlineData("V/")]                  // no digit tail
    public void Real_names_are_not_held(string name)
    {
        IsPlaceholder(name).Should().BeFalse();
    }
}
