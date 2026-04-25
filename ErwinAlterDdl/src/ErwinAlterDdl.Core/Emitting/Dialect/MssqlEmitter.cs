using System.Text.RegularExpressions;

using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

namespace EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;

/// <summary>
/// MSSQL (SQL Server 2019+) alter DDL emitter. Phase 3.D polish fills in
/// concrete column lists for PK / UNIQUE / Index / FK when the v2 CREATE DDL
/// provides them and splits schema-qualified names into properly quoted parts.
/// </summary>
public sealed class MssqlEmitter : ISqlEmitter
{
    public string Dialect => "MSSQL";

    /// <summary>
    /// XLS-derived schema-by-entity-name lookup, valid only for the duration
    /// of a single <see cref="Emit"/> call. Stored in a [ThreadStatic] so
    /// the existing static <c>EmitXxx</c> helpers can read it without an
    /// extra parameter on every signature.
    /// </summary>
    [System.ThreadStatic]
    private static IReadOnlyDictionary<string, string>? t_xlsSchemas;

    public AlterDdlScript Emit(CompareResult compareResult)
    {
        ArgumentNullException.ThrowIfNull(compareResult);
        var stmts = new List<AlterStatement>();

        DdlColumnMap? rightCols = null;
        if (compareResult.RightDdl is { SqlPath: var p } && File.Exists(p))
        {
            try { rightCols = CreateDdlParser.Parse(File.ReadAllText(p)); }
            catch { /* best effort, fall back to TODO placeholder */ }
        }
        t_xlsSchemas = compareResult.SchemaByEntityName;
        try
        {

        foreach (var change in compareResult.Changes)
        {
            AlterStatement? emitted = change switch
            {
                EntityAdded ea => EmitEntityAdded(ea, rightCols),
                EntityDropped ed => EmitEntityDropped(ed, rightCols),
                EntityRenamed er => EmitEntityRenamed(er),
                SchemaMoved sm => EmitSchemaMoved(sm),
                AttributeAdded aa => EmitAttributeAdded(aa, rightCols),
                AttributeDropped ad => EmitAttributeDropped(ad, rightCols),
                AttributeRenamed ar => EmitAttributeRenamed(ar),
                AttributeTypeChanged at => EmitAttributeTypeChanged(at, rightCols),
                AttributeNullabilityChanged an => EmitAttributeNullability(an, rightCols),
                AttributeDefaultChanged ad2 => EmitAttributeDefault(ad2, rightCols),
                AttributeIdentityChanged ai => EmitAttributeIdentity(ai, rightCols),
                KeyGroupAdded ka => EmitKeyGroupAdded(ka, rightCols),
                KeyGroupDropped kd => EmitKeyGroupDropped(kd, rightCols),
                KeyGroupRenamed kr => EmitKeyGroupRenamed(kr),
                ForeignKeyAdded fa => EmitForeignKeyAdded(fa, rightCols),
                ForeignKeyDropped fd => EmitForeignKeyDropped(fd),
                ForeignKeyRenamed fr => EmitForeignKeyRenamed(fr),
                ViewAdded va => EmitViewAdded(va, rightCols),
                ViewDropped vd => new($"DROP VIEW {QuoteEntityName(vd.Target.Name, rightCols)};", $"drop view {vd.Target.Name}"),
                ViewRenamed vr => new($"EXEC sp_rename '{vr.OldName}', '{vr.Target.Name}';", $"rename view {vr.OldName} -> {vr.Target.Name}"),
                TriggerAdded ta => new($"-- TODO: CREATE TRIGGER {QuoteQualified(ta.Target.Name)} body from v2 model", $"add trigger {ta.Target.Name}"),
                TriggerDropped td => new($"DROP TRIGGER {QuoteQualified(td.Target.Name)};", $"drop trigger {td.Target.Name}"),
                TriggerRenamed tr => new($"EXEC sp_rename '{tr.OldName}', '{tr.Target.Name}';", $"rename trigger {tr.OldName} -> {tr.Target.Name}"),
                SequenceAdded sa => new($"-- TODO: CREATE SEQUENCE {QuoteQualified(sa.Target.Name)} <options from v2 model>", $"add sequence {sa.Target.Name}"),
                SequenceDropped sd => new($"DROP SEQUENCE {QuoteQualified(sd.Target.Name)};", $"drop sequence {sd.Target.Name}"),
                SequenceRenamed sr => new($"EXEC sp_rename '{sr.OldName}', '{sr.Target.Name}';", $"rename sequence {sr.OldName} -> {sr.Target.Name}"),
                _ => null,
            };
            if (emitted is not null) stmts.Add(emitted);
        }

        return new AlterDdlScript(Dialect, stmts);
        }
        finally
        {
            t_xlsSchemas = null;
        }
    }

