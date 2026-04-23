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

    /// <summary>Oracle identifier quoting is "..." with doubled internal quotes.</summary>
    private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";
}
