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

    /// <summary>Quote an identifier with square brackets and escape any embedded ']'.</summary>
    private static string Quote(string ident) => "[" + ident.Replace("]", "]]") + "]";
}
