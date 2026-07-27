using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// The integrate merge must never run twice at once: the cascade answers erwin's
/// dialogs BY TITLE and so cannot tell its own dialogs from another run's. On
/// 2026-07-26 two overlapping runs ticked the same "Close Model" checkboxes (each
/// click is a toggle, so the passes cancelled) and both posted Save to the same
/// description dialog, deadlocking erwin in "Saving in progress....".
/// </summary>
public class IntegrateSingleFlightTests
{
    [Fact]
    public void No_run_is_in_flight_by_default()
    {
        MartMartAutomation.IsIntegrateRunning.Should().BeFalse();
    }

    [Fact]
    public void The_token_is_released_when_a_run_fails_early()
    {
        // An empty target path is rejected before any Win32 work, so this exercises
        // the claim/release pair without touching erwin. A missing finally here would
        // wedge the token and refuse every later Integrate for the process lifetime.
        var log = new System.Collections.Generic.List<string>();
        MartMartAutomation.RunIntegrateMerge("", "comment", 1000, log.Add)
            .Should().BeFalse();

        MartMartAutomation.IsIntegrateRunning.Should().BeFalse();
        log.Should().Contain(m => m.Contains("no target catalog path"));
    }

    [Fact]
    public void A_second_run_is_refused_while_the_first_holds_the_token()
    {
        // Re-enter from inside the first run's own logging callback: at that point the
        // token is held, so the nested call must be refused rather than start a rival
        // cascade.
        bool? nested = null;
        var nestedLog = new System.Collections.Generic.List<string>();
        void Log(string m)
        {
            if (nested == null)
                nested = MartMartAutomation.RunIntegrateMerge("", "c", 1000, nestedLog.Add);
        }

        MartMartAutomation.RunIntegrateMerge("", "comment", 1000, Log).Should().BeFalse();

        nested.Should().BeFalse();
        nestedLog.Should().Contain(m => m.Contains("already in progress"));
        MartMartAutomation.IsIntegrateRunning.Should().BeFalse();
    }
}
