using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the pure locked-column ORDER enforcement helper
/// <see cref="PredefinedColumnService.ComputeColumnsWedgedInLockedBlock"/>.
/// Rule (user-confirmed 2026-06-07): predefined locked columns stay as a
/// contiguous block at the START of the table in SORT_ORDER; every user-added
/// column must sit AFTER them. The helper returns the non-locked columns the
/// user wedged in front of / between the locked block - those get moved to the
/// table end. No SCAPI, no DB.
/// </summary>
public class PredefinedColumnOrderTests
{
    private static List<string> Wedged(IReadOnlyList<string> order, params string[] locked)
        => PredefinedColumnService.ComputeColumnsWedgedInLockedBlock(order, locked);

    [Fact]
    public void Compliant_locked_block_at_start_user_cols_after_yields_nothing()
    {
        var order = new[] { "ID", "CREATED_AT", "CREATED_BY", "Name", "Amount" };
        Wedged(order, "ID", "CREATED_AT", "CREATED_BY").Should().BeEmpty();
    }

    [Fact]
    public void User_column_wedged_between_locked_columns_is_returned()
    {
        var order = new[] { "ID", "Name", "CREATED_AT", "CREATED_BY", "Amount" };
        // "Name" sits between ID and the rest of the locked block -> wedged.
        Wedged(order, "ID", "CREATED_AT", "CREATED_BY").Should().Equal("Name");
    }

    [Fact]
    public void User_column_in_front_of_locked_block_is_returned()
    {
        var order = new[] { "Name", "ID", "CREATED_AT", "CREATED_BY" };
        Wedged(order, "ID", "CREATED_AT", "CREATED_BY").Should().Equal("Name");
    }

    [Fact]
    public void Multiple_wedged_columns_returned_in_order()
    {
        var order = new[] { "ID", "U1", "CREATED_AT", "U2", "CREATED_BY", "U3" };
        // U1 and U2 are before the last locked (CREATED_BY); U3 is after -> not wedged.
        Wedged(order, "ID", "CREATED_AT", "CREATED_BY").Should().Equal("U1", "U2");
    }

    [Fact]
    public void User_columns_only_after_the_locked_block_are_not_wedged()
    {
        var order = new[] { "ID", "CREATED_AT", "U1", "U2", "U3" };
        Wedged(order, "ID", "CREATED_AT").Should().BeEmpty();
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var order = new[] { "id", "name", "created_at" };
        Wedged(order, "ID", "CREATED_AT").Should().Equal("name");
    }

    [Fact]
    public void Locked_names_not_present_do_not_extend_the_block()
    {
        // Only CREATED_AT is present; PURGED_AT is a locked rule with no column.
        // The block ends at the last PRESENT locked column (CREATED_AT, index 1),
        // so the trailing user column is not wedged.
        var order = new[] { "ID", "CREATED_AT", "Name" };
        Wedged(order, "ID", "CREATED_AT", "PURGED_AT").Should().BeEmpty();
    }

    [Fact]
    public void No_locked_column_present_yields_nothing()
    {
        var order = new[] { "Name", "Amount" };
        Wedged(order, "ID", "CREATED_AT").Should().BeEmpty();
    }

    [Fact]
    public void Empty_inputs_yield_nothing()
    {
        Wedged(new string[0], "ID").Should().BeEmpty();
        PredefinedColumnService.ComputeColumnsWedgedInLockedBlock(new[] { "A", "B" }, new string[0])
            .Should().BeEmpty();
    }
}
