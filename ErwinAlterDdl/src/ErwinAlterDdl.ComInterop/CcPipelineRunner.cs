using System.Diagnostics;
using System.Text.Json;

using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.ComInterop;

/// <summary>
/// Bundles the artifacts produced by a single Worker <c>cc-pipeline</c>
/// invocation: the CC XLS, both sides' ObjectId model maps, and (optionally)
/// the right-side CREATE DDL. Letting one Worker process drive every SCAPI
/// step amortizes the ~10s erwin LocalServer startup that each separate
/// Worker spawn pays.
/// </summary>
public sealed record CcPipelineResult(
    string LeftLocator,
    string RightLocator,
    CompareArtifact Xls,
    ErwinModelMap LeftMap,
    ErwinModelMap RightMap,
    DdlArtifact? LeftDdl,
    DdlArtifact? RightDdl);

/// <summary>
/// Spawns a single Worker process that runs the full CC pipeline (open both
/// Mart locators, dump model maps, save to temp .erwin files, run
/// CompleteCompare, and optionally generate CREATE DDL on the right side).
/// Replaces 4-5 separate Worker invocations the orchestrator would otherwise
/// make.
/// </summary>
public sealed class CcPipelineRunner
{
    private const string WorkerExeName = "erwin-alter-ddl-worker.exe";
    private const string WorkerPathEnvVar = "ERWIN_ALTER_DDL_WORKER";

    private readonly string _workerPath;
    private readonly TimeSpan _timeout;
    private readonly ILogger<CcPipelineRunner> _logger;

    public CcPipelineRunner(
        string? workerPath = null,
        TimeSpan? timeout = null,
        ILogger<CcPipelineRunner>? logger = null)
    {
        _workerPath = workerPath
            ?? Environment.GetEnvironmentVariable(WorkerPathEnvVar)
            ?? ProbeDefaultWorkerPath()
            ?? throw new FileNotFoundException(
                $"could not locate {WorkerExeName}. set {WorkerPathEnvVar} or pass an explicit path.");
        if (!File.Exists(_workerPath))
            throw new FileNotFoundException("worker exe missing", _workerPath);
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _logger = logger ?? NullLogger<CcPipelineRunner>.Instance;
    }

