using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.ComInterop;

/// <summary>
/// In-process <see cref="IModelMapProvider"/> that walks the existing
/// active session of a live PersistenceUnit - the session erwin's
/// host attached when the user opened the model. This is the only
/// session guaranteed to see the in-memory dirty buffer, so it is the
/// correct source for the "current state" side of a Mart-bound,
/// dirty-aware compare. Empirically verified 2026-04-26 (Test L probe):
/// the active session's <c>ModelObjects.Collect(root, "Entity")</c> +
/// <c>Collect(entity, "Attribute")</c> walk surfaces freshly added
/// (unsaved) attributes; <see cref="LiveSessionModelMapProvider"/>'s
/// <c>Sessions.Add() + Open(pu, 0, 0)</c> alternative was NOT verified
/// for dirty preservation in the add-in's host process and is used by
/// the Worker (separate erwin.exe) instead.
/// </summary>
public sealed class ActiveSessionMapProvider : IModelMapProvider
{
    private readonly dynamic _scapi;
    private readonly dynamic _persistenceUnit;
    private readonly string _pathKey;
    private readonly ILogger<ActiveSessionMapProvider> _logger;

    public ActiveSessionMapProvider(
        object scapi,
        object persistenceUnit,
        string pathKey,
        ILogger<ActiveSessionMapProvider>? logger = null)
    {
        _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
        _persistenceUnit = persistenceUnit ?? throw new ArgumentNullException(nameof(persistenceUnit));
        _pathKey = !string.IsNullOrWhiteSpace(pathKey)
            ? pathKey
            : throw new ArgumentException("pathKey must be non-empty", nameof(pathKey));
        _logger = logger ?? NullLogger<ActiveSessionMapProvider>.Instance;
    }

    public Task<ErwinModelMap> BuildMapAsync(string erwinPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(erwinPath, _pathKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"ActiveSessionMapProvider only serves '{_pathKey}'; received '{erwinPath}'");

        dynamic? session = LocateActiveSession();
        if (session is null)
            throw new InvalidOperationException(
                "Could not locate an active session for the live PU. The model may have been closed.");

        ErwinModelMapDto dto = WalkSession(session);
        _logger.LogInformation(
            "Active-session walk produced {Count} objects for {Path}",
            dto.Objects.Count, _pathKey);
        return Task.FromResult(ModelMapJsonSerializer.BuildMap(dto));
    }

