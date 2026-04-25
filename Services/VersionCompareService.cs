using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using EliteSoft.Erwin.AlterDdl.ComInterop;
using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Emitting;
using EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;
using EliteSoft.Erwin.AlterDdl.Core.Pipeline;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Phase 3.F wiring: bridges the add-in's live SCAPI handle + active PU to
    /// the ErwinAlterDdl pipeline.
    ///
    /// Baseline = active PU (walked in-process via
    /// <see cref="LiveSessionModelMapProvider"/>, dirty buffer preserved, no
    /// disk save - safe for Mart-backed PUs).
    /// Target   = selected Mart version (opened in a fresh out-of-process
    /// worker via <see cref="WorkerJsonModelMapProvider"/> with the
    /// <c>mart://</c> locator inferred from the active PU).
    ///
    /// CompleteCompare is intentionally skipped (<c>SkipCompleteCompare</c>)
    /// because we cannot safely dump the active Mart PU to disk. That means
    /// property-level changes (type / nullable / default / identity) are NOT
    /// emitted yet - a follow-up phase enriches the maps with property data
    /// so we can diff them directly. Structural changes (ADD / DROP / RENAME
    /// for entities, attributes, key groups, relationships, views, triggers,
    /// sequences) are fully supported today.
    /// </summary>
    public sealed class VersionCompareService
    {
        /// <summary>Dirty-state probe result reused by the UI for labelling.</summary>
        public readonly record struct DirtyProbe(bool IsDirty, string Source);

        /// <summary>Single row in the target-version combo plan.</summary>
        public readonly record struct TargetVersion(int Version, string Label);

        /// <summary>
        /// Build the target-version combo contents. Lists every prior Mart
        /// version from <c>currentVersion-1</c> down to <c>1</c>. Phase 3.G
        /// pipeline runs CompleteCompare in an out-of-process worker against
        /// two Mart locators (vCurrent vs vN), so the dirty buffer is not
        /// captured - the UI surfaces a "Dirty" warning separately.
        /// </summary>
        public static IReadOnlyList<TargetVersion> PlanTargetVersions(int currentVersion, bool isDirty)
        {
            if (currentVersion < 2) return Array.Empty<TargetVersion>();
            var list = new List<TargetVersion>();
            for (int v = currentVersion - 1; v >= 1; v--)
                list.Add(new TargetVersion(v, $"v{v}"));
            return list;
        }

        private readonly dynamic _scapi;
        private readonly dynamic _activePU;
        private readonly Action<string> _log;

        public VersionCompareService(dynamic scapi, dynamic activePU, Action<string> log)
        {
            _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
            _activePU = activePU ?? throw new ArgumentNullException(nameof(activePU));
            _log = log ?? (_ => { });
        }

        public DirtyProbe ProbeDirty()
        {
            foreach (var prop in new[] { "Modified", "IsModified", "IsDirty", "Dirty", "HasChanges" })
            {
                try
                {
                    object target = (object)_activePU;
                    var val = target.GetType().InvokeMember(
                        prop,
                        System.Reflection.BindingFlags.GetProperty,
                        binder: null,
                        target: target,
                        args: null);
                    if (val != null && bool.TryParse(val.ToString(), out var b))
                        return new DirtyProbe(b, prop);
                }
                catch { /* keep probing */ }
            }
            return new DirtyProbe(true, "(unknown)");
        }

        public int ReadActiveVersion()
        {
            try
            {
                var locator = ReadActiveLocator();
                return DdlGenerationService.ParseVersionFromLocator(locator);
            }
            catch (Exception ex)
            {
                _log($"VersionCompare: ReadActiveVersion failed: {ex.Message}");
                return 1;
            }
        }

        public (string TargetServer, int Major, int Minor) ReadActiveTargetServer()
        {
            try
            {
                dynamic bag = _activePU.PropertyBag(null, true);
                string target = SafeGet(bag, "Target_Server");
                int major = ParseInt(SafeGet(bag, "Target_Server_Version"));
                int minor = ParseInt(SafeGet(bag, "Target_Server_Minor_Version"));
                return (target, major, minor);
            }
            catch (Exception ex)
            {
                _log($"VersionCompare: ReadActiveTargetServer failed: {ex.Message}");
                return (string.Empty, 0, 0);
            }
        }

        public static string ResolveDialect(string targetServer)
        {
            if (string.IsNullOrWhiteSpace(targetServer)) return "MSSQL";
            var s = targetServer.Trim().ToUpperInvariant();
            if (s.Contains("SQL SERVER") || s.Contains("MSSQL") || s.Contains("AZURE")) return "MSSQL";
            if (s.Contains("ORACLE")) return "Oracle";
            if (s.Contains("DB2") || s.Contains("Z/OS")) return "Db2";
            return "MSSQL";
        }

        /// <summary>
        /// End-to-end compare of the active Mart-backed model against an
        /// earlier Mart version of itself. The pipeline is the same one the
        /// CLI / sub-module uses end-to-end: out-of-process Worker for both
        /// sides, real CompleteCompare via SCAPI (so the XLS report drives
        /// schema-aware emit and CC-allowlist filtering), CREATE DDL for
        /// each side for column types and verbatim CREATE TABLE bodies.
        ///
        /// The active PU is opened by the worker via its Mart locator
        /// (vCurrent), which captures only the saved state. If the live PU
        /// is dirty, the unsaved buffer is NOT in the diff - the caller is
        /// responsible for warning the user before they save and lose
        /// recoverability.
        /// </summary>
        public async Task<CompareOutcome> CompareAsync(int targetVersion, CancellationToken ct = default)
        {
            if (targetVersion <= 0) throw new ArgumentOutOfRangeException(nameof(targetVersion));

            int currentVersion = ReadActiveVersion();
            if (currentVersion <= targetVersion)
                throw new InvalidOperationException(
                    $"target Mart version (v{targetVersion}) must be older than the active version (v{currentVersion}).");

            var dirty = ProbeDirty();
            if (dirty.IsDirty)
                _log($"VersionCompare: WARNING active PU has unsaved changes ({dirty.Source}). " +
                    $"Compare runs against the last-saved Mart v{currentVersion}; in-memory edits are NOT captured.");

            string leftMart = BuildMartLocatorForTarget(targetVersion);
            string rightMart = BuildMartLocatorForTarget(currentVersion);
            _log($"VersionCompare: from=Mart v{targetVersion} (left), to=Mart v{currentVersion} (right, saved state)");
            _log($"VersionCompare: left  locator = {MaskMartPassword(leftMart)}");
            _log($"VersionCompare: right locator = {MaskMartPassword(rightMart)}");

            WorkerJsonModelMapProvider workerProvider;
            OutOfProcessScapiSession scapiSession;
            try
            {
                workerProvider = new WorkerJsonModelMapProvider();
                scapiSession = new OutOfProcessScapiSession();
                _log("VersionCompare: out-of-process worker session ready");
            }
            catch (Exception ex)
            {
                _log($"VersionCompare: worker init failed: {ex.GetType().FullName}: {ex.Message}");
                throw new InvalidOperationException(
                    "Could not locate erwin-alter-ddl-worker.exe. Set the ERWIN_ALTER_DDL_WORKER environment " +
                    "variable to its full path, or copy the worker EXE next to the add-in DLL.", ex);
            }

            var options = new CompareOptions
            {
                SkipCompleteCompare = false,    // Run real CC for XLS-driven schema + filter
                IncludeCreateDdl = true,        // CREATE DDL feeds column types + verbatim CREATE TABLE
                IncludeLeftCreateDdl = false,   // emitter only consumes RIGHT DDL; saves a Worker call
                SkipMetadataRead = true,        // we already have active PU metadata locally
            };

            var (target, major, minor) = ReadActiveTargetServer();
            string dialect = ResolveDialect(target);

            CompareResult result;
            await using (scapiSession)
            {
                var orchestrator = new CompareOrchestrator(scapiSession, workerProvider);
                result = await orchestrator
                    .CompareAsync(leftMart, rightMart, options, ct)
                    .ConfigureAwait(false);
            }

            var registry = new SqlEmitterRegistry()
                .Register(new MssqlEmitter(), "SQL Server")
                .Register(new OracleEmitter(), "Oracle")
                .Register(new Db2Emitter(), "Db2", "DB2 z/OS");
            var emitter = registry.Resolve(dialect);
            var script = emitter.Emit(result);

            _log($"VersionCompare: {result.Changes.Count} change(s), {script.Statements.Count} statement(s), dialect={dialect} ({target} v{major}.{minor})");
            return new CompareOutcome(result, script, dialect);
        }

        /// <summary>
        /// Build CREATE DDL artifacts for both sides of the compare:
        ///   left  = Mart vN target (out-of-process worker)
        ///   right = active PU (in-process FEModel_DDL on the live handle;
        ///           safe per existing DdlGenerationService pattern)
        /// </summary>
        private async Task<(DdlArtifact Left, DdlArtifact Right)> BuildCreateDdlArtifactsAsync(
            string martLocator,
            CancellationToken ct)
        {
            DdlArtifact leftDdl = null;
            DdlArtifact rightDdl = null;

            // Active PU side - in-process FEModel_DDL is safe (does not
            // corrupt the PU; the Mart "DDL Generation" tab uses the same
            // call on every run).
            try
            {
                string leftSql = Path.Combine(Path.GetTempPath(), $"erwin-active-ddl-{Guid.NewGuid():N}.sql");
                bool ok = false;
                try { ok = (bool)_activePU.FEModel_DDL(leftSql, ""); }
                catch (Exception ex) { _log($"VersionCompare: FEModel_DDL on active PU threw: {ex.Message}"); }
                if (ok && File.Exists(leftSql) && new FileInfo(leftSql).Length > 0)
                {
                    string targetServer = ReadActiveTargetServer().TargetServer;
                    rightDdl = new DdlArtifact(leftSql, new FileInfo(leftSql).Length, targetServer);
                    _log($"VersionCompare: active PU CREATE DDL = {rightDdl.SizeBytes} bytes");
                }
                else
                {
                    _log("VersionCompare: active PU FEModel_DDL did not produce output");
                }
            }
            catch (Exception ex)
            {
                _log($"VersionCompare: active PU CREATE DDL build failed: {ex.GetType().Name}: {ex.Message}");
            }

            // Mart vN side - delegated to a fresh worker process (own SCAPI
            // instance, no interference with the user's add-in session).
            try
            {
                string martSql = Path.Combine(Path.GetTempPath(), $"erwin-mart-ddl-{Guid.NewGuid():N}.sql");
                var workerExe = LocateWorkerExe();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = workerExe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("ddl");
                psi.ArgumentList.Add("--erwin"); psi.ArgumentList.Add(martLocator);
                psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(martSql);
                psi.ArgumentList.Add("--disposition"); psi.ArgumentList.Add("OVM=Yes");

                _log($"VersionCompare: Worker ddl: {workerExe} ddl --erwin <mart> --out {martSql}");
                using var proc = System.Diagnostics.Process.Start(psi)
                    ?? throw new InvalidOperationException("Process.Start returned null");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromMinutes(5));
                var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);
                await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                if (proc.ExitCode == 0 && File.Exists(martSql) && new FileInfo(martSql).Length > 0)
                {
                    leftDdl = new DdlArtifact(martSql, new FileInfo(martSql).Length, "(worker)");
                    _log($"VersionCompare: Mart CREATE DDL = {leftDdl.SizeBytes} bytes");
                }
                else
                {
                    _log($"VersionCompare: Mart Worker ddl exit={proc.ExitCode} stderr={stderr.TrimEnd()}");
                }
            }
            catch (Exception ex)
            {
                _log($"VersionCompare: Mart CREATE DDL build failed: {ex.GetType().Name}: {ex.Message}");
            }

            return (leftDdl, rightDdl);
        }

        private static string LocateWorkerExe()
        {
            const string exeName = "erwin-alter-ddl-worker.exe";
            var env = Environment.GetEnvironmentVariable("ERWIN_ALTER_DDL_WORKER");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var asmDir = Path.GetDirectoryName(typeof(VersionCompareService).Assembly.Location) ?? "";
            foreach (var c in new[]
            {
                Path.Combine(asmDir, "Worker", exeName),
                Path.Combine(asmDir, exeName),
            })
            {
                if (File.Exists(c)) return c;
            }
            throw new FileNotFoundException("could not locate erwin-alter-ddl-worker.exe", exeName);
        }

        /// <summary>
        /// Derive the Mart locator for a specific version of the same model the
        /// active PU points at. Preserves the existing Mart connection
        /// parameters (server / port / credentials) and only swaps VNO.
        /// </summary>
        /// <summary>
        /// Derive a Worker-usable Mart locator for the requested target
        /// version. Active PU locators come in several shapes (observed in
        /// the wild):
        ///   "Mart://Mart/&lt;lib&gt;/&lt;model&gt;?...VNO=N..."
        ///   "erwin://Mart://Mart/&lt;lib&gt;/&lt;model&gt;?&amp;version=N&amp;modelLongId=..."
        /// We extract just the model path and re-emit a fresh, full Mart URL
        /// with the connection credentials read from the local CONNECTION_DEF
        /// row (mirrors <c>OpenMartVersionPU</c>'s "full + RDO" attempt). The
        /// fresh erwin.exe Worker process has no Mart session of its own, so
        /// embedded credentials are mandatory.
        /// </summary>
        private string BuildMartLocatorForTarget(int targetVersion)
        {
            string active = ReadActiveLocator();
            _log($"VersionCompare: active PU locator = '{MaskMartPassword(active)}' (length={active.Length})");
            if (string.IsNullOrEmpty(active))
                throw new InvalidOperationException(
                    "active PU locator could not be read (PropertyBag returned empty). " +
                    "Open the model from Mart and try again.");

            var pathMatch = Regex.Match(active, @"Mart://Mart/(?<path>[^?]+?)(?:[?&]|$)", RegexOptions.IgnoreCase);
            if (!pathMatch.Success)
                throw new InvalidOperationException(
                    $"could not extract a Mart model path from locator='{MaskMartPassword(active)}'. " +
                    "Version compare is only meaningful for Mart models.");
            string modelPath = pathMatch.Groups["path"].Value.Trim('/');
            _log($"VersionCompare: extracted Mart model path = '{modelPath}'");

            var info = DdlGenerationService.GetMartConnectionInfo(s => _log(s));
            if (info is null)
            {
                _log("VersionCompare: Mart credentials unavailable (CONNECTION_DEF lookup failed); short-form may not authenticate from a fresh worker.");
                return $"mart://Mart/{modelPath}?VNO={targetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            string ver = targetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"mart://Mart/{modelPath}?TRC=NO;SRV={info.Value.host};PRT={info.Value.port};ASR=MartServer;UID={info.Value.username};PSW={info.Value.password};VNO={ver}";
        }

        private static string MaskMartPassword(string locator) =>
            Regex.Replace(locator, @"PSW=[^;]*", "PSW=***", RegexOptions.IgnoreCase);

        /// <summary>
        /// Read the active PU's <c>Locator</c> property bag value with two
        /// fallbacks: the no-arg <c>PropertyBag()</c> overload (matches the
        /// existing add-in's pattern) and the <c>PropertyBag(null, true)</c>
        /// overload (returns the bag with derived strings). Logs the failures
        /// instead of swallowing them silently.
        /// </summary>
        private string ReadActiveLocator()
        {
            string value = "";
            try
            {
                value = (string)(_activePU.PropertyBag().Value("Locator") ?? string.Empty);
            }
            catch (Exception ex)
            {
                _log($"VersionCompare: PropertyBag().Value(Locator) threw: {ex.GetType().Name}: {ex.Message}");
            }
            if (!string.IsNullOrEmpty(value)) return value;

            try
            {
                value = (string)(_activePU.PropertyBag(null, true).Value("Locator") ?? string.Empty);
            }
            catch (Exception ex)
            {
                _log($"VersionCompare: PropertyBag(null,true).Value(Locator) threw: {ex.GetType().Name}: {ex.Message}");
            }
            if (!string.IsNullOrEmpty(value)) return value;

            // Last-resort: read erwin's main window title. The existing
            // ModelConfigForm parser already mines the version from this
            // string ("erwin DM - [Mart://.../<model> : vN : ...]"), so we
            // mirror that approach for the full Mart locator stem.
            value = ReadLocatorFromWindowTitle();
            if (!string.IsNullOrEmpty(value))
                _log($"VersionCompare: locator recovered from window title");
            return value ?? string.Empty;
        }

        private static string ReadLocatorFromWindowTitle()
        {
            try
            {
                var hWnd = Win32Helper.GetErwinMainWindow();
                if (hWnd == IntPtr.Zero) return string.Empty;
                var sb = new System.Text.StringBuilder(1024);
                Win32Helper.GetWindowTextPublic(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                // Patterns observed:
                //   "erwin DM - [Mart://Mart/<lib>/<model> : vN : <diagram> [* ]]"
                // Extract the bracketed locator stem; we still need a version
                // suffix on the consumer end via ParseVersionFromLocator.
                var m = Regex.Match(title, @"\[(?<base>(?:[Mm]art://)[^\s\]]+)(?:\s*:\s*v(?<v>\d+))?", RegexOptions.IgnoreCase);
                if (!m.Success) return string.Empty;
                var basePart = m.Groups["base"].Value;
                var ver = m.Groups["v"].Value;
                return string.IsNullOrEmpty(ver) ? basePart : $"{basePart}?VNO={ver}";
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Tolerant detection of a Mart-hosted locator. Accepts
        /// <c>mart://</c> in any casing AND the (rare) plain
        /// <c>Mart:</c> form some servicing levels emit before the slashes.
        /// </summary>
        private static bool IsMartLocator(string locator)
        {
            if (string.IsNullOrEmpty(locator)) return false;
            return locator.StartsWith("mart://", StringComparison.OrdinalIgnoreCase)
                || locator.StartsWith("mart:", StringComparison.OrdinalIgnoreCase)
                || locator.IndexOf("mart://", StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static string SafeGet(dynamic bag, string key)
        {
            try { return (string)(bag.Value(key) ?? string.Empty); }
            catch { return string.Empty; }
        }

        private static int ParseInt(string s) =>
            int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>UI-friendly bundle returned by <see cref="VersionCompareService.CompareAsync"/>.</summary>
    public sealed record CompareOutcome(
        CompareResult Result,
        AlterDdlScript Script,
        string Dialect);

    /// <summary>
    /// Multiplexer over two <see cref="IModelMapProvider"/>s keyed by the
    /// compare side's pathKey. Used to route the "baseline" request at the
    /// live provider and the "target" request at the Worker provider.
    /// </summary>
    internal sealed class DispatchByPathModelMapProvider : IModelMapProvider
    {
        private readonly string _leftPath;
        private readonly IModelMapProvider _left;
        private readonly string _rightPath;
        private readonly IModelMapProvider _right;

        public DispatchByPathModelMapProvider(string leftPath, IModelMapProvider left, string rightPath, IModelMapProvider right)
        {
            _leftPath = leftPath;
            _left = left;
            _rightPath = rightPath;
            _right = right;
        }

        public Task<ErwinModelMap> BuildMapAsync(string erwinPath, CancellationToken ct = default)
        {
            if (string.Equals(erwinPath, _leftPath, StringComparison.OrdinalIgnoreCase))
                return _left.BuildMapAsync(erwinPath, ct);
            if (string.Equals(erwinPath, _rightPath, StringComparison.OrdinalIgnoreCase))
                return _right.BuildMapAsync(erwinPath, ct);
            throw new InvalidOperationException(
                $"no provider registered for '{erwinPath}'. Expected '{_leftPath}' or '{_rightPath}'.");
        }
    }

    /// <summary>
    /// <see cref="IScapiSession"/> stub used by Phase 3.F structural-only
    /// compares. <see cref="CompareOrchestrator"/> still wants a session
    /// instance, but with <c>SkipCompleteCompare = true</c> none of its
    /// methods are invoked on the happy path. <see cref="ReadModelMetadataAsync"/>
    /// is provided because the orchestrator does read metadata; we return a
    /// minimal placeholder so the flow succeeds.
    /// </summary>
    internal sealed class NoCompleteCompareSession : IScapiSession
    {
        public Task<CompareArtifact> RunCompleteCompareAsync(
            string leftErwinPath, string rightErwinPath, CompareOptions options, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "structural-only compare path invoked CompleteCompare; check SkipCompleteCompare flag.");

        public Task<DdlArtifact> GenerateCreateDdlAsync(string erwinPath, DdlOptions options, CancellationToken ct = default)
            => throw new NotSupportedException("NoCompleteCompareSession does not implement FEModel_DDL");

        public Task<ModelMetadata> ReadModelMetadataAsync(string erwinPath, CancellationToken ct = default)
        {
            string name = erwinPath;
            int dot = erwinPath.LastIndexOf('/');
            if (dot >= 0 && dot + 1 < erwinPath.Length) name = erwinPath[(dot + 1)..];
            return Task.FromResult(new ModelMetadata(
                PersistenceUnitId: erwinPath,
                Name: name,
                ModelType: "Physical",
                TargetServer: string.Empty,
                TargetServerVersion: 0,
                TargetServerMinorVersion: 0));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
