using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for <see cref="PredefinedColumnService.IsLockedColumnName(System.Collections.Generic.IEnumerable{PredefinedColumn}, string)"/>,
/// the name-only guard that EXEMPTS locked predefined columns from naming
/// standards. Regression fixed 2026-06-09 (user choice "Muaf tut"): a suffix
/// rule (e.g. "_DATE" on DateTime columns) used to rename "CreateDate" ->
/// "CreateDate_DATE", which broke the name-based locked-order match and shoved
/// the column to the end of the table. The exemption runs BEFORE the rename, so
/// the locked column keeps its admin name and stays in its defined slot.
/// </summary>
public class PredefinedColumnLockedNameTests
{
    private static List<PredefinedColumn> Cols() => new()
    {
        new PredefinedColumn { Id = 1, ColumnName = "ID",         IsLocked = true },
        new PredefinedColumn { Id = 2, ColumnName = "CreatedBy",  IsLocked = true },
        new PredefinedColumn { Id = 3, ColumnName = "CreateDate", IsLocked = true },
        new PredefinedColumn { Id = 4, ColumnName = "Note",       IsLocked = false }, // suggested, editable
    };

    [Fact]
    public void Locked_column_name_is_recognized()
        => PredefinedColumnService.IsLockedColumnName(Cols(), "CreateDate").Should().BeTrue();

    [Fact]
    public void Match_is_case_insensitive()
        => PredefinedColumnService.IsLockedColumnName(Cols(), "createDATE").Should().BeTrue();

    [Fact]
    public void Naming_suffixed_name_is_NOT_a_locked_name()
        // The bug shape: once renamed, the column no longer matches - which is
        // exactly why we must exempt it BEFORE the rename can happen.
        => PredefinedColumnService.IsLockedColumnName(Cols(), "CreateDate_DATE").Should().BeFalse();

    [Fact]
    public void Non_locked_predefined_column_is_not_exempt()
        => PredefinedColumnService.IsLockedColumnName(Cols(), "Note").Should().BeFalse();

    [Fact]
    public void Unknown_column_is_not_locked()
        => PredefinedColumnService.IsLockedColumnName(Cols(), "Amount").Should().BeFalse();

    [Fact]
    public void Null_or_empty_inputs_are_false()
    {
        PredefinedColumnService.IsLockedColumnName(null, "ID").Should().BeFalse();
        PredefinedColumnService.IsLockedColumnName(Cols(), "").Should().BeFalse();
        PredefinedColumnService.IsLockedColumnName(Cols(), null).Should().BeFalse();
    }

    /// <summary>
    /// End-to-end shape of the reported bug + the fix, expressed purely through
    /// the existing wedge helper. Admin order: ID, CreatedBy, CreateDate,
    /// ModifyBy, ModifyDate (all locked).
    /// </summary>
    [Fact]
    public void Renamed_locked_column_is_wedged_but_admin_name_is_not()
    {
        var locked = new[] { "ID", "CreatedBy", "CreateDate", "ModifyBy", "ModifyDate" };

        // BUG: naming renamed the middle locked column in place; the locked set
        // still looks for "CreateDate", so "CreateDate_DATE" falls out of the
        // block and is moved to the end (matches the screenshot).
        var renamedOrder = new[] { "ID", "CreatedBy", "CreateDate_DATE", "ModifyBy", "ModifyDate" };
        PredefinedColumnService.ComputeColumnsWedgedInLockedBlock(renamedOrder, locked)
            .Should().Equal("CreateDate_DATE");

        // FIX: the exemption keeps the admin name, so nothing is wedged and the
        // defined order is preserved.
        var exemptOrder = new[] { "ID", "CreatedBy", "CreateDate", "ModifyBy", "ModifyDate" };
        PredefinedColumnService.ComputeColumnsWedgedInLockedBlock(exemptOrder, locked)
            .Should().BeEmpty();
    }
}
