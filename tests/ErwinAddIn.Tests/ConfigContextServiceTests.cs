using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the pure parsing logic inside
/// <see cref="ConfigContextService"/>. The DB-bound path (LookupConfigId /
/// LoadConfigRow) needs a live RepoDbContext and is exercised end-to-end
/// when the add-in attaches to a Mart-hosted model.
/// </summary>
public class ConfigContextServiceTests
{
    [Theory]
    // Modern shape returned by PU.Locator on Mart-hosted PUs.
    [InlineData("erwin://Mart://Mart/Kursat/MetaRepo?&version=6&modelLongId={ABC123}", "Kursat/MetaRepo")]
    // Bare short form used by the bridge / Worker.
    [InlineData("Mart://Mart/Kursat/MetaRepo?VNO=3", "Kursat/MetaRepo")]
    // Fully qualified short form (DdlGenerationService writes these).
    [InlineData("mart://Mart/Kursat/MetaRepo?TRC=NO;SRV=host;PRT=18170;VNO=2", "Kursat/MetaRepo")]
    // Multi-level library path (admin's BuildPath supports nesting).
    [InlineData("mart://Mart/Kursat/SubLib/MyModel?VNO=1", "Kursat/SubLib/MyModel")]
    // Locator without query parameters - boundary is end-of-string.
    [InlineData("erwin://Mart://Mart/Kursat/MetaRepo", "Kursat/MetaRepo")]
    // Locator with a leading '&' instead of '?' (observed in the wild on
    // some servicing levels of erwin DM r10).
    [InlineData("erwin://Mart://Mart/Kursat/MetaRepo&version=2", "Kursat/MetaRepo")]
    // Mixed case for the schema portion.
    [InlineData("ERWIN://MART://MART/Kursat/MetaRepo?VNO=1", "Kursat/MetaRepo")]
    public void ParseMartPath_extracts_path_stem(string locator, string expected)
    {
        ConfigContextService.ParseMartPath(locator).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // Local-file locator - no Mart://Mart/ in the string.
    [InlineData("erwin://c:\\tmp\\scapi_smoke\\v1.erwin")]
    // Mart catalog only, no path stem.
    [InlineData("mart://Mart?TRC=NO;SRV=host")]
    // Empty stem between catalog and query.
    [InlineData("mart://Mart/?VNO=1")]
    public void ParseMartPath_returns_null_for_non_mart_locators(string locator)
    {
        ConfigContextService.ParseMartPath(locator).Should().BeNull();
    }

    [Fact]
    public void ParseMartPath_returns_null_for_null_input()
    {
        ConfigContextService.ParseMartPath(null!).Should().BeNull();
    }
}
