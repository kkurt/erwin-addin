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

        foreach (var change in compareResult.Changes)
        {
            AlterStatement? emitted = change switch
            {
                EntityAdded ea => EmitEntityAdded(ea),
                EntityDropped ed => EmitEntityDropped(ed),
                EntityRenamed er => EmitEntityRenamed(er),
                SchemaMoved sm => EmitSchemaMoved(sm),
                AttributeAdded aa => EmitAttributeAdded(aa, rightCols),
                AttributeDropped ad => EmitAttributeDropped(ad),
                AttributeRenamed ar => EmitAttributeRenamed(ar),
                AttributeTypeChanged at => EmitAttributeTypeChanged(at),
                AttributeNullabilityChanged an => EmitAttributeNullability(an, rightCols),
                AttributeDefaultChanged ad2 => EmitAttributeDefault(ad2),
                AttributeIdentityChanged ai => EmitAttributeIdentity(ai),
                KeyGroupAdded ka => EmitKeyGroupAdded(ka, rightCols),
                KeyGroupDropped kd => EmitKeyGroupDropped(kd),
                KeyGroupRenamed kr => EmitKeyGroupRenamed(kr),
                ForeignKeyAdded fa => EmitForeignKeyAdded(fa, rightCols),
                ForeignKeyDropped fd => EmitForeignKeyDropped(fd),
                ForeignKeyRenamed fr => EmitForeignKeyRenamed(fr),
                ViewAdded va => new($"-- TODO: CREATE VIEW {QuoteQualified(va.Target.Name)} AS <body from v2 DDL>", $"add view {va.Target.Name}"),
                ViewDropped vd => new($"DROP VIEW {QuoteQualified(vd.Target.Name)};", $"drop view {vd.Target.Name}"),
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

    // ---------- Entity-level ----------

    private static AlterStatement EmitEntityAdded(EntityAdded ea) => new(
        Sql: $"-- TODO: copy CREATE TABLE body for {QuoteQualified(ea.Target.Name)} from v2 CREATE DDL",
        Comment: $"new entity {ea.Target.Name}");

    private static AlterStatement EmitEntityDropped(EntityDropped ed) => new(
        Sql: $"DROP TABLE {QuoteQualified(ed.Target.Name)};",
        Comment: $"drop entity {ed.Target.Name}");

    private static AlterStatement EmitEntityRenamed(EntityRenamed er) => new(
        Sql: $"EXEC sp_rename '{er.OldName}', '{er.Target.Name}';",
        Comment: $"rename entity {er.OldName} -> {er.Target.Name}");

    private static AlterStatement EmitSchemaMoved(SchemaMoved sm) => new(
        Sql: $"ALTER SCHEMA {Quote(sm.NewSchema)} TRANSFER {Quote(sm.OldSchema)}.{Quote(sm.Target.Name)};",
        Comment: $"move {sm.Target.Name} from schema {sm.OldSchema} to {sm.NewSchema}");

    // ---------- Attribute-level ----------

    private static AlterStatement EmitAttributeAdded(AttributeAdded aa, DdlColumnMap? rightCols)
    {
        var type = rightCols is not null
            && rightCols.TryGetType(UnqualifiedTable(aa.ParentEntity.Name), aa.Target.Name, out var t)
                ? t
                : "/* TODO: datatype from v2 CREATE DDL */";
        return new AlterStatement(
            Sql: $"ALTER TABLE {QuoteQualified(aa.ParentEntity.Name)} ADD {Quote(aa.Target.Name)} {type};",
            Comment: $"add column {aa.ParentEntity.Name}.{aa.Target.Name}");
    }

    private static AlterStatement EmitAttributeDropped(AttributeDropped ad) => new(
        Sql: $"ALTER TABLE {QuoteQualified(ad.ParentEntity.Name)} DROP COLUMN {Quote(ad.Target.Name)};",
        Comment: $"drop column {ad.ParentEntity.Name}.{ad.Target.Name}");

    private static AlterStatement EmitAttributeRenamed(AttributeRenamed ar) => new(
        Sql: $"EXEC sp_rename '{ar.ParentEntity.Name}.{ar.OldName}', '{ar.Target.Name}', 'COLUMN';",
        Comment: $"rename column {ar.ParentEntity.Name}.{ar.OldName} -> {ar.Target.Name}");

    private static AlterStatement EmitAttributeTypeChanged(AttributeTypeChanged at) => new(
        Sql: $"ALTER TABLE {QuoteQualified(at.ParentEntity.Name)} ALTER COLUMN {Quote(at.Target.Name)} {at.RightType};",
        Comment: $"type change {at.ParentEntity.Name}.{at.Target.Name} {at.LeftType} -> {at.RightType}");

    private static AlterStatement EmitAttributeNullability(AttributeNullabilityChanged an, DdlColumnMap? rightCols)
    {
        string type = rightCols is not null
            && rightCols.TryGetType(UnqualifiedTable(an.ParentEntity.Name), an.Target.Name, out var t)
                ? t
                : "/* TODO: datatype */";
        var nullSuffix = an.RightNullable ? "NULL" : "NOT NULL";
        return new AlterStatement(
            Sql: $"ALTER TABLE {QuoteQualified(an.ParentEntity.Name)} ALTER COLUMN {Quote(an.Target.Name)} {type} {nullSuffix};",
            Comment: $"nullability {an.ParentEntity.Name}.{an.Target.Name} {(an.LeftNullable ? "NULL" : "NOT NULL")} -> {nullSuffix}");
    }

    private static AlterStatement EmitAttributeDefault(AttributeDefaultChanged ad)
    {
        var table = QuoteQualified(ad.ParentEntity.Name);
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

    private static AlterStatement EmitAttributeIdentity(AttributeIdentityChanged ai)
    {
        var arrow = ai.RightHasIdentity
            ? "add IDENTITY (requires table rebuild)"
            : "drop IDENTITY (requires table rebuild)";
        return new AlterStatement(
            Sql: $"-- TODO: {arrow} on {QuoteQualified(ai.ParentEntity.Name)}.{Quote(ai.Target.Name)}\n"
               + $"--       SQL Server has no in-place ALTER for IDENTITY; plan a swap table + sp_rename migration.",
            Comment: $"identity {ai.ParentEntity.Name}.{ai.Target.Name}: {ai.LeftHasIdentity} -> {ai.RightHasIdentity}");
    }

    private static AlterStatement EmitKeyGroupAdded(KeyGroupAdded ka, DdlColumnMap? rightCols)
    {
        var columns = ColumnsClause(rightCols, ka.Target.Name);
        var table = QuoteQualified(ka.ParentEntity.Name);
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

    private static AlterStatement EmitKeyGroupDropped(KeyGroupDropped kd)
    {
        var table = QuoteQualified(kd.ParentEntity.Name);
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
}
