using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for <see cref="DbmsLabelComposer"/>, the pure brand+version
/// -> display-label mapping behind the model/config DBMS-mismatch check. Guards
/// the 2026-06-22 "SQL Server 15" raw-engine-version leak and the 2026-05-16
/// Oracle-suffix regression: both surfaced a misleading DBMS label in the
/// mismatch popup and broke the same-brand comparison key.
/// </summary>
public class DbmsLabelComposerTests
{
    [Theory]
    // SQL Server reports its engine major; erwin shows the marketing label and
    // groups the newer engines into the release pairs its status bar displays.
    [InlineData("SQL Server", "15", "SQL Server 2019/2022")]
    [InlineData("SQL Server", "16", "SQL Server 2019/2022")]
    [InlineData("SQL Server", "13", "SQL Server 2016/2017")]
    [InlineData("SQL Server", "14", "SQL Server 2016/2017")]
    [InlineData("SQL Server", "12", "SQL Server 2014")]
    [InlineData("SQL Server", "11", "SQL Server 2012")]
    [InlineData("SQL Server", "10", "SQL Server 2008")]
    // Oracle keeps its era suffix - existing behavior that must not regress.
    [InlineData("Oracle", "21", "Oracle 21c")]
    [InlineData("Oracle", "19", "Oracle 19c")]
    [InlineData("Oracle", "11", "Oracle 11g")]
    [InlineData("Oracle", "10", "Oracle 10g")]
    [InlineData("Oracle", "9", "Oracle 9i")]
    // Brands without an erwin naming convention pass the version through as-is.
    [InlineData("PostgreSQL", "16", "PostgreSQL 16")]
    [InlineData("MySQL", "8", "MySQL 8")]
    // An unknown / future SQL Server engine major falls back to raw passthrough
    // rather than an invented pairing, so it surfaces visibly for a map update.
    [InlineData("SQL Server", "17", "SQL Server 17")]
    public void Compose_maps_engine_version_to_erwin_label(string brand, string version, string expected)
    {
        DbmsLabelComposer.Compose(brand, version).Should().Be(expected);
    }

    [Theory]
    // A blank version yields the brand alone (no trailing space or raw number).
    [InlineData("SQL Server", "", "SQL Server")]
    [InlineData("SQL Server", "   ", "SQL Server")]
    public void Compose_returns_brand_only_when_version_blank(string brand, string version, string expected)
    {
        DbmsLabelComposer.Compose(brand, version).Should().Be(expected);
    }

    [Fact]
    // A null version is the IsNullOrWhiteSpace branch too - the model PU can
    // report no Target_Server_Version. Kept as a Fact so InlineData(null) does
    // not trip xUnit1012 on the non-nullable parameter.
    public void Compose_returns_brand_when_version_is_null()
    {
        DbmsLabelComposer.Compose("Oracle", null!).Should().Be("Oracle");
    }
}
