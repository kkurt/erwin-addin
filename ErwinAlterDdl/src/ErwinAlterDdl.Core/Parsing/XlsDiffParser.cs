using HtmlAgilityPack;

namespace EliteSoft.Erwin.AlterDdl.Core.Parsing;

/// <summary>
/// Parses the CompleteCompare "XLS" output. Despite the .xls extension, erwin r10
/// emits an HTML <c>&lt;table&gt;</c> document with 4 columns: Type, Left Value,
/// Status, Right Value. Hierarchy is encoded as leading non-breaking spaces in
/// the Type column, 3 spaces = 1 level.
/// </summary>
public static class XlsDiffParser
{
    /// <summary>
    /// Parse the XLS file at <paramref name="xlsPath"/> into a flat list of rows.
    /// Caller traverses the list respecting <see cref="XlsDiffRow.IndentLevel"/>
    /// to reconstruct hierarchy.
    /// </summary>
    public static IReadOnlyList<XlsDiffRow> Parse(string xlsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xlsPath);
        if (!File.Exists(xlsPath)) throw new FileNotFoundException(xlsPath);

        var doc = new HtmlDocument();
        doc.Load(xlsPath, System.Text.Encoding.UTF8);
        return ParseDocument(doc);
    }

    /// <summary>
    /// Parse in-memory HTML string. Useful for unit tests.
    /// </summary>
    public static IReadOnlyList<XlsDiffRow> ParseHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return ParseDocument(doc);
    }

    private static List<XlsDiffRow> ParseDocument(HtmlDocument doc)
    {
        var rows = new List<XlsDiffRow>();
        var trNodes = doc.DocumentNode.SelectNodes("//tr");
        if (trNodes == null) return rows;

        foreach (var tr in trNodes)
        {
            var tds = tr.SelectNodes("./td");
            if (tds == null || tds.Count < 4) continue;

            var rawType = HtmlEntity.DeEntitize(tds[0].InnerText);
            var indent = CountLeadingIndent(rawType) / 3;
            var type = rawType.Trim();
            var left = HtmlEntity.DeEntitize(tds[1].InnerText).Trim();
            var status = HtmlEntity.DeEntitize(tds[2].InnerText).Trim();
            var right = HtmlEntity.DeEntitize(tds[3].InnerText).Trim();

            rows.Add(new XlsDiffRow(indent, type, left, status, right));
        }
        return rows;
    }

    private static int CountLeadingIndent(string rawType)
    {
        // erwin writes &nbsp; which HtmlEntity.DeEntitize converts to U+00A0. We
        // also tolerate regular spaces for robustness.
        int n = 0;
        foreach (var ch in rawType)
        {
            if (ch == ' ' || ch == ' ') n++;
            else break;
        }
        return n;
    }
}
