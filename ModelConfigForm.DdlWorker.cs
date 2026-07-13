using System;
using System.Linq;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn
{
    /// <summary>
    /// DDL queue worker. Since 2026-07-12 the worker exists ONLY in the
    /// DDLGENERATOR build flavor (`dotnet build -p:DdlGenerator=true`): it is
    /// always on there (auto-started at form load, guarded by a per-logon-
    /// session single-worker mutex) and can never run in normal interactive
    /// builds (the old General-tab checkbox + HKCU Enabled flag are gone).
    ///
    /// On the dedicated auto-logon CONSOLE-session erwin (DdlWorker user, RDP
    /// may be disconnected) this timer polls DDL_GENERATION_QUEUE. For each
    /// PENDING job it opens MODEL_PATH at LEFT_VERSION via the GUI Mart>Open
    /// automation, lets the add-in's reconnect timer adopt it, selects
    /// RIGHT_VERSION, runs the proven Generate-DDL pipeline
    /// (BtnAlterWizardProd_Click) with all user-facing modals suppressed
    /// (_ddlQueueActive), writes the produced DDL back to the row (DONE/FAILED),
    /// then closes the model and moves on. Strictly sequential; a small state
    /// machine drives the lifecycle off the 2 s WinForms timer (UI/STA thread,
    /// like StartReconnectTimer).
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

        // Cleanup (model close) retry CAP. The job-4 incident (2026-07-11)
        // retried the close FOREVER (every ~15s: WM_CLOSE + Save-Models sweep +
        // ForceForeground) because a leftover CC wizard held the ;Duplicate=YES
        // PU and the model could never close - erwin was unusable for the
        // operator. After this many failed attempts the worker gives up loudly,
        // goes Idle and waits for a manual close (the Idle guard already
        // refuses to claim jobs while a model is open, so nothing is lost).
        private const int DdlWorkerMaxCleanupAttempts = 4;
        private int _ddlCleanupAttempts;

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

        /// <summary>
        /// True when this erwin instance is DEDICATED to DDL generation -
        /// decided at COMPILE TIME by the DDLGENERATOR flavor since 2026-07-12
        /// (previously a hidden General-tab checkbox). In this mode the add-in
        /// must stay OUT of erwin's way completely: no glossary, no naming
        /// standards, no predefined columns, no dependency sets, no UDP
        /// sync/runtime, no validation monitoring - only ConfigContext + the
        /// DDL Generation surfaces are initialized on connect
        /// (user requirement 2026-07-11: the interactive-service init +
        /// UDP-sync writes on adopted models dirtied BOTH sides of a manual
        /// Complete Compare and left a live session on the LEFT model; the
        /// compare then hung at "Processing Left Model").
        /// </summary>
#if DDLGENERATOR
        private static bool IsDdlDedicatedInstance => true;
#else
        private static bool IsDdlDedicatedInstance => false;
#endif

        // ---- Form visibility during automation ----

        private bool _formHiddenForAutomation;

        /// <summary>
        /// Hides the add-in main form while an automation drives erwin
        /// (manual Generate-DDL pipeline or a whole worker job including its
        /// cleanup). The pipelines synthesize REAL mouse clicks (RD
        /// Apply-to-Right arrow, Save-Models checkbox) at absolute screen
        /// coordinates - if the form happens to overlap those points the
        /// click lands on our UI instead and the step silently no-ops
        /// (user requirement 2026-07-11). Timers/BeginInvoke keep working on
        /// a hidden form (handle stays alive), so the worker state machine is
        /// unaffected.
        /// </summary>
        private void HideFormForAutomation(string reason)
        {
            if (_formHiddenForAutomation) return;
            _formHiddenForAutomation = true;
            try { Hide(); Log($"[FORM] add-in form hidden for automation ({reason})."); }
            catch (Exception ex) { Log($"[FORM] hide failed: {ex.Message}"); }
        }

        /// <summary>Reverses <see cref="HideFormForAutomation"/> once the automation is done.</summary>
        private void RestoreFormAfterAutomation(string reason)
        {
            if (!_formHiddenForAutomation) return;
            _formHiddenForAutomation = false;
            try { Show(); Log($"[FORM] add-in form restored ({reason})."); }
            catch (Exception ex) { Log($"[FORM] restore failed: {ex.Message}"); }
        }

#if DDLGENERATOR
        // ---- DDLGENERATOR flavor: always-on worker + single-worker mutex ----

        // Held for the whole process lifetime once acquired (never released
        // early); the OS frees it at process exit. Static: one per process.
        private static System.Threading.Mutex _singleWorkerMutex;

        /// <summary>
        /// Acquires the per-logon-session single-worker mutex. Two erwin
        /// processes in one session must NEVER both drive the queue: the job-4
        /// incident (2026-07-11) showed two workers interleaving Mart>Open
        /// automation and ForceForeground fights on one desktop. "Local\" scope
        /// = one owner per logon session. An abandoned mutex (previous owner
        /// crashed) is treated as acquired - ownership passes to us.
        /// </summary>
        private static bool TryAcquireSingleWorkerMutex()
        {
            if (_singleWorkerMutex != null) return true;
            var m = new System.Threading.Mutex(initiallyOwned: false, @"Local\EliteSoft.ErwinAddIn.DdlWorker");
            bool acquired;
            try { acquired = m.WaitOne(0); }
            catch (System.Threading.AbandonedMutexException) { acquired = true; } // prior owner died - ownership granted
            if (!acquired) { m.Dispose(); return false; }
            _singleWorkerMutex = m;
            return true;
        }

        /// <summary>
        /// DDLGENERATOR flavor UI: only the General tab stays visible, topped
        /// by a red "DDL Generation MODE ON!" banner; the form-level Close
        /// button is hidden (closing the form would kill the worker). Called
        /// once from the ctor AFTER InitializeGeneralTab. The removed tab
        /// PAGES are only detached from the TabControl - NOT disposed: the DDL
        /// Generation tab's controls (rbFromMart, cmbRightModel,
        /// btnAlterWizardProd, chkFilterObjects) stay alive because the worker
        /// pipeline drives them programmatically. Dev-only surfaces (#if DEV
        /// buttons, RECON hotkeys) are untouched and keep working in dev
        /// builds of this flavor.
        /// </summary>
        private void ApplyDdlGeneratorUiRestrictions()
        {
            foreach (var page in new[] { tabValidation, tabTableProcesses, tabDdlGeneration })
            {
                if (page != null && tabControl.TabPages.Contains(page))
                    tabControl.TabPages.Remove(page);
            }

            Text += " - DDL Generator";

            var banner = new System.Windows.Forms.Label
            {
                Text = "DDL Generation MODE ON!",
                Font = new System.Drawing.Font("Segoe UI", 13f, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.White,
                BackColor = System.Drawing.Color.FromArgb(200, 60, 60),
                AutoSize = true,
                Padding = new System.Windows.Forms.Padding(14, 8, 14, 8),
                // Top-right of the General tab header area: the title label ends
                // well before x=360 and the first card starts below y=80, so the
                // banner never overlaps existing content.
                Location = new System.Drawing.Point(360, 18)
            };
            tabGeneral.Controls.Add(banner);
            banner.BringToFront();

            // The bottom status-bar Close button would end the worker with one
            // accidental click on the dedicated VM - hide it (the status label
            // itself stays: it shows worker/login state).
            if (btnClose != null) btnClose.Visible = false;

            Log("[DDL-ONLY] UI restricted: General tab only, banner shown, form Close button hidden.");
        }

        // ---- Mart auto-login state (DDLGENERATOR flavor) ----

        private bool _martLoginVerified;
        private bool _martLoginInProgress;
        private DateTime _martLoginNextTryUtc = DateTime.MinValue;  // backoff after a failed config-read or login
        // Last successful Mart activity (login, keep-alive ping, or a finished
        // job). Drives the keep-alive ping. Stamped on job COMPLETION
        // (user decision 2026-07-12), not job start.
        private DateTime _lastMartActivityUtc = DateTime.MinValue;
        // Keep-alive ping state. _keepAliveMinutes is refreshed live from
        // DDL_GENERATION_CONF on each login and each ping (user: read live), so
        // an admin edit takes effect within one interval.
        private bool _martKeepAliveInProgress;
        private int _keepAliveMinutes = 5;

        // Bootstrap model (watcher-launched, closed on first tick). The marker
        // is the bootstrap file name the watcher passes to erwin.exe; keep it
        // in sync with the watcher + installer (installer/assets/ddlgen-bootstrap.erwin).
        private bool _bootstrapHandled;
        private const string DdlBootstrapMarker = "ddlgen-bootstrap";

        // Self-healing restart (job 2026-07-13): the Mart server enforces an
        // absolute session lifetime (~4h observed); no keep-alive extends it,
        // and an in-place drop -> "Access Denied" modal -> stalled worker ->
        // erwin crash. So the DDL-generator restarts erwin for a fresh session
        // BEFORE the timeout (proactive) or on a detected drop (reactive); the
        // watcher relaunches it. Restart only happens while idle.
        private DateTime _martLoginTimeUtc = DateTime.MinValue;
        private bool _restartRequested;
        private const int MartSessionMaxAgeMinutes = 210; // 3.5h - safe margin below the ~4h server timeout

        private const int MartLoginRetryBackoffSec = 60;

        /// <summary>
        /// Non-blocking Mart login driver, called from the worker tick while
        /// login is not yet verified. Reads DDL_GENERATION_CONF FRESH on every
        /// attempt (user 2026-07-12: never cache the config - the admin may
        /// correct a wrong value, e.g. the Mart port, and the worker must pick
        /// it up on the next retry WITHOUT restarting erwin), then kicks off
        /// <see cref="MartMartAutomation.ConnectToMart"/> on a BACKGROUND thread
        /// (it pumps 25s+ of dialog waits that must not freeze erwin's UI
        /// thread) and marshals the result back via
        /// <see cref="OnMartLoginComplete"/>. Safe to call every tick: it
        /// no-ops while a login is in flight or inside the backoff window.
        /// </summary>
        private void EnsureMartLogin()
        {
            if (_martLoginInProgress) return;
            if (DateTime.UtcNow < _martLoginNextTryUtc) return;

            // Read config FRESH every attempt - no field cache. A missing /
            // ambiguous / undecryptable row returns null; back off and retry
            // (the admin may still be fixing DDL_GENERATION_CONF).
            DdlWorkerConfig cfg;
            try { cfg = DdlWorkerConfigService.Instance.ReadActiveConfig(Log); }
            catch (Exception ex)
            {
                _martLoginNextTryUtc = DateTime.UtcNow.AddSeconds(MartLoginRetryBackoffSec);
                Log($"[MART-LOGIN] config read failed ({ex.Message}) - retry in {MartLoginRetryBackoffSec}s.");
                UpdateStatus("Waiting for DDL_GENERATION_CONF...", System.Drawing.Color.OrangeRed);
                return;
            }
            if (cfg == null)
            {
                _martLoginNextTryUtc = DateTime.UtcNow.AddSeconds(MartLoginRetryBackoffSec);
                UpdateStatus("No usable DDL_GENERATION_CONF row - Mart login disabled.", System.Drawing.Color.OrangeRed);
                return;
            }

            // Mirror the watcher's erwin-check interval to HKCU (the watcher is a
            // PowerShell process with no DB access - it reads this value there).
            WriteWatcherCheckInterval(cfg.ErwinCheckIntervalSeconds);

            _martLoginInProgress = true;
            UpdateStatus("Logging into Mart...", System.Drawing.Color.Gray);
            Action<string> log = msg =>
            {
                if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                else Log(msg);
            };
            System.Threading.Tasks.Task.Run(() =>
            {
                MartMartAutomation.MartLoginResult result;
                try { result = MartMartAutomation.ConnectToMart(cfg, log); }
                catch (Exception ex)
                {
                    log($"[MART-LOGIN] threw: {ex.GetType().Name}: {ex.Message}");
                    result = MartMartAutomation.MartLoginResult.Failed;
                }
                try { BeginInvoke(new Action(() => OnMartLoginComplete(result, cfg.KeepAliveMinutes))); } catch { /* form closing */ }
            });
        }

        /// <summary>UI-thread continuation after a Mart login attempt finishes.</summary>
        private void OnMartLoginComplete(MartMartAutomation.MartLoginResult result, int keepAliveMinutes)
        {
            _martLoginInProgress = false;
            if (result == MartMartAutomation.MartLoginResult.LoggedIn ||
                result == MartMartAutomation.MartLoginResult.AlreadyConnected)
            {
                _martLoginVerified = true;
                _keepAliveMinutes = keepAliveMinutes;
                _lastMartActivityUtc = DateTime.UtcNow;
                _martLoginTimeUtc = DateTime.UtcNow; // start the session-age clock for the proactive restart
                Log($"[MART-LOGIN] verified ({result}) - worker will start claiming jobs. Keep-alive every {keepAliveMinutes}min.");
                UpdateStatus("Mart connected - DDL worker active.", System.Drawing.Color.Green);
            }
            else
            {
                _martLoginNextTryUtc = DateTime.UtcNow.AddSeconds(MartLoginRetryBackoffSec);
                Log($"[MART-LOGIN] failed - retry in {MartLoginRetryBackoffSec}s.");
                UpdateStatus("Mart login failed - retrying...", System.Drawing.Color.OrangeRed);
            }
        }

        /// <summary>
        /// Keep-alive gate, called from the worker tick after login is verified
        /// and before a job claim. Returns true when a ping was started (the
        /// caller must NOT claim a job this tick). Non-blocking: the ping runs
        /// on a background thread (Mart&gt;Open dialog waits must not freeze the
        /// UI thread). The tick already returned early if a job/pipeline is
        /// active, so a ping and a job can never overlap; IsKeepAliveDue also
        /// re-checks the busy flags defensively.
        /// </summary>
        private bool MaybeStartKeepAlivePing()
        {
            if (_martKeepAliveInProgress) return true; // ping in flight - skip claim

            bool busy = _ddlQueueActive || _martMartPipelineActive || _currentDdlJob != null;
            if (!DdlWorkerConfig.IsKeepAliveDue(_lastMartActivityUtc, DateTime.UtcNow, _keepAliveMinutes,
                    jobActive: busy, pingActive: _martKeepAliveInProgress))
                return false; // not due - proceed to claim

            _martKeepAliveInProgress = true;
            Log($"[MART-KEEPALIVE] due (idle >= {_keepAliveMinutes}min) - pinging Mart session (Mart>Open + Cancel).");
            Action<string> log = msg =>
            {
                if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                else Log(msg);
            };
            System.Threading.Tasks.Task.Run(() =>
            {
                // Refresh the interval live (and detect a dropped session).
                int freshMinutes = _keepAliveMinutes;
                try
                {
                    var cfg = DdlWorkerConfigService.Instance.ReadActiveConfig(log);
                    if (cfg != null)
                    {
                        freshMinutes = cfg.KeepAliveMinutes;
                        WriteWatcherCheckInterval(cfg.ErwinCheckIntervalSeconds); // live-refresh the watcher poll interval too
                    }
                }
                catch (Exception ex) { log($"[MART-KEEPALIVE] config refresh failed ({ex.Message}) - keeping {_keepAliveMinutes}min."); }

                bool alive;
                try { alive = MartMartAutomation.PingMartSession(log); }
                catch (Exception ex) { log($"[MART-KEEPALIVE] threw: {ex.GetType().Name}: {ex.Message}"); alive = false; }

                int minutesLocal = freshMinutes;
                bool aliveLocal = alive;
                try { BeginInvoke(new Action(() => OnKeepAlivePingComplete(aliveLocal, minutesLocal))); } catch { /* form closing */ }
            });
            return true;
        }

        /// <summary>UI-thread continuation after a keep-alive ping finishes.</summary>
        private void OnKeepAlivePingComplete(bool alive, int freshKeepAliveMinutes)
        {
            _martKeepAliveInProgress = false;
            _keepAliveMinutes = freshKeepAliveMinutes; // live-refreshed interval
            if (alive)
            {
                _lastMartActivityUtc = DateTime.UtcNow;
                Log("[MART-KEEPALIVE] session alive - ping OK.");
            }
            else
            {
                // Session dropped. In-place re-login is unsafe: a dropped Mart
                // session leaves erwin showing an "Access Denied" modal that
                // stalls the worker and then crashes erwin (job 2026-07-13).
                // Restart for a clean session instead (watcher relaunches).
                _martLoginVerified = false;
                Log("[MART-KEEPALIVE] session DROPPED - restarting erwin for a fresh Mart session.");
                RequestErwinRestart("Mart session dropped");
            }
        }

        /// <summary>
        /// Restarts erwin for a fresh Mart session (self-healing). One-shot per
        /// process: stops the worker and asks MartMartAutomation to close erwin;
        /// the watcher relaunches it with the bootstrap and logs in cleanly.
        /// Called only while idle (no job in flight).
        /// </summary>
        private void RequestErwinRestart(string reason)
        {
            if (_restartRequested) return;
            _restartRequested = true;
            Log($"[DDL-RESTART] {reason} - closing erwin so the watcher relaunches it with a fresh Mart login.");
            UpdateStatus("Restarting erwin for a fresh Mart session...", System.Drawing.Color.OrangeRed);
            try { StopDdlWorker(); } catch (Exception ex) { Log($"[DDL-RESTART] StopDdlWorker note: {ex.Message}"); }
            try { MartMartAutomation.RequestErwinRestart(Log); }
            catch (Exception ex) { Log($"[DDL-RESTART] restart request failed: {ex.Message}"); }
        }

        /// <summary>
        /// Mirrors the DB's ERWIN_CHECK_INTERVAL_SECONDS to HKCU so the watcher
        /// (a PowerShell process with no DB access) can read it. Best-effort:
        /// the watcher falls back to its own default when the value is absent.
        /// </summary>
        private void WriteWatcherCheckInterval(int seconds)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\EliteSoft\ErwinAddIn\Watcher");
                key?.SetValue("ErwinCheckIntervalSeconds", seconds, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception ex) { Log($"[DDLWORKER] HKCU check-interval write failed: {ex.Message}"); }
        }

        /// <summary>
        /// DDLGENERATOR flavor entry point, called once from ModelConfigForm
        /// load: the worker is ALWAYS on (no checkbox, no HKCU flag - both
        /// removed 2026-07-12), gated only by the single-worker mutex.
        /// </summary>
        private void InitializeDdlWorker()
        {
            if (!TryAcquireSingleWorkerMutex())
            {
                Log("[DDLWORKER] NOT started: another erwin in this logon session already owns the DDL worker (single-worker mutex). Close the other erwin and restart this one.");
                UpdateStatus("DDL worker blocked: another erwin instance owns the worker.", System.Drawing.Color.Red);
                return;
            }
            StartDdlWorker();
        }
#endif

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
                // one) is running or erwin is blocked by a modal. (No enabled-check
                // needed: the timer only ever starts in the DDLGENERATOR flavor.)
                if (_ddlQueueActive) return;            // Running: pipeline in flight

                // Cleanup / Closing must run even if a modal (the dirty-model "Save
                // Models" prompt) is up - dismissing it is their whole job. So they are
                // handled BEFORE the pipeline-active / modal-blocked guards below.
                if (_ddlWorkerState == DdlWorkerState.Closing) return; // bg close in flight
                if (_ddlWorkerState == DdlWorkerState.Cleanup) { DdlWorkerDoCleanup(); return; }

                if (_martMartPipelineActive) return;    // some pipeline owns erwin

#if DDLGENERATOR
                // Startup Welcome / Start Page dismissal (job 2026-07-12): a
                // cold erwin launch shows a modal Welcome page that DISABLES the
                // main window, stalling every tick at the modal gate below until
                // a human closes it. Pre-login only (the modal here is the
                // startup Welcome, never our Connect dialog), and idle only, so
                // it never fights the login/keep-alive/pipeline flows.
                if (!_martLoginVerified && !_martLoginInProgress && !_martKeepAliveInProgress)
                {
                    try { if (MartMartAutomation.DismissBlockingStartupDialog(Log)) return; }
                    catch (Exception ex) { Log($"[DDL-WELCOME] dismiss threw: {ex.Message}"); }
                }
#endif

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
#if DDLGENERATOR
            // Bootstrap gate: the watcher launches erwin with a throwaway
            // bootstrap model (so the add-in command is enabled and loads on a
            // model-less erwin - S1 spike). Close it FIRST so erwin is
            // model-less and the worker can open job models. One-shot: once no
            // bootstrap is active (closed, or a manual/dev run without one),
            // the gate is done.
            if (!_bootstrapHandled)
            {
                if (MartMartAutomation.CloseBootstrapModelIfActive(DdlBootstrapMarker, Log))
                    return; // bootstrap being closed - re-check next tick
                _bootstrapHandled = true;
            }

            // Mart auto-login gate: the DDL-generator instance must be logged
            // into Mart before it can open any model. Until login is verified,
            // do NOT claim jobs (a claim would just fail to open + churn the
            // transient-retry). EnsureMartLogin is non-blocking (login runs on
            // a background thread); it flips _martLoginVerified when done.
            if (!_martLoginVerified)
            {
                EnsureMartLogin();
                return;
            }

            // Proactive self-healing restart: the Mart session has a hard
            // server-side lifetime (~4h). Before it expires, restart erwin for
            // a fresh session while idle (this method only runs on the idle
            // path, so no job is interrupted). Avoids the drop -> Access Denied
            // -> crash path entirely.
            if ((DateTime.UtcNow - _martLoginTimeUtc).TotalMinutes >= MartSessionMaxAgeMinutes)
            {
                RequestErwinRestart($"Mart session age >= {MartSessionMaxAgeMinutes}min (near the server timeout)");
                return;
            }

            // Keep-alive: if the Mart login has been idle for KEEPALIVE_MINUTES,
            // ping it (Mart>Open + Cancel) before claiming. Skips (returns true)
            // this tick's claim while the ping is in flight. Never runs during a
            // job - the tick already returned early on _ddlQueueActive /
            // _martMartPipelineActive above.
            if (MaybeStartKeepAlivePing())
                return;
#endif

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
            _ddlCleanupAttempts = 0; // fresh cap per job lifecycle
            // Whole-job form hide: the pipeline AND the closing Save-Models
            // sweep use raw mouse clicks; the form must never sit under them.
            // Restored by OnDdlWorkerCloseComplete (success or give-up).
            HideFormForAutomation($"DDL worker job {job.Id}");
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
                    // Identical versions: DONE with EMPTY RESULT_DDL. A note like
                    // "-- No differences..." reads as if a DDL was produced and
                    // is misleading to the operator (user 2026-07-11); empty is
                    // the honest "there is nothing to apply" signal. The DONE
                    // status distinguishes this from a FAILED job.
                    DdlQueueService.Instance.WriteResult(job.Id, string.Empty, Log);
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

            // QUIESCE the add-in FIRST (job-5 finding 2026-07-11): the job model's
            // close silently aborted after every Save-Models discard (Mart Offline
            // never raised, window survived) while the pipeline's v1 version child
            // had closed clean seconds earlier. The difference: v1 was closed with
            // monitoring suspended and NO add-in session on it; the job model is
            // closed AFTER the pipeline's finally resumed the reconnect tick +
            // validation walks, with the add-in's SCAPI session still open on the
            // very PU erwin must unwind - in-proc COM refs/dispatches that make
            // erwin abort the close. Stop the timers, suspend the walkers and drop
            // the session so erwin sees a quiet model it can actually close.
            // All idempotent - safe on every retry tick. On give-up/success the
            // reconnect timer is restarted (and validation resumed on give-up,
            // where a human takes over with the model still open).
            try { StopReconnectTimer(); } catch (Exception ex) { Log($"[DDLWORKER] cleanup StopReconnectTimer note: {ex.Message}"); }
            try { _validationCoordinatorService?.SuspendValidation(); } catch (Exception ex) { Log($"[DDLWORKER] cleanup SuspendValidation note: {ex.Message}"); }
            try { _tableTypeMonitorService?.StopMonitoring(); } catch (Exception ex) { Log($"[DDLWORKER] cleanup TableTypeMonitor stop note: {ex.Message}"); }
            try { _validationService?.StopMonitoring(); } catch (Exception ex) { Log($"[DDLWORKER] cleanup ColumnValidation stop note: {ex.Message}"); }
            try { CloseCurrentSession(); } catch (Exception ex) { Log($"[DDLWORKER] cleanup CloseCurrentSession note: {ex.Message}"); }

            Log("[DDLWORKER] cleanup: add-in quiesced (timers stopped, validation suspended, SCAPI session closed) - closing the job model (discard changes) on a background thread...");
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
                _ddlCleanupAttempts++;
                if (_ddlCleanupAttempts < DdlWorkerMaxCleanupAttempts)
                {
                    Log($"[DDLWORKER] cleanup: model not closed (Save Models dismiss aborted / left for manual) - retry next tick (attempt {_ddlCleanupAttempts}/{DdlWorkerMaxCleanupAttempts}).");
                    _ddlWorkerState = DdlWorkerState.Cleanup; // retry (exempt from the modal guard)
                    return;
                }
                // CAP reached (job-4 incident 2026-07-11: the close retried
                // FOREVER while a leftover CC wizard blocked it - erwin was
                // unusable). Give up LOUDLY and go Idle: no more WM_CLOSE /
                // dialog-sweep hammering, erwin stays usable, and the Idle
                // guard refuses new claims until the operator closes the model
                // (choose 'discard/close' - do NOT save) - then the worker
                // resumes on its own. HandleSessionLost below resets the add-in
                // to the disconnected state; its restarted reconnect tick then
                // RE-ADOPTS the still-open model interactively (services
                // re-initialized from scratch) so the human takeover behaves
                // exactly like a fresh attach.
                Log($"[DDLWORKER] cleanup GAVE UP after {DdlWorkerMaxCleanupAttempts} attempts - the job model (and any leftover compare wizard) must be closed MANUALLY without saving. " +
                    "Worker is idle and will resume once erwin is model-less.");
                _ddlCleanupAttempts = 0;
                DdlWorkerActiveUnattended = false; // a human is taking over - re-enable interactive modals
                _ddlWorkerState = DdlWorkerState.Idle;
#if DDLGENERATOR
                _lastMartActivityUtc = DateTime.UtcNow; // job attempt ended - reset keep-alive clock
#endif
                RestoreFormAfterAutomation("worker cleanup gave up - manual close needed");
                HandleSessionLost();
                return;
            }
            // Window is gone. The cleanup quiesce already closed the SCAPI
            // session and stopped the timers; HandleSessionLost is the canonical
            // "model closed" reset (disposes the suspended services, clears
            // _isConnected/_currentModel/_knownLocators, restarts the reconnect
            // timer). Without it the worker would hang in the Idle guard: the
            // reconnect tick early-returns on PU count 0 and the monitoring
            // session-lost callback that used to flip the flags is suspended by
            // the quiesce.
            Log("[DDLWORKER] cleanup done - model window closed; resetting to disconnected so the next job can be claimed.");
            _ddlCleanupAttempts = 0;
            DdlWorkerActiveUnattended = false; // re-enable interactive connect modals
            _ddlWorkerState = DdlWorkerState.Idle;
#if DDLGENERATOR
            // Job COMPLETION resets the keep-alive clock (user decision
            // 2026-07-12: stamp at END). A job that ran longer than the
            // keep-alive interval must NOT trigger a ping the instant it
            // finishes - the just-completed Mart activity IS the keep-alive.
            _lastMartActivityUtc = DateTime.UtcNow;
#endif
            RestoreFormAfterAutomation("worker job finished");
            HandleSessionLost();
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
