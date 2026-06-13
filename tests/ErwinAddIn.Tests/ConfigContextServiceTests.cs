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

    // Local .erwin file locators (2026-06-13): the canonical key must equal
    // what the Configuration Warning dialog displays, so an admin-registered
    // row matches the lookup exactly. Scheme-less per user decision 2026-06-13.
    [Theory]
    [InlineData(@"erwin://C:\work\FromPowerDesignerRepo\Demo\SQL_DEV\EK_KART\9\EK_KART.erwin",
                @"C:\work\FromPowerDesignerRepo\Demo\SQL_DEV\EK_KART\9\EK_KART.erwin")]
    [InlineData(@"erwin://C:\models\A.erwin?&something=1", @"C:\models\A.erwin")]
    [InlineData(@"  erwin://C:\models\A.erwin\  ", @"C:\models\A.erwin")]
    public void ParseLocalModelPath_canonicalizes_file_locators(string locator, string expected)
    {
        ConfigContextService.ParseLocalModelPath(locator).Should().Be(expected);
    }

    [Theory]
    [InlineData("erwin://Mart://Mart/Demo/SQL/1_DEV/KKR?&version=6")] // Mart -> handled by ParseMartPath
    [InlineData(@"C:\models\A.erwin")]                                // no erwin:// scheme
    [InlineData("erwin://")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseLocalModelPath_returns_null_for_non_local_locators(string locator)
    {
        ConfigContextService.ParseLocalModelPath(locator!).Should().BeNull();
    }

    // Mirrors the UDP-sync apply policy (APPLY_UDP_CHANGES, 2026-06-08): the
    // UPPER_SNAKE member names match the values the admin stores, so
    // GetEffectiveEnum/ParseEffectiveEnum resolve them directly.
    public enum UdpApplyMode { WARN_AND_APPLY, SILENTLY_APPLY, OFF }

    [Theory]
    [InlineData("WARN_AND_APPLY", UdpApplyMode.WARN_AND_APPLY)]
    [InlineData("warn_and_apply", UdpApplyMode.WARN_AND_APPLY)] // case-insensitive
    [InlineData("SILENTLY_APPLY", UdpApplyMode.SILENTLY_APPLY)]
    [InlineData("  OFF  ", UdpApplyMode.OFF)]                   // trimmed
    [InlineData("off", UdpApplyMode.OFF)]
    public void ParseEffectiveEnum_parses_apply_policy_values(string raw, UdpApplyMode expected)
    {
        ConfigContextService.ParseEffectiveEnum(raw, UdpApplyMode.WARN_AND_APPLY).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("99")] // out-of-range numeric is rejected by the IsDefined guard
    public void ParseEffectiveEnum_falls_back_to_default_for_blank_or_unknown(string? raw)
    {
        ConfigContextService.ParseEffectiveEnum(raw, UdpApplyMode.WARN_AND_APPLY)
            .Should().Be(UdpApplyMode.WARN_AND_APPLY);
    }
}
