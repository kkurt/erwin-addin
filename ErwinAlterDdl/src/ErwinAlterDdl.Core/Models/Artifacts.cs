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
    CompareArtifact XlsArtifact)
{
    /// <summary>Optional CREATE DDL for the baseline (left) model.</summary>
    public DdlArtifact? LeftDdl { get; init; }

    /// <summary>Optional CREATE DDL for the target (right) model.</summary>
    public DdlArtifact? RightDdl { get; init; }

    /// <summary>
    /// Owner-schema lookup keyed by bare entity name (e.g. <c>"CUSTOMER" -&gt;
    /// "dbo"</c>). Built from the CompleteCompare XLS rows whose Entity/Table
    /// values come schema-prefixed (<c>dbo.CUSTOMER</c>). Emitters consult
    /// this when CreateDdlParser doesn't carry a schema for the table -
    /// erwin's default FEModel_DDL output is schema-less for some target
    /// servers, but the CC XLS exposes it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? SchemaByEntityName { get; init; }
}
