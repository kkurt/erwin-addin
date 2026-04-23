using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AlterDdl.Core.Abstractions;

/// <summary>
/// Abstraction over the erwin SCAPI COM surface. Implementations hide all COM
/// lifecycle, state-pollution workarounds and process-isolation choices from
/// Core pipeline code. See <c>docs/ARCHITECTURE.md</c> for sequence flow.
/// </summary>
public interface IScapiSession : IAsyncDisposable
{
    /// <summary>
    /// Run erwin's <c>ISCPersistenceUnit::CompleteCompare</c> on two disk-based
    /// .erwin files. Returns path to the produced XLS (HTML table) plus size
    /// and a coarse row count.
    /// </summary>
    Task<CompareArtifact> RunCompleteCompareAsync(
        string leftErwinPath,
        string rightErwinPath,
        CompareOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Run erwin's <c>ISCPersistenceUnit::FEModel_DDL</c> on a single .erwin
    /// file. Phase 3 uses this for alter-DDL body details. Phase 2 session
    /// implementations may return <see cref="DdlArtifact"/> stubs.
    /// </summary>
    Task<DdlArtifact> GenerateCreateDdlAsync(
        string erwinPath,
        DdlOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Read the PropertyBag of a PU to learn Target_Server, Model_Type, version.
    /// Used by the orchestrator to pick the right dialect emitter (Phase 3) and
    /// to log context. Cheap, no DDL or compare work.
    /// </summary>
    Task<ModelMetadata> ReadModelMetadataAsync(
        string erwinPath,
        CancellationToken ct = default);
}
