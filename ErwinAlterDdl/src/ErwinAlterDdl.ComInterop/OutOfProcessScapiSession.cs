using System.Diagnostics;
using System.Text.Json;

using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.ComInterop;

/// <summary>
/// Executes each SCAPI operation in a short-lived child process
/// (<c>erwin-alter-ddl-worker.exe</c>). The Worker owns its own
/// <c>erwin.exe</c> COM LocalServer and exits when the operation completes,
/// guaranteeing clean state between operations. This defeats the SCAPI
/// r10.10 singleton state-pollution bug documented in
/// <c>reference_scapi_gotchas_r10.md</c>.
/// </summary>
public sealed class OutOfProcessScapiSession : IScapiSession
{
    private const string WorkerExeName = "erwin-alter-ddl-worker.exe";
    private const string WorkerPathEnvVar = "ERWIN_ALTER_DDL_WORKER";

    private readonly string _workerPath;
    private readonly TimeSpan _perOpTimeout;
    private readonly ILogger<OutOfProcessScapiSession> _logger;
    private bool _disposed;

    public OutOfProcessScapiSession(
        string? workerPath = null,
        TimeSpan? perOpTimeout = null,
        ILogger<OutOfProcessScapiSession>? logger = null)
    {
        _workerPath = workerPath
            ?? Environment.GetEnvironmentVariable(WorkerPathEnvVar)
            ?? ProbeDefaultWorkerPath()
            ?? throw new FileNotFoundException(
                $"could not locate {WorkerExeName}. set {WorkerPathEnvVar} or pass an explicit path.");

        if (!File.Exists(_workerPath))
            throw new FileNotFoundException("worker exe missing", _workerPath);

        _perOpTimeout = perOpTimeout ?? TimeSpan.FromMinutes(5);
        _logger = logger ?? NullLogger<OutOfProcessScapiSession>.Instance;
    }

    public async Task<CompareArtifact> RunCompleteCompareAsync(
        string leftErwinPath, string rightErwinPath, CompareOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(leftErwinPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightErwinPath);

        var outPath = options.OutputXlsPath ?? Path.Combine(Path.GetTempPath(),
            $"erwin-diff-{Guid.NewGuid():N}.xls");

        var args = new[]
        {
            "cc",
            "--left", leftErwinPath,
            "--right", rightErwinPath,
            "--out", outPath,
            "--preset", options.PresetOrOptionXmlPath,
            "--level", options.Level.ToScapiString(),
        };

        try
        {
            var json = await RunWorkerAsync(args, ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<CompareArtifact>(json)
                ?? throw new InvalidOperationException("worker returned empty CC result");
            return result;
        }
        catch (InvalidOperationException ex) when (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
        {
            // SCAPI r10.10 AccessViolation during CC cleanup is known (see
            // reference_scapi_gotchas_r10.md). The XLS is written BEFORE the
            // native crash, so if we can see it on disk with non-zero size we
            // honor it as the artifact. Logged as warning so the anomaly is
            // still visible.
            var info = new FileInfo(outPath);
            _logger.LogWarning(
                "Worker crashed after CC wrote {Size} bytes to {Path}; treating as success (SCAPI cleanup bug). msg: {Msg}",
                info.Length, outPath, ex.Message);
            return new CompareArtifact(outPath, info.Length, 0);
        }
    }

    public async Task<DdlArtifact> GenerateCreateDdlAsync(
        string erwinPath, DdlOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(erwinPath);

        var outPath = options.OutputSqlPath ?? Path.Combine(Path.GetTempPath(),
            $"erwin-ddl-{Guid.NewGuid():N}.sql");

        var args = new List<string>
        {
            "ddl",
            "--erwin", erwinPath,
            "--out", outPath,
        };
        if (!string.IsNullOrEmpty(options.FeOptionXmlPath))
        {
            args.Add("--fe-option-xml");
            args.Add(options.FeOptionXmlPath);
        }

        try
        {
            var json = await RunWorkerAsync(args.ToArray(), ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<DdlArtifact>(json)
                ?? throw new InvalidOperationException("worker returned empty ddl result");
            return result;
        }
        catch (InvalidOperationException ex) when (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
        {
            // Same SCAPI r10.10 cleanup AV pattern as CC: sql is written before
            // the native crash. Honor the artifact.
            var info = new FileInfo(outPath);
            _logger.LogWarning(
                "Worker crashed after DDL wrote {Size} bytes to {Path}; treating as success. msg: {Msg}",
                info.Length, outPath, ex.Message);
            return new DdlArtifact(outPath, info.Length, "(unknown)");
        }
    }

    public async Task<ModelMetadata> ReadModelMetadataAsync(string erwinPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(erwinPath);
        var json = await RunWorkerAsync(["metadata", "--erwin", erwinPath], ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ModelMetadata>(json)
            ?? throw new InvalidOperationException("worker returned empty metadata");
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    // ---------- internals ----------

    private async Task<string> RunWorkerAsync(string[] args, CancellationToken ct)
    {
        // Kill any erwin.exe left from a previous worker call. Each SCAPI op
        // must run against a freshly started LocalServer to dodge r10.10's
        // singleton state pollution. Access-denied (system-owned instances)
        // is silently tolerated; if we can't kill them the op will still
        // fail and surface a useful error.
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

        _logger.LogInformation("Worker start: {Path} {Args}", _workerPath, string.Join(' ', args));

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_perOpTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        _logger.LogDebug("Worker exit={Code} stdout.len={Out} stderr.len={Err}",
            process.ExitCode, stdout.Length, stderr.Length);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"worker failed with exit code {process.ExitCode}. stderr: {stderr.TrimEnd()}");
        }
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
            catch { /* access denied / already exited - best effort */ }
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
            // fallback: sibling bin directory at publish time
            Path.Combine(baseDir, "..", "ErwinAlterDdl.Worker", WorkerExeName),
        })
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }

        // dev-time: walk up two directories looking for the Worker's bin output
        // (handy when running CLI from `dotnet run`).
        var probeRoot = baseDir;
        for (int i = 0; i < 6 && probeRoot is not null; i++)
        {
            var candidate = Path.Combine(probeRoot, "src", "ErwinAlterDdl.Worker", "bin",
                "Debug", "net10.0-windows", WorkerExeName);
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(probeRoot, "src", "ErwinAlterDdl.Worker", "bin",
                "Release", "net10.0-windows", WorkerExeName);
            if (File.Exists(candidate)) return candidate;
            probeRoot = Directory.GetParent(probeRoot)?.FullName;
        }
        return null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OutOfProcessScapiSession));
    }
}
