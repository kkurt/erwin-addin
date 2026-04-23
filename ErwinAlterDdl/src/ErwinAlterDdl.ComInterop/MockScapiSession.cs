using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AlterDdl.ComInterop;

/// <summary>
/// Test double: returns artifacts from a pre-populated directory instead of
/// talking to SCAPI. Useful for unit tests and for replay when SCAPI is
/// unavailable (e.g. SCAPI singleton pollution). The caller prepares:
///     artifactsDir/
///       diff.xls
///       left_metadata.json  (optional; otherwise synthesized)
///       right_metadata.json (optional)
///       left.sql            (optional; Phase 3)
///       right.sql           (optional; Phase 3)
/// </summary>
public sealed class MockScapiSession : IScapiSession
{
    private readonly string _artifactsDir;
    private readonly ModelMetadata _leftMetadata;
    private readonly ModelMetadata _rightMetadata;

    public MockScapiSession(
        string artifactsDir,
        ModelMetadata? leftMetadata = null,
        ModelMetadata? rightMetadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDir);
        if (!Directory.Exists(artifactsDir))
            throw new DirectoryNotFoundException(artifactsDir);

        _artifactsDir = artifactsDir;
        _leftMetadata = leftMetadata ?? DefaultMetadata("left");
        _rightMetadata = rightMetadata ?? DefaultMetadata("right");
    }

    public Task<CompareArtifact> RunCompleteCompareAsync(
        string leftErwinPath, string rightErwinPath, CompareOptions options, CancellationToken ct = default)
    {
        var xlsPath = Path.Combine(_artifactsDir, "diff.xls");
        if (!File.Exists(xlsPath))
            throw new FileNotFoundException($"mock artifacts dir missing diff.xls: {xlsPath}");
        var size = new FileInfo(xlsPath).Length;
        return Task.FromResult(new CompareArtifact(xlsPath, size, 0));
    }

    public Task<DdlArtifact> GenerateCreateDdlAsync(
        string erwinPath, DdlOptions options, CancellationToken ct = default)
    {
        var basename = Path.GetFileNameWithoutExtension(erwinPath);
        var sqlPath = Path.Combine(_artifactsDir, $"{basename}.sql");
        if (!File.Exists(sqlPath))
            throw new FileNotFoundException($"mock artifacts dir missing {basename}.sql", sqlPath);
        return Task.FromResult(new DdlArtifact(sqlPath, new FileInfo(sqlPath).Length, _leftMetadata.TargetServer));
    }

    public Task<ModelMetadata> ReadModelMetadataAsync(string erwinPath, CancellationToken ct = default)
    {
        // Distinguish left vs right by filename suffix (_v1 / _v2). Fallback to left.
        var lower = Path.GetFileName(erwinPath).ToLowerInvariant();
        var picked = lower.Contains("v2") ? _rightMetadata : _leftMetadata;
        return Task.FromResult(picked);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ModelMetadata DefaultMetadata(string label) => new(
        PersistenceUnitId: $"(mock-{label})",
        Name: label,
        ModelType: "Physical",
        TargetServer: "SQL Server",
        TargetServerVersion: 15,
        TargetServerMinorVersion: 0);
}
