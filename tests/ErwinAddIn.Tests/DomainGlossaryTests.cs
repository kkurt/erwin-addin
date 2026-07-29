using System.Collections.Generic;
using System.Linq;

using EliteSoft.Erwin.AddIn.Forms;
using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// "Domain Like Glossary" (2026-07-28). The admin stores this mode in its OWN
/// DG_TABLE_MAPPING row (MAPPING_CODE='DOMAIN_GLOSSARY') with three marker rows -
/// _DOMAIN_ / _COLUMN_ / _NAMING_STANDARD_ - and no _MATCH_ row at all: the user picks the
/// glossary row through a popup (domain, then that domain's columns) instead of the row being
/// matched by the erwin column name.
///
/// These tests pin the three contracts the feature rests on, all DB-free:
///   1. marker rows are NEVER classified as value mappings (a marker that leaked into the value
///      list would be written onto every matched column as a UDP literally named "_DOMAIN_");
///   2. the domain -&gt; column index and its case-insensitive lookups;
///   3. the typeahead filter behind both combos, and the picker's anti-reopen guard.
/// </summary>
public class DomainGlossaryTests
{
    private static DomainGlossaryRow Row(string domain, string column, string namingStandard = "")
    {
        var row = new DomainGlossaryRow { Domain = domain, ColumnName = column, NamingStandard = namingStandard };
        return row;
    }

    // --- 1. Marker classification -------------------------------------------------------

    // The enum is internal, so it cannot appear in a public [Theory] signature - one fact each.
    [Fact]
    public void Domain_marker_is_configuration_not_a_value_mapping()
    {
        DomainGlossaryService.ClassifyRow("DOMAIN_NAME", "_DOMAIN_")
            .Should().Be(DomainGlossaryService.MappingRole.DomainMarker);
    }

    [Fact]
    public void Column_marker_is_configuration_not_a_value_mapping()
    {
        DomainGlossaryService.ClassifyRow("COLUMN_NAME", "_COLUMN_")
            .Should().Be(DomainGlossaryService.MappingRole.ColumnMarker);
    }

    [Fact]
    public void Naming_standard_marker_is_configuration_not_a_value_mapping()
    {
        DomainGlossaryService.ClassifyRow("NAMING_STD", "_NAMING_STANDARD_")
            .Should().Be(DomainGlossaryService.MappingRole.NamingStandardMarker);
    }

    [Fact]
    public void Ordinary_rows_are_value_mappings()
    {
        DomainGlossaryService.ClassifyRow("OWNER", "DATA_OWNER")
            .Should().Be(DomainGlossaryService.MappingRole.Value);
    }

    [Theory]
    [InlineData("_domain_")]
    [InlineData("_Domain_")]
    [InlineData("_DOMAIN")]
    [InlineData("DOMAIN_")]
    public void Marker_comparison_is_ordinal_and_case_sensitive(string nearMiss)
    {
        // Admin writes the literals verbatim; a near-miss is an ordinary mapping, not a marker.
        // This is deliberate - the same ordinal `==` the standard glossary uses for _MATCH_.
        DomainGlossaryService.ClassifyRow("SOME_COL", nearMiss)
            .Should().Be(DomainGlossaryService.MappingRole.Value);
    }

    [Theory]
    [InlineData("", "_DOMAIN_")]
    [InlineData(null, "ANY")]
    [InlineData("SOME_COL", "")]
    [InlineData("SOME_COL", null)]
    public void Rows_missing_either_side_are_ignored(string sourceColumn, string targetField)
    {
        DomainGlossaryService.ClassifyRow(sourceColumn, targetField)
            .Should().Be(DomainGlossaryService.MappingRole.Ignored);
    }

    // --- 2. The domain -> column index --------------------------------------------------

    [Fact]
    public void Index_groups_columns_under_their_domain_and_orders_both()
    {
        var index = DomainGlossaryService.BuildIndex(new[]
        {
            Row("Party", "CUSTOMER_ID"),
            Row("Party", "ACCOUNT_NO"),
            Row("Money", "AMOUNT"),
        });

        DomainGlossaryService.DomainsIn(index).Should().Equal("Money", "Party");
        DomainGlossaryService.ColumnsIn(index, "Party").Should().Equal("ACCOUNT_NO", "CUSTOMER_ID");
    }

    [Fact]
    public void Lookups_are_case_insensitive_on_both_levels()
    {
        var index = DomainGlossaryService.BuildIndex(new[] { Row("Party", "CUSTOMER_ID") });

        DomainGlossaryService.ColumnsIn(index, "pArTy").Should().Equal("CUSTOMER_ID");
        DomainGlossaryService.FindIn(index, "PARTY", "customer_id").Should().NotBeNull();
    }

