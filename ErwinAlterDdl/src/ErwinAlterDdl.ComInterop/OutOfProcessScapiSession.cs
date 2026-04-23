using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AlterDdl.ComInterop;

/// <summary>
/// Phase 3 target: owns its own erwin.exe child process, isolated from any user
/// GUI, respawned on demand to defeat the SCAPI r10.10 singleton state
/// pollution. Phase 2 ships this as a stub so wiring code can reference the
/// type. Invoking any method throws <see cref="NotImplementedException"/>.
/// </summary>
public sealed class OutOfProcessScapiSession : IScapiSession
{
    public Task<CompareArtifact> RunCompleteCompareAsync(
        string leftErwinPath, string rightErwinPath, CompareOptions options, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 3 - process isolation not implemented yet");

    public Task<DdlArtifact> GenerateCreateDdlAsync(
        string erwinPath, DdlOptions options, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 3 - process isolation not implemented yet");

    public Task<ModelMetadata> ReadModelMetadataAsync(
        string erwinPath, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 3 - process isolation not implemented yet");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