    /// <summary>
    /// Run the full pipeline. The Worker's own JSON output names the four
    /// artifact paths; this method eagerly loads the model-map JSON into
    /// <see cref="ErwinModelMap"/> objects (so the temp files can be deleted
    /// later) and packages everything in a <see cref="CcPipelineResult"/>.
    /// </summary>
    public async Task<CcPipelineResult> RunAsync(
        string leftLocator,
        string rightLocator,
        bool generateLeftDdl,
        bool generateRightDdl,
        string preset = "Standard",
        string level = "LP",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftLocator);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightLocator);

        string xlsOut = TempFile("erwin-diff", ".xls");
        string leftMapOut = TempFile("erwin-model-map-left", ".json");
        string rightMapOut = TempFile("erwin-model-map-right", ".json");
        string? rightDdlOut = generateRightDdl ? TempFile("erwin-ddl-right", ".sql") : null;
        string? leftDdlOut = generateLeftDdl ? TempFile("erwin-ddl-left", ".sql") : null;

        var args = new List<string>
        {
            "cc-pipeline",
            "--left", leftLocator,
            "--right", rightLocator,
            "--xls-out", xlsOut,
            "--left-map-out", leftMapOut,
            "--right-map-out", rightMapOut,
            "--preset", preset,
            "--level", level,
        };
        if (rightDdlOut is not null) { args.Add("--right-ddl-out"); args.Add(rightDdlOut); }
        if (leftDdlOut is not null) { args.Add("--left-ddl-out"); args.Add(leftDdlOut); }

        var json = await RunWorkerAsync(args.ToArray(), ct).ConfigureAwait(false);

        WorkerOutput? wo;
        try
        {
            wo = JsonSerializer.Deserialize<WorkerOutput>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException jex)
        {
            throw new InvalidOperationException(
                $"cc-pipeline output was not valid JSON: {jex.Message}\nstdout was: {json}", jex);
        }
        if (wo is null) throw new InvalidOperationException("cc-pipeline returned null JSON");
        if (string.IsNullOrEmpty(wo.xlsPath) || !File.Exists(wo.xlsPath))
            throw new InvalidOperationException($"cc-pipeline xls missing: {wo.xlsPath}");
        if (string.IsNullOrEmpty(wo.leftMapPath) || !File.Exists(wo.leftMapPath))
            throw new InvalidOperationException($"cc-pipeline left map missing: {wo.leftMapPath}");
        if (string.IsNullOrEmpty(wo.rightMapPath) || !File.Exists(wo.rightMapPath))
            throw new InvalidOperationException($"cc-pipeline right map missing: {wo.rightMapPath}");

        // Eagerly load the model maps so the temp JSON files can be cleaned
        // up immediately. The maps live in memory for the rest of the
        // compare; the XLS / DDL files are kept on disk so emitters /
        // callers can re-read them.
        ErwinModelMap leftMap = ModelMapJsonSerializer.DeserializeFile(wo.leftMapPath);
        ErwinModelMap rightMap = ModelMapJsonSerializer.DeserializeFile(wo.rightMapPath);
        try { File.Delete(wo.leftMapPath); } catch { /* best effort */ }
        try { File.Delete(wo.rightMapPath); } catch { /* best effort */ }

        var xlsArtifact = new CompareArtifact(wo.xlsPath, wo.xlsBytes, 0);
        DdlArtifact? rightDdl = null;
        if (!string.IsNullOrEmpty(wo.rightDdlPath) && File.Exists(wo.rightDdlPath!))
            rightDdl = new DdlArtifact(wo.rightDdlPath!, wo.rightDdlBytes,
                wo.rightTargetServer ?? string.Empty);
        DdlArtifact? leftDdl = null;
        if (!string.IsNullOrEmpty(wo.leftDdlPath) && File.Exists(wo.leftDdlPath!))
            leftDdl = new DdlArtifact(wo.leftDdlPath!, wo.leftDdlBytes,
                wo.leftTargetServer ?? string.Empty);

        return new CcPipelineResult(
            LeftLocator: leftLocator,
            RightLocator: rightLocator,
            Xls: xlsArtifact,
            LeftMap: leftMap,
            RightMap: rightMap,
            LeftDdl: leftDdl,
            RightDdl: rightDdl);
    }

    private async Task<string> RunWorkerAsync(string[] args, CancellationToken ct)
    {
        // Same pre-flight kill that OutOfProcessScapiSession does: any
        // erwin.exe left over from an earlier worker poisons the SCAPI
        // singleton in the new one.
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

        _logger.LogInformation("CcPipelineRunner: {Path} {Args}", _workerPath, string.Join(' ', args));

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        _logger.LogDebug("cc-pipeline exit={Code} stdout.len={Out} stderr.len={Err}",
            process.ExitCode, stdout.Length, stderr.Length);
        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogInformation("cc-pipeline stderr: {Stderr}", stderr.TrimEnd());

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"cc-pipeline worker failed with exit code {process.ExitCode}. stderr: {stderr.TrimEnd()}");
        }
        return stdout;
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

    private static string TempFile(string prefix, string suffix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}{suffix}");

    private static string? ProbeDefaultWorkerPath()
    {
        // Mirrors WorkerJsonModelMapProvider / OutOfProcessScapiSession: try
        // the assembly's own folder first (add-in scenario where erwin.exe
        // is the host process), then AppContext.BaseDirectory, then walk-up.
        var anchors = new List<string>();
        try
        {
            var asmLoc = typeof(CcPipelineRunner).Assembly.Location;
            if (!string.IsNullOrEmpty(asmLoc))
                anchors.Add(Path.GetDirectoryName(asmLoc) ?? string.Empty);
        }
        catch { }
        anchors.Add(AppContext.BaseDirectory);

        foreach (var anchor in anchors)
        {
            if (string.IsNullOrEmpty(anchor)) continue;
            foreach (var candidate in new[]
            {
                Path.Combine(anchor, WorkerExeName),
                Path.Combine(anchor, "Worker", WorkerExeName),
                Path.Combine(anchor, "..", "ErwinAlterDdl.Worker", WorkerExeName),
            })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                string full;
                try { full = Path.GetFullPath(candidate); }
                catch { continue; }
                if (File.Exists(full)) return full;
            }

            var probeRoot = anchor;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(probeRoot); i++)
            {
                foreach (var sub in new[]
                {
                    Path.Combine(probeRoot, "src", "ErwinAlterDdl.Worker", "bin", "Debug", "net10.0-windows", WorkerExeName),
                    Path.Combine(probeRoot, "src", "ErwinAlterDdl.Worker", "bin", "Release", "net10.0-windows", WorkerExeName),
                    Path.Combine(probeRoot, "ErwinAlterDdl", "src", "ErwinAlterDdl.Worker", "bin", "Debug", "net10.0-windows", WorkerExeName),
                    Path.Combine(probeRoot, "ErwinAlterDdl", "src", "ErwinAlterDdl.Worker", "bin", "Release", "net10.0-windows", WorkerExeName),
                })
                {
                    if (File.Exists(sub)) return sub;
                }
                try { probeRoot = Directory.GetParent(probeRoot)?.FullName ?? string.Empty; }
                catch { break; }
            }
        }
        return null;
    }

    /// <summary>
    /// Wire-format mirror of the Worker's <c>cc-pipeline</c> stdout payload.
    /// Keep in sync with <c>RunCcPipeline</c>'s anonymous-object literal in
    /// <c>ErwinAlterDdl.Worker.Program.cs</c>.
    /// </summary>
    private sealed class WorkerOutput
    {
        public string xlsPath { get; set; } = string.Empty;
        public long xlsBytes { get; set; }
        public string leftMapPath { get; set; } = string.Empty;
        public string rightMapPath { get; set; } = string.Empty;
        public string? leftDdlPath { get; set; }
        public long leftDdlBytes { get; set; }
        public string? leftTargetServer { get; set; }
        public string? rightDdlPath { get; set; }
        public long rightDdlBytes { get; set; }
        public string? rightTargetServer { get; set; }
    }
}
