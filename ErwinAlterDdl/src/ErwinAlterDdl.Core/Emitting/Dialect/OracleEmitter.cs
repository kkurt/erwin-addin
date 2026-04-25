using System.Text.RegularExpressions;

using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

namespace EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;

/// <summary>
/// Oracle 19c / 21c alter DDL emitter. Phase 3.D polish fills in column lists
/// for PK / UQ / Index / FK when available, and quotes schema-qualified names
/// as "schema"."table" rather than "schema.table". Phase 3.G adds the same
/// CC-XLS-derived schema lookup the MSSQL emitter uses, so bare entity names
/// in the CREATE DDL are still emitted with their owner schema.
/// </summary>
public sealed class OracleEmitter : ISqlEmitter
{
    public string Dialect => "Oracle";

    /// <summary>
    /// XLS-derived schema-by-entity-name lookup, valid only for the duration
    /// of a single <see cref="Emit"/> call. Stored in [ThreadStatic] so the
    /// existing static helpers can read it without an extra parameter.
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
            try { rightCols = CreateDdlParser.Parse(File.ReadAllText(p)); } catch { }
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
                EntityRenamed er => EmitEntityRenamed(er, rightCols),
                SchemaMoved sm => EmitSchemaMoved(sm),
                AttributeAdded aa => EmitAttributeAdded(aa, rightCols),
                AttributeDropped ad => EmitAttributeDropped(ad, rightCols),
                AttributeRenamed ar => EmitAttributeRenamed(ar, rightCols),
                AttributeTypeChanged at => EmitAttributeTypeChanged(at, rightCols),
                AttributeNullabilityChanged an => EmitAttributeNullability(an, rightCols),
                AttributeDefaultChanged ad2 => EmitAttributeDefault(ad2, rightCols),
                AttributeIdentityChanged ai => EmitAttributeIdentity(ai, rightCols),
                KeyGroupAdded ka => EmitKeyGroupAdded(ka, rightCols),
                KeyGroupDropped kd => EmitKeyGroupDropped(kd, rightCols),
                KeyGroupRenamed kr => EmitKeyGroupRenamed(kr, rightCols),
                ForeignKeyAdded fa => EmitForeignKeyAdded(fa, rightCols),
                ForeignKeyDropped fd => EmitForeignKeyDropped(fd),
                ForeignKeyRenamed fr => EmitForeignKeyRenamed(fr),
                ViewAdded va => new($"-- TODO: CREATE OR REPLACE VIEW {QuoteEntityName(va.Target.Name, rightCols)} AS <body from v2 DDL>", $"add view {va.Target.Name}"),
                ViewDropped vd => new($"DROP VIEW {QuoteEntityName(vd.Target.Name, rightCols)};", $"drop view {vd.Target.Name}"),
                ViewRenamed vr => new($"RENAME {Quote(vr.OldName)} TO {Quote(vr.Target.Name)};", $"rename view {vr.OldName} -> {vr.Target.Name}"),
                TriggerAdded ta => new($"-- TODO: CREATE OR REPLACE TRIGGER {QuoteEntityName(ta.Target.Name, rightCols)} body from v2 model", $"add trigger {ta.Target.Name}"),
                TriggerDropped td => new($"DROP TRIGGER {QuoteEntityName(td.Target.Name, rightCols)};", $"drop trigger {td.Target.Name}"),
                TriggerRenamed tr => new($"ALTER TRIGGER {Quote(tr.OldName)} RENAME TO {Quote(tr.Target.Name)};", $"rename trigger {tr.OldName} -> {tr.Target.Name}"),
                SequenceAdded sa => new($"-- TODO: CREATE SEQUENCE {QuoteEntityName(sa.Target.Name, rightCols)} START WITH / INCREMENT BY from v2 model", $"add sequence {sa.Target.Name}"),
                SequenceDropped sd => new($"DROP SEQUENCE {QuoteEntityName(sd.Target.Name, rightCols)};", $"drop sequence {sd.Target.Name}"),
                SequenceRenamed sr => new($"RENAME {Quote(sr.OldName)} TO {Quote(sr.Target.Name)};", $"rename sequence {sr.OldName} -> {sr.Target.Name}"),
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

