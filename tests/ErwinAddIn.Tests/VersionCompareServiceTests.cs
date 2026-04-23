using System;
using System.Linq;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the pure logic inside <see cref="VersionCompareService"/>.
/// COM-bound code paths (save-to-temp, CompleteCompare, Mart PU open) require
/// a live erwin instance and are smoke-tested by hand - see the follow-up note
/// in the Phase 3.F commit message.
/// </summary>
public class VersionCompareServiceTests
{
    // -------------------- ResolveDialect --------------------

    [Theory]
    [InlineData("SQL Server", "MSSQL")]
    [InlineData("sql server 2019", "MSSQL")]
    [InlineData("MSSQL", "MSSQL")]
    [InlineData("Azure SQL Database", "MSSQL")]
    [InlineData("Oracle", "Oracle")]
    [InlineData("Oracle 19c", "Oracle")]
    [InlineData("Db2", "Db2")]
    [InlineData("DB2 z/OS v12", "Db2")]
    [InlineData("", "MSSQL")] // empty falls back to safe default
    [InlineData(" Oracle ", "Oracle")] // whitespace tolerant
    public void ResolveDialect_maps_known_target_servers(string input, string expected)
    {
        VersionCompareService.ResolveDialect(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("Teradata")]
    [InlineData("UnknownRdbms")]
    public void ResolveDialect_falls_back_to_mssql_for_unknown(string input)
    {
        // Safe default so downstream Registry.Resolve always succeeds.
        VersionCompareService.ResolveDialect(input).Should().Be("MSSQL");
    }

    // -------------------- PlanTargetVersions --------------------

    [Fact]
    public void PlanTargetVersions_clean_model_excludes_current_version()
    {
        var rows = VersionCompareService.PlanTargetVersions(currentVersion: 5, isDirty: false);
        rows.Select(r => r.Version).Should().Equal(4, 3, 2, 1);
        rows.All(r => !r.Label.Contains("current saved copy")).Should().BeTrue();
    }

    [Fact]
    public void PlanTargetVersions_dirty_model_includes_current_version_with_hint_label()
    {
        var rows = VersionCompareService.PlanTargetVersions(currentVersion: 5, isDirty: true);
        rows.Select(r => r.Version).Should().Equal(5, 4, 3, 2, 1);
        rows[0].Label.Should().Be("v5 (current saved copy)");
        rows.Skip(1).All(r => r.Label == $"v{r.Version}").Should().BeTrue();
    }

    [Fact]
    public void PlanTargetVersions_clean_v1_returns_empty()
    {
        // Version 1 clean: nothing older to compare against.
        VersionCompareService.PlanTargetVersions(1, isDirty: false).Should().BeEmpty();
    }

    [Fact]
    public void PlanTargetVersions_dirty_v1_still_allows_self_compare()
    {
        var rows = VersionCompareService.PlanTargetVersions(1, isDirty: true);
        rows.Should().ContainSingle();
        rows[0].Version.Should().Be(1);
        rows[0].Label.Should().Be("v1 (current saved copy)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void PlanTargetVersions_invalid_current_returns_empty(int invalid)
    {
        VersionCompareService.PlanTargetVersions(invalid, isDirty: true).Should().BeEmpty();
    }

    // -------------------- ProbeDirty (reflection-based) --------------------

    [Fact]
    public void ProbeDirty_reads_Modified_property_on_PU_poco()
    {
        var svc = new VersionCompareService(new object(), new PuWithModified(true), null!);
        var result = svc.ProbeDirty();
        result.IsDirty.Should().BeTrue();
        result.Source.Should().Be("Modified");
    }

    [Fact]
    public void ProbeDirty_picks_first_available_dirty_flag_name()
    {
        // PU exposes only IsDirty (not Modified / IsModified). The probe must
        // walk the fallback list until it hits one that answers.
        var svc = new VersionCompareService(new object(), new PuWithIsDirty(false), null!);
        var result = svc.ProbeDirty();
        result.IsDirty.Should().BeFalse();
        result.Source.Should().Be("IsDirty");
    }

    [Fact]
    public void ProbeDirty_unknown_PU_shape_defaults_to_dirty_so_combo_is_not_under_filled()
    {
        // A POCO exposing none of the known flags - probe can't answer.
        // Documented behavior: return dirty=true so the version combo
        // surfaces all candidates including the current version. Missing a
        // valid comparison target is worse than offering an extra one.
        var svc = new VersionCompareService(new object(), new PuWithoutFlags(), null!);
        var result = svc.ProbeDirty();
        result.IsDirty.Should().BeTrue();
        result.Source.Should().Be("(unknown)");
    }

    // -------------------- helpers --------------------

    private sealed class PuWithModified
    {
        public PuWithModified(bool modified) { Modified = modified; }
        public bool Modified { get; }
    }

    private sealed class PuWithIsDirty
    {
        public PuWithIsDirty(bool dirty) { IsDirty = dirty; }
        public bool IsDirty { get; }
    }

    private sealed class PuWithoutFlags
    {
        public string Name => "no-flag";
    }
}
