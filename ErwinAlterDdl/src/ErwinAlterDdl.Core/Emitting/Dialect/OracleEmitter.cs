using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

namespace EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;

/// <summary>
/// Oracle 19c / 21c alter DDL emitter. Covers the Phase 2 change subset;
/// 3.D adds PK / FK / Index / View / Trigger / Sequence cases.
/// </summary>
public sealed class OracleEmitter : ISqlEmitter
{
    public string Dialect => "Oracle";

    public AlterDdlScript Emit(CompareResult compareResult)
    {
        ArgumentNullException.ThrowIfNull(compareResult);
        var stmts = new List<AlterStatement>();

        DdlColumnMap? rightCols = null;
        if (compareResult.RightDdl is { SqlPath: var p } && File.Exists(p))
        {
            try { rightCols = CreateDdlParser.Parse(File.ReadAllText(p)); } catch { }
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
                AttributeNullabilityChanged an => EmitAttributeNullability(an),
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

    private static AlterStatement EmitEntityAdded(EntityAdded ea) => new(
        Sql: $"-- TODO: copy CREATE TABLE body for {Quote(ea.Target.Name)} from v2 CREATE DDL",
        Comment: $"new entity {ea.Target.Name}");

    private static AlterStatement EmitEntityDropped(EntityDropped ed) => new(
        Sql: $"DROP TABLE {Quote(ed.Target.Name)} CASCADE CONSTRAINTS;",
        Comment: $"drop entity {ed.Target.Name}");

    private static AlterStatement EmitEntityRenamed(EntityRenamed er) => new(
        Sql: $"ALTER TABLE {Quote(er.OldName)} RENAME TO {Quote(er.Target.Name)};",
        Comment: $"rename entity {er.OldName} -> {er.Target.Name}");

    private static AlterStatement EmitSchemaMoved(SchemaMoved sm) => new(
        // Oracle has no cross-schema ALTER TABLE RENAME. Recommended workaround
        // is CTAS + DROP old + rename constraints. Emit a TODO marker rather
        // than guess the strategy - the user must craft the migration.
        Sql: $"-- TODO: Oracle has no cross-schema rename; emit CTAS from {sm.OldSchema}.{sm.Target.Name} to {sm.NewSchema}.{sm.Target.Name}",
        Comment: $"schema move {sm.OldSchema}.{sm.Target.Name} -> {sm.NewSchema}.{sm.Target.Name}");

    private static AlterStatement EmitAttributeAdded(AttributeAdded aa, DdlColumnMap? rightCols)
    {
        var type = rightCols is not null
            && rightCols.TryGetType(aa.ParentEntity.Name, aa.Target.Name, out var t)
                ? t
                : "/* TODO: datatype from v2 CREATE DDL */";
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(aa.ParentEntity.Name)} ADD ({Quote(aa.Target.Name)} {type});",
            Comment: $"add column {aa.ParentEntity.Name}.{aa.Target.Name}");
    }

    private static AlterStatement EmitAttributeDropped(AttributeDropped ad) => new(
        Sql: $"ALTER TABLE {Quote(ad.ParentEntity.Name)} DROP COLUMN {Quote(ad.Target.Name)};",
        Comment: $"drop column {ad.ParentEntity.Name}.{ad.Target.Name}");

    private static AlterStatement EmitAttributeRenamed(AttributeRenamed ar) => new(
        Sql: $"ALTER TABLE {Quote(ar.ParentEntity.Name)} RENAME COLUMN {Quote(ar.OldName)} TO {Quote(ar.Target.Name)};",
        Comment: $"rename column {ar.ParentEntity.Name}.{ar.OldName} -> {ar.Target.Name}");

    private static AlterStatement EmitAttributeTypeChanged(AttributeTypeChanged at) => new(
        Sql: $"ALTER TABLE {Quote(at.ParentEntity.Name)} MODIFY ({Quote(at.Target.Name)} {at.RightType});",
        Comment: $"type change {at.ParentEntity.Name}.{at.Target.Name} {at.LeftType} -> {at.RightType}");