    private static AlterStatement EmitEntityAdded(EntityAdded ea, DdlColumnMap? rightCols)
    {
        var bare = UnqualifiedTable(ea.Target.Name);
        if (rightCols is not null && rightCols.TryGetCreateBlock(bare, out var block))
        {
            var withSchema = InjectSchemaIntoCreateBlock(block, bare, rightCols);
            var quoted = QuoteColumnIdentifiersInCreateBody(withSchema);
            return new AlterStatement(
                Sql: quoted.TrimEnd() + ";",
                Comment: $"new entity {ea.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"-- TODO: copy CREATE TABLE body for {QuoteEntityName(ea.Target.Name, rightCols)} from v2 CREATE DDL",
            Comment: $"new entity {ea.Target.Name}");
    }

    private static AlterStatement EmitEntityDropped(EntityDropped ed, DdlColumnMap? rightCols) => new(
        Sql: $"DROP TABLE {QuoteEntityName(ed.Target.Name, rightCols)} CASCADE CONSTRAINTS;",
        Comment: $"drop entity {ed.Target.Name}");

    private static AlterStatement EmitEntityRenamed(EntityRenamed er, DdlColumnMap? rightCols) => new(
        Sql: $"ALTER TABLE {QuoteEntityName(er.OldName, rightCols)} RENAME TO {Quote(er.Target.Name)};",
        Comment: $"rename entity {er.OldName} -> {er.Target.Name}");

    private static AlterStatement EmitSchemaMoved(SchemaMoved sm) => new(
        Sql: $"-- TODO: Oracle has no cross-schema rename; emit CTAS from {sm.OldSchema}.{sm.Target.Name} to {sm.NewSchema}.{sm.Target.Name}",
        Comment: $"schema move {sm.OldSchema}.{sm.Target.Name} -> {sm.NewSchema}.{sm.Target.Name}");

    private static AlterStatement EmitAttributeAdded(AttributeAdded aa, DdlColumnMap? rightCols)
    {
        var type = rightCols is not null
            && rightCols.TryGetType(UnqualifiedTable(aa.ParentEntity.Name), aa.Target.Name, out var t)
                ? t
                : "/* TODO: datatype from v2 CREATE DDL */";
        return new AlterStatement(
            Sql: $"ALTER TABLE {QuoteEntityName(aa.ParentEntity.Name, rightCols)} ADD ({Quote(aa.Target.Name)} {type});",
            Comment: $"add column {aa.ParentEntity.Name}.{aa.Target.Name}");
    }

    private static AlterStatement EmitAttributeDropped(AttributeDropped ad, DdlColumnMap? rightCols) => new(
        Sql: $"ALTER TABLE {QuoteEntityName(ad.ParentEntity.Name, rightCols)} DROP COLUMN {Quote(ad.Target.Name)};",
        Comment: $"drop column {ad.ParentEntity.Name}.{ad.Target.Name}");

    private static AlterStatement EmitAttributeRenamed(AttributeRenamed ar, DdlColumnMap? rightCols) => new(
        Sql: $"ALTER TABLE {QuoteEntityName(ar.ParentEntity.Name, rightCols)} RENAME COLUMN {Quote(ar.OldName)} TO {Quote(ar.Target.Name)};",
        Comment: $"rename column {ar.ParentEntity.Name}.{ar.OldName} -> {ar.Target.Name}");

    private static AlterStatement EmitAttributeTypeChanged(AttributeTypeChanged at, DdlColumnMap? rightCols) => new(
        Sql: $"ALTER TABLE {QuoteEntityName(at.ParentEntity.Name, rightCols)} MODIFY ({Quote(at.Target.Name)} {at.RightType});",
        Comment: $"type change {at.ParentEntity.Name}.{at.Target.Name} {at.LeftType} -> {at.RightType}");

    private static AlterStatement EmitAttributeNullability(AttributeNullabilityChanged an, DdlColumnMap? rightCols)
    {
        var clause = an.RightNullable ? "NULL" : "NOT NULL";
        return new AlterStatement(
            Sql: $"ALTER TABLE {QuoteEntityName(an.ParentEntity.Name, rightCols)} MODIFY ({Quote(an.Target.Name)} {clause});",
            Comment: $"nullability {an.ParentEntity.Name}.{an.Target.Name} {(an.LeftNullable ? "NULL" : "NOT NULL")} -> {clause}");
    }

    private static AlterStatement EmitAttributeDefault(AttributeDefaultChanged ad, DdlColumnMap? rightCols)
    {
        if (string.IsNullOrWhiteSpace(ad.RightDefault))
        {
            return new AlterStatement(
                Sql: $"ALTER TABLE {QuoteEntityName(ad.ParentEntity.Name, rightCols)} MODIFY ({Quote(ad.Target.Name)} DEFAULT NULL);",
                Comment: $"drop default {ad.ParentEntity.Name}.{ad.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"ALTER TABLE {QuoteEntityName(ad.ParentEntity.Name, rightCols)} MODIFY ({Quote(ad.Target.Name)} DEFAULT {ad.RightDefault});",
            Comment: $"default {ad.ParentEntity.Name}.{ad.Target.Name} '{ad.LeftDefault}' -> '{ad.RightDefault}'");
    }

    private static AlterStatement EmitAttributeIdentity(AttributeIdentityChanged ai, DdlColumnMap? rightCols)
    {
        if (ai.RightHasIdentity)
        {
            return new AlterStatement(
                Sql: $"ALTER TABLE {QuoteEntityName(ai.ParentEntity.Name, rightCols)} MODIFY ({Quote(ai.Target.Name)} GENERATED BY DEFAULT AS IDENTITY);",
                Comment: $"add identity {ai.ParentEntity.Name}.{ai.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"ALTER TABLE {QuoteEntityName(ai.ParentEntity.Name, rightCols)} MODIFY ({Quote(ai.Target.Name)} DROP IDENTITY);",
            Comment: $"drop identity {ai.ParentEntity.Name}.{ai.Target.Name}");
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
                Sql: $"DROP INDEX {Quote(kd.Target.Name)};",
                Comment: $"drop index {kd.ParentEntity.Name}.{kd.Target.Name}"),
        };
    }

    private static AlterStatement EmitKeyGroupRenamed(KeyGroupRenamed kr, DdlColumnMap? rightCols) => new(
        Sql: kr.Kind == KeyGroupKind.Index
            ? $"ALTER INDEX {Quote(kr.OldName)} RENAME TO {Quote(kr.Target.Name)};"
            : $"ALTER TABLE {QuoteEntityName(kr.ParentEntity.Name, rightCols)} RENAME CONSTRAINT {Quote(kr.OldName)} TO {Quote(kr.Target.Name)};",
        Comment: $"rename {kr.Kind} {kr.OldName} -> {kr.Target.Name}");

    private static AlterStatement EmitForeignKeyAdded(ForeignKeyAdded fa, DdlColumnMap? rightCols)
    {
        if (rightCols is not null && rightCols.TryGetForeignKey(fa.Target.Name, out var fk))
        {
            var childCols = string.Join(", ", fk.ChildColumns.Select(Quote));
            var parentCols = string.Join(", ", fk.ParentColumns.Select(Quote));
            return new AlterStatement(
                Sql: $"ALTER TABLE {QuoteEntityName(fk.ChildTable, rightCols)} ADD CONSTRAINT {Quote(fa.Target.Name)} FOREIGN KEY ({childCols}) REFERENCES {QuoteEntityName(fk.ParentTable, rightCols)} ({parentCols});",
                Comment: $"add FK {fa.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"-- TODO: ADD CONSTRAINT {Quote(fa.Target.Name)} FOREIGN KEY (/* child cols */) REFERENCES <parent> (/* parent cols */)",
            Comment: $"add FK {fa.Target.Name}");
    }

    private static AlterStatement EmitForeignKeyDropped(ForeignKeyDropped fd) => new(
        Sql: $"-- TODO: ALTER TABLE <child> DROP CONSTRAINT {Quote(fd.Target.Name)}",
        Comment: $"drop FK {fd.Target.Name}");

    private static AlterStatement EmitForeignKeyRenamed(ForeignKeyRenamed fr) => new(
        Sql: $"-- TODO: ALTER TABLE <child> RENAME CONSTRAINT {Quote(fr.OldName)} TO {Quote(fr.Target.Name)}",
        Comment: $"rename FK {fr.OldName} -> {fr.Target.Name}");

    // ---------- Helpers ----------

    private static string ColumnsClause(DdlColumnMap? rightCols, string keyGroupName)
    {
        if (rightCols is not null && rightCols.TryGetKeyGroupColumns(keyGroupName, out var cols) && cols.Length > 0)
            return string.Join(", ", cols.Select(Quote));
        return "/* TODO: columns */";
    }

    private static string UnqualifiedTable(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>Oracle identifier quoting is "..." with doubled internal quotes.</summary>
    private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    private static string QuoteQualified(string name)
    {
        var dot = name.IndexOf('.');
        if (dot < 0) return Quote(name);
        return Quote(name[..dot]) + "." + Quote(name[(dot + 1)..]);
    }

    /// <summary>
    /// Quote an entity / table name. Schema lookup priority mirrors
    /// MssqlEmitter.QuoteEntityName so all dialects behave consistently.
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

    /// <summary>
    /// Inject the resolved schema into a verbatim CREATE TABLE block so the
    /// emitted body reads <c>CREATE TABLE "schema"."table" (...)</c> rather
    /// than the bare form FEModel_DDL sometimes produces.
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
    /// Wrap bare column identifiers inside a verbatim CREATE TABLE body in
    /// Oracle's <c>"double quotes"</c> so the emitted SQL matches what
    /// erwin's GUI compare wizard produces. Constraint-keyword lines pass
    /// through untouched.
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
            or "INDEX" or "KEY" or "REFERENCES" => true,
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
}
