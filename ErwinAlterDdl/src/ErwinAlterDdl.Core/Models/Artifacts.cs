namespace EliteSoft.Erwin.AlterDdl.Core.Models;

/// <summary>
/// Result of a successful <c>CompleteCompare</c> invocation.
/// </summary>
public sealed record CompareArtifact(string XlsPath, long SizeBytes, int RowCountHint);

/// <summary>
/// Result of a successful <c>FEModel_DDL</c> invocation.
/// </summary>
public sealed record DdlArtifact(string SqlPath, long SizeBytes, string TargetServer);

/// <summary>
/// Read-only metadata captured from a PU's property bag. Used by the orchestrator
/// to pick the right SQL emitter (Phase 3) and to log context.
/// </summary>
public sealed record ModelMetadata(
    string PersistenceUnitId,
    string Name,
    string ModelType,
    string TargetServer,
    int TargetServerVersion,
    int TargetServerMinorVersion);

/// <summary>
/// Top-level output of the <c>CompareOrchestrator</c>. Consumed by CLI (JSON
/// dump) and REST API (HTTP body).
/// </summary>
public sealed record CompareResult(
    ModelMetadata LeftMetadata,
    ModelMetadata RightMetadata,
    IReadOnlyList<Change> Changes,
    CompareArtifact XlsArtifact);