    private static AlterStatement EmitAttributeNullability(AttributeNullabilityChanged an)
    {
        var clause = an.RightNullable ? "NULL" : "NOT NULL";
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(an.ParentEntity.Name)} MODIFY ({Quote(an.Target.Name)} {clause});",
            Comment: $"nullability {an.ParentEntity.Name}.{an.Target.Name} {(an.LeftNullable ? "NULL" : "NOT NULL")} -> {clause}");
    }

    private static AlterStatement EmitAttributeDefault(AttributeDefaultChanged ad)
    {
        if (string.IsNullOrWhiteSpace(ad.RightDefault))
        {
            return new AlterStatement(
                Sql: $"ALTER TABLE {Quote(ad.ParentEntity.Name)} MODIFY ({Quote(ad.Target.Name)} DEFAULT NULL);",
                Comment: $"drop default {ad.ParentEntity.Name}.{ad.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(ad.ParentEntity.Name)} MODIFY ({Quote(ad.Target.Name)} DEFAULT {ad.RightDefault});",
            Comment: $"default {ad.ParentEntity.Name}.{ad.Target.Name} '{ad.LeftDefault}' -> '{ad.RightDefault}'");
    }

    private static AlterStatement EmitAttributeIdentity(AttributeIdentityChanged ai)
    {
        if (ai.RightHasIdentity)
        {
            return new AlterStatement(
                Sql: $"ALTER TABLE {Quote(ai.ParentEntity.Name)} MODIFY ({Quote(ai.Target.Name)} GENERATED BY DEFAULT AS IDENTITY);",
                Comment: $"add identity {ai.ParentEntity.Name}.{ai.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(ai.ParentEntity.Name)} MODIFY ({Quote(ai.Target.Name)} DROP IDENTITY);",
            Comment: $"drop identity {ai.ParentEntity.Name}.{ai.Target.Name}");
    }

    private static AlterStatement EmitKeyGroupAdded(KeyGroupAdded ka) => ka.Kind switch
    {
        KeyGroupKind.PrimaryKey => new(
            Sql: $"ALTER TABLE {Quote(ka.ParentEntity.Name)} ADD CONSTRAINT {Quote(ka.Target.Name)} PRIMARY KEY (/* TODO: columns */);",
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
            Sql: $"DROP INDEX {Quote(kd.Target.Name)};",
            Comment: $"drop index {kd.ParentEntity.Name}.{kd.Target.Name}"),
    };

    private static AlterStatement EmitKeyGroupRenamed(KeyGroupRenamed kr) => new(
        // Oracle: ALTER INDEX / ALTER CONSTRAINT rename paths differ; constraint
        // rename uses RENAME TO on the table.
        Sql: kr.Kind == KeyGroupKind.Index
            ? $"ALTER INDEX {Quote(kr.OldName)} RENAME TO {Quote(kr.Target.Name)};"
            : $"ALTER TABLE {Quote(kr.ParentEntity.Name)} RENAME CONSTRAINT {Quote(kr.OldName)} TO {Quote(kr.Target.Name)};",
        Comment: $"rename {kr.Kind} {kr.OldName} -> {kr.Target.Name}");

    private static AlterStatement EmitForeignKeyAdded(ForeignKeyAdded fa) => new(
        Sql: $"-- TODO: ADD CONSTRAINT {Quote(fa.Target.Name)} FOREIGN KEY (/* child cols */) REFERENCES <parent> (/* parent cols */)",
        Comment: $"add FK {fa.Target.Name}");

    private static AlterStatement EmitForeignKeyDropped(ForeignKeyDropped fd) => new(
        Sql: $"-- TODO: ALTER TABLE <child> DROP CONSTRAINT {Quote(fd.Target.Name)}",
        Comment: $"drop FK {fd.Target.Name}");

    private static AlterStatement EmitForeignKeyRenamed(ForeignKeyRenamed fr) => new(
        Sql: $"-- TODO: ALTER TABLE <child> RENAME CONSTRAINT {Quote(fr.OldName)} TO {Quote(fr.Target.Name)}",
        Comment: $"rename FK {fr.OldName} -> {fr.Target.Name}");

    /// <summary>Oracle identifier quoting is "..." with doubled internal quotes.</summary>
    private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";
}
