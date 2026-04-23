using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using EliteSoft.Erwin.AlterDdl.Core.Emitting;
using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Phase 3.F (in-process pivot disabled): bridges the add-in's live SCAPI
    /// handle + active PU to the ErwinAlterDdl pipeline metadata only. The
    /// end-to-end compare flow is currently intentionally inert - see
    /// <see cref="CompareAsync"/> - because the naive save-to-temp / open-mart
    /// dance collides with two hard SCAPI r10 constraints (see below).
    /// The pure-logic helpers (<see cref="ResolveDialect"/>,
    /// <see cref="PlanTargetVersions"/>, <see cref="ProbeDirty"/>) are still
    /// used by the UI and are fully unit-tested.
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
        public Task<CompareOutcome> CompareAsync(int targetVersion, CancellationToken ct = default)
        {
            // Phase 3.F in-process pivot: the naive "save both PUs to temp
            // + CompleteCompare" flow is blocked by two hard SCAPI r10
            // constraints documented in DdlGenerationService.cs:
            //   1. "Mart API blocks opening a second PU" (so target version
            //      can't live next to the active PU in the same session).
            //   2. pu.Save(path) intentionally corrupts a Mart-backed PU
            //      (the add-in's own code relies on this to force reconnect).
            // A user-triggered compare would therefore destroy the active
            // model. We refuse rather than risk lost work and ask the user
            // to wait for the out-of-process Worker pivot.
            _ = targetVersion;
            _ = ct;
            throw new NotSupportedException(
                "Active-vs-Mart compare through the in-process SCAPI pipeline is disabled: "
                + "pu.Save(...) invalidates the active Mart PU on r10.10 and Mart API "
                + "blocks opening a second PU in the same session. The follow-up design "
                + "moves this flow to an out-of-process Worker that leaves the active "
                + "add-in session untouched. Tracked as the 3.F 'real solution' pivot.");
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
