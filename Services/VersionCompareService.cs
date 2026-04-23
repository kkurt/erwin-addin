using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using EliteSoft.Erwin.AlterDdl.ComInterop;
using EliteSoft.Erwin.AlterDdl.Core.Emitting;
using EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Pipeline;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Phase 3.F: bridges the add-in's live SCAPI handle + active PU to the
    /// ErwinAlterDdl pipeline. The baseline is always the currently-active model
    /// (including its dirty buffer); the target is a selected Mart version of
    /// the same model family.
    ///
    /// The service does NOT generate CREATE DDL for either side (InProcess
    /// sessions can't on r10.10 because of singleton state pollution). As a
    /// result the emitted ALTER SQL will contain TODO placeholders for new
    /// column datatypes and constraint / index column lists. A follow-up phase
    /// can add out-of-process DDL lookup if we need those filled.
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
        ///
        /// Pure function - no SCAPI involvement. Extracted from the form to
        /// stay unit-testable.
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

        /// <summary>
        /// Best-effort read of a PU's dirty state. erwin exposes one of several
        /// flag names depending on r10 servicing level; we probe in order and
        /// return the first one that answers.
        /// </summary>
        public DirtyProbe ProbeDirty()
        {
            // erwin r10 exposes the dirty flag as a COM property on the PU
            // itself, but the available name varies across servicing levels.
            // We probe by reflection rather than `_activePU.Modified`-style
            // dynamic dispatch so missing members don't break the lookup.
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
            // Treat unknown as dirty so the target combo still surfaces every
            // Mart version (including the current one) for comparison. A false
            // negative here would hide a valid target, which is worse than a
            // spurious combo entry.
            return new DirtyProbe(true, "(unknown)");
        }

        /// <summary>
        /// Parse the active PU's locator to recover its current Mart version
        /// number. Returns 1 for file-based (non-Mart) models or on parse
        /// failure.
        /// </summary>
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

        /// <summary>
        /// Read the Target_Server / version metadata from the active PU so the
        /// UI can pick the right emitter (and display a read-only dialect label).
        /// </summary>
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

        /// <summary>
        /// Map an erwin Target_Server name (e.g. "SQL Server") to the
        /// <see cref="ISqlEmitter.Dialect"/> value used by the registry.
        /// </summary>
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
        /// End-to-end compare: dump the active PU (with its dirty buffer) to a
        /// temp .erwin, open the selected Mart version, dump it too, run the
        /// Core pipeline, and emit the ALTER SQL for the active model's target
        /// server.
        /// </summary>
        public async Task<CompareOutcome> CompareAsync(int targetVersion, CancellationToken ct = default)
        {
            if (targetVersion <= 0) throw new ArgumentOutOfRangeException(nameof(targetVersion));

            string tempDir = Path.Combine(Path.GetTempPath(), $"alter-ddl-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string leftErwin = Path.Combine(tempDir, "baseline.erwin");
            string leftXml = Path.ChangeExtension(leftErwin, ".xml");
            string rightErwin = Path.Combine(tempDir, "target.erwin");
            string rightXml = Path.ChangeExtension(rightErwin, ".xml");
            string xlsPath = Path.Combine(tempDir, "diff.xls");

            dynamic targetPU = null;
            string dialect = ResolveDialect(ReadActiveTargetServer().TargetServer);
            try
            {
                _log($"VersionCompare: dumping baseline (active PU) to {leftErwin}");
                SavePuToBothFormats(_activePU, leftErwin, leftXml);

                _log($"VersionCompare: opening Mart target v{targetVersion}");
                targetPU = DdlGenerationService.OpenMartVersionPU(_scapi, _activePU, targetVersion, _log);
                if (targetPU is null)
                    throw new InvalidOperationException($"Could not open Mart version v{targetVersion}.");

                _log($"VersionCompare: dumping target (v{targetVersion}) to {rightErwin}");
                SavePuToBothFormats(targetPU, rightErwin, rightXml);

                var session = new InProcessScapiSession((object)_scapi);
                await using (session.ConfigureAwait(false))
                {
                    var options = new CompareOptions
                    {
                        PresetOrOptionXmlPath = "Standard",
                        Level = CompareLevel.PhysicalOnly,
                        OutputXlsPath = xlsPath,
                        IncludeCreateDdl = false,
                    };

                    var orchestrator = new CompareOrchestrator(session);
                    var result = await orchestrator.CompareAsync(leftErwin, rightErwin, options, ct)
                        .ConfigureAwait(false);

                    var registry = new SqlEmitterRegistry()
                        .Register(new MssqlEmitter(), "SQL Server")
                        .Register(new OracleEmitter(), "Oracle")
                        .Register(new Db2Emitter(), "Db2", "DB2 z/OS");
                    var emitter = registry.Resolve(dialect);
                    var script = emitter.Emit(result);

                    _log($"VersionCompare: {result.Changes.Count} change(s), {script.Statements.Count} statement(s), dialect={dialect}");
                    return new CompareOutcome(result, script, dialect);
                }
            }
            finally
            {
                if (targetPU is not null)
                {
                    try { _scapi.PersistenceUnits.Remove(targetPU); } catch { }
                    try { Marshal.FinalReleaseComObject(targetPU); } catch { }
                }
                TryCleanupTempDir(tempDir);
            }
        }

        /// <summary>
        /// erwin r10 supports saving a PU to either the binary .erwin format or
        /// the XML interchange format by switching file extensions. Both are
        /// produced side-by-side so the Core pipeline can read the XML for
        /// ObjectId correlation and hand the .erwin to CompleteCompare.
        /// </summary>
        private void SavePuToBothFormats(dynamic pu, string erwinPath, string xmlPath)
        {
            try { pu.Save(erwinPath); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Save({erwinPath}) failed: {ex.Message}", ex);
            }
            try { pu.Save(xmlPath); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Save({xmlPath}) failed (erwin may not support the .xml extension on this PU type): {ex.Message}", ex);
            }
        }

        private void TryCleanupTempDir(string tempDir)
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _log($"VersionCompare: cleanup {tempDir} failed: {ex.Message}"); }
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
}
