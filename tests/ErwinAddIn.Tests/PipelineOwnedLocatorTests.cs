using System;
using System.Reflection;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the pure locator helpers behind the DDL pipeline's
/// reconnect guard (2026-06-10): <c>ExtractLocatorVersion</c> and
/// <c>BuildVersionLocator</c> on <see cref="ModelConfigForm"/>. The guard
/// stores the locator of the Mart version copy the pipeline opens for the
/// compare RIGHT side; a wrong derivation either neuters the guard (leftover
/// copy gets adopted and UDP-synced - the 2026-06-10 incident) or, worse,
/// registers the ACTIVE model's own locator, which the hard refusal gate in
/// ConnectToModel would then refuse to bind. Helpers are private statics, so
/// they are exercised via reflection (no Form instantiation needed).
/// </summary>
public class PipelineOwnedLocatorTests
{
    private const string PuLocatorV4 =
        "erwin://Mart://Mart/Kursat/MetaRepo?&version=4&modelLongId={83BE3D72-032E-4377-BEE6-534C88C04DD5}+00000000";
    private const string PuLocatorV1 =
        "erwin://Mart://Mart/Kursat/MetaRepo?&version=1&modelLongId={83BE3D72-032E-4377-BEE6-534C88C04DD5}+00000000";
    private const string TitleLocatorV1 = "Mart://Mart/Kursat/MetaRepo?VNO=1";

    private static MethodInfo Helper(string name)
    {
        var mi = typeof(ModelConfigForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        mi.Should().NotBeNull($"private static helper {name} must exist on ModelConfigForm");
        return mi!;
    }

    private static int ExtractVersion(string locator)
        => (int)Helper("ExtractLocatorVersion").Invoke(null, new object?[] { locator })!;

    private static string? BuildVersionLocator(string locator, int version)
        => (string?)Helper("BuildVersionLocator").Invoke(null, new object?[] { locator, version });

    [Fact]
    public void ExtractVersion_reads_PropertyBag_form()
        => ExtractVersion(PuLocatorV4).Should().Be(4);

    [Fact]
    public void ExtractVersion_reads_window_title_VNO_form()
        => ExtractVersion(TitleLocatorV1).Should().Be(1);

    [Theory]
    [InlineData("")]
    [InlineData("erwin://Mart://Mart/Kursat/MetaRepo")]
    [InlineData("Mart://Mart/Kursat/MetaRepo?modelLongId={X}")]
    public void ExtractVersion_returns_minus_one_when_absent(string locator)
        => ExtractVersion(locator).Should().Be(-1);

    [Fact]
    public void BuildVersionLocator_swaps_only_the_version_parameter()
        => BuildVersionLocator(PuLocatorV4, 1).Should().Be(PuLocatorV1);

    [Fact]
    public void BuildVersionLocator_result_reads_back_as_requested_version()
        => ExtractVersion(BuildVersionLocator(PuLocatorV4, 2)!).Should().Be(2);

    [Fact]
    public void BuildVersionLocator_refuses_VNO_form_input()
        // The swap regex only understands '&version='. A VNO-form input must
        // yield null, NOT the input unchanged: registering the unchanged
        // ACTIVE locator would make the refusal gate block the user's own
        // model.
        => BuildVersionLocator(TitleLocatorV1, 3).Should().BeNull();

    [Fact]
    public void BuildVersionLocator_refuses_locator_without_version_parameter()
        => BuildVersionLocator("erwin://Mart://Mart/Kursat/MetaRepo?modelLongId={X}", 1).Should().BeNull();

    [Fact]
    public void BuildVersionLocator_refuses_same_version_swap()
        // Cross-version callers always pass version != active; equality means
        // the result would equal the active locator - never register it.
        => BuildVersionLocator(PuLocatorV4, 4).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildVersionLocator_refuses_empty_input(string? locator)
        => BuildVersionLocator(locator!, 1).Should().BeNull();

    [Fact]
    public void BuildVersionLocator_refuses_nonpositive_version()
        => BuildVersionLocator(PuLocatorV4, 0).Should().BeNull();
}
