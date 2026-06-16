#if !PACKAGED
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn
{
    /// <summary>
    /// Phase 0 dev-only spike for the DDL queue-worker linchpins. NOT compiled
    /// into packaged builds. It re-introduces the proven cold-start primitives
    /// from the reverted commit 2d622dc, triggered by dev hotkeys instead of
    /// WM_COPYDATA, to prove two unverified linchpins on a dedicated worker session:
    ///   0a: an in-process <c>PersistenceUnits.Add</c> surfaces a Mart model as a
    ///       GUI MDI child, the reconnect timer adopts it, and the proven
    ///       cross-version Generate-DDL pipeline then runs.
    ///   0b: the same run still renders the visible XTP wizard well enough for the
    ///       pixel-based mouse clicks to land (and real DDL to be produced) while
    ///       the worker's RDP session is disconnected.
    /// The hotkey wiring (ids, register/unregister, WndProc dispatch) lives in the
    /// existing <c>#if !PACKAGED</c> block of ModelConfigForm.cs. Delete this file
    /// plus those few lines to remove the spike entirely. It deliberately does NOT
    /// touch the production pipeline (BtnAlterWizardProd_Click) or any persisted state.
    /// </summary>
    public partial class ModelConfigForm
    {
        // Editable spike target (single place to retune). KKR on the test Mart.
        private const string SpikeMartPath = "Demo/SQL/1_DEV/KKR";
        private const int SpikeLeftVersion = 7;   // LEFT/active version opened in-process (locator VNO)
        private const int SpikeRightVersion = 1;  // RIGHT/compare target selected in cmbRightModel

        // Disposition for the in-process open. "" = normal open, which should
        // surface the model as an MDI child. If "" does NOT surface it (the
        // reconnect timer never adopts within the deadline), switch to "OVM=No"
        // and rebuild - this is the documented GUI-open fallback to try.
        private const string SpikeColdDisposition = "";
        private const int SpikeConnectTimeoutSec = 40;
        private const int SpikeArmDelayMs = 30000; // 0b: time to disconnect RDP after arming

        // Cold-start adoption-poll state.
        private Timer _spikeColdTimer;
        private int _spikeColdRightVersion;
        private DateTime _spikeColdDeadlineUtc;

        // 0b auto-fire arm state.
        private Timer _spikeArmTimer;

        // Last cmbRightModel snapshot, surfaced in the status file.
        private string _spikeComboItems;

        /// <summary>0a: fire the cold-start open + pipeline now (attended run).</summary>
        private void SpikeColdStartFire()
        {
            try
            {
                if (_isConnected && _currentModel != null)
                {
                    Log("[SPIKE] 0a: already connected to a model - in-process Add is N/A; running pipeline directly.");
                    RunSpikeAcceptedPath(SpikeRightVersion);
                    return;
                }
                ColdStartOpenThenFire(SpikeMartPath, SpikeLeftVersion, SpikeRightVersion);
            }
            catch (Exception ex)
            {
                Log("[SPIKE] 0a fire failed: " + ex.Message);
                WriteSpikeStatus("fire", false, ex.Message);
            }
        }

        /// <summary>
        /// 0b: arm a one-shot auto-fire so the run happens while the RDP session is
        /// disconnected. Non-blocking (no modal) so the user can disconnect in time.
        /// </summary>
        private void SpikeColdStartArm()
        {
            try
            {
                int secs = SpikeArmDelayMs / 1000;
                Log($"[SPIKE] 0b: auto-fire armed in {secs}s. DISCONNECT the RDP session now (do NOT log off).");
                try { lblDDLStatus.Text = $"[SPIKE] auto-fire in {secs}s - disconnect RDP now"; }
                catch (Exception ex) { Log("[SPIKE] status label note: " + ex.Message); }

                if (_spikeArmTimer == null)
                {
                    _spikeArmTimer = new Timer();
                    _spikeArmTimer.Tick += SpikeArmTimer_Tick;
                }
                _spikeArmTimer.Stop();
                _spikeArmTimer.Interval = SpikeArmDelayMs;
                _spikeArmTimer.Start();
            }
            catch (Exception ex)
            {
                Log("[SPIKE] 0b arm failed: " + ex.Message);
                WriteSpikeStatus("arm", false, ex.Message);
            }
        }

        private void SpikeArmTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                _spikeArmTimer.Stop();
                Log("[SPIKE] 0b: auto-fire NOW (this run should execute under RDP disconnect).");
                SpikeColdStartFire();
            }
            catch (Exception ex)
            {
                Log("[SPIKE] 0b auto-fire tick failed: " + ex.Message);
                WriteSpikeStatus("arm-fire", false, ex.Message);
            }
        }

        /// <summary>
        /// Open the LEFT model in-process via SCAPI, then let the add-in's own
        /// reconnect timer adopt it. It deliberately does NOT set _currentModel /
        /// _session / ConfigContext by hand - doing so would bypass the pipeline's
        /// duplicate-locator / pipeline-owned guards. Adapted from reverted commit 2d622dc.
        /// </summary>
        private void ColdStartOpenThenFire(string modelPath, int leftVersion, int rightVersion)
        {
            try
            {
                Log($"[SPIKE] cold-start begin: model='{modelPath}' lv={leftVersion} rv={rightVersion} disp='{SpikeColdDisposition}'");

                var mi = Services.DdlGenerationService.GetMartConnectionInfo(Log);
                if (mi == null) { WriteSpikeStatus("creds", false, "no Mart connection info (CONNECTION_DEF DB_TYPE='MART_API')"); return; }

                string mp = modelPath.Trim().Trim('/');
                // SCAPI Mart locator with embedded creds; VNO selects the version.
                string locator = $"mart://Mart/{mp}?TRC=NO;SRV={mi.Value.host};PRT={mi.Value.port};ASR=MartServer;UID={mi.Value.username};PSW={mi.Value.password};VNO={leftVersion}";
                string masked = Regex.Replace(locator, @"PSW=[^;]*", "PSW=***");
                Log($"[SPIKE] opening LEFT in-process: {masked}");

                int before = 0;
                try { before = (int)_scapi.PersistenceUnits.Count; }
                catch (Exception ex) { Log("[SPIKE] PU count (before) note: " + ex.Message); }

                dynamic pu;
                try
                {
                    pu = _scapi.PersistenceUnits.Add(locator, SpikeColdDisposition);
                }
                catch (Exception ex)
                {
                    Log($"[SPIKE] PersistenceUnits.Add FAILED ({ex.GetType().Name}): {ex.Message}");
                    WriteSpikeStatus("add", false, $"{ex.GetType().Name}: {ex.Message}");
                    return;
                }

                int after = 0;
                try { after = (int)_scapi.PersistenceUnits.Count; }
                catch (Exception ex) { Log("[SPIKE] PU count (after) note: " + ex.Message); }

                string puName = "(?)";
                try { puName = pu?.Name?.ToString() ?? "(null)"; }
                catch (Exception ex) { Log("[SPIKE] PU name note: " + ex.Message); }

                Log($"[SPIKE] PersistenceUnits.Add OK: count {before}->{after}, name='{puName}'. Waiting for reconnect-timer adoption...");
                WriteSpikeStatus("add-ok", true, $"puCount {before}->{after}, name='{puName}'");

                // The reconnect timer adopts the newly opened model. It is started
                // on a bare-erwin form load, but re-arm defensively.
                try { StartReconnectTimer(); }
                catch (Exception ex) { Log("[SPIKE] StartReconnectTimer note: " + ex.Message); }

                // Poll (non-blocking) for the add-in to bind the model.
                _spikeColdRightVersion = rightVersion;
                _spikeColdDeadlineUtc = DateTime.UtcNow.AddSeconds(SpikeConnectTimeoutSec);
                if (_spikeColdTimer == null)
                {
                    _spikeColdTimer = new Timer { Interval = 500 };
                    _spikeColdTimer.Tick += SpikeColdTimer_Tick;
                }
                _spikeColdTimer.Start();
            }
            catch (Exception ex)
            {
                Log($"[SPIKE] cold-start open failed: {ex.Message}");
                WriteSpikeStatus("cold-start", false, ex.Message);
            }
        }

        private void SpikeColdTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_isConnected && _currentModel != null && ConfigContextService.Instance.IsInitialized)
                {
                    _spikeColdTimer.Stop();
                    Log($"[SPIKE] add-in adopted model '{_connectedModelName}'. Running pipeline (rv={_spikeColdRightVersion}).");
                    WriteSpikeStatus("adopted", true, $"model='{_connectedModelName}'");
                    RunSpikeAcceptedPath(_spikeColdRightVersion);
                    return;
                }
                if (DateTime.UtcNow > _spikeColdDeadlineUtc)
                {
                    _spikeColdTimer.Stop();
                    string err = $"model opened but add-in did not adopt within {SpikeConnectTimeoutSec}s " +
                                 $"(isConnected={_isConnected}, currentModel={(_currentModel != null)}, " +
                                 $"configInit={ConfigContextService.Instance.IsInitialized}). " +
                                 "If no MDI child ever appeared, set SpikeColdDisposition=\"OVM=No\" and rebuild.";
                    Log("[SPIKE] " + err);
                    WriteSpikeStatus("adopt-timeout", false, err);
                }
            }
            catch (Exception ex)
            {
                try { _spikeColdTimer.Stop(); } catch (Exception stopEx) { Log("[SPIKE] cold timer stop note: " + stopEx.Message); }
                Log($"[SPIKE] adoption wait failed: {ex.Message}");
                WriteSpikeStatus("adopt-wait", false, ex.Message);
            }
        }

        /// <summary>
        /// The four runtime guards + right-version selection + pipeline fire,
        /// mirroring the reverted RunTriggerAcceptedPath. Fails LOUDLY (no silent
        /// fallback) when the requested version is not present in the combo.
        /// </summary>
        private void RunSpikeAcceptedPath(int rightVersion)
        {
            try
            {
                if (!ConfigContextService.Instance.IsInitialized) { WriteSpikeStatus("guard", false, "config context not initialized (model not Mart-mapped)"); return; }
                if (_martMartPipelineActive) { WriteSpikeStatus("guard", false, "pipeline busy - retry shortly"); return; }
                if (Services.Win32Helper.IsErwinMainWindowBlockedByModal()) { WriteSpikeStatus("guard", false, "erwin is blocked by a modal dialog"); return; }
                // From-Mart availability is decided by the admin DDL gates, NOT by
                // rbFromMart.Visible (an inactive tab-page control reports Visible=false).
                if (!(_ddlAllowLastSaved || _ddlAllowPreviousVersions)) { WriteSpikeStatus("guard", false, "From-Mart DDL source not enabled (DDL_COMPARE_LAST_SAVED + DDL_COMPARE_PREVIOUS_VERSIONS both off)"); return; }

                _spikeComboItems = string.Join(", ", cmbRightModel.Items.Cast<object>().Select(x => x.ToString()));
                Log($"[SPIKE] cmbRightModel items: [{_spikeComboItems}]");

                if (!rbFromMart.Checked) rbFromMart.Checked = true;
                if (rightVersion <= 0) { WriteSpikeStatus("right-select", false, $"invalid right version '{rightVersion}'"); return; }
                if (!TrySelectRightVersion(rightVersion, out string selErr)) { WriteSpikeStatus("right-select", false, selErr); return; }

                Log("[SPIKE] guards passed + right version selected. Launching pipeline (BtnAlterWizardProd_Click).");
                WriteSpikeStatus("pipeline-launched", true, null);
                // Same handler the green button invokes. async void: returns
                // immediately; the outcome surfaces via the pipeline's own logs,
                // its DDL result dialog, and %TEMP%\erwin-alter-ddl-captured.sql.
                BtnAlterWizardProd_Click(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log($"[SPIKE] accepted-path failed: {ex.Message}");
                WriteSpikeStatus("accepted-path", false, ex.Message);
            }
        }

        // TrySelectRightVersion moved to ModelConfigForm.DdlWorker.cs (compiled in
        // all builds, shared by this dev spike and the production queue worker).

        /// <summary>
        /// Write a concise milestone marker to %TEMP%\erwin-ddl-spike-status.json
        /// (atomic tmp+move, UTF-8 no BOM). The DDL itself is captured separately by
        /// the native bridge at %TEMP%\erwin-alter-ddl-captured.sql. This file is the
        /// readable report for the 0b (disconnected) run, inspected after reconnect.
        /// </summary>
        private void WriteSpikeStatus(string stage, bool ok, string error)
        {
            try
            {
                string Esc(string s) => s == null ? "null"
                    : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
                string captured = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erwin-alter-ddl-captured.sql");
                string json = "{"
                    + $"\"stage\":{Esc(stage)},"
                    + $"\"ok\":{(ok ? "true" : "false")},"
                    + $"\"error\":{Esc(error)},"
                    + $"\"martPath\":{Esc(SpikeMartPath)},"
                    + $"\"leftVersion\":{SpikeLeftVersion},"
                    + $"\"rightVersion\":{SpikeRightVersion},"
                    + $"\"disposition\":{Esc(SpikeColdDisposition)},"
                    + $"\"comboItems\":{Esc(_spikeComboItems)},"
                    + $"\"capturedFile\":{Esc(captured)},"
                    + $"\"ts\":{Esc(DateTime.UtcNow.ToString("o"))}"
                    + "}";
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erwin-ddl-spike-status.json");
                string tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(tmp, path);
                Log($"[SPIKE] status written: stage={stage} ok={ok} -> {path}");
            }
            catch (Exception ex) { Log("[SPIKE] status write failed: " + ex.Message); }
        }
    }
}
#endif
