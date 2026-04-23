using System.Runtime.InteropServices;

using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.ComInterop;

/// <summary>
/// Wraps a live SCAPI handle (e.g. obtained inside the addin) and lets the Core
/// pipeline drive it via <see cref="IScapiSession"/>. Assumes the caller passes
/// a already-activated SCAPI COM object; this class does NOT own its lifetime.
/// </summary>
public sealed class InProcessScapiSession : IScapiSession
{
    private readonly dynamic _scapi;
    private readonly ILogger<InProcessScapiSession> _logger;
    private bool _disposed;

    public InProcessScapiSession(object scapi, ILogger<InProcessScapiSession>? logger = null)
    {
        _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
        _logger = logger ?? NullLogger<InProcessScapiSession>.Instance;
    }

    public Task<CompareArtifact> RunCompleteCompareAsync(
        string leftErwinPath, string rightErwinPath, CompareOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var xlsPath = options.OutputXlsPath ?? Path.Combine(Path.GetTempPath(),
            $"erwin-diff-{Guid.NewGuid():N}.xls");

        _logger.LogInformation("CC {Left} -> {Right} preset={Preset} level={Level} out={Out}",
            leftErwinPath, rightErwinPath, options.PresetOrOptionXmlPath,
            options.Level.ToScapiString(), xlsPath);

        // CompleteCompare works on disk-saved models; a disposable PU via
        // PersistenceUnits.Create(propBag) is the idiomatic scratch target.
        var bagType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0", throwOnError: true)!;
        dynamic bag = Activator.CreateInstance(bagType)!;
        dynamic pu = _scapi.PersistenceUnits.Create(bag);
        try
        {
            bool ok = pu.CompleteCompare(
                leftErwinPath, rightErwinPath, xlsPath,
                options.PresetOrOptionXmlPath, options.Level.ToScapiString(), "");
            if (!ok)
                throw new InvalidOperationException("CompleteCompare returned false");
            var size = new FileInfo(xlsPath).Length;
            return Task.FromResult(new CompareArtifact(xlsPath, size, 0));
        }
        finally
        {
            try { Marshal.FinalReleaseComObject(pu); } catch { /* best effort */ }
            try { Marshal.FinalReleaseComObject(bag); } catch { /* best effort */ }
        }
    }

    public Task<DdlArtifact> GenerateCreateDdlAsync(
        string erwinPath, DdlOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        // FEModel_DDL on r10.10 hits singleton server state pollution when called
        // across multiple PUs in the same lifetime (bug documented in
        // reference_scapi_gotchas_r10.md). Phase 3 will drive this through the
        // out-of-process session with fresh erwin.exe per call.
        throw new NotSupportedException(
            "InProcessScapiSession does not implement GenerateCreateDdlAsync in Phase 2. "
            + "Use OutOfProcessScapiSession in Phase 3 for isolated process-per-DDL.");
    }

    public Task<ModelMetadata> ReadModelMetadataAsync(string erwinPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        dynamic pu = _scapi.PersistenceUnits.Add(erwinPath, "");
        try
        {
            dynamic bag = pu.PropertyBag(null, true);
            string puId = SafeGet(bag, "Persistence_Unit_Id");
            string name = pu.Name?.ToString() ?? "";
            string modelType = SafeGet(bag, "Model_Type");
            string target = SafeGet(bag, "Target_Server");
            int verMajor = ParseInt(SafeGet(bag, "Target_Server_Version"));
            int verMinor = ParseInt(SafeGet(bag, "Target_Server_Minor_Version"));
            return Task.FromResult(new ModelMetadata(puId, name, modelType, target, verMajor, verMinor));
        }
        finally
        {
            try { Marshal.FinalReleaseComObject(pu); } catch { /* best effort */ }
        }
    }

    private static string SafeGet(dynamic bag, string key)
    {
        try { return (string)(bag.Value(key) ?? string.Empty); }
        catch { return string.Empty; }
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InProcessScapiSession));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
