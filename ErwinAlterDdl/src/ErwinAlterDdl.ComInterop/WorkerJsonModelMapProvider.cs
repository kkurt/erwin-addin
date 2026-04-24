using System.Diagnostics;

using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.ComInterop;

/// <summary>
/// <see cref="IModelMapProvider"/> that invokes the erwin-alter-ddl-worker
/// child process to walk a <c>.erwin</c> file's <c>session.ModelObjects</c>
/// and emit an <c>ErwinModelMapDto</c> JSON. The add-in / CLI then reads
/// the JSON back into an <see cref="Core.Parsing.ErwinModelMap"/> via
/// <see cref="ModelMapJsonSerializer"/>.
///
/// This removes the runtime dependency on a sibling <c>.xml</c> export file
/// (previously required by <c>XmlFileModelMapProvider</c>). The Worker owns
/// its own short-lived erwin.exe LocalServer, avoiding any interaction with
/// a concurrently-running add-in session.
/// </summary>
public sealed class WorkerJsonModelMapProvider : IModelMapProvider
{
    private const string WorkerExeName = "erwin-alter-ddl-worker.exe";
    private const string WorkerPathEnvVar = "ERWIN_ALTER_DDL_WORKER";

    private readonly string _workerPath;
    private readonly TimeSpan _perOpTimeout;
    private readonly ILogger<WorkerJsonModelMapProvider> _logger;

    public WorkerJsonModelMapProvider(
        string? workerPath = null,
        TimeSpan? perOpTimeout = null,
        ILogger<WorkerJsonModelMapProvider>? logger = null)
    {
        _workerPath = workerPath
            ?? Environment.GetEnvironmentVariable(WorkerPathEnvVar)
            ?? ProbeDefaultWorkerPath()
            ?? throw new FileNotFoundException(
                $"could not locate {WorkerExeName}. set {WorkerPathEnvVar} or pass an explicit path.");
        if (!File.Exists(_workerPath))
            throw new FileNotFoundException("worker exe missing", _workerPath);
        _perOpTimeout = perOpTimeout ?? TimeSpan.FromMinutes(3);
        _logger = logger ?? NullLogger<WorkerJsonModelMapProvider>.Instance;
    }

    public async Task<ErwinModelMap> BuildMapAsync(string erwinPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(erwinPath);
        if (!File.Exists(erwinPath)) throw new FileNotFoundException("erwin file missing", erwinPath);

        var jsonOut = Path.Combine(Path.GetTempPath(), $"erwin-model-map-{Guid.NewGuid():N}.json");
        try
        {
            await RunWorkerAsync(["dump-model", "--erwin", erwinPath, "--out", jsonOut], ct).ConfigureAwait(false);
            if (!File.Exists(jsonOut))
                throw new InvalidOperationException($"worker did not produce {jsonOut}");
            return ModelMapJsonSerializer.DeserializeFile(jsonOut);
        }
        finally
        {
            try { if (File.Exists(jsonOut)) File.Delete(jsonOut); }
            catch { /* best effort */ }
        }
    }

    private async Task<string> RunWorkerAsync(string[] args, CancellationToken ct)
    {
        int killed = KillStaleErwinProcesses();
        if (killed > 0) _logger.LogInformation("Pre-call kill: {Killed} stale erwin.exe", killed);

        var psi = new ProcessStartInfo
        {
            FileName = _workerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _logger.LogInformation("Worker dump-model: {Path} {Args}", _workerPath, string.Join(' ', args));

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_perOpTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { TryKill(process); throw; }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        _logger.LogDebug("Worker dump-model exit={Code} stdout.len={Out} stderr.len={Err}",
            process.ExitCode, stdout.Length, stderr.Length);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"worker dump-model failed with exit code {process.ExitCode}. stderr: {stderr.TrimEnd()}");
        return stdout;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static int KillStaleErwinProcesses()
    {
        int killed = 0;
        foreach (var p in Process.GetProcessesByName("erwin"))
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                    killed++;
                }
            }
            catch { /* access denied / already exited */ }
            finally { try { p.Dispose(); } catch { } }
        }
        return killed;
    }

    private static string? ProbeDefaultWorkerPath()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir, WorkerExeName),
            Path.Combine(baseDir, "..", "ErwinAlterDdl.Worker", WorkerExeName),
        })
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }

        var probeRoot = baseDir;
        for (int i = 0; i < 6 && probeRoot is not null; i++)
        {
            var c1 = Path.Combine(probeRoot, "src", "ErwinAlterDdl.Worker", "bin", "Debug", "net10.0-windows", WorkerExeName);
            if (File.Exists(c1)) return c1;
            var c2 = Path.Combine(probeRoot, "src", "ErwinAlterDdl.Worker", "bin", "Release", "net10.0-windows", WorkerExeName);
            if (File.Exists(c2)) return c2;
            probeRoot = Directory.GetParent(probeRoot)?.FullName;
        }
        return null;
    }
}
