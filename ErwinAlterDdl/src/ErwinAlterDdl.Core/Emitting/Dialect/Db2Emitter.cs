using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

namespace EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;

/// <summary>
/// IBM Db2 z/OS v12 / v13 alter DDL emitter. Phase 2 change subset.
/// </summary>
public sealed class Db2Emitter : ISqlEmitter
{
    public string Dialect => "Db2";

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
        Sql: $"DROP TABLE {Quote(ed.Target.Name)};",
        Comment: $"drop entity {ed.Target.Name}");

    private static AlterStatement EmitEntityRenamed(EntityRenamed er) => new(
        Sql: $"RENAME TABLE {Quote(er.OldName)} TO {Quote(er.Target.Name)};",
        Comment: $"rename entity {er.OldName} -> {er.Target.Name}");

    private static AlterStatement EmitSchemaMoved(SchemaMoved sm) => new(
        // Db2 z/OS RENAME TABLE does not change schema. Needs an unload/reload
        // or an ALIAS redirection. Emit a marker so the DBA can pick strategy.
        Sql: $"-- TODO: Db2 z/OS cross-schema move: unload {sm.OldSchema}.{sm.Target.Name}, CREATE TABLE {sm.NewSchema}.{sm.Target.Name} LIKE ..., LOAD, then DROP",
        Comment: $"schema move {sm.OldSchema}.{sm.Target.Name} -> {sm.NewSchema}.{sm.Target.Name}");

    private static AlterStatement EmitAttributeAdded(AttributeAdded aa, DdlColumnMap? rightCols)
    {
        var type = rightCols is not null
            && rightCols.TryGetType(aa.ParentEntity.Name, aa.Target.Name, out var t)
                ? t
                : "/* TODO: datatype from v2 CREATE DDL */";
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(aa.ParentEntity.Name)} ADD COLUMN {Quote(aa.Target.Name)} {type};",
            Comment: $"add column {aa.ParentEntity.Name}.{aa.Target.Name}");
    }

    private static AlterStatement EmitAttributeDropped(AttributeDropped ad) => new(
        Sql: $"ALTER TABLE {Quote(ad.ParentEntity.Name)} DROP COLUMN {Quote(ad.Target.Name)};",
        Comment: $"drop column {ad.ParentEntity.Name}.{ad.Target.Name}");

    private static AlterStatement EmitAttributeRenamed(AttributeRenamed ar) => new(
        Sql: $"ALTER TABLE {Quote(ar.ParentEntity.Name)} RENAME COLUMN {Quote(ar.OldName)} TO {Quote(ar.Target.Name)};",
        Comment: $"rename column {ar.ParentEntity.Name}.{ar.OldName} -> {ar.Target.Name}");

    private static AlterStatement EmitAttributeTypeChanged(AttributeTypeChanged at) => new(
        Sql: $"ALTER TABLE {Quote(at.ParentEntity.Name)} ALTER COLUMN {Quote(at.Target.Name)} SET DATA TYPE {at.RightType};",
        Comment: $"type change {at.ParentEntity.Name}.{at.Target.Name} {at.LeftType} -> {at.RightType}");

    private static AlterStatement EmitAttributeNullability(AttributeNullabilityChanged an)
    {
        var clause = an.RightNullable ? "DROP NOT NULL" : "SET NOT NULL";
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(an.ParentEntity.Name)} ALTER COLUMN {Quote(an.Target.Name)} {clause};",
            Comment: $"nullability {an.ParentEntity.Name}.{an.Target.Name} {(an.LeftNullable ? "NULL" : "NOT NULL")} -> {(an.RightNullable ? "NULL" : "NOT NULL")}");
    }

    private static AlterStatement EmitAttributeDefault(AttributeDefaultChanged ad)
    {
        if (string.IsNullOrWhiteSpace(ad.RightDefault))
        {
            return new AlterStatement(
                Sql: $"ALTER TABLE {Quote(ad.ParentEntity.Name)} ALTER COLUMN {Quote(ad.Target.Name)} DROP DEFAULT;",
                Comment: $"drop default {ad.ParentEntity.Name}.{ad.Target.Name}");
        }
        return new AlterStatement(
            Sql: $"ALTER TABLE {Quote(ad.ParentEntity.Name)} ALTER COLUMN {Quote(ad.Target.Name)} SET DEFAULT {ad.RightDefault};",
            Comment: $"default {ad.ParentEntity.Name}.{ad.Target.Name} '{ad.LeftDefault}' -> '{ad.RightDefault}'");
    }

    private static AlterStatement EmitAttributeIdentity(AttributeIdentityChanged ai)
    {
        // Db2 has no in-place ALTER to toggle identity; DBA must create a new
        // column and copy values. Emit a marker.
        var arrow = ai.RightHasIdentity
            ? "add GENERATED BY DEFAULT AS IDENTITY"
            : "drop IDENTITY";
        return new AlterStatement(
            Sql: $"-- TODO: {arrow} on {Quote(ai.ParentEntity.Name)}.{Quote(ai.Target.Name)}\n"
               + $"--       Db2 has no in-place identity toggle; copy column + DROP/RENAME is required.",
            Comment: $"identity {ai.ParentEntity.Name}.{ai.Target.Name}: {ai.LeftHasIdentity} -> {ai.RightHasIdentity}");
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
            Sql: $"ALTER TABLE {Quote(kd.ParentEntity.Name)} DROP PRIMARY KEY;",
            Comment: $"drop PK {kd.ParentEntity.Name}.{kd.Target.Name}"),
        KeyGroupKind.UniqueConstraint => new(
            Sql: $"ALTER TABLE {Quote(kd.ParentEntity.Name)} DROP CONSTRAINT {Quote(kd.Target.Name)};",
            Comment: $"drop UQ {kd.ParentEntity.Name}.{kd.Target.Name}"),
        _ => new(
            Sql: $"DROP INDEX {Quote(kd.Target.Name)};",
            Comment: $"drop index {kd.ParentEntity.Name}.{kd.Target.Name}"),
    };

    private static AlterStatement EmitKeyGroupRenamed(KeyGroupRenamed kr) => new(
        Sql: kr.Kind == KeyGroupKind.Index
            ? $"RENAME INDEX {Quote(kr.OldName)} TO {Quote(kr.Target.Name)};"
            : $"-- TODO: Db2 z/OS constraint rename requires DROP + ADD of {Quote(kr.OldName)} -> {Quote(kr.Target.Name)}",
        Comment: $"rename {kr.Kind} {kr.OldName} -> {kr.Target.Name}");

    private static AlterStatement EmitForeignKeyAdded(ForeignKeyAdded fa) => new(
        Sql: $"-- TODO: ALTER TABLE <child> ADD CONSTRAINT {Quote(fa.Target.Name)} FOREIGN KEY (/* cols */) REFERENCES <parent> (/* cols */)",
        Comment: $"add FK {fa.Target.Name}");

    private static AlterStatement EmitForeignKeyDropped(ForeignKeyDropped fd) => new(
        Sql: $"-- TODO: ALTER TABLE <child> DROP FOREIGN KEY {Quote(fd.Target.Name)}",
        Comment: $"drop FK {fd.Target.Name}");

    private static AlterStatement EmitForeignKeyRenamed(ForeignKeyRenamed fr) => new(
        Sql: $"-- TODO: Db2 has no FK rename; DROP + ADD {Quote(fr.OldName)} -> {Quote(fr.Target.Name)}",
        Comment: $"rename FK {fr.OldName} -> {fr.Target.Name}");

    /// <summary>Db2 identifier quoting is "..." with doubled internal quotes (same as Oracle).</summary>
    private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";
}
