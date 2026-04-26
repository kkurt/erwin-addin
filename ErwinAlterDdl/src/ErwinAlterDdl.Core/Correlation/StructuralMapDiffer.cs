using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

namespace EliteSoft.Erwin.AlterDdl.Core.Correlation;

/// <summary>
/// Pure structural differ that consumes two <see cref="ErwinModelMap"/>
/// snapshots and produces a list of <see cref="Change"/> records,
/// without going through CompleteCompare's XLS report.
///
/// The differ is the basis for Path D - the in-process dirty-aware
/// compare. The add-in feeds it a left map (clean target Mart version
/// fetched via the Worker) and a right map (the active session walk,
/// which sees the dirty in-memory buffer). It can ONLY surface changes
/// that are visible from the maps themselves, so the supported set is:
///
///   * Entity / View / Trigger / Sequence : Added / Dropped / Renamed
///   * Attribute                          : Added / Dropped / Renamed
///   * Key_Group                          : Added / Dropped / Renamed
///   * Relationship (FK)                  : Added / Dropped / Renamed
///   * Schema-on-Entity move (left.schema vs right.schema differ)
///
/// Property-level diffs (column type, nullability, default, identity,
/// PK kind change) are NOT produced here - the maps don't carry that
/// data. Those continue to come from the CompleteCompare XLS pipeline
/// (<see cref="ChangeCorrelator"/>) when a structural diff is not
/// sufficient. For the dirty-aware add-in flow we accept that
/// limitation as v1; an enrichment pass that walks
/// <c>attribute.Properties</c> can fill the gap later.
///
/// Rename detection is ObjectId-stable: same id, different name. The
/// erwin model graph keeps ObjectId stable across renames, so this is
/// reliable as long as both sides come from the same model lineage
/// (which is always true for Mart vN vs active vN+dirty).
/// </summary>
public static class StructuralMapDiffer
{
    /// <summary>
    /// Diff two model-map snapshots. Result is a flat list of
    /// <see cref="Change"/> records ready to feed the dialect emitter.
    /// </summary>
    /// <param name="left">Baseline (older) - typically Mart vN-1.</param>
    /// <param name="right">Target (newer) - typically active dirty.</param>
    public static IReadOnlyList<Change> Diff(ErwinModelMap left, ErwinModelMap right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var changes = new List<Change>();
        DiffTopLevel(left, right, "Entity", changes,
            added: t => new EntityAdded(t),
            dropped: t => new EntityDropped(t),
            renamed: (t, oldName) => new EntityRenamed(t, oldName),
            schemaMoved: (t, oldSchema, newSchema) => new SchemaMoved(t, oldSchema, newSchema));

        DiffTopLevel(left, right, "View", changes,
            added: t => new ViewAdded(t),
            dropped: t => new ViewDropped(t),
            renamed: (t, oldName) => new ViewRenamed(t, oldName));

        DiffTopLevel(left, right, "Trigger_Template", changes,
            added: t => new TriggerAdded(t),
            dropped: t => new TriggerDropped(t),
            renamed: (t, oldName) => new TriggerRenamed(t, oldName));

        foreach (var seqClass in new[] { "Sequence", "Oracle_Sequence", "ER_Sequence" })
        {
            DiffTopLevel(left, right, seqClass, changes,
                added: t => new SequenceAdded(t),
                dropped: t => new SequenceDropped(t),
                renamed: (t, oldName) => new SequenceRenamed(t, oldName));
        }

        DiffTopLevel(left, right, "Relationship", changes,
            added: t => new ForeignKeyAdded(t),
            dropped: t => new ForeignKeyDropped(t),
            renamed: (t, oldName) => new ForeignKeyRenamed(t, oldName));

        // Attributes and Key_Groups are entity-scoped: each emits a
        // ParentEntity ObjectRef so the emitter knows which table to
        // ALTER. Both sides share ObjectIds, so we resolve the parent
        // from whichever side contains the object (Right for Add, Left
        // for Drop, Right for Rename - the parent is visible there).
        DiffNestedUnderEntity(left, right, "Attribute", changes,
            added: (t, parent) => new AttributeAdded(t, parent),
            dropped: (t, parent) => new AttributeDropped(t, parent),
            renamed: (t, parent, oldName) => new AttributeRenamed(t, parent, oldName));

        DiffNestedUnderEntity(left, right, "Key_Group", changes,
            added: (t, parent) => new KeyGroupAdded(t, parent),
            dropped: (t, parent) => new KeyGroupDropped(t, parent),
            renamed: (t, parent, oldName) => new KeyGroupRenamed(t, parent, oldName));

        return changes;
    }

