#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Research probe that captures the exact behavior of
    /// <c>activePU.Save("temp.erwin", "OVF=Yes")</c> on a live Mart-backed
    /// PU. Used to learn whether SCAPI's Save call is destructive (relocates
    /// the live PU) or non-destructive (creates a copy). The API doc claims
    /// the former, but our previous "corruption" observation was on a .xml
    /// Save, not the .erwin form - so we have not actually verified the
    /// .erwin behavior on a live Mart PU.
    ///
    /// The probe takes no destructive action by itself - it only inspects
    /// PropertyBag values before and after Save and reports the diff. A
    /// follow-up call (FEModel_DDL on the active PU) is run as a benign
    /// post-condition check; if that succeeds the live PU is still healthy.
    /// </summary>
    public static class PuSaveProbe
    {
        /// <summary>One observation snapshot taken from the active PU.</summary>
        public sealed record Snapshot(
            string Locator,
            string Disposition,
            string PersistenceUnitId,
            string Name,
            string ModelType,
            string TargetServer,
            string ActiveModel,
            string HiddenModel)
        {
            public static Snapshot Empty => new(
                Locator: "(unread)",
                Disposition: "(unread)",
                PersistenceUnitId: "(unread)",
                Name: "(unread)",
                ModelType: "(unread)",
                TargetServer: "(unread)",
                ActiveModel: "(unread)",
                HiddenModel: "(unread)");
        }

        public sealed record ProbeResult(
            Snapshot Before,
            Snapshot After,
            bool SaveSucceeded,
            string? SaveError,
            long ProducedFileBytes,
            string TempFilePath,
            bool PostFeDdlOk,
            string? PostFeDdlError);

        /// <summary>
        /// Run the probe. Caller MUST display a confirmation dialog first.
        /// Logs every step via <paramref name="log"/>. Returns a structured
        /// result the caller can render.
        /// </summary>
        public static async Task<ProbeResult> ProbeAsync(
            object scapi, object activePU, Action<string> log)
        {
            ArgumentNullException.ThrowIfNull(scapi);
            ArgumentNullException.ThrowIfNull(activePU);
            log ??= _ => { };

            log("=== PuSaveProbe START ===");
            var before = ReadSnapshot(activePU, log, "BEFORE");

            string tempPath = Path.Combine(Path.GetTempPath(),
                $"pu-save-probe-{DateTime.Now:yyyyMMdd-HHmmss}.erwin");
            log($"PuSaveProbe: target temp file = {tempPath}");

            bool saveOk = false;
            string? saveErr = null;
            long fileBytes = 0;
            try
            {
                log("PuSaveProbe: calling activePU.Save(tempPath, \"OVF=Yes\") ...");
                dynamic pu = activePU;
                object? result = await Task.Run(() => (object?)pu.Save(tempPath, "OVF=Yes")).ConfigureAwait(false);
                if (result is bool b) saveOk = b;
                else if (result != null) saveOk = bool.TryParse(result.ToString(), out var bb) && bb;
                log($"PuSaveProbe: Save returned {result?.ToString() ?? "(null)"}");

                if (File.Exists(tempPath))
                {
                    fileBytes = new FileInfo(tempPath).Length;
                    log($"PuSaveProbe: temp file written, size={fileBytes:N0} bytes");
                }
                else
                {
                    log("PuSaveProbe: temp file NOT found after Save");
                }
            }
            catch (Exception ex)
            {
                saveErr = $"{ex.GetType().FullName}: {ex.Message}";
                log($"PuSaveProbe: Save THREW: {saveErr}");
                if (ex.InnerException is not null)
                    log($"PuSaveProbe:   inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            }

            var after = ReadSnapshot(activePU, log, "AFTER");
            LogDiff(before, after, log);

            // Post-condition: try a benign FEModel_DDL call on the active PU.
            // If it still works, the live PU's data is intact even if the
            // Locator changed. This is the strongest signal of "not corrupted".
            bool feOk = false;
            string? feErr = null;
            string ddlProbePath = Path.Combine(Path.GetTempPath(),
                $"pu-save-probe-feddl-{DateTime.Now:yyyyMMdd-HHmmss}.sql");
            try
            {
                log($"PuSaveProbe: post-check: calling activePU.FEModel_DDL(...) ...");
                dynamic pu = activePU;
                object? result = await Task.Run(() => (object?)pu.FEModel_DDL(ddlProbePath, "")).ConfigureAwait(false);
                if (result is bool fb) feOk = fb;
                else if (result != null) feOk = bool.TryParse(result.ToString(), out var fbb) && fbb;
                log($"PuSaveProbe: FE_DDL returned {result?.ToString() ?? "(null)"}, file exists={File.Exists(ddlProbePath)}");
                if (File.Exists(ddlProbePath))
                {
                    long sz = new FileInfo(ddlProbePath).Length;
                    log($"PuSaveProbe: FE_DDL file size = {sz:N0} bytes");
                    try { File.Delete(ddlProbePath); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                feErr = $"{ex.GetType().FullName}: {ex.Message}";
                log($"PuSaveProbe: FE_DDL post-check THREW: {feErr}");
            }

            log("=== PuSaveProbe END ===");

            return new ProbeResult(
                Before: before,
                After: after,
                SaveSucceeded: saveOk,
                SaveError: saveErr,
                ProducedFileBytes: fileBytes,
                TempFilePath: tempPath,
                PostFeDdlOk: feOk,
                PostFeDdlError: feErr);
        }

        private static Snapshot ReadSnapshot(object activePU, Action<string> log, string label)
        {
            string Read(string key) => SafeBag(activePU, key);

            var snap = new Snapshot(
                Locator: Read("Locator"),
                Disposition: Read("Disposition"),
                PersistenceUnitId: Read("Persistence_Unit_Id"),
                Name: Read("Name"),
                ModelType: Read("Model_Type"),
                TargetServer: Read("Target_Server"),
                ActiveModel: Read("Active_Model"),
                HiddenModel: Read("Hidden_Model"));

            log($"PuSaveProbe[{label}]: Locator           = {Mask(snap.Locator)}");
            log($"PuSaveProbe[{label}]: Disposition       = {snap.Disposition}");
            log($"PuSaveProbe[{label}]: Persistence_Unit_Id = {snap.PersistenceUnitId}");
            log($"PuSaveProbe[{label}]: Name              = {snap.Name}");
            log($"PuSaveProbe[{label}]: Model_Type        = {snap.ModelType}");
            log($"PuSaveProbe[{label}]: Target_Server     = {snap.TargetServer}");
            log($"PuSaveProbe[{label}]: Active_Model      = {snap.ActiveModel}");
            log($"PuSaveProbe[{label}]: Hidden_Model      = {snap.HiddenModel}");
            return snap;
        }

        private static void LogDiff(Snapshot before, Snapshot after, Action<string> log)
        {
            var fields = new (string Name, string Before, string After)[]
            {
                ("Locator", Mask(before.Locator), Mask(after.Locator)),
                ("Disposition", before.Disposition, after.Disposition),
                ("Persistence_Unit_Id", before.PersistenceUnitId, after.PersistenceUnitId),
                ("Name", before.Name, after.Name),
                ("Model_Type", before.ModelType, after.ModelType),
                ("Target_Server", before.TargetServer, after.TargetServer),
                ("Active_Model", before.ActiveModel, after.ActiveModel),
                ("Hidden_Model", before.HiddenModel, after.HiddenModel),
            };
            int diffCount = 0;
            foreach (var (name, b, a) in fields)
            {
                if (!string.Equals(b, a, StringComparison.Ordinal))
                {
                    diffCount++;
                    log($"PuSaveProbe[DIFF]: {name}: '{b}' -> '{a}'");
                }
            }
            if (diffCount == 0)
                log("PuSaveProbe[DIFF]: no PropertyBag fields changed (Save was non-destructive on metadata)");
            else
                log($"PuSaveProbe[DIFF]: {diffCount} field(s) changed");
        }

        private static string SafeBag(object pu, string propName)
        {
            // Try the PropertyBag(null,true) overload first - returns string
            // representations, which is what we want for diagnostics.
            try
            {
                dynamic dp = pu;
                dynamic bag = dp.PropertyBag(null, true);
                object? v = bag?.Value(propName);
                if (v != null) return v.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                // try the no-arg overload
                try
                {
                    dynamic dp2 = pu;
                    dynamic bag2 = dp2.PropertyBag();
                    object? v2 = bag2?.Value(propName);
                    if (v2 != null) return v2.ToString() ?? string.Empty;
                }
                catch (Exception ex2)
                {
                    return $"(error: {ex.GetType().Name}/{ex2.GetType().Name})";
                }
            }
            return string.Empty;
        }

        /// <summary>Mask the PSW=... segment so logs don't leak credentials.</summary>
        private static string Mask(string s) =>
            string.IsNullOrEmpty(s)
                ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(s, @"PSW=[^;&\s]*", "PSW=***",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
