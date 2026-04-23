using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AlterDdl.Core.Parsing;

/// <summary>
/// Minimal column-level CREATE TABLE parser. Input: the full CREATE DDL
/// produced by <c>FEModel_DDL</c>. Output: a lookup of each table's column
/// list with raw datatype strings. Intended to feed the SQL emitter when it
/// needs an AttributeAdded's target datatype that is not present in the CC
/// XLS (the Physical Data Type row only appears for changed columns).
///
/// Intentionally permissive - we strip just enough syntax to pair a column
/// name with its datatype. Anything beyond (constraints, defaults, identity
/// clauses) is ignored at this phase. Phase 3.D can evolve it further.
/// </summary>
public static class CreateDdlParser
{
    private static readonly Regex CreateTableHeader = new(
        @"CREATE\s+TABLE\s+(?:(?<schema>[\[""]?[\w]+[\]""]?)\s*\.\s*)?(?<table>[\[""]?[\w]+[\]""]?)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parse all CREATE TABLE statements in <paramref name="ddl"/>. Uses a
    /// paren-aware walk for the body so datatypes containing commas
    /// (DECIMAL(18,4)) do not confuse the splitter.
    /// </summary>
    public static DdlColumnMap Parse(string ddl)
    {
        ArgumentNullException.ThrowIfNull(ddl);
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        int searchStart = 0;
        while (searchStart < ddl.Length)
        {
            var headerMatch = CreateTableHeader.Match(ddl, searchStart);
            if (!headerMatch.Success) break;

            var table = StripQuoting(headerMatch.Groups["table"].Value);
            int bodyStart = headerMatch.Index + headerMatch.Length; // first char inside the outer `(`
            int bodyEnd = FindMatchingCloseParen(ddl, bodyStart);
            if (bodyEnd < 0) break;

            var body = ddl[bodyStart..bodyEnd];
            searchStart = bodyEnd + 1;

            if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(body)) continue;

            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, type) in SplitColumns(body))
                columns.TryAdd(name, type);
            if (columns.Count > 0) map.TryAdd(table, columns);
        }

        return new DdlColumnMap(map);
    }

    /// <summary>
    /// Given an index immediately AFTER an opening '(', find the index of
    /// the matching ')' (tracking nested parens). Returns -1 if unbalanced.
    /// </summary>
    private static int FindMatchingCloseParen(string s, int startAfterOpen)
    {
        int depth = 1;
        for (int i = startAfterOpen; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static IEnumerable<(string Name, string Type)> SplitColumns(string body)
    {
        // A CREATE TABLE body has columns separated by commas, but commas
        // also appear inside datatype parens (VARCHAR(100), DECIMAL(18,4)).
        // Walk char-by-char tracking paren depth.
        int depth = 0;
        int start = 0;
        for (int i = 0; i <= body.Length; i++)
        {
            char c = i < body.Length ? body[i] : ',';
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                var piece = body[start..i].Trim();
                if (TryParseColumn(piece, out var name, out var type))
                    yield return (name, type);
                start = i + 1;
            }
        }
    }

    private static bool TryParseColumn(string piece, out string name, out string type)
    {
        name = string.Empty;
        type = string.Empty;
        if (string.IsNullOrWhiteSpace(piece)) return false;

        // Skip table-level constraints (PRIMARY KEY, FOREIGN KEY, CONSTRAINT, UNIQUE, CHECK).
        var upper = piece.TrimStart().ToUpperInvariant();
        if (upper.StartsWith("PRIMARY KEY", StringComparison.Ordinal)
            || upper.StartsWith("FOREIGN KEY", StringComparison.Ordinal)
            || upper.StartsWith("CONSTRAINT", StringComparison.Ordinal)
            || upper.StartsWith("UNIQUE", StringComparison.Ordinal)
            || upper.StartsWith("CHECK", StringComparison.Ordinal))
            return false;

        // First token = column name (possibly bracketed / quoted).
        var tokens = Tokenize(piece);
        if (tokens.Count < 2) return false;
        name = StripQuoting(tokens[0]);

        // Everything up to NULL / NOT NULL / DEFAULT / IDENTITY / COLLATE /
        // FOR BIT DATA is the datatype expression.
        var typeBuf = new System.Text.StringBuilder();
        foreach (var tok in tokens.Skip(1))
        {
            var tu = tok.ToUpperInvariant();
            if (tu == "NULL" || tu == "NOT" || tu == "DEFAULT" || tu == "IDENTITY"
                || tu == "COLLATE" || tu == "FOR" || tu == "GENERATED" || tu == "AS")
                break;
            if (typeBuf.Length > 0) typeBuf.Append(' ');
            typeBuf.Append(tok);
        }
        type = typeBuf.ToString().Trim();
        return type.Length > 0;
    }

    private static List<string> Tokenize(string s)
    {
        // Whitespace-delimited tokens, but keep parenthesized groups together.
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        int depth = 0;
        foreach (var c in s)
        {
            if (c == '(') { depth++; cur.Append(c); continue; }
            if (c == ')') { depth--; cur.Append(c); continue; }
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }

    private static string StripQuoting(string s) =>
        s.Trim().Trim('[', ']', '"', '\'');
}

/// <summary>Read-only per-table column-name -> datatype-string lookup.</summary>
public sealed class DdlColumnMap
{
    private readonly Dictionary<string, Dictionary<string, string>> _tables;

    internal DdlColumnMap(Dictionary<string, Dictionary<string, string>> tables)
    {
        _tables = tables;
    }

    public int TableCount => _tables.Count;

    public bool TryGetType(string tableName, string columnName, out string type)
    {
        if (_tables.TryGetValue(tableName, out var cols) && cols.TryGetValue(columnName, out var t))
        {
            type = t;
            return true;
        }
        type = string.Empty;
        return false;
    }

    public IEnumerable<string> TableNames => _tables.Keys;

    public IEnumerable<(string Table, string Column, string Type)> AllColumns()
    {
        foreach (var (t, cols) in _tables)
            foreach (var (c, ty) in cols)
                yield return (t, c, ty);
    }
}
