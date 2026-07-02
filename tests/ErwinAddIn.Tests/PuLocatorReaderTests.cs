using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Locator-parse coverage for <see cref="PuLocatorReader.ParseLocatorFromCaption"/>.
/// The window/MDI-child caption parse is the load-bearing half of MDI tab-switch
/// detection: the prior regex (<c>[^\s\]]+</c>) stopped at the first SPACE, so every
/// erwin model whose Mart path contains a space ("Core Banking/...") collapsed to the
/// same truncated locator <c>Mart://Mart/FibaBenzerleri/Core</c> and the switch
/// detector could never tell two CORE BANKING tabs apart. These fixtures pin the parse
/// against the REAL captions captured from the live erwin debug log.
/// </summary>
public class PuLocatorReaderTests
{
    // Real raw titles observed in the erwin window title (debug log 2026-06-30).
    private const string EftTitle =
        "[Mart://Mart/FibaBenzerleri/Core Banking/CORE BANKING CASH MANAGEMENT MONEY TRANSFER_EFT :  v2  : EFT";
    private const string EftLocator =
        "Mart://Mart/FibaBenzerleri/Core Banking/CORE BANKING CASH MANAGEMENT MONEY TRANSFER_EFT?VNO=2";

    private const string MetaRepoTitle =
        "[Mart://Mart/Kursat/MetaRepo :  v1  : ER_Diagram_164 *";
    private const string MetaRepoLocator = "Mart://Mart/Kursat/MetaRepo?VNO=1";

    // A bare MDI-child caption has no leading "[" - the parse must still work.
    private const string BulkChildCaption =
        "Mart://Mart/FibaBenzerleri/Core Banking/CORE BANKING CASH MANAGEMENT MONEY TRANSFER_BULK REMITTANCE EFT :  v1  : BULK REMITTANCE EFT";
    private const string BulkLocator =
        "Mart://Mart/FibaBenzerleri/Core Banking/CORE BANKING CASH MANAGEMENT MONEY TRANSFER_BULK REMITTANCE EFT?VNO=1";

    [Theory]
    [InlineData(EftTitle, EftLocator)]
    [InlineData(MetaRepoTitle, MetaRepoLocator)]
    [InlineData(BulkChildCaption, BulkLocator)]
    public void ParseLocatorFromCaption_extracts_full_locator_with_version(string caption, string expected)
    {
        PuLocatorReader.ParseLocatorFromCaption(caption).Should().Be(expected);
    }

    [Fact]
    public void ParseLocatorFromCaption_keeps_spaces_in_path_and_does_not_truncate_at_first_space()
    {
        string loc = PuLocatorReader.ParseLocatorFromCaption(EftTitle);

        // The old regex returned this truncated value for EVERY CORE BANKING model.
        loc.Should().NotBe("Mart://Mart/FibaBenzerleri/Core?VNO=2");
        loc.Should().Contain("Core Banking/CORE BANKING CASH MANAGEMENT MONEY TRANSFER_EFT");
    }

    [Fact]
    public void ParseLocatorFromCaption_distinguishes_two_models_sharing_the_same_path_prefix()
    {
        // The exact failure: two CORE BANKING tabs used to parse identically and the
        // switch detector never fired. They must now produce DIFFERENT locators.
        string eft = PuLocatorReader.ParseLocatorFromCaption(EftTitle);
        string bulk = PuLocatorReader.ParseLocatorFromCaption(BulkChildCaption);

        eft.Should().NotBe(bulk);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("erwin Data Modeler")]                         // no model open
    [InlineData("[C:\\models\\local.erwin] - erwin Data Modeler")] // local file model, not Mart
    public void ParseLocatorFromCaption_returns_empty_for_non_mart_caption(string caption)
    {
        PuLocatorReader.ParseLocatorFromCaption(caption).Should().BeEmpty();
    }
}
