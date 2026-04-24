using EliteSoft.Erwin.AlterDdl.Core.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.Core.Parsing;

/// <summary>
/// Convenience provider that hands out pre-built <see cref="ErwinModelMap"/>
/// instances by .erwin path. Intended for tests, benchmark harnesses, and any
/// caller that already has both maps in memory.
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