    [Fact]
    public void Surrounding_whitespace_in_a_lookup_is_tolerated()
    {
        // The picker passes raw combo text, which routinely carries a stray space.
        var index = DomainGlossaryService.BuildIndex(new[] { Row("Party", "CUSTOMER_ID") });

        DomainGlossaryService.FindIn(index, "  Party ", " CUSTOMER_ID ").Should().NotBeNull();
    }

    [Fact]
    public void Unknown_keys_return_empty_or_null_never_throw()
    {
        var index = DomainGlossaryService.BuildIndex(new[] { Row("Party", "CUSTOMER_ID") });

        DomainGlossaryService.ColumnsIn(index, "Nope").Should().BeEmpty();
        DomainGlossaryService.ColumnsIn(index, null).Should().BeEmpty();
        DomainGlossaryService.FindIn(index, "Party", "NOPE").Should().BeNull();
        DomainGlossaryService.FindIn(index, null, null).Should().BeNull();
    }

    [Fact]
    public void Duplicate_domain_column_pairs_keep_the_first_row()
    {
        // The external query owns the ordering, so a later duplicate is data noise, not an update.
        var first = Row("Party", "CUSTOMER_ID", "STD_A");
        var index = DomainGlossaryService.BuildIndex(new[] { first, Row("Party", "CUSTOMER_ID", "STD_B") });

        DomainGlossaryService.FindIn(index, "Party", "CUSTOMER_ID").NamingStandard.Should().Be("STD_A");
    }

    [Fact]
    public void Rows_without_a_domain_or_column_are_dropped()
    {
        // Neither is reachable through the two combos, so caching them would only inflate Count.
        var index = DomainGlossaryService.BuildIndex(new[]
        {
            Row("", "CUSTOMER_ID"),
            Row("Party", ""),
            Row("Party", "CUSTOMER_ID"),
        });

        DomainGlossaryService.DomainsIn(index).Should().Equal("Party");
        index.Values.Sum(d => d.Count).Should().Be(1);
    }

    // --- 3. Typeahead filter + the anti-reopen guard ------------------------------------

