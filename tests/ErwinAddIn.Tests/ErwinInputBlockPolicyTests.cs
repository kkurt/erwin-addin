using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// WP 329 disables erwin's main frame while the add-in is on screen. Getting this
/// policy wrong strands the user in an erwin that ignores every click, so each veto
/// is pinned here.
/// </summary>
public class ErwinInputBlockPolicyTests
{
    private static bool Block(bool onScreen = true, bool suspended = false, bool debug = false,
                              bool wizard = false, bool martSave = false, bool hasModel = true)
        => ErwinInputBlock.ShouldBlock(onScreen, suspended, debug, wizard, martSave, hasModel);

    [Fact]
    public void Blocks_while_the_addin_is_on_screen_over_an_open_model()
    {
        Block().Should().BeTrue();
    }

    [Fact]
    public void Never_blocks_when_no_model_is_open()
    {
        // The regression: after an Integrate closes both models, erwin's own menus are
        // the only way to open another one - and the title-bar X stops working too.
        Block(hasModel: false).Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_frame_counts_as_no_model_so_erwin_stays_usable()
    {
        // Win32Helper.GetActiveMdiChild returns Zero for a hung or non-MDI frame, which
        // reaches this policy as hasOpenModel: false. Fail-safe direction is "usable".
        Block(hasModel: false, onScreen: true).Should().BeFalse();
    }

    [Fact]
    public void Never_blocks_while_the_addin_is_off_screen()
    {
        Block(onScreen: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, false, false)]   // automation suspension
    [InlineData(false, true, false, false)]   // DebugMode
    [InlineData(false, false, true, false)]   // wizard gate
    [InlineData(false, false, false, true)]   // Mart save gate
    public void Every_veto_releases_the_block(bool suspended, bool debug, bool wizard, bool martSave)
    {
        // A disabled window swallows the synthetic mouse input the pipelines depend on,
        // so any of these being set must leave erwin operable.
        Block(suspended: suspended, debug: debug, wizard: wizard, martSave: martSave)
            .Should().BeFalse();
    }
}
