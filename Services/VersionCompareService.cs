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
        /// Build the target-version combo contents. Clean models list only
        /// prior versions (current vs its own saved copy is a no-op); dirty
        /// models additionally include the current version so the user can
        /// diff the dirty buffer against its saved Mart counterpart.
        /// </summary>
        public static IReadOnlyList<TargetVersion> PlanTargetVersions(int currentVersion, bool isDirty)
        {
            if (currentVersion < 1) return Array.Empty<TargetVersion>();
            int max = isDirty ? currentVersion : currentVersion - 1;
            var list = new List<TargetVersion>();
            for (int v = max; v >= 1; v--)
            {
                string label = $"v{v}" +
                    (isDirty && v == currentVersion ? " (current saved copy)" : "");
                list.Add(new TargetVersion(v, label));
            }
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
                string locator = "";
                try { locator = _activePU.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }
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
        /// End-to-end compare against a specific Mart version of the same model.
        /// Produces a <see cref="CompareOutcome"/> that the UI can render.
        /// Structural changes only (see class summary).
        /// </summary>
        public async Task<CompareOutcome> CompareAsync(int targetVersion, CancellationToken ct = default)
        {
            if (targetVersion <= 0) throw new ArgumentOutOfRangeException(nameof(targetVersion));

            string martLocator = BuildMartLocatorForTarget(targetVersion);
            string activePathKey = $"active-pu://{SafeGet(_activePU.PropertyBag(null, true), "Persistence_Unit_Id")}";

            _log($"VersionCompare: baseline=live active PU (v{ReadActiveVersion()}), target=Mart v{targetVersion}");
            _log($"VersionCompare: target locator = {MaskMartPassword(martLocator)}");

            // Compose the per-side providers into a single IModelMapProvider
            // the orchestrator can consume.
            var liveProvider = new LiveSessionModelMapProvider((object)_scapi, (object)_activePU, activePathKey);
            var workerProvider = new WorkerJsonModelMapProvider();
            var combined = new DispatchByPathModelMapProvider(activePathKey, liveProvider, martLocator, workerProvider);

            // Null SCAPI session is safe because SkipCompleteCompare=true.
            // Any accidental call to session methods would blow up loudly.
            var session = new NoCompleteCompareSession();

            var options = new CompareOptions
            {
                SkipCompleteCompare = true,
                IncludeCreateDdl = false,
            };

            var (target, major, minor) = ReadActiveTargetServer();
            string dialect = ResolveDialect(target);

            var orchestrator = new CompareOrchestrator(session, combined);
            var result = await orchestrator.CompareAsync(activePathKey, martLocator, options, ct).ConfigureAwait(false);

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
        /// Derive the Mart locator for a specific version of the same model the
        /// active PU points at. Preserves the existing Mart connection
        /// parameters (server / port / credentials) and only swaps VNO.
        /// </summary>
        private string BuildMartLocatorForTarget(int targetVersion)
        {
            string active = "";
            try { active = _activePU.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }
            if (!active.StartsWith("mart://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("active PU is not Mart-hosted; version compare is only meaningful for Mart models.");

            // Extract the "mart://Mart/<path>" prefix + the query params.
            var pathMatch = Regex.Match(active, @"^(?<base>mart://[^?]+)\??(?<q>.*)$", RegexOptions.IgnoreCase);
            if (!pathMatch.Success)
                throw new InvalidOperationException($"cannot parse Mart locator from active PU: '{active}'");
            string basePart = pathMatch.Groups["base"].Value;
            string query = pathMatch.Groups["q"].Value;

            var kv = new List<KeyValuePair<string, string>>();
            foreach (var chunk in query.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kvPair = chunk.Split('=', 2);
                if (kvPair.Length == 2) kv.Add(new(kvPair[0], kvPair[1]));
            }

            bool replacedVno = false;
            for (int i = 0; i < kv.Count; i++)
            {
                if (string.Equals(kv[i].Key, "VNO", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv[i].Key, "version", StringComparison.OrdinalIgnoreCase))
                {
                    kv[i] = new(kv[i].Key, targetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    replacedVno = true;
                }
            }
            if (!replacedVno) kv.Add(new("VNO", targetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            var qs = string.Join(';', kv.ConvertAll(p => $"{p.Key}={p.Value}"));
            return qs.Length == 0 ? basePart : $"{basePart}?{qs}";
        }

        private static string MaskMartPassword(string locator) =>
            Regex.Replace(locator, @"PSW=[^;]*", "PSW=***", RegexOptions.IgnoreCase);

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
