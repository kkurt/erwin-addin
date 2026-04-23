using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AlterDdl.Core.Parsing;

/// <summary>
/// CREATE DDL parser. Extracts column datatypes, constraint column lists
/// (PK / Unique / FK), and standalone CREATE INDEX definitions from the
/// FEModel_DDL output so the SQL emitter can fill in concrete column lists
/// instead of TODO placeholders.
///
/// The parser is intentionally permissive: we skip rows we cannot classify
/// rather than failing, since the input varies across MSSQL / Oracle / Db2.
/// </summary>
public static class CreateDdlParser
{
    private static readonly Regex CreateTableHeader = new(
        @"CREATE\s+TABLE\s+(?:(?<schema>[\[""]?[\w]+[\]""]?)\s*\.\s*)?(?<table>[\[""]?[\w]+[\]""]?)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CreateIndexStmt = new(
        @"CREATE\s+(?<unique>UNIQUE\s+)?INDEX\s+(?<name>[\[""]?[\w]+[\]""]?)\s+ON\s+(?:(?<schema>[\[""]?[\w]+[\]""]?)\s*\.\s*)?(?<table>[\[""]?[\w]+[\]""]?)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlterTableAddConstraint = new(
        @"ALTER\s+TABLE\s+(?:(?<schema>[\[""]?[\w]+[\]""]?)\s*\.\s*)?(?<table>[\[""]?[\w]+[\]""]?)\s+ADD\s+\(?\s*CONSTRAINT\s+(?<name>[\[""]?[\w]+[\]""]?)\s+(?<kind>PRIMARY\s+KEY|UNIQUE|FOREIGN\s+KEY)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReferencesFragment = new(
        @"REFERENCES\s+(?:(?<pschema>[\[""]?[\w]+[\]""]?)\s*\.\s*)?(?<ptable>[\[""]?[\w]+[\]""]?)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parse the full DDL. All lookups on the returned
    /// <see cref="DdlColumnMap"/> are case-insensitive on identifiers.
    /// </summary>
    public static DdlColumnMap Parse(string ddl)
    {
        ArgumentNullException.ThrowIfNull(ddl);

        var columnsByTable = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var keyGroupColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var foreignKeys = new Dictionary<string, ForeignKeyInfo>(StringComparer.OrdinalIgnoreCase);

        ParseCreateTables(ddl, columnsByTable, keyGroupColumns, foreignKeys);
        ParseCreateIndexes(ddl, keyGroupColumns);
        ParseAlterTableAddConstraints(ddl, keyGroupColumns, foreignKeys);

        return new DdlColumnMap(columnsByTable, keyGroupColumns, foreignKeys);
    }

    private static void ParseCreateTables(
        string ddl,
        Dictionary<string, Dictionary<string, string>> columnsByTable,
        Dictionary<string, string[]> keyGroupColumns,
        Dictionary<string, ForeignKeyInfo> foreignKeys)
    {
        int searchStart = 0;
        while (searchStart < ddl.Length)
        {
            var headerMatch = CreateTableHeader.Match(ddl, searchStart);
            if (!headerMatch.Success) break;

            var table = StripQuoting(headerMatch.Groups["table"].Value);
            int bodyStart = headerMatch.Index + headerMatch.Length;
            int bodyEnd = FindMatchingCloseParen(ddl, bodyStart);
            if (bodyEnd < 0) break;

            var body = ddl[bodyStart..bodyEnd];
            searchStart = bodyEnd + 1;

            if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(body)) continue;

            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var piece in SplitBodyPieces(body))
            {
                if (TryParseConstraint(piece, table, out var kgName, out var kgCols, out var fk))
                {
                    if (fk is not null && !string.IsNullOrEmpty(kgName))
                        foreignKeys.TryAdd(kgName, fk);
                    else if (!string.IsNullOrEmpty(kgName) && kgCols.Length > 0)
                        keyGroupColumns.TryAdd(kgName, kgCols);
                    continue;
                }

                if (TryParseColumn(piece, out var name, out var type))
                    columns.TryAdd(name, type);
            }
            if (columns.Count > 0) columnsByTable.TryAdd(table, columns);
        }
    }

    private static void ParseCreateIndexes(string ddl, Dictionary<string, string[]> keyGroupColumns)
    {
        foreach (Match m in CreateIndexStmt.Matches(ddl))
        {
            var name = StripQuoting(m.Groups["name"].Value);
            int bodyStart = m.Index + m.Length;
            int bodyEnd = FindMatchingCloseParen(ddl, bodyStart);
            if (bodyEnd < 0) continue;
            var cols = ExtractColumnNames(ddl[bodyStart..bodyEnd]);
            if (cols.Length > 0) keyGroupColumns.TryAdd(name, cols);
        }
    }

    private static void ParseAlterTableAddConstraints(
        string ddl,
        Dictionary<string, string[]> keyGroupColumns,
        Dictionary<string, ForeignKeyInfo> foreignKeys)
    {
        foreach (Match m in AlterTableAddConstraint.Matches(ddl))
        {
            var childTable = StripQuoting(m.Groups["table"].Value);
            var name = StripQuoting(m.Groups["name"].Value);
            var kind = Regex.Replace(m.Groups["kind"].Value, @"\s+", " ").ToUpperInvariant();
            int bodyStart = m.Index + m.Length;
            int bodyEnd = FindMatchingCloseParen(ddl, bodyStart);
            if (bodyEnd < 0) continue;
            var childCols = ExtractColumnNames(ddl[bodyStart..bodyEnd]);
            if (childCols.Length == 0) continue;

            if (kind == "FOREIGN KEY")
            {
                var tail = ddl[(bodyEnd + 1)..Math.Min(ddl.Length, bodyEnd + 400)];
                var refMatch = ReferencesFragment.Match(tail);
                if (!refMatch.Success) continue;
                int refBodyStart = bodyEnd + 1 + refMatch.Index + refMatch.Length;
                int refBodyEnd = FindMatchingCloseParen(ddl, refBodyStart);
                if (refBodyEnd < 0) continue;
                var parentTable = StripQuoting(refMatch.Groups["ptable"].Value);
                var parentCols = ExtractColumnNames(ddl[refBodyStart..refBodyEnd]);
                foreignKeys.TryAdd(name, new ForeignKeyInfo(childTable, childCols, parentTable, parentCols));
            }
            else
            {
                keyGroupColumns.TryAdd(name, childCols);
            }
        }
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

    private static IEnumerable<string> SplitBodyPieces(string body)
    {
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
                if (piece.Length > 0) yield return piece;
                start = i + 1;
            }
        }
    }

    private static string[] ExtractColumnNames(string colList)
    {
        var cols = new List<string>();
        foreach (var piece in SplitBodyPieces(colList))
        {
            var tokens = Tokenize(piece);
            if (tokens.Count == 0) continue;
            var name = StripQuoting(tokens[0]);
            if (!string.IsNullOrWhiteSpace(name)) cols.Add(name);
        }
        return cols.ToArray();
    }

    private static bool TryParseConstraint(
        string piece,
        string tableName,
        out string name,
        out string[] columns,
        out ForeignKeyInfo? foreignKey)
    {
        name = string.Empty;
        columns = Array.Empty<string>();
        foreignKey = null;

        var upper = piece.TrimStart().ToUpperInvariant();
        if (upper.StartsWith("CONSTRAINT", StringComparison.Ordinal))
        {
            var m = Regex.Match(
                piece,
                @"^\s*CONSTRAINT\s+(?<name>[\[""]?[\w]+[\]""]?)\s+(?<kind>PRIMARY\s+KEY|UNIQUE|FOREIGN\s+KEY)\s*\((?<cols>[^)]*)\)(?<tail>.*)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return false;
            name = StripQuoting(m.Groups["name"].Value);
            var cols = ExtractColumnNames(m.Groups["cols"].Value);
            columns = cols;
            var kind = Regex.Replace(m.Groups["kind"].Value, @"\s+", " ").ToUpperInvariant();
            if (kind == "FOREIGN KEY")
            {
                var tail = m.Groups["tail"].Value;
                var refM = ReferencesFragment.Match(tail);
                if (refM.Success)
                {
                    int refBodyStart = refM.Index + refM.Length; // right after '('
                    int refBodyEnd = FindMatchingCloseParen(tail, refBodyStart);
                    if (refBodyEnd >= 0)
                    {
                        var parentCols = ExtractColumnNames(tail[refBodyStart..refBodyEnd]);
                        var parentTable = StripQuoting(refM.Groups["ptable"].Value);
                        foreignKey = new ForeignKeyInfo(tableName, cols, parentTable, parentCols);
                    }
                }
                return true;
            }
            return true;
        }

        // Bare "PRIMARY KEY (cols)" or "UNIQUE (cols)" (no CONSTRAINT keyword, unnamed).
        if (upper.StartsWith("PRIMARY KEY", StringComparison.Ordinal)
            || upper.StartsWith("UNIQUE", StringComparison.Ordinal)
            || upper.StartsWith("FOREIGN KEY", StringComparison.Ordinal)
            || upper.StartsWith("CHECK", StringComparison.Ordinal))
        {
            return true; // recognized-but-unnamed; caller skips it
        }

        return false;
    }

    private static bool TryParseColumn(string piece, out string name, out string type)
    {
        name = string.Empty;
        type = string.Empty;
        if (string.IsNullOrWhiteSpace(piece)) return false;

        var upper = piece.TrimStart().ToUpperInvariant();
        if (upper.StartsWith("PRIMARY KEY", StringComparison.Ordinal)
            || upper.StartsWith("FOREIGN KEY", StringComparison.Ordinal)
            || upper.StartsWith("CONSTRAINT", StringComparison.Ordinal)
            || upper.StartsWith("UNIQUE", StringComparison.Ordinal)
            || upper.StartsWith("CHECK", StringComparison.Ordinal))
            return false;

        var tokens = Tokenize(piece);
        if (tokens.Count < 2) return false;
        name = StripQuoting(tokens[0]);

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

/// <summary>
/// Info captured for a foreign-key constraint parsed from the DDL.
/// </summary>
public sealed record ForeignKeyInfo(
    string ChildTable,
    string[] ChildColumns,
    string ParentTable,
    string[] ParentColumns);

/// <summary>
/// Read-only lookups derived from a CREATE DDL script:
///   - (table, column) -> datatype
///   - key-group name (PK / UNIQUE / Index) -> column list
///   - FK name -> child / parent table + columns
/// </summary>
public sealed class DdlColumnMap
{
    private readonly Dictionary<string, Dictionary<string, string>> _tables;
    private readonly Dictionary<string, string[]> _keyGroupColumns;
    private readonly Dictionary<string, ForeignKeyInfo> _foreignKeys;

    internal DdlColumnMap(
        Dictionary<string, Dictionary<string, string>> tables,
        Dictionary<string, string[]> keyGroupColumns,
        Dictionary<string, ForeignKeyInfo> foreignKeys)
    {
        _tables = tables;
        _keyGroupColumns = keyGroupColumns;
        _foreignKeys = foreignKeys;
    }

    public int TableCount => _tables.Count;

    public int KeyGroupCount => _keyGroupColumns.Count;

    public int ForeignKeyCount => _foreignKeys.Count;

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

    public bool TryGetKeyGroupColumns(string keyGroupName, out string[] columns)
    {
        if (_keyGroupColumns.TryGetValue(keyGroupName, out var c))
        {
            columns = c;
            return true;
        }
        columns = Array.Empty<string>();
        return false;
    }

    public bool TryGetForeignKey(string foreignKeyName, out ForeignKeyInfo info)
    {
        if (_foreignKeys.TryGetValue(foreignKeyName, out var fk))
        {
            info = fk;
            return true;
        }
        info = default!;
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
