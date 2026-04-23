using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

namespace EliteSoft.Erwin.AlterDdl.Core.Correlation;

/// <summary>
/// Combines the CompleteCompare XLS (name-based diff signal) with the two
/// <c>.erwin</c> XML models (ObjectId-aware identity) to produce a typed
/// <see cref="Change"/> list.
/// Phase 2 scope: Entity ADD/DROP/RENAME + Attribute ADD/DROP/RENAME + AttributeTypeChanged.
/// </summary>
public static class ChangeCorrelator
{
    public static IReadOnlyList<Change> Correlate(
        ErwinModelMap leftMap,
        ErwinModelMap rightMap,
        IReadOnlyList<XlsDiffRow> xlsRows)
    {
        ArgumentNullException.ThrowIfNull(leftMap);
        ArgumentNullException.ThrowIfNull(rightMap);
        ArgumentNullException.ThrowIfNull(xlsRows);

        var changes = new List<Change>();
        CorrelateEntities(leftMap, rightMap, changes);
        CorrelateAttributes(leftMap, rightMap, changes);
        CorrelateAttributeTypeChanges(leftMap, rightMap, xlsRows, changes);

        // Deterministic order: stable by (Target.Class, Target.Name, kind name).
        return changes
            .OrderBy(c => c.Target.Class, StringComparer.Ordinal)
            .ThenBy(c => c.Target.Name, StringComparer.Ordinal)
            .ThenBy(c => c.GetType().Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void CorrelateEntities(ErwinModelMap left, ErwinModelMap right, List<Change> sink)
    {
        var leftEntities = left.ObjectsOfClass("Entity").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
        var rightEntities = right.ObjectsOfClass("Entity").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);

        foreach (var id in rightEntities.Keys.Except(leftEntities.Keys))
            sink.Add(new EntityAdded(rightEntities[id]));

        foreach (var id in leftEntities.Keys.Except(rightEntities.Keys))
            sink.Add(new EntityDropped(leftEntities[id]));

        foreach (var id in leftEntities.Keys.Intersect(rightEntities.Keys))
        {
            var l = leftEntities[id];
            var r = rightEntities[id];
            if (!string.Equals(l.Name, r.Name, StringComparison.Ordinal))
            {
                sink.Add(new EntityRenamed(r, l.Name));
            }
        }
    }

    private static void CorrelateAttributes(ErwinModelMap left, ErwinModelMap right, List<Change> sink)
    {
        var leftAttrs = left.ObjectsOfClass("Attribute").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
        var rightAttrs = right.ObjectsOfClass("Attribute").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);

        foreach (var id in rightAttrs.Keys.Except(leftAttrs.Keys))
        {
            var attr = rightAttrs[id];
            var parent = ResolveParentEntity(right, attr) ?? UnknownParent();
            sink.Add(new AttributeAdded(attr, parent));
        }

        foreach (var id in leftAttrs.Keys.Except(rightAttrs.Keys))
        {
            var attr = leftAttrs[id];
            var parent = ResolveParentEntity(left, attr) ?? UnknownParent();
            sink.Add(new AttributeDropped(attr, parent));
        }

        foreach (var id in leftAttrs.Keys.Intersect(rightAttrs.Keys))
        {
            var l = leftAttrs[id];
            var r = rightAttrs[id];
            if (!string.Equals(l.Name, r.Name, StringComparison.Ordinal))
            {
                var parent = ResolveParentEntity(right, r) ?? UnknownParent();
                sink.Add(new AttributeRenamed(r, parent, l.Name));
            }
        }
    }

    private static void CorrelateAttributeTypeChanges(
        ErwinModelMap left,
        ErwinModelMap right,
        IReadOnlyList<XlsDiffRow> xlsRows,
        List<Change> sink)
    {
        // Walk the XLS rows as a depth-indexed stream and look for a "Physical
        // Data Type" Not Equal row whose parent Attribute/Column row is tracked.
        string? ctxEntityName = null;
        string? ctxAttrName = null;
        int? ctxEntityIndent = null;
        int? ctxAttrIndent = null;

        foreach (var row in xlsRows)
        {
            // Hierarchy reset: a row at an indent <= the current context's
            // indent means we have popped out of that scope.
            if (ctxAttrIndent is int a && row.IndentLevel <= a) ctxAttrName = null;
            if (ctxEntityIndent is int e && row.IndentLevel <= e)
            {
                ctxEntityName = null;
                ctxAttrName = null;
            }

            switch (row.Type)
            {
                case "Entity/Table":
                    ctxEntityName = row.LeftValue.Length > 0 ? row.LeftValue : row.RightValue;
                    ctxEntityIndent = row.IndentLevel;
                    ctxAttrName = null;
                    ctxAttrIndent = null;
                    break;

                case "Attribute/Column":
                    ctxAttrName = row.LeftValue.Length > 0 ? row.LeftValue : row.RightValue;
                    ctxAttrIndent = row.IndentLevel;
                    break;

                case "Physical Data Type" when row.IsNotEqual:
                    if (ctxEntityName is null || ctxAttrName is null) break;

                    ObjectRef? attr = null;
                    if (right.TryGetAttributeId(ctxEntityName, ctxAttrName, out var aid) &&
                        right.TryGetById(aid, out var resolved))
                    {
                        attr = resolved;
                    }
                    else
                    {
                        // Synthetic reference when the XML map cannot resolve the
                        // attribute (e.g. new attribute not yet in v2 XML via XLS).
                        attr = new ObjectRef(
                            ObjectId: $"(xls-only):{ctxEntityName}.{ctxAttrName}",
                            Name: ctxAttrName,
                            Class: "Attribute");
                    }

                    // Parent entity: first try the attribute's own ParentObjectId,
                    // fall back to looking up the entity by the XLS context name.
                    var parent = ResolveParentEntity(right, attr)
                        ?? LookupEntityByName(right, ctxEntityName)
                        ?? new ObjectRef(
                            ObjectId: $"(xls-only):{ctxEntityName}",
                            Name: ctxEntityName,
                            Class: "Entity");
                    sink.Add(new AttributeTypeChanged(attr, parent, row.LeftValue, row.RightValue));
                    break;
            }
        }
    }

    private static ObjectRef? LookupEntityByName(ErwinModelMap map, string entityName)
    {
        return map.TryGetId("Entity", entityName, out var id) && map.TryGetById(id, out var entity)
            ? entity
            : null;
    }

    private static ObjectRef? ResolveParentEntity(ErwinModelMap map, ObjectRef child)
    {
        if (child.ParentObjectId is null) return null;
        return map.TryGetById(child.ParentObjectId, out var parent) ? parent : null;
    }

    private static ObjectRef UnknownParent() =>
        new(ObjectId: "(unknown)", Name: "(unknown)", Class: "Entity");
}

