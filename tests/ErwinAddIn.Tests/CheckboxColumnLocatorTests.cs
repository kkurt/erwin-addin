using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// The checklist checkbox columns are located by reading pixels, because an XTPReport
/// cell answers no Win32 message and UI Automation crashes erwin on these dialogs. The
/// strips below are VERBATIM captures from the add-in log of a live run
/// (2026-07-26 19:15), which is what makes these tests worth anything.
/// </summary>
public class CheckboxColumnLocatorTests
{
    // "Close Model": two checkboxes - close (checked, so its centre carries the tick)
    // and save (unchecked, a clean white interior).
    private const string CloseModelGutter =
        ".#........#WWW###.W...............WWWWWWW.W.............................";

    // "Save Models": ONE checkbox. The light pixels further right are the white
    // model-name text drawn on the selected row - assuming a second column there is
    // what aborted the 19:15 run.
    private const string SaveModelsGutter =
        ".#........#WWW###.W...............................WWW...W.W.....W..W....";

    [Fact]
    public void Close_Model_has_two_checkbox_columns()
    {
        var centres = MartMartAutomation.LocateCheckboxCentres(CloseModelGutter);

        centres.Should().HaveCount(2);
        // The live run read state successfully at +12 and +36, so the centres must sit
        // within a checkbox-half-width of those.
        centres[0].Should().BeInRange(9, 16);
        centres[1].Should().BeInRange(33, 40);
    }

    [Fact]
    public void Save_Models_has_one_checkbox_column_and_its_text_is_not_mistaken_for_one()
    {
        var centres = MartMartAutomation.LocateCheckboxCentres(SaveModelsGutter);

        centres.Should().HaveCount(1);
        centres[0].Should().BeInRange(9, 16);
    }

    [Fact]
    public void Both_dialogs_agree_on_the_first_column()
    {
        MartMartAutomation.LocateCheckboxCentres(CloseModelGutter)[0]
            .Should().Be(MartMartAutomation.LocateCheckboxCentres(SaveModelsGutter)[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("........................")]              // nothing but background
    [InlineData(".W...W....W..W...W..W...")]              // scattered glyph strokes only
    public void Nothing_is_located_when_there_is_no_checkbox(string gutter)
    {
        // An empty result makes the caller ABORT rather than click blind, which is the
        // whole point: a misread must never become a click on an unknown cell.
        MartMartAutomation.LocateCheckboxCentres(gutter).Should().BeEmpty();
    }

    [Fact]
    public void A_long_run_of_text_is_rejected()
    {
        MartMartAutomation.LocateCheckboxCentres("..WWWWWWWWWWWWWWWWWWWWWW..").Should().BeEmpty();
    }
}
