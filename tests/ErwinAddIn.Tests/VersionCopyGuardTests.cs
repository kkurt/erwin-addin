using System;
using System.Reflection;
using System.Runtime.CompilerServices;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for <c>IsVersionCopyOfBoundModel</c> on
/// <see cref="ModelConfigForm"/> (2026-07-18): the reconnect adoption gates
/// must never bind a Mart-version copy of the CURRENTLY BOUND model that the
/// USER opened (Complete Compare right side, Mart version browse). Adopting
/// one re-inits validation against the copy and UDP sync commits definitions
/// INTO the compare target mid-compare (log-proven 2026-07-18 19:36:46,
/// creates=8 into the v1 right side while the CC wizard was open). The helper
/// is a private instance method that only reads <c>_lastConnectedLocator</c>,
/// so it is exercised on an uninitialized form instance (no ctor, no SCAPI).
/// </summary>
public class VersionCopyGuardTests
{
    private const string BoundV7 =
        "erwin://Mart://TestRoot/Kursat/MetaRepo?&version=7&modelLongId={FFA2B07B-0531-4D8E-AF39-9B7922959E78}+00000000";
    private const string CopyV1 =
        "erwin://Mart://TestRoot/Kursat/MetaRepo?&version=1&modelLongId={FFA2B07B-0531-4D8E-AF39-9B7922959E78}+00000000";
    private const string TitleCopyV1 = "Mart://TestRoot/Kursat/MetaRepo?VNO=1";
    private const string TitleSameV7 = "Mart://TestRoot/Kursat/MetaRepo?VNO=7";
    private const string OtherModelV1 =
        "erwin://Mart://TestRoot/Kursat/PAYMENTS?&version=1&modelLongId={F3CC58C0-8967-4373-B410-B31E315ECA5B}+00000000";

    private static bool IsVersionCopy(string? bound, string? candidate)
    {
        // No ctor ran: suppress the Component finalizer so the test host
        // never runs Dispose(false) on a half-built Form.
        var form = (ModelConfigForm)RuntimeHelpers.GetUninitializedObject(typeof(ModelConfigForm));
        GC.SuppressFinalize(form);

        var field = typeof(ModelConfigForm).GetField(
            "_lastConnectedLocator", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("_lastConnectedLocator must exist on ModelConfigForm");
        field!.SetValue(form, bound);

        var mi = typeof(ModelConfigForm).GetMethod(
            "IsVersionCopyOfBoundModel", BindingFlags.NonPublic | BindingFlags.Instance);
        mi.Should().NotBeNull("private helper IsVersionCopyOfBoundModel must exist on ModelConfigForm");
        return (bool)mi!.Invoke(form, new object?[] { candidate })!;
    }

    [Fact]
    public void Different_version_of_bound_model_is_a_version_copy()
        => IsVersionCopy(BoundV7, CopyV1).Should().BeTrue();

    [Fact]
    public void Title_shape_VNO_form_is_recognised_cross_shape()
        // The tab-switch detector hands over the window-title form (?VNO=N)
        // while the bound locator is the PropertyBag form (&version=N).
        => IsVersionCopy(BoundV7, TitleCopyV1).Should().BeTrue();

    [Fact]
    public void Same_version_is_not_a_version_copy()
        => IsVersionCopy(BoundV7, BoundV7).Should().BeFalse();

    [Fact]
    public void Title_shape_same_version_is_not_a_version_copy()
        => IsVersionCopy(BoundV7, TitleSameV7).Should().BeFalse();

    [Fact]
    public void Other_model_with_different_version_is_not_a_version_copy()
        // A genuinely different Mart model (different stem) must keep firing
        // the normal model-switch reconnect even when its version differs.
        => IsVersionCopy(BoundV7, OtherModelV1).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_bound_never_guards(string? bound)
        => IsVersionCopy(bound, CopyV1).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_candidate_never_guards(string? candidate)
        => IsVersionCopy(BoundV7, candidate).Should().BeFalse();

    [Fact]
    public void Bound_locator_without_version_never_guards()
        => IsVersionCopy("erwin://Mart://TestRoot/Kursat/MetaRepo?modelLongId={X}", CopyV1)
            .Should().BeFalse();

    [Fact]
    public void Candidate_without_version_never_guards()
        // A stem-only title parse (no VNO) must not be treated as a copy:
        // the guard only suppresses adoption when both versions are certain.
        => IsVersionCopy(BoundV7, "Mart://TestRoot/Kursat/MetaRepo").Should().BeFalse();

    [Fact]
    public void Local_model_locator_never_guards()
        => IsVersionCopy(BoundV7, "C:\\models\\local.erwin").Should().BeFalse();
}
