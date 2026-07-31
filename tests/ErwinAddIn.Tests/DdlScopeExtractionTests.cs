using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// "Only Selected Objects" scope, derived from the generated script.
///
/// The accurate source is erwin's own Object Filter page, but the OnFE fast path never builds
/// that page - it opens the wizard hidden and calls straight into it - so on that route the
/// script is the only evidence of what the user selected. Before this, the gate silently checked
/// the WHOLE model instead: two selected tables produced 43 issues over 397 objects.
///
/// Both spellings are emitted because the walk may know a table either way.
/// </summary>
public class DdlScopeExtractionTests
{
    [Fact]
    public void A_bracketed_create_yields_both_the_qualified_name_and_the_leaf()
    {
        var names = ApprovalBlockingRuleGate.ExtractTableNamesFromDdl(
            "CREATE TABLE [dbo].[E_60Log]\n(\n  [CreatedBy] NVARCHAR(64) NOT NULL\n)");

        names.Should().Contain("E_60Log");
        names.Should().Contain("dbo.E_60Log");
    }

    [Fact]
    public void Every_table_in_a_multi_table_script_is_captured()
    {
        var names = ApprovalBlockingRuleGate.ExtractTableNamesFromDdl(
            "CREATE TABLE [dbo].[TABLE_A] ( [X] INT )\ngo\nCREATE TABLE [dbo].[TABLE_B] ( [Y] INT )\ngo");

        names.Should().Contain("TABLE_A").And.Contain("TABLE_B");
    }

    [Theory]
    [InlineData("ALTER TABLE [dbo].[TABLE_A] ADD [Z] INT")]
    [InlineData("DROP TABLE [dbo].[TABLE_A]")]
    [InlineData("CREATE TABLE IF NOT EXISTS \"dbo\".\"TABLE_A\" (x int)")]
    [InlineData("create table dbo.TABLE_A (x int)")]
    [InlineData("CREATE TABLE TABLE_A (x int)")]
    public void The_statement_kind_quoting_and_qualification_do_not_matter(string ddl)
    {
        ApprovalBlockingRuleGate.ExtractTableNamesFromDdl(ddl).Should().Contain("TABLE_A");
    }

    [Fact]
    public void Column_and_property_names_are_not_mistaken_for_tables()
    {
        // The script also carries sp_addextendedproperty calls naming columns; only the
        // CREATE/ALTER/DROP TABLE targets are scope.
        var names = ApprovalBlockingRuleGate.ExtractTableNamesFromDdl(
            "CREATE TABLE [dbo].[E_60Log] ( [CreatedBy] NVARCHAR(64) NOT NULL )\ngo\n"
            + "EXEC sp_addextendedproperty @name = 'MS_Description', @value = 'x',\n"
            + "  @level0type = 'SCHEMA', @level0name = 'dbo',\n"
            + "  @level1type = 'TABLE', @level1name = 'E_60Log',\n"
            + "  @level2type = 'COLUMN', @level2name = 'CreatedBy'");

        names.Should().Contain("E_60Log");
        names.Should().NotContain("CreatedBy");
        names.Should().NotContain("MS_Description");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("-- nothing to see here")]
    public void No_script_yields_no_scope_so_the_caller_can_tell_it_failed(string ddl)
    {
        // Must stay EMPTY rather than throw: the caller distinguishes "no scope" from "some
        // scope" to decide whether it may proceed.
        ApprovalBlockingRuleGate.ExtractTableNamesFromDdl(ddl).Should().BeEmpty();
    }
}