    [Fact]
    public void Filter_narrows_case_insensitively_on_a_contains_match()
    {
        var source = new[] { "CUSTOMER_ID", "CUSTOMER_NAME", "ACCOUNT_NO" };

        DomainGlossaryPickerForm.Filter(source, "cust")
            .Should().Equal("CUSTOMER_ID", "CUSTOMER_NAME");
        DomainGlossaryPickerForm.Filter(source, "_no")
            .Should().Equal("ACCOUNT_NO");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Filter_with_no_term_restores_the_full_list(string term)
    {
        var source = new[] { "A", "B" };

        DomainGlossaryPickerForm.Filter(source, term).Should().Equal("A", "B");
    }

    [Fact]
    public void Filter_on_an_empty_source_is_empty_not_null()
    {
        DomainGlossaryPickerForm.Filter(new List<string>(), "x").Should().BeEmpty();
        DomainGlossaryPickerForm.Filter(null, "x").Should().BeEmpty();
    }

    [Fact]
    public void Filter_yields_nothing_when_the_term_matches_no_item()
    {
        DomainGlossaryPickerForm.Filter(new[] { "CUSTOMER_ID" }, "zzz").Should().BeEmpty();
    }

    // --- 4. Name composition from the naming-standard regex ------------------------------
    // The glossary's column value is a human LABEL ("ADRES TIPI" - 181 of 209 production rows
    // contain spaces), so it can never be the physical name. The naming standard is the real
    // contract: a modeler-chosen name plus a fixed tail. The picker asks for the former and
    // appends the latter.

    [Theory]
    [InlineData("^[A-Z0-9_]+_ADRES_TIP$", "_ADRES_TIP")]
    [InlineData("^[A-Z0-9_]+_BLV_CAD$", "_BLV_CAD")]
    [InlineData("^[A-Z0-9_]+_ADDR_TYPE$", "_ADDR_TYPE")]
    public void The_fixed_tail_is_extracted_from_the_standard(string pattern, string expected)
    {
        DomainGlossaryPickerForm.LiteralSuffix(pattern).Should().Be(expected);
    }

    [Fact]
    public void An_all_literal_standard_yields_the_whole_token()
    {
        // "^ADRES_TIP$" pins the name outright: the tail IS the name, and an empty prefix
        // composes to exactly it.
        DomainGlossaryPickerForm.LiteralSuffix("^ADRES_TIP$").Should().Be("ADRES_TIP");
        DomainGlossaryPickerForm.ComposeName("", "ADRES_TIP").Should().Be("ADRES_TIP");
    }

    [Theory]
    [InlineData("^[A-Z_]+$")]   // no literal tail at all
    [InlineData("")]
    [InlineData(null)]
    public void A_standard_with_no_fixed_tail_yields_nothing_to_append(string pattern)
    {
        // The user then types the WHOLE name; nothing is fabricated on their behalf.
        DomainGlossaryPickerForm.LiteralSuffix(pattern).Should().BeEmpty();
    }

    [Fact]
    public void Composition_trims_the_typed_prefix_but_keeps_the_tail_verbatim()
    {
        DomainGlossaryPickerForm.ComposeName("  MUSTERI ", "_ADRES_TIP").Should().Be("MUSTERI_ADRES_TIP");
        DomainGlossaryPickerForm.ComposeName(null, "_ADRES_TIP").Should().Be("_ADRES_TIP");
        DomainGlossaryPickerForm.ComposeName("MUSTERI", null).Should().Be("MUSTERI");
    }

    [Fact]
    public void The_composed_name_is_what_the_standard_actually_accepts()
    {
        // End to end: the tail is extracted, composed, and the result satisfies the same regex
        // the picker gates Apply on - while the raw glossary label never could.
        const string pattern = "^[A-Z0-9_]+_ADRES_TIP$";
        string composed = DomainGlossaryPickerForm.ComposeName(
            "MUSTERI", DomainGlossaryPickerForm.LiteralSuffix(pattern));

        composed.Should().Be("MUSTERI_ADRES_TIP");
        System.Text.RegularExpressions.Regex.IsMatch(composed, pattern).Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch("ADRES TIPI", pattern).Should().BeFalse();
    }

    [Fact]
    public void Matching_is_case_sensitive_so_an_uppercase_class_actually_enforces_uppercase()
    {
        // The patterns spell out [A-Z0-9_]; folding case would silently accept exactly the
        // lowercase names the author wrote that class to reject.
        System.Text.RegularExpressions.Regex.IsMatch("musteri_adres_tip", "^[A-Z0-9_]+_ADRES_TIP$")
            .Should().BeFalse();
    }

    // --- 5. New-column placeholder recognition -------------------------------------------
    // The focus trigger has to tell a column NAME cell from every other cell in the grid, and
    // for a brand-new column the only clue is the placeholder text.

    [Theory]
    [InlineData("_default_")]   // what the grid/tree EDITOR is seeded with
    [InlineData("<default>")]   // what SCAPI reports for the same column
    [InlineData("PLEASE CHANGE IT")]
    public void Both_spellings_of_the_new_column_placeholder_are_recognised(string text)
    {
        // Matching only the SCAPI form made the picker skip every brand-new column: the cell
        // editor never contains "<default>", it contains "_default_".
        ValidationCoordinatorService.IsNewColumnNamePlaceholder(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("ADRES_TIP")]
    [InlineData("NVARCHAR(64)")]
    [InlineData("")]
    [InlineData(null)]
    public void A_real_value_is_not_a_placeholder(string text)
    {
        ValidationCoordinatorService.IsNewColumnNamePlaceholder(text).Should().BeFalse();
    }

    [Fact]
    public void A_column_never_picked_before_always_prompts()
    {
        ValidationCoordinatorService.ShouldSkipDomainGlossaryPrompt(appliedName: null, currentName: "ANY")
            .Should().BeFalse();
    }

    [Fact]
    public void The_edit_the_picker_itself_made_does_not_reopen_the_picker()
    {
        // The popup fires on every column edit, and the picker's own rename IS an edit. Without
        // this the next tick would reopen it for the change we just made, forever.
        ValidationCoordinatorService.ShouldSkipDomainGlossaryPrompt("CUSTOMER_ID", "CUSTOMER_ID")
            .Should().BeTrue();
    }

    [Fact]
    public void A_later_rename_diverges_and_prompts_again()
    {
        ValidationCoordinatorService.ShouldSkipDomainGlossaryPrompt("CUSTOMER_ID", "CUSTOMER_CODE")
            .Should().BeFalse();
        // Ordinal: a case-only rename is a real rename in erwin, so it must prompt.
        ValidationCoordinatorService.ShouldSkipDomainGlossaryPrompt("CUSTOMER_ID", "customer_id")
            .Should().BeFalse();
    }
}
