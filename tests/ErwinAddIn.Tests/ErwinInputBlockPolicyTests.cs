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

    private static readonly IntPtr Frame = new(0x1000);
    private static readonly IntPtr Other = new(0x2000);

    private static bool Raise(IntPtr? blocked = null, IntPtr? foreground = null, bool onScreen = true)
        => ErwinInputBlock.ShouldRaiseAddinOverErwin(blocked ?? Frame, foreground ?? Frame, onScreen);

    [Fact]
    public void Comes_back_to_the_front_when_the_blocked_frame_is_the_foreground()
    {
        // The 2026-07-28 report: the user switches to another app and then raises erwin from
        // the taskbar. A disabled frame accepts activation, so erwin ends up on top, ignoring
        // every click, with the only window that can release it buried behind.
        Raise().Should().BeTrue();
    }

    [Fact]
    public void Leaves_the_foreground_alone_while_the_user_is_in_another_application()
    {
        // Chrome (or an erwin dialog - a popup is never the frame) owns the foreground:
        // stealing it would be worse than the bug.
        Raise(foreground: Other).Should().BeFalse();
    }

    [Fact]
    public void Never_raises_when_nothing_is_blocked()
    {
        // No block == erwin is operable == the add-in has no claim on the foreground. Zero
        // also has to lose against a Zero foreground, which is what GetForegroundWindow
        // returns while no window is active at all.
        Raise(blocked: IntPtr.Zero, foreground: Other).Should().BeFalse();
        Raise(blocked: IntPtr.Zero, foreground: IntPtr.Zero).Should().BeFalse();
    }

    [Fact]
    public void A_minimized_or_hidden_addin_stays_out_of_the_way()
    {
        // Minimizing IS the gesture that releases the block; popping back up would fight it.
        Raise(onScreen: false).Should().BeFalse();
    }
}
