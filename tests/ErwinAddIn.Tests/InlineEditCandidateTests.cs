using System.Collections.Generic;
using System.Linq;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Inline-edit recheck candidates (2026-07-09): when the user starts an in-place edit
/// (Model Explorer F2 or the Properties-pane grid - both plain Win32 'Edit' controls), the
/// edit's INITIAL text is the OLD value. Matching it against in-memory snapshots identifies
/// which attribute the edit could belong to, so an EXISTING column's rename/retype - which has
/// no other observer (no editor window, no attribute-count delta) - can be re-validated after
/// the edit commits. These tests pin the pure selection semantics: exact Ordinal matches only,
/// name matches before datatype matches, and a hard cap with an explicit overflow signal
/// (never a silent truncation).
/// </summary>
public class InlineEditCandidateTests
{
    private static (string?, string?, string?, string?) Snap(string? id, string? table, string? name, string? type)
        => (id, table, name, type);

    [Fact]
    public void Matches_exact_name_ordinal_only()
    {
        var snaps = new List<(string?, string?, string?, string?)>
        {
            Snap("id1", "TEST", "Pre_Abc", "varchar(18)"),
            Snap("id2", "TEST", "pre_abc", "varchar(18)"),   // different case: no match
            Snap("id3", "LOG",  "OID",     "int"),
        };

        var (candidates, overflowed) = ValidationCoordinatorService.SelectInlineEditCandidates(snaps, "Pre_Abc", 8);

        candidates.Should().ContainSingle().Which.Should().Be(("id1", "TEST"));
        overflowed.Should().BeFalse();
    }

    [Fact]
    public void Name_matches_come_before_datatype_matches()
    {
        // 'varchar(18)' is BOTH a column name (contrived) and a datatype elsewhere:
        // the name hit must be first so a tight cap keeps the most specific candidates.
        var snaps = new List<(string?, string?, string?, string?)>
        {
            Snap("typeHit", "T1", "ColA",        "varchar(18)"),
            Snap("nameHit", "T2", "varchar(18)", "int"),
        };

        var (candidates, _) = ValidationCoordinatorService.SelectInlineEditCandidates(snaps, "varchar(18)", 8);

        candidates.Should().HaveCount(2);
        candidates[0].Should().Be(("nameHit", "T2"));
        candidates[1].Should().Be(("typeHit", "T1"));
    }

    [Fact]
    public void Cap_is_enforced_and_reported_as_overflow()
    {
        var snaps = Enumerable.Range(1, 12)
            .Select(i => Snap($"id{i}", $"T{i}", "Col", "int"))
            .ToList();

        var (candidates, overflowed) = ValidationCoordinatorService.SelectInlineEditCandidates(snaps, "Col", 8);

        candidates.Should().HaveCount(8);
        overflowed.Should().BeTrue();
    }

    [Fact]
    public void A_snapshot_matching_both_name_and_type_is_added_once()
    {
        var snaps = new List<(string?, string?, string?, string?)>
        {
            Snap("id1", "TEST", "int", "int"), // name == type == edited text
        };

        var (candidates, overflowed) = ValidationCoordinatorService.SelectInlineEditCandidates(snaps, "int", 8);

        candidates.Should().ContainSingle().Which.Should().Be(("id1", "TEST"));
        overflowed.Should().BeFalse();
    }

    [Fact]
    public void Empty_text_or_incomplete_snapshots_yield_nothing()
    {
        var snaps = new List<(string?, string?, string?, string?)>
        {
            Snap(null, "TEST", "Col", "int"),   // no object id: cannot be rechecked
            Snap("id2", null,  "Col", "int"),   // no table: cannot be resolved
        };

        ValidationCoordinatorService.SelectInlineEditCandidates(snaps, "Col", 8).Candidates.Should().BeEmpty();
        ValidationCoordinatorService.SelectInlineEditCandidates(snaps, "", 8).Candidates.Should().BeEmpty();
        ValidationCoordinatorService.SelectInlineEditCandidates(null, "Col", 8).Candidates.Should().BeEmpty();
    }
}

/// <summary>
/// The selection-scoped fingerprint (2026-07-10) reads erwin's Overview-pane Static text to learn
/// which single entity is selected, so a Properties-pane dropdown datatype edit (no "Edit" focus)
/// is caught. These tests pin the pure parse: only a single, unambiguous entity selection yields a
/// name; nothing-selected and multi-select both return null so the caller no-ops rather than
/// fingerprinting the wrong entity.
/// </summary>
public class OverviewSelectionParseTests
{
    [Theory]
    [InlineData("DTHMODEL (DTH_TXN)", "DTH_TXN")]
    [InlineData("MODEL (TEST)", "TEST")]
    [InlineData("  MODEL (Musteri)  ", "Musteri")]
    public void Single_entity_selection_is_parsed(string overviewText, string expected)
    {
        Win32Helper.ParseSelectedEntityFromOverviewText(overviewText).Should().Be(expected);
    }

    [Theory]
    [InlineData("DTHMODEL")]                 // nothing selected - no parentheses
    [InlineData("MODEL (3 objects)")]        // multi-select count
    [InlineData("MODEL (1 object)")]         // single-count phrasing, still not an entity name
    [InlineData("MODEL (A, B)")]             // several names listed
    [InlineData("MODEL ()")]                 // empty parens
    [InlineData("")]
    [InlineData(null)]
    public void Non_single_selection_yields_null(string overviewText)
    {
        Win32Helper.ParseSelectedEntityFromOverviewText(overviewText).Should().BeNull();
    }
}
