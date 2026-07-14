using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the pure decision helpers on
/// <see cref="SessionTrackingService"/>:
/// <list type="bullet">
/// <item><see cref="SessionTrackingService.DecideShutdownAction"/> - SHUTDOWN_TYPE
/// -> action mapping. NULL / blank / unknown must map to None so the add-in only
/// ever acts on a recognised non-empty command (an admin who clears the cell
/// cancels the order). Recognised tokens come from AddinSession.ShutdownTypes.</item>
/// <item><see cref="SessionTrackingService.ResolveIntervalMinutes"/> - the
/// heartbeat interval. Absent / blank / non-numeric / non-positive fall back to
/// the 5-minute default; user-session tracking runs by default and the interval
/// only sets the polling period, it never disables tracking (the former
/// USER_TRACKING_ENABLED gate was removed).</item>
/// </list>
/// </summary>
public class SessionTrackingDecisionTests
{
    [Theory]
    [InlineData("GRACEFUL")]
    [InlineData("graceful")]
    [InlineData("  Graceful  ")]
    public void Graceful_token_maps_to_Graceful(string value)
        => SessionTrackingService.DecideShutdownAction(value)
            .Should().Be(SessionTrackingService.ShutdownAction.Graceful);

    [Theory]
    [InlineData("FORCE")]
    [InlineData("force")]
    [InlineData(" Force ")]
    public void Force_token_maps_to_Force(string value)
        => SessionTrackingService.DecideShutdownAction(value)
            .Should().Be(SessionTrackingService.ShutdownAction.Force);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_maps_to_None(string value)
        => SessionTrackingService.DecideShutdownAction(value)
            .Should().Be(SessionTrackingService.ShutdownAction.None);

    [Theory]
    [InlineData("SHUTDOWN")]
    [InlineData("kill")]
    [InlineData("GRACEFUL_NOW")]
    [InlineData("0")]
    public void Unknown_token_maps_to_None(string value)
        => SessionTrackingService.DecideShutdownAction(value)
            .Should().Be(SessionTrackingService.ShutdownAction.None);

    // ---- ResolveIntervalMinutes: tracking runs by default; the interval only
    // ---- sets the heartbeat period and never disables tracking. Default 5 min.

    [Theory]
    [InlineData("10", 10)]
    [InlineData("1", 1)]
    [InlineData("  15  ", 15)]
    [InlineData("1440", 1440)]
    public void Positive_interval_is_used_verbatim(string raw, int expected)
        => SessionTrackingService.ResolveIntervalMinutes(raw).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void Absent_blank_invalid_or_nonpositive_interval_falls_back_to_5(string raw)
        => SessionTrackingService.ResolveIntervalMinutes(raw).Should().Be(5);
}
