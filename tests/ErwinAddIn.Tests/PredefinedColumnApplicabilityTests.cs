using System;
using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// <see cref="PredefinedColumnService.GetApplicableNames(IEnumerable{PredefinedColumn}, Func{string,string})"/>
/// returns only the predefined columns that apply to a SPECIFIC entity (unconditional + conditional
/// rows whose gating UDP matches). 2026-07-08 bug: the caller used GetAll() across ALL table classes,
/// so "OID" (predefined only for TableClass='Parametre') was treated as predefined on a
/// TableClass='Log' table and wrongly skipped from the glossary.
/// </summary>
public class PredefinedColumnApplicabilityTests
{
    private static PredefinedColumn Unconditional(string name) =>
        new() { ColumnName = name }; // DependsOnUdpId null => IsUnconditional

    private static PredefinedColumn Conditional(string name, string udpName, string udpValue) =>
        new() { ColumnName = name, DependsOnUdpId = 1, DependsOnUdpName = udpName, DependsOnUdpValue = udpValue };

    // A UDP reader backed by a dict (name -> value); unassigned UDPs return "".
    private static Func<string, string> Reader(params (string udp, string val)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (udp, val) in pairs) map[udp] = val;
        return name => map.TryGetValue(name, out var v) ? v : "";
    }

    [Fact]
    public void Unconditional_columns_always_apply()
    {
        var cols = new[] { Unconditional("CREATEDATE"), Unconditional("MODIFYDATE") };
        var names = PredefinedColumnService.GetApplicableNames(cols, Reader());
        names.Should().BeEquivalentTo(new[] { "CREATEDATE", "MODIFYDATE" });
    }

    [Fact]
    public void Conditional_column_applies_only_when_its_udp_matches()
    {
        var cols = new[] { Conditional("OID", "TableClass", "Parametre") };

        // Log table -> OID is NOT applicable (the reported bug: it must NOT be skipped from glossary).
        PredefinedColumnService.GetApplicableNames(cols, Reader(("TableClass", "Log")))
            .Should().NotContain("OID");

        // Parametre table -> OID IS applicable (predefined).
        PredefinedColumnService.GetApplicableNames(cols, Reader(("TableClass", "Parametre")))
            .Should().Contain("OID");
    }

    [Fact]
    public void Conditional_match_is_case_insensitive()
    {
        var cols = new[] { Conditional("OID", "TableClass", "Parametre") };
        PredefinedColumnService.GetApplicableNames(cols, Reader(("TableClass", "PARAMETRE")))
            .Should().Contain("OID");
    }

    [Fact]
    public void Conditional_column_not_applicable_when_udp_unassigned()
    {
        var cols = new[] { Conditional("OID", "TableClass", "Parametre") };
        // UDP not set on the entity -> reader returns "" -> condition cannot hold.
        PredefinedColumnService.GetApplicableNames(cols, Reader())
            .Should().NotContain("OID");
    }

    [Fact]
    public void Mixed_set_scopes_by_the_entity_table_class()
    {
        var cols = new[]
        {
            Unconditional("CREATEDATE"),
            Conditional("OID", "TableClass", "Parametre"),
            Conditional("LOGDATE", "TableClass", "Log"),
        };

        // A Log table: CREATEDATE (unconditional) + LOGDATE (Log) apply; OID (Parametre) does not.
        var logNames = PredefinedColumnService.GetApplicableNames(cols, Reader(("TableClass", "Log")));
        logNames.Should().BeEquivalentTo(new[] { "CREATEDATE", "LOGDATE" });
        logNames.Should().NotContain("OID");
    }

    [Fact]
    public void Udp_is_read_once_per_distinct_name()
    {
        int reads = 0;
        var cols = new[]
        {
            Conditional("OID", "TableClass", "Parametre"),
            Conditional("PRMSTART", "TableClass", "Parametre"),
            Conditional("PRMEND", "TableClass", "Parametre"),
        };
        Func<string, string> counting = name => { reads++; return "Parametre"; };

        var names = PredefinedColumnService.GetApplicableNames(cols, counting);
        names.Should().BeEquivalentTo(new[] { "OID", "PRMSTART", "PRMEND" });
        reads.Should().Be(1); // TableClass read once, cached for the other two rows
    }
}
