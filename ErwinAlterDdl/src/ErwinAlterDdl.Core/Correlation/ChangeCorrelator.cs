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
        CorrelateAttributePropertyChanges(leftMap, rightMap, xlsRows, changes);
        CorrelateKeyGroups(leftMap, rightMap, changes);
        CorrelateRelationships(leftMap, rightMap, changes);
        CorrelateViews(leftMap, rightMap, changes);
        CorrelateTriggers(leftMap, rightMap, changes);
        CorrelateSequences(leftMap, rightMap, changes);

        // CC is the source of truth. When the caller supplies XLS rows, drop
        // any change whose target/parent was not mentioned by CC. Structural
        // diff (XML map set-diff) can find more granular differences than CC
        // chooses to surface, but the user's policy is "if CC didn't report
        // it, we don't emit it" - both to avoid noise and to stay aligned
        // with what the DBA sees in erwin's GUI compare.
        // When xlsRows is empty (e.g. SkipCompleteCompare path or unit tests
        // that don't supply XLS), we trust the structural diff outright.
        if (xlsRows.Count > 0)
            changes = FilterByXlsAllowlist(changes, xlsRows);

        // Deterministic order: stable by (Target.Class, Target.Name, kind name).
        return changes
            .OrderBy(c => c.Target.Class, StringComparer.Ordinal)
            .ThenBy(c => c.Target.Name, StringComparer.Ordinal)
            .ThenBy(c => c.GetType().Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Build an allow-set of (class -> names) and (entity -> attribute names)
    /// observed in CC XLS, then drop any structural-diff change whose target
    /// is not in the corresponding set. Names are normalized by stripping
    /// schema prefix (CC sometimes qualifies entities as "schema.X" while
    /// the XML map stores just "X").
    /// </summary>
    private static List<Change> FilterByXlsAllowlist(List<Change> changes, IReadOnlyList<XlsDiffRow> xlsRows)
    {
        var entityAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attrAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // entity\0attr
        var keyGroupAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relationshipAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var viewAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var triggerAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequenceAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? ctxEntity = null;
        int? ctxEntityIndent = null;

        void AddBoth(HashSet<string> set, XlsDiffRow row)
        {
            if (!string.IsNullOrEmpty(row.LeftValue))
                set.Add(StripSchemaPrefix(row.LeftValue));
            if (!string.IsNullOrEmpty(row.RightValue))
                set.Add(StripSchemaPrefix(row.RightValue));
        }

        foreach (var row in xlsRows)
        {
            if (ctxEntityIndent is int e && row.IndentLevel <= e)
            {
                ctxEntity = null;
                ctxEntityIndent = null;
            }

            if (IsEntityRowType(row.Type))
            {
                AddBoth(entityAllow, row);
                var name = row.LeftValue.Length > 0 ? row.LeftValue : row.RightValue;
                if (!string.IsNullOrEmpty(name))
                {
                    ctxEntity = StripSchemaPrefix(name);
                    ctxEntityIndent = row.IndentLevel;
                }
            }
            else if (IsAttributeRowType(row.Type))
            {
                if (ctxEntity is not null)
                {
                    if (!string.IsNullOrEmpty(row.LeftValue))
                        attrAllow.Add(ctxEntity + "\0" + row.LeftValue);
                    if (!string.IsNullOrEmpty(row.RightValue))
                        attrAllow.Add(ctxEntity + "\0" + row.RightValue);
                }
            }
            else if (IsKeyGroupRowType(row.Type))
            {
                AddBoth(keyGroupAllow, row);
            }
            else if (IsRelationshipRowType(row.Type))
            {
                AddBoth(relationshipAllow, row);
            }
            else if (IsViewRowType(row.Type))
            {
                AddBoth(viewAllow, row);
            }
            else if (IsTriggerRowType(row.Type))
            {
                AddBoth(triggerAllow, row);
            }
            else if (IsSequenceRowType(row.Type))
            {
                AddBoth(sequenceAllow, row);
            }
        }

        bool EntityKept(string name) =>
            entityAllow.Contains(StripSchemaPrefix(name));

        bool AttrKept(string entity, string attr) =>
            attrAllow.Contains(StripSchemaPrefix(entity) + "\0" + attr);

        var kept = new List<Change>(changes.Count);
        foreach (var c in changes)
        {
            bool keep = c switch
            {
                EntityAdded ea => EntityKept(ea.Target.Name),
                EntityDropped ed => EntityKept(ed.Target.Name),
                EntityRenamed er => EntityKept(er.Target.Name) || EntityKept(er.OldName),
                SchemaMoved sm => EntityKept(sm.Target.Name),
                AttributeAdded aa => AttrKept(aa.ParentEntity.Name, aa.Target.Name),
                AttributeDropped ad => AttrKept(ad.ParentEntity.Name, ad.Target.Name),
                AttributeRenamed ar => AttrKept(ar.ParentEntity.Name, ar.Target.Name)
                                       || AttrKept(ar.ParentEntity.Name, ar.OldName),
                AttributeTypeChanged at => AttrKept(at.ParentEntity.Name, at.Target.Name),
                AttributeNullabilityChanged an => AttrKept(an.ParentEntity.Name, an.Target.Name),
                AttributeDefaultChanged ad2 => AttrKept(ad2.ParentEntity.Name, ad2.Target.Name),
                AttributeIdentityChanged ai => AttrKept(ai.ParentEntity.Name, ai.Target.Name),
                KeyGroupAdded ka => keyGroupAllow.Contains(ka.Target.Name),
                KeyGroupDropped kd => keyGroupAllow.Contains(kd.Target.Name),
                KeyGroupRenamed kr => keyGroupAllow.Contains(kr.Target.Name)
                                      || keyGroupAllow.Contains(kr.OldName),
                ForeignKeyAdded fa => relationshipAllow.Contains(fa.Target.Name),
                ForeignKeyDropped fd => relationshipAllow.Contains(fd.Target.Name),
                ForeignKeyRenamed fr => relationshipAllow.Contains(fr.Target.Name)
                                        || relationshipAllow.Contains(fr.OldName),
                ViewAdded va => viewAllow.Contains(va.Target.Name),
                ViewDropped vd => viewAllow.Contains(vd.Target.Name),
                ViewRenamed vr => viewAllow.Contains(vr.Target.Name)
                                  || viewAllow.Contains(vr.OldName),
                TriggerAdded ta => triggerAllow.Contains(ta.Target.Name),
                TriggerDropped td => triggerAllow.Contains(td.Target.Name),
                TriggerRenamed tr => triggerAllow.Contains(tr.Target.Name)
                                     || triggerAllow.Contains(tr.OldName),
                SequenceAdded sa => sequenceAllow.Contains(sa.Target.Name),
                SequenceDropped sd => sequenceAllow.Contains(sd.Target.Name),
                SequenceRenamed sr => sequenceAllow.Contains(sr.Target.Name)
                                      || sequenceAllow.Contains(sr.OldName),
                _ => true,
            };
            if (keep) kept.Add(c);
        }
        return kept;
    }

    private static bool IsEntityRowType(string t) =>
        t.Equals("Entity/Table", StringComparison.OrdinalIgnoreCase)
        || t.Equals("Table", StringComparison.OrdinalIgnoreCase)
        || t.Equals("Entity", StringComparison.OrdinalIgnoreCase);

    private static bool IsAttributeRowType(string t) =>
        t.Equals("Attribute/Column", StringComparison.OrdinalIgnoreCase)
        || t.Equals("Column", StringComparison.OrdinalIgnoreCase)
        || t.Equals("Attribute", StringComparison.OrdinalIgnoreCase);

    // Liberal matchers: erwin's CC XLS row-Type strings drift between
    // metamodel revisions and DBMS adapters, so we accept anything that
    // mentions the broad object class. False positives here only WIDEN the
    // allow-set, never discard real CC signals.
    private static bool IsKeyGroupRowType(string t) =>
        t.Contains("Key Group", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Key_Group", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Index", StringComparison.OrdinalIgnoreCase)
        || t.Equals("Primary Key", StringComparison.OrdinalIgnoreCase)
        || t.Equals("Unique Constraint", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelationshipRowType(string t) =>
        t.Contains("Relationship", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Foreign Key", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Foreign_Key", StringComparison.OrdinalIgnoreCase);

    private static bool IsViewRowType(string t) =>
        t.Equals("View", StringComparison.OrdinalIgnoreCase);

    private static bool IsTriggerRowType(string t) =>
        t.Contains("Trigger", StringComparison.OrdinalIgnoreCase);

    private static bool IsSequenceRowType(string t) =>
        t.Contains("Sequence", StringComparison.OrdinalIgnoreCase);

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

                    // Skip XLS-derived property changes for entities/attributes
                    // that no longer exist on the right side. Structural diff
                    // already emits EntityDropped / AttributeDropped for these.
                    if (!TryResolveAttribute(right, ctxEntityName, ctxAttrName, out var resolved))
                        break;

                    // Skip property change when the attribute is brand new on
                    // the right side. erwin's CC emits "Not Equal" property
                    // rows (Physical Data Type, Null Option, Default, ...)
                    // for newly-added attributes too - one row per property,
                    // with the missing side empty. Surfacing those as ALTER
                    // COLUMN statements is duplicate noise: AttributeAdded
                    // (or the verbatim CREATE TABLE for newly-added entities)
                    // already carries the type, nullability, default, etc.
                    // We use ObjectId (not name) so renames - same id, new
                    // name on the right - still get their property changes.
                    if (!left.TryGetById(resolved.ObjectId, out _)) break;

                    var parent = ResolveParentEntity(right, resolved)
                        ?? LookupEntityByName(right, ctxEntityName)
                        ?? new ObjectRef(
                            ObjectId: $"(xls-only):{ctxEntityName}",
                            Name: ctxEntityName,
                            Class: "Entity");
                    sink.Add(new AttributeTypeChanged(resolved, parent, row.LeftValue, row.RightValue));
                    break;
            }
        }
    }

    /// <summary>
    /// Resolve an attribute by its entity + column name, tolerating a
    /// schema-qualified entity name (e.g. <c>app.CUSTOMER</c>) coming from
    /// the CC XLS when the XML map stores only the unqualified entity name.
    /// </summary>
    private static bool TryResolveAttribute(ErwinModelMap map, string entityName, string attrName, out ObjectRef attr)
    {
        if (map.TryGetAttributeId(entityName, attrName, out var aid) && map.TryGetById(aid, out attr!))
            return true;
        var unqualified = StripSchemaPrefix(entityName);
        if (!string.Equals(unqualified, entityName, StringComparison.Ordinal)
            && map.TryGetAttributeId(unqualified, attrName, out aid)
            && map.TryGetById(aid, out attr!))
            return true;
        attr = default!;
        return false;
    }

    private static string StripSchemaPrefix(string name)
    {
        int dot = name.IndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>
    /// Walks the XLS rows hunting for property-level changes on attributes
    /// that the emitters can turn into ALTER COLUMN-style statements:
    /// Null Option (nullability), Default / Default Value (default),
    /// Identity / Identity Increment / Identity Seed.
    /// </summary>
    private static void CorrelateAttributePropertyChanges(
        ErwinModelMap left,
        ErwinModelMap right,
        IReadOnlyList<XlsDiffRow> xlsRows,
        List<Change> sink)
    {
        string? ctxEntityName = null;
        string? ctxAttrName = null;
        int? ctxEntityIndent = null;
        int? ctxAttrIndent = null;

        // Dedupe: "Identity", "Identity Increment", and "Identity Seed" are
        // separate XLS property rows but a single semantic IdentityChanged.
        var identityEmitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in xlsRows)
        {
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

                case "Null Option" when row.IsNotEqual:
                    if (ctxEntityName is null || ctxAttrName is null) break;
                    if (!TryResolveAttribute(right, ctxEntityName, ctxAttrName, out var attrN0)) break;
                    // Suppress redundant "Null Option Not Equal" rows that CC
                    // emits for newly-added attributes - the AttributeAdded
                    // path (or verbatim CREATE TABLE for new entities) already
                    // carries the column nullability. ObjectId match (rather
                    // than name match) tolerates renames.
                    if (!left.TryGetById(attrN0.ObjectId, out _)) break;
                    var (attrN, parentN) = ResolveAttrRefs(right, ctxEntityName, ctxAttrName);
                    sink.Add(new AttributeNullabilityChanged(
                        attrN, parentN,
                        LeftNullable: ParseNullable(row.LeftValue),
                        RightNullable: ParseNullable(row.RightValue)));
                    break;

                case "Default" when row.IsNotEqual:
                case "Default Value" when row.IsNotEqual:
                    if (ctxEntityName is null || ctxAttrName is null) break;
                    if (!TryResolveAttribute(right, ctxEntityName, ctxAttrName, out var attrD0)) break;
                    if (!left.TryGetById(attrD0.ObjectId, out _)) break;
                    var (attrD, parentD) = ResolveAttrRefs(right, ctxEntityName, ctxAttrName);
                    sink.Add(new AttributeDefaultChanged(
                        attrD, parentD,
                        LeftDefault: row.LeftValue,
                        RightDefault: row.RightValue));
                    break;

                case "Identity" when row.IsNotEqual:
                case "Identity Increment" when row.IsNotEqual:
                case "Identity Seed" when row.IsNotEqual:
                    if (ctxEntityName is null || ctxAttrName is null) break;
                    if (!TryResolveAttribute(right, ctxEntityName, ctxAttrName, out var attrI0)) break;
                    if (!left.TryGetById(attrI0.ObjectId, out _)) break;
                    var dedupeKey = $"{ctxEntityName}.{ctxAttrName}";
                    if (!identityEmitted.Add(dedupeKey)) break;
                    var (attrI, parentI) = ResolveAttrRefs(right, ctxEntityName, ctxAttrName);
                    sink.Add(new AttributeIdentityChanged(
                        attrI, parentI,
                        LeftHasIdentity: ParseBool(row.LeftValue, fallback: false),
                        RightHasIdentity: ParseBool(row.RightValue, fallback: true)));
                    break;
            }
        }
    }

    private static (ObjectRef Attr, ObjectRef Parent) ResolveAttrRefs(
        ErwinModelMap map, string entityName, string attrName)
    {
        if (!TryResolveAttribute(map, entityName, attrName, out var attr))
        {
            attr = new ObjectRef(
                ObjectId: $"(xls-only):{entityName}.{attrName}",
                Name: attrName,
                Class: "Attribute");
        }
        var parent = (attr.ParentObjectId is not null && map.TryGetById(attr.ParentObjectId, out var p))
            ? p
            : LookupEntityByName(map, entityName)
                ?? new ObjectRef(
                    ObjectId: $"(xls-only):{entityName}",
                    Name: entityName,
                    Class: "Entity");
        return (attr, parent);
    }

    private static bool ParseNullable(string raw)
    {
        // SCAPI emits "Null" for nullable and "Not Null" for not-nullable.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("Not", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.Equals("Null", StringComparison.OrdinalIgnoreCase)) return true;
        // Some DBMS variants emit "Nullable" / "Required" or booleans.
        if (trimmed.Equals("Nullable", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("Required", StringComparison.OrdinalIgnoreCase)) return false;
        if (bool.TryParse(trimmed, out var b)) return b;
        return true; // safest assumption
    }

    private static bool ParseBool(string raw, bool fallback)
    {
        var trimmed = raw.Trim();
        if (trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(trimmed)) return fallback;
        return bool.TryParse(trimmed, out var b) ? b : fallback;
    }

    private static ObjectRef? LookupEntityByName(ErwinModelMap map, string entityName)
    {
        if (map.TryGetId("Entity", entityName, out var id) && map.TryGetById(id, out var entity))
            return entity;
        // CC XLS sometimes qualifies entities with a schema ("app.CUSTOMER")
        // while the XML map stores just the name ("CUSTOMER"). Retry with the
        // unqualified form so these entries link back to the real ObjectId.
        var unqualified = StripSchemaPrefix(entityName);
        if (!string.Equals(unqualified, entityName, StringComparison.Ordinal)
            && map.TryGetId("Entity", unqualified, out id)
            && map.TryGetById(id, out entity))
            return entity;
        return null;
    }

    private static void CorrelateKeyGroups(ErwinModelMap left, ErwinModelMap right, List<Change> sink)
    {
        var lk = left.ObjectsOfClass("Key_Group").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
        var rk = right.ObjectsOfClass("Key_Group").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);

        // Dedup guard: erwin sometimes reassigns a KeyGroup's ObjectId across
        // exports even though its parent entity and name are unchanged. A naive
        // set-diff would emit DROP of the old id plus ADD of the new id, which
        // the DBA would have to manually deduplicate. Collapse those pairs by
        // (parentObjectId, name) so identity churn stays silent.
        var rightByParentAndName = rk.Values
            .Where(kg => kg.ParentObjectId is not null)
            .ToLookup(kg => KeyOf(kg.ParentObjectId!, kg.Name), StringComparer.Ordinal);
        var leftByParentAndName = lk.Values
            .Where(kg => kg.ParentObjectId is not null)
            .ToLookup(kg => KeyOf(kg.ParentObjectId!, kg.Name), StringComparer.Ordinal);

        foreach (var id in rk.Keys.Except(lk.Keys))
        {
            var kg = rk[id];
            if (kg.ParentObjectId is not null
                && leftByParentAndName.Contains(KeyOf(kg.ParentObjectId, kg.Name)))
            {
                // matching (parent, name) exists on the left side under a
                // different ObjectId -> identity churn, not a real add
                continue;
            }
            var parent = (kg.ParentObjectId is not null && right.TryGetById(kg.ParentObjectId, out var p))
                ? p : UnknownParent();
            sink.Add(new KeyGroupAdded(kg, parent, GuessKind(kg.Name)));
        }
        foreach (var id in lk.Keys.Except(rk.Keys))
        {
            var kg = lk[id];
            if (kg.ParentObjectId is not null
                && rightByParentAndName.Contains(KeyOf(kg.ParentObjectId, kg.Name)))
            {
                continue;
            }
            var parent = (kg.ParentObjectId is not null && left.TryGetById(kg.ParentObjectId, out var p))
                ? p : UnknownParent();
            sink.Add(new KeyGroupDropped(kg, parent, GuessKind(kg.Name)));
        }
        foreach (var id in lk.Keys.Intersect(rk.Keys))
        {
            if (!string.Equals(lk[id].Name, rk[id].Name, StringComparison.Ordinal))
            {
                var kg = rk[id];
                var parent = (kg.ParentObjectId is not null && right.TryGetById(kg.ParentObjectId, out var p))
                    ? p : UnknownParent();
                sink.Add(new KeyGroupRenamed(kg, parent, lk[id].Name, GuessKind(kg.Name)));
            }
        }
    }

    private static string KeyOf(string parentObjectId, string name) =>
        parentObjectId + "\0" + name;

    private static void CorrelateRelationships(ErwinModelMap left, ErwinModelMap right, List<Change> sink)
    {
        var lr = left.ObjectsOfClass("Relationship").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
        var rr = right.ObjectsOfClass("Relationship").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);

        foreach (var id in rr.Keys.Except(lr.Keys))
            sink.Add(new ForeignKeyAdded(rr[id]));
        foreach (var id in lr.Keys.Except(rr.Keys))
            sink.Add(new ForeignKeyDropped(lr[id]));
        foreach (var id in lr.Keys.Intersect(rr.Keys))
        {
            if (!string.Equals(lr[id].Name, rr[id].Name, StringComparison.Ordinal))
                sink.Add(new ForeignKeyRenamed(rr[id], lr[id].Name));
        }
    }

    private static KeyGroupKind GuessKind(string name)
    {
        // erwin default naming: XPK* = PK, XAK* = AK/Unique, XIE* = Inversion entry (non-unique index).
        // Users can override. We fall back to Index when the prefix doesn't match.
        if (name.StartsWith("XPK", StringComparison.OrdinalIgnoreCase)) return KeyGroupKind.PrimaryKey;
        if (name.StartsWith("XAK", StringComparison.OrdinalIgnoreCase)) return KeyGroupKind.UniqueConstraint;
        if (name.StartsWith("XIE", StringComparison.OrdinalIgnoreCase)) return KeyGroupKind.InversionEntry;
        return KeyGroupKind.Index;
    }

    private static void CorrelateViews(ErwinModelMap left, ErwinModelMap right, List<Change> sink)
    {
        // erwin represents views under class "View" in its metamodel.
        var lv = left.ObjectsOfClass("View").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
        var rv = right.ObjectsOfClass("View").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);

        foreach (var id in rv.Keys.Except(lv.Keys)) sink.Add(new ViewAdded(rv[id]));
        foreach (var id in lv.Keys.Except(rv.Keys)) sink.Add(new ViewDropped(lv[id]));
        foreach (var id in lv.Keys.Intersect(rv.Keys))
        {
            if (!string.Equals(lv[id].Name, rv[id].Name, StringComparison.Ordinal))
                sink.Add(new ViewRenamed(rv[id], lv[id].Name));
        }
    }

    private static void CorrelateTriggers(ErwinModelMap left, ErwinModelMap right, List<Change> sink)
    {
        // erwin's Trigger_Template class holds user-defined triggers.
        // Built-in RI trigger templates (Default_Trigger_Template) are excluded
        // to avoid noise from erwin's referential-integrity generators.
        var lt = left.ObjectsOfClass("Trigger_Template").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
        var rt = right.ObjectsOfClass("Trigger_Template").ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);

        foreach (var id in rt.Keys.Except(lt.Keys))
        {
            var trg = rt[id];
            var owner = (trg.ParentObjectId is not null && right.TryGetById(trg.ParentObjectId, out var p) && p.Class == "Entity") ? p : null;
            sink.Add(new TriggerAdded(trg, owner));
        }
        foreach (var id in lt.Keys.Except(rt.Keys))
        {
            var trg = lt[id];
            var owner = (trg.ParentObjectId is not null && left.TryGetById(trg.ParentObjectId, out var p) && p.Class == "Entity") ? p : null;
            sink.Add(new TriggerDropped(trg, owner));
        }
        foreach (var id in lt.Keys.Intersect(rt.Keys))
        {
            if (!string.Equals(lt[id].Name, rt[id].Name, StringComparison.Ordinal))
            {
                var trg = rt[id];
                var owner = (trg.ParentObjectId is not null && right.TryGetById(trg.ParentObjectId, out var p) && p.Class == "Entity") ? p : null;
                sink.Add(new TriggerRenamed(trg, lt[id].Name, owner));
            }
        }
    }

    private static void CorrelateSequences(ErwinModelMap left, ErwinModelMap right, List<Change> sink)
    {
        // Oracle / Db2 sequences show up under class "Sequence" in the
        // erwin metamodel (name may vary by DBMS adapter; we include common
        // candidates to stay tolerant).
        foreach (var cls in new[] { "Sequence", "ER_Sequence", "Oracle_Sequence" })
        {
            var ls = left.ObjectsOfClass(cls).ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
            var rs = right.ObjectsOfClass(cls).ToDictionary(o => o.ObjectId, o => o, StringComparer.Ordinal);
            foreach (var id in rs.Keys.Except(ls.Keys)) sink.Add(new SequenceAdded(rs[id]));
            foreach (var id in ls.Keys.Except(rs.Keys)) sink.Add(new SequenceDropped(ls[id]));
            foreach (var id in ls.Keys.Intersect(rs.Keys))
            {
                if (!string.Equals(ls[id].Name, rs[id].Name, StringComparison.Ordinal))
                    sink.Add(new SequenceRenamed(rs[id], ls[id].Name));
            }
        }
    }

    private static ObjectRef? ResolveParentEntity(ErwinModelMap map, ObjectRef child)
    {
        if (child.ParentObjectId is null) return null;
        return map.TryGetById(child.ParentObjectId, out var parent) ? parent : null;
    }

    private static ObjectRef UnknownParent() =>
        new(ObjectId: "(unknown)", Name: "(unknown)", Class: "Entity");
}

