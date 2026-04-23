using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

namespace EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;

/// <summary>
/// MSSQL (SQL Server 2019+) alter DDL emitter. Phase 3.C covers the Phase 2
/// change subset:
///   Entity ADD / DROP / RENAME, SchemaMoved,
///   Attribute ADD / DROP / RENAME, AttributeTypeChanged.
/// Phase 3.D will add PK / FK / Index / View / Trigger / Sequence types.
/// </summary>
public sealed class MssqlEmitter : ISqlEmitter
{
    public string Dialect => "MSSQL";

    public AlterDdlScript Emit(CompareResult compareResult)
    {
        ArgumentNullException.ThrowIfNull(compareResult);
        var stmts = new List<AlterStatement>();

        // Optional column lookup from right-side CREATE DDL (Phase 3.B
        // artifact). Lets AttributeAdded emit a concrete datatype.
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
                KeyGroupAdded ka => EmitKeyGroupAdded(ka),
                KeyGroupDropped kd => EmitKeyGroupDropped(kd),
                KeyGroupRenamed kr => EmitKeyGroupRenamed(kr),
                ForeignKeyAdded fa => EmitForeignKeyAdded(fa),
                ForeignKeyDropped fd => EmitForeignKeyDropped(fd),
                ForeignKeyRenamed fr => EmitForeignKeyRenamed(fr),
                _ => null,
            };
            if (emitted is not null) stmts.Add(emitted);
        }

        return new AlterDdlScript(Dialect, stmts);
    }

    // ---------- Entity-level ----------

    private static AlterStatement EmitEntityAdded(EntityAdded ea) => new(
        Sql: $"-- TODO: copy CREATE TABLE body for {Quote(ea.Target.Name)} from v2 CREATE DDL",
        Comment: $"new entity {ea.Target.Name}");

    private static AlterStatement EmitEntityDropped(EntityDropped ed) => new(
        Sql: $"DROP TABLE {Quote(ed.Target.Name)};",
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
            && rightCols.TryGetType(aa.ParentEntity.Name, aa.Target.Name, out var t)
                ? t
                : "/* TODO: datatype from v2 CREATE DDL */";
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(aa.ParentEntity.Name)} ADD {Quote(aa.Target.Name)} {type};",
            Comment: $"add column {aa.ParentEntity.Name}.{aa.Target.Name}");
    }

    private static AlterStatement EmitAttributeDropped(AttributeDropped ad) => new(
        Sql: $"ALTER TABLE {Quote(ad.ParentEntity.Name)} DROP COLUMN {Quote(ad.Target.Name)};",
        Comment: $"drop column {ad.ParentEntity.Name}.{ad.Target.Name}");

    private static AlterStatement EmitAttributeRenamed(AttributeRenamed ar) => new(
        Sql: $"EXEC sp_rename '{ar.ParentEntity.Name}.{ar.OldName}', '{ar.Target.Name}', 'COLUMN';",
        Comment: $"rename column {ar.ParentEntity.Name}.{ar.OldName} -> {ar.Target.Name}");

    private static AlterStatement EmitAttributeTypeChanged(AttributeTypeChanged at) => new(
        Sql: $"ALTER TABLE {Quote(at.ParentEntity.Name)} ALTER COLUMN {Quote(at.Target.Name)} {at.RightType};",
        Comment: $"type change {at.ParentEntity.Name}.{at.Target.Name} {at.LeftType} -> {at.RightType}");

    private static AlterStatement EmitAttributeNullability(AttributeNullabilityChanged an, DdlColumnMap? rightCols)
    {
        // SQL Server requires the full datatype on ALTER COLUMN. We read it
        // from the right-side CREATE DDL; fall back to a TODO placeholder if
        // not available.
        string type = rightCols is not null
            && rightCols.TryGetType(an.ParentEntity.Name, an.Target.Name, out var t)
                ? t
                : "/* TODO: datatype */";
        var nullSuffix = an.RightNullable ? "NULL" : "NOT NULL";
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(an.ParentEntity.Name)} ALTER COLUMN {Quote(an.Target.Name)} {type} {nullSuffix};",
            Comment: $"nullability {an.ParentEntity.Name}.{an.Target.Name} {(an.LeftNullable ? "NULL" : "NOT NULL")} -> {nullSuffix}");
    }

    private static AlterStatement EmitAttributeDefault(AttributeDefaultChanged ad)
    {
        // SQL Server defaults are named constraints. Emit drop/add pairs when
        // both sides non-empty; add-only / drop-only for lopsided cases.
        // The existing default constraint name is SCAPI-generated (DF_...);
        // we emit a safe sys.default_constraints drop lookup + a fresh ADD.
        var table = Quote(ad.ParentEntity.Name);
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
        // SQL Server cannot toggle IDENTITY in place. The only supported path
        // is a drop/recreate cycle (or swap via sp_rename + new table). Emit a
        // marker so the DBA sees the intent.
        var arrow = ai.RightHasIdentity
            ? "add IDENTITY (requires table rebuild)"
            : "drop IDENTITY (requires table rebuild)";
        return new AlterStatement(
            Sql: $"-- TODO: {arrow} on {Quote(ai.ParentEntity.Name)}.{Quote(ai.Target.Name)}\n"
               + $"--       SQL Server has no in-place ALTER for IDENTITY; plan a swap table + sp_rename migration.",
            Comment: $"identity {ai.ParentEntity.Name}.{ai.Target.Name}: {ai.LeftHasIdentity} -> {ai.RightHasIdentity}");
    }

    private static AlterStatement EmitKeyGroupAdded(KeyGroupAdded ka) => ka.Kind switch
    {
        KeyGroupKind.PrimaryKey => new(
            Sql: $"ALTER TABLE {Quote(ka.ParentEntity.Name)} ADD CONSTRAINT {Quote(ka.Target.Name)} PRIMARY KEY (/* TODO: columns from v2 CREATE DDL */);",
            Comment: $"add PK {ka.ParentEntity.Name}.{ka.Target.Name}"),
        KeyGroupKind.UniqueConstraint => new(
            Sql: $"ALTER TABLE {Quote(ka.ParentEntity.Name)} ADD CONSTRAINT {Quote(ka.Target.Name)} UNIQUE (/* TODO: columns */);",
            Comment: $"add UQ {ka.ParentEntity.Name}.{ka.Target.Name}"),
        _ => new(
            Sql: $"CREATE INDEX {Quote(ka.Target.Name)} ON {Quote(ka.ParentEntity.Name)} (/* TODO: columns */);",
            Comment: $"add index {ka.ParentEntity.Name}.{ka.Target.Name}"),
    };

    private static AlterStatement EmitKeyGroupDropped(KeyGroupDropped kd) => kd.Kind switch
    {
        KeyGroupKind.PrimaryKey => new(
            Sql: $"ALTER TABLE {Quote(kd.ParentEntity.Name)} DROP CONSTRAINT {Quote(kd.Target.Name)};",
            Comment: $"drop PK {kd.ParentEntity.Name}.{kd.Target.Name}"),
        KeyGroupKind.UniqueConstraint => new(
            Sql: $"ALTER TABLE {Quote(kd.ParentEntity.Name)} DROP CONSTRAINT {Quote(kd.Target.Name)};",
            Comment: $"drop UQ {kd.ParentEntity.Name}.{kd.Target.Name}"),
        _ => new(
            Sql: $"DROP INDEX {Quote(kd.Target.Name)} ON {Quote(kd.ParentEntity.Name)};",
            Comment: $"drop index {kd.ParentEntity.Name}.{kd.Target.Name}"),
    };

    private static AlterStatement EmitKeyGroupRenamed(KeyGroupRenamed kr) => new(
        Sql: $"EXEC sp_rename '{kr.ParentEntity.Name}.{kr.OldName}', '{kr.Target.Name}', 'INDEX';",
        Comment: $"rename {kr.Kind} {kr.OldName} -> {kr.Target.Name}");

    private static AlterStatement EmitForeignKeyAdded(ForeignKeyAdded fa) => new(
        Sql: $"-- TODO: ADD FOREIGN KEY CONSTRAINT {Quote(fa.Target.Name)} - child/parent columns from v2 CREATE DDL",
        Comment: $"add FK {fa.Target.Name}");

    private static AlterStatement EmitForeignKeyDropped(ForeignKeyDropped fd) => new(
        Sql: $"-- TODO: ALTER TABLE <child> DROP CONSTRAINT {Quote(fd.Target.Name)} - child table from v1 model",
        Comment: $"drop FK {fd.Target.Name}");

    private static AlterStatement EmitForeignKeyRenamed(ForeignKeyRenamed fr) => new(
        Sql: $"EXEC sp_rename '{fr.OldName}', '{fr.Target.Name}', 'OBJECT';",
        Comment: $"rename FK {fr.OldName} -> {fr.Target.Name}");

    /// <summary>Quote an identifier with square brackets and escape any embedded ']'.</summary>
    private static string Quote(string ident) => "[" + ident.Replace("]", "]]") + "]";
}
