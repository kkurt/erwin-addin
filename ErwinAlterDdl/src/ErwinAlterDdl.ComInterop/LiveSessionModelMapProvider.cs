using System.Runtime.InteropServices;

using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.ComInterop;

/// <summary>
/// In-process <see cref="IModelMapProvider"/> that walks a live SCAPI
/// PersistenceUnit via its <c>session.ModelObjects</c> collection and returns
/// a <see cref="ErwinModelMap"/> - no disk save, no <c>.xml</c> export, no
/// Mart corruption. Used by the add-in to capture the active model's current
/// (including dirty buffer) state as one side of a compare.
///
/// The provider is constructed with a single PU handle and is tied to that
/// PU's lifetime. Every <see cref="BuildMapAsync"/> call opens a fresh
/// session on the PU, walks it, closes the session.
/// </summary>
public sealed class LiveSessionModelMapProvider : IModelMapProvider
{
    private readonly dynamic _scapi;
    private readonly dynamic _persistenceUnit;
    private readonly string _pathKey;
    private readonly ILogger<LiveSessionModelMapProvider> _logger;

    /// <summary>
    /// Wraps a live PU. <paramref name="pathKey"/> is the logical "path" the
    /// orchestrator will pass back on <see cref="BuildMapAsync"/>; use the
    /// same string when building a <c>PrebuiltModelMapProvider</c> or when
    /// calling <c>CompareOrchestrator.CompareAsync</c> so the lookups line up.
    /// </summary>
    public LiveSessionModelMapProvider(
        object scapi,
        object persistenceUnit,
        string pathKey,
        ILogger<LiveSessionModelMapProvider>? logger = null)
    {
        _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
        _persistenceUnit = persistenceUnit ?? throw new ArgumentNullException(nameof(persistenceUnit));
        _pathKey = !string.IsNullOrWhiteSpace(pathKey)
            ? pathKey
            : throw new ArgumentException("pathKey must be non-empty", nameof(pathKey));
        _logger = logger ?? NullLogger<LiveSessionModelMapProvider>.Instance;
    }

    public Task<ErwinModelMap> BuildMapAsync(string erwinPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(erwinPath, _pathKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"LiveSessionModelMapProvider only serves '{_pathKey}'; received '{erwinPath}'");
        var dto = WalkLivePu();
        _logger.LogInformation("Live walk produced {Count} objects for {Path}", dto.Objects.Count, _pathKey);
        return Task.FromResult(ModelMapJsonSerializer.BuildMap(dto));
    }

    private ErwinModelMapDto WalkLivePu()
    {
        dynamic sess = _scapi.Sessions.Add();
        try
        {
            sess.Open(_persistenceUnit, 0, 0); // SCD_SL_M0 - data level
            dynamic modelObjects = sess.ModelObjects;
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
        finally
        {
            try { sess.Close(); } catch { /* best effort */ }
            try { Marshal.FinalReleaseComObject(sess); } catch { /* best effort */ }
        }
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

        // erwin's Table editor exposes a Schema column for Entity / View;
        // prefix the name with that schema so the emitter renders
        // [schema].[entity] (matches erwin's own CompleteCompare output).
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
        // First try the Properties("Name") accessor pattern with the most
        // common erwin r10 property keys.
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
            catch { /* try next */ }
        }

        // Fall back to direct COM property access. Some r10 metamodels
        // surface Schema as a navigable reference rather than a scalar
        // Properties() entry.
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
    /// Mirror of the Worker's schema-prefix post-pass. Iterates the
    /// model's <c>Schema</c> objects, maps each member entity / view to its
    /// owning schema name, and rewrites the matching nodes' Name to
    /// "schema.entity" so the emitter's QuoteQualified splits it back into
    /// "[schema].[entity]".
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
                        catch { /* member class may not exist for this schema */ }
                    }
                }
            }
        }
        catch { /* Schema class may be unavailable in some target servers */ }

        if (schemaByObjectId.Count == 0) return;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Class != "Entity" && nodes[i].Class != "View") continue;
            // ToNode may have already attached a schema prefix via the
            // entity's Property("Schema"); skip to avoid "dbo.dbo.X".
            if (nodes[i].Name.Contains('.')) continue;
            if (schemaByObjectId.TryGetValue(nodes[i].ObjectId, out var sch) && !string.IsNullOrEmpty(sch))
                nodes[i] = nodes[i] with { Name = $"{sch}.{nodes[i].Name}" };
        }
    }
}
