using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// <see cref="PredefinedColumnService.GetApplicableNames(IEnumerable{PredefinedColumn}, object)"/>
/// returns only the predefined columns that apply to a SPECIFIC entity - unconditional
/// rows plus rows whose ordered AND/OR condition list
/// (<see cref="PredefinedColumn.Conditions"/>) folds TRUE. WP#280 replaced the single
/// "When UDP=value" gate with the same multi-condition contract naming rules use, so
/// applicability is now the shared
/// <see cref="NamingValidationEngine.AreConditionsSatisfied"/> fold. A migrated single
/// condition is one ORDER_INDEX=0 term, so its behaviour is bit-for-bit the old gate
/// (regression guard for the 2026-07-08 bug: "OID" predefined only for
/// TableClass='Parametre' must NOT count as predefined on a 'Log' table).
/// </summary>
public class PredefinedColumnApplicabilityTests
{
    // A live-entity stand-in: resolves "Entity.Physical.{Udp}" to a backing value,
    // "" for any UDP the entity was never assigned (the sparse-storage "condition
    // cannot hold" outcome). Deterministic - no ModelRootProvider / COM. MUST be
    // public: the evaluator resolves .Properties via `dynamic` from the main
    // assembly, which cannot bind a non-public type's members across the boundary.
    public sealed class FakeEntity
    {
        private readonly Dictionary<string, string> _udps;
        public FakeEntity(params (string udp, string val)[] pairs)
        {
            _udps = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (udp, val) in pairs) _udps[udp] = val;
        }
        public FakeProp Properties(string path)
        {
            const string prefix = "Entity.Physical.";
            string name = path != null && path.StartsWith(prefix) ? path.Substring(prefix.Length) : path;
            return new FakeProp(_udps.TryGetValue(name ?? "", out var v) ? v : "");
        }
    }

    public sealed class FakeProp
    {
        public FakeProp(object value) { Value = value; }
        public object Value { get; }
    }

    private static NamingRuleCondition Cond(int order, string connector, string udp, string csv) =>
        new()
        {
            OrderIndex = order,
            Connector = connector,
            DependsOnUdpId = 100 + order, // any non-null id => UDP source
            DependsOnUdpName = udp,
            DependsOnPropertyValues = csv,
        };

    private static PredefinedColumn Unconditional(string name) => new() { ColumnName = name };

    private static PredefinedColumn Conditional(string name, params NamingRuleCondition[] conds)
    {
        var c = new PredefinedColumn { ColumnName = name };
        foreach (var x in conds) c.Conditions.Add(x);
        return c;
    }

    [Fact]
    public void Unconditional_columns_always_apply()
    {
        var cols = new[] { Unconditional("CREATEDATE"), Unconditional("MODIFYDATE") };
        var names = PredefinedColumnService.GetApplicableNames(cols, new FakeEntity());
        names.Should().BeEquivalentTo(new[] { "CREATEDATE", "MODIFYDATE" });
    }

    // --- Single migrated condition (ORDER_INDEX=0) == old single-UDP gate ---

    [Fact]
    public void Single_condition_applies_only_when_its_udp_matches()
    {
        var cols = new[] { Conditional("OID", Cond(0, null, "TableClass", "Parametre")) };

        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "Log")))
            .Should().NotContain("OID");

        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "Parametre")))
            .Should().Contain("OID");
    }

    [Fact]
    public void Single_condition_match_is_case_insensitive()
    {
        var cols = new[] { Conditional("OID", Cond(0, null, "TableClass", "Parametre")) };
        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "PARAMETRE")))
            .Should().Contain("OID");
    }

    [Fact]
    public void Single_condition_not_applicable_when_udp_unassigned()
    {
        var cols = new[] { Conditional("OID", Cond(0, null, "TableClass", "Parametre")) };
        // UDP not set on the entity -> reads "" -> condition cannot hold.
        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity())
            .Should().NotContain("OID");
    }

    [Fact]
    public void Dangling_udp_reference_does_not_apply_to_every_table()
    {
        // A term whose UDP FK is set but whose resolved name is empty (the gating UDP
        // was deleted while the condition row survives; the loader's LEFT JOIN yields a
        // NULL name). The gate can never hold, so the column must NOT apply - the old
        // predefined path guarded this with an empty-name skip; without the evaluator
        // fix it would fall into the vacuous-true branch and apply to EVERY table.
        var dangling = new NamingRuleCondition
        {
            OrderIndex = 0,
            Connector = null,
            DependsOnUdpId = 999,          // source id present...
            DependsOnUdpName = "",         // ...but unresolved (dangling FK)
            DependsOnPropertyValues = "Parametre",
        };
        var cols = new[] { Conditional("GHOSTCOL", dangling) };

        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "Parametre")))
            .Should().NotContain("GHOSTCOL");
        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity())
            .Should().NotContain("GHOSTCOL");
    }

    [Fact]
    public void Mixed_set_scopes_by_the_entity_table_class()
    {
        var cols = new[]
        {
            Unconditional("CREATEDATE"),
            Conditional("OID", Cond(0, null, "TableClass", "Parametre")),
            Conditional("LOGDATE", Cond(0, null, "TableClass", "Log")),
        };

        var logNames = PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "Log")));
        logNames.Should().BeEquivalentTo(new[] { "CREATEDATE", "LOGDATE" });
        logNames.Should().NotContain("OID");
    }

    [Fact]
    public void Empty_csv_is_an_existence_check_any_nonempty_value_matches()
    {
        // DEPENDS_ON_PROPERTY_VALUES empty + a source set => "any non-empty value".
        var cols = new[] { Conditional("HASCLASS", Cond(0, null, "TableClass", "")) };
        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "Anything")))
            .Should().Contain("HASCLASS");
        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity())
            .Should().NotContain("HASCLASS");
    }

    // --- Multi-condition AND/OR fold (the WP#280 feature) ---

    [Fact]
    public void And_requires_both_terms()
    {
        // TableClass IN (Log) AND Application IN (UYG547)
        var cols = new[]
        {
            Conditional("LOGAPPCOL",
                Cond(0, null, "TableClass", "Log"),
                Cond(1, "AND", "Application", "UYG547")),
        };

        PredefinedColumnService
            .GetApplicableNames(cols, new FakeEntity(("TableClass", "Log"), ("Application", "UYG547")))
            .Should().Contain("LOGAPPCOL");

        // one term wrong -> AND fails
        PredefinedColumnService
            .GetApplicableNames(cols, new FakeEntity(("TableClass", "Log"), ("Application", "OTHER")))
            .Should().NotContain("LOGAPPCOL");
        PredefinedColumnService
            .GetApplicableNames(cols, new FakeEntity(("TableClass", "History"), ("Application", "UYG547")))
            .Should().NotContain("LOGAPPCOL");
    }

    [Fact]
    public void Or_applies_when_either_term_matches()
    {
        // TableClass IN (Log) OR TableClass IN (History)
        var cols = new[]
        {
            Conditional("AUDITCOL",
                Cond(0, null, "TableClass", "Log"),
                Cond(1, "OR", "TableClass", "History")),
        };

        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "Log")))
            .Should().Contain("AUDITCOL");
        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "History")))
            .Should().Contain("AUDITCOL");
        PredefinedColumnService.GetApplicableNames(cols, new FakeEntity(("TableClass", "Parametre")))
            .Should().NotContain("AUDITCOL");
    }

    [Fact]
    public void Fold_is_strict_left_to_right_no_precedence()
    {
        // ((C0 AND C1) OR C2): (TableClass=Log AND Application=UYG547) OR TableClass=History
        var cols = new[]
        {
            Conditional("COL",
                Cond(0, null, "TableClass", "Log"),
                Cond(1, "AND", "Application", "UYG547"),
                Cond(2, "OR", "TableClass", "History")),
        };

        // C0 AND C1 true -> true
        PredefinedColumnService
            .GetApplicableNames(cols, new FakeEntity(("TableClass", "Log"), ("Application", "UYG547")))
            .Should().Contain("COL");
        // C0 AND C1 false, but C2 (History) true -> (false OR true) = true
        PredefinedColumnService
            .GetApplicableNames(cols, new FakeEntity(("TableClass", "History")))
            .Should().Contain("COL");
        // C0 true, C1 false, C2 false -> (true AND false) OR false = false
        PredefinedColumnService
            .GetApplicableNames(cols, new FakeEntity(("TableClass", "Log"), ("Application", "OTHER")))
            .Should().NotContain("COL");
    }
}
