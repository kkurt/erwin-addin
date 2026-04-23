using EliteSoft.Erwin.AlterDdl.Core.Parsing;

namespace EliteSoft.Erwin.AlterDdl.Core.Abstractions;

/// <summary>
/// Produces an <see cref="ErwinModelMap"/> (ObjectId -> Name / Class / Parent
/// lookup) for one side of a compare. The provider abstracts over the data
/// source, so the pipeline can work off any of:
///   * the co-located <c>.xml</c> export (default, <see cref="XmlFileModelMapProvider"/>).
///   * an already-loaded <see cref="ErwinModelMap"/> (tests, CLI, preloaded maps).
///   * a live SCAPI session that walks <c>ModelObjects</c> directly without a
///     disk artifact (add-in, in-process).
///   * an out-of-process worker that hands back a JSON-serialized map.
///
/// <see cref="Pipeline.CompareOrchestrator"/> calls this once per side, after
/// CompleteCompare. The <c>erwinPath</c> parameter is informational (letting
/// the provider choose a sibling artifact when appropriate); a provider that
/// already has the map may ignore it.
/// </summary>
public interface IModelMapProvider
{
    /// <summary>
    /// Build (or return a cached) <see cref="ErwinModelMap"/> for the given
    /// <paramref name="erwinPath"/>.
    /// </summary>
    Task<ErwinModelMap> BuildMapAsync(string erwinPath, CancellationToken ct = default);
}