    // ---------- Entity-level ----------

    private static AlterStatement EmitEntityAdded(EntityAdded ea, DdlColumnMap? rightCols)
    {
        // Prefer the verbatim CREATE TABLE block from the v2 CREATE DDL so
        // the user sees exactly what erwin would generate; fall back to a
        // TODO comment when the DDL is unavailable.
        var bare = UnqualifiedTable(ea.Target.Name);
        if (rightCols is not null && rightCols.TryGetCreateBlock(bare, out var block))
        {
            // erwin's FEModel_DDL writes the CREATE TABLE header schema-less
            // for some target servers, but the CC XLS / right map gives us
            // the real owner. Rewrite the header so the emitted body lines
            // up with the schema-prefixed ALTER statements emitted elsewhere.
            var withSchema = InjectSchemaIntoCreateBlock(block, bare, rightCols);
            // erwin's "Resolve Differences > Right Alter Script" output
            // brackets every column identifier; FEModel_DDL leaves them bare.
            // Bracket them ourselves so the verbatim CREATE matches what the
            // user sees in the GUI compare wizard.
            var quoted = QuoteColumnIdentifiersInCreateBody(withSchema);
            return new AlterStatement(
                Sql: quoted.TrimEnd() + ";",
                Comment: $"new entity {ea.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"-- TODO: copy CREATE TABLE body for {QuoteEntityName(ea.Target.Name, rightCols)} from v2 CREATE DDL",
            Comment: $"new entity {ea.Target.Name}");
    }

    /// <summary>
    /// Rewrite the <c>CREATE TABLE [schema.]name (</c> header in a verbatim
    /// CREATE block so the emitted name is schema-qualified using MSSQL
    /// quoting (<c>[schema].[name]</c>). Schema is resolved from the XLS map
    /// first (most reliable - CC always emits Entity/Table rows as
    /// <c>schema.table</c>), then from the v2 CREATE DDL header. If neither
    /// knows the schema, the block is returned unchanged.
    /// </summary>
    private static string InjectSchemaIntoCreateBlock(string block, string bareTableName, DdlColumnMap? rightCols)
    {
        var schema = ResolveSchema(bareTableName, rightCols);
        if (string.IsNullOrEmpty(schema)) return block;

        var pattern = new Regex(
            @"(CREATE\s+TABLE\s+)(?:[\[""]?\w+[\]""]?\s*\.\s*)?[\[""]?"
                + Regex.Escape(bareTableName)
                + @"[\]""]?(\s*\()",
            RegexOptions.IgnoreCase);
        var replacement = "$1" + Quote(schema) + "." + Quote(bareTableName) + "$2";
        return pattern.Replace(block, replacement, count: 1);
    }

    /// <summary>Schema lookup priority: XLS map first, then right CREATE DDL.</summary>
    private static string? ResolveSchema(string bareTableName, DdlColumnMap? rightCols)
    {
        var xls = t_xlsSchemas;
        if (xls is not null && xls.TryGetValue(bareTableName, out var xlsSch) && !string.IsNullOrEmpty(xlsSch))
            return xlsSch;
        if (rightCols is not null && rightCols.TryGetSchema(bareTableName, out var sch) && !string.IsNullOrEmpty(sch))
            return sch;
        return null;
    }

    /// <summary>
    /// Bracket bare column identifiers inside a verbatim CREATE TABLE body so
    /// the emitted SQL matches what erwin's GUI compare wizard produces (every
    /// column name in <c>[brackets]</c>). Lines whose first token is a
    /// constraint keyword (CONSTRAINT / PRIMARY / FOREIGN / etc.) are left
    /// untouched. Already-bracketed names stay untouched because <c>[</c> is
    /// not a word character so the regex's identifier group never matches.
    /// </summary>
    private static string QuoteColumnIdentifiersInCreateBody(string block)
    {
        int open = block.IndexOf('(');
        if (open < 0) return block;
        int close = FindMatchingCloseParen(block, open);
        if (close < 0) return block;

        string before = block[..(open + 1)];
        string body = block.Substring(open + 1, close - open - 1);
        string after = block[close..];

        var rewritten = Regex.Replace(
            body,
            @"^([\t ]*)([A-Za-z_]\w*)(\s)",
            m =>
            {
                var keyword = m.Groups[2].Value.ToUpperInvariant();
                if (IsBodyKeyword(keyword)) return m.Value;
                return m.Groups[1].Value + Quote(m.Groups[2].Value) + m.Groups[3].Value;
            },
            RegexOptions.Multiline);

        return before + rewritten + after;
    }

    private static bool IsBodyKeyword(string upper) => upper switch
    {
        "CONSTRAINT" or "PRIMARY" or "FOREIGN" or "UNIQUE" or "CHECK"
            or "INDEX" or "KEY" or "REFERENCES" or "CLUSTERED" or "NONCLUSTERED" => true,
        _ => false,
    };

    private static int FindMatchingCloseParen(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static AlterStatement EmitEntityDropped(EntityDropped ed, DdlColumnMap? rightCols) => new(
        Sql: $"DROP TABLE {QuoteEntityName(ed.Target.Name, rightCols)};",
        Comment: $"drop entity {ed.Target.Name}");

    private static AlterStatement EmitEntityRenamed(EntityRenamed er) => new(
        Sql: $"EXEC sp_rename '{er.OldName}', '{er.Target.Name}';",
        Comment: $"rename entity {er.OldName} -> {er.Target.Name}");

    private static AlterStatement EmitSchemaMoved(SchemaMoved sm) => new(
        Sql: $"ALTER SCHEMA {Quote(sm.NewSchema)} TRANSFER {Quote(sm.OldSchema)}.{Quote(sm.Target.Name)};",
        Comment: $"move {sm.Target.Name} from schema {sm.OldSchema} to {sm.NewSchema}");

    private static AlterStatement EmitViewAdded(ViewAdded va, DdlColumnMap? rightCols) => new(
        Sql: $"-- TODO: CREATE VIEW {QuoteEntityName(va.Target.Name, rightCols)} AS <body from v2 DDL>",
        Comment: $"add view {va.Target.Name}");

    // ---------- Attribute-level ----------

    private static AlterStatement EmitAttributeAdded(AttributeAdded aa, DdlColumnMap? rightCols)
    {
        var type = rightCols is not null
            && rightCols.TryGetType(UnqualifiedTable(aa.ParentEntity.Name), aa.Target.Name, out var t)
                ? t
                : "/* TODO: datatype from v2 CREATE DDL */";
        return new AlterStatement(
            Sql: $"ALTER TABLE {QuoteEntityName(aa.ParentEntity.Name, rightCols)} ADD {Quote(aa.Target.Name)} {type};",
            Comment: $"add column {aa.ParentEntity.Name}.{aa.Target.Name}");
    }

    private static AlterStatement EmitAttributeDropped(AttributeDropped ad, DdlColumnMap? rightCols) => new(
        Sql: $"ALTER TABLE {QuoteEntityName(ad.ParentEntity.Name, rightCols)} DROP COLUMN {Quote(ad.Target.Name)};",
        Comment: $"drop column {ad.ParentEntity.Name}.{ad.Target.Name}");

    private static AlterStatement EmitAttributeRenamed(AttributeRenamed ar) => new(
        Sql: $"EXEC sp_rename '{ar.ParentEntity.Name}.{ar.OldName}', '{ar.Target.Name}', 'COLUMN';",
        Comment: $"rename column {ar.ParentEntity.Name}.{ar.OldName} -> {ar.Target.Name}");

    private static AlterStatement EmitAttributeTypeChanged(AttributeTypeChanged at, DdlColumnMap? rightCols) => new(
        Sql: $"ALTER TABLE {QuoteEntityName(at.ParentEntity.Name, rightCols)} ALTER COLUMN {Quote(at.Target.Name)} {at.RightType};",
        Comment: $"type change {at.ParentEntity.Name}.{at.Target.Name} {at.LeftType} -> {at.RightType}");

    private static AlterStatement EmitAttributeNullability(AttributeNullabilityChanged an, DdlColumnMap? rightCols)
    {
        string type = rightCols is not null
            && rightCols.TryGetType(UnqualifiedTable(an.ParentEntity.Name), an.Target.Name, out var t)
                ? t
                : "/* TODO: datatype */";
        var nullSuffix = an.RightNullable ? "NULL" : "NOT NULL";
        return new AlterStatement(
            Sql: $"ALTER TABLE {QuoteEntityName(an.ParentEntity.Name, rightCols)} ALTER COLUMN {Quote(an.Target.Name)} {type} {nullSuffix};",
            Comment: $"nullability {an.ParentEntity.Name}.{an.Target.Name} {(an.LeftNullable ? "NULL" : "NOT NULL")} -> {nullSuffix}");
    }

    private static AlterStatement EmitAttributeDefault(AttributeDefaultChanged ad, DdlColumnMap? rightCols)
    {
        var table = QuoteEntityName(ad.ParentEntity.Name, rightCols);
        var column = Quote(ad.Target.Name);
        var comment = $"default {ad.ParentEntity.Name}.{ad.Target.Name} '{ad.LeftDefault}' -> '{ad.RightDefault}'";

        if (string.IsNullOrWhiteSpace(ad.RightDefault))
        {
            return new AlterStatement(
                Sql: $"-- TODO: DROP existing DEFAULT constraint on {table}.{column} (look up in sys.default_constraints)",
                Comment: comment);
        }

        var addSql =
            $"-- TODO: DROP existing DEFAULT constraint on {table}.{column} first\n" +
            $"ALTER TABLE {table} ADD DEFAULT ({ad.RightDefault}) FOR {column};";
        return new AlterStatement(addSql, comment);
    }

    private static AlterStatement EmitAttributeIdentity(AttributeIdentityChanged ai, DdlColumnMap? rightCols)
    {
        var arrow = ai.RightHasIdentity
            ? "add IDENTITY (requires table rebuild)"
            : "drop IDENTITY (requires table rebuild)";
        return new AlterStatement(
            Sql: $"-- TODO: {arrow} on {QuoteEntityName(ai.ParentEntity.Name, rightCols)}.{Quote(ai.Target.Name)}\n"
               + $"--       SQL Server has no in-place ALTER for IDENTITY; plan a swap table + sp_rename migration.",
            Comment: $"identity {ai.ParentEntity.Name}.{ai.Target.Name}: {ai.LeftHasIdentity} -> {ai.RightHasIdentity}");
    }

    private static AlterStatement EmitKeyGroupAdded(KeyGroupAdded ka, DdlColumnMap? rightCols)
    {
        var columns = ColumnsClause(rightCols, ka.Target.Name);
        var table = QuoteEntityName(ka.ParentEntity.Name, rightCols);
        return ka.Kind switch
        {
            KeyGroupKind.PrimaryKey => new(
                Sql: $"ALTER TABLE {table} ADD CONSTRAINT {Quote(ka.Target.Name)} PRIMARY KEY ({columns});",
                Comment: $"add PK {ka.ParentEntity.Name}.{ka.Target.Name}"),
            KeyGroupKind.UniqueConstraint => new(
                Sql: $"ALTER TABLE {table} ADD CONSTRAINT {Quote(ka.Target.Name)} UNIQUE ({columns});",
                Comment: $"add UQ {ka.ParentEntity.Name}.{ka.Target.Name}"),
            _ => new(
                Sql: $"CREATE INDEX {Quote(ka.Target.Name)} ON {table} ({columns});",
                Comment: $"add index {ka.ParentEntity.Name}.{ka.Target.Name}"),
        };
    }

    private static AlterStatement EmitKeyGroupDropped(KeyGroupDropped kd, DdlColumnMap? rightCols)
    {
        var table = QuoteEntityName(kd.ParentEntity.Name, rightCols);
        return kd.Kind switch
        {
            KeyGroupKind.PrimaryKey => new(
                Sql: $"ALTER TABLE {table} DROP CONSTRAINT {Quote(kd.Target.Name)};",
                Comment: $"drop PK {kd.ParentEntity.Name}.{kd.Target.Name}"),
            KeyGroupKind.UniqueConstraint => new(
                Sql: $"ALTER TABLE {table} DROP CONSTRAINT {Quote(kd.Target.Name)};",
                Comment: $"drop UQ {kd.ParentEntity.Name}.{kd.Target.Name}"),
            _ => new(
                Sql: $"DROP INDEX {Quote(kd.Target.Name)} ON {table};",
                Comment: $"drop index {kd.ParentEntity.Name}.{kd.Target.Name}"),
        };
    }

    private static AlterStatement EmitKeyGroupRenamed(KeyGroupRenamed kr) => new(
        Sql: $"EXEC sp_rename '{kr.ParentEntity.Name}.{kr.OldName}', '{kr.Target.Name}', 'INDEX';",
        Comment: $"rename {kr.Kind} {kr.OldName} -> {kr.Target.Name}");

    private static AlterStatement EmitForeignKeyAdded(ForeignKeyAdded fa, DdlColumnMap? rightCols)
    {
        if (rightCols is not null && rightCols.TryGetForeignKey(fa.Target.Name, out var fk))
        {
            var childCols = string.Join(", ", fk.ChildColumns.Select(Quote));
            var parentCols = string.Join(", ", fk.ParentColumns.Select(Quote));
            return new AlterStatement(
                Sql: $"ALTER TABLE {Quote(fk.ChildTable)} ADD CONSTRAINT {Quote(fa.Target.Name)} FOREIGN KEY ({childCols}) REFERENCES {Quote(fk.ParentTable)} ({parentCols});",
                Comment: $"add FK {fa.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"-- TODO: ADD FOREIGN KEY CONSTRAINT {Quote(fa.Target.Name)} - child/parent columns from v2 CREATE DDL",
            Comment: $"add FK {fa.Target.Name}");
    }

    private static AlterStatement EmitForeignKeyDropped(ForeignKeyDropped fd) => new(
        Sql: $"-- TODO: ALTER TABLE <child> DROP CONSTRAINT {Quote(fd.Target.Name)} - child table from v1 model",
        Comment: $"drop FK {fd.Target.Name}");

    private static AlterStatement EmitForeignKeyRenamed(ForeignKeyRenamed fr) => new(
        Sql: $"EXEC sp_rename '{fr.OldName}', '{fr.Target.Name}', 'OBJECT';",
        Comment: $"rename FK {fr.OldName} -> {fr.Target.Name}");

    // ---------- Helpers ----------

    private static string ColumnsClause(DdlColumnMap? rightCols, string keyGroupName)
    {
        if (rightCols is not null && rightCols.TryGetKeyGroupColumns(keyGroupName, out var cols) && cols.Length > 0)
            return string.Join(", ", cols.Select(Quote));
        return "/* TODO: columns from v2 CREATE DDL */";
    }

    /// <summary>
    /// Returns just the bare table name when the caller hands us a
    /// schema-qualified "schema.table" (we store the DDL map keyed on
    /// unqualified names because FEModel_DDL writes the schema separately).
    /// </summary>
    private static string UnqualifiedTable(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>Quote an identifier with square brackets and escape any embedded ']'.</summary>
    private static string Quote(string ident) => "[" + ident.Replace("]", "]]") + "]";

    /// <summary>
    /// Quote a potentially schema-qualified name. <c>"schema.table"</c> becomes
    /// <c>[schema].[table]</c> rather than <c>[schema.table]</c>.
    /// </summary>
    private static string QuoteQualified(string name)
    {
        var dot = name.IndexOf('.');
        if (dot < 0) return Quote(name);
        return Quote(name[..dot]) + "." + Quote(name[(dot + 1)..]);
    }

    /// <summary>
    /// Quote an entity / table name. Schema lookup priority:
    ///   1. The name is already qualified (<c>schema.table</c>) - split + quote.
    ///   2. CC XLS told us the owner schema for this table (most reliable -
    ///      erwin's CC always emits <c>schema.table</c> in Entity/Table rows).
    ///   3. The v2 CREATE DDL has a schema in its <c>CREATE TABLE</c> header.
    ///   4. No schema known - emit bare <c>[table]</c>.
    /// </summary>
    private static string QuoteEntityName(string name, DdlColumnMap? rightCols)
    {
        if (name.Contains('.')) return QuoteQualified(name);
        var xls = t_xlsSchemas;
        if (xls is not null && xls.TryGetValue(name, out var xlsSch) && !string.IsNullOrEmpty(xlsSch))
            return Quote(xlsSch) + "." + Quote(name);
        if (rightCols is not null && rightCols.TryGetSchema(name, out var sch))
            return Quote(sch) + "." + Quote(name);
        return Quote(name);
    }
}