    /// <summary>
    /// Find the existing session whose <c>PersistenceUnit.Persistence_Unit_Id</c>
    /// matches the active PU's id. We do NOT spawn a new session here -
    /// only the host-attached session is guaranteed to see dirty edits.
    /// </summary>
    private dynamic? LocateActiveSession()
    {
        string activePuId = SafeStr(() => _persistenceUnit.PropertyBag(null, true).Value("Persistence_Unit_Id")?.ToString());
        if (string.IsNullOrEmpty(activePuId))
        {
            _logger.LogWarning("Active PU has no Persistence_Unit_Id; cannot match a session.");
            return null;
        }

        int sessCount = 0;
        try { sessCount = (int)_scapi.Sessions.Count; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sessions.Count read failed");
            return null;
        }

        for (int i = 0; i < sessCount; i++)
        {
            dynamic? s;
            try { s = _scapi.Sessions.Item(i); }
            catch { continue; } // some indices throw "Value does not fall within range" - normal
            if (s is null) continue;

            string sId = SafeStr(() => s.PersistenceUnit?.PropertyBag(null, true).Value("Persistence_Unit_Id")?.ToString());
            if (string.Equals(sId, activePuId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Matched active session at index {Index}", i);
                return s;
            }
        }
        _logger.LogWarning("No session in Sessions[0..{Count}] matched activePu.Persistence_Unit_Id={Id}", sessCount, activePuId);
        return null;
    }

    /// <summary>
    /// Walks the host-attached session's <c>ModelObjects</c> in the same
    /// pattern <see cref="LiveSessionModelMapProvider"/> uses for fresh
    /// sessions - Entity (with nested Attribute + Key_Group), then top-
    /// level Relationship / View / Trigger_Template / Sequence. Schema
    /// prefix is applied as a post-pass so the emitter renders
    /// <c>[schema].[entity]</c> identifiers correctly.
    /// </summary>
    private ErwinModelMapDto WalkSession(dynamic session)
    {
        dynamic modelObjects = session.ModelObjects;
        dynamic root = modelObjects.Root
            ?? throw new InvalidOperationException("session.ModelObjects.Root was null");

        var nodes = new List<ObjectNodeDto>();
        CollectTopLevel(modelObjects, root, "Entity", nodes, walkNested: true);
        CollectTopLevel(modelObjects, root, "Relationship", nodes, walkNested: false);
        CollectTopLevel(modelObjects, root, "View", nodes, walkNested: false);
        CollectTopLevel(modelObjects, root, "Trigger_Template", nodes, walkNested: false);
        foreach (var cls in new[] { "Sequence", "Oracle_Sequence", "ER_Sequence" })
            CollectTopLevel(modelObjects, root, cls, nodes, walkNested: false);

        ApplySchemaPrefix(modelObjects, root, nodes);

        return new ErwinModelMapDto(ErwinModelMapDto.CurrentSchemaVersion, _pathKey, nodes);
    }

    private static void CollectTopLevel(
        dynamic modelObjects, dynamic root, string className,
        List<ObjectNodeDto> sink, bool walkNested)
    {
        dynamic items;
        try { items = modelObjects.Collect(root, className); }
        catch { return; }
        if (items is null) return;

        foreach (dynamic obj in items)
        {
            if (obj is null) continue;
            var node = ToNode(obj, owningEntityName: null);
            sink.Add(node);

            if (walkNested && className == "Entity")
            {
                TryCollectChildren(modelObjects, obj, "Attribute", node.Name, sink);
                TryCollectChildren(modelObjects, obj, "Key_Group", null, sink);
            }
        }
    }

    private static void TryCollectChildren(
        dynamic modelObjects, dynamic parent, string className,
        string? owningEntityName, List<ObjectNodeDto> sink)
    {
        dynamic items;
        try { items = modelObjects.Collect(parent, className); }
        catch { return; }
        if (items is null) return;

        foreach (dynamic obj in items)
        {
            if (obj is null) continue;
            sink.Add(ToNode(obj, owningEntityName));
        }
    }

    private static ObjectNodeDto ToNode(dynamic obj, string? owningEntityName)
    {
        string id = SafeStr(() => obj.ObjectId?.ToString());
        string name = SafeStr(() => obj.Name?.ToString());
        string cls = SafeStr(() => obj.ClassName?.ToString());
        string? parentId = null;
        try
        {
            dynamic ctx = obj.Context;
            if (ctx is not null)
                parentId = SafeStr(() => ctx.ObjectId?.ToString());
        }
        catch { /* Context unavailable on some classes */ }
        if (string.IsNullOrEmpty(parentId)) parentId = null;
        if (string.IsNullOrEmpty(owningEntityName)) owningEntityName = null;

        if (cls == "Entity" || cls == "View")
        {
            var sch = ReadSchemaProperty(obj);
            if (!string.IsNullOrEmpty(sch))
                name = $"{sch}.{name}";
        }

        return new ObjectNodeDto(id, name, cls, parentId, owningEntityName);
    }

    private static string ReadSchemaProperty(dynamic obj)
    {
        foreach (var key in new[]
        {
            "Schema",
            "SQL_Server_Schema",
            "Oracle_Owner",
            "Owner_Schema",
            "Owner",
            "Physical_Schema",
            "Table_Owner",
            "Owner_Name",
            "Schema_Name",
            "Owner_Schema_Name",
            "DB_Schema_Name",
        })
        {
            try
            {
                var v = SafeStr(() => obj.Properties(key).Value?.ToString());
                if (IsRealValue(v)) return v;
            }
            catch { }
        }

        foreach (var directProbe in new Func<dynamic, string>[]
        {
            o => SafeStr(() => o.Schema?.Name?.ToString()),
            o => SafeStr(() => o.Owner?.Name?.ToString()),
            o => SafeStr(() => o.OwnerSchema?.Name?.ToString()),
            o => SafeStr(() => o.Schema?.ToString()),
            o => SafeStr(() => o.Owner?.ToString()),
        })
        {
            try
            {
                var v = directProbe(obj);
                if (IsRealValue(v)) return v;
            }
            catch { }
        }
        return string.Empty;
    }

    private static bool IsRealValue(string v) =>
        !string.IsNullOrEmpty(v)
        && !v.StartsWith("%", StringComparison.Ordinal)
        && !v.Equals("System.__ComObject", StringComparison.Ordinal);

    private static string SafeStr(Func<string?> get)
    {
        try { return get() ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Mirrors <see cref="LiveSessionModelMapProvider.ApplySchemaPrefix"/>:
    /// rewrites Entity / View nodes with "schema.entity" so the emitter
    /// renders the qualified identifier when the schema is set on the
    /// model side rather than directly on the entity.
    /// </summary>
    private static void ApplySchemaPrefix(dynamic modelObjects, dynamic root, List<ObjectNodeDto> nodes)
    {
        var schemaByObjectId = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            dynamic schemas = modelObjects.Collect(root, "Schema");
            if (schemas is not null)
            {
                foreach (dynamic sch in schemas)
                {
                    if (sch is null) continue;
                    string schName = SafeStr(() => sch.Name?.ToString());
                    if (string.IsNullOrEmpty(schName)) continue;
                    foreach (var memberClass in new[] { "Entity", "View" })
                    {
                        try
                        {
                            dynamic members = modelObjects.Collect(sch, memberClass);
                            if (members is null) continue;
                            foreach (dynamic m in members)
                            {
                                if (m is null) continue;
                                string mid = SafeStr(() => m.ObjectId?.ToString());
                                if (!string.IsNullOrEmpty(mid)) schemaByObjectId[mid] = schName;
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        if (schemaByObjectId.Count == 0) return;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Class != "Entity" && nodes[i].Class != "View") continue;
            if (nodes[i].Name.Contains('.')) continue;
            if (schemaByObjectId.TryGetValue(nodes[i].ObjectId, out var sch) && !string.IsNullOrEmpty(sch))
                nodes[i] = nodes[i] with { Name = $"{sch}.{nodes[i].Name}" };
        }
    }
}