    /// <summary>
    /// Diff one top-level class (Entity / View / Trigger / Sequence /
    /// Relationship). Uses ObjectId for stable matching; same id +
    /// different name -> rename. For Entity, also detects schema
    /// changes (left "dbo.X" vs right "ops.X") and emits SchemaMoved.
    /// </summary>
    private static void DiffTopLevel(
        ErwinModelMap left,
        ErwinModelMap right,
        string className,
        List<Change> sink,
        Func<ObjectRef, Change> added,
        Func<ObjectRef, Change> dropped,
        Func<ObjectRef, string, Change> renamed,
        Func<ObjectRef, string, string, Change>? schemaMoved = null)
    {
        var leftIndex = left.ObjectsOfClass(className).ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
        var rightIndex = right.ObjectsOfClass(className).ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);

        foreach (var (id, leftObj) in leftIndex)
        {
            if (!rightIndex.TryGetValue(id, out var rightObj))
            {
                sink.Add(dropped(leftObj));
                continue;
            }

            var (leftSchema, leftBare) = SplitSchema(leftObj.Name);
            var (rightSchema, rightBare) = SplitSchema(rightObj.Name);

            if (!string.Equals(leftBare, rightBare, StringComparison.Ordinal))
                sink.Add(renamed(rightObj, leftObj.Name));
            else if (schemaMoved != null
                     && !string.Equals(leftSchema, rightSchema, StringComparison.Ordinal))
                sink.Add(schemaMoved(rightObj, leftSchema, rightSchema));
        }

        foreach (var (id, rightObj) in rightIndex)
        {
            if (!leftIndex.ContainsKey(id))
                sink.Add(added(rightObj));
        }
    }

    /// <summary>
    /// Diff a class that lives under an Entity (Attribute, Key_Group).
    /// The emitter needs a ParentEntity ObjectRef on every change record,
    /// resolved from whichever side currently owns the object.
    /// </summary>
    private static void DiffNestedUnderEntity(
        ErwinModelMap left,
        ErwinModelMap right,
        string className,
        List<Change> sink,
        Func<ObjectRef, ObjectRef, Change> added,
        Func<ObjectRef, ObjectRef, Change> dropped,
        Func<ObjectRef, ObjectRef, string, Change> renamed)
    {
        var leftIndex = left.ObjectsOfClass(className).ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
        var rightIndex = right.ObjectsOfClass(className).ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);

        foreach (var (id, leftObj) in leftIndex)
        {
            var parent = ResolveParentEntity(leftObj, left, right);
            if (parent is null) continue; // orphan attribute (shouldn't happen)
            if (!rightIndex.TryGetValue(id, out var rightObj))
            {
                sink.Add(dropped(leftObj, parent));
                continue;
            }
            if (!string.Equals(leftObj.Name, rightObj.Name, StringComparison.Ordinal))
                sink.Add(renamed(rightObj, parent, leftObj.Name));
        }

        foreach (var (id, rightObj) in rightIndex)
        {
            if (leftIndex.ContainsKey(id)) continue;
            var parent = ResolveParentEntity(rightObj, right, left);
            if (parent is null) continue;
            sink.Add(added(rightObj, parent));
        }
    }

    /// <summary>
    /// Resolve the entity ObjectRef this object belongs to. Walks up the
    /// ParentObjectId chain on the side that owns the object first, then
    /// falls back to the other side (for renames where the parent's name
    /// might have changed too).
    /// </summary>
    private static ObjectRef? ResolveParentEntity(ObjectRef child, ErwinModelMap primary, ErwinModelMap fallback)
    {
        var parentId = child.ParentObjectId;
        if (string.IsNullOrEmpty(parentId)) return null;

        if (primary.TryGetById(parentId, out var p1) && p1.Class == "Entity")
            return p1;
        if (fallback.TryGetById(parentId, out var p2) && p2.Class == "Entity")
            return p2;

        // Walk the chain in the primary side - some metamodels nest
        // Attributes under intermediate objects (Subtype, etc.).
        var current = parentId;
        var guard = 0;
        while (!string.IsNullOrEmpty(current) && guard++ < 16)
        {
            if (!primary.TryGetById(current, out var node)) break;
            if (node.Class == "Entity") return node;
            current = node.ParentObjectId;
        }
        return null;
    }

    /// <summary>
    /// Splits "schema.entity" or "schema.view" into (schema, name). If the
    /// name has no dot, schema is empty. Mirrors how the emitter's
    /// QuoteQualified() splits the same identifier - keep them in sync.
    /// </summary>
    private static (string Schema, string Bare) SplitSchema(string name)
    {
        if (string.IsNullOrEmpty(name)) return (string.Empty, string.Empty);
        var dot = name.IndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return (string.Empty, name);
        return (name[..dot], name[(dot + 1)..]);
    }
}
