using EliteSoft.Erwin.AlterDdl.Core.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.Core.Parsing;

/// <summary>
/// Default <see cref="IModelMapProvider"/> implementation. Loads the <c>.xml</c>
/// export that erwin's <em>Actions &gt; Export &gt; XML</em> menu (or a compatible
/// pipeline) emits alongside the <c>.erwin</c> file. This is the Phase 2
/// behavior preserved for backward compatibility; callers that cannot produce
/// an XML sibling at runtime (e.g. the in-process add-in) plug in an
/// alternative provider.
/// </summary>
public sealed class XmlFileModelMapProvider : IModelMapProvider
{
    public Task<ErwinModelMap> BuildMapAsync(string erwinPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(erwinPath);
        ct.ThrowIfCancellationRequested();

        var xmlPath = Path.ChangeExtension(erwinPath, ".xml");
        if (!File.Exists(xmlPath))
            throw new FileNotFoundException(
                "xml export missing. The default XmlFileModelMapProvider expects a " +
                "sibling .xml file (erwin GUI: Actions > Export > XML). Pass a " +
                "different IModelMapProvider to CompareOrchestrator if this is not " +
                "your source-of-truth.",
                xmlPath);

        return Task.FromResult(ErwinXmlObjectIdMapper.ParseFile(xmlPath));
    }
}

/// <summary>
/// Convenience provider that returns the same pre-built map regardless of
/// which side the orchestrator is asking for. Useful when the caller already
/// has an <see cref="ErwinModelMap"/> in memory (tests, two-sided live reads
/// bundled ahead of time).
/// </summary>
public sealed class PrebuiltModelMapProvider : IModelMapProvider
{
    private readonly IReadOnlyDictionary<string, ErwinModelMap> _byErwinPath;

    public PrebuiltModelMapProvider(string leftErwinPath, ErwinModelMap leftMap, string rightErwinPath, ErwinModelMap rightMap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftErwinPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightErwinPath);
        ArgumentNullException.ThrowIfNull(leftMap);
        ArgumentNullException.ThrowIfNull(rightMap);
        _byErwinPath = new Dictionary<string, ErwinModelMap>(StringComparer.OrdinalIgnoreCase)
        {
            [leftErwinPath] = leftMap,
            [rightErwinPath] = rightMap,
        };
    }

    public Task<ErwinModelMap> BuildMapAsync(string erwinPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(erwinPath);
        if (!_byErwinPath.TryGetValue(erwinPath, out var map))
            throw new InvalidOperationException(
                $"PrebuiltModelMapProvider has no map for '{erwinPath}'. It was constructed with " +
                string.Join(", ", _byErwinPath.Keys) + ".");
        return Task.FromResult(map);
    }
}
