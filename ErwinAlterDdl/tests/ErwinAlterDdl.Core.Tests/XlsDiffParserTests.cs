using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

public class XlsDiffParserTests
{
    private const string SampleHtml = """
        <html><head><Title>Report</Title></head><body>
        <table border=1>
          <CAPTION>Table Description</CAPTION>
          <THEAD valign="top"><TR><TH>Type</TH><TH>Left Value</TH><TH>Status</TH><TH>Right Value</TH></TR></THEAD>
          <TBODY>
            <tr><td>&nbsp;&nbsp;&nbsp;Model</td><td>AchModel</td><td>Equal</td><td>AchModel</td></tr>
            <tr><td>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;Entity/Table</td><td>CUSTOMER</td><td>Equal</td><td>CUSTOMER</td></tr>
            <tr><td>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;Attribute/Column</td><td>mobile_phone</td><td>Not Equal</td><td>mobile_no</td></tr>
            <tr><td>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;Physical Data Type</td><td>varchar(100)</td><td>Not Equal</td><td>varchar(250)</td></tr>
          </TBODY>
        </table>
        </body></html>
        """;

    [Fact]
    public void ParseHtml_returns_expected_row_count()
    {
        var rows = XlsDiffParser.ParseHtml(SampleHtml);
        rows.Should().HaveCount(4);
    }

    [Fact]
    public void ParseHtml_extracts_columns_correctly()
    {
        var rows = XlsDiffParser.ParseHtml(SampleHtml);
        var modelRow = rows[0];
        modelRow.Type.Should().Be("Model");
        modelRow.LeftValue.Should().Be("AchModel");
        modelRow.Status.Should().Be("Equal");
        modelRow.RightValue.Should().Be("AchModel");
    }

    [Fact]
    public void ParseHtml_decodes_indent_to_levels()
    {
        var rows = XlsDiffParser.ParseHtml(SampleHtml);
        rows[0].IndentLevel.Should().Be(1); // Model -> 3 spaces -> level 1
        rows[1].IndentLevel.Should().Be(2); // Entity/Table -> 6 spaces
        rows[2].IndentLevel.Should().Be(3); // Attribute/Column -> 9
        rows[3].IndentLevel.Should().Be(4); // property -> 12
    }

    [Fact]
    public void IsNotEqual_is_case_insensitive()
    {
        var rows = XlsDiffParser.ParseHtml(SampleHtml);
        rows.Count(r => r.IsNotEqual).Should().Be(2);
        rows[0].IsNotEqual.Should().BeFalse();
        rows[2].IsNotEqual.Should().BeTrue();
    }

    [Fact]
    public void ParseHtml_preserves_Turkish_characters_in_values()
    {
        const string html = """
            <html><body><table>
              <TR><TH>Type</TH><TH>Left Value</TH><TH>Status</TH><TH>Right Value</TH></TR>
              <tr><td>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;Entity/Table</td><td>KAYIT_ÖZETİ</td><td>Not Equal</td><td>KAYIT_ÖZETİ_YENİ</td></tr>
            </table></body></html>
            """;
        var rows = XlsDiffParser.ParseHtml(html);
        rows.Should().HaveCount(1);
        rows[0].LeftValue.Should().Be("KAYIT_ÖZETİ");
        rows[0].RightValue.Should().Be("KAYIT_ÖZETİ_YENİ");
    }

    [Fact]
    public void ParseHtml_on_empty_document_returns_empty()
    {
        var rows = XlsDiffParser.ParseHtml("<html><body></body></html>");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void ParseHtml_skips_rows_with_less_than_four_cells()
    {
        const string html = """
            <html><body><table>
              <TR><TH>Type</TH><TH>Left</TH></TR>
              <tr><td>broken</td><td>only two</td></tr>
              <tr><td>   Entity/Table</td><td>X</td><td>Equal</td><td>X</td></tr>
            </table></body></html>
            """;
        var rows = XlsDiffParser.ParseHtml(html);
        rows.Should().HaveCount(1);
        rows[0].Type.Should().Be("Entity/Table");
    }
}
