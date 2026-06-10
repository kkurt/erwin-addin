using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the pure SHUTDOWN_TYPE -> action mapping
/// (<see cref="SessionTrackingService.DecideShutdownAction"/>). NULL / blank /
/// unknown must map to None so the add-in only ever acts on a recognised
/// non-empty command (an admin who clears the cell cancels the order). The
/// recognised tokens come from the shared AddinSession.ShutdownTypes constants.
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
}
