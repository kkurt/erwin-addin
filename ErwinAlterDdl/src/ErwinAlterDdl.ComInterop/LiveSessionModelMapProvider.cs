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
        return new ObjectNodeDto(id, name, cls, parentId, owningEntityName);
    }

    private static string SafeStr(Func<string?> get)
    {
        try { return get() ?? string.Empty; }
        catch { return string.Empty; }
    }
}
