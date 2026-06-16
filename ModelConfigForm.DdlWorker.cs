using System;
using System.Linq;
using Microsoft.Win32;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn
{
    /// <summary>
    /// DDL queue worker (Phase 1). Compiled in ALL builds (the feature is real,
    /// not dev-only; the on/off checkbox is just hidden in packaged builds).
    ///
    /// On a dedicated auto-logon CONSOLE-session erwin (DdlWorker user, RDP may be
    /// disconnected), when the hidden General-tab checkbox is ON this timer polls
    /// DDL_GENERATION_QUEUE. For each PENDING job it opens MODEL_PATH at LEFT_VERSION
    /// via the GUI Mart>Open automation, lets the add-in's reconnect timer adopt it,
    /// selects RIGHT_VERSION, runs the proven Generate-DDL pipeline
    /// (BtnAlterWizardProd_Click) with all user-facing modals suppressed
    /// (_ddlQueueActive), writes the produced DDL back to the row (DONE/FAILED), then
    /// closes the model and moves on. Strictly sequential (erwin is
    /// single-instance-per-logon); a small state machine drives the lifecycle off the
    /// 5 s WinForms timer (UI/STA thread, like StartReconnectTimer).
    /// </summary>
    public partial class ModelConfigForm
    {
        private const int DdlWorkerPollMs = 2000; // poll/claim cadence (also adopt-poll + cleanup retry)
        private const int DdlWorkerAdoptTimeoutSec = 60;

        // Transient open-failure retry (e.g. erwin not Mart-connected). The job is
        // requeued PENDING (RETRY_COUNT++) with an in-memory backoff so it is not
        // lost and does not tight-loop; only after MaxOpenRetries do we give up.
        private const int DdlWorkerMaxOpenRetries = 1000;   // ~ keep retrying until Mart connects
        private const int DdlWorkerRetryBackoffSec = 60;
        private DateTime _ddlNextClaimAllowedUtc = DateTime.MinValue;

        // HKCU persistence (per-user; only the dedicated DdlWorker user flips it on).
        private const string DdlWorkerRegKey = @"Software\EliteSoft\ErwinAddIn\DdlWorker";
        private const string DdlWorkerRegValue = "Enabled";

        private enum DdlWorkerState { Idle, Opening, Adopting, Running, Cleanup, Closing }

        private System.Windows.Forms.Timer _ddlWorkerTimer;
        private DdlWorkerState _ddlWorkerState = DdlWorkerState.Idle;

        // Read by the BtnAlterWizardProd_Click tail to suppress its user-facing
        // modals and route the result through FinishCurrentDdlJob. volatile because
        // the pipeline hops Task.Run threads, though the flag flips on the UI thread.
        private volatile bool _ddlQueueActive;

        private DdlQueueJob _currentDdlJob;
        private DateTime _ddlAdoptDeadlineUtc;
        private bool _ddlWorkerLoggedModelOpen; // log "waiting (model open)" once, not every tick

        /// <summary>
        /// True for the WHOLE lifetime of a worker job (claim -> open -> adopt ->
        /// pipeline -> cleanup). The connect flow that runs when the worker's opened
        /// model is adopted (InitializeValidationService) would otherwise pop two
        /// blocking modals with no one to dismiss them: the UDP-sync dialog
        /// (RunUdpSyncIfNeeded) and the model-required-UDP prompt
        /// (ValidationCoordinatorService.CheckModelRequiredUdpsOnce). Both honor this
        /// flag: the sync is forced to SILENTLY_APPLY (still dirties the model, no
        /// dialog) and the required prompt is skipped. Static so the coordinator
        /// service can read it without a back-reference to the form.
        /// </summary>
        internal static volatile bool DdlWorkerActiveUnattended;

        // ---- HKCU enable flag (mirrors WmCommandLogger's HKCU DWORD idiom) ----

        /// <summary>Read the persisted worker-enabled flag from HKCU (default false).</summary>
        private static bool ReadDdlWorkerEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DdlWorkerRegKey);
                if (key == null) return false;
                return key.GetValue(DdlWorkerRegValue, 0) is int i && i != 0;
            }
            catch { return false; }
        }

        /// <summary>Persist the worker-enabled flag to HKCU.</summary>
        private void WriteDdlWorkerEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(DdlWorkerRegKey);
                key?.SetValue(DdlWorkerRegValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex) { Log($"[DDLWORKER] HKCU write failed: {ex.Message}"); }
        }

        // ---- Start / stop (called by the checkbox + form load) ----

        /// <summary>
        /// Restore the checkbox state from HKCU on form load and start the worker
        /// if it was left enabled. Safe to call when chkDdlWorker exists; no-op if not.
        /// </summary>
        private void InitializeDdlWorkerFromRegistry()
        {
            try
            {
                bool enabled = ReadDdlWorkerEnabled();
                if (chkDdlWorker != null) chkDdlWorker.Checked = enabled; // fires CheckedChanged -> Start/Stop
                Log($"[DDLWORKER] init from HKCU: enabled={enabled}");
            }
            catch (Exception ex) { Log($"[DDLWORKER] init failed: {ex.Message}"); }
        }

        /// <summary>Checkbox handler: persist + start/stop the worker.</summary>
        private void ChkDdlWorker_CheckedChanged(object sender, EventArgs e)
        {
            bool on = chkDdlWorker != null && chkDdlWorker.Checked;
            WriteDdlWorkerEnabled(on);
            if (on) StartDdlWorker();
            else StopDdlWorker();
        }

        private void StartDdlWorker()
        {
            if (_ddlWorkerTimer == null)
            {
                _ddlWorkerTimer = new System.Windows.Forms.Timer { Interval = DdlWorkerPollMs };
                _ddlWorkerTimer.Tick += DdlWorkerTimer_Tick;
            }
            _ddlWorkerState = DdlWorkerState.Idle;
            _ddlWorkerTimer.Start();
            Log($"[DDLWORKER] STARTED (poll {DdlWorkerPollMs} ms).");
        }

        private void StopDdlWorker()
        {
            _ddlWorkerTimer?.Stop();
            DdlWorkerActiveUnattended = false;
            Log("[DDLWORKER] STOPPED.");
        }

        // ---- State machine (5 s tick, UI/STA thread) ----

        private void DdlWorkerTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Hard safety gates - never act while a pipeline (ours or a manual
                // one) is running or erwin is blocked by a modal.
                if (chkDdlWorker == null || !chkDdlWorker.Checked) return;
                if (_ddlQueueActive) return;            // Running: pipeline in flight

                // Cleanup / Closing must run even if a modal (the dirty-model "Save
                // Models" prompt) is up - dismissing it is their whole job. So they are
                // handled BEFORE the pipeline-active / modal-blocked guards below.
                if (_ddlWorkerState == DdlWorkerState.Closing) return; // bg close in flight
                if (_ddlWorkerState == DdlWorkerState.Cleanup) { DdlWorkerDoCleanup(); return; }

                if (_martMartPipelineActive) return;    // some pipeline owns erwin
                try { if (Services.Win32Helper.IsErwinMainWindowBlockedByModal()) return; } catch { /* probe best-effort */ }

                switch (_ddlWorkerState)
                {
                    case DdlWorkerState.Opening:  break; // background open in flight; wait for its continuation
                    case DdlWorkerState.Adopting: DdlWorkerCheckAdoption(); break;
                    case DdlWorkerState.Running:  break; // guarded by _ddlQueueActive above
                    default:                      DdlWorkerTryStartNextJob(); break;
                }
            }
            catch (Exception ex)
            {
                Log($"[DDLWORKER] tick error: {ex.Message}");
                FailAndResetCurrentJob("worker tick exception: " + ex.Message);
            }
        }

        private void DdlWorkerTryStartNextJob()
        {
            // Backoff after a transient open failure (e.g. Mart not connected): do
            // not tight-loop re-claiming; wait out the backoff window first.
            if (DateTime.UtcNow < _ddlNextClaimAllowedUtc) return;

            // The worker only opens a model on a model-less erwin, so it never
            // disturbs a model a human (or a prior job) left open. (On the dedicated
            // console worker, erwin sits model-less; close any open model to let the
            // worker pick up jobs.)
            if (_isConnected || _currentModel != null)
            {
                if (!_ddlWorkerLoggedModelOpen)
                {
                    _ddlWorkerLoggedModelOpen = true;
                    Log("[DDLWORKER] enabled but a model is open - worker runs only on a model-less erwin. Close the model to let it claim jobs. Waiting...");
                }
                return;
            }
            _ddlWorkerLoggedModelOpen = false;

            DdlQueueJob job;
            try { job = DdlQueueService.Instance.TryClaimNextPending(Log); }
            catch (Exception ex) { Log($"[DDLWORKER] claim failed (queue table missing?): {ex.Message}"); return; }
            if (job == null) return; // queue empty

            _currentDdlJob = job;
            // Suppress connect-time modals (UDP sync + required-UDP) for the whole
            // job - the adopted model's InitializeValidationService runs while this
            // is set. Cleared when the job's model is closed (back to Idle).
            DdlWorkerActiveUnattended = true;
            _ddlWorkerState = DdlWorkerState.Opening; // background open in flight; tick must not re-enter
            Log($"[DDLWORKER] job {job.Id}: opening '{job.ModelPath}' v{job.LeftVersion} as active/LEFT (background thread)...");

            var jobLocal = job;
            // Open on a BACKGROUND thread - exactly like the compare pipeline's
            // Task.Run(DriveCompareToResolveDifferences). The picker is driven via
            // Win32 from there, leaving erwin's UI/STA thread (this thread) FREE to
            // process WM_COMMAND 1060 and paint the picker. (2026-06-15 root cause:
            // running OpenMartVersionAsMdiChild on the UI thread blocked it for the
            // whole WaitForNewDialog, so the picker could not appear until the wait
            // timed out - "Open dialog appeared exactly at the 20s timeout".)
            System.Threading.Tasks.Task.Run(() =>
            {
                IntPtr child = IntPtr.Zero;
                bool transient = true;
                string reason = null;
                // keepVisible:true mirrors the proven cross-version path. The picker +
                // open are pure Win32 (headless-safe). Log -> AddinLogger (thread-safe).
                // transient=false means a PERMANENT data error (bad model/version) ->
                // the job is FAILED, not retried.
                try { child = Services.MartMartAutomation.OpenMartVersionAsMdiChild(jobLocal.LeftVersion, jobLocal.ModelPath, true, out transient, out reason, Log); }
                catch (Exception ex) { reason = "open model threw: " + ex.Message; transient = true; }
                IntPtr childLocal = child; bool transientLocal = transient; string reasonLocal = reason;
                try { BeginInvoke(new Action(() => OnDdlWorkerOpenComplete(jobLocal, childLocal, transientLocal, reasonLocal))); }
                catch (Exception ex) { Log($"[DDLWORKER] open-continuation marshal failed: {ex.Message}"); }
            });
        }

        /// <summary>Back on the UI thread after the background Mart>Open completes.</summary>
        private void OnDdlWorkerOpenComplete(DdlQueueJob job, IntPtr child, bool transient, string failReason)
        {
            if (child == IntPtr.Zero)
            {
                // transient=true -> requeue + backoff (e.g. erwin not Mart-connected).
                // transient=false -> PERMANENT (model/version not in catalog): FAILED, no retry.
                RequeueOrFailOpen(job, failReason ?? $"failed to open model '{job.ModelPath}' v{job.LeftVersion} (see Debug Log)", transient);
                _ddlWorkerState = DdlWorkerState.Cleanup;
                return;
            }

            // Opened. Let the add-in's reconnect timer adopt it
            // (sets _isConnected / _currentModel / ConfigContext).
            _ddlAdoptDeadlineUtc = DateTime.UtcNow.AddSeconds(DdlWorkerAdoptTimeoutSec);
            _ddlWorkerState = DdlWorkerState.Adopting;
            try { StartReconnectTimer(); } catch (Exception ex) { Log($"[DDLWORKER] StartReconnectTimer note: {ex.Message}"); }
        }

        private void DdlWorkerCheckAdoption()
        {
            if (_isConnected && _currentModel != null && ConfigContextService.Instance.IsInitialized)
            {
                Log($"[DDLWORKER] job {_currentDdlJob?.Id}: model adopted ('{_connectedModelName}'). Running pipeline (right v{_currentDdlJob?.RightVersion}).");
                DdlWorkerRunPipeline();
                return;
            }
            if (DateTime.UtcNow > _ddlAdoptDeadlineUtc)
            {
                FailAndResetCurrentJob($"model opened but add-in did not adopt within {DdlWorkerAdoptTimeoutSec}s " +
                    $"(isConnected={_isConnected}, currentModel={(_currentModel != null)}, configInit={ConfigContextService.Instance.IsInitialized})");
            }
        }

        private void DdlWorkerRunPipeline()
        {
            var job = _currentDdlJob;
            if (job == null) { _ddlWorkerState = DdlWorkerState.Cleanup; return; }

            // Same guards a green-button click hits (mirrors the spike accepted-path).
            if (!ConfigContextService.Instance.IsInitialized) { FailAndResetCurrentJob("config context not initialized (model not Mart-mapped)"); return; }
            if (!ConfigContextService.Instance.IsMartModel)   { FailAndResetCurrentJob("adopted model is not Mart-hosted"); return; }
            if (!(_ddlAllowLastSaved || _ddlAllowPreviousVersions)) { FailAndResetCurrentJob("From-Mart DDL source not enabled (admin gates DDL_COMPARE_LAST_SAVED + DDL_COMPARE_PREVIOUS_VERSIONS both off)"); return; }
            if (!rbFromMart.Checked) rbFromMart.Checked = true;
            if (job.RightVersion <= 0) { FailAndResetCurrentJob($"invalid right version v{job.RightVersion}"); return; }
            if (!TrySelectRightVersion(job.RightVersion, out string selErr)) { FailAndResetCurrentJob(selErr); return; }

            _ddlQueueActive = true;
            _ddlWorkerState = DdlWorkerState.Running;
            Log($"[DDLWORKER] job {job.Id}: launching pipeline (right v{job.RightVersion}).");
            // Same handler the green button invokes. async void: returns immediately;
            // its tail calls FinishCurrentDdlJob with (script, err).
            BtnAlterWizardProd_Click(this, EventArgs.Empty);
        }

        /// <summary>
        /// Called from the BtnAlterWizardProd_Click tail when _ddlQueueActive: writes
        /// this run's outcome to the claimed queue row, clears the active flag, and
        /// schedules model cleanup on the next tick.
        /// </summary>
        private void FinishCurrentDdlJob(string script, string err)
        {
            _ddlQueueActive = false;
            var job = _currentDdlJob;
            if (job == null) { Log("[DDLWORKER] FinishCurrentDdlJob: no current job (ignored)."); _ddlWorkerState = DdlWorkerState.Cleanup; return; }

            try
            {
                if (err != null)
                    DdlQueueService.Instance.WriteFailure(job.Id, err, Log);
                else if (string.IsNullOrEmpty(script))
                    DdlQueueService.Instance.WriteResult(job.Id, string.Empty, Log); // no diff = DONE (empty DDL)
                else
                    DdlQueueService.Instance.WriteResult(job.Id, script, Log);
            }
            catch (Exception ex)
            {
                // Best-effort: leave the row RUNNING for admin requeue; never crash the worker.
                Log($"[DDLWORKER] job {job.Id}: writing result to queue FAILED: {ex.Message}");
            }

            _currentDdlJob = null;
            _ddlWorkerState = DdlWorkerState.Cleanup; // next tick closes the model
        }

        /// <summary>
        /// Transient OPEN-phase failure (e.g. erwin not Mart-connected): record the
        /// error and requeue the job to PENDING (RETRY_COUNT++) with a backoff, so it
        /// is not lost and runs once the condition clears. After MaxOpenRetries give
        /// up and mark FAILED so a genuinely broken job does not churn forever.
        /// </summary>
        private void RequeueOrFailOpen(DdlQueueJob job, string error, bool transient)
        {
            try
            {
                if (!transient)
                {
                    // PERMANENT data error (model/version not in the Mart catalog):
                    // retrying cannot fix a wrong MODEL_PATH/version, so FAIL the job
                    // with the specific reason for the operator to correct the row.
                    Log($"[DDLWORKER] job {job.Id}: PERMANENT failure (no retry): {error}");
                    DdlQueueService.Instance.WriteFailure(job.Id, error, Log);
                }
                else if (job.RetryCount + 1 >= DdlWorkerMaxOpenRetries)
                {
                    Log($"[DDLWORKER] job {job.Id}: open failed {job.RetryCount + 1}x - giving up (FAILED): {error}");
                    DdlQueueService.Instance.WriteFailure(job.Id, $"open failed after {job.RetryCount + 1} attempts: {error}", Log);
                }
                else
                {
                    Log($"[DDLWORKER] job {job.Id}: transient open failure (attempt {job.RetryCount + 1}) - requeue + backoff {DdlWorkerRetryBackoffSec}s: {error}");
                    DdlQueueService.Instance.RequeueForRetry(job.Id, error, Log);
                    _ddlNextClaimAllowedUtc = DateTime.UtcNow.AddSeconds(DdlWorkerRetryBackoffSec);
                }
            }
            catch (Exception ex) { Log($"[DDLWORKER] job {job.Id}: requeue/fail write err: {ex.Message}"); }
            _currentDdlJob = null;
        }

        private void FailAndResetCurrentJob(string error)
        {
            _ddlQueueActive = false;
            var job = _currentDdlJob;
            if (job != null)
            {
                Log($"[DDLWORKER] job {job.Id} FAILED: {error}");
                try { DdlQueueService.Instance.WriteFailure(job.Id, error, Log); }
                catch (Exception ex) { Log($"[DDLWORKER] write failure err: {ex.Message}"); }
                _currentDdlJob = null;
            }
            _ddlWorkerState = DdlWorkerState.Cleanup;
        }

        private void DdlWorkerDoCleanup()
        {
            // Close the job's LEFT/active model WITHOUT saving so erwin returns to
            // model-less and the next job can open its own model. The adopted model is
            // dirty (connect-time UDP sync + the compare), so a plain WM_CLOSE leaves a
            // "Save Models" prompt; CloseActiveMartModelDiscardingChanges runs the same
            // proven Save-Models + Mart-Offline dismiss sweep the Review teardown uses.
            // Runs on a BACKGROUND thread (like the open) so erwin's UI thread stays free
            // to raise + tear down the prompts (and the dismiss uses GetCursorPos).
            _ddlWorkerState = DdlWorkerState.Closing;
            Log("[DDLWORKER] cleanup: closing the job model (discard changes) on a background thread...");
            System.Threading.Tasks.Task.Run(() =>
            {
                bool gone = false;
                try { gone = Services.MartMartAutomation.CloseActiveMartModelDiscardingChanges(Log); }
                catch (Exception ex) { Log($"[DDLWORKER] cleanup close threw: {ex.Message}"); }
                try { BeginInvoke(new Action(() => OnDdlWorkerCloseComplete(gone))); }
                catch (Exception ex) { Log($"[DDLWORKER] cleanup-continuation marshal failed: {ex.Message}"); }
            });
        }

        /// <summary>Back on the UI thread after the background model-close completes.</summary>
        private void OnDdlWorkerCloseComplete(bool gone)
        {
            if (!gone)
            {
                Log("[DDLWORKER] cleanup: model not closed (Save Models dismiss aborted / left for manual) - retry next tick.");
                _ddlWorkerState = DdlWorkerState.Cleanup; // retry (exempt from the modal guard)
                return;
            }
            // Window is gone; the add-in's own close detection will flip
            // _isConnected/_currentModel shortly. Go Idle - the Idle guard
            // (DdlWorkerTryStartNextJob) waits for model-less before the next claim.
            Log("[DDLWORKER] cleanup done - model window closed; next job will start once the add-in goes model-less.");
            DdlWorkerActiveUnattended = false; // re-enable interactive connect modals
            _ddlWorkerState = DdlWorkerState.Idle;
        }

        /// <summary>
        /// Select the cmbRightModel entry matching v{version}. Items are
        /// "v{v} (Version {v})" (RebuildRightCombo) and read back by ParseRightVersion
        /// via regex ^v(\d+). Compiled in all builds (also used by the dev spike).
        /// </summary>
        private bool TrySelectRightVersion(int v, out string error)
        {
            error = null;
            try
            {
                for (int i = 0; i < cmbRightModel.Items.Count; i++)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(cmbRightModel.Items[i].ToString(), @"^v(\d+)");
                    if (match.Success && int.Parse(match.Groups[1].Value) == v)
                    {
                        cmbRightModel.SelectedIndex = i;
                        Log($"[DDLWORKER] right version set to v{v} (combo index {i}).");
                        return true;
                    }
                }
                error = $"requested right version v{v} not in combo - enable DDL_COMPARE_PREVIOUS_VERSIONS for this model " +
                        $"(items: [{string.Join(", ", cmbRightModel.Items.Cast<object>().Select(x => x.ToString()))}])";
                return false;
            }
            catch (Exception ex) { error = "right-version select failed: " + ex.Message; return false; }
        }
    }
}
