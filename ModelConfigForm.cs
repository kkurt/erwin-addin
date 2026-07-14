using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Forms;
using EliteSoft.Erwin.AddIn.Services;
using EliteSoft.MetaAdmin.Services;

namespace EliteSoft.Erwin.AddIn
{
    /// <summary>
    /// Main configuration form for the Elite Soft Erwin Add-In.
    /// Provides model selection, configuration, glossary management, and validation features.
    /// </summary>
    public partial class ModelConfigForm : Form
    {
        #region Constants

        private const string StatusConnected = "Connected";
        private const string StatusDisconnected = "Disconnected";
        private const string StatusConnecting = "Connecting...";
        private const string StatusLoading = "Loading...";

        #endregion

        #region Fields

        private readonly dynamic _scapi;
        private dynamic _currentModel;
        private dynamic _session;
        private bool _isConnected;
        private bool _allowClose = false;
        private readonly List<dynamic> _openModels = new List<dynamic>();
        private string _connectedModelName;
        private bool _globalDataLoaded;
        // Set by ErwinAddIn.Execute() when it shows a splash BEFORE this form
        // is constructed (license check + ctor + Show overhead is ~1.5s).
        // ConnectToModel uses this instead of creating its own loading dialog
        // so we keep ONE splash for the whole startup path. Once consumed it
        // is cleared so subsequent ConnectToModel calls (model switch via
        // reconnect timer, manual reload, etc.) create a fresh dialog.
        private Form _earlySplash;
        // Degraded-mode tracking. _inDegradedMode is true when ConnectToModel
        // succeeded at the SCAPI level but ConfigContext.Initialize failed
        // (model not Mart-bound, or Mart path has no MODEL_CONFIG_MAPPING
        // row). _lastDegradedLocator is the PU locator we degraded on, used
        // by the reconnect timer to ignore subsequent ticks while the same
        // unmapped model stays open and to fire the moment the user closes
        // it and opens a different one (e.g. a Mart-bound model with a
        // valid CONFIG mapping). Without this pair the form sticks in
        // degraded UI forever after the user switches to a good model.
        private bool _inDegradedMode;
        private string _lastDegradedLocator;

        // Race guard for the UDP sync dialog (Phase 5, 2026-05-16). The
        // dialog is opened via BeginInvoke from InitializeModelServices so
        // it does not deadlock Form.Load; a fast model switch could queue
        // a second BeginInvoke while the first dialog is still up. Without
        // this flag two dialogs stack and the second one resolves against
        // a stale UdpSyncEngine. Set true around the BeginInvoke-posted
        // ShowDialog scope; the inner finally always clears it.
        private bool _udpSyncDialogOpen;

        // Connected-mode locator tracking (2026-05-14). Mirror of
        // _lastDegradedLocator for the success path: set after ConnectToModel
        // finishes a non-degraded connect, cleared on disconnect / session
        // loss. The reconnect timer compares this against every open PU's
        // locator on each tick; if a NEW locator shows up (sequential model
        // switch OR side-by-side new model created in the same erwin
        // session) the timer fires ConnectToModel against that locator's
        // index so ConfigContext is re-resolved for the new model.
        // Without this the add-in keeps validating against the original
        // model's config indefinitely, ignoring whatever the user just
        // created.
        private string _lastConnectedLocator;

        // Known-locator set (2026-05-14). Loop fix for side-by-side model
        // scenarios: with a single tracked locator the reconnect timer
        // ping-pongs between two open PUs (whichever we bind to becomes
        // tracked, the other always looks divergent, so the tick flips us
        // back and forth forever). This set holds every PU locator the
        // add-in has already observed at the end of a ConnectToModel cycle;
        // the tick only fires a switch when at least one open PU's locator
        // is NOT in the set - i.e. a genuinely NEW model was opened. Re-
        // populated from PersistenceUnits on every successful connect so a
        // closed PU drops out automatically. Cleared on disconnect and
        // session loss so the next connect starts from scratch.
        private readonly HashSet<string> _knownLocators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Count of PUs open at the end of the last successful connect cycle.
        // The reconnect timer uses this to detect when a PU is CLOSED: closing
        // a side-by-side local model does not introduce a new locator, it only
        // removes one from the set, and the remaining PU keeps the same
        // (empty) locator on r10.10. Without this counter the add-in stayed
        // degraded forever after the user closed the unmapped model and went
        // back to the Mart-bound one (verified 2026-05-14 11:46).
        private int _lastConnectPuCount;

        // Active-window-title locator observed at the end of the last connect
        // cycle. erwin's MDI tab switch (between two open PUs, no count
        // change, no per-PU locator change) is invisible to PU-set diffing -
        // the only signal that "the user is now looking at a different model"
        // is the main-window title flipping to the other model's path.
        // The reconnect timer polls this on every tick (count > 1 only -
        // single-PU runs cannot tab-switch) and forces a reconnect when the
        // current title locator differs from what we recorded. Verified
        // 2026-05-14 11:59: with Mart + side-by-side local Model_12 open,
        // tabbing back to Mart used to leave the add-in stuck in degraded
        // mode because no PU set element changed.
        private string _lastObservedTitleLocator = string.Empty;

        // HWND of the active MDI child (the model tab in front) captured at the
        // last connect. The active MDI child is the ground truth for which model
        // the user is looking at: a modal dialog cannot become the active child,
        // so it cannot mask a real tab switch the way it can steal the main-frame
        // title. A change here is a parse-free switch signal that complements the
        // locator-string compare (regex-independent). Zero when erwin is not on a
        // standard MDI frame (then we fall back to the main-frame title compare).
        private IntPtr _lastActiveMdiChildHwnd = IntPtr.Zero;

        // Locators of Mart-version PUs the DDL pipeline opened ITSELF (the
        // older version loaded as the compare RIGHT side, e.g. v1 in a
        // v4-vs-v1 run). The reconnect tick must treat these as pipeline
        // residue, NEVER as a user model switch: adopting one re-binds the
        // form to the pipeline's copy and UDP sync then dirties it (verified
        // 2026-06-10 02:08 + 02:37 in erwin-addin-debug.log - 6 UDP creates
        // were committed into the leftover v1 copy and the Target combo
        // collapsed to "v1 only"). Registered BEFORE the pipeline posts
        // Mart > Open, removed in the pipeline finally only when the
        // POST-CLOSE PU scan proves the copy is gone; if the copy survives
        // teardown the guard stays armed so ticks keep skipping it until the
        // user closes the leftover tab manually (surfaced via a warning
        // dialog - no silent state).
        private readonly HashSet<string> _pipelineOwnedLocators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // One-shot latch so the disconnected tick logs "only pipeline-owned
        // copies are open" once instead of every 500 ms while the user is
        // still closing the leftover tab. Reset when a real PU is adopted.
        private bool _loggedPipelineOnlyPusOpen;

        // One-shot latch for the stale-PU guard: in-process SCAPI can keep
        // reporting a PersistenceUnit after its model window was closed (the
        // mismatch force-close hits this; so does any GUI close). The adopt
        // path skips such ghosts by trusting the live MDI hierarchy; this latch
        // keeps that "no model window" note off the 500 ms spam loop. Reset
        // when a real model window appears.
        private bool _loggedNoModelWindow;

        // Two-tick debounce for the tab-switch detector's EMPTY title reads.
        // A single '' read can be transient (caption stolen by an appearing
        // dialog - 2026-06-10 crash chain) while a real switch to a local
        // non-Mart tab keeps reading '' on the next tick too.
        private bool _emptyTitleDebouncePending;

        // Diagnostic counter for the tab-switch polling heartbeat; bumps once
        // per reconnect tick when count>1 and rolls over every ~10 s to emit a
        // single [TabPoll] log line. Kept on the form so the heartbeat
        // survives across timer Tick handlers but is reset on disconnect.
        private int _tabPollDebugTickCounter;

        // Services
        private ColumnValidationService _validationService;
        private TableTypeMonitorService _tableTypeMonitorService;

        private ValidationCoordinatorService _validationCoordinatorService;
        private PropertyApplicatorService _propertyApplicatorService;
        private UdpRuntimeService _udpRuntimeService;
        private DependencySetRuntimeService _dependencySetService;

        // Metamodel Property_Type name set collected during the UDP sync
        // engine's WalkModelUdps pass (2026-05-16) and reused by
        // ValidationCoordinator.StartMonitoring so the connect path walks
        // the ~1500-entry collection once, not twice. Null when sync was
        // skipped (DB offline / degraded mode); the coordinator then walks
        // metamodel itself as a fallback. Reset on every connect so a fresh
        // walk happens after model switches.
        private HashSet<string> _cachedPropertyTypeNames;
        // Locator of the last model whose UDP-sync walk actually surfaced UDPs.
        // Used to tell a genuine "model has no UDPs" apart from an incomplete
        // metamodel read on a transient reconnect (Mart Save-As + Cancel), where
        // the walk momentarily returns zero Property_Types for a model that
        // already has them. See RunUdpSyncIfNeeded (2026-06-08).
        private string _udpLastGoodWalkLocator;

        // State tracking
        private Timer _glossaryRefreshTimer;
        private Timer _reconnectTimer;
        // Re-entrancy guard for ReconnectTimer_Tick. The tick handler ends up
        // calling ConnectToModel, whose splash Form.Show + COM Sessions.Open +
        // explicit Application.DoEvents all pump the WinForms message loop;
        // queued WM_TIMER messages from _reconnectTimer fire during that pump
        // and re-enter the handler. Verified from the 2026-05-16 debug log:
        // two ticks 144 ms apart both detected the same divergent locator,
        // each running its own InitializeValidationService -> degraded path
        // -> ShowConfigWarningDialog, producing two identical "no config"
        // popups for a single model open.
        private bool _inReconnectTick;

        // Set true while a Review / Complete Compare pipeline is loading a Mart
        // version into the session. Loading a version makes erwin open a new PU
        // which the reconnect timer would otherwise treat as a model switch and
        // re-init the addin (config reload + "Sync UDP definitions" popup) mid-
        // pipeline - disrupting the automation and risking a form rebuild while
        // BtnAlterWizardProd_Click is still running. ReconnectTimer_Tick early-
        // returns while this is set; the pipeline also stops the timer outright.
        private volatile bool _martMartPipelineActive;
        // One-time-per-addin-session DWM warm-up guard. The first PRODUCTION
        // Generate DDL keeps the Alter Script wizard visible once (so the DWM warms
        // its on-screen surface); every later run is silent. Reset only by an addin
        // reload (new erwin session), which matches the DWM surface lifetime.
        private bool _dwmWarmedThisSession;
        private DateTime? _lastGlossaryRefreshTime;
        private volatile bool _isRefreshingGlossary;

        #endregion

        #region Constructor

        public ModelConfigForm(dynamic scapi)
        {
            _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
            using (AddinLogger.BeginScope("InitializeComponent"))
                InitializeComponent();
            using (AddinLogger.BeginScope("InitializeValidationUI"))
                InitializeValidationUI();
            using (AddinLogger.BeginScope("InitializeGeneralTab"))
                InitializeGeneralTab();
#if DDLGENERATOR
            // DDL-generator flavor: strip the UI down to the General tab +
            // "DDL Generation MODE ON!" banner (defined in the DdlWorker partial).
            using (AddinLogger.BeginScope("ApplyDdlGeneratorUiRestrictions"))
                ApplyDdlGeneratorUiRestrictions();
#endif
            using (AddinLogger.BeginScope("InitializeGlossaryRefreshTimer"))
                InitializeGlossaryRefreshTimer();

            // Focus-stealing diagnostic. User reported (2026-05-09) the
            // ModelConfigForm losing foreground spontaneously while idle - the
            // form pops behind erwin "as if a diagram click happened" without
            // any user input. Hooking Activated/Deactivate logs the foreground
            // change with the new foreground window's hwnd / class / title /
            // pid so the next reproduction tells us exactly which window
            // grabbed focus. Cheap (~1 line per transition); removable later.
            this.Activated += (s, ev) => Log("[FOCUS] form Activated");
            this.Deactivate += (s, ev) =>
            {
                try
                {
                    IntPtr fg = Services.Win32Helper.GetForegroundWindowPublic();
                    var classSb = new System.Text.StringBuilder(128);
                    Services.Win32Helper.GetClassNamePublic(fg, classSb, classSb.Capacity);
                    // Timeout-bounded: this Deactivate diagnostic fires on the UI
                    // thread during foreground-stealing teardown; a raw GetWindowText
                    // to the new foreground window (possibly a hung erwin dialog)
                    // would freeze the UI thread (hang class 2026-06-03).
                    string fgTitle = Services.Win32Helper.GetWindowTextNoHang(fg);
                    uint fgPid = Services.Win32Helper.GetWindowThreadProcessIdPublic(fg);
                    Log($"[FOCUS] form Deactivate -> fg=0x{fg.ToInt64():X} class='{classSb}' title='{fgTitle}' pid={fgPid}");
                }
                catch (Exception ex) { Log($"[FOCUS] Deactivate diag failed: {ex.Message}"); }
            };
        }

        #endregion

        #region Form Lifecycle

        /// <summary>
        /// Hand off a loading-splash form that was created by the caller (e.g.
        /// ErwinAddIn.Execute) BEFORE this form was shown. ConnectToModel will
        /// reuse it instead of creating its own, so the user sees one continuous
        /// splash from add-in start to model connection complete.
        /// </summary>
        internal void AttachEarlySplash(Form splash)
        {
            _earlySplash = splash;
        }

        private void ModelConfigForm_Load(object sender, EventArgs e)
        {
            using (AddinLogger.BeginScope("ModelConfigForm_Load"))
                LoadOpenModels();

#if DDLGENERATOR
            // DDL-generator flavor: the queue worker is ALWAYS on (no checkbox,
            // no HKCU flag) - start polling as soon as the form is up. The
            // per-logon-session mutex inside refuses a second worker instance.
            InitializeDdlWorker();
#endif
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { StopDdlWorker(); } catch (Exception ex) { Log($"[DDLWORKER] stop on close err: {ex.Message}"); }
            CleanupResources();
        }

        #endregion

        #region Model Management

        private void LoadOpenModels()
        {
            using var scope = AddinLogger.BeginScope("LoadOpenModels");
            try
            {
                UpdateConnectionStatus(StatusLoading, Color.Gray);
                _openModels.Clear();
                // Note: previous Application.DoEvents() here was removed
                // 2026-05-14. It was meant to repaint the "Loading..." label
                // before the COM call, but on this STA-shared-with-erwin
                // thread it pumped erwin's entire model-load message queue
                // before returning - up to 40s wait verified in logs. The
                // label render lag (~1 frame) is acceptable; the splash
                // shown by ConnectToModel below provides the real feedback.

                dynamic persistenceUnits;
                using (AddinLogger.BeginScope("scapi.PersistenceUnits"))
                    persistenceUnits = _scapi.PersistenceUnits;

                int puCount;
                using (AddinLogger.BeginScope("PersistenceUnits.Count"))
                    puCount = persistenceUnits.Count;
                AddinLogger.Log($"PersistenceUnits.Count = {puCount}");

                if (puCount == 0)
                {
                    AddinLogger.Log("No models open - starting reconnect timer");
                    // The early splash from ErwinAddIn.Execute is normally
                    // consumed by ConnectToModel below; this branch returns
                    // before reaching it, so close the splash here or it stays
                    // on screen forever (user-reported: manual add-in open with
                    // no model loaded left the splash up, 2026-06-12).
                    CloseEarlySplash("no models open");
                    lblActiveModel.Text = "(Waiting for model...)";
                    UpdateStatus("No models open. Waiting for a model...", Color.Gray);
                    StartReconnectTimer();
                    return;
                }

                using (AddinLogger.BeginScope($"Iterate {puCount} PUs"))
                {
                    for (int i = 0; i < puCount; i++)
                    {
                        dynamic model = persistenceUnits.Item(i);
                        _openModels.Add(model);
                    }
                }

                if (_openModels.Count > 0)
                {
                    ConnectToModel(0);
                }
            }
            catch (Exception ex)
            {
                AddinLogger.Log($"LoadOpenModels FAILED: {ex.GetType().Name}: {ex.Message}");
                // Same leak as the no-model branch: a failure before
                // ConnectToModel leaves the early splash unconsumed - and it
                // would otherwise sit on top of the error dialog.
                CloseEarlySplash("LoadOpenModels failed");
                ShowError($"Failed to load models:\n{ex.Message}", "Connection Error");
            }
        }

        /// <summary>
        /// Closes the early splash handed over by ErwinAddIn.Execute when a
        /// code path returns before <c>ConnectToModel</c> (the normal consumer)
        /// runs. Idempotent; safe when no splash was attached.
        /// </summary>
        private void CloseEarlySplash(string reason)
        {
            var splash = _earlySplash;
            _earlySplash = null;
            if (splash == null || splash.IsDisposed) return;
            try
            {
                splash.Close();
                splash.Dispose();
                AddinLogger.Log($"Early splash closed ({reason}).");
            }
            catch (Exception ex)
            {
                AddinLogger.Log($"CloseEarlySplash({reason}) error: {ex.Message}");
            }
        }

        /// <summary>
        /// Switch to a different model by name (called when erwin active model changes).
        /// </summary>
        private void SwitchToModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return;
            if (string.Equals(modelName, _connectedModelName, StringComparison.OrdinalIgnoreCase)) return;

            Log($"Model switch detected: '{_connectedModelName}' -> '{modelName}'");

            // Refresh PersistenceUnits list
            _openModels.Clear();
            try
            {
                dynamic pus = _scapi.PersistenceUnits;
                for (int i = 0; i < pus.Count; i++)
                    _openModels.Add(pus.Item(i));
            }
            catch (Exception ex)
            {
                Log($"SwitchToModel: Failed to refresh models: {ex.Message}");
                return;
            }

            // Find the target model by name
            for (int i = 0; i < _openModels.Count; i++)
            {
                string name = GetModelName(_openModels[i]) ?? "";
                if (name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                {
                    ConnectToModel(i);
                    return;
                }
            }

            Log($"SwitchToModel: Model '{modelName}' not found in PersistenceUnits");
        }

        private void ConnectToModel(int modelIndex)
        {
            Log($">>> ConnectToModel({modelIndex}) called. Stack: {new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name ?? "?"}");
            using var scope = AddinLogger.BeginScope($"ConnectToModel({modelIndex})");

            // HARD refusal gate (2026-06-10 review fix): never bind a PU the
            // DDL pipeline opened for itself, no matter which call site picked
            // the index (the reconnect tick's disconnected / count-drop /
            // session-lost recovery paths all funnel here). Adopting the
            // pipeline's version copy runs a full re-init and UDP sync INTO
            // that copy (the 02:08/02:38 incidents). Checked BEFORE any state
            // teardown so a refusal leaves the current session untouched.
            // Title fallback OFF: the global window title must never alias
            // the per-PU locator.
            if (modelIndex >= 0 && modelIndex < _openModels.Count)
            {
                string candidateLoc = string.Empty;
                try
                {
                    candidateLoc = Services.PuLocatorReader.Read(
                        _openModels[modelIndex], allowWindowTitleFallback: false) ?? string.Empty;
                }
                catch (Exception ex) { Log($"ConnectToModel: pipeline-guard locator read failed: {ex.Message}"); }
                if (_pipelineOwnedLocators.Contains(candidateLoc))
                {
                    Log($"ConnectToModel REFUSED: PU[{modelIndex}] locator '{candidateLoc}' is a pipeline-owned version copy. Close that tab WITHOUT saving; staying in the current state.");
                    return;
                }
                // erwin's ephemeral Review copy is equally forbidden: it dies
                // with the Review wizard and a binding to it dangles (native
                // AV on the next dispatch - erwin crash 2026-06-10 10:47).
                // Checked unconditionally, not just while the pipeline guard
                // is armed: erwin creates these copies for ITS OWN Review
                // button too.
                if (candidateLoc.IndexOf(";Duplicate=YES", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log($"ConnectToModel REFUSED: PU[{modelIndex}] locator '{candidateLoc}' is erwin's ephemeral ;Duplicate=YES Review copy. Staying in the current state.");
                    return;
                }
            }

            // Show splash IMMEDIATELY. _session.Open() below can take 2-4s on large
            // models; without early splash the user sees a 5s dead-time after
            // opening a model in erwin before any add-in feedback appears.
            // If ErwinAddIn.Execute already opened one before this form was
            // constructed (covering ~1.5s of license + ctor + Show overhead),
            // reuse it; otherwise create a new one. The field is cleared on
            // consume so model-switch / reconnect-timer reconnects produce a
            // fresh dialog.
            Form loadingDialog = _earlySplash;
            if (loadingDialog != null && !loadingDialog.IsDisposed)
            {
                _earlySplash = null;
                UpdateLoadingMessage(loadingDialog, "Please wait...");
                AddinLogger.Log("ConnectToModel: reusing early splash from ErwinAddIn.Execute");
            }
            else
            {
                _earlySplash = null;
                loadingDialog = ShowLoadingDialog("Please wait...");
            }

            try
            {
                // Stop old monitoring BEFORE closing session (prevents COM exception race)
                using (AddinLogger.BeginScope("Stop old monitoring"))
                {
                    if (_validationCoordinatorService != null)
                    {
                        _validationCoordinatorService.OnSessionLost -= HandleSessionLost;
                        _validationCoordinatorService.OnModelChanged -= HandleModelChanged;
                        _validationCoordinatorService.OnModelUdpChanged -= HandleModelUdpChanged;
                        _validationCoordinatorService.StopMonitoring();
                    }
                    _tableTypeMonitorService?.StopMonitoring();
                }

                using (AddinLogger.BeginScope("CloseCurrentSession"))
                    CloseCurrentSession();
                _isConnected = false;
                // Drop the previous connected-locator marker before the new
                // session opens; otherwise a half-completed switch could leave
                // a stale value that confuses the reconnect timer's diff.
                _lastConnectedLocator = null;
                UpdateConnectionStatus(StatusConnecting, Color.Gray);

                // Every connect starts with the Integrate tab hidden; only the
                // config-driven success path re-shows it (when the resolved
                // config has INTEGRATE_ENABLED). This single reset covers the
                // mismatch / config-less / not-enabled exits uniformly.
                SetIntegrateTabVisible(false);

                Application.DoEvents();

                _currentModel = _openModels[modelIndex];
                using (AddinLogger.BeginScope("scapi.Sessions.Add()"))
                    _session = _scapi.Sessions.Add();
                using (AddinLogger.BeginScope("session.Open(model)"))
                    _session.Open(_currentModel);

                _connectedModelName = GetModelName(_currentModel) ?? $"Model {modelIndex + 1}";
                lblActiveModel.Text = _connectedModelName;
                AddinLogger.Log($"Connected to model: {_connectedModelName}");

                _isConnected = true;

                // Read the active PU locator FIRST so the reconnect timer has
                // something to track against on its very next tick - otherwise
                // the long init sequence below (~1.5 s) lets the timer fire
                // with _lastConnectedLocator still empty, which the tick
                // treats as "no model yet" and re-triggers ConnectToModel in
                // a loop. PuLocatorReader's multi-stage fallback handles
                // Mart-bound PUs whose PropertyBag('Locator') is empty on
                // r10.10. RE models legitimately return '' here; the tick's
                // divergence check treats '' == '' as "no change" so the
                // loop is harmless there too.
                //
                // Window-title fallback is gated on PU count: only safe when a
                // SINGLE PU is open (the title is then unambiguously about that
                // PU). With multiple PUs the title still points at whichever
                // tab erwin painted last, which can be a different model from
                // the one we just bound to (verified 2026-05-14 11:07 against
                // Mart + side-by-side local Model_12). In that case we read
                // PU-only and let an empty locator flow into ConfigContext,
                // which correctly drops the add-in into degraded mode and
                // fires the "configuration not found" dialog.
                int openPuCount = 1;
                try { openPuCount = (int)_scapi.PersistenceUnits.Count; }
                catch (Exception ex) { Log($"ConnectToModel: PU count read failed: {ex.Message}"); }
                bool allowTitleFallback = openPuCount <= 1;
                string puLocator = Services.PuLocatorReader.Read(
                    _currentModel,
                    allowTitleFallback,
                    (Action<string>)Log) ?? "";
                Log($"ConnectToModel: PU[{modelIndex}] locator='{puLocator}' (titleFallback={(allowTitleFallback ? "on" : "off")}, openPuCount={openPuCount})");
                _lastConnectedLocator = puLocator;

                // Rebuild the known-locators set from EVERY currently open PU
                // (not just the one we just bound to). The tick uses this to
                // distinguish "user added a brand-new model" from "the tick is
                // looking at another already-open model the add-in just hasn't
                // bound to". Without this, the tick would treat the OTHER open
                // PU as divergent and flip the add-in back and forth between
                // them indefinitely (loop reproduced 2026-05-14 10:50 with a
                // Mart model + a side-by-side RE model). Empty locators are
                // kept in the set with the empty string entry so RE models do
                // not look "new" on every tick either.
                _knownLocators.Clear();
                int pipelineOwnedOpen = 0;
                try
                {
                    dynamic puColl = _scapi.PersistenceUnits;
                    int puCount = puColl.Count;
                    for (int i = 0; i < puCount; i++)
                    {
                        // PU iteration MUST disable the window-title fallback.
                        // The title is a GLOBAL erwin state - using it here
                        // would attribute the active window's locator to every
                        // PU in the collection (verified 2026-05-14 11:07: a
                        // local Model_12 came back as the Mart locator because
                        // the focused diagram tab was still the Mart model),
                        // poisoning the known set and hiding genuine switches.
                        string loc = Services.PuLocatorReader.Read(
                            puColl.Item(i),
                            allowWindowTitleFallback: false) ?? string.Empty;
                        // Skip ephemeral Compare-with-Last-Saved PUs (Review
                        // button creates them with ';Duplicate=YES' suffix).
                        // They should never enter the known set; the
                        // ReconnectTimer_Tick guard also short-circuits on
                        // this suffix, so adding them here would be a no-op
                        // at best and confuse the count-drop detection at
                        // worst.
                        if (loc.IndexOf(";Duplicate=YES", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        // Same exclusion for pipeline-owned version copies
                        // (2026-06-10 review fix): keeping them out of BOTH
                        // the known set and the count baseline below means a
                        // surviving leftover can never skew the tick's
                        // count-drop arithmetic.
                        if (_pipelineOwnedLocators.Contains(loc))
                        {
                            pipelineOwnedOpen++;
                            continue;
                        }
                        _knownLocators.Add(loc);
                    }
                }
                catch (Exception kex)
                {
                    Log($"ConnectToModel: failed to seed known-locators set: {kex.Message}");
                    // Defensive: ensure at least the active locator is recorded
                    // so the tick has SOMETHING to compare against. Skip
                    // ;Duplicate=YES here too on the same rationale.
                    if (puLocator.IndexOf(";Duplicate=YES", StringComparison.OrdinalIgnoreCase) < 0)
                        _knownLocators.Add(puLocator);
                }

                // Record the open PU count so the tick can detect a PU being
                // CLOSED (the locator-diff path only catches PUs being added).
                // Pipeline-owned copies are excluded so their later close does
                // not masquerade as the user closing a model (and the leftover
                // does not mask a REAL close by inflating the baseline).
                _lastConnectPuCount = Math.Max(0, openPuCount - pipelineOwnedOpen);

                // Snapshot the current active-window-title locator so the tick
                // can detect a user-driven MDI tab switch between two open
                // PUs without a PU set change. Reading the title here (not in
                // the tick body alone) ensures the baseline matches the PU we
                // just bound to.
                // Prefer the ACTIVE MDI child caption (ground truth, immune to
                // dialog title theft); fall back to the main-frame title only when
                // erwin is not on a standard MDI frame. Capture the child HWND so
                // the tick can detect a switch parse-free.
                _lastObservedTitleLocator = Services.PuLocatorReader.ReadFromActiveMdiChild(out _lastActiveMdiChildHwnd);
                if (_lastActiveMdiChildHwnd == IntPtr.Zero)
                    _lastObservedTitleLocator = Services.PuLocatorReader.ReadFromWindowTitle() ?? string.Empty;

                // Keep the reconnect timer alive even after a successful
                // connect so it can act as a model-state monitor: the tick
                // now compares each open PU's locator against
                // _lastConnectedLocator and fires a fresh connect when the
                // user creates a NEW model in the same erwin session
                // (side-by-side) or closes this one and opens a different
                // one (sequential). StartReconnectTimer is idempotent
                // (Stop + recreate), so calling it here on every connect is
                // safe whether the timer was already running (we re-armed
                // ourselves) or stopped (first connect after startup).
                StartReconnectTimer();
                UpdateConnectionStatus(StatusConnected, Color.DarkGreen);
                UpdateStatus("Connected to model.", Color.DarkGreen);

                // Skip validations for RE models (temporary models created by
                // Reverse Engineer). RE models share the same shape as a fresh
                // File > New Model (empty locator + auto-name "Model_NN"), so
                // we additionally require openPuCount == 1 to qualify: an RE
                // run replaces the single previous PU, while a side-by-side
                // File > New leaves the existing Mart model open and adds a
                // second PU. Without this guard the switch path was treating
                // every user-created local model as an RE temp and skipping
                // the degraded-mode dialog (verified 2026-05-14 11:31).
                bool isReModel = string.IsNullOrEmpty(puLocator) &&
                    (_connectedModelName.StartsWith("Model_") || _connectedModelName.StartsWith("Model ")) &&
                    openPuCount <= 1;

                if (isReModel)
                {
                    Log($"Skipping validations for RE model '{_connectedModelName}' (openPuCount={openPuCount})");
                    // Source display: same single-line label the normal path uses.
                    lblOpenedModel.Text = $"{_connectedModelName} (with last changes)";
                    return;
                }

                // Update splash text now that we know the model name
                UpdateLoadingMessage(loadingDialog,
                    _globalDataLoaded ? $"Switching to {_connectedModelName}..." : "Loading model services...");

                if (_globalDataLoaded)
                {
                    // Model switch: only reload model-specific services (fast)
                    using (AddinLogger.BeginScope("ReinitializeForModelSwitch"))
                        ReinitializeForModelSwitch();
                }
                else
                {
                    // First connect: full initialization
                    using (AddinLogger.BeginScope("InitializeValidationService"))
                        InitializeValidationService();
                    // Only mark global data loaded when the connect actually
                    // populated it. In degraded mode InitializeValidationService
                    // returns early (no glossary/UDP/etc loaded); flagging it
                    // anyway would fool the next connect into taking the fast
                    // ReinitializeForModelSwitch path and skip the corporate /
                    // glossary load that the new Mart-bound model needs.
                    if (Services.ConfigContextService.Instance.IsInitialized)
                        _globalDataLoaded = true;
                }

                // If ForceClose was triggered during init, stop further processing
                if (_allowClose || this.IsDisposed) return;

            }
            catch (Exception ex)
            {
                _isConnected = false;
                _lastConnectedLocator = null;
                _knownLocators.Clear();
                UpdateConnectionStatus(StatusDisconnected, Color.Red);
                UpdateStatus($"Error: {ex.Message}", Color.Red);
            }
            finally
            {
                if (loadingDialog != null && !loadingDialog.IsDisposed)
                {
                    loadingDialog.Close();
                    loadingDialog.Dispose();
                }
            }
        }

        /// <summary>Updates the big message label inside the splash, if present.</summary>
        // internal so ErwinAddIn.Execute can update the early splash message
        // during license check / form construction phases. Application.DoEvents
        // here is INTENTIONAL and SAFE: limited to repainting our own loading
        // form; the heavy-pump variant that caused the model-load wait (40s
        // outlier) was in LoadOpenModels, removed 2026-05-14. This call only
        // flushes WM_PAINT for our small splash dialog.
        internal static void UpdateLoadingMessage(Form loadingDialog, string newMessage)
        {
            if (loadingDialog == null || loadingDialog.IsDisposed) return;
            try
            {
                foreach (Control root in loadingDialog.Controls)
                    SetLabelText(root, newMessage);
                Application.DoEvents();
            }
            catch { }
        }

        private static void SetLabelText(Control c, string text)
        {
            if (c is Label lbl) { lbl.Text = text; return; }
            foreach (Control child in c.Controls) SetLabelText(child, text);
        }

        private string GetModelName(dynamic model)
        {
            try
            {
                try { return model.Name; } catch { }
                try { return model.Properties("Name").Value; } catch { }
                try
                {
                    string path = model.FilePath;
                    if (!string.IsNullOrEmpty(path))
                        return System.IO.Path.GetFileNameWithoutExtension(path);
                }
                catch { }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Finds the PU index whose locator matches the active window-title
        /// locator (2026-05-14). The mapping is the only signal that survives
        /// erwin r10.10's habit of returning the SAME pu.Name="Model_N" for
        /// every open PU when a side-by-side local model is created next to
        /// a Mart-bound one.
        ///
        /// Title locator forms (post PuLocatorReader.ReadFromWindowTitle):
        ///   "Mart://Mart/&lt;path&gt;?VNO=N"   active tab is a Mart model
        ///   ""                                  active tab is a local model
        ///                                       (title bracket has no Mart://)
        /// Per-PU locator forms (post PuLocatorReader.Read with title
        /// fallback OFF):
        ///   "erwin://Mart://Mart/&lt;path&gt;?&amp;version=N&amp;modelLongId=..."
        ///                                       Mart PU
        ///   ""                                  local-unsaved PU
        ///
        /// Match rule: extract the "Mart://Mart/&lt;path&gt;" stem from both
        /// sides (strip optional "erwin://" prefix + any query string) and
        /// case-insensitive compare. When the title locator is empty, match
        /// the PU whose locator is also empty (the local model).
        ///
        /// Returns -1 when nothing matches; caller is expected to fall back
        /// to a locator-different heuristic before giving up.
        /// </summary>
        private int FindPuIndexMatchingTitleLocator(string titleLocator, dynamic persistenceUnits, int count)
        {
            string titleStem = ExtractMartStem(titleLocator);
            int titleVersion = ExtractLocatorVersion(titleLocator);

            for (int i = 0; i < count; i++)
            {
                string puLoc = string.Empty;
                try { puLoc = Services.PuLocatorReader.Read(persistenceUnits.Item(i), allowWindowTitleFallback: false) ?? string.Empty; }
                catch (Exception ex) { Log($"TabSwitch: PU[{i}] locator read error: {ex.Message}"); }

                // Never select a pipeline-owned version copy (2026-06-10
                // review fix): two versions of one Mart model share the SAME
                // stem, so without this skip the leftover (when it sits at a
                // lower index) would be returned for the USER's model and
                // ConnectToModel would re-init + UDP-sync into the copy.
                if (_pipelineOwnedLocators.Contains(puLoc))
                {
                    Log($"TabSwitch: PU[{i}] skipped (pipeline-owned version copy)");
                    continue;
                }

                // Never select erwin's ephemeral Review copy either: its
                // locator is the ACTIVE model's plus ';Duplicate=YES', i.e.
                // same stem AND same version, so neither the stem nor the
                // version check below can tell it apart. Binding it is fatal:
                // erwin releases the copy when the Review wizard closes and
                // the next dispatch on the dead PU AVs natively (erwin crash
                // 2026-06-10 10:47).
                if (puLoc.IndexOf(";Duplicate=YES", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log($"TabSwitch: PU[{i}] skipped (;Duplicate=YES Review copy)");
                    continue;
                }

                string puStem = ExtractMartStem(puLoc);

                // Both empty -> local-PU match.
                // Both non-empty + equal stem -> Mart-PU match.
                if (string.IsNullOrEmpty(titleStem) && string.IsNullOrEmpty(puStem))
                {
                    Log($"TabSwitch: matched local-unsaved PU[{i}] (both stems empty)");
                    return i;
                }
                if (!string.IsNullOrEmpty(titleStem) &&
                    string.Equals(titleStem, puStem, StringComparison.OrdinalIgnoreCase))
                {
                    // Same stem can mean two open VERSIONS of one Mart model.
                    // When both sides expose a version number, require them to
                    // agree (title carries ?VNO=N, PU carries &version=N) so
                    // tabbing onto v4 never binds a same-stem v1 and vice
                    // versa. Sides without a readable version keep the old
                    // stem-only behavior.
                    int puVersion = ExtractLocatorVersion(puLoc);
                    if (titleVersion > 0 && puVersion > 0 && titleVersion != puVersion)
                    {
                        Log($"TabSwitch: PU[{i}] stem matches but version differs (title v{titleVersion} vs PU v{puVersion}) - continuing");
                        continue;
                    }
                    Log($"TabSwitch: matched PU[{i}] by Mart stem '{puStem}'{(puVersion > 0 ? $" v{puVersion}" : string.Empty)}");
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Normalises a Mart-style locator down to its "Mart://Mart/&lt;path&gt;"
        /// stem so locators from two different sources (active window title vs
        /// PU.PropertyBag) can be compared. Drops the optional leading
        /// "erwin://" prefix that PropertyBag adds and strips any query string
        /// (?VNO=N, ?&amp;version=N&amp;modelLongId=..., etc).
        /// Returns empty for empty input and for inputs that don't look like
        /// Mart locators - the caller treats both empty + non-Mart strings as
        /// "local model" and routes to the PU with an empty locator.
        /// </summary>
        private static string ExtractMartStem(string locator)
        {
            if (string.IsNullOrEmpty(locator)) return string.Empty;
            string s = locator;
            // erwin's PropertyBag locator carries the duplicate "erwin://"
            // prefix; the title-parsed locator does not. Strip so both shapes
            // share the same root.
            if (s.StartsWith("erwin://", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("erwin://".Length);
            // Only Mart locators are meaningful for tab-switch matching. A
            // local model bound via File > New has no locator at all; treat
            // "anything that isn't Mart://" as empty so the empty-vs-empty
            // branch above catches it.
            if (!s.StartsWith("Mart://", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            return s.TrimEnd('/');
        }

        /// <summary>
        /// Extracts the Mart version number from either locator shape:
        /// the title-parsed form ("Mart://...?VNO=N") or the PropertyBag
        /// form ("erwin://Mart://...?&amp;version=N&amp;modelLongId=...").
        /// Returns -1 when no version parameter is present (local models,
        /// malformed locators) so callers can treat "unknown" explicitly.
        /// </summary>
        private static int ExtractLocatorVersion(string locator)
        {
            if (string.IsNullOrEmpty(locator)) return -1;
            var m = System.Text.RegularExpressions.Regex.Match(
                locator, @"[?&](?:VNO|version)=(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : -1;
        }

        /// <summary>
        /// Derives the locator of ANOTHER version of the same Mart model by
        /// swapping the version parameter of the active PU's locator (e.g.
        /// "...?&amp;version=4&amp;..." -> "...?&amp;version=1&amp;...").
        /// erwin r10.10 keeps every other locator component (catalog path,
        /// modelLongId) identical across versions of one model, verified
        /// 2026-06-10 in erwin-addin-debug.log (v4 and v1 PU locators differ
        /// only in version=). Returns null when the input has no version
        /// parameter - the caller must then treat the reconnect guard as
        /// NOT armed and say so in the log instead of guessing.
        /// </summary>
        private static string BuildVersionLocator(string activeLocator, int version)
        {
            if (string.IsNullOrEmpty(activeLocator) || version <= 0) return null;
            string swapped = System.Text.RegularExpressions.Regex.Replace(
                activeLocator, @"([?&]version=)\d+", "${1}" + version,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // The swap must produce a DIFFERENT locator that reads back as the
            // requested version. An unchanged result (no version= parameter,
            // VNO-form input which ExtractLocatorVersion accepts but the swap
            // regex does not, or a same-version input) means "cannot derive":
            // registering it would put the ACTIVE model's own locator into the
            // pipeline guard, and the hard refusal gate in ConnectToModel
            // would then refuse the user's own model. Cross-version callers
            // always pass version != active, so equality is never legitimate.
            if (string.Equals(swapped, activeLocator, StringComparison.OrdinalIgnoreCase)) return null;
            return ExtractLocatorVersion(swapped) == version ? swapped : null;
        }

        /// <summary>
        /// True when the active window-title locator points at a Mart-version
        /// copy the DDL pipeline opened itself (see _pipelineOwnedLocators).
        /// Stem and version are compared separately because the two locator
        /// shapes carry the version under different keys (?VNO=N in the title
        /// vs &amp;version=N in the PropertyBag form), so a plain string
        /// compare can never match them.
        /// </summary>
        private bool IsPipelineOwnedTitleLocator(string titleLocator)
        {
            if (_pipelineOwnedLocators.Count == 0 || string.IsNullOrEmpty(titleLocator)) return false;
            string titleStem = ExtractMartStem(titleLocator);
            int titleVersion = ExtractLocatorVersion(titleLocator);
            if (string.IsNullOrEmpty(titleStem) || titleVersion <= 0) return false;
            foreach (string owned in _pipelineOwnedLocators)
            {
                if (string.Equals(titleStem, ExtractMartStem(owned), StringComparison.OrdinalIgnoreCase)
                    && titleVersion == ExtractLocatorVersion(owned))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// First PU index the reconnect logic may bind to: skips erwin's
        /// ephemeral ;Duplicate=YES copies and every pipeline-owned version
        /// copy. Returns -1 when nothing adoptable is open (the caller must
        /// then wait/disconnect instead of binding pipeline residue). Fast
        /// path: with no guard armed, index 0 - identical to the historical
        /// blind PU-0 pick, so behavior only changes while a leftover exists.
        /// </summary>
        private int FindFirstAdoptablePuIndex(dynamic persistenceUnits, int count)
        {
            if (count <= 0) return -1;
            if (_pipelineOwnedLocators.Count == 0) return 0;
            for (int i = 0; i < count; i++)
            {
                string loc = string.Empty;
                try
                {
                    loc = Services.PuLocatorReader.Read(
                        persistenceUnits.Item(i), allowWindowTitleFallback: false) ?? string.Empty;
                }
                catch (Exception ex) { Log($"FindFirstAdoptablePuIndex: PU[{i}] locator read err: {ex.Message}"); }
                if (loc.IndexOf(";Duplicate=YES", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (_pipelineOwnedLocators.Contains(loc)) continue;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// Dirty gate for the cross-version Review pipeline (2026-06-10,
        /// reworked after review): erwin's Mart > Review refuses to open on a
        /// clean checked-out model. SCAPI dirty probes are unusable for this
        /// gate - none of the Modified/IsDirty-style names exist on the
        /// r10.10 PU (VersionCompareService.ProbeDirty therefore always falls
        /// back to "assume dirty", making a ProbeDirty-based gate inert), and
        /// DirtyBit is proven to diverge from the GUI dirty state. The gate
        /// instead reads the GUI signal erwin itself shows: the '*' in the
        /// active MDI child title, which matched Review's accept/refuse
        /// behavior in every logged incident. Returns TRUE when the pipeline
        /// may launch (dirty, or unknown - erwin stays the final authority
        /// via the refusal-box detection in MartMartAutomation); returns
        /// FALSE only on a POSITIVE clean reading (Mart-titled active child
        /// with no asterisk).
        /// </summary>
        private bool ProbeActiveModelDirtyForReview(Action<string> log)
        {
            bool? dirty = Services.MartMartAutomation.IsActiveMdiChildDirtyByTitle(log);
            log($"[REVIEW] dirty gate: title-asterisk probe = {(dirty == null ? "unknown (proceeding - erwin decides)" : dirty.Value ? "dirty" : "clean")}");
            return dirty != false;
        }

        /// <summary>
        /// Ground-truth diagnostic for tab-switch debugging (2026-05-14).
        /// Logs every signal we could possibly use to decide which open PU
        /// the user is currently looking at:
        ///   - the raw erwin main-window title (full string, no parsing)
        ///   - per-PU index, Name (via GetModelName), raw Locator readings
        ///     from PuLocatorReader (window-title fallback OFF so per-PU
        ///     reads don't all alias to the active title)
        ///   - the add-in's currently bound model name
        ///   - the parsed Mart-title locator (what _lastObservedTitleLocator
        ///     gets written from)
        /// Intended use: collect log evidence in the user-reported "tab-switch
        /// goes unnoticed / lands on wrong PU" repro, decide which signal
        /// uniquely identifies the active tab on r10.10, then build the
        /// matcher around it. Heavy; only called at heartbeat (every 10 s)
        /// and at actual tab-switch firings.
        /// </summary>
        private void DumpDiagnosticsForTabSwitch(string tag, dynamic persistenceUnits, int count)
        {
            try
            {
                // 1. Raw main-window title (no regex, no bracket extraction).
                IntPtr hWnd = Services.Win32Helper.GetErwinMainWindow();
                string rawTitle = hWnd != IntPtr.Zero ? Services.Win32Helper.GetWindowTextNoHang(hWnd) : "(no erwin main window)";
                Log($"{tag}: rawTitle='{rawTitle}'");
                Log($"{tag}: parsedTitleLoc='{Services.PuLocatorReader.ReadFromWindowTitle() ?? string.Empty}' boundName='{_connectedModelName ?? string.Empty}'");

                // 2. Per-PU dump. Locator read MUST disable window-title
                //    fallback - otherwise every PU appears to have the
                //    active-window locator and the dump becomes useless.
                for (int i = 0; i < count; i++)
                {
                    string puName = "(name read failed)";
                    string puLoc = "(loc read failed)";
                    try { puName = GetModelName(persistenceUnits.Item(i)) ?? "(null)"; }
                    catch (Exception ex) { puName = $"(name err: {ex.GetType().Name})"; }
                    try { puLoc = Services.PuLocatorReader.Read(persistenceUnits.Item(i), allowWindowTitleFallback: false) ?? "(null)"; }
                    catch (Exception ex) { puLoc = $"(loc err: {ex.GetType().Name})"; }
                    Log($"{tag}: PU[{i}] name='{puName}' locator='{puLoc}'");
                }
            }
            catch (Exception ex)
            {
                Log($"{tag}: DumpDiagnosticsForTabSwitch FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        #endregion

        #region Reconnect Timer

        private void StartReconnectTimer()
        {
            StopReconnectTimer();
            _reconnectTimer = new Timer { Interval = 500 };
            _reconnectTimer.Tick += ReconnectTimer_Tick;
            _reconnectTimer.Start();
            Log("Reconnect timer started - waiting for model to open...");
        }

        private void StopReconnectTimer()
        {
            if (_reconnectTimer != null)
            {
                _reconnectTimer.Stop();
                _reconnectTimer.Dispose();
                _reconnectTimer = null;
            }
        }

        private void ReconnectTimer_Tick(object sender, EventArgs e)
        {
            // Unified model-state monitor (2026-05-14). The timer is kept alive
            // in every state (disconnected, degraded, connected) and reacts
            // when an open PU's locator diverges from what we last successfully
            // tracked. Four scenarios share this code path:
            //   1. Disconnected -> a model opens                  (tracked = "")
            //   2. Degraded     -> user closes unmapped, opens mapped
            //   3. Connected    -> user closes current, opens different model
            //   4. Connected    -> user creates a new model in the same erwin
            //                      session (old + new PUs both live; we detect
            //                      the new locator and switch to it so
            //                      ConfigContext is re-resolved)
            //
            // Removing the original "if connected && !degraded -> stop" branch
            // was the fix for scenario 4 - the add-in used to keep validating
            // the original model's config indefinitely after a side-by-side
            // create, with no UI hint that anything was stale.
            //
            // Re-entrancy guard: ConnectToModel below pumps the message loop
            // (splash Form.Show, _session.Open COM call, explicit
            // Application.DoEvents) and the next queued WM_TIMER from this
            // same timer fires inside that pump. Without the flag the second
            // tick re-runs the whole detect-and-reconnect body for the same
            // locator and produces a duplicate ShowConfigWarningDialog popup.
            if (_inReconnectTick) return;
            // Suppress all reconnect/re-init while a version-compare pipeline is
            // loading a Mart version (the loaded PU is transient and must not
            // trigger an addin re-init mid-pipeline).
            if (_martMartPipelineActive) return;
            // Defensive backstop: NEVER touch SCAPI while erwin's main window is
            // disabled by a modal dialog (Close Model / Mart Offline / Save
            // Models). A WinForms timer tick is on the STA/UI thread; calling
            // _scapi.PersistenceUnits into a modal-blocked process deadlocks
            // (erwin "Not Responding" - verified 2026-05-29). This guards the
            // residual race where a queued WM_TIMER fires after the pipeline
            // flag clears but a modal still lingers.
            try { if (Services.Win32Helper.IsErwinMainWindowBlockedByModal()) return; } catch { }
            _inReconnectTick = true;
            try
            {
                dynamic persistenceUnits = _scapi.PersistenceUnits;
                int count = persistenceUnits.Count;

                // Pipeline-owned guard maintenance (2026-06-10). While the
                // guard is armed, read every open PU locator ONCE up front:
                //  - self-prune: drop guard entries whose PU is gone (closed
                //    manually or by a late teardown). Runs BEFORE the count==0
                //    early-return so an all-models-closed state clears the
                //    guard too; without the prune a stale entry would make the
                //    add-in ignore a version the user later opens on purpose.
                //  - openGuardedCount: open PUs that are pipeline residue.
                //    Every count comparison below uses effectiveCount (real
                //    models only); otherwise the leftover's +1 masks the user
                //    closing their own model and the add-in stays bound to a
                //    dead PU (review finding, 2026-06-10).
                // Costs one locator read per PU per tick, and only while a
                // guard entry exists at all.
                int openGuardedCount = 0;
                if (_pipelineOwnedLocators.Count > 0)
                {
                    var openLocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < count; i++)
                    {
                        string loc = Services.PuLocatorReader.Read(
                            persistenceUnits.Item(i),
                            allowWindowTitleFallback: false) ?? string.Empty;
                        openLocs.Add(loc);
                        if (_pipelineOwnedLocators.Contains(loc)) openGuardedCount++;
                    }
                    int pruned = _pipelineOwnedLocators.RemoveWhere(l => !openLocs.Contains(l));
                    if (pruned > 0)
                        Log($"Reconnect guard: pruned {pruned} pipeline-owned locator(s) - the version copy is closed; normal model-switch handling restored.");
                }
                int effectiveCount = count - openGuardedCount;

                if (count == 0) return;

                // Disconnected path: we have no connect cycle yet, no known
                // locators are recorded - bind to the first ADOPTABLE PU (not
                // pipeline residue, not a ;Duplicate=YES copy) and let
                // ConnectToModel seed the set. Session-lost recovery funnels
                // here too, so a blind PU-0 bind would adopt the leftover v1
                // and UDP-sync into it (review finding, 2026-06-10).
                if (!_isConnected)
                {
                    // Stale-PU guard (2026-06-22): in-process SCAPI can keep
                    // reporting a PersistenceUnit after its model window was
                    // closed (the DBMS-mismatch force-close, or any GUI close -
                    // the DDL worker's "a model is open" check sees the same
                    // ghost). Adopting that ghost would re-run ConnectToModel
                    // against a dead model - and, on a force-closed mismatch
                    // model, loop warn -> close -> warn. Trust the live MDI
                    // hierarchy: with NO open model window there is nothing real
                    // to adopt, so stay disconnected until a genuine model opens
                    // (which yields a fresh MDI child and re-fires the check).
                    if (!Services.MartMartAutomation.HasActiveModelWindow())
                    {
                        if (!_loggedNoModelWindow)
                        {
                            _loggedNoModelWindow = true;
                            Log("Reconnect: SCAPI reports a PU but erwin has no open model window (stale PU) - staying disconnected until a real model opens.");
                        }
                        return;
                    }
                    _loggedNoModelWindow = false;

                    _openModels.Clear();
                    for (int i = 0; i < count; i++)
                        _openModels.Add(persistenceUnits.Item(i));

                    int adoptIdx = FindFirstAdoptablePuIndex(persistenceUnits, count);
                    if (adoptIdx < 0)
                    {
                        // Only pipeline residue remains open. Latch the log so
                        // the 500 ms tick does not spam it while waiting.
                        if (!_loggedPipelineOnlyPusOpen)
                        {
                            _loggedPipelineOnlyPusOpen = true;
                            Log("Reconnect: only pipeline-owned version copies are open - staying disconnected. Close the leftover tab WITHOUT saving.");
                        }
                        return;
                    }
                    _loggedPipelineOnlyPusOpen = false;
                    Log($"Model detected ({count} open). Connecting to PU[{adoptIdx}]...");
                    ConnectToModel(adoptIdx);
                    return;
                }

                // PU count drop detection: when the effective count (real
                // models only) < known-set size, the user just closed at least
                // one PU. The add-in's _currentModel may now point at the
                // closed PU or a stale CONFIG (verified 2026-05-14 11:46:
                // degraded on Model_12, user closed it and switched focus back
                // to the Mart model, but tick saw every remaining PU's locator
                // empty and treated it as "no change", leaving the add-in
                // stuck in degraded mode). Force a full re-init so
                // ConfigContext re-resolves against whatever REAL PU is still
                // open; with only pipeline residue left, drop to the
                // disconnected state instead of binding the leftover.
                if (effectiveCount < _knownLocators.Count)
                {
                    Log($"PU count dropped {_knownLocators.Count} -> {effectiveCount} (real models; {openGuardedCount} pipeline-owned excluded); clearing _globalDataLoaded and reconnecting so ConfigContext re-resolves.");
                    _globalDataLoaded = false;

                    _openModels.Clear();
                    for (int i = 0; i < count; i++)
                        _openModels.Add(persistenceUnits.Item(i));

                    int adoptIdx = FindFirstAdoptablePuIndex(persistenceUnits, count);
                    if (adoptIdx >= 0)
                    {
                        ConnectToModel(adoptIdx);
                    }
                    else
                    {
                        Log("Reconnect: no adoptable PU remains (only pipeline-owned copies) - dropping to disconnected state. Close the leftover tab WITHOUT saving.");
                        HandleSessionLost();
                    }
                    return;
                }

                // Connected (mapped or degraded) path. The add-in records every
                // PU it has seen at the end of a successful connect cycle in
                // _knownLocators. The tick only fires a switch when an open PU
                // has a locator NOT in the set - i.e. a genuinely new model
                // appeared since the last connect. This avoids the ping-pong
                // loop where a single tracked locator would always classify
                // the OTHER already-open PU as divergent.
                int newIndex = -1;
                string newLocator = null;
                for (int i = 0; i < count; i++)
                {
                    // Same window-title-fallback guard as the seed loop in
                    // ConnectToModel: never let the global active-window
                    // locator masquerade as a per-PU locator during iteration.
                    string loc = Services.PuLocatorReader.Read(
                        persistenceUnits.Item(i),
                        allowWindowTitleFallback: false) ?? string.Empty;

                    // Skip ephemeral Compare-with-Last-Saved PUs that erwin's
                    // Review button creates ('locator;Duplicate=YES'). These
                    // are short-lived clean-mart copies used by erwin's
                    // internal CC pipeline; they should NOT be treated as
                    // genuine new models. Without this guard our reconnect
                    // timer fired ConnectToModel(<dup index>), our session.Open
                    // on the Duplicate caused erwin's MDI to swap views to the
                    // clean side, the user's dirty changes vanished from the
                    // diagram, and erwin's Compare showed no diff because the
                    // active view WAS the Duplicate (verified 2026-05-26 from
                    // erwin-addin-debug.log at 20:33:38: Locator='...;Duplicate=YES'
                    // followed by ConnectToModel(1) -> dirty changes disappeared).
                    // Original PU's session+dirty stays preserved (see memory
                    // reference_review_button_duplicate_yes); we simply leave
                    // it alone.
                    if (loc.IndexOf(";Duplicate=YES", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    // Skip Mart-version copies the DDL pipeline opened itself
                    // (the compare RIGHT side, e.g. v1 in a v4-vs-v1 run).
                    // Same idea as the ;Duplicate=YES guard above: these are
                    // pipeline artifacts, not user model switches. Adopting one
                    // ran a full ConnectToModel re-init against the copy and
                    // UDP sync committed 6 creates INTO it (erwin-addin-debug.log
                    // 2026-06-10 02:08:19 + 02:38:03), dirtying a model the user
                    // never opened. The set is maintained by the cross-version
                    // pipeline (armed pre-open, cleared when teardown proves the
                    // PU is gone).
                    if (_pipelineOwnedLocators.Contains(loc))
                    {
                        continue;
                    }

                    if (!_knownLocators.Contains(loc))
                    {
                        newIndex = i;
                        newLocator = loc;
                        break;
                    }
                }
                if (newIndex < 0)
                {
                    // No NEW locator showed up, but a previously-open PU may
                    // have been closed. PU-close is invisible to the locator
                    // diff above (closing a side-by-side local model does not
                    // introduce a new locator, only removes one), so we cross
                    // check against the saved count. If something went away,
                    // re-bind to PU 0 with a full re-init so ConfigContext is
                    // re-resolved against whatever remains - this is what
                    // pulls the add-in out of degraded mode when the user
                    // closes the unmapped local model and goes back to the
                    // Mart-bound one.
                    if (effectiveCount < _lastConnectPuCount)
                    {
                        Log($"PU closed: count {_lastConnectPuCount} -> {effectiveCount} (real models; {openGuardedCount} pipeline-owned excluded); reconnecting with full re-init.");
                        _globalDataLoaded = false;
                        _openModels.Clear();
                        for (int i = 0; i < count; i++)
                            _openModels.Add(persistenceUnits.Item(i));
                        int adoptIdx = FindFirstAdoptablePuIndex(persistenceUnits, count);
                        if (adoptIdx >= 0)
                        {
                            ConnectToModel(adoptIdx);
                        }
                        else
                        {
                            Log("Reconnect: no adoptable PU remains (only pipeline-owned copies) - dropping to disconnected state. Close the leftover tab WITHOUT saving.");
                            HandleSessionLost();
                        }
                        return;
                    }

                    // Single-active-PU model switch-BACK (2026-06-16): erwin's
                    // in-process SCAPI surfaces only the ACTIVE model's PU, so
                    // the PU count stays 1 across MDI tabs - switching tabs just
                    // swaps the single PU's locator. The new-locator scan above
                    // only reconnects for a NEVER-SEEN locator (NOT in
                    // _knownLocators); switching BACK to a previously-open model
                    // (its locator already in the set) was therefore missed and
                    // the add-in stayed bound to the last-opened model, loading
                    // config for the wrong tab (user-reported 2026-06-16). With a
                    // single adoptable PU visible, that PU IS the active model:
                    // if its locator no longer matches the one we are bound to,
                    // the active model changed - reconnect so ConfigContext
                    // re-resolves. No ping-pong risk (one visible PU has nothing
                    // to oscillate with); the count>1 title detector below is
                    // unaffected. activeLoc empty (an unsaved local PU) is left to
                    // the title detector / PU-close paths - we only act on a
                    // concrete locator change.
                    if (effectiveCount == 1)
                    {
                        // Find the single adoptable PU and compare its locator to
                        // the one we are bound to. Guards mirror the new-locator
                        // scan above EXACTLY (skip empty/unsaved-local, erwin's
                        // ;Duplicate Review copy, and DDL-pipeline-owned copies) -
                        // we do NOT use FindFirstAdoptablePuIndex here because its
                        // _pipelineOwnedLocators-empty shortcut returns PU[0]
                        // without the ;Duplicate check, which could hand us a
                        // Review copy that ConnectToModel then refuses every tick.
                        for (int i = 0; i < count; i++)
                        {
                            string loc = Services.PuLocatorReader.Read(
                                persistenceUnits.Item(i),
                                allowWindowTitleFallback: false) ?? string.Empty;
                            if (string.IsNullOrEmpty(loc)) continue;
                            if (loc.IndexOf(";Duplicate=YES", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (_pipelineOwnedLocators.Contains(loc)) continue;

                            if (!string.Equals(loc, _lastConnectedLocator, StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"Active model switched to a previously-open PU: bound '{_lastConnectedLocator}' -> active '{loc}' (locator already in known set) - reconnecting so ConfigContext re-resolves.");
                                _globalDataLoaded = false;
                                _openModels.Clear();
                                for (int j = 0; j < count; j++)
                                    _openModels.Add(persistenceUnits.Item(j));
                                ConnectToModel(i);
                                return;
                            }
                            // The single real PU IS the bound model - no switch.
                            break;
                        }
                    }

                    // MDI tab-switch detection: with multiple PUs open, the
                    // user can tab between them without any SCAPI-visible
                    // change (PU count and per-PU locators both stay
                    // identical). The only signal is the erwin main-window
                    // title flipping to the other model's path. We compare
                    // that title locator against the snapshot taken on the
                    // last connect; if it changed, the user is now looking at
                    // a different PU and ConfigContext must be re-resolved.
                    // Skipped at count==1 because a single-PU run cannot tab
                    // switch, and the locator-only-diff path already covers
                    // count==2 -> count==1 transitions via PU-close.
                    // effectiveCount (real models only): with one real model
                    // plus a pipeline leftover there is nothing to legally
                    // switch TO - the leftover is guarded and the bound model
                    // is already bound - so the detector would only ever fire
                    // wrongly (e.g. onto the leftover's title).
                    if (effectiveCount > 1)
                    {
                        // Ground truth: the ACTIVE MDI child (the model tab in
                        // front). Its caption cannot be stolen by a modal dialog
                        // the way the main-frame title can, and it carries the
                        // FULL locator (spaces and all). Only when erwin is not on
                        // a standard MDI frame do we fall back to the main-frame
                        // title parse.
                        IntPtr activeChild;
                        string currentTitleLoc = Services.PuLocatorReader.ReadFromActiveMdiChild(out activeChild);
                        if (activeChild == IntPtr.Zero)
                            currentTitleLoc = Services.PuLocatorReader.ReadFromWindowTitle() ?? string.Empty;

                        // Diagnostic heartbeat: log the active locator + child hwnd
                        // every ~10 s so a "tab switch went unnoticed" report is
                        // one-line diagnosable (did the child / locator actually
                        // change, or did we read the wrong one?).
                        _tabPollDebugTickCounter++;
                        if (_tabPollDebugTickCounter >= 20)
                        {
                            _tabPollDebugTickCounter = 0;
                            Log($"[TabPoll] count={count} titleLoc='{currentTitleLoc}' child=0x{activeChild.ToInt64():X} boundName='{_connectedModelName}'");
                        }

                        // Two complementary switch signals: the active MDI child
                        // HWND changed (parse-free, regex-independent) OR the parsed
                        // locator changed. The HWND signal catches a switch even if
                        // a caption fails to parse; the locator signal catches the
                        // fallback (non-MDI) path. Either fires the reconnect.
                        bool hwndSwitched = activeChild != IntPtr.Zero
                            && _lastActiveMdiChildHwnd != IntPtr.Zero
                            && activeChild != _lastActiveMdiChildHwnd;

                        if (hwndSwitched
                            || !string.Equals(currentTitleLoc, _lastObservedTitleLocator, StringComparison.OrdinalIgnoreCase))
                        {
                            // Transient-empty debounce (2026-06-10 crash chain):
                            // ReadFromWindowTitle returned '' for a single read
                            // while the raw title still showed the bound Mart
                            // model (ground truth logged at 10:43:26 - a late
                            // compare wizard was stealing the caption at that
                            // instant); the resulting false tab-switch bound the
                            // ;Duplicate copy and erwin later crashed on the dead
                            // PU. A REAL switch to a local (non-Mart) tab also
                            // reads '', so empty cannot be ignored outright -
                            // instead it must persist across TWO consecutive
                            // ticks (1 s) before it counts. Non-empty changes
                            // keep firing immediately.
                            if (string.IsNullOrEmpty(currentTitleLoc)
                                && !string.IsNullOrEmpty(_lastObservedTitleLocator)
                                && !_emptyTitleDebouncePending)
                            {
                                _emptyTitleDebouncePending = true;
                                return;
                            }
                            _emptyTitleDebouncePending = false;

                            // Tab landed on a Mart-version copy the DDL pipeline
                            // opened itself (its teardown failed to close it).
                            // Re-binding would adopt the copy and UDP sync would
                            // dirty it (2026-06-10 incident), so skip the
                            // reconnect entirely. The title snapshot is still
                            // updated: it logs once instead of every 500 ms tick,
                            // and re-arms the detector so tabbing BACK to the
                            // user's real model fires a normal reconnect.
                            if (IsPipelineOwnedTitleLocator(currentTitleLoc))
                            {
                                Log($"Tab switch onto pipeline-owned version copy ('{currentTitleLoc}') - NOT reconnecting. Close that tab without saving to clean up.");
                                _lastObservedTitleLocator = currentTitleLoc;
                                // Arm BOTH detectors so we log once, not every tick,
                                // and so tabbing BACK to the real model fires normally.
                                _lastActiveMdiChildHwnd = activeChild;
                                return;
                            }

                            Log($"Active tab switch detected: window title locator '{_lastObservedTitleLocator}' -> '{currentTitleLoc}'; reconnecting with full re-init.");
                            DumpDiagnosticsForTabSwitch("[TabSwitch] pre-reconnect ground truth", persistenceUnits, count);
                            _globalDataLoaded = false;
                            _openModels.Clear();
                            for (int i = 0; i < count; i++)
                                _openModels.Add(persistenceUnits.Item(i));

                            // Pick the target PU by LOCATOR, not by name.
                            // 2026-05-14 ground-truth log proved BOTH PUs in
                            // the user's Mart + side-by-side local repro come
                            // back as pu.Name='Model_1' (erwin r10.10 reuses
                            // the same display name for the new local model
                            // and the bound Mart model). Name-diff therefore
                            // selects nothing and the old code fell back to
                            // PU[0] - which is the same Mart PU we are
                            // already bound to, so degraded mode never lifted.
                            //
                            // Per-PU locator from PuLocatorReader (window
                            // title fallback OFF) IS reliable on this build:
                            // Mart PU returns "erwin://Mart://Mart/<path>?...",
                            // local-unsaved PU returns "". The parsed window
                            // title (currentTitleLoc) likewise returns the
                            // Mart stem "Mart://Mart/<path>?VNO=N" when the
                            // active tab is the Mart model, "" when the
                            // active tab is the local model. We match the
                            // two by normalised Mart stem (strip leading
                            // "erwin://" and any query string), and treat
                            // empty locator + empty title as the local-PU
                            // match. This works regardless of how many PUs
                            // share the same display name.
                            int targetIndex = FindPuIndexMatchingTitleLocator(
                                currentTitleLoc, persistenceUnits, count);

                            if (targetIndex < 0)
                            {
                                // No locator match - either the title isn't
                                // a Mart stem and no PU has an empty locator,
                                // or two PUs hit the same Mart stem (rare:
                                // same model opened twice from different
                                // versions). Fall back to "the other PU"
                                // heuristic: pick the FIRST PU index whose
                                // locator differs from the one we are bound
                                // to right now. Still better than blindly
                                // re-selecting PU[0] which is the entrenched
                                // failure mode this branch fixes.
                                string boundLoc = _lastConnectedLocator ?? string.Empty;
                                for (int i = 0; i < count; i++)
                                {
                                    string loc = Services.PuLocatorReader.Read(
                                        persistenceUnits.Item(i),
                                        allowWindowTitleFallback: false) ?? string.Empty;
                                    // "Different from bound" must never select a
                                    // pipeline-owned version copy - with a leftover
                                    // open it is BY DEFINITION locator-different
                                    // (2026-06-10 review fix; a transiently empty
                                    // parsed title lands here and would otherwise
                                    // adopt the copy directly).
                                    if (_pipelineOwnedLocators.Contains(loc))
                                    {
                                        continue;
                                    }
                                    // Same for erwin's ;Duplicate=YES Review copy:
                                    // it differs from the bound locator only by the
                                    // suffix, so it is the FIRST thing this loop
                                    // would pick. Exactly that bound the add-in to
                                    // the Duplicate at 10:43:29 on 2026-06-10; the
                                    // copy was released when the user closed the
                                    // leftover wizard and the dead-PU dispatch
                                    // crashed erwin (coreclr 0xC0000005).
                                    if (loc.IndexOf(";Duplicate=YES", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        continue;
                                    }
                                    if (!string.Equals(loc, boundLoc, StringComparison.OrdinalIgnoreCase))
                                    {
                                        targetIndex = i;
                                        Log($"TabSwitch: locator-different fallback targeting PU[{i}] loc='{loc}' (was bound to '{boundLoc}')");
                                        break;
                                    }
                                }
                            }

                            if (targetIndex < 0)
                            {
                                targetIndex = 0;
                                Log("TabSwitch: no locator-different PU either; last-ditch fallback to PU[0].");
                            }
                            ConnectToModel(targetIndex);
                            return;
                        }

                        // Title matches the snapshot again - clear any pending
                        // empty-title debounce so a later real transition gets
                        // a fresh two-tick window.
                        _emptyTitleDebouncePending = false;

                        // Keep the HWND detector armed/current. Normally a no-op
                        // (same bound child); it heals a connect snapshot taken
                        // while a dialog hid the active child (HWND was Zero) so a
                        // later switch is caught parse-free too.
                        if (activeChild != IntPtr.Zero)
                            _lastActiveMdiChildHwnd = activeChild;
                    }

                    // All open PUs are already in the known set; idle tick.
                    return;
                }

                if (_inDegradedMode)
                    Log($"Reconnect: new PU detected while degraded ('{newLocator}' at index {newIndex}) - reattempting connect.");
                else
                    Log($"Model switch detected: new PU locator '{newLocator}' at index {newIndex} not in known set - reconnecting so ConfigContext is re-resolved.");

                // Force the next ConnectToModel onto the full-init path so
                // ConfigContext.Initialize runs against the new locator.
                // Otherwise ReinitializeForModelSwitch keeps the previous
                // model's resolved CONFIG row alive (verified 2026-05-14 11:21:
                // switch fired, Connected to model: Model_1, but ShowConfigWarningDialog
                // never opened because the cached FIBA_Default mapping was
                // still considered valid). Resetting the flag also re-triggers
                // glossary / naming-standards / predefined-column loads, which
                // is correct because the new model may live under a different
                // corporate root with different data-governance config.
                _globalDataLoaded = false;
                Log("Model switch: cleared _globalDataLoaded so InitializeValidationService runs against the new locator.");

                _openModels.Clear();
                for (int i = 0; i < count; i++)
                    _openModels.Add(persistenceUnits.Item(i));

                if (_openModels.Count > newIndex)
                    ConnectToModel(newIndex);
            }
            catch (Exception ex)
            {
                Log($"Reconnect poll error: {ex.Message}");
            }
            finally
            {
                _inReconnectTick = false;
            }
        }

        #endregion

        #region Validation Service

        /// <summary>
        /// Full initialization: corporate + global data + model-specific services.
        /// Called on first connect.
        /// </summary>
        private void InitializeValidationService(bool closeConfigLessMartModel = true)
        {
            Log("Initializing validation service (full)...");

            // Reset Warnings row at the start of every connect cycle so previous
            // model's warnings don't bleed into this one. AddConnectWarning calls
            // from downstream loaders re-populate it; UpdateGeneralTab renders.
            _connectWarnings.Clear();

            // Reset action-buttons + status messages to "normal connect"
            // defaults. Degraded mode (below) overrides them when the active
            // model has no CONFIG mapping. Without this reset a previous
            // degraded run leaves disabled buttons + the orange "Disabled
            // until ..." DDL status hint visible after the user switches to
            // a Mart-bound model that DOES have a config (bug observed
            // 2026-05-09 10:43: General tab refreshed correctly but DDL
            // Generation tab kept the old warning).
            btnValidateAll.Enabled = true;
            btnAlterWizardProd.Enabled = true;
            btnMartReview.Enabled = true;
            lblDDLStatus.Text = "";
            lblDDLStatus.ForeColor = Color.FromArgb(120, 120, 120);

            // Config guard — resolve CONFIG row from the active model's mart path
            var ctx = ConfigContextService.Instance;
            ctx.OnLog -= Log;
            ctx.OnLog += Log;

            // PU.Locator is unreliable on r10.10 Mart-bound PUs (often ""),
            // so we use the shared fallback chain: direct -> PropertyBag() ->
            // PropertyBag(null,true) -> erwin main-window title.
            // Window-title fallback is only safe when a single PU is open;
            // with multiple PUs the title can belong to a different tab than
            // the one we just bound to, attributing the wrong locator to
            // _currentModel (see matching guard in ConnectToModel).
            int openPuCount = 1;
            try { openPuCount = (int)_scapi.PersistenceUnits.Count; }
            catch (Exception ex) { Log($"InitializeValidationService: PU count read failed: {ex.Message}"); }
            bool allowTitleFallback = openPuCount <= 1;
            string locator = PuLocatorReader.Read(_currentModel, allowTitleFallback, (Action<string>)Log);
            Log($"PuLocatorReader returned: '{locator}' (length={locator.Length}, titleFallback={(allowTitleFallback ? "on" : "off")}, openPuCount={openPuCount})");

            bool ok;
            using (AddinLogger.BeginScope("ConfigContext.Initialize"))
                ok = ctx.Initialize(locator);
            if (!ok)
            {
#if DDLGENERATOR
                // DDL-generator (unattended): a config-less model is the
                // bootstrap (about to be closed) or a transient pre-job state.
                // NEVER pop the "Configuration Warning" modal - nobody can click
                // OK on the worker VM and it disables erwin's main frame,
                // stalling the worker (user requirement 2026-07-13). Degrade
                // silently; the worker's bootstrap gate / job open handles the
                // model, and a config-mapped job model takes the normal path
                // (ok==true) instead.
                Log("[DDL-ONLY] config-less model - suppressing Configuration Warning (unattended worker).");
                btnValidateAll.Enabled = false;
                btnAlterWizardProd.Enabled = false;
                btnMartReview.Enabled = false;
                // Track degraded state so the reconnect timer recovers when a
                // config-mapped job model replaces this config-less one (the
                // bootstrap), same as the interactive degraded path.
                _inDegradedMode = true;
                _lastDegradedLocator = locator ?? "";
                using (AddinLogger.BeginScope("UpdateGeneralTab(ddlgen-config-less)"))
                    UpdateGeneralTab();
                StartReconnectTimer();
                return;
#else
                // No CONFIG resolved for the active model. Two cases, handled
                // differently (2026-06-22):
                //   * Mart-bound model with NO CONFIG mapping -> same policy as a
                //     DBMS mismatch: warn (with the copy-the-path modal so the user
                //     can register it in Admin) and CLOSE it. A reopen re-runs this
                //     check (the reconnect adopt path's HasActiveModelWindow guard
                //     handles the lingering stale PU).
                //   * Local-file (.erwin / non-Mart) model -> keep the existing
                //     degraded mode (form stays open, action buttons disabled): the
                //     user may have opened it on purpose for the non-validation
                //     surfaces, and 'copy the path into Admin' is meaningless for it.
                // Closing is deferred off this connect/Load message: a synchronous
                // ForceClose here disposed the form mid-Show(), surfacing as "Cannot
                // access a disposed object" from Execute().
                string reason = ctx.LastError
                    ?? "No configuration is defined for the model you are trying to load. Add-in controls will be disabled.";
                string contextPath = ctx.LastErrorPath ?? "";
                Log($"Config not resolved: {reason} (path='{contextPath}').");

                // Action buttons would touch SCAPI on the active PU; disable them.
                // (On a NON-Mart PU, dynamic-dispatch SCAPI methods NULL-deref deep
                // in EM_GDM/mfc140 and crash the host - verified 2026-05-08 against a
                // PowerDesigner-imported local .erwin. The model-close path below is
                // pure Win32, so it is safe for the Mart case.)
                btnValidateAll.Enabled = false;
                btnAlterWizardProd.Enabled = false;
                btnMartReview.Enabled = false;

                // closeConfigLessMartModel is false on the manual "Reload Config"
                // path: the user is already working in this open model, so a reload
                // that lands on config-less must NOT yank it from under them
                // (possible unsaved work). It stays in degraded mode instead - only
                // a fresh CONNECT to a config-less Mart model closes it.
                bool isMartBound = !string.IsNullOrEmpty(ConfigContextService.ParseMartPath(locator));
                // Only a model whose mart path RESOLVED against a reachable DB but has
                // no MODEL_CONFIG_MAPPING row is genuinely config-less: ConfigContext
                // sets LastErrorPath to that path on that branch only. A DB-ACCESS
                // failure - the config DB password could not be decrypted on this
                // account/profile (DPAPI "Key not valid for use in specified state"),
                // or the DB was unreachable - leaves LastErrorPath empty: we do NOT
                // know whether a mapping exists, so closing the user's model would be
                // wrong. (Regression 2026-06-23: a transient crypto blip closed a
                // perfectly mapped model and killed validation for the whole session,
                // while the same model worked on accounts where DPAPI decrypted.)
                bool resolvedButUnmapped = !string.IsNullOrEmpty(contextPath);
                bool dbAccessError = isMartBound && !resolvedButUnmapped;
                if (isMartBound && resolvedButUnmapped && closeConfigLessMartModel)
                {
                    Log("Config not resolved for a Mart-bound model (no CONFIG mapping) - warning + closing.");
                    UpdateStatus("No CONFIG mapping - closing model.", Color.Red);
                    lblDDLStatus.Text = "No CONFIG mapping for this Mart model - closing.";
                    lblDDLStatus.ForeColor = Color.Red;
                    // We are closing, not running degraded - clear any stale degraded
                    // markers (e.g. from a prior local-file model) so the reconnect
                    // timer's adopt path, not its degraded-recovery path, handles the
                    // reopen.
                    _inDegradedMode = false;
                    _lastDegradedLocator = null;
                    // _isConnected is still true (set in ConnectToModel before this
                    // method) and _lastConnectedLocator points at this model, so the
                    // reconnect timer treats it as bound and will NOT re-adopt it
                    // while the warning modal is up - no warn->close->warn loop. The
                    // close continuation then forgets it so a reopen re-runs this
                    // check.
                    // Refresh the General tab so the Config row shows "(no config)"
                    // instead of the previous model's config name - otherwise it
                    // contradicts the red "No CONFIG mapping" status the user sees
                    // (reported 2026-07-07: Config row kept the last-loaded config
                    // while the status bar said no config).
                    using (AddinLogger.BeginScope("UpdateGeneralTab(config-less-close)"))
                        UpdateGeneralTab();
                    // The model is being closed - drop the stale "Model" + glossary
                    // rows so they don't keep showing the just-closed model.
                    ResetActiveModelDisplay();
                    ShowConfigWarningAndCloseModel();
                    StartReconnectTimer();
                    return;
                }

                // Degraded mode (kept open): a local-file (non-Mart) model, OR a
                // Mart-bound model reached via a manual Reload Config. Kept for the
                // non-validation surfaces (General tab, log link, version compare
                // for OTHER models) - and, on reload, so the user's open model is
                // never yanked from under them.
                Log($"Config not resolved - degraded mode (kept open). isMartBound={isMartBound}, fromReload={!closeConfigLessMartModel}, dbAccessError={dbAccessError}.");
                if (dbAccessError)
                {
                    UpdateStatus("Connected (configuration database unreachable - validation disabled).", Color.Red);
                    lblDDLStatus.Text = "Cannot reach the configuration database - reopen the model or contact your administrator.";
                }
                else
                {
                    UpdateStatus("Connected (no config: add-in controls disabled).", Color.Red);
                    lblDDLStatus.Text = "Disabled until a Mart-bound model with CONFIG mapping is loaded.";
                }
                lblDDLStatus.ForeColor = Color.Red;
                using (AddinLogger.BeginScope("UpdateGeneralTab(degraded)"))
                    UpdateGeneralTab();
                // Defer the modal so Form.Load can finish + Show() can return: a
                // direct ShowDialog nests a modal pump in the Load handler, the form
                // never finishes painting, and the add-in looks "not loaded".
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (dbAccessError)
                                AddinMessageDialog.Show(
                                    this,
                                    "The add-in could not read its configuration database, so model validation is disabled for this session.\n\n" +
                                    "This is usually a transient connection or credentials problem on this machine or account. Please reopen the model; if it keeps happening, contact your administrator.\n\n" +
                                    $"Details: {reason}",
                                    "Configuration database unavailable",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            else
                                ShowConfigWarningDialog(reason, contextPath);
                        }
                        catch (Exception ex) { Log($"Config warning (deferred) failed: {ex.Message}"); }
                    }));
                }
                catch (Exception ex) { Log($"BeginInvoke for config warning failed: {ex.Message}"); }

                // Track the degraded locator + re-arm the reconnect timer so the
                // form recovers when the user switches to a Mart-bound model with a
                // valid CONFIG (2026-05-09 bug: stuck in degraded UI after switch).
                _inDegradedMode = true;
                _lastDegradedLocator = locator ?? "";
                StartReconnectTimer();
                return;
#endif
            }
            Log($"Config: {ctx.ActiveConfigName} (ID={ctx.ActiveConfigId}), corporate='{ctx.CorporateName ?? "(none)"}', mart='{ctx.MartPath}'");
            // Local-file model with a registered CONFIG (2026-06-13): all
            // validation features run, but every Mart pipeline stays off -
            // Review / Generate DDL drive Mart commands + version children and
            // AV on non-Mart PUs (EM_GDM null deref, verified 2026-05-08).
            if (!ctx.IsMartModel)
            {
                btnAlterWizardProd.Enabled = false;
                btnMartReview.Enabled = false;
                lblDDLStatus.Text = "DDL / Review disabled: local-file model (requires a Mart-hosted model).";
                lblDDLStatus.ForeColor = Color.FromArgb(120, 120, 120);
                Log("Config resolved for a LOCAL model - Mart-only actions disabled, validation active.");
            }
            // Successful config resolution: clear degraded markers so the
            // reconnect timer (if still running) won't immediately re-arm.
            _inDegradedMode = false;
            _lastDegradedLocator = null;
            // Remember the locator we successfully connected to. The reconnect
            // timer (kept alive even in connected mode) compares this against
            // every open PU on each tick and fires a fresh ConnectToModel when
            // a new locator appears - covers both sequential model switches
            // and side-by-side new-model creation.
            _lastConnectedLocator = locator ?? "";

            // DDL-dedicated instance (worker checkbox ON): the add-in must stay
            // completely out of erwin's way - no glossary, naming standards,
            // predefined columns, dependency sets, UDP sync/runtime or
            // validation monitoring. Those interactive services dirtied BOTH
            // sides of a manual Complete Compare (UDP sync creates on every
            // adopted model) and kept walks + a live session on the compare
            // LEFT; the compare hung at "Processing Left Model"
            // (user requirement 2026-07-11). Only ConfigContext (resolved
            // above), the DBMS-mismatch guard and the DDL Generation surfaces
            // are initialized here.
            if (IsDdlDedicatedInstance)
            {
                Log("[DDL-ONLY] DDL-dedicated instance: skipping glossary, naming standards, predefined columns, dependency sets, UDP sync/runtime and ALL validation monitoring.");
                using (AddinLogger.BeginScope("DisposeServices"))
                    DisposeServices();
                // A wrong-platform model would produce wrong DDL - the mismatch
                // guard applies in DDL-only mode too (queues warning + close).
                if (CheckDbmsMismatch())
                {
                    btnValidateAll.Enabled = false;
                    lblPlatformStatus.Text = "DBMS mismatch - operations disabled. Contact the Data Architecture team.";
                    lblPlatformStatus.ForeColor = Color.OrangeRed;
                    return;
                }
                btnValidateAll.Enabled = false; // validation surfaces stay OFF in this mode
                using (AddinLogger.BeginScope("UpdateGeneralTab"))
                    UpdateGeneralTab();
                using (AddinLogger.BeginScope("PopulateVersionCombos"))
                    PopulateVersionCombos();
                Log("[DDL-ONLY] init complete (ConfigContext + DDL Generation tab only).");
                return;
            }

            // Global data (corporate-scoped, not model-specific)
            using (AddinLogger.BeginScope("DisposeServices"))
                DisposeServices();
            GlossaryService.Instance.OnLog -= Log;
            GlossaryService.Instance.OnLog += Log;
            using (AddinLogger.BeginScope("LoadGlossary"))
                LoadGlossary();
            using (AddinLogger.BeginScope("LoadPredefinedColumns"))
                LoadPredefinedColumns();
            using (AddinLogger.BeginScope("LoadDomainDefs"))
                LoadDomainDefs();
            using (AddinLogger.BeginScope("LoadNamingStandards"))
                LoadNamingStandards();

            // Model-specific initialization
            using (AddinLogger.BeginScope("InitializeModelServices"))
                InitializeModelServices();
        }

        /// <summary>
        /// Model-only initialization: model-specific services only.
        /// Called on model switch (corporate + global data already loaded).
        /// </summary>
        private void ReinitializeForModelSwitch()
        {
            Log("Reinitializing for model switch (model-only)...");
            DisposeServices();
            InitializeModelServices();
        }

        /// <summary>
        /// Initialize model-specific services: UDPs, PropertyApplicator, monitoring.
        /// Shared by both full init and model switch.
        /// </summary>
        private void InitializeModelServices()
        {
            // New connect cycle - any previous cached metamodel walk belongs
            // to the previous model and must be discarded.
            _cachedPropertyTypeNames = null;

            // Pre-warm AddInPropertyMetadataService.GetObjectTypes in the
            // background. The first cold DB hit costs ~1.6s (EF first-query
            // cost on a small static MC table); running it concurrently with
            // the 700-900ms EnsureAllUdpsExist metamodel walk lets it finish
            // off the critical path. Result is held in a static cache so
            // PropertyApplicator's later GetObjectTypes() call is a HashSet
            // hit rather than a SQL round-trip.
            var dbPrewarmTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    new AddInPropertyMetadataService(DatabaseService.Instance.BootstrapService).GetObjectTypes();
                }
                catch (Exception ex)
                {
                    AddinLogger.Log($"DB pre-warm failed (best-effort): {ex.GetType().Name}: {ex.Message}");
                }
            });

            // EnsureAllUdpsExist used to walk Property_Type here just to
            // populate _cachedPropertyTypeNames. After the 2026-05-16
            // refactor that responsibility moved into UdpSyncEngine.WalkModelUdps
            // which now returns both the filtered model snapshot AND the
            // full names set in a single walk - saving ~2.2 s on big
            // metamodels. When sync runs (normal mode) the cache is filled
            // by RunUdpSyncIfNeeded below. When sync is skipped (DB
            // offline, degraded mode) the cache stays null and
            // ValidationCoordinator falls back to its own walk via
            // InitializeModelUdpTracking.

            _validationService = new ColumnValidationService(_session);
            btnValidateAll.Enabled = true;

            // Make sure the background pre-warm has had a chance to complete
            // before PropertyApplicator's first metadata call. Wait is bounded
            // (3s) so a stalled DB connection does not deadlock the load - in
            // that case PropertyApplicator falls back to its own DB call.
            using (AddinLogger.BeginScope("Wait DB pre-warm"))
                dbPrewarmTask.Wait(TimeSpan.FromSeconds(3));

            // Gate config-driven setup on a model/config DBMS match. On mismatch
            // CheckDbmsMismatch shows the modal; we then skip PropertyApplicator
            // (no standards/questions loaded) and disable validation, so nothing
            // config-driven runs against a model that does not match its config.
            if (CheckDbmsMismatch())
            {
                // Model/config DBMS mismatch: the model does not belong to this
                // config, so NONE of the config-driven setup may touch it
                // (PropertyApplicator, UDP sync, runtime, monitors, validation).
                // CheckDbmsMismatch has queued the themed warning + model close
                // (deferred off the connect/Load thread), so we return now -
                // nothing config-driven runs against the model before it closes.
                btnValidateAll.Enabled = false;
                lblPlatformStatus.Text = "DBMS mismatch - operations disabled. Contact the Data Architecture team.";
                lblPlatformStatus.ForeColor = Color.OrangeRed;
                return;
            }

            using (AddinLogger.BeginScope("InitializePropertyApplicator"))
                InitializePropertyApplicator();

            // Load dependency sets BEFORE UdpRuntime (so List UDP options are available during creation)
            _dependencySetService = new DependencySetRuntimeService();
            _dependencySetService.OnLog += Log;
            using (AddinLogger.BeginScope("DependencySetService.Load"))
            {
                if (_dependencySetService.Load())
                {
                    Log($"Dependency sets loaded: {_dependencySetService.SetCount} set(s), {_dependencySetService.MappingCount} mapping(s)");
                }
            }

            // Admin -> Model UDP sync (Phase 5, 2026-05-16). Runs BEFORE
            // UdpRuntime.Initialize so the metamodel is consistent before
            // any downstream value-level consumer (ApplyDefaults,
            // ValidationCoordinator) reads it. The sync engine computes the
            // diff in-line; if it finds anything, the user-facing dialog is
            // deferred via BeginInvoke (same pattern as ShowConfigWarningDialog
            // at line ~1066 - direct ShowDialog from Form.Load deadlocks the
            // form's paint cycle, lesson 2026-05-07). Errors (DB offline,
            // metamodel session failure, etc.) skip the sync and surface
            // via AddConnectWarning so the General tab Warnings row tells
            // the user why their definitions did not refresh.
            RunUdpSyncIfNeeded();

            _udpRuntimeService = new UdpRuntimeService(_session, _scapi, _currentModel);
            _udpRuntimeService.OnLog += Log;
            _udpRuntimeService.SetDependencySetService(_dependencySetService);
            using (AddinLogger.BeginScope("UdpRuntimeService.Initialize"))
            {
                if (_udpRuntimeService.Initialize())
                {
                    var objectTypes = string.Join(", ", UdpDefinitionService.Instance.GetLoadedObjectTypes());
                    Log($"UDP runtime initialized: {UdpDefinitionService.Instance.Count} definitions [{objectTypes}]");
                }
                else
                {
                    Log("UDP runtime initialization skipped (no definitions or DB not configured)");
                }
            }

            _tableTypeMonitorService = new TableTypeMonitorService(_session);
            _tableTypeMonitorService.OnLog += Log;
            if (_propertyApplicatorService != null)
                _tableTypeMonitorService.SetPropertyApplicator(_propertyApplicatorService);
            if (_udpRuntimeService.IsInitialized)
                _tableTypeMonitorService.SetUdpRuntimeService(_udpRuntimeService);
            // Phase-1A (2026-05-05): TakeSnapshot removed from startup path - the first
            // CheckForTableTypeChanges call (driven by ValidationCoordinator's tick) now
            // performs a silent baseline. Saves several seconds on large models without
            // changing observable behavior for new/changed entities.
            using (AddinLogger.BeginScope("TableTypeMonitor.StartMonitoring"))
                _tableTypeMonitorService.StartMonitoring();

            _validationCoordinatorService = new ValidationCoordinatorService(_session, _scapi);
            _validationCoordinatorService.OnLog += Log;
            _validationCoordinatorService.OnSessionLost += HandleSessionLost;
            _validationCoordinatorService.OnModelChanged += HandleModelChanged;
            _validationCoordinatorService.OnModelUdpChanged += HandleModelUdpChanged;
            _validationCoordinatorService.OnUdpEditorOpened += HandleUdpEditorOpened;
            _validationCoordinatorService.OnUdpEditorClosed += HandleUdpEditorClosed;
            _validationCoordinatorService.SetTableTypeMonitor(_tableTypeMonitorService);
            if (_udpRuntimeService.IsInitialized)
                _validationCoordinatorService.SetUdpRuntimeService(_udpRuntimeService);
            if (_dependencySetService != null && _dependencySetService.IsLoaded)
                _validationCoordinatorService.SetDependencySetService(_dependencySetService);
            using (AddinLogger.BeginScope("ValidationCoordinator.StartMonitoring"))
                _validationCoordinatorService.StartMonitoring(_cachedPropertyTypeNames);

            using (AddinLogger.BeginScope("LoadTablesComboBox"))
                LoadTablesComboBox();
            UpdateValidationStatus();
            Log("Validation service initialized.");
            using (AddinLogger.BeginScope("UpdateGeneralTab"))
                UpdateGeneralTab();
            using (AddinLogger.BeginScope("PopulateVersionCombos"))
                PopulateVersionCombos();

            // Config-driven success path: this is reached only with a resolved,
            // DBMS-matched config, so it is the one place the Integrate tab may
            // appear (gated again on INTEGRATE_ENABLED inside RefreshIntegrateTab).
            using (AddinLogger.BeginScope("RefreshIntegrateTab"))
                RefreshIntegrateTab();

            // Save baseline DDL at connect time (FEModel_DDL does NOT corrupt PU)
            // Baseline DDL removed - DdlHelper fetches any version from Mart on demand

        }

        /// <summary>
        /// Compute the admin-vs-model UDP diff and, when non-empty, deferred-
        /// show the <see cref="UdpSyncDialog"/> for user consent. Errors are
        /// surfaced via <see cref="AddConnectWarning"/> and DO NOT block model
        /// load - the model still opens, just without UDP definition updates.
        ///
        /// Synchronous within <see cref="InitializeModelServices"/> for the
        /// diff calculation; only the dialog ShowDialog + Apply path is
        /// deferred via BeginInvoke. This matches the existing
        /// <c>ShowConfigWarningDialog</c> pattern (see line ~1066) where
        /// modal interaction during Form.Load deadlocks form painting.
        /// </summary>
        /// <summary>
        /// How the add-in applies admin UDP definition changes on model load,
        /// resolved from the inherit cascade (model CONFIG_PROPERTY -> corporate
        /// CORPORATE_PROPERTY -> default) under the key APPLY_UDP_CHANGES. The
        /// UPPER_SNAKE member names match the values the admin stores, so
        /// GetEffectiveEnum parses them directly. Default is WARN_AND_APPLY.
        /// Replaces the old APPLY_UDP_CHANGES_SILENTLY boolean (2026-06-08).
        /// </summary>
        private enum UdpSyncApplyMode { WARN_AND_APPLY, SILENTLY_APPLY, OFF }

        /// <param name="afterApply">Optional continuation invoked AFTER the diff
        /// is applied to the metamodel, in BOTH policy branches (silent and the
        /// WARN dialog closure). Used by the UDP-editor-close recovery to
        /// restore instance VALUES from session snapshots once the deleted
        /// definitions exist again. Connect-time callers pass null.</param>
        /// <param name="dialogTitle">Optional context-specific title for the
        /// WARN_AND_APPLY dialog (e.g. the deletion-recovery wording). Null =
        /// the generic sync title.</param>
        /// <param name="dialogSubtitle">Optional context-specific subtitle for
        /// the WARN_AND_APPLY dialog. Null = the generic explanation.</param>
        private void RunUdpSyncIfNeeded(Action<UdpDiff> afterApply = null,
            string dialogTitle = null, string dialogSubtitle = null)
        {
            // Never sync UDP definitions into a model whose live DBMS does not
            // match its config (CheckDbmsMismatch): the model does not belong to
            // this config - applying its UDPs would corrupt it - and it is being
            // closed anyway. Defense in depth: the connect path already returns
            // before reaching here on mismatch, but the UDP-editor deletion-
            // recovery path also calls this.
            if (_dbmsMismatch)
            {
                Log("UDP sync skipped: model/config DBMS mismatch.");
                return;
            }

            var ctx = ConfigContextService.Instance;
            if (!ctx.IsInitialized || ctx.ActiveConfigId <= 0)
            {
                Log("UDP sync skipped: no active CONFIG");
                return;
            }

            // Apply-policy (tri-state, replaces the old APPLY_UDP_CHANGES_SILENTLY
            // boolean): OFF skips the sync entirely; SILENTLY_APPLY and
            // WARN_AND_APPLY both apply EVERY change, differing only in whether
            // the user is first shown the read-only notification dialog.
            var udpMode = ctx.GetEffectiveEnum("APPLY_UDP_CHANGES", UdpSyncApplyMode.WARN_AND_APPLY);
            if (udpMode == UdpSyncApplyMode.OFF)
            {
                Log("UDP sync skipped: APPLY_UDP_CHANGES=OFF for this config");
                return;
            }

            // DDL queue worker runs unattended: the WARN_AND_APPLY dialog has no one
            // to dismiss it and would block the worker. Force the silent apply (it
            // still applies + dirties the model, just no dialog). See DdlWorker.
            if (DdlWorkerActiveUnattended && udpMode == UdpSyncApplyMode.WARN_AND_APPLY)
            {
                Log("UDP sync: forcing SILENTLY_APPLY (DDL worker unattended - no dialog)");
                udpMode = UdpSyncApplyMode.SILENTLY_APPLY;
            }

            UdpSyncEngine syncEngine;
            UdpDiff diff;
            try
            {
                syncEngine = new UdpSyncEngine(_session, _scapi, _currentModel, ctx.ActiveConfigId);
                syncEngine.OnLog += Log;
                using (AddinLogger.BeginScope("UdpSyncEngine.FetchSnapshot"))
                {
                    var snapshot = syncEngine.FetchSnapshot();
                    // Filter the model walk to admin-defined names only.
                    // Erwin models can hold ~1500 Property_Type entries while
                    // admin defines a handful; reading 4 tag_* properties on
                    // every entry costs ~18 s via COM marshalling. Filtering
                    // drops detail reads to milliseconds (verified 2026-05-16).
                    var expected = UdpSyncEngine.ExpectedFullNames(snapshot);
                    var walk = syncEngine.WalkModelUdps(expected);

                    // Share the all-names set with the connect-level cache so
                    // ValidationCoordinator does not redo the walk. Saves the
                    // 2.2 s EnsureAllUdpsExist pass we used to run separately.
                    var previousPropertyTypeNames = _cachedPropertyTypeNames;
                    _cachedPropertyTypeNames = walk.AllNames;

                    diff = UdpSyncEngine.ComputeDiff(snapshot, walk.Map);
                    Log($"UDP sync diff: creates={diff.Creates.Count}, updates={diff.Updates.Count}");

                    // Incomplete-metamodel-read guard (2026-06-08): a Mart
                    // "Save As" + Cancel (and similar transient reconnects) fires
                    // this sync while erwin is still reloading the model, so the
                    // walk reads a partial Property_Type set that is MISSING the
                    // UDPs and proposes a phantom "creates=N" for UDPs the model
                    // already has (verified: TableClass, which exists, surfaced as
                    // a Create). Distinguish this from a genuine "model has no
                    // UDPs": fire ONLY when the walk found NONE of the admin UDPs
                    // AND a prior walk for THIS SAME model (same locator) earlier
                    // this session DID surface them. Skip the dialog and keep the
                    // complete cached name set.
                    if (walk.Map.Count == 0
                        && !string.IsNullOrEmpty(_lastConnectedLocator)
                        && string.Equals(_lastConnectedLocator, _udpLastGoodWalkLocator, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"UDP sync: walk surfaced 0 admin UDP(s) but this model already had them earlier this session - treating as an incomplete metamodel read (e.g. Mart Save-As reload) and skipping (phantom creates={diff.Creates.Count}).");
                        _cachedPropertyTypeNames = previousPropertyTypeNames;
                        return;
                    }
                    if (walk.Map.Count > 0)
                        _udpLastGoodWalkLocator = _lastConnectedLocator;

                    // Diagnostic for the recurring "List options changed" false-positive:
                    // dump expected vs current verbatim, with length and per-codeunit hex,
                    // so we can see exactly which character/order differs across runs.
                    // Triggered only when ListValues flag is set, so it stays quiet on
                    // healthy models.
                    foreach (var upd in diff.Updates)
                    {
                        if (!upd.Changes.HasFlag(UdpUpdateChanges.ListValues)) continue;
                        if (upd.AdminUdp == null || upd.ExistingUdp == null) continue;
                        string diagExpected = string.Join(",", upd.AdminUdp.ListOptions.Select(o => o.Value));
                        string diagCurrent = upd.ExistingUdp.CurrentListValues ?? "";
                        Log($"UDP diff DIAG [{upd.FullName}] ListValues:");
                        Log($"  admin   ({diagExpected.Length} chars): '{diagExpected}'");
                        Log($"  model   ({diagCurrent.Length} chars): '{diagCurrent}'");
                        Log($"  admin hex: {string.Join(" ", diagExpected.Select(c => ((int)c).ToString("X4")))}");
                        Log($"  model hex: {string.Join(" ", diagCurrent.Select(c => ((int)c).ToString("X4")))}");
                        Log($"  admin opts ({upd.AdminUdp.ListOptions.Count}): [{string.Join(" | ", upd.AdminUdp.ListOptions.Select(o => $"sort={o.SortOrder} val='{o.Value}'"))}]");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"UDP sync skipped: {ex.Message}");
                AddConnectWarning($"UDP sync skipped: {ex.Message}");
                return;
            }

            if (diff.IsEmpty)
            {
                Log("UDP defs already in sync");
                return;
            }

            // SILENTLY_APPLY: write the diff straight to the metamodel, no
            // dialog. Used by admin-managed shops where UDP definitions are
            // centrally controlled and users should not see a prompt every model
            // open. (mode resolved at the top of this method via the cascade.)
            if (udpMode == UdpSyncApplyMode.SILENTLY_APPLY)
            {
                Log($"UDP sync: SILENTLY_APPLY (creates={diff.Creates.Count}, updates={diff.Updates.Count})");
                ApplyUdpDiffSilently(syncEngine, diff);
                try { afterApply?.Invoke(diff); }
                catch (Exception ex) { Log($"UDP sync afterApply (silent) failed: {ex.Message}"); }
                return;
            }

            // Fall through: WARN_AND_APPLY - show the read-only notification
            // dialog, then apply EVERY change (no per-row opt-out, no Cancel).

            if (_udpSyncDialogOpen)
            {
                // Race guard: a previous BeginInvoke for an earlier connect
                // cycle has a dialog open right now. Drop this trigger so
                // the user does not see two stacked dialogs against stale
                // engines. The earlier dialog runs to completion against
                // its own engine; on the NEXT model open the diff will be
                // recomputed fresh against current state.
                Log("UDP sync dialog already open - skipping this trigger (race guard)");
                return;
            }

            // Capture the engine + diff in locals so the BeginInvoke closure
            // sees the same instances even if a subsequent connect cycle
            // overwrites the field-level ones.
            var engineLocal = syncEngine;
            var diffLocal = diff;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    _udpSyncDialogOpen = true;
                    // Serialize popups (bug 2026-06-14): while the UDP sync dialog
                    // is shown AND the diff is being applied, suspend validation so
                    // the model-required-UDP prompt (CheckModelRequiredUdpsOnce) and
                    // any naming/glossary popup cannot open hidden behind this modal.
                    // The sync modal pumps the message loop, so the coordinator's
                    // timer keeps ticking; without this a RequiredUdpForm opened in
                    // behind the sync dialog and blocked the user. The model-required
                    // check resumes on a later settle tick, after the synced UDPs exist.
                    _validationCoordinatorService?.SuspendValidation();
                    try
                    {
                        // WARN_AND_APPLY: show the changes read-only (no per-row
                        // opt-out, no Cancel), then apply ALL of them. The user is
                        // informed but cannot decline - admin policy. Context
                        // callers (deletion recovery) supply their own wording.
                        UdpSyncDialog.ShowInformational(diffLocal, this, dialogTitle, dialogSubtitle);
                        Log($"UDP sync: WARN_AND_APPLY - applying all (creates={diffLocal.Creates.Count}, updates={diffLocal.Updates.Count})");

                        // Apply blocks the UI thread for the duration of the
                        // metamodel transaction (SCAPI calls are STA-bound).
                        // Show a busy overlay first + DoEvents so the user
                        // sees "please wait" instead of an apparently-frozen
                        // application. Same pattern as Reload Config.
                        Form overlay = null;
                        ApplyResult result;
                        try
                        {
                            overlay = ShowBusyOverlay("Applying config UDP definitions to the model, please wait...");
                            Application.DoEvents();
                            result = engineLocal.Apply(diffLocal);
                        }
                        finally
                        {
                            if (overlay != null && !overlay.IsDisposed)
                            {
                                try { overlay.Close(); } catch (Exception ex) { Log($"UDP sync overlay close failed: {ex.Message}"); }
                            }
                        }

                        if (result.Success)
                        {
                            Log($"UDP sync applied: created={result.CreatedCount}, updated={result.UpdatedCount}");
                        }
                        else
                        {
                            Log($"UDP sync apply FAILED: {result.Error}");
                            AddConnectWarning($"UDP sync apply failed: {result.Error}");
                        }

                        // Recovery continuation (UDP-editor close path): restore
                        // instance values now that the definitions exist again.
                        // Run regardless of partial failure - per-write errors
                        // are logged inside; values for defs that did not make
                        // it back simply fail-and-log.
                        try { afterApply?.Invoke(diffLocal); }
                        catch (Exception cbEx) { Log($"UDP sync afterApply failed: {cbEx.Message}"); }

                        // Cascade refresh AFTER apply so the dependency-set
                        // list values reflect any UDPs the user just
                        // accepted from the dialog. Independent of result
                        // - even on failure we want the runtime cascade to
                        // re-run against whatever made it to the metamodel.
                        try
                        {
                            if (_udpRuntimeService != null && _udpRuntimeService.IsInitialized)
                                _udpRuntimeService.UpdateDependencySetListValues();
                        }
                        catch (Exception ex)
                        {
                            Log($"UDP sync post-apply cascade refresh failed: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"UdpSyncDialog flow error: {ex.Message}");
                        AddConnectWarning($"UDP sync dialog error: {ex.Message}");
                    }
                    finally
                    {
                        _udpSyncDialogOpen = false;
                        // Resume validation so the deferred model-required-UDP
                        // check + naming/glossary popups can surface on the next
                        // settle tick, now that the sync modal is gone.
                        try { _validationCoordinatorService?.ResumeValidation(); }
                        catch (Exception ex) { Log($"UDP sync ResumeValidation failed: {ex.Message}"); }
                    }
                }));
            }
            catch (Exception ex)
            {
                Log($"UdpSync BeginInvoke failed: {ex.Message}");
                AddConnectWarning($"UDP sync deferral failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Silent-apply branch of <see cref="RunUdpSyncIfNeeded"/>: when the
        /// admin enabled "Apply UDP changes silently" for this config, write
        /// the diff straight to the metamodel without showing the dialog.
        /// Status bar reflects the outcome so the user still sees that
        /// something happened. Runs synchronously inside InitializeModelServices
        /// (no BeginInvoke deferral needed - silent apply has no modal
        /// pump so it cannot deadlock Form.Load the way ShowDialog could).
        /// </summary>
        // Re-entrancy guard for the UDP-editor-close recovery: the sync's modal
        // dialog pumps messages, which re-fires the monitor timers.
        private bool _udpEditorRecoveryRunning;

        /// <summary>
        /// Admin-UDP definition recovery ("instant undo", 2026-06-12). erwin's
        /// UDP editor is the only place a UDP DEFINITION (Property_Type) can be
        /// deleted - and deleting it silently wipes every instance value with
        /// it. SCAPI offers no pre-event veto, so the closest achievable to
        /// "deletion not allowed" is: when the UDP editor closes, re-run the
        /// admin UDP sync (recreates any deleted admin definitions, with the
        /// usual APPLY_UDP_CHANGES policy dialog) and then restore the instance
        /// VALUES from the session's tracking snapshots (tables, views, model).
        /// Values for objects never tracked this session cannot be recovered.
        /// </summary>
        /// <summary>
        /// UDP editor OPENED: snapshot admin Table/View UDP values now, while the
        /// definitions still exist, so the close-edge recovery can restore them
        /// even if a definition is deleted (model-UDP values are captured by the
        /// coordinator on the same edge). Silent + best-effort - never blocks.
        /// </summary>
        private void HandleUdpEditorOpened()
        {
            try
            {
                _tableTypeMonitorService?.CaptureUdpRecoverySnapshot();
            }
            catch (Exception ex)
            {
                Log($"HandleUdpEditorOpened error: {ex.Message}");
            }
        }

        private void HandleUdpEditorClosed()
        {
            if (_udpEditorRecoveryRunning)
            {
                Log("UDP editor close: recovery already running - skipped.");
                return;
            }
            _udpEditorRecoveryRunning = true;
            try
            {
                Log("UDP editor closed - checking admin UDP definitions against the model.");
                // Context-specific wording (user request 2026-06-13): the generic
                // "Sync UDP definitions from config?" reads like a config drift;
                // here the cause is a delete/edit of an admin-managed UDP inside
                // the editor, and the dialog announces the undo.
                RunUdpSyncIfNeeded(
                    afterApply: RestoreValuesForCreatedDefs,
                    dialogTitle: "Admin-managed UDP definitions protected",
                    dialogSubtitle: "These UDP definitions are managed by the administrator. The change below is being reverted and the values will be restored.");
            }
            catch (Exception ex)
            {
                Log($"HandleUdpEditorClosed error: {ex.Message}");
            }
            finally
            {
                _udpEditorRecoveryRunning = false;
            }
        }

        /// <summary>
        /// Restores instance values for just-recreated admin UDP definitions
        /// from the session snapshots. Grouped by owner type: Table + View
        /// values live in TableTypeMonitorService's snapshots, Model values in
        /// the coordinator's model-UDP baseline.
        /// </summary>
        private void RestoreValuesForCreatedDefs(UdpDiff diff)
        {
            if (diff == null || diff.Creates.Count == 0) return;

            var tableNames = new List<string>();
            var viewNames = new List<string>();
            var modelNames = new List<string>();
            foreach (var c in diff.Creates)
            {
                if (c == null || string.IsNullOrEmpty(c.UdpName)) continue;
                string ot = c.ObjectType?.Trim() ?? "";
                if (ot.Equals("Table", StringComparison.OrdinalIgnoreCase)) tableNames.Add(c.UdpName);
                else if (ot.Equals("View", StringComparison.OrdinalIgnoreCase)) viewNames.Add(c.UdpName);
                else if (ot.Equals("Model", StringComparison.OrdinalIgnoreCase)) modelNames.Add(c.UdpName);
                // Column-scoped UDP values are not snapshot-tracked per
                // attribute today; their defaults re-seed via the runtime
                // cascade. Logged so the gap is visible, never silent.
                else Log($"UDP recovery: no value-restore path for object type '{ot}' ('{c.UdpName}').");
            }

            Log($"UDP recovery: restoring values for recreated defs (table={tableNames.Count}, view={viewNames.Count}, model={modelNames.Count}).");
            try
            {
                if ((tableNames.Count > 0 || viewNames.Count > 0) && _tableTypeMonitorService != null)
                    _tableTypeMonitorService.RestoreTrackedUdpValues(tableNames, viewNames);
            }
            catch (Exception ex) { Log($"UDP recovery: table/view value restore failed: {ex.Message}"); }
            try
            {
                if (modelNames.Count > 0 && _validationCoordinatorService != null)
                    _validationCoordinatorService.RestoreModelUdpValues(modelNames);
            }
            catch (Exception ex) { Log($"UDP recovery: model value restore failed: {ex.Message}"); }
        }

        private void ApplyUdpDiffSilently(UdpSyncEngine engine, UdpDiff diff)
        {
            try
            {
                var result = engine.Apply(diff);
                if (result.Success)
                {
                    int total = result.CreatedCount + result.UpdatedCount;
                    Log($"UDP sync applied silently: created={result.CreatedCount}, updated={result.UpdatedCount}");
                    UpdateStatus(
                        $"UDP sync applied silently ({total} change{(total == 1 ? "" : "s")}).",
                        Color.DarkGreen);
                }
                else
                {
                    Log($"UDP sync silent apply FAILED: {result.Error}");
                    AddConnectWarning($"UDP sync apply failed: {result.Error}");
                }

                // Cascade refresh - same as dialog path. Independent of
                // apply success/failure so dependency-set values reflect
                // whatever made it into the metamodel.
                try
                {
                    if (_udpRuntimeService != null && _udpRuntimeService.IsInitialized)
                        _udpRuntimeService.UpdateDependencySetListValues();
                }
                catch (Exception ex)
                {
                    Log($"UDP sync silent-apply post-cascade refresh failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"UDP sync silent apply error: {ex.Message}");
                AddConnectWarning($"UDP sync silent apply error: {ex.Message}");
            }
        }


        #region General Tab

        // Labels to update after corporate initialization
        private Label _lblCorporateValue;
        private Label _lblWarningsValue;
        private Label _lblLogPathValue;

        // Warnings raised during the connect cycle (schema mismatch, missing
        // CONFIG mapping, service load failures, ...). Reset on every connect
        // and rendered onto the General tab Warnings row by UpdateGeneralTab.
        private readonly List<string> _connectWarnings = new List<string>();

        // Hidden tabs registry — Ctrl+Shift+RightClick on a tab header hides it,
        // Ctrl+Shift+LeftClick on the copyright label on the General tab restores all.
        // tabGeneral itself is never hidden because the restore mechanism lives on it.
        private readonly List<TabPage> _hiddenTabs = new List<TabPage>();

        private void InitializeGeneralTab()
        {
            var font = new Font("Segoe UI", 9.5f);
            var fontBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var fontTitle = new Font("Segoe UI", 16f, FontStyle.Bold);
            var fontSubtitle = new Font("Segoe UI", 9f);
            var clrAccent = Color.FromArgb(0, 102, 204);
            var clrCardBg = Color.White;
            var clrCardHeader = Color.FromArgb(60, 60, 60);

            // --- Header (title + subtitle stacked, accent underline) ---
            var lblTitle = new Label
            {
                Text = "Elite Soft Erwin AddIn",
                Font = fontTitle,
                ForeColor = clrAccent,
                AutoSize = true,
                Location = new Point(24, 16)
            };
            tabGeneral.Controls.Add(lblTitle);

            var lblSubtitle = new Label
            {
                Text = "Model configuration, glossary status, and quick diagnostics",
                Font = fontSubtitle,
                ForeColor = Color.FromArgb(120, 120, 120),
                AutoSize = true,
                Location = new Point(26, 46)
            };
#if DDLGENERATOR
            lblSubtitle.Text = "Dedicated DDL generation instance - validation surfaces are disabled.";
#endif
            tabGeneral.Controls.Add(lblSubtitle);

            var pnlAccent = new Panel
            {
                Location = new Point(24, 68),
                Size = new Size(64, 3),
                BackColor = clrAccent
            };
            tabGeneral.Controls.Add(pnlAccent);

            var lblCopyright = new Label
            {
                Text = "\u00A9 2026 Elite Soft. All rights reserved.",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(160, 160, 160),
                AutoSize = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point(24, 432)
            };
            lblCopyright.MouseDown += (s, e) =>
            {
                if (Control.ModifierKeys != (Keys.Control | Keys.Shift)) return;

                if (e.Button == MouseButtons.Right)
                {
                    ForceClose();
                }
                else if (e.Button == MouseButtons.Left)
                {
                    // Restore every tab the user hid via Ctrl+Shift+RightClick on its header,
                    // plus the Debug Log tab if a packaged build stripped it at startup.
                    int restored = 0;
                    foreach (var page in _hiddenTabs)
                    {
                        if (page != null && !tabControl.TabPages.Contains(page))
                        {
                            tabControl.TabPages.Add(page);
                            restored++;
                        }
                    }
                    _hiddenTabs.Clear();

                    if (restored > 0)
                        Log($"Restored {restored} hidden tab(s)");

                    // Reveal the diagnostic Step Mode button (created hidden in
                    // packaged builds) so the field user can run the RDP
                    // black-rectangle bisection from the DDL Generation tab.
                    // Stays on the General tab (user request 2026-06-15) - no
                    // auto-jump. (The old DDL-worker checkbox this gesture also
                    // revealed is gone: the worker is a compile-time flavor now.)
                    if (btnStepMode != null && !btnStepMode.Visible)
                    {
                        btnStepMode.Visible = true;
                        Log("Step Mode button revealed on the DDL Generation tab.");
                    }
                }
            };
            tabGeneral.Controls.Add(lblCopyright);

            // Ctrl+Shift+RightClick on a tab header hides that tab. tabGeneral is protected
            // so the restore label on it stays reachable. Restoring any hidden tab is done
            // via Ctrl+Shift+LeftClick on the copyright label above.
            tabControl.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                if (Control.ModifierKeys != (Keys.Control | Keys.Shift)) return;

                for (int i = 0; i < tabControl.TabPages.Count; i++)
                {
                    var rect = tabControl.GetTabRect(i);
                    if (!rect.Contains(e.Location)) continue;

                    var page = tabControl.TabPages[i];
                    if (page == tabGeneral)
                    {
                        Log("General tab cannot be hidden (it hosts the restore control).");
                        return;
                    }

                    _hiddenTabs.Add(page);
                    tabControl.TabPages.Remove(page);
                    Log($"Tab '{page.Text}' hidden. Ctrl+Shift+LeftClick on the copyright label (General tab) to restore.");
                    return;
                }
            };

            // Card sizing constants - kept in sync across the three sections so
            // the layout reads as a single column. Card width is the tab width
            // minus the 24px left/right padding the tabGeneral page already has.
            const int cardX = 24;
            const int cardW = 892;

            // --- Top Card: Repository / Connection / Diagnostics ---
            // 3 rows fit comfortably in 106px (3 * 26 + 28 padding). The bootstrap
            // source (Registry hive + Database) moved to its own "System" card below
            // 2026-06-08 so the resolved-config diagnostics (Config / Warnings / Log)
            // stay separate from where the connection was bootstrapped from.
            const int repoY = 84;
            const int repoH = 106;
            var card = CreateSectionCard("Repository", cardX, repoY, cardW, repoH, clrCardBg, clrCardHeader);
            AddCardRow(card, "Config:", "(not loaded)", fontBold, font, 0, out _, out _lblCorporateValue);
#if !PACKAGED
            // Dev-only: re-run the full validation pipeline (CONFIG row from DB
            // + glossary / UDP / naming-standards / predefined-columns reload)
            // without restarting the addin. Hidden in packaged builds so end
            // users don't trigger the 4-5s reinit cycle by accident.
            int reloadBtnLeft = AddRepositoryReloadButton(card, cardW);
            // Keep the AutoSize label from running under the dev button when a
            // very long "Corp / Config (DBType)" string is rendered.
            _lblCorporateValue.MaximumSize = new Size(reloadBtnLeft - 120 - 8, 0);
            _lblCorporateValue.AutoEllipsis = true;
#endif
            // Warnings row surfaces any service-load failure (schema mismatch,
            // missing rows, ConfigContext degraded mode, ...) so the user does
            // not have to dig through the log file to discover that a
            // background service silently no-op'd. Width is wide because the
            // text can carry multiple semicolon-separated reasons.
            AddCardRow(card, "Warnings:",  "(none)",       fontBold, font, 1, out _, out _lblWarningsValue);
            _lblWarningsValue.MaximumSize = new Size(cardW - 160, 0);
            _lblWarningsValue.AutoSize = true;
            _lblWarningsValue.ForeColor = Color.FromArgb(120, 120, 120);
            // Log file row: clickable link that opens the folder in Explorer
            // with the log file pre-selected. The Debug Log tab was removed
            // 2026-05-07 (UIA event raise from TextBox.AppendText crashed
            // erwin host); the link is the supported way to view the log.
            AddCardRow(card, "Log file:",  AddinLogger.FilePath, fontBold, font, 2, out _, out _lblLogPathValue);
            _lblLogPathValue.ForeColor = clrAccent;
            _lblLogPathValue.Cursor = Cursors.Hand;
            _lblLogPathValue.Click += (s, ev) => OpenLogFolder();
            tabGeneral.Controls.Add(card);

            // --- Middle Card: Model ---
            // The standalone "System / Database" card was removed 2026-07-07: the
            // active database is now shown in the Repository card's Config row
            // (as a "DB:<name>  |  ..." prefix), so a separate card was redundant.
            // The legacy grpModel GroupBox sat here with an etched-border that
            // jarred against the modern card chrome above and below. We unhost
            // its child labels and re-place them inside a section card body so
            // the visual rhythm stays consistent. grpModel itself is no longer
            // added to the tab.
            const int modelY = repoY + repoH + 30 + 16;  // directly below the Repository card
            const int modelH = 44;  // single "Model:" row now that the DBMS line is gone
            var modelCard = CreateSectionCard("Model", cardX, modelY, cardW, modelH, clrCardBg, clrCardHeader);
            var modelBody = modelCard.Tag as Panel;

            grpModel.Controls.Remove(lblModelName);
            grpModel.Controls.Remove(lblActiveModel);
            grpModel.Controls.Remove(lblConnectionStatus);
            grpModel.Controls.Remove(lblPlatformStatus);

            lblModelName.Location = new Point(16, 14);
            lblModelName.Font = font;
            lblModelName.ForeColor = Color.FromArgb(80, 80, 80);
            lblModelName.Text = "Model:";
            modelBody.Controls.Add(lblModelName);

            // Long model names (e.g. "CORE BANKING CASH MANAGEMENT MONEY TRANSFER_EFT")
            // must not run under the right-aligned connection status. Fixed width +
            // AutoEllipsis gives a single-line "...EFT" clip (AutoEllipsis is ignored
            // when AutoSize=true, so AutoSize is OFF here). Width spans from x=120 up
            // to a gap before lblConnectionStatus (which starts at cardW-220).
            lblActiveModel.AutoSize = false;
            lblActiveModel.Location = new Point(120, 14);
            lblActiveModel.Size = new Size((cardW - 220) - 120 - 16, 20);
            lblActiveModel.TextAlign = ContentAlignment.MiddleLeft;
            lblActiveModel.AutoEllipsis = true;
            lblActiveModel.Font = fontBold;
            lblActiveModel.ForeColor = Color.FromArgb(40, 40, 40);
            modelBody.Controls.Add(lblActiveModel);

            lblConnectionStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblConnectionStatus.Location = new Point(cardW - 220, 14);
            lblConnectionStatus.AutoSize = false;
            lblConnectionStatus.Size = new Size(200, 20);
            lblConnectionStatus.TextAlign = ContentAlignment.MiddleRight;
            modelBody.Controls.Add(lblConnectionStatus);

            // The DBMS_VERSION_ID / "N standard(s)" status line (lblPlatformStatus)
            // was removed from the Active Model card 2026-06-08 (user: unnecessary).
            // The label still exists on grpModel (off-tab) so its setters stay
            // harmless; it is simply no longer placed in the visible card.

            tabGeneral.Controls.Add(modelCard);

            // --- Bottom Card: Glossary status (formerly the Glossary tab) ---
            // The glossary is metadata that decorates validation results and
            // table-mapping lookups, so its load state belongs alongside the
            // repo/connection card on the General tab. We surface just two
            // pieces of information: current status and last refresh.
            const int glossY = modelY + modelH + 30 + 16;
            const int glossH = 70;
            var glossCard = CreateSectionCard("Glossary", cardX, glossY, cardW, glossH, clrCardBg, clrCardHeader);
            AddCardRow(glossCard, "Status:",       "(not loaded)", fontBold, font, 0, out _, out lblGlossaryStatus);
            AddCardRow(glossCard, "Last refresh:", "(not yet)",    fontBold, font, 1, out _, out lblLastRefreshValue);
            lblGlossaryStatus.ForeColor = Color.FromArgb(120, 120, 120);
            lblLastRefreshValue.ForeColor = Color.FromArgb(120, 120, 120);
            tabGeneral.Controls.Add(glossCard);

            // Reposition copyright label dynamically below the last card.
            int footerY = glossY + glossH + 30 + 12;
            lblCopyright.Location = new Point(24, footerY);

            // (2026-07-12: the DDL queue-worker on/off checkbox that used to sit on
            // this footer row is GONE - the worker is a compile-time build flavor
            // now (DDLGENERATOR, always on) and normal builds have no worker at all.)

            // --- Footer action: Close erwin ---
            // Destructive action lives in the bottom-right corner, kept apart
            // from the configuration cards so it is hard to hit by accident.
            // Posts WM_CLOSE to erwin's main frame (graceful shutdown - erwin
            // raises its own "Save changes?" prompt for any dirty model).
            const int closeBtnW = 120;
            const int closeBtnH = 30;
            var btnCloseErwin = new Button
            {
                Text = "Close erwin",
                // Right-align to the card column's right edge (cardX + cardW),
                // vertically centred on the copyright baseline.
                Location = new Point((cardX + cardW) - closeBtnW, footerY - 8),
                Size = new Size(closeBtnW, closeBtnH),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(200, 60, 60),
                ForeColor = Color.White,
                Font = fontBold,
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            btnCloseErwin.FlatAppearance.BorderSize = 0;
            // Darken on hover so the destructive intent reads clearly.
            btnCloseErwin.MouseEnter += (s, e) => btnCloseErwin.BackColor = Color.FromArgb(168, 44, 44);
            btnCloseErwin.MouseLeave += (s, e) => btnCloseErwin.BackColor = Color.FromArgb(200, 60, 60);
            btnCloseErwin.Click += BtnCloseErwin_Click;
#if DDLGENERATOR
            // Dedicated worker instance: one accidental click would take down
            // erwin AND the queue worker with it - no buttons in this flavor
            // (user requirement 2026-07-12).
            btnCloseErwin.Visible = false;
#endif
            tabGeneral.Controls.Add(btnCloseErwin);
        }

        /// <summary>
        /// Closes the entire erwin host after explicit confirmation. Routes
        /// through <see cref="Services.Win32Helper.CloseErwinMainWindow"/>, which
        /// posts WM_CLOSE to erwin's main frame so erwin's own save-prompt fires
        /// for any unsaved model (no Process.Kill, no data loss).
        /// </summary>
        private void BtnCloseErwin_Click(object sender, EventArgs e)
        {
            var result = AddinMessageDialog.Show(
                this,
                "This will close erwin completely.\r\n\r\n" +
                "If any open model has unsaved changes, erwin will ask you to save first.\r\n\r\n" +
                "Close erwin now?",
                "Close erwin",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
            {
                Log("Close erwin: cancelled by user.");
                return;
            }

            Log("Close erwin: posting WM_CLOSE to the erwin main frame.");
            bool posted = Services.Win32Helper.CloseErwinMainWindow();
            if (!posted)
            {
                Log("Close erwin: erwin main window not found - nothing to close.");
                AddinMessageDialog.Show(
                    this,
                    "Could not locate the erwin main window. erwin may already be closing.",
                    "Close erwin",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Show a friendly configuration-warning dialog with the relevant
        /// path (locator or mart path) presented in a read-only textbox so
        /// the admin can copy/paste it straight into the Admin panel's
        /// MODEL_CONFIG_MAPPING configuration. Replaces the stock
        /// MessageBox path that rendered the URI inline and was hard to
        /// select. Shown TopMost so it surfaces over the erwin host.
        /// </summary>
        private void ShowConfigWarningDialog(string reason, string path)
        {
            try
            {
                using var dlg = new Form
                {
                    Text = "Configuration Warning",
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false,
                    TopMost = true,
                    ClientSize = new Size(560, 240),
                    BackColor = Color.White,
                    Padding = new Padding(20),
                };

                // Warning icon (SystemIcons.Warning) + heading row.
                var iconBox = new PictureBox
                {
                    Image = SystemIcons.Warning.ToBitmap(),
                    SizeMode = PictureBoxSizeMode.AutoSize,
                    Location = new Point(20, 20),
                };
                dlg.Controls.Add(iconBox);

                var lblHeading = new Label
                {
                    Text = "Add-in loaded with controls disabled",
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(60, 60, 60),
                    AutoSize = true,
                    Location = new Point(70, 22),
                };
                dlg.Controls.Add(lblHeading);

                var lblReason = new Label
                {
                    Text = reason,
                    Font = new Font("Segoe UI", 9.5f),
                    ForeColor = Color.FromArgb(60, 60, 60),
                    AutoSize = false,
                    Size = new Size(460, 60),
                    Location = new Point(70, 50),
                };
                dlg.Controls.Add(lblReason);

                // Path label + selectable read-only textbox + Copy button.
                var lblPathHdr = new Label
                {
                    Text = "Path (copy this into Admin to register the model):",
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(80, 80, 80),
                    AutoSize = true,
                    Location = new Point(20, 130),
                };
                dlg.Controls.Add(lblPathHdr);

                var txtPath = new TextBox
                {
                    Text = path ?? "",
                    Font = new Font("Consolas", 9.5f),
                    ReadOnly = true,
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = Color.FromArgb(248, 248, 248),
                    Location = new Point(20, 152),
                    Size = new Size(430, 24),
                };
                txtPath.GotFocus += (s, e) => txtPath.SelectAll();
                dlg.Controls.Add(txtPath);

                var btnCopy = new Button
                {
                    Text = "Copy",
                    Location = new Point(456, 150),
                    Size = new Size(80, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                };
                btnCopy.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
                btnCopy.Click += (s, e) =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(txtPath.Text))
                            Clipboard.SetText(txtPath.Text);
                        btnCopy.Text = "Copied";
                    }
                    catch (Exception ex)
                    {
                        Log($"ShowConfigWarningDialog Copy failed: {ex.Message}");
                    }
                };
                dlg.Controls.Add(btnCopy);

                var btnOk = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new Point(456, 195),
                    Size = new Size(80, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(0, 120, 212),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                };
                btnOk.FlatAppearance.BorderColor = Color.FromArgb(0, 100, 180);
                dlg.Controls.Add(btnOk);
                dlg.AcceptButton = btnOk;

                dlg.ShowDialog(this);
            }
            catch (Exception ex)
            {
                Log($"ShowConfigWarningDialog failed: {ex.Message} - falling back to MessageBox");
                ErwinAddIn.ShowTopMostMessage($"{reason}\n\n{path}", "Configuration Warning", isError: false);
            }
        }

        /// <summary>
        /// Warn that the open Mart model has no CONFIG mapping and close it - the
        /// same policy AND the same themed <see cref="Forms.AddinMessageDialog"/>
        /// (red error accent) as <see cref="ShowDbmsMismatchWarningAndCloseModel"/>,
        /// for one consistent add-in dialog language. The model is being closed, so
        /// this is NOT the degraded-mode copy-the-path dialog
        /// (<see cref="ShowConfigWarningDialog"/>, still used when the model stays
        /// open): no Mart path, no Copy button - just the message and OK. Deferred
        /// via <see cref="System.Windows.Forms.Control.BeginInvoke(Delegate)"/>
        /// because this runs in the connect/Form.Load cycle where a synchronous
        /// <c>ShowDialog</c> deadlocks the paint cycle. The model is freshly opened
        /// and untouched (no config-driven setup ran), so the fast close discards
        /// cleanly; the close continuation then forgets the model so a reopen
        /// re-runs the config check.
        /// </summary>
        private void ShowConfigWarningAndCloseModel()
        {
            void ShowAndClose()
            {
                try
                {
                    AddinMessageDialog.Show(
                        null,
                        "No configuration is registered for this model.\n\n" +
                        "Please contact the Data Architecture team to register one for it.",
                        "No Configuration for Model",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch (Exception ex) { Log($"Config-less warning dialog failed: {ex.GetType().Name}: {ex.Message}"); }

                // OK acknowledged - close the unmapped Mart model (discard via the
                // fast cascade) on a background thread (it blocks on the dialog
                // watch + dismiss and steals foreground, so it must run off the UI
                // thread, and only AFTER the warning modal above has closed).
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool closed = false;
                    try
                    {
                        closed = Services.MartMartAutomation.CloseActiveModelFast(Log);
                    }
                    catch (Exception ex)
                    {
                        Log($"Config-less model-close failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    // Forget the closed model so a reopen re-fires the connect (and
                    // re-shows this warning + re-closes). Without this the reconnect
                    // timer keeps _isConnected=true / _lastConnectedLocator=X and,
                    // because a reopen has the identical locator, never re-detects
                    // it. The adopt path's stale-PU guard (HasActiveModelWindow)
                    // stops this reset from looping on the lingering ghost PU.
                    if (closed && IsHandleCreated && !IsDisposed)
                    {
                        try
                        {
                            BeginInvoke((Action)(() =>
                            {
                                _isConnected = false;
                                _lastConnectedLocator = null;
                                _knownLocators.Clear();
                                // See ShowDbmsMismatchWarningAndCloseModel: clearing
                                // _globalDataLoaded forces the next ConnectToModel to
                                // re-run ConfigContext.Initialize (full first-connect
                                // path) instead of the fast model-switch path, so the
                                // next model is checked against ITS OWN config, not
                                // this closed model's stale one.
                                _globalDataLoaded = false;
                                Log("Config-less: forgot the closed model - a reopen will re-run the check.");
                            }));
                        }
                        catch (Exception ex)
                        {
                            Log($"Config-less state reset failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                });
            }

            if (IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke((Action)ShowAndClose); return; }
                catch (Exception ex)
                {
                    Log($"Config-less BeginInvoke failed, running inline: {ex.GetType().Name}: {ex.Message}");
                }
            }
            ShowAndClose();
        }

        /// <summary>
        /// Open Windows Explorer with the addin log file selected. Falls
        /// back to opening the temp folder if the file does not exist
        /// yet (first-run race).
        /// </summary>
        private void OpenLogFolder()
        {
            try
            {
                string path = AddinLogger.FilePath;
                if (System.IO.File.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{System.IO.Path.GetDirectoryName(path)}\"");
                }
            }
            catch (Exception ex)
            {
                Log($"OpenLogFolder failed: {ex.Message}");
            }
        }

        private Panel CreateInfoCard(string title, int x, int y, int w, int h, Color bgColor)
        {
            var card = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = bgColor,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(16, 12, 16, 12)
            };

            if (!string.IsNullOrEmpty(title))
            {
                var lblTitle = new Label
                {
                    Text = title,
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(60, 60, 60),
                    AutoSize = true,
                    Location = new Point(16, 10)
                };
                card.Controls.Add(lblTitle);
            }

            return card;
        }

        /// <summary>
        /// CreateInfoCard with a small section header band along the top so
        /// rows below can start under it without overlapping. The first
        /// AddCardRow call places its label at y=14 within the card; the
        /// header band sits in y=0..30 area separated by a 1px tinted strip.
        /// Used by the new General-tab layout to label the Repository and
        /// Glossary sections without resorting to GroupBox borders, which
        /// look heavy next to the card chrome.
        /// </summary>
        private Panel CreateSectionCard(string sectionTitle, int x, int y, int w, int h, Color bgColor, Color headerColor)
        {
            var card = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h + 30),
                BackColor = bgColor,
                BorderStyle = BorderStyle.FixedSingle
            };

            var header = new Label
            {
                Text = sectionTitle,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = headerColor,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(0, 0),
                Size = new Size(w - 2, 26),
                BackColor = Color.FromArgb(247, 249, 252),
                Padding = new Padding(16, 0, 0, 0)
            };
            card.Controls.Add(header);

            var divider = new Panel
            {
                Location = new Point(0, 26),
                Size = new Size(w - 2, 1),
                BackColor = Color.FromArgb(228, 231, 236)
            };
            card.Controls.Add(divider);

            var body = new Panel
            {
                Location = new Point(0, 27),
                Size = new Size(w - 2, h),
                BackColor = bgColor
            };
            card.Controls.Add(body);

            // Track the body so AddCardRow targets it instead of the card
            // chrome. We do this by tagging the card with the body reference;
            // AddCardRow looks at the tag and falls back to the card itself
            // when it is null (preserving the legacy CreateInfoCard call sites).
            card.Tag = body;

            return card;
        }

        private void AddCardRow(Panel card, string label, string value, Font labelFont, Font valueFont, int row, out Label lblLabel, out Label lblValue)
        {
            // CreateSectionCard tags the card with its body Panel so the rows
            // skip the section-header band along the top. CreateInfoCard sets
            // no Tag, so we fall back to the card itself - existing call sites
            // keep their original visual layout.
            var host = card.Tag as Panel ?? card;
            int y = 14 + row * 26;

            lblLabel = new Label
            {
                Text = label,
                Font = labelFont,
                ForeColor = Color.FromArgb(80, 80, 80),
                AutoSize = true,
                Location = new Point(16, y)
            };
            host.Controls.Add(lblLabel);

            lblValue = new Label
            {
                Text = value,
                Font = valueFont,
                ForeColor = Color.FromArgb(40, 40, 40),
                AutoSize = true,
                Location = new Point(120, y)
            };
            host.Controls.Add(lblValue);
        }

#if !PACKAGED
        /// <summary>
        /// Dev-only "Reload Config" button anchored to the right of the
        /// Repository card's first row. Re-runs the full validation pipeline
        /// (CONFIG row from DB + all config-scoped services) so a config edit
        /// in the admin tool is picked up without restarting erwin.
        /// </summary>
        private int AddRepositoryReloadButton(Panel card, int cardW)
        {
            var host = card.Tag as Panel ?? card;
            const int btnW = 110;
            const int btnH = 22;
            // Row 0's label sits at y=14; align the button's vertical center
            // with the row by inset of 2px above (matches typical Button vs
            // AutoSize Label baseline on Segoe UI 9pt).
            int reloadX = (cardW - 2) - btnW - 12;
            int btnY = 12;
            int leftMostX = reloadX;

            var reloadBtn = new Button
            {
                Text = "Reload Config",
                Location = new Point(reloadX, btnY),
                Size = new Size(btnW, btnH),
                FlatStyle = FlatStyle.System,
                UseVisualStyleBackColor = true,
                TabStop = false,
            };
            reloadBtn.Click += BtnReloadConfig_Click;
            host.Controls.Add(reloadBtn);

#if DEV
            // Dev-only "Change DB": re-run the startup MetaRepo* picker to switch the
            // active database in-memory, then reload the config. Sits to the left of
            // Reload Config. Only compiled when DEV is defined (never in a package).
            const int changeW = 100;
            const int changeGap = 6;
            int changeX = reloadX - changeW - changeGap;
            var changeDbBtn = new Button
            {
                Text = "Change DB",
                Location = new Point(changeX, btnY),
                Size = new Size(changeW, btnH),
                FlatStyle = FlatStyle.System,
                UseVisualStyleBackColor = true,
                TabStop = false,
            };
            changeDbBtn.Click += BtnChangeDb_Click;
            host.Controls.Add(changeDbBtn);
            leftMostX = changeX;
#endif

            // Return the LEFT-most button's X so the label MaximumSize math shrinks past them.
            return leftMostX;
        }

        private void BtnReloadConfig_Click(object sender, EventArgs e)
#if DEV
            // DEV: preserve any "Change DB" override - do NOT clear the bootstrap cache
            // (that would snap back to the registry DB). Config-less stays kept-open
            // (the dev is working in this model against the currently-selected DB).
            => RunConfigReload(sender as Button, clearBootstrapCache: false, closeConfigLessModel: false,
                overlayMessage: "Reloading config from database, please wait...",
                logPrefix: "Reload Config");
#else
            => RunConfigReload(sender as Button, clearBootstrapCache: true, closeConfigLessModel: false,
                overlayMessage: "Reloading config from database, please wait...",
                logPrefix: "Reload Config");
#endif

        /// <summary>
        /// Shared reload used by "Reload Config" and (dev) "Change DB". Re-runs the
        /// full validation pipeline (CONFIG row from DB + all config-scoped services)
        /// and refreshes the General tab + session-tracking settings.
        /// <para>
        /// <paramref name="clearBootstrapCache"/> true drops both bootstrap caches so
        /// the next GetConfig() re-reads the registry (Reload Config's purpose: pick up
        /// an install.bat edit without restarting erwin). Change DB passes FALSE:
        /// DevDatabaseSelector has already installed an in-memory bootstrap override,
        /// and clearing the cache would discard it and snap back to the registry DB.
        /// </para>
        /// </summary>
        private void RunConfigReload(Button btn, bool clearBootstrapCache, bool closeConfigLessModel, string overlayMessage, string logPrefix)
        {
            // Re-entrancy guard: full reload is ~4-5s; disabling the button prevents a
            // second pass interleaving with the first DisposeServices/LoadGlossary cycle.
            if (btn != null) btn.Enabled = false;
            Form overlay = null;
            try
            {
                Log($"{logPrefix}: user triggered full validation re-init.");
                overlay = ShowBusyOverlay(overlayMessage);
                Application.DoEvents();

                if (clearBootstrapCache)
                {
                    // Drop both bootstrap caches so the next GetConfig() re-reads the
                    // registry. Without this the reload re-runs every DB query but keeps
                    // the connection captured at process start.
                    Services.DatabaseService.Instance.ClearCache();
                    Log($"{logPrefix}: bootstrap cache cleared - registry will be re-read.");
                }

                // Explicit reload / DB switch: clear the glossary credential-failure latch so a
                // load that gave up earlier (undecryptable creds) is retried once now - the admin
                // may have re-entered the credentials. Passive gestures still respect the latch.
                Services.GlossaryService.Instance.ResetCredentialFailureLatch();

                // Also drop the loaded glossary itself. IsLoaded is config-scoped (keyed on
                // ActiveConfigId), but CONFIG.ID is repo-local, so a Change-DB to a DIFFERENT repo
                // whose active model reuses the same id would otherwise keep serving the previous
                // repo's glossary. Invalidate forces the post-Initialize LoadGlossary below to
                // re-read under the new repo/config.
                Services.GlossaryService.Instance.Invalidate();

                // Force the full pipeline (not the fast model-switch path).
                _globalDataLoaded = false;
                using (AddinLogger.BeginScope($"InitializeValidationService({logPrefix})"))
                    InitializeValidationService(closeConfigLessMartModel: closeConfigLessModel);

                if (Services.ConfigContextService.Instance.IsInitialized)
                    _globalDataLoaded = true;

                using (AddinLogger.BeginScope($"UpdateGeneralTab({logPrefix})"))
                    UpdateGeneralTab();

                // Re-apply user-session tracking interval (USER_TRACKING_INTERVAL_MINUTES)
                // so a mid-session change takes effect without restarting erwin.
                // Non-blocking + best-effort.
                Services.SessionTrackingService.Instance.ReloadSettings();

                Log($"{logPrefix}: complete.");
            }
            catch (Exception ex)
            {
                // Surface the failure rather than swallowing - the stack trace is what we need.
                Log($"{logPrefix} failed: {ex}");
                AddinMessageDialog.Show(this,
                    $"{logPrefix} failed:\r\n{ex.Message}",
                    logPrefix,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (overlay != null && !overlay.IsDisposed)
                {
                    overlay.Close();
                    overlay.Dispose();
                }
                if (btn != null && !btn.IsDisposed) btn.Enabled = true;
            }
        }

#if DEV
        // Dev-only: re-run the startup MetaRepo* database picker mid-session, then
        // reload the config against the newly chosen database. Cancel / no selection
        // keeps the current database (unlike startup, we never abort - a model is
        // already open). Only compiled when DEV is defined (never in a package).
        private void BtnChangeDb_Click(object sender, EventArgs e)
        {
            try
            {
                Log("Change DB: re-running dev database picker...");
                if (!Services.DevDatabaseSelector.TrySelectAndOverride(Log))
                {
                    Log("Change DB: cancelled / no selection - keeping current database.");
                    return;
                }
                // TrySelectAndOverride already installed the in-memory override; reload
                // WITHOUT clearing the bootstrap cache (that would discard the override).
                // Switching the whole DB: if the current model has no config in the new
                // DB it does not belong there, so close it (like a fresh connect).
                RunConfigReload(sender as Button, clearBootstrapCache: false, closeConfigLessModel: true,
                    overlayMessage: "Switching database and reloading config, please wait...",
                    logPrefix: "Change DB");
            }
            catch (Exception ex)
            {
                Log($"Change DB failed: {ex}");
                AddinMessageDialog.Show(this,
                    $"Change DB failed:\r\n{ex.Message}",
                    "Change DB",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
#endif
#endif

        /// <summary>
        /// Clears the "Model" + connection + glossary rows to their no-model state.
        /// Used by the config-less / DBMS-mismatch CLOSE paths: they shut the model
        /// but deliberately keep _isConnected=true for the reconnect timer's adopt
        /// path, so the display would otherwise keep showing the just-closed model
        /// (user-reported 2026-07-07: "Model" showed the old name after the close).
        /// Display-only - does NOT touch _isConnected / _lastConnectedLocator.
        /// </summary>
        private void ResetActiveModelDisplay()
        {
            if (lblActiveModel != null) lblActiveModel.Text = "(no model open)";
            if (lblConnectionStatus != null)
            {
                lblConnectionStatus.Text = "Not connected";
                lblConnectionStatus.ForeColor = Color.FromArgb(120, 120, 120);
            }
            if (lblGlossaryStatus != null)
            {
                lblGlossaryStatus.Text = "(not loaded)";
                lblGlossaryStatus.ForeColor = Color.FromArgb(120, 120, 120);
            }
            if (lblLastRefreshValue != null)
            {
                lblLastRefreshValue.Text = "(not yet)";
                lblLastRefreshValue.ForeColor = Color.FromArgb(120, 120, 120);
            }
        }

        /// <summary>
        /// Update General tab with corporate and connection info after initialization.
        /// </summary>
        private void UpdateGeneralTab()
        {
            try
            {
                var ctx = ConfigContextService.Instance;
                if (ctx.IsInitialized)
                {
                    // Resolved config: green = "validation is live". DB type
                    // is appended in parens so the user can sanity-check at
                    // a glance which back-end the Mart row lives on (the
                    // Database card row still carries the full host/catalog).
                    // ForeColor MUST be reset here - a prior degraded cycle
                    // would have left the label red, and skipping the reset
                    // was the user-reported 2026-05-14 bug ("bağlanınca da
                    // kırmızı kalıyor").
                    // Label column already reads "Config:", so the value just
                    // carries the config name (optionally prefixed by the
                    // corporate group) without duplicating the "Config:" word.
                    string corpLabel = string.IsNullOrEmpty(ctx.CorporateName)
                        ? ctx.ActiveConfigName
                        : $"{ctx.CorporateName} / {ctx.ActiveConfigName}";
                    string dbType = null;
                    try { dbType = DatabaseService.Instance.GetDbType(); } catch { }
                    if (!string.IsNullOrEmpty(dbType))
                        corpLabel += $" ({dbType})";
                    _lblCorporateValue.Text = corpLabel;
                    _lblCorporateValue.ForeColor = Color.DarkGreen;
                }
                else
                {
                    // Degraded mode: surface the config-resolution failure inline so
                    // the user can see WHY validation is disabled without re-opening
                    // the warning dialog. Red text emphasizes the missing-config
                    // state more strongly than the previous amber (user feedback
                    // 2026-05-14: amber read as a warning, but this is the actual
                    // reason every action button is disabled).
                    // Not initialized: short, readable "No Config". The full reason is
                    // in the log + the degraded status bar; a long error string here
                    // was unreadable (user feedback 2026-07-07). The DB prefix below
                    // still shows which database this is.
                    _lblCorporateValue.Text = "No Config";
                    _lblCorporateValue.ForeColor = Color.Red;
                }

                // Prefix the Config row with the active database so it is always visible
                // which repository DB the add-in connected to - the standalone "System /
                // Database" card was removed 2026-07-07 as redundant. Refreshes with the
                // row, so a dev "Change DB" updates it too.
                var config = DatabaseService.Instance.GetConfig();
                if (config != null && !string.IsNullOrEmpty(config.Database))
                    _lblCorporateValue.Text = $"DB:{config.Database}  |  {_lblCorporateValue.Text}";

                if (_lblWarningsValue != null)
                {
                    if (_connectWarnings.Count == 0)
                    {
                        _lblWarningsValue.Text = "(none)";
                        _lblWarningsValue.ForeColor = Color.FromArgb(120, 120, 120);
                    }
                    else
                    {
                        _lblWarningsValue.Text = string.Join("  ;  ", _connectWarnings);
                        _lblWarningsValue.ForeColor = Color.FromArgb(192, 0, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateGeneralTab error: {ex.Message}");
            }
        }

        /// <summary>
        /// Record a service-load failure or schema-mismatch warning to be
        /// shown on the General tab Warnings row. Idempotent (deduplicates).
        /// Caller is responsible for invoking <c>UpdateGeneralTab</c> after
        /// adding/removing entries so the UI reflects the current set.
        /// </summary>
        private void AddConnectWarning(string warning)
        {
            if (string.IsNullOrWhiteSpace(warning)) return;
            if (!_connectWarnings.Contains(warning))
                _connectWarnings.Add(warning);
        }

        #endregion

        private void InitializeValidationUI()
        {
            // Severity icons rendered onto the leading column via SmallImageList.
            // Two indices: 0 = success (green check), 1 = error (red x). Drawn
            // at runtime so the form has no PNG/icon resource dependency.
            listValidationResults.SmallImageList = CreateValidationImageList();

            // Type column widened slightly because it now hosts the severity
            // icon to the left of the type text. The dedicated "" status icon
            // column from before was redundant and is removed.
            listValidationResults.Columns.Add("Type", 100);
            listValidationResults.Columns.Add("Object Name", 220);
            listValidationResults.Columns.Add("Rule", 110);
            listValidationResults.Columns.Add("Message", 400);

            btnValidateAll.Enabled = false;
        }

        private static ImageList CreateValidationImageList()
        {
            var imgs = new ImageList
            {
                ImageSize = new Size(16, 16),
                ColorDepth = ColorDepth.Depth32Bit
            };
            imgs.Images.Add(MakeSeverityIcon(Color.FromArgb(0, 138, 62),  "✓")); // 0 success ✓
            imgs.Images.Add(MakeSeverityIcon(Color.FromArgb(204, 0, 0),   "×")); // 1 error    ×
            return imgs;
        }

        private static Bitmap MakeSeverityIcon(Color bg, string glyph)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var brush = new SolidBrush(bg))
                {
                    g.FillEllipse(brush, 0, 0, 15, 15);
                }
                using (var font = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                using (var fb = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString(glyph, font, fb, new RectangleF(0, 0, 16, 16), sf);
                }
            }
            return bmp;
        }

        private void BtnValidateAll_Click(object sender, EventArgs e)
        {
            listValidationResults.Items.Clear();

            string filter = cmbValidationFilter.SelectedItem?.ToString() ?? "All";
            int errorCount = 0;
            int totalCount = 0;

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Table validation (Glossary column check + TableType + Naming Standards)
                if (filter == "All" || filter == "Table")
                {
                    dynamic allEntities = modelObjects.Collect(root, "Entity");
                    if (allEntities != null)
                    {
                        foreach (dynamic entity in allEntities)
                        {
                            if (entity == null) continue;
                            string tableName = "";
                            try
                            {
                                string physName = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                                tableName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : (entity.Name ?? "");
                            }
                            catch { try { tableName = entity.Name ?? ""; } catch { continue; } }

                            // Naming standard validation
                            var namingResults = NamingValidationEngine.ValidateObjectName("Table", tableName, entity);
                            foreach (var r in namingResults)
                            {
                                totalCount++;
                                if (!r.IsValid) errorCount++;
                                AddValidationRow("Table", tableName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                            }

                            // TABLE_TYPE validation is now handled by NamingStandard rules with UDP conditions
                        }
                    }
                }

                // Column validation (Glossary + Naming Standards)
                if (filter == "All" || filter == "Column")
                {
                    dynamic allEntities = modelObjects.Collect(root, "Entity");
                    if (allEntities != null)
                    {
                        var glossary = GlossaryService.Instance;
                        foreach (dynamic entity in allEntities)
                        {
                            if (entity == null) continue;
                            string tableName = "";
                            try
                            {
                                string physName = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                                tableName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : (entity.Name ?? "");
                            }
                            catch { try { tableName = entity.Name ?? ""; } catch { continue; } }

                            dynamic attrs = null;
                            try { attrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                            if (attrs == null) continue;

                            foreach (dynamic attr in attrs)
                            {
                                if (attr == null) continue;
                                string colName = "";
                                try
                                {
                                    string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                                    colName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : (attr.Name ?? "");
                                }
                                catch { try { colName = attr.Name ?? ""; } catch { continue; } }

                                // Glossary check
                                if (glossary.IsLoaded)
                                {
                                    bool inGlossary = glossary.HasEntry(colName);
                                    totalCount++;
                                    if (!inGlossary) errorCount++;
                                    AddValidationRow("Column", $"{tableName}.{colName}", "Glossary", inGlossary, inGlossary ? "" : "Not found in glossary");
                                }

                                // Naming standard validation
                                var namingResults = NamingValidationEngine.ValidateObjectName("Column", colName, attr);
                                foreach (var r in namingResults)
                                {
                                    totalCount++;
                                    if (!r.IsValid) errorCount++;
                                    AddValidationRow("Column", $"{tableName}.{colName}", $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                                }
                            }
                        }
                    }
                }

                // Index (Key_Group) validation
                if (filter == "All" || filter == "Index")
                {
                    try
                    {
                        dynamic allKg = modelObjects.Collect(root, "Key_Group");
                        if (allKg != null)
                        {
                            foreach (dynamic kg in allKg)
                            {
                                if (kg == null) continue;
                                string kgName = "";
                                try { kgName = kg.Name ?? ""; } catch { continue; }

                                var namingResults = NamingValidationEngine.ValidateObjectName("Index", kgName, kg);
                                foreach (var r in namingResults)
                                {
                                    totalCount++;
                                    if (!r.IsValid) errorCount++;
                                    AddValidationRow("Index", kgName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // View validation
                if (filter == "All" || filter == "View")
                {
                    try
                    {
                        dynamic allViews = modelObjects.Collect(root, "View");
                        if (allViews != null)
                        {
                            foreach (dynamic view in allViews)
                            {
                                if (view == null) continue;
                                string viewName = "";
                                try
                                {
                                    string physName = view.Properties("Physical_Name").Value?.ToString() ?? "";
                                    viewName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : (view.Name ?? "");
                                }
                                catch { try { viewName = view.Name ?? ""; } catch { continue; } }

                                var namingResults = NamingValidationEngine.ValidateObjectName("View", viewName, view);
                                foreach (var r in namingResults)
                                {
                                    totalCount++;
                                    if (!r.IsValid) errorCount++;
                                    AddValidationRow("View", viewName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Model validation (naming standards)
                if (filter == "All" || filter == "Model")
                {
                    try
                    {
                        string modelName = "";
                        try
                        {
                            dynamic modelRoot = modelObjects.Root;
                            modelName = modelRoot?.Name ?? "";
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(modelName))
                        {
                            var namingResults = NamingValidationEngine.ValidateObjectName("Model", modelName);
                            foreach (var r in namingResults)
                            {
                                totalCount++;
                                if (!r.IsValid) errorCount++;
                                AddValidationRow("Model", modelName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                            }
                        }
                    }
                    catch { }
                }

                // Subject Area validation (naming standards)
                if (filter == "All" || filter == "Subject Area")
                {
                    try
                    {
                        dynamic allSAs = modelObjects.Collect(root, "Subject_Area");
                        if (allSAs != null)
                        {
                            foreach (dynamic sa in allSAs)
                            {
                                if (sa == null) continue;
                                string saName = "";
                                try { saName = sa.Name ?? ""; } catch { continue; }

                                var namingResults = NamingValidationEngine.ValidateObjectName("Subject Area", saName, sa);
                                foreach (var r in namingResults)
                                {
                                    totalCount++;
                                    if (!r.IsValid) errorCount++;
                                    AddValidationRow("Subject Area", saName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Apply "Errors Only" checkbox filter. Severity is now carried
                // by ImageIndex (0 = success, 1 = error) instead of a dedicated
                // SubItems[3] glyph column.
                if (chkErrorsOnly.Checked)
                {
                    var toRemove = new List<ListViewItem>();
                    foreach (ListViewItem item in listValidationResults.Items)
                    {
                        if (item.ImageIndex == 0) toRemove.Add(item);
                    }
                    foreach (var item in toRemove) listValidationResults.Items.Remove(item);
                }
            }
            catch (Exception ex)
            {
                Log($"ValidateAll error: {ex.Message}");
            }

            ShowValidationSummary(totalCount, errorCount);
        }

        private void AddValidationRow(string objectType, string objectName, string rule, bool isValid, string message)
        {
            // Severity is now carried by the ImageIndex (rendered as the row
            // icon left of the Type cell) rather than the dedicated symbol
            // column or whole-row foreground color. Errors stay readable on
            // the new flat-no-gridlines list - the red icon is the primary
            // signal, the message text in normal foreground keeps things
            // accessible at any zoom level.
            var item = new ListViewItem(objectType)
            {
                ImageIndex = isValid ? 0 : 1
            };
            item.SubItems.Add(objectName);
            item.SubItems.Add(rule);
            item.SubItems.Add(isValid ? "" : message);
            listValidationResults.Items.Add(item);
        }

        private void ShowValidationSummary(int totalCount, int errorCount)
        {
            if (errorCount == 0)
            {
                lblValidationStatus.Text = $"All validations passed ({totalCount} checks)";
                lblValidationStatus.ForeColor = Color.DarkGreen;
            }
            else
            {
                lblValidationStatus.Text = $"Validation: {errorCount} error(s) / {totalCount} checks";
                lblValidationStatus.ForeColor = Color.Red;
            }
        }

        private void CmbValidationFilter_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbValidationFilter.SelectedIndex < 0) return;

            // Re-run validation with the selected filter
            if (_isConnected && _session != null)
            {
                BtnValidateAll_Click(sender, e);
            }
        }

        private void ChkErrorsOnly_CheckedChanged(object sender, EventArgs e)
        {
            // Re-run validation with current filter + errors only toggle
            if (_isConnected && _session != null)
            {
                BtnValidateAll_Click(sender, e);
            }
        }

        private void UpdateValidationStatus()
        {
            var glossary = GlossaryService.Instance;
            if (glossary.IsLoaded)
            {
                lblValidationStatus.Text = $"Ready - Glossary: {glossary.Count} entries. Click 'Validate All' to check.";
                lblValidationStatus.ForeColor = Color.DarkGreen;
            }
            else
            {
                lblValidationStatus.Text = "Warning: Glossary not loaded";
                lblValidationStatus.ForeColor = Color.Orange;
            }
        }

        #endregion

        #region Glossary Management

        private void InitializeGlossaryRefreshTimer()
        {
            int intervalMinutes = GetGlossaryLoadInterval();
            int intervalMs = intervalMinutes * 60 * 1000;

            _glossaryRefreshTimer = new Timer { Interval = intervalMs };
            _glossaryRefreshTimer.Tick += GlossaryRefreshTimer_Tick;
            _glossaryRefreshTimer.Start();
            Log($"Glossary auto-refresh timer started (every {intervalMinutes} minute(s))");
        }

        private int GetGlossaryLoadInterval()
        {
            // Effective GLOSSARY_LOAD_INTERVAL via the two-level cascade: model
            // CONFIG_PROPERTY -> corporate CORPORATE_PROPERTY -> built-in 5 minutes
            // (spec 2026-06-04). The admin Glossary screen writes this row, so the
            // DB value always wins; 5 only applies when NEITHER level has a row.
            // minutes must be > 0 to be honoured.
            const int defaultInterval = 5;
            try
            {
                int minutes = ConfigContextService.Instance.GetEffectiveInt("GLOSSARY_LOAD_INTERVAL", defaultInterval);
                return minutes > 0 ? minutes : defaultInterval;
            }
            catch (Exception ex)
            {
                // A real DB read error is SURFACED (logged as an error), not silently
                // defaulted - a missing row would have returned 5 with no error.
                Log($"GetGlossaryLoadInterval: DB read error (falling back to {defaultInterval} min): {ex.Message}");
                return defaultInterval;
            }
        }

        private void GlossaryRefreshTimer_Tick(object sender, EventArgs e)
        {
            // DDL-dedicated instance: the glossary is never consumed (all
            // validation surfaces are off), so skip the periodic DB reload.
            if (IsDdlDedicatedInstance) return;

            // Prevent re-entrancy - skip if already refreshing
            if (_isRefreshingGlossary) return;

            // Run on background thread to avoid UI freeze
            Task.Run(() => RefreshGlossarySilently());
        }

        private void RefreshGlossarySilently()
        {
            if (_isRefreshingGlossary) return;
            if (!DatabaseService.Instance.IsConfigured) return;

            try
            {
                _isRefreshingGlossary = true;

                GlossaryService.Instance.Reload();
                _lastGlossaryRefreshTime = DateTime.Now;

                // Update UI on main thread
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        UpdateLastRefreshLabel();
                        if (GlossaryService.Instance.IsLoaded)
                        {
                            Log($"Glossary auto-refreshed: {GlossaryService.Instance.Count} entries");
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() => Log($"Glossary refresh error: {ex.Message}")));
                }
            }
            finally
            {
                _isRefreshingGlossary = false;
            }
        }

        private void UpdateLastRefreshLabel()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateLastRefreshLabel));
                return;
            }

            if (_lastGlossaryRefreshTime.HasValue)
            {
                lblLastRefreshValue.Text = _lastGlossaryRefreshTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                lblLastRefreshValue.ForeColor = Color.DarkGreen;
            }
        }

        // BtnTestConnection_Click and BtnReloadGlossary_Click removed (2026-05-09):
        // the buttons used to live on the Glossary tab, but that tab is gone -
        // the glossary status is now a passive section on the General tab.
        // Glossary loading runs automatically when the model loads or when
        // DatabaseService.ClearCache is invoked elsewhere; manual reload is no
        // longer surfaced.

        private void LoadGlossary()
        {
            try
            {
                if (!DatabaseService.Instance.IsConfigured)
                {
                    lblGlossaryStatus.Text = "Repository database not configured. Please configure in ErwinAdmin.";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    return;
                }

                var glossary = GlossaryService.Instance;
                if (!glossary.IsLoaded)
                {
                    glossary.LoadGlossary();
                }

                if (glossary.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Loaded ({glossary.Count} entries)";
                    lblGlossaryStatus.ForeColor = Color.FromArgb(0, 138, 62);
                    _lastGlossaryRefreshTime = DateTime.Now;
                    UpdateLastRefreshLabel();
                }
                else
                {
                    lblGlossaryStatus.Text = $"Not loaded - {glossary.LastError}";
                    lblGlossaryStatus.ForeColor = Color.FromArgb(204, 0, 0);
                    AddConnectWarning($"Glossary: {glossary.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log($"LoadGlossary error: {ex.Message}");
                lblGlossaryStatus.Text = $"Error - {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.FromArgb(204, 0, 0);
                AddConnectWarning($"Glossary: {ex.Message}");
            }
        }

        // UpdateGlossaryConnectionLabels / ClearGlossaryConnectionLabels removed
        // (2026-05-09): the host/port/database labels they wrote to lived on
        // the retired Glossary tab and only ever surfaced placeholder values
        // ("Configured", "-", "(N entries)") instead of real connection
        // metadata. The General tab's Glossary card surfaces just the load
        // status and last refresh time, both managed by LoadGlossary directly.

        #endregion

        #region Table Type Management

        private void LoadPredefinedColumns()
        {
            try
            {
                var predefinedColumnService = PredefinedColumnService.Instance;
                predefinedColumnService.LoadPredefinedColumns();

                if (predefinedColumnService.IsLoaded)
                {
                    Log($"PREDEFINED_COLUMN loaded: {predefinedColumnService.Count} entries");
                }
                else
                {
                    Log($"PREDEFINED_COLUMN not loaded: {predefinedColumnService.LastError}");
                    AddConnectWarning($"PredefinedColumns: {predefinedColumnService.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log($"LoadPredefinedColumns error: {ex.Message}");
                AddConnectWarning($"PredefinedColumns: {ex.Message}");
            }
        }

        // Set when the open model's live DBMS does not match its bound config's
        // DBMS (see CheckDbmsMismatch). While true the add-in blocks config-driven
        // operations (property standards init) and DDL submission.
        private bool _dbmsMismatch;
        private string _dbmsMismatchMessage;

        /// <summary>
        /// Compares the OPEN MODEL's live DBMS (target-server label, e.g. "Oracle
        /// 19c") against the bound CONFIG's DBMS label (ConfigContextService.DbmsLabel).
        /// On a mismatch the add-in INFORMS the modeler via a modal and BLOCKS the
        /// DBMS-driven operations: the model and its configuration must agree on the
        /// DBMS, and CONFIG.DBMS_VERSION_ID is admin-owned, so the modeler cannot
        /// silently reconcile it. Comparison is label-based (case/whitespace
        /// normalized) - the model and config sides use different erwin code schemes,
        /// so the composed "{DBMS} {Version}" label is the only shared key. Returns
        /// true on mismatch. Only fires when BOTH labels are known (a missing side is
        /// handled elsewhere and must not cause a false block).
        /// </summary>
        private bool CheckDbmsMismatch()
        {
            _dbmsMismatch = false;
            _dbmsMismatchMessage = null;

            string configLabel = Services.ConfigContextService.Instance?.DbmsLabel;
            string modelLabel = ReadActivePuTargetServer();

            if (string.IsNullOrWhiteSpace(configLabel) || string.IsNullOrWhiteSpace(modelLabel))
                return false;

            if (NormalizeDbmsLabel(modelLabel) == NormalizeDbmsLabel(configLabel))
                return false;

            _dbmsMismatch = true;
            _dbmsMismatchMessage =
                $"This model's configuration specifies DBMS \"{configLabel}\", " +
                $"but the open model targets \"{modelLabel}\".\n\n" +
                "DBMS-driven operations (property standards and DDL generation/approval) " +
                "are disabled for this model. Please contact the Data Architecture team " +
                "to align the configuration with the model.";

            Log($"DBMS mismatch: config='{configLabel}' vs model='{modelLabel}' - config-driven operations blocked; closing model.");
            ShowDbmsMismatchWarningAndCloseModel();
            return true;
        }

        /// <summary>
        /// Show the themed model/config DBMS-mismatch warning and, once the user
        /// acknowledges it, close the mismatched model. Deferred via
        /// <see cref="System.Windows.Forms.Control.BeginInvoke(Delegate)"/>
        /// because <see cref="CheckDbmsMismatch"/> runs inside the connect /
        /// Form.Load cycle, where a synchronous managed <c>ShowDialog</c>
        /// deadlocks the form's paint cycle (the same reason
        /// <see cref="RunUdpSyncIfNeeded"/> posts its dialog). The legacy native
        /// MessageBox sidestepped this by pumping its own modal message loop;
        /// the themed <see cref="Forms.AddinMessageDialog"/> cannot, so it must
        /// run after the connect cycle yields. Closing a Mart model always raises
        /// erwin's "Mart Offline" dialog (and a "Save Models" dialog if the model
        /// is dirty); <see cref="Services.MartMartAutomation.CloseActiveModelFast"/>
        /// dismisses it (Save-to=Close + OK). The model is freshly opened and
        /// untouched here (all config-driven setup is skipped on mismatch), so it
        /// is clean and single - the fast path can skip the dirty-cascade's Save
        /// Models / Close Model waits, keeping the modal on screen ~2-3s.
        /// </summary>
        private void ShowDbmsMismatchWarningAndCloseModel()
        {
            void ShowAndClose()
            {
                try
                {
                    AddinMessageDialog.Show(
                        null,
                        _dbmsMismatchMessage ?? "The model's DBMS does not match its configuration. Please contact the Data Architecture team.",
                        "Model / Configuration DBMS Mismatch",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch (Exception ex)
                {
                    Log($"DBMS mismatch dialog failed: {ex.GetType().Name}: {ex.Message}");
                }

                // OK acknowledged - close the mismatched model so the user cannot
                // keep working against a config that does not match it. Closing a
                // Mart model raises erwin's "Mart Offline" dialog (per-row Save-to
                // combo + OK); CloseActiveModelFast drives it to Save-to=Close + OK.
                // It blocks on the dialog watch + dismiss and steals foreground, so
                // it MUST run off the UI thread - and only AFTER the warning above
                // has closed, so the foreground-steal never pushes a live modal
                // behind erwin (see the teardown lesson).
                // Drop the stale "Model" + glossary rows now (still on the UI thread)
                // so they don't keep showing the model being closed below.
                ResetActiveModelDisplay();
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool closed = false;
                    try
                    {
                        closed = Services.MartMartAutomation.CloseActiveModelFast(Log);
                    }
                    catch (Exception ex)
                    {
                        Log($"DBMS mismatch model-close failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    // Forget the closed model so REOPENING it re-fires the connect
                    // (and re-shows this warning + re-closes). Without this the
                    // reconnect timer keeps _isConnected=true / _lastConnectedLocator=X
                    // and, because a reopen has the identical locator, never
                    // re-detects it. The adopt path's stale-PU guard
                    // (HasActiveModelWindow) stops this reset from looping on the
                    // lingering ghost PU. Marshalled to the UI thread - these fields
                    // belong to the reconnect timer.
                    if (closed && IsHandleCreated && !IsDisposed)
                    {
                        try
                        {
                            BeginInvoke((Action)(() =>
                            {
                                _isConnected = false;
                                _lastConnectedLocator = null;
                                _knownLocators.Clear();
                                // MUST also clear _globalDataLoaded: otherwise the
                                // next ConnectToModel (reopen, or a DIFFERENT model)
                                // takes the fast model-switch path which does NOT
                                // re-run ConfigContext.Initialize, so the mismatch /
                                // config check would reuse THIS closed model's stale
                                // config against the next model (false mismatch on an
                                // unmapped/different-config model). Forcing the full
                                // first-connect path re-resolves config per model.
                                _globalDataLoaded = false;
                                Log("DBMS mismatch: forgot the closed model - a reopen will re-run the check.");
                            }));
                        }
                        catch (Exception ex)
                        {
                            Log($"DBMS mismatch state reset failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                });
            }

            if (IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke((Action)ShowAndClose); return; }
                catch (Exception ex)
                {
                    Log($"DBMS mismatch BeginInvoke failed, running inline: {ex.GetType().Name}: {ex.Message}");
                }
            }
            ShowAndClose();
        }

        // Case-insensitive, whitespace-collapsed comparison key for DBMS labels.
        private static string NormalizeDbmsLabel(string s)
            => System.Text.RegularExpressions.Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

        /// <summary>
        /// Initialize PropertyApplicatorService: detect platform, load project standards from DB.
        /// Standards will be auto-applied to new tables when created.
        /// </summary>
        private void InitializePropertyApplicator()
        {
            try
            {
                _propertyApplicatorService?.Dispose();
                _propertyApplicatorService = null;

                var config = DatabaseService.Instance.GetConfig();
                if (config == null || !config.IsConfigured)
                {
                    Log("PropertyApplicator: DB not configured, skipping");
                    return;
                }

                var metadataService = new AddInPropertyMetadataService(DatabaseService.Instance.BootstrapService);
                _propertyApplicatorService = new PropertyApplicatorService(_session, metadataService);
                _propertyApplicatorService.OnLog += Log;

                if (_propertyApplicatorService.Initialize())
                {
                    int stdCount = _propertyApplicatorService.StandardCount;
                    int qCount = _propertyApplicatorService.QuestionCount;
                    int dbmsVer = _propertyApplicatorService.DbmsVersionId;
                    string statusParts = $"DBMS_VERSION_ID: {dbmsVer}  |  {stdCount} standard(s)";
                    if (qCount > 0) statusParts += $"  |  {qCount} rule(s) loaded";
                    lblPlatformStatus.Text = statusParts;
                    lblPlatformStatus.ForeColor = Color.DarkGreen;
                    Log($"PropertyApplicator: Ready (DBMS_VERSION_ID={dbmsVer}, {stdCount} standards, {qCount} questions)");
                }
                else
                {
                    // Initialize failed — most likely the active CONFIG has no DBMS_VERSION_ID
                    // assigned, or the config has zero matching property defs / standards.
                    var ctx = ConfigContextService.Instance;
                    if (!ctx.IsInitialized)
                        lblPlatformStatus.Text = "Config not resolved for this model.";
                    else if (!ctx.DbmsVersionId.HasValue)
                        lblPlatformStatus.Text = $"CONFIG '{ctx.ActiveConfigName}' has no DBMS_VERSION_ID — pick one in Admin.";
                    else
                        lblPlatformStatus.Text = $"No property definitions for DBMS_VERSION_ID={ctx.DbmsVersionId} TABLE.";
                    lblPlatformStatus.ForeColor = Color.OrangeRed;
                    Log("PropertyApplicator: Initialization failed (no config/dbms_version/standards)");
                    _propertyApplicatorService.Dispose();
                    _propertyApplicatorService = null;
                }
            }
            catch (Exception ex)
            {
                Log($"InitializePropertyApplicator error: {ex.Message}");
            }
        }

        #endregion

        #region Domain Management

        private void LoadDomainDefs()
        {
            try
            {
                var domainDefService = DomainDefService.Instance;
                domainDefService.Reload();

                if (domainDefService.IsLoaded)
                {
                    Log($"DOMAIN_DEF loaded: {domainDefService.Count} entries");
                    Log($"Domain values: {domainDefService.GetNamesAsCommaSeparated()}");
                }
                else
                {
                    Log($"DOMAIN_DEF not loaded: {domainDefService.LastError}");
                    AddConnectWarning($"DomainDefs: {domainDefService.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log($"LoadDomainDefs error: {ex.Message}");
                AddConnectWarning($"DomainDefs: {ex.Message}");
            }
        }

        private void LoadNamingStandards()
        {
            try
            {
                var service = NamingStandardService.Instance;
                service.LoadStandards();

                if (service.IsLoaded)
                {
                    Log($"NAMING_STANDARD loaded: {service.Count} active rules");

                    // Per-rule diagnostic dump. Each row is now atomic (post
                    // 2026-05-16 migration); we print the RuleType plus only
                    // the field(s) that kind uses. This makes admin-authoring
                    // mistakes (e.g. a Regexp rule with an empty pattern, a
                    // Length rule missing operator/value) trivially visible.
                    foreach (var rule in service.AllRules)
                    {
                        // Ordered AND/OR condition list (MC_NAMING_RULE_CONDITION).
                        // Render every term left-to-right with its connector so an
                        // admin reading the log can confirm exactly what the rule is
                        // gating on, e.g. "udp[TABLE_TYPE] in [LOG] AND prop[Is_PK] in [True]".
                        string udpCond;
                        if (rule.Conditions.Count == 0)
                        {
                            udpCond = "(none)";
                        }
                        else
                        {
                            var condParts = new List<string>();
                            foreach (var c in rule.Conditions)
                            {
                                // Qualify a related-object property (e.g. prop[SCHEMA.Name])
                                // so the dump shows WHERE the value is read from.
                                string propName = !string.IsNullOrEmpty(c.DependsOnPropertyObjectType)
                                        && !string.Equals(c.DependsOnPropertyObjectType?.Trim(), rule.ObjectType?.Trim(), StringComparison.OrdinalIgnoreCase)
                                    ? $"{c.DependsOnPropertyObjectType}.{c.DependsOnPropertyCode}"
                                    : c.DependsOnPropertyCode;
                                string src = !string.IsNullOrEmpty(c.DependsOnUdpName)
                                    ? $"udp[{c.DependsOnUdpName}]"
                                    : (!string.IsNullOrEmpty(c.DependsOnPropertyCode) ? $"prop[{propName}]" : "?[]");
                                string term = $"{src} in [{(string.IsNullOrEmpty(c.DependsOnPropertyValues) ? "(any)" : c.DependsOnPropertyValues)}]";
                                condParts.Add(c.OrderIndex == 0
                                    ? term
                                    : $"{(string.IsNullOrEmpty(c.Connector) ? "AND" : c.Connector.ToUpperInvariant())} {term}");
                            }
                            udpCond = string.Join(" ", condParts);
                        }
                        string msg = rule.ErrorMessage ?? "";
                        if (msg.Length > 80) msg = msg.Substring(0, 77) + "...";

                        string typeParam = rule.RuleType switch
                        {
                            NamingRuleKind.Prefix => $"prefix='{rule.Prefix}' auto={rule.AutoApply}",
                            NamingRuleKind.Suffix => $"suffix='{rule.Suffix}' auto={rule.AutoApply}",
                            NamingRuleKind.Length => $"len {(string.IsNullOrEmpty(rule.LengthOperator) ? "?" : rule.LengthOperator)} {rule.LengthValue?.ToString() ?? "?"}",
                            NamingRuleKind.Regexp => $"regex(len={(rule.RegexpPattern ?? "").Length})='{rule.RegexpPattern ?? ""}'",
                            NamingRuleKind.Required => "empty-check",
                            NamingRuleKind.Template => $"template fill={rule.TemplateFillMode} auto={rule.AutoApply} value='{(rule.ValueTemplate ?? "").Replace("\r", " ").Replace("\n", " ")}'",
                            _ => "(unknown)",
                        };
                        Log($"  rule#{rule.Id} [{rule.RuleType}] {rule.ObjectType}.{rule.PropertyCode} " +
                            $"req={rule.IsRequired} apply={rule.ApplyOn} {typeParam} cond={udpCond} msg='{msg}'");
                    }

                    // Template rules navigate related objects via the global
                    // MC_OBJECT_RELATION alias catalog. Refresh it alongside the
                    // rules (only when a Template rule exists, to avoid a needless
                    // query) so a corporate/DB switch picks up the right catalog.
                    // Non-fatal: ObjectRelationCatalog lazy-loads on first use and
                    // an unreadable catalog surfaces per-rule at apply time.
                    if (service.AllRules.Any(r => r.RuleType == NamingRuleKind.Template))
                    {
                        try
                        {
                            ObjectRelationCatalog.Instance.Reload();
                            Log("MC_OBJECT_RELATION catalog loaded for Template rules");
                        }
                        catch (Exception relEx)
                        {
                            Log($"MC_OBJECT_RELATION catalog not loaded: {relEx.Message}");
                        }
                    }
                }
                else
                {
                    Log($"NAMING_STANDARD not loaded: {service.LastError}");
                    AddConnectWarning($"NamingStandards: {service.LastError}");
                }

                // Datatype whitelist = admin "Datatype Library" for the active config
                // (DATATYPE_LIBRARY rows WHERE CONFIG_ID = ActiveConfigId; config-scoped
                // since the 2026-07-02 admin migration). No STATUS gate. Empty set =
                // no restriction. Enforced on a column's Physical_Data_Type change in
                // ValidationCoordinatorService.
                var dtSvc = AllowedDatatypeService.Instance;
                if (dtSvc.Load())
                    Log($"Datatype library loaded: {(dtSvc.HasRestriction ? dtSvc.Allowed.Count + " allowed type(s) - " + string.Join(", ", dtSvc.Allowed.Select(a => a.Datatype + " [" + a.ParametrizationType + (a.AllowNonParametrized ? "/bare" : "") + "]")) : "no restriction (config whitelist empty)")}");
                else
                    Log($"Datatype library not loaded: {dtSvc.LastError}");
            }
            catch (Exception ex)
            {
                Log($"LoadNamingStandards error: {ex.Message}");
                AddConnectWarning($"NamingStandards: {ex.Message}");
            }
        }

        #endregion

        #region Table Processes

        private void LoadTablesComboBox()
        {
            try
            {
                cmbTables.Items.Clear();

                if (_session == null) return;

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                var tables = new List<string>();

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        string entityName = entity.Name ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch
                    {
                        try { tableName = entity.Name ?? ""; } catch { continue; }
                    }

                    if (!string.IsNullOrEmpty(tableName))
                    {
                        tables.Add(tableName);
                    }
                }

                tables.Sort();
                foreach (var table in tables)
                {
                    cmbTables.Items.Add(table);
                }

                if (cmbTables.Items.Count > 0)
                {
                    cmbTables.SelectedIndex = 0;
                }

                Log($"Loaded {tables.Count} tables into combo box");
            }
            catch (Exception ex)
            {
                Log($"LoadTablesComboBox error: {ex.Message}");
            }
        }

        private void BtnCreateTables_Click(object sender, EventArgs e)
        {
            try
            {
                if (cmbTables.SelectedItem == null)
                {
                    AddinMessageDialog.Show("Please select a table first.", "No Table Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!chkArchiveTable.Checked && !chkIsolatedTable.Checked)
                {
                    AddinMessageDialog.Show("Please select at least one option (Archive or Isolated).", "No Option Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string sourceTableName = cmbTables.SelectedItem.ToString();
                lblTableProcessStatus.Text = "Processing...";
                lblTableProcessStatus.ForeColor = System.Drawing.Color.DarkBlue;
                Application.DoEvents();

                // Suspend validation during table copy to avoid validation popups
                _validationCoordinatorService?.SuspendValidation();

                int tablesCreated = 0;

                try
                {
                    if (chkArchiveTable.Checked)
                    {
                        string archiveName = sourceTableName + "_ARCHIVE";
                        if (CreateTableCopy(sourceTableName, archiveName))
                        {
                            tablesCreated++;
                            Log($"Created archive table: {archiveName}");
                        }
                    }

                    if (chkIsolatedTable.Checked)
                    {
                        string isolatedName = sourceTableName + "_ISOLATED";
                        if (CreateTableCopy(sourceTableName, isolatedName))
                        {
                            tablesCreated++;
                            Log($"Created isolated table: {isolatedName}");
                        }
                    }
                }
                finally
                {
                    // Refresh snapshots BEFORE resuming so the freshly created entities and
                    // their attributes are seen as "known" baseline rather than "new" on the
                    // next tick. Without this, the resumed validation cycle treats every new
                    // column as new -> Glossary validation FAILED -> "PLEASE CHANGE IT" rename
                    // loop (verified 01:04:05 log), and PropertyApplicator fires its question
                    // wizard for each new entity. Snapshot calls are safe while suspended:
                    // both timers are gated and the calls just clear-and-rebuild internal dicts.
                    // Phase-1B (2026-05-06): switched from sync TakeSnapshot to deferred
                    // rebaseline. The bulk-create path used to freeze big-model UI for
                    // ~21 seconds here; the new flag lets the next MonitorTimer cycle
                    // silently re-baseline. Newly created entities are baselined the
                    // same way startup-loaded ones are.
                    try { _tableTypeMonitorService?.RebaselineDeferred(); }
                    catch (Exception ex) { Log($"BtnCreateTables: TableTypeMonitor RebaselineDeferred err: {ex.Message}"); }
                    try { _validationCoordinatorService?.RebaselineDeferred(); }
                    catch (Exception ex) { Log($"BtnCreateTables: ValidationCoord RebaselineDeferred err: {ex.Message}"); }

                    _validationCoordinatorService?.ResumeValidation();
                }

                if (tablesCreated > 0)
                {
                    lblTableProcessStatus.Text = $"Successfully created {tablesCreated} table(s)!";
                    lblTableProcessStatus.ForeColor = System.Drawing.Color.DarkGreen;
                    LoadTablesComboBox(); // Refresh list
                }
                else
                {
                    lblTableProcessStatus.Text = "No tables created. Check log for details.";
                    lblTableProcessStatus.ForeColor = System.Drawing.Color.DarkRed;
                }
            }
            catch (Exception ex)
            {
                Log($"BtnCreateTables_Click error: {ex.Message}");
                lblTableProcessStatus.Text = $"Error: {ex.Message}";
                lblTableProcessStatus.ForeColor = System.Drawing.Color.DarkRed;
            }
        }

        private bool CreateTableCopy(string sourceTableName, string newTableName)
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                // Find source entity
                dynamic sourceEntity = null;
                dynamic allEntities = modelObjects.Collect(root, "Entity");

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        string entityName = entity.Name ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch
                    {
                        try { tableName = entity.Name ?? ""; } catch { continue; }
                    }

                    if (tableName == sourceTableName)
                    {
                        sourceEntity = entity;
                        break;
                    }
                }

                if (sourceEntity == null)
                {
                    Log($"Source table '{sourceTableName}' not found");
                    return false;
                }

                // Check if target already exists
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        string entityName = entity.Name ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch
                    {
                        try { tableName = entity.Name ?? ""; } catch { continue; }
                    }

                    if (tableName == newTableName)
                    {
                        Log($"Table '{newTableName}' already exists - skipping");
                        AddinMessageDialog.Show($"Table '{newTableName}' already exists.", "Table Exists", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                }

                // Create new entity
                int transId = _session.BeginNamedTransaction($"CreateTableCopy_{newTableName}");
                try
                {
                    // Create new entity
                    dynamic newEntity = modelObjects.Add("Entity");
                    newEntity.Properties("Name").Value = newTableName;
                    newEntity.Properties("Physical_Name").Value = newTableName;

                    // Copy entity-level properties from source
                    CopyEntityProperties(sourceEntity, newEntity);

                    // Copy all attributes from source entity
                    dynamic sourceAttrs = modelObjects.Collect(sourceEntity, "Attribute");
                    if (sourceAttrs != null)
                    {
                        int attrCount = 0;
                        foreach (dynamic sourceAttr in sourceAttrs)
                        {
                            if (sourceAttr == null) continue;

                            try
                            {
                                // Create new attribute under new entity using Collect().Add() pattern
                                dynamic newAttr = modelObjects.Collect(newEntity).Add("Attribute");

                                if (newAttr != null)
                                {
                                    // Copy attribute properties
                                    CopyAttributeProperties(sourceAttr, newAttr);
                                    attrCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Error copying attribute: {ex.Message}");
                            }
                        }
                        Log($"Copied {attrCount} attributes to {newTableName}");
                    }

                    _session.CommitTransaction(transId);
                    Log($"Successfully created table copy: {newTableName}");
                    return true;
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"Error creating table copy: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"CreateTableCopy error: {ex.Message}");
                return false;
            }
        }

        private void CopyEntityProperties(dynamic source, dynamic target)
        {
            // Copy common entity properties
            string[] propertiesToCopy = { "Definition", "Note", "Owner" };

            foreach (string propName in propertiesToCopy)
            {
                try
                {
                    var value = source.Properties(propName).Value;
                    if (value != null && !string.IsNullOrEmpty(value.ToString()))
                    {
                        target.Properties(propName).Value = value;
                    }
                }
                catch { }
            }
        }

        private void CopyAttributeProperties(dynamic source, dynamic target)
        {
            // Copy attribute name and physical name
            try
            {
                string attrName = source.Name ?? "";
                target.Properties("Name").Value = attrName;
            }
            catch { }

            try
            {
                string physName = source.Properties("Physical_Name").Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%"))
                {
                    target.Properties("Physical_Name").Value = physName;
                }
            }
            catch { }

            // Copy other attribute properties
            string[] propertiesToCopy = {
                "Physical_Data_Type",
                "Logical_Data_Type",
                "Null_Option",
                "Definition",
                "Note",
                "Default_Value",
                "Parent_Domain_Ref"
            };

            foreach (string propName in propertiesToCopy)
            {
                try
                {
                    var value = source.Properties(propName).Value;
                    if (value != null)
                    {
                        string strValue = value.ToString();
                        if (!string.IsNullOrEmpty(strValue) && !strValue.StartsWith("%"))
                        {
                            target.Properties(propName).Value = value;
                        }
                    }
                }
                catch { }
            }
        }

        #endregion

        // Configuration Tab region removed (2026-05-09):
        //   - The Database/Schema/Name editor duplicated erwin's own model
        //     property editors and forced a redundant Save round-trip.
        //   - Methods LoadExistingValues, OnConfigChanged, BtnApply_Click and
        //     the EnableControls helper were retired with the tab.

        #region Approval / Review

        /// <summary>
        /// Triggers erwin's native "Review" toolbar button. Restored 2026-05-07
        /// because the smart-routing Generate DDL pipeline emits alter DDL but
        /// does not open erwin's built-in Mart compare-with-last-saved diff UI;
        /// the user wants both paths reachable from the add-in.
        /// </summary>
        private void BtnMartReview_Click(object sender, EventArgs e)
        {
            // ConfigContext guard - mirrors BtnAlterWizardProd_Click. Native
            // Review on a non-Mart PU triggers the same EM_GDM null-deref AV.
            if (!ConfigContextService.Instance.IsInitialized)
            {
                ErwinAddIn.ShowTopMostMessage(
                    "No configuration is defined for the model you are trying to load. Add-in controls will be disabled.",
                    "Review",
                    isError: false);
                Log("[REVIEW] Aborted: ConfigContext not initialized (no Mart mapping).");
                return;
            }
            // Local-file models can be config-initialized since 2026-06-13; the
            // Mart pipelines still must never run on them (EM_GDM AV).
            if (!ConfigContextService.Instance.IsMartModel)
            {
                ErwinAddIn.ShowTopMostMessage(
                    "Review requires a Mart-hosted model. The active model is a local file.",
                    "Review",
                    isError: false);
                Log("[REVIEW] Aborted: active model is a local file (Mart required).");
                return;
            }

            if (Services.Win32Helper.IsErwinMainWindowBlockedByModal())
            {
                ErwinAddIn.ShowTopMostMessage(
                    "erwin'in açık bir diyalogu var (Mart Save / Mart Open / Properties / ...).\n\n" +
                    "Lütfen önce o diyalogu kapatın, sonra Review'i tekrar çalıştırın.\n\n" +
                    "Açık diyalogla aynı anda Review tetiklenirse erwin'de doğrulanmış crash riski var.",
                    "Review",
                    isError: false);
                Log("[REVIEW] Aborted: erwin main window is blocked by a modal dialog.");
                return;
            }

            var hWnd = Services.Win32Helper.GetErwinMainWindow();
            if (hWnd == IntPtr.Zero)
            {
                ErwinAddIn.ShowTopMostMessage("erwin window not found.", "Review");
                return;
            }

            Log("[REVIEW] Triggering erwin Review...");
            bool invoked = Services.MartMartAutomation.PostMartReviewCommand(hWnd, Log);

            if (invoked)
                Log("[REVIEW] Review triggered.");
            else
            {
                Log("[REVIEW] 'Review' button not found.");
                ErwinAddIn.ShowTopMostMessage("'Review' button not found in erwin.", "Review");
            }
        }

#if !PACKAGED
        private void BtnAlterWizardProdDebug_Click(object sender, EventArgs e)
        {
            // Forward to the production handler; the sender check at the top
            // of BtnAlterWizardProd_Click flips DebugMode.Enabled for this run
            // only, so each click is self-contained.
            BtnAlterWizardProd_Click(sender, e);
        }

        // ---- RECON (Faz 2a, dev only): global hotkey Ctrl+Alt+D dumps the
        // current foreground window's control tree to the native-bridge log.
        // Global (system-wide) so it captures whatever erwin dialog is up while
        // the user manually walks Review / Complete Compare - the addin form is
        // not foreground during that flow.
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int ReconHotkeyId = 0x5245;     // 'RE'  -> Ctrl+Alt+D dump tree
        private const int ReconCmdHotkeyId = 0x5243;   // 'RC'  -> Ctrl+Alt+C toggle cmd capture
        private const int SpikeMdiHotkeyId = 0x534D;   // 'SM'  -> Ctrl+Alt+M dump MDI children
        private const int SpikePuHotkeyId = 0x5350;    // 'SP'  -> Ctrl+Alt+P dump session PUs
        private const int SpikeCloseHotkeyId = 0x5358; // 'SX'  -> Ctrl+Alt+X graceful-close active MDI child
        private const int SpikeColdFireHotkeyId = 0x5347; // 'SG'  -> Ctrl+Alt+G DDL-worker spike: cold-start fire (linchpin 0a)
        private const int SpikeColdArmHotkeyId = 0x5342;  // 'SB'  -> Ctrl+Alt+B DDL-worker spike: arm auto-fire under RDP disconnect (linchpin 0b)
        private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
        private const uint VK_D = 0x44;
        private const uint VK_C = 0x43;
        private const uint VK_M = 0x4D;
        private const uint VK_P = 0x50;
        private const uint VK_X = 0x58;
        private const uint VK_G = 0x47;
        private const uint VK_B = 0x42;
        private const int WM_HOTKEY = 0x0312;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (RegisterHotKey(this.Handle, ReconHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_D))
                    Log("[RECON] hotkey Ctrl+Alt+D registered - press over any dialog to dump its control tree.");
                else
                    Log($"[RECON] RegisterHotKey(D) failed (err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}) - Ctrl+Alt+D may be taken.");

                if (RegisterHotKey(this.Handle, ReconCmdHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_C))
                    Log("[RECON] hotkey Ctrl+Alt+C registered - press to START cmd/notify capture on the foreground window, press again to STOP.");
                else
                    Log($"[RECON] RegisterHotKey(C) failed (err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}) - Ctrl+Alt+C may be taken.");

                RegisterHotKey(this.Handle, SpikeMdiHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_M);
                RegisterHotKey(this.Handle, SpikePuHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_P);
                RegisterHotKey(this.Handle, SpikeCloseHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_X);
                Log("[SPIKE] hotkeys registered - Ctrl+Alt+M=dump MDI children, Ctrl+Alt+P=dump session PUs, Ctrl+Alt+X=graceful-close ACTIVE MDI child.");
                // DDL queue-worker Phase 0 linchpin spike (see ModelConfigForm.Spike.cs).
                RegisterHotKey(this.Handle, SpikeColdFireHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_G);
                RegisterHotKey(this.Handle, SpikeColdArmHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_B);
                Log("[SPIKE] DDL-worker hotkeys registered - Ctrl+Alt+G=cold-start fire (0a), Ctrl+Alt+B=arm auto-fire under RDP disconnect (0b).");
                // STEP-MODE toggle moved to the dev-only "Step Mode" button
                // (Ctrl+Alt+S collided with erwin's Scheduler shortcut).
            }
            catch (Exception ex) { Log($"[RECON] RegisterHotKey threw: {ex.Message}"); }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { UnregisterHotKey(this.Handle, ReconHotkeyId); } catch { /* best-effort */ }
            try { UnregisterHotKey(this.Handle, ReconCmdHotkeyId); } catch { /* best-effort */ }
            try { UnregisterHotKey(this.Handle, SpikeMdiHotkeyId); } catch { /* best-effort */ }
            try { UnregisterHotKey(this.Handle, SpikePuHotkeyId); } catch { /* best-effort */ }
            try { UnregisterHotKey(this.Handle, SpikeCloseHotkeyId); } catch { /* best-effort */ }
            try { UnregisterHotKey(this.Handle, SpikeColdFireHotkeyId); } catch { /* best-effort */ }
            try { UnregisterHotKey(this.Handle, SpikeColdArmHotkeyId); } catch { /* best-effort */ }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int hk = m.WParam.ToInt32();
                if (hk == ReconHotkeyId) { Services.NativeBridgeService.DumpForegroundWindowTree(Log); return; }
                if (hk == ReconCmdHotkeyId) { Services.NativeBridgeService.ToggleReconCommandCapture(Log); return; }
                if (hk == SpikeMdiHotkeyId)
                {
                    var h = Services.Win32Helper.GetErwinMainWindow();
                    Services.NativeBridgeService.DumpMdiChildren(h, Log);
                    return;
                }
                if (hk == SpikePuHotkeyId) { try { LogSessionPUs("SPIKE", Log); } catch (Exception ex) { Log($"[SPIKE] LogSessionPUs err: {ex.Message}"); } return; }
                if (hk == SpikeCloseHotkeyId)
                {
                    var h = Services.Win32Helper.GetErwinMainWindow();
                    Services.NativeBridgeService.GracefulCloseActiveMdiChild(h, Log);
                    return;
                }
                if (hk == SpikeColdFireHotkeyId) { SpikeColdStartFire(); return; }
                if (hk == SpikeColdArmHotkeyId) { SpikeColdStartArm(); return; }
            }
            base.WndProc(ref m);
        }
#endif

        // STEP-MODE state + toggle (RDP black-rectangle bisection diagnostic).
        // Compiled in ALL builds so the button can exist in packaged too, but in
        // packaged it is created hidden and only revealed via the existing
        // Ctrl+Shift+LeftClick gesture on the copyright label (same as the Debug
        // Log tab). In dev builds the button is visible by default. While armed,
        // the same-version Generate DDL teardown pops checkpoint MessageBoxes so
        // the user can pinpoint which step turns the screen black. Diagnostic
        // only - remove once the trigger is found.
        private bool _stepModeOn;

        private void BtnStepMode_Click(object sender, EventArgs e)
        {
            _stepModeOn = !_stepModeOn;
            Services.NativeBridgeService.SetStepMode(_stepModeOn, Log);
            btnStepMode.Text = _stepModeOn ? "Step Mode: ON" : "Step Mode: OFF";
            btnStepMode.BackColor = _stepModeOn
                ? System.Drawing.Color.FromArgb(192, 0, 0)     // red = armed
                : System.Drawing.Color.FromArgb(96, 96, 96);   // gray = off
            if (_stepModeOn)
            {
                ErwinAddIn.ShowTopMostMessage(
                    "STEP-MODE ACIK.\n\nSimdi 'Generate DDL' (From Mart, dirty vs last saved) butonuna bas. " +
                    "Teardown'daki her adimda bir checkpoint penceresi cikacak:\n\n" +
                    "[A] DDL yakalandi, wizard hala acik\n" +
                    "[B] IDCANCEL+WM_CLOSE gonderildi (wizard kapaniyor)\n" +
                    "[C] wizard destroy oldu\n" +
                    "[D] native teardown bitti\n" +
                    "[E] DDL sonuc penceresi acildi\n\n" +
                    "Her checkpoint'te diyagrama tiklayip SIYAHLIK olustu mu bak, sonra OK'a bas. " +
                    "Hangi checkpoint'te siyahlik ilk cikarsa tetikleyici o adim.\n\n" +
                    "Kapatmak icin butona tekrar bas.",
                    "Step Mode");
            }
        }

        private async void BtnAlterWizardProd_Click(object sender, EventArgs e)
        {
#if !PACKAGED
            // Two buttons share this handler in dev builds: production
            // (silent / fast) and debug (visible windows / 5 s pauses).
            // Mode is decided per click so back-to-back clicks of either
            // button don't inherit the other's state. NativeBridge picks
            // up the flag flip via SyncDebugVisibility, called at the next
            // line below where it already lives in the production path.
            Services.DebugMode.Enabled = (sender == btnAlterWizardProdDebug);
            // The debug button keeps the long human-read settle; reset it here in case a
            // prior forced-interactive production run lowered TransitionDelayMs (it is a
            // static, so it persists across clicks until reset).
            if (Services.DebugMode.Enabled) Services.DebugMode.TransitionDelayMs = 3000;
#endif
            if (!_isConnected || _currentModel == null)
            {
                ErwinAddIn.ShowTopMostMessage("No model connected.", "Generate DDL");
                return;
            }

            // ConfigContext guard. The Generate DDL flow does dynamic SCAPI
            // dispatch on the active PU (PropertyBag().Value("Locator"),
            // FEModel_DDL, ...). On a non-Mart-bound PU (e.g. PowerDesigner-
            // imported local .erwin) those calls crash deep in EM_GDM via
            // IDispatchInvoke unwind. The ConnectToModel degraded path
            // already disables btnAlterWizardProd in this case; this is a
            // defensive belt - if anything ever re-enables the button while
            // the context is still unresolved we still bail before touching
            // SCAPI. Verified host crash 2026-05-08 13:48.
            if (!ConfigContextService.Instance.IsInitialized)
            {
                ErwinAddIn.ShowTopMostMessage(
                    "No configuration is defined for the model you are trying to load. Add-in controls will be disabled.",
                    "Generate DDL",
                    isError: false);
                Log("[ROUTE] Aborted: ConfigContext not initialized (no Mart mapping).");
                return;
            }
            // Local-file models can be config-initialized since 2026-06-13; the
            // Mart pipelines still must never run on them (EM_GDM AV).
            if (!ConfigContextService.Instance.IsMartModel)
            {
                ErwinAddIn.ShowTopMostMessage(
                    "Generate DDL requires a Mart-hosted model. The active model is a local file.",
                    "Generate DDL",
                    isError: false);
                Log("[ROUTE] Aborted: active model is a local file (Mart required).");
                return;
            }

            // Admin gates: with every DDL source off the button is disabled by
            // ApplyDdlGenerationGates. Same defensive belt as the ConfigContext
            // guard above - covers the dev-only debug button (which shares this
            // handler and is never gated) and any future re-enable leak.
            if (!DdlSourceEnabled)
            {
                ErwinAddIn.ShowTopMostMessage("No DDL source is enabled.", "Generate DDL", isError: false);
                Log("[ROUTE] Aborted: no DDL source is enabled (admin gates all off).");
                return;
            }

            // Re-entry guard (2026-05-29, Option A): the From-DB / Review
            // pipelines now run their erwin teardown on a background task and
            // return early so the DDL renders ~11s sooner. _martMartPipelineActive
            // stays TRUE until that teardown finishes; block a second Generate
            // until then so two pipelines never overlap (the first one's wizard /
            // RD / CC / Save-Models dialogs are still being dismissed).
            if (_martMartPipelineActive)
            {
                ErwinAddIn.ShowTopMostMessage(
                    "Önceki DDL üretimi hâlâ tamamlanıyor (arka planda erwin temizliği sürüyor).\n\n" +
                    "Lütfen birkaç saniye bekleyip tekrar deneyin.",
                    "Generate DDL",
                    isError: false);
                Log("[ROUTE] Aborted: previous pipeline still finalizing in background (_martMartPipelineActive=true).");
                return;
            }

            // Concurrent-modal guard. Verified crash trigger 2026-05-07: user
            // had Mart Save dialog open, switched to addin, clicked Generate
            // DDL; NativeBridge sent Ctrl+Alt+T which misrouted to the modal,
            // wizard never opened, 15s timeout, then queued WinForms UIA
            // events flushed against erwin's broken UIA proxy on a
            // 280-entity diagram - 0xC0000005 in coreclr.dll.
            if (Services.Win32Helper.IsErwinMainWindowBlockedByModal())
            {
                ErwinAddIn.ShowTopMostMessage(
                    "erwin'in açık bir diyalogu var (Mart Save / Mart Open / Properties / ...).\n\n" +
                    "Lütfen önce o diyalogu kapatın, sonra Generate DDL'i tekrar çalıştırın.\n\n" +
                    "Açık diyalogla aynı anda Generate DDL tetiklenirse erwin'de doğrulanmış crash riski var (UIA proxy NULL deref).",
                    "Generate DDL",
                    isError: false);
                Log("[ROUTE] Aborted: erwin main window is blocked by a modal dialog.");
                return;
            }

            btnAlterWizardProd.Enabled = false;
            lblDDLStatus.Text = "Generating DDL...";
            lblDDLStatus.ForeColor = Color.Gray;
            Application.DoEvents();

            // The pipelines below synthesize REAL mouse clicks at absolute
            // screen coordinates (RD Apply-to-Right arrow, wizard Preview
            // jump); the add-in form must not sit under any of them. Hidden
            // here for manual AND worker runs; restored at the tail (manual)
            // or after the worker's model cleanup (queue jobs keep it hidden
            // through the Save-Models sweep, whose checkbox click is also a
            // raw mouse click).
            HideFormForAutomation("Generate DDL pipeline");

            bool martMode = rbFromMart.Checked;
            bool dbMode = rbFromDB.Checked;

            Action<string> log = msg =>
            {
                if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                else Log(msg);
            };

            string script = null;
            string err = null;
            // Set by the cross-version finally when the teardown could not
            // close the version copy the pipeline opened (the reconnect guard
            // then stays armed). Surfaced at the dialog tail below: appended
            // to the failure modal when there is one, otherwise shown as its
            // own warning - never silently dropped.
            string pipelineLeftoverWarning = null;
            // Track which pipeline produced the DDL so the approval-queue row
            // captured by Forms.DdlApprovalDialog reflects the actual source
            // (admin needs this to decide which environment / target server
            // the DDL applies to). Set in each branch below; final success
            // path passes it to ShowDDLResult.
            string sourceMode = null;
            // Precise no-diff status for the cross-version compare (set where
            // the right version 'v' is still in scope); the generic per-mode
            // texts at the tail are used when this stays null.
            string noDiffStatusOverride = null;

            // Faz 2.1 (active-vs-older-version via Review) reaches "Resolve
            // Differences" but does NOT capture DDL yet (Apply-to-Right + 1057
            // land in Faz 2.2/2.3). Reaching RD is a SUCCESS, not a failure, so
            // the post-routing must not render the generic "did not produce
            // DDL" error for this path. This flag distinguishes "intentionally
            // no DDL yet" from "pipeline failed to produce DDL".
            bool reviewReachedRd = false;

            // Auto-apply XML_OPTION TYPE='DDL' to erwin's FE options state
            // before invoking the Alter Script Wizard pipeline. The wizard
            // (driven by NativeBridgeService.GenerateAlterDdl / GenerateMart-
            // MartDdlViaOnFE) reads from the same per-model FE options that
            // FEModel_DDL writes when given an XML path - calling FEModel_DDL
            // with a throwaway DDL target file is the cheapest way to make the
            // wizard pick up the admin-authored options without native bridge
            // changes. Best-effort: failure here is logged but never blocks
            // the user (pipeline still runs with erwin defaults). All three
            // branches (From-DB / Mart-Same / Mart-Cross) benefit from a
            // single warm-up because the wizard's FE options state is shared.
            // Sync debug-mode visibility into the native bridge before
            // anything else. When DebugMode.KeepDialogsVisible is on the
            // bridge's HideWizardAggressive becomes a no-op for this run
            // so the user can watch the CC wizard / RD dialog / hidden
            // Alter Script Wizard transitions. Pushed here (not at addin
            // init) so the dev "Generate DDL (debug)" button's per-click
            // flag flip takes effect on the very next pipeline dispatch.
            //
            // 2026-06-03: the PIXEL-JUMP paths (From-DB + cross-version Review) REACH the
            // FE Alter Script wizard's Preview by mouse-sim on its nav pane, and the
            // cross-version path also walks a multi-page CC/Review navigation. Both REQUIRE
            // the interactive wizard mode the "Generate DDL (debug)" button uses: VISIBLE
            // windows (so the pixel-jump can actually click the wizard) + SETTLE pauses (so
            // each wizard page renders before the next step probes it). The production-
            // silent mode (hidden SW_HIDE wizard, no pauses) breaks BOTH - proven by the
            // user 2026-06-03 and three dumps: hidden wizard = no DDL; the in-memory list
            // probe races (id=1083 not found -> "did not reach Resolve Differences"); and
            // the half-driven state corrupts erwin and crashes its native diagram engine on
            // the ;Duplicate release (tsm15editor / EM_ERD null-deref 50400, then a heap-
            // corruption FailFast 5140). The debug button (DebugMode.Enabled=true) captures
            // DDL AND tears down clean. Same-version (OnFE) needs NONE of this (no CC
            // wizard, no pixel-jump) so it stays silent/fast. So force the interactive mode
            // for the pixel-jump routes here - the GREEN button then behaves like the debug
            // button FOR THOSE PATHS ONLY. The pauses are background Thread.Sleeps on the
            // worker thread (they pace the wizard, they do NOT hang the add-in UI), and
            // DebugMode.Enabled is re-decided per click (line ~3652) so this never leaks to
            // the next run. Trade-off accepted by the user: a briefly visible wizard + a
            // few seconds slower, in exchange for a working + crash-free compare.
            if (!Services.DebugMode.Enabled)
            {
                int rvForMode = martMode ? ParseRightVersion() : 0;
                int avForMode = martMode ? ParseActivePuVersion() : 0;
                bool crossVersionPixelJump = martMode && rvForMode > 0 && avForMode > 0 && rvForMode != avForMode;
                if (dbMode || crossVersionPixelJump)
                {
                    Services.DebugMode.Enabled = true;
                    // Faster than the debug button: the debug button keeps the long
                    // 3000 ms human-read settle; this production interactive run only
                    // needs each wizard page to RENDER before the next step probes it
                    // (the id=1083 race needs a real settle, but not 3 s). Tunable - if a
                    // page still races, raise it; if rock-solid, lower it.
                    Services.DebugMode.TransitionDelayMs = 1200;
                    log("[ROUTE] pixel-jump path (" + (dbMode ? "From-DB" : "cross-version v" + avForMode + " vs v" + rvForMode) +
                        ") - INTERACTIVE wizard mode (visible windows + 1200 ms settle, faster than the debug button's 3000 ms). " +
                        "Required for DDL capture + crash-free teardown; same-version OnFE stays silent.");
                }
            }
            Services.NativeBridgeService.SyncDebugVisibility(log);

            // DWM warm-up (2026-06-03): the production path hides the Alter Script
            // wizard before its first paint, so RDP composites an unrendered (black)
            // surface = the "black rectangle" leak. A single on-screen render warms
            // the DWM for the rest of the session. On the FIRST production Generate
            // this session (NOT the debug button - that is already visible), force
            // the wizard VISIBLE once (no debug pauses) so the DWM warms; mark warmed
            // after a successful capture below so later runs stay silent.
            bool dwmWarmupRun = !Services.DebugMode.KeepDialogsVisible && !_dwmWarmedThisSession;
            if (dwmWarmupRun)
            {
                Services.NativeBridgeService.SetWizardVisible(true, log);
                log("[DWM-WARMUP] first production Generate this session - keeping the Alter Script wizard VISIBLE once to warm the DWM surface (prevents cold-start black rectangles). Subsequent runs are silent.");
            }

            // "Only Selected Objects": tell the bridge to answer YES to erwin's
            // "Use current diagram selections?" Object Filter popup so the alter
            // script is scoped natively to the entities selected on the active
            // diagram. Unchecked (or disabled on the From-DB path) answers NO
            // (all changed objects). The checkbox is only enabled on the
            // From-Mart path (see OnRightSourceChanged), which is the only one
            // whose wizard raises this popup.
            Services.NativeBridgeService.SetUseDiagramSelection(
                chkFilterObjects.Enabled && chkFilterObjects.Checked, log);

            // FE option warm-up: write the admin XML_OPTION TYPE='DDL' into the
            // active model's FE-options state (FEModel_DDL retains the option
            // path; the Alter Script Wizard re-reads it on open). ALL three
            // pipelines open a wizard that compares against _currentModel
            // (same-version OnFE, From-DB via WM_COMMAND 1056, and the Review
            // active-vs-older path's eventual 1057 wizard), so warm up once
            // here for every path. Best-effort - failure is logged, not fatal.
            // No debug Pause here: the warm-up is a background COM call, not a
            // visible window transition, and stacking it with the pipeline's
            // first pause made the initial wait feel like a hang.
            WarmupDdlFEOptions(log);

            try
            {
                if (dbMode)
                {
                    // From-DB programmatic pipeline (FROM_DB_PLAN.md):
                    //   1. silent RE imports DB schema into a fresh in-memory PU
                    //      (locator='', PUs.Remove-safe per reference_rescript_pu_removable)
                    //   2. activate original Mart MDI child so left=dirty
                    //   3. drive CC wizard "Open Models in Memory" picker
                    //      against the RE'd PU as right
                    //   4. OnFE+GA captures alter DDL (left dirty vs right RE'd)
                    //   5. clean up: CloseSession + PUs.Remove(rePU,false)
                    Services.DebugMode.Pause("From-DB route selected - starting silent RE + CC pipeline", log);
                    var (dbScript, dbErr) = await RunFromDbDdlPipelineAsync(log);
                    Services.DebugMode.Pause("From-DB pipeline returned - about to render DDL", log);

                    // "Only Selected Objects" post-filter for the From-DB path
                    // (2026-05-30): the From-DB wizard does NOT raise a "Use
                    // current diagram selections?" popup, so the bridge's
                    // SetUseDiagramSelection toggle has no effect here. Instead
                    // we read the currently selected entities from erwin's
                    // Overview pane (Win32Helper.GetDiagramSelectedEntities),
                    // map them to Physical_Name via SCAPI, and regex-filter the
                    // captured DDL to only the matching ALTER blocks.
                    //
                    // No-selection path is treated as a hard error per user
                    // decision 2026-05-30 ("Hata + iptal"): if the user enabled
                    // the filter checkbox but has nothing selected on the
                    // diagram, we surface the message and skip the render
                    // entirely rather than dump the unfiltered DDL.
                    if (chkFilterObjects.Checked
                        && !string.IsNullOrWhiteSpace(dbScript)
                        && string.IsNullOrEmpty(dbErr))
                    {
                        var erwinMain = Services.Win32Helper.GetErwinMainWindow();
                        string modelName = _connectedModelName ?? "";
                        var selectedEntities = (erwinMain != IntPtr.Zero && !string.IsNullOrEmpty(modelName))
                            ? Services.Win32Helper.GetDiagramSelectedEntities(erwinMain, modelName)
                            : new List<string>();
                        if (selectedEntities == null || selectedEntities.Count == 0)
                        {
                            log("[From-DB] 'Only Selected Objects' checked but no entity is selected on the active diagram - aborting render.");
                            dbScript = null;
                            dbErr = "Lutfen diyagramda 1+ entity secip 'Generate DDL'i tekrar tiklayin (\"Only Selected Objects\" isaretli).";
                        }
                        else
                        {
                            var map = MapEntityNamesToPhysicalNames(selectedEntities);
                            var physNames = new HashSet<string>(map.Values, StringComparer.OrdinalIgnoreCase);
                            log($"[From-DB] filtering DDL by {physNames.Count} physical name(s): [{string.Join(", ", physNames)}] (logical: [{string.Join(", ", selectedEntities)}])");
                            string filtered = FilterFromDbDdlByPhysicalNames(dbScript, physNames, log);
                            if (string.IsNullOrWhiteSpace(filtered))
                            {
                                log("[From-DB] post-filter result is EMPTY (no DDL blocks reference the selected entities) - rendering empty result.");
                            }
                            dbScript = filtered;
                        }
                    }

                    script = dbScript;
                    err = dbErr;
                    sourceMode = "FromDB";
                }
                else if (martMode)
                {
                    int v = ParseRightVersion();
                    int activeV = ParseActivePuVersion();
                    // Source (Left) is always the open active model now (the
                    // cmbLeftModel single-entry combo was deleted 2026-05-30).
                    // Kept as a local for readability + so the dormant Faz-3
                    // (useCompleteCompare = !leftIsActive) branch still
                    // compiles.
                    bool leftIsActive = true;
                    // Same-version fast path requires the LEFT to be the active
                    // (dirty) model AND the RIGHT to be its own version: that is
                    // the proven "dirty vs last saved" OnFE route. Any other
                    // combination is a multi-version / Mart-vs-Mart compare that
                    // routes through the Review / Complete Compare wizards
                    // (Faz 2 / Faz 3).
                    bool sameVersion = leftIsActive && v > 0 && activeV > 0 && v == activeV;
                    sourceMode = sameVersion ? "FromMart-Same" : "FromMart-Cross";

                    if (sameVersion)
                    {
                        // Same version on both sides = "active dirty vs last
                        // saved". OnFE pipeline alone handles this with zero
                        // GUI flashes (no CC wizard, no Mart picker). This is
                        // the proven flash-free path used by Debug Log's
                        // "Normal Alter DDL (dirty vs save)" button.
                        log($"[ROUTE] Same version v{v} on both sides - OnFE fast path (dirty vs last saved, no flashes)");
                        // (FE options already warmed once before the routing.)
                        // Splash: the Next-loop + GA detour takes ~1-3 s on
                        // Mart-bound models; without the overlay the user
                        // sees a frozen ribbon and may double-click. Cross-
                        // version + From-DB paths already use the same
                        // ShowBusyOverlay helper - we just hadn't wired it
                        // into the fast path. Restored 2026-05-08.
                        // 2026-06-02: the alter wizard is HIDDEN again (the black
                        // rectangle was traced to an add-in HOOK, not the wizard's
                        // visibility - the 2026-06-01 "leave visible" workaround is
                        // reverted in the native bridge). With no visible wizard the
                        // user needs a progress cue, so restore the busy overlay
                        // (the original pre-Option-A behavior). It's fine for the
                        // overlay to be TopMost now - there is no visible wizard for
                        // it to occlude.
                        var fastOverlay = ShowBusyOverlay("Generating DDL, please wait...");
                        Services.DebugMode.Pause("Mart fast path - about to open hidden Alter Script Wizard", log);
                        try
                        {
                            script = await System.Threading.Tasks.Task.Run(() =>
                                Services.NativeBridgeService.GenerateAlterDdl(log));
                            Services.DebugMode.Pause("Mart fast path returned - wizard captured DDL (or null)", log);
                        }
                        finally
                        {
                            try
                            {
                                if (fastOverlay != null && !fastOverlay.IsDisposed)
                                {
                                    if (InvokeRequired) Invoke(new Action(() => { fastOverlay.Close(); fastOverlay.Dispose(); }));
                                    else { fastOverlay.Close(); fastOverlay.Dispose(); }
                                }
                            }
                            catch (Exception ex) { log($"[ROUTE] fast-path overlay close err: {ex.Message}"); }
                        }
                        // STEP [D]: native teardown + CloseHiddenWizard fully
                        // returned; report the DDL state so a null (wizard never
                        // opened) run is not mistaken for a valid leak test.
                        Services.NativeBridgeService.StepCheckpoint("D",
                            "GenerateAlterDdl dondu. DDL durumu: " +
                            (script == null ? "NULL (wizard acilmadi / yakalama basarisiz - BU RUN GECERSIZ TEST)"
                             : script.Length == 0 ? "BOS (fark yok)"
                             : script.Length + " karakter") +
                            ". Native teardown bitti, sonuc penceresi henuz acilmadi.", log);
                    }
                    else if (leftIsActive && LegacyCrossVersionEnabled)
                    {
                    // LEGACY cross-version path (active dirty vs older Mart
                    // version) via bridge CC + Apply-to-Right + OnFE. Gated OFF
                    // by default (LegacyCrossVersionEnabled=false) on 2026-05-28:
                    // it leaves an orphan right-version PU and can lock erwin
                    // (see reference_cross_version_orphan_unsolved). Faz 2
                    // replaces it with the Review-wizard-driven flow whose own
                    // lifecycle releases the loaded version cleanly. Kept here
                    // (not deleted) so the service methods stay referenced and
                    // we can compare behaviour while wiring the Review path.
                    // Production Mart-Mart zero-click flow:
                    //   1. Drive CC wizard + Apply-to-Right programmatically
                    //      (hidden dialogs), honoring cmbRightModel selection.
                    //   2. After EDR state is populated, call native
                    //      GenerateMartMartDdlViaOnFE to produce the alter DDL.
                    //   3. Close the CC/RD session regardless of outcome.
                    string catalog = ParseActivePuCatalog();
                    if (v <= 0 || string.IsNullOrEmpty(catalog))
                    {
                        err = "Could not derive right-version or Mart catalog path " +
                              "(need a Mart-opened model + valid right selection).";
                    }
                    else
                    {
                        // Suspend all monitoring services for the entire
                        // cross-version pipeline. Apply-to-Right + cross-mart
                        // PU loading triggers UdpRuntime/PropertyApplicator
                        // rename loops + ValidationCoordinator popup spam
                        // identical to the From-DB case (the lock-up that
                        // followed cross-version DDL generation in 04-26 logs
                        // is a strong suspect for this validation cascade).
                        // Same 9-layer guard pattern as RunFromDbDdlPipelineAsync.
                        _validationCoordinatorService?.SuspendValidation();
                        try { _tableTypeMonitorService?.StopMonitoring(); } catch (Exception ex) { log($"[XV] StopMonitoring err: {ex.Message}"); }
                        try { _validationService?.StopMonitoring(); } catch (Exception ex) { log($"[XV] ColumnValidation StopMonitoring err: {ex.Message}"); }
                        log("[XV] all monitoring services suspended for pipeline duration");

                        try
                        {
                        // DIAG: dump session PUs to pinpoint stale dirty
                        // right-version PUs left over from previous runs.
                        LogSessionPUs("PRE-RUN", log);
                        Services.MartMartAutomation.CCSession sess = null;
                        var overlay = ShowBusyOverlay("Generating DDL, please wait...");
                        Action<bool> toggle = visible =>
                        {
                            try
                            {
                                // Invoke is SYNCHRONOUS - we MUST wait for the
                                // UI thread to finish hiding the windows
                                // before returning; otherwise the click that
                                // immediately follows lands on our still-
                                // visible form instead of the RD dialog.
                                if (InvokeRequired)
                                    Invoke(new Action(() => ToggleBusyOverlay(overlay, visible)));
                                else
                                    ToggleBusyOverlay(overlay, visible);
                            }
                            catch { }
                        };
                        try
                        {
                            Services.DebugMode.Pause("Mart cross-version - about to drive CC wizard + Apply-to-Right", log);
                            sess = await Services.MartMartAutomation.DriveCCAndApplyAsync(v, catalog, log, toggle);
                            Services.DebugMode.Pause($"CC + Apply-to-Right returned (Applied={sess?.Applied}) - about to capture DDL via OnFE", log);
                            if (sess == null || !sess.Applied)
                            {
                                err = "Programmatic CC + Apply-to-Right failed. See Debug Log.";
                            }
                            else
                            {
                                script = await System.Threading.Tasks.Task.Run(() =>
                                    Services.NativeBridgeService.GenerateMartMartDdlViaOnFE(log));
                                Services.DebugMode.Pause($"GenerateMartMartDdlViaOnFE returned ({script?.Length ?? 0} chars)", log);
                            }
                        }
                        finally
                        {
                            Services.MartMartAutomation.CloseSession(sess, log);
                            try { overlay?.Close(); } catch { }
                        }

                        // Evict the orphan right-version PU that CC wizard
                        // loaded into the SCAPI session. Without this, the
                        // session accumulates a Model_1 v<right> PU on every
                        // cross-version run; erwin's main UI thread can hang
                        // on subsequent modal/popup lookups against the now
                        // stale engine references (verified lock-up
                        // 2026-04-27 01:13 after a successful v3-vs-v1 run).
                        //
                        // We wait 800 ms first because the CC engine clears
                        // its global state pointers asynchronously after
                        // ForceDestroyWizard returns - on the same UI thread
                        // that's about to receive PUs.Remove. Removing too
                        // early triggered AVs in earlier sessions; the
                        // 800 ms settle window has been stable in manual
                        // tests with the post-ForceDestroy CLEAN-EVT
                        // pipeline.
                        //
                        // CloseSelectedVersionPU is defensive: tries
                        // PersistenceUnits.Remove first, falls back to
                        // pu.Close() if Remove throws, with a background
                        // popup watcher to dismiss any "Save changes to
                        // <model>?" prompts the close path can spawn.
                        if (script != null && !string.IsNullOrEmpty(script))
                        {
                            await System.Threading.Tasks.Task.Delay(800);
                            LogSessionPUs("PRE-PU-REMOVE", log);
                            CloseSelectedVersionPU(v, catalog, log);
                            LogSessionPUs("POST-PU-REMOVE", log);
                        }
                        }
                        finally
                        {
                            // Resume monitoring on the UI/STA thread (2026-06-02
                            // crash fix - same as From-DB/Review). _validationService
                            // .StartMonitoring() does a synchronous SCAPI walk +
                            // arms WinForms timers; off a threadpool MTA worker that
                            // cross-apartment-marshals into erwin's STA RCWs during
                            // teardown -> use-after-free -> 0xC0000005 in coreclr.
                            // BeginInvoke keeps it in-apartment, non-blocking.
                            if (this.IsHandleCreated && !this.IsDisposed)
                            {
                                this.BeginInvoke((Action)(() =>
                                {
                                    try { _validationCoordinatorService?.ResumeValidation(); }
                                    catch (Exception ex) { try { log($"[XV] ResumeValidation err: {ex.Message}"); } catch { } }
                                    try { _tableTypeMonitorService?.StartMonitoring(); }
                                    catch (Exception ex) { try { log($"[XV] StartMonitoring err: {ex.Message}"); } catch { } }
                                    try { _validationService?.StartMonitoring(); }
                                    catch (Exception ex) { try { log($"[XV] ColumnValidation StartMonitoring err: {ex.Message}"); } catch { } }
                                    try { log("[XV] monitoring resumed (UI/STA thread)"); } catch { }
                                }));
                            }
                            log("[XV] pipeline complete - monitoring resume scheduled to background");
                        }
                    }
                    } // close: else if (leftIsActive && LegacyCrossVersionEnabled)
                    else
                    {
                        // Current model vs an OLDER Mart version, captured as alter
                        // DDL. Two entry wizards share the SAME pipeline (open right
                        // version, reach Resolve Differences, Apply-to-Right + cmd
                        // 1057 + capture, clean teardown):
                        //   - DIRTY model  -> Review wizard (Faz 2): the unsaved
                        //     buffer is the compare LEFT.
                        //   - CLEAN model  -> Complete Compare (Faz 3, WM_COMMAND
                        //     1082): the current model's last-saved baseline is the
                        //     LEFT. Routed by the dirty gate below, and ALSO chosen
                        //     in-flight when erwin refuses Review on a model the
                        //     gate thought dirty (the title asterisk over-reports).
                        bool useCompleteCompare = !leftIsActive;
                        string catalog = ParseActivePuCatalog();
                        if (v <= 0 || string.IsNullOrEmpty(catalog))
                        {
                            err = "Could not derive right-version or Mart catalog path for the version compare.";
                        }
                        else
                        {
                            // Dirty gate as a ROUTER, not a blocker (user
                            // requirement 2026-06-10: a clean model must still
                            // compare against an older version). A positively
                            // clean reading means erwin's Mart > Review would
                            // refuse ("There have been no changes to model
                            // since it was checked out"), so launch straight
                            // via Complete Compare (WM_COMMAND 1082) whose
                            // LEFT is the current model's last-saved state -
                            // for a clean checked-out model that is the SAME
                            // compare, with no dirty precondition. A dirty or
                            // unknown reading keeps the proven Review entry;
                            // if erwin still refuses (the title asterisk is
                            // known to over-report - it was set on every
                            // "clean" run on 2026-06-10 while Review said no
                            // changes), DriveCompareToResolveDifferences
                            // dismisses the refusal box and relaunches via
                            // Complete Compare in the same session.
                            if (!useCompleteCompare && !ProbeActiveModelDirtyForReview(log))
                            {
                                useCompleteCompare = true;
                                log("[REVIEW] dirty gate: model is positively clean - routing to Complete Compare (1082; Review would refuse).");
                            }

                            // This path is one of the pixel-jump routes forced into
                            // INTERACTIVE wizard mode at the top of this handler (visible
                            // windows + short settle pauses). keepVisible / SyncDebugVisibility
                            // therefore see DebugMode.Enabled=true here, exactly like the
                            // debug button - the wizard renders (pixel-jump can click it) and
                            // the CC/Review navigation settles between pages (no id=1083 race).
                            bool keepVisible = Services.DebugMode.KeepDialogsVisible;
                            Services.NativeBridgeService.SyncDebugVisibility(log);

                            // Suppress addin re-init while Review loads the version
                            // (the loaded PU must not trigger a reconnect/UDP popup).
                            _martMartPipelineActive = true;
                            StopReconnectTimer();
                            _validationCoordinatorService?.SuspendValidation();
                            try { _tableTypeMonitorService?.StopMonitoring(); } catch (Exception ex) { log($"[REVIEW] StopMonitoring err: {ex.Message}"); }
                            try { _validationService?.StopMonitoring(); } catch (Exception ex) { log($"[REVIEW] ColumnValidation StopMonitoring err: {ex.Message}"); }
                            log("[REVIEW] monitoring suspended + reconnect timer stopped for pipeline");

                            // Reconnect guard (2026-06-10): register the locator of
                            // the version PU this pipeline is ABOUT to open so the
                            // resumed reconnect tick can never mistake it for a user
                            // model switch (the tick's new-PU scan and the tab-switch
                            // path both skip pipeline-owned locators). Armed BEFORE
                            // Mart > Open: the timer is stopped above, but the guard
                            // must already be in place for the restart in the finally
                            // because the teardown cannot always prove the PU is gone
                            // (verified 02:08 + 02:37 incidents: leftover v1 adopted,
                            // UDP sync dirtied it, form re-bound to v1).
                            string ownedLocator = BuildVersionLocator(_lastConnectedLocator, v);
                            if (ownedLocator != null)
                            {
                                _pipelineOwnedLocators.Add(ownedLocator);
                                log($"[REVIEW] reconnect guard armed for pipeline-owned locator '{ownedLocator}'");
                            }
                            else
                            {
                                log($"[REVIEW] WARN: could not derive a v{v} locator from '{_lastConnectedLocator}' - reconnect guard NOT armed; a leftover v{v} copy would still trigger a model switch.");
                            }

                            var overlay = ShowBusyOverlay("Comparing versions, please wait...");
                            Services.MartMartAutomation.CCSession rsess = null;
                            try
                            {
                                // No DebugMode.Pause on the UI thread here: Thread.Sleep
                                // would freeze the addin form's message pump. The
                                // per-step observation pauses live INSIDE
                                // DriveReviewToResolveDifferences, which runs on this
                                // Task.Run worker thread, so they pace the wizard
                                // without blocking the UI.
                                rsess = await System.Threading.Tasks.Task.Run(() =>
                                    Services.MartMartAutomation.DriveCompareToResolveDifferences(v, catalog, keepVisible, useCompleteCompare, (Action<string>)log));

                                if (rsess != null && rsess.CompareNoDifferences)
                                {
                                    // Identical versions (job-4 incident 2026-07-11):
                                    // NOT an error. The empty script routes to the
                                    // informational no-diff status at the tail, and
                                    // the queue worker's FinishCurrentDdlJob
                                    // finalizes the row DONE with the no-diff note
                                    // instead of the old generic "did not reach
                                    // Resolve Differences" FAILED.
                                    script = string.Empty;
                                    noDiffStatusOverride =
                                        $"No differences between the compared versions ({(useCompleteCompare ? "last saved" : "active")} vs v{v}) - no alter DDL needed.";
                                    log($"[REVIEW] compare found NO differences ({(useCompleteCompare ? "last saved" : "active")} vs v{v}) - no alter DDL to generate.");
                                }
                                else if (rsess == null || rsess.ResolveDifferences == IntPtr.Zero)
                                {
                                    // Precise text beats the generic timeout one:
                                    // ReviewRefusedNoChanges here means erwin refused
                                    // Review (clean model) AND the in-session Complete
                                    // Compare relaunch ALSO failed to reach the wizard
                                    // (when the relaunch works, RD is reached and this
                                    // branch is never entered).
                                    err = (rsess != null && rsess.ReviewRefusedNoChanges)
                                        ? "erwin, Review karşılaştırmasını reddetti (modelde checkout'tan beri değişiklik yok) " +
                                          "ve Complete Compare ile yeniden deneme de wizard'a ulaşamadı.\n\n" +
                                          "Ayrıntı için Debug Log'a bakın ve tekrar deneyin."
                                        : "Compare wizard did not reach Resolve Differences (see Debug Log). " +
                                          "If the active model has no unsaved changes, Review cannot open.";
                                }
                                else
                                {
                                    log($"[REVIEW] reached Resolve Differences (RD=0x{rsess.ResolveDifferences.ToInt64():X}) - Apply-to-Right + alter DDL capture");
                                    reviewReachedRd = true;

                                    // Faz 2.2/2.3: Apply-to-Right (cascade active
                                    // v2 -> v1) then capture the alter DDL via the
                                    // proven RD-generic chain (1056 -> FE Wizard ->
                                    // Next-loop -> Preview -> GA hook). Runs while
                                    // RD is still OPEN; the outer finally tears the
                                    // session down afterwards.
                                    IntPtr capturedWizard = IntPtr.Zero;
                                    try
                                    {
                                        var applyOutcome = await System.Threading.Tasks.Task.Run(() =>
                                            Services.MartMartAutomation.ApplyToRightArrowOnReviewRd(rsess.ResolveDifferences, (Action<string>)log));
                                        if (applyOutcome == Services.MartMartAutomation.ApplyToRightOutcome.NoDifferences)
                                        {
                                            // Identical versions manifest as an RD whose diff grid
                                            // stays EMPTY (job-5 finding 2026-07-11) - erwin opens
                                            // RD even with nothing to resolve. NOT an error: empty
                                            // script routes to the informational tail; the queue
                                            // worker finalizes the row DONE with the no-diff note.
                                            script = string.Empty;
                                            noDiffStatusOverride =
                                                $"No differences between the compared versions ({(useCompleteCompare ? "last saved" : "active")} vs v{v}) - no alter DDL needed.";
                                            log($"[REVIEW] Resolve Differences grid is empty ({(useCompleteCompare ? "last saved" : "active")} vs v{v}) - no alter DDL to generate.");
                                        }
                                        else if (applyOutcome != Services.MartMartAutomation.ApplyToRightOutcome.Applied)
                                        {
                                            err = "Review Apply-to-Right did not register (no EDR tx) - see Debug Log.";
                                        }
                                        else
                                        {
                                            Services.NativeBridgeService.ClearCapturedDdl();
                                            Services.MartMartAutomation.ClearLastCapturedWizardDdl();
                                            // cmd 1057 = RIGHT Alter Script (enabled after
                                            // Apply-to-Right). 1056 is the LEFT button and is
                                            // DISABLED here - clicking it never opened the wizard.
                                            capturedWizard = await Services.MartMartAutomation
                                                .ClickRightAlterScriptInRdAsync(rsess.ResolveDifferences, 1057, (Action<string>)log);
                                            if (capturedWizard == IntPtr.Zero)
                                            {
                                                err = "Right Alter Script (cmd 1057) click failed - FE Wizard did not appear.";
                                            }
                                            else
                                            {
                                                // NOTE: hiding the FE wizard + RD during the Next-loop
                                                // (WS_EX_LAYERED) was tried 2026-05-29 and REVERTED - it
                                                // appeared to hang erwin on teardown (cf.
                                                // reference_layered_wizard_compositor_leak). Both stay
                                                // visible during the capture.
                                                // "Only Selected Objects" requires walking the Object
                                                // Filter wizard page so its "Use current diagram
                                                // selections? You have N entity selected" popup fires
                                                // (the bridge then answers Yes via the
                                                // SetUseDiagramSelection toggle). The default jump
                                                // SKIPS that page and would silently drop the
                                                // filter - regression verified 2026-05-30. When the
                                                // user did not enable the filter, keep using the
                                                // faster jump.
                                                bool onlySelected = chkFilterObjects.Enabled && chkFilterObjects.Checked;
                                                bool previewOk = await Services.MartMartAutomation
                                                    .ClickWizardPreviewTabAsync(capturedWizard, (Action<string>)log,
                                                        overlayToggle: null,
                                                        requireObjectFilterPass: onlySelected);
                                                if (!previewOk)
                                                {
                                                    err = "Wizard Next-loop / Preview tab failed.";
                                                }
                                                else
                                                {
                                                    string stashed = Services.MartMartAutomation.LastCapturedWizardDdl;
                                                    if (!string.IsNullOrEmpty(stashed))
                                                    {
                                                        script = stashed;
                                                        log($"[REVIEW] DDL stashed by Next-loop ({stashed.Length} chars)");
                                                        Services.MartMartAutomation.ClearLastCapturedWizardDdl();
                                                    }
                                                    else
                                                    {
                                                        log("[REVIEW] no stashed DDL - polling capture buffer");
                                                        for (int i = 0; i < 30; i++)
                                                        {
                                                            await System.Threading.Tasks.Task.Delay(200);
                                                            string ddl = await System.Threading.Tasks.Task.Run(() =>
                                                                Services.NativeBridgeService.ConsumeLastCapturedDdl());
                                                            if (!string.IsNullOrEmpty(ddl)) { script = ddl; log($"[REVIEW] DDL captured ({ddl.Length} chars) after {(i + 1) * 200}ms"); break; }
                                                        }
                                                        if (script == null) err = "DDL not captured within 6s after Next-loop.";
                                                    }
                                                    if (!string.IsNullOrEmpty(script))
                                                    {
                                                        sourceMode = useCompleteCompare ? "Mart-vs-Mart" : "Review-Active-vs-Version";
                                                        string leftLabel = useCompleteCompare ? "last saved" : "active";
                                                        lblDDLStatus.Text = $"Alter DDL captured ({leftLabel} vs v{v}, {script.Length} chars).";
                                                        lblDDLStatus.ForeColor = Color.DarkGreen;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        if (capturedWizard != IntPtr.Zero)
                                        {
                                            try { await Services.MartMartAutomation.DismissUseCurrentDiagramPopupAsync((Action<string>)log); }
                                            catch (Exception pex) { log($"[REVIEW] popup dismiss err: {pex.Message}"); }
                                            try { await Services.MartMartAutomation.CloseFEWizardCleanAsync(capturedWizard, (Action<string>)log); }
                                            catch (Exception dex) { log($"[REVIEW] wizard close err: {dex.Message}"); }
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                // GRACEFUL teardown (2026-05-29): IDCANCEL the RD +
                                // CC wizards (releases the ;Duplicate=YES copy), then
                                // WM_CLOSE the loaded v1 child and close its "Save
                                // Models" prompt WITHOUT saving (uncheck the single
                                // save row + OK), then drive "Mart Offline" to
                                // Save-to=Close. A single-row guard inside
                                // CloseReviewSession means the active dirty v2 (never
                                // listed in that dialog) is never touched. Runs on a
                                // bg thread so erwin's STA UI can surface its dialogs;
                                // the modal guard in ReconnectTimer_Tick + the
                                // pipeline flag staying TRUE keep erwin responsive.
                                // Close the busy overlay BEFORE the teardown: if it
                                // (a TopMost addin form) covers the Save Models dialog,
                                // the teardown's checkbox mouse-click lands on the
                                // overlay instead of the checkbox and silently no-ops
                                // (suspected splash-cover, 2026-05-29). Teardown
                                // dialogs flash briefly but that is acceptable.
                                try { overlay?.Close(); } catch { }

                                bool teardownModalUp = false;
                                if (rsess != null)
                                {
                                    try
                                    {
                                        // SCAPI on this STA thread deadlocks while an
                                        // erwin modal disables the main window (same
                                        // hazard the reconnect tick guards against,
                                        // verified 2026-05-29) - and the teardown has
                                        // DESIGNED exits that leave Save Models /
                                        // Mart Offline up for the user. Gate every
                                        // PU walk here on the modal check.
                                        if (!Services.Win32Helper.IsErwinMainWindowBlockedByModal())
                                            LogSessionPUs("REVIEW-PRE-CLOSE", log);
                                        await System.Threading.Tasks.Task.Run(() =>
                                            Services.MartMartAutomation.CloseReviewSession(rsess, (Action<string>)log));
                                        teardownModalUp = Services.Win32Helper.IsErwinMainWindowBlockedByModal();
                                        if (!teardownModalUp)
                                            LogSessionPUs("REVIEW-POST-CLOSE", log);
                                        else
                                            log("[REVIEW] erwin is modal-blocked after teardown (a close dialog was left for the user) - skipping SCAPI PU diagnostics to avoid the STA deadlock.");
                                    }
                                    catch (Exception ex) { log($"[REVIEW] teardown err: {ex.Message}"); }
                                }

                                // Reconnect guard resolution: disarm only when the
                                // POST-CLOSE PU scan PROVES the pipeline's version
                                // copy is gone. The old "v1 closed silently" verdict
                                // was a false positive (2026-06-10: the PU survived,
                                // the resumed tick adopted it, UDP sync dirtied it),
                                // so the guard stays armed on any doubt and the user
                                // is told explicitly - no silent leftover state. The
                                // tick also self-prunes the guard once the copy's PU
                                // disappears, so a manual close re-enables normal
                                // model-switch handling for that version. When a
                                // teardown modal is still up, the proof walk is
                                // skipped entirely (STA deadlock) - keep the guard
                                // and warn; the tick's prune owns the cleanup.
                                if (ownedLocator != null)
                                {
                                    if (!teardownModalUp && !IsPuLocatorStillLoaded(ownedLocator, log))
                                    {
                                        _pipelineOwnedLocators.Remove(ownedLocator);
                                        log($"[REVIEW] reconnect guard disarmed - pipeline-owned v{v} copy is gone.");
                                    }
                                    else
                                    {
                                        pipelineLeftoverWarning =
                                            $"The v{v} copy opened for the compare could not be closed automatically.\n\n" +
                                            "Close its tab manually WITHOUT saving (choose 'Save to: Close' if the " +
                                            "Mart Offline dialog asks).\n\n" +
                                            "Until then the add-in deliberately ignores that copy: no reconnect, " +
                                            "no UDP sync against it.";
                                        log($"[REVIEW] WARN: pipeline-owned v{v} copy {(teardownModalUp ? "unverifiable (modal up)" : "still loaded")} after teardown - reconnect guard stays armed; user will be warned to close it without saving.");
                                    }
                                }

                                // Resume monitoring fire-and-forget. Clear the
                                // pipeline guard + restart the reconnect timer LAST
                                // so no tick can touch SCAPI while a teardown dialog
                                // is still up (the modal guard backstops this too).
                                // Resume monitoring on the UI/STA thread (2026-06-02
                                // crash fix). _validationService.StartMonitoring()
                                // (ColumnValidationService) does a synchronous SCAPI
                                // walk + arms WinForms timers; off a threadpool MTA
                                // worker that cross-apartment-marshals into erwin's
                                // STA RCWs during CC/RD teardown -> use-after-free ->
                                // 0xC0000005 in coreclr. BeginInvoke keeps it in the
                                // form's STA apartment, non-blocking. (Same fix as the
                                // From-DB teardown.)
                                if (this.IsHandleCreated && !this.IsDisposed)
                                {
                                    this.BeginInvoke((Action)(() =>
                                    {
                                        try { _validationCoordinatorService?.ResumeValidation(); } catch (Exception ex) { try { log($"[REVIEW] ResumeValidation err: {ex.Message}"); } catch { } }
                                        try { _tableTypeMonitorService?.StartMonitoring(); } catch (Exception ex) { try { log($"[REVIEW] StartMonitoring err: {ex.Message}"); } catch { } }
                                        try { _validationService?.StartMonitoring(); } catch (Exception ex) { try { log($"[REVIEW] ColumnValidation StartMonitoring err: {ex.Message}"); } catch { } }
                                        try { log("[REVIEW] monitoring resumed (UI/STA thread)"); } catch { }
                                    }));
                                }
                                _martMartPipelineActive = false;
                                try { StartReconnectTimer(); } catch (Exception ex) { log($"[REVIEW] StartReconnectTimer err: {ex.Message}"); }
                                log("[REVIEW] pipeline complete - graceful teardown attempted (observe-mode); monitoring + reconnect resumed");
                            }
                        }
                    }
                }
                else
                {
                    // No source mode selected. Surface a clear error rather
                    // than silently running a different pipeline
                    // (feedback_no_silent_fallback): silent fallbacks mask
                    // failures and produce DDL the user did not ask for.
                    err = "No source selected. Choose 'From Mart' or 'From DB' first.";
                }
            }
            catch (Exception ex)
            {
                err = $"{ex.GetType().Name}: {ex.Message}";
            }

            // A visible wizard ran this session (debug button OR the one-time warm-up
            // that produced DDL) => the DWM surface is warm; later production runs can
            // stay silent without the black-rectangle leak.
            if (Services.DebugMode.KeepDialogsVisible || (dwmWarmupRun && !string.IsNullOrEmpty(script)))
            {
                _dwmWarmedThisSession = true;
            }

            // Leftover version copy after a failed teardown: fold the cleanup
            // instruction into the failure modal when there is one (a single
            // dialog beats two stacked modals), otherwise it is shown on its
            // own after the result chain below.
            if (pipelineLeftoverWarning != null && err != null)
            {
                err = err + "\n\n" + pipelineLeftoverWarning;
                pipelineLeftoverWarning = null;
            }

            if (err != null)
            {
                // The inline rtbDDLOutput viewer used to host long failure
                // diagnostics; with the black box removed we surface them via
                // a modal so the user actually sees the failure instead of a
                // truncated status-label one-liner. Debug Log keeps the full
                // stack for postmortem.
                lblDDLStatus.Text = $"Error: {err}";
                lblDDLStatus.ForeColor = Color.Red;
                // DDL queue worker runs unattended - suppress the modal (it would
                // block forever with no one to dismiss it); the outcome is written
                // to the queue row by FinishCurrentDdlJob below.
                if (!_ddlQueueActive)
                    ErwinAddIn.ShowTopMostMessage($"DDL generation failed:\n\n{err}", "Generate DDL", isError: true);
            }
            else if (reviewReachedRd && script == null)
            {
                // Review path reached Resolve Differences but produced no DDL
                // AND set no error (defensive). Status label already shows the
                // checkpoint; do NOT fall through to the generic "did not
                // produce DDL" error. When capture succeeds, script is set and
                // this guard is skipped so the DDL renders normally below.
                // (script == null, NOT IsNullOrEmpty: an explicit "" is the
                // no-differences outcome and must reach the informational
                // status branch below.)
            }
            else if (script == null)
            {
                string errTitle;
                string errBody;
                if (martMode)
                {
                    errTitle = "Mart-Mart automation failed (see Debug Log).";
                    errBody  = "Programmatic CC + Apply-to-Right did not produce DDL.\n\n" +
                               "Check Debug Log for the step that failed (CC wizard open, " +
                               "Mart picker navigation, Apply-to-Right click, or native DDL capture).";
                }
                else if (dbMode)
                {
                    errTitle = "From-DB automation failed (see Debug Log).";
                    errBody  = "From-DB CC + Apply-to-Right did not produce DDL.\n\n" +
                               "Check Debug Log for the step that failed (silent RE, MDI tab " +
                               "activation, Open-Models-in-Memory picker, Apply-to-Right, OnFE).";
                }
                else
                {
                    errTitle = "erwin did not return a DDL buffer (see Debug Log).";
                    errBody  = "Native bridge returned null. Check Debug Log for the failing step.";
                }
                lblDDLStatus.Text = errTitle;
                lblDDLStatus.ForeColor = Color.Red;
                if (!_ddlQueueActive)
                    ErwinAddIn.ShowTopMostMessage(errBody, "Generate DDL", isError: true);
            }
            else if (script.Length == 0)
            {
                // No diff = informational, not an error. One-line status is
                // sufficient; no need to pop a modal that the user has to
                // dismiss every time they verify "nothing's changed".
                string noDiffText = noDiffStatusOverride
                                   ?? (martMode ? "No differences between current model and Mart baseline."
                                     : dbMode  ? "No differences between current model and DB schema."
                                               : "No differences between model and last save.");
                lblDDLStatus.Text = noDiffText;
                lblDDLStatus.ForeColor = Color.OrangeRed;
            }
            else
            {
                // STEP [E]: all native teardown done; DDL is about to be rendered.
                // OK here -> the DdlApprovalDialog (our own WinForms modal) opens.
                Services.NativeBridgeService.StepCheckpoint("E", "Tum native teardown bitti. OK'a basinca DDL sonuc/approval penceresi (kendi WinForms modalimiz) ACILACAK. Bu pencere acilirken siyahlik olusursa tetikleyici bizim dialog, degilse erwin teardown'u.", Log);
                if (!_ddlQueueActive)
                {
                    ShowDDLResult(script, "Alter DDL", sourceMode);
                    Log($"DDL produced ({script.Length} chars). Review popup opened; user can submit to approval queue or cancel.");
                }
                else
                {
                    Log($"DDL produced ({script.Length} chars). DDL queue worker active - approval dialog skipped; result queued to the DB by FinishCurrentDdlJob.");
                }
                // The cross-version path now evicts the orphan right-version
                // PU before reaching here (see CloseSelectedVersionPU call
                // inside the cross-version branch). This DIAG dump is the
                // last-stop sanity check that no PUs remain leaked across
                // runs.
                if (martMode || dbMode)
                    LogSessionPUs("POST-RUN", log);
            }

            // Success (or no-diff) run that still left the pipeline's version
            // copy open: tell the user how to clean up. The failure path above
            // already folded this text into its own modal and nulled it.
            if (pipelineLeftoverWarning != null && !_ddlQueueActive)
            {
                ErwinAddIn.ShowTopMostMessage(pipelineLeftoverWarning, "Generate DDL - cleanup needed", isError: true);
            }

            // DDL queue worker: report this run's outcome (script / err) to the
            // claimed queue row and clear the active flag so the worker timer can
            // proceed (close the model + claim the next job). Defined in the
            // ModelConfigForm.DdlWorker partial. All user-facing modals above are
            // suppressed while _ddlQueueActive is set.
            if (_ddlQueueActive)
            {
                FinishCurrentDdlJob(script, err);
            }

            btnAlterWizardProd.Enabled = DdlSourceEnabled;

            // Restore the form hidden at pipeline start - MANUAL runs only.
            // For a queue job the worker state is already Cleanup here
            // (FinishCurrentDdlJob above): the form stays hidden through the
            // model-close mouse-sim sweep and OnDdlWorkerCloseComplete
            // restores it.
            if (_ddlWorkerState == DdlWorkerState.Idle)
                RestoreFormAfterAutomation("pipeline finished");

            // Bring the add-in form back to the foreground. The Ctrl+Alt+T
            // path pushes erwin's main window up, and the brief
            // wizard-open/close cycle leaves focus on erwin. The TopMost
            // flip is Windows' sanctioned way to jump past the foreground
            // lock owned by another window in the same process. From-DB
            // pipeline'inda 3 dialog cleanup (FE Wizard + RD + CC) sonrasi
            // erwin focus'u inatla tutuyor; double-flip + delay + ek
            // SetForegroundWindow ile zorla.
            try
            {
                this.TopMost = true;
                this.Activate();
                this.BringToFront();
                this.Focus();
                this.TopMost = false;

                // Double-flip after a small delay - cleanup'tan sonra
                // erwin'in async window activation mesajlari TopMost
                // flip'imizi over-write edebiliyor. Ikinci flip ile
                // garantile.
                await System.Threading.Tasks.Task.Delay(150);
                this.TopMost = true;
                this.Activate();
                this.BringToFront();
                this.Focus();
                Services.Win32Helper.SetForegroundWindowPublic(this.Handle);
                this.TopMost = false;
            }
            catch (Exception focusEx)
            {
                Log($"Restore focus failed: {focusEx.Message}");
            }
        }


        private bool HasModelUnsavedChanges()
        {
            try
            {
                var hWnd = Services.Win32Helper.GetErwinMainWindow();
                if (hWnd == IntPtr.Zero) return false;

                var sb = new System.Text.StringBuilder(512);
                Services.Win32Helper.GetWindowTextPublic(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                // erwin shows "*" in title when model has unsaved changes
                // e.g., "erwin DM - [Mart://Mart/KKB/KKB_Demo : v4 : ER_Diagram_164 * ]"
                bool hasChanges = title.Contains("* ]") || title.Contains("*]");
                Log($"DDL: Window title='{title}', unsaved changes={hasChanges}");
                return hasChanges;
            }
            catch { return false; }
        }

        /// <summary>
        /// Renders a produced DDL script in the approval-review popup. The
        /// inline dark RichTextBox that previously hosted SQL on the DDL
        /// Generation tab was removed 2026-05-16; successful DDL output now
        /// routes exclusively to <see cref="Forms.DdlApprovalDialog"/> so it
        /// can optionally be persisted to DDL_APPROVAL_QUEUE for the admin
        /// module to triage. Failure / "no diff" paths surface via a modal
        /// AddinMessageDialog (errors) or the status label (no-diff).
        /// </summary>
        /// <param name="sourceMode">Stored verbatim in DDL_APPROVAL_QUEUE.SOURCE_MODE
        /// when the user clicks Sent to Approve. Use the call-site-specific
        /// label (e.g. "FromMart-Same", "FromDB", "DDL-Diff-Version").</param>
        private void ShowDDLResult(string content, string label, string sourceMode)
        {
            if (string.IsNullOrEmpty(content))
            {
                lblDDLStatus.Text = "No output generated.";
                lblDDLStatus.ForeColor = Color.OrangeRed;
                return;
            }

            string displayContent = content;

            // Check if content is HTML (from CompleteCompare) - parse to text
            if (content.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                displayContent = ParseCompleteCompareHtml(content);

            // "Only Selected Objects" scoping is now done natively by erwin's
            // Alter Script Wizard Object Filter page (the bridge answers its
            // "Use current diagram selections?" popup with Yes via
            // SetUseDiagramSelection). The DDL arriving here is therefore
            // already scoped - no post-hoc regex DDL text filtering.
            int lineCount = displayContent.Split('\n').Length;
            lblDDLStatus.Text = $"{label}: {lineCount} lines. Review popup opened.";
            lblDDLStatus.ForeColor = Color.DarkGreen;

            ShowDdlForApproval(displayContent, sourceMode);
        }

        /// <summary>
        /// Opens the DDL approval-review popup against the current model's
        /// resolved ConfigContext. Caller is responsible for passing the
        /// already-filtered / already-HTML-parsed display content; this method
        /// only handles the dialog plumbing and metadata harvesting.
        /// </summary>
        private void ShowDdlForApproval(string ddl, string sourceMode)
        {
            if (string.IsNullOrWhiteSpace(ddl)) return;

            // Block DDL submission when the open model's DBMS diverges from its
            // configuration (detected at connect). The model and config must agree
            // before any governance-bound DDL leaves the add-in.
            if (_dbmsMismatch)
            {
                ErwinAddIn.ShowTopMostMessage(
                    _dbmsMismatchMessage ?? "The model's DBMS does not match its configuration. Please contact the Data Architecture team.",
                    "Model / Configuration DBMS Mismatch",
                    isError: true);
                return;
            }

            var ctx = Services.ConfigContextService.Instance;
            if (!ctx.IsInitialized || ctx.ActiveConfigId <= 0)
            {
                // Defensive: BtnAlterWizardProd_Click already guards against
                // unresolved ConfigContext, but the other two call sites
                // (PUWatcher version-diff, From-DB compare) could in theory
                // arrive here in degraded mode if the user races a model
                // switch. We surface a clear message instead of silently
                // dropping the DDL (no_silent_fallback rule).
                Log("ShowDdlForApproval: ConfigContext not initialized; cannot route to approval queue.");
                ErwinAddIn.ShowTopMostMessage(
                    "No configuration is defined for the model. Submission to the approval queue is unavailable.",
                    "DDL Review",
                    isError: true);
                return;
            }

            string modelName    = _connectedModelName ?? string.Empty;
            string modelLocator = _lastConnectedLocator ?? string.Empty;
            // DBMS label comes from the active PU's PropertyBag, NOT from the
            // CONFIG row. CONFIG.DBMS_VERSION_ID can drift from the model's
            // actual target server (admin classified FIBA as Oracle 21c but
            // the user's open model targets Oracle 19; matches erwin's own
            // status-bar label). The live value is what the alter DDL will
            // execute against, which is what the approval reviewer needs.
            string dbmsType = ReadActivePuTargetServer();

            // Approval routing is driven by the admin "Use Approvement Mechanism"
            // FLAG (USE_APPROVEMENT_MECHANISM, two-level corporate -> model), NOT by
            // whether approvers happen to be configured. Flag ON -> "Send to Approve"
            // (the row stays Pending; the admin approves and fires the REST callback
            // afterwards). Flag OFF -> "Send": the add-in saves the row as
            // 'ApprovedBySystem' and fires the REST callback itself. On a read error,
            // fail toward the SAFE path (approval, no auto-REST) and surface it -
            // never silently auto-send. (Default when unset = false, per the admin
            // seed.)
            bool approvalEnabled;
            try
            {
                approvalEnabled = Services.ConfigContextService.Instance
                    .GetEffectiveBool("USE_APPROVEMENT_MECHANISM", false);
                Log($"ShowDdlForApproval: config {ctx.ActiveConfigId} USE_APPROVEMENT_MECHANISM={approvalEnabled}");
            }
            catch (Exception ex)
            {
                Log($"ShowDdlForApproval: USE_APPROVEMENT_MECHANISM read failed ({ex.Message}); defaulting to approval path (Send to Approve, no auto-REST).");
                approvalEnabled = true;
            }

            using var dlg = new Forms.DdlApprovalDialog(
                ddlText:           ddl,
                configId:          ctx.ActiveConfigId,
                modelName:         modelName,
                modelLocator:      modelLocator,
                sourceMode:        sourceMode ?? "Unknown",
                dbmsType:          dbmsType,
                approvalEnabled:   approvalEnabled,
                log:               (Action<string>)Log,
                martSaveCallback:  SaveCurrentModelWithDescription);
            dlg.ShowDialog(this);
        }

        /// <summary>
        /// Programmatic Mart commit invoked by the DDL Review "Send to
        /// Approve" button before the approval-queue insert. Returns true
        /// to let the caller proceed with the insert; false aborts.
        ///
        /// Mechanism (2026-05-31 rewrite): drives erwin's own ribbon
        /// Mart > Save flow from inside the addin via
        /// <see cref="Services.MartSaveAutomation"/>. The earlier
        /// SCAPI-based pu.Save(martUri, "OVM=Yes") path was blocked
        /// in-process by "Mart user interface is active" (memory
        /// reference_scapi_mart_ui_active_block), and the bare
        /// pu.Save() variant silently wrote a LOCAL .erwin file
        /// without ever advancing the Mart version. The native UI path
        /// (PostMessage WM_COMMAND 1061 on XTPMainFrame, hidden
        /// description dialog auto-fill, IDOK) is what the user's manual
        /// click does anyway - we just drive it programmatically with
        /// the description prefilled and the dialog SW_HIDE'd so the
        /// user sees zero UI.
        ///
        /// Behaviour:
        ///   1. Dirty gate via <see cref="Services.VersionCompareService.ProbeDirty"/>.
        ///      Clean model = nothing to commit = return true so the
        ///      caller still inserts the queue row.
        ///   2. Resolve erwin's XTPMainFrame HWND via
        ///      <see cref="Services.Win32Helper.GetErwinMainWindow"/>.
        ///   3. Hand off to
        ///      <see cref="Services.MartSaveAutomation.SaveWithDescriptionAsync"/> -
        ///      that runs the WinEvent hook + dialog automation chain.
        ///   4. Re-probe dirty as positive-signal commit proof. After
        ///      a successful Mart commit pu.IsDirty MUST flip to false;
        ///      if it stays true the commit silently failed and we
        ///      surface that to the user (queue insert is aborted, the
        ///      status strip shows the error).
        /// </summary>
        private System.Threading.Tasks.Task<bool> SaveCurrentModelWithDescription(string description)
        {
            return System.Threading.Tasks.Task.Run(async () =>
            {
                if (_currentModel == null)
                {
                    Log("SaveCurrentModelWithDescription: no _currentModel; aborting.");
                    return false;
                }

                // Step 1: dirty gate. Clean model = nothing to commit.
                Services.VersionCompareService.DirtyProbe beforeDirty;
                try
                {
                    var probeService = new Services.VersionCompareService(_currentModel, (Action<string>)Log);
                    beforeDirty = probeService.ProbeDirty();
                }
                catch (Exception ex)
                {
                    Log($"SaveCurrentModelWithDescription: ProbeDirty threw {ex.GetType().Name}: {ex.Message} - assuming dirty.");
                    beforeDirty = new Services.VersionCompareService.DirtyProbe(true, "(probe-error)");
                }

                Log($"SaveCurrentModelWithDescription: dirty before save = {beforeDirty.IsDirty} (source={beforeDirty.Source})");

                if (!beforeDirty.IsDirty)
                {
                    Log("SaveCurrentModelWithDescription: model is clean (no unsaved changes) - skipping Mart save, proceeding with approval queue insert.");
                    return true;
                }

                // Step 2: resolve erwin's main XTPMainFrame HWND.
                IntPtr erwinMain = IntPtr.Zero;
                try
                {
                    erwinMain = Services.Win32Helper.GetErwinMainWindow();
                }
                catch (Exception ex)
                {
                    Log($"SaveCurrentModelWithDescription: GetErwinMainWindow threw {ex.GetType().Name}: {ex.Message}");
                }
                if (erwinMain == IntPtr.Zero)
                {
                    Log("SaveCurrentModelWithDescription: erwin XTPMainFrame HWND not resolvable - aborting.");
                    return false;
                }
                Log($"SaveCurrentModelWithDescription: erwin main HWND = 0x{erwinMain.ToInt64():X}");

                // Step 3: drive the native Mart Save flow. 15s timeout
                // covers cold Mart connections (manual flow ground-truth
                // 2026-05-31: ~10s end-to-end including SCAPI init).
                bool uiOk = await Services.MartSaveAutomation.SaveWithDescriptionAsync(
                    erwinMain, description ?? string.Empty, timeoutMs: 15000, (Action<string>)Log)
                    .ConfigureAwait(false);
                Log($"SaveCurrentModelWithDescription: MartSaveAutomation.SaveWithDescriptionAsync returned {uiOk}");
                if (!uiOk)
                {
                    Log("SaveCurrentModelWithDescription: UI automation reported failure - aborting (queue insert will NOT happen).");
                    return false;
                }

                // Step 4: post-save dirty re-probe as positive proof
                // that the commit actually flushed the dirty buffer.
                Services.VersionCompareService.DirtyProbe afterDirty;
                try
                {
                    var postProbe = new Services.VersionCompareService(_currentModel, (Action<string>)Log);
                    afterDirty = postProbe.ProbeDirty();
                }
                catch (Exception ex)
                {
                    Log($"SaveCurrentModelWithDescription: post-save ProbeDirty threw {ex.GetType().Name}: {ex.Message}");
                    afterDirty = new Services.VersionCompareService.DirtyProbe(false, "(post-probe-error)");
                }

                Log($"SaveCurrentModelWithDescription: dirty after save = {afterDirty.IsDirty} (source={afterDirty.Source})");

                // If the probe could not read any dirty property at all
                // (Source="(unknown)") we cannot tell if the commit
                // succeeded - trust the UI automation rc and proceed.
                // Otherwise: still-dirty after a successful UI flow
                // means erwin opened + IDOK'd the dialog but the commit
                // did not flush. That is a real failure - flag it.
                if (afterDirty.IsDirty && afterDirty.Source != "(unknown)" && afterDirty.Source != "(post-probe-error)")
                {
                    Log("SaveCurrentModelWithDescription: UI automation completed but PU is STILL dirty after the save - treating as commit failure.");
                    return false;
                }

                return true;
            });
        }

        /// <summary>
        /// Reads the active model's target DBMS for the approval popup header
        /// and DDL_APPROVAL_QUEUE.DBMS_TYPE column.
        ///
        /// IMPORTANT: erwin r10.10 stores the DBMS family in the PU's
        /// PropertyBag <c>Target_Server</c> field as a "DBMS Brand ID"
        /// (large 32-bit int, e.g. 1075858979 = Oracle, 1075859016 = SQL
        /// Server). The published mapping lives in the SCAPI API Reference
        /// Guide 15.0, "Property Bag Contents for Persistence Unit"
        /// section, lines 8920+ in docs/erwin-api-ref-15.txt. We decode it
        /// via <see cref="DbmsBrandNames"/> below.
        ///
        /// A previous attempt read <c>modelRoot.Properties("Target_Server").Value</c>
        /// (the same call erwin-admin's ScapiService makes) but on r10.10
        /// that returns 172 for every model regardless of DBMS - likely a
        /// metamodel property descriptor id, not the property value. Mirroring
        /// erwin-admin's small-int mapping (50..200) produced wrong labels
        /// (Oracle 19c on a SQL Server model in the 2026-05-16 bug).
        ///
        /// The major version comes from PropertyBag's <c>Target_Server_Version</c>,
        /// which is the INTERNAL engine major (SQL Server 15, Oracle 19), not
        /// the marketing label. <see cref="Services.DbmsLabelComposer"/> maps it
        /// to the label erwin itself shows - "Oracle 19c", "SQL Server 2019/2022"
        /// (erwin groups the 2019+2022 engines into one release-pair target).
        ///
        /// This reads the model's OWN PropertyBag only. It deliberately does NOT
        /// scrape erwin's status bar / window captions: that XTP custom-painted
        /// text is usually unreadable, and on the one occasion a caption DID match
        /// it grabbed the MDI title - a locator like
        /// "Mart://.../Sql Server Models/FIBA-TEST" whose FOLDER NAME contains a
        /// brand keyword - so the mismatch check compared the config DBMS against
        /// the locator and wrongly closed a correctly-configured model
        /// (2026-06-23). The model's brand ID + engine version is the reliable,
        /// unambiguous source; the config side is the admin DB. We never compare
        /// against window text.
        /// </summary>
        private string ReadActivePuTargetServer()
        {
            // Read the model's DBMS from the model's OWN properties (PropertyBag
            // Target_Server brand ID + Target_Server_Version engine major) and
            // compose the label from the documented brand-ID table. No status-bar /
            // window-caption scraping (see the remarks above for why it was removed).
            if (_currentModel == null) return null;
            try
            {
                long brandId = ReadPropertyBagLong(_currentModel, "Target_Server");
                string version = ReadPropertyBagString(_currentModel, "Target_Server_Version");
                Log($"ReadActivePuTargetServer: PropertyBag Target_Server={brandId}, Target_Server_Version='{version}'");

                if (brandId == 0) return null;
                string brand = DbmsBrandNames.TryGetValue(brandId, out string b) ? b : $"DBMS Brand {brandId}";
                return Services.DbmsLabelComposer.Compose(brand, version);
            }
            catch (Exception ex) { Log($"ReadActivePuTargetServer: PropertyBag probe threw: {ex.GetType().Name}: {ex.Message}"); }
            return null;
        }

        private static long ReadPropertyBagLong(dynamic pu, string key)
        {
            try
            {
                dynamic bag = pu.PropertyBag();
                object raw = bag?.Value(key);
                if (raw == null) return 0;
                if (long.TryParse(raw.ToString(), out long n)) return n;
            }
            catch { }
            return 0;
        }

        private static string ReadPropertyBagString(dynamic pu, string key)
        {
            try
            {
                dynamic bag = pu.PropertyBag();
                object raw = bag?.Value(key);
                return raw?.ToString();
            }
            catch { return null; }
        }

        // DBMS label composition moved to Services.DbmsLabelComposer (pure +
        // unit-tested) - the model/config mismatch label regressed twice on
        // raw engine-version leaks, so it now lives behind a tested seam.

        /// <summary>
        /// erwin Property Bag "Target_Server" DBMS Brand ID -> name. Verbatim
        /// from the SCAPI API Reference Guide 15.0, Property Bag Contents for
        /// Persistence Unit and Persistence Unit Collection, page 324-326
        /// (lines 8920..8985 in docs/erwin-api-ref-15.txt). These IDs are
        /// stable across erwin releases - the version (Oracle 19 vs 21,
        /// SQL Server 2016 vs 2019) is encoded separately in
        /// Target_Server_Version, not in the brand ID.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<long, string> DbmsBrandNames =
            new System.Collections.Generic.Dictionary<long, string>
            {
                { 1075858977L, "IBM Db2 for i" },
                { 1075858978L, "IBM Db2 for LUW" },
                { 1075858979L, "Oracle" },
                { 1075859006L, "IBM Informix" },
                { 1075859009L, "ODBC/Generic" },
                { 1075859010L, "Progress" },
                { 1075859013L, "SAS" },
                { 1075859016L, "SQL Server" },
                { 1075859017L, "SAP ASE" },
                { 1075859018L, "Teradata" },
                { 1075859019L, "IBM Db2 for z/OS" },
                { 1075859129L, "MySQL" },
                { 1075859130L, "SAP IQ" },
                { 1075859187L, "Apache Hive" },
                { 1075859190L, "MariaDB" },
                { 1075859193L, "Snowflake" },
                { 1075859196L, "MongoDB" },
                { 1075859199L, "Apache Cassandra" },
                { 1075859202L, "Couchbase" },
                { 1075859205L, "Apache Avro" },
                { 1075859208L, "JSON" },
                { 1075859211L, "Azure Synapse" },
                { 1075859214L, "Neo4j" },
                { 1075859217L, "ArangoDB" },
                { 1075859220L, "Apache Parquet" },
                { 1075859223L, "Amazon Keyspaces" },
                { 1075859226L, "Google BigQuery" },
                { 1075859229L, "Amazon DynamoDB" },
                { 1075859232L, "Databricks" },
                { 1075859235L, "PostgreSQL" },
                { 1075885345L, "OpenAPI" },
                { 1075918978L, "IBM Netezza" },
                { 1075918979L, "Amazon Redshift" },
                { 1075918980L, "AlloyDB for PostgreSQL" },
            };

        private string ParseCompleteCompareHtml(string html)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("-- CompleteCompare Results");
            sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // Parse HTML table rows
            int pos = 0;
            int diffCount = 0;

            while (true)
            {
                int trStart = html.IndexOf("<tr>", pos, StringComparison.OrdinalIgnoreCase);
                if (trStart < 0) break;
                int trEnd = html.IndexOf("</tr>", trStart, StringComparison.OrdinalIgnoreCase);
                if (trEnd < 0) break;

                string trContent = html.Substring(trStart, trEnd - trStart + 5);
                pos = trEnd + 5;

                var cells = new List<string>();
                int tdPos = 0;
                while (true)
                {
                    int tdStart = trContent.IndexOf("<td", tdPos, StringComparison.OrdinalIgnoreCase);
                    if (tdStart < 0) break;
                    int tdContentStart = trContent.IndexOf(">", tdStart) + 1;
                    int tdEnd = trContent.IndexOf("</td>", tdContentStart, StringComparison.OrdinalIgnoreCase);
                    if (tdEnd < 0) break;
                    string cellText = trContent.Substring(tdContentStart, tdEnd - tdContentStart)
                        .Replace("&nbsp;", " ").Replace("&amp;", "&").Trim();
                    cells.Add(cellText);
                    tdPos = tdEnd + 5;
                }

                if (cells.Count < 3) continue;

                string type = cells[0];
                string leftVal = cells.Count > 1 ? cells[1] : "";
                string status = cells.Count > 2 ? cells[2] : "";
                string rightVal = cells.Count > 3 ? cells[3] : "";

                if (status.Equals("Equal", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(status) && string.IsNullOrEmpty(leftVal) && string.IsNullOrEmpty(rightVal)) continue;

                string marker = "";
                if (!string.IsNullOrEmpty(leftVal) && string.IsNullOrEmpty(rightVal))
                    marker = "-- NEW: ";
                else if (string.IsNullOrEmpty(leftVal) && !string.IsNullOrEmpty(rightVal))
                    marker = "-- DROPPED: ";
                else if (status.Contains("Not Equal"))
                    marker = "-- CHANGED: ";

                sb.AppendLine($"{marker}{type}: {leftVal} | {status} | {rightVal}");
                diffCount++;
            }

            sb.Insert(sb.ToString().IndexOf('\n') + 1, $"-- {diffCount} difference(s) found\r\n");
            return sb.ToString();
        }

        // ApplySqlHighlighting / HighlightRegex used to live here as private
        // helpers and have been extracted to Forms.SqlHighlighter so the
        // approval popup can reuse the same VS-Code-flavoured palette. The
        // inline rtbDDLOutput viewer was removed at the same time.

        private void StartPUWatcher()
        {
            StopPUWatcher();
            try { _puWatcherInitialCount = _scapi.PersistenceUnits.Count; } catch { _puWatcherInitialCount = 1; }

            _puWatcherTimer = new Timer { Interval = 500 };
            _puWatcherTimer.Tick += PUWatcher_Tick;
            _puWatcherTimer.Start();
            Log($"DDL: PU watcher started (initial count={_puWatcherInitialCount})");
        }

        private void StopPUWatcher()
        {
            if (_puWatcherTimer != null)
            {
                _puWatcherTimer.Stop();
                _puWatcherTimer.Dispose();
                _puWatcherTimer = null;
            }
        }

        private void PUWatcher_Tick(object sender, EventArgs e)
        {
            try
            {
                int currentCount = _scapi.PersistenceUnits.Count;
                if (currentCount > _puWatcherInitialCount && _pendingDDLVersion > 0)
                {
                    Log($"DDL: PU count changed {_puWatcherInitialCount} -> {currentCount}. Auto-generating DDL...");
                    StopPUWatcher();

                    int ver = _pendingDDLVersion;
                    // FE option XML: resolve TYPE='DDL' from XML_OPTION on
                    // demand (XmlOptionLoaderService handles MetaRepo lookup
                    // + temp-file write + fallback). Returns null when no row
                    // is configured for the active config; GenerateDiffWith-
                    // Duplicate then treats it as "no XML" and erwin falls
                    // back to its own defaults.
                    string feOpt = ResolveDdlFEOptionXml(Log) ?? "";
                    _pendingDDLVersion = 0;

                    // Set selected version and generate DDL
                    DdlGenerationService.SetSelectedVersion(ver);

                    btnAlterWizardProd.Enabled = false;
                    lblDDLStatus.Text = $"v{ver} detected. Generating DDL diff...";
                    lblDDLStatus.ForeColor = Color.Gray;
                    Application.DoEvents();

                    _validationCoordinatorService?.SuspendValidation();
                    try
                    {
                        string diff = DdlGenerationService.GenerateDiffWithDuplicate(
                            _scapi, _currentModel, feOpt, (Action<string>)Log);
                        ShowDDLResult(diff, "DDL Diff", "DDL-Diff-Version");
                    }
                    catch (Exception ex)
                    {
                        lblDDLStatus.Text = $"Error: {ex.Message}";
                        lblDDLStatus.ForeColor = Color.Red;
                    }
                    finally
                    {
                        _validationCoordinatorService?.ResumeValidation();
                        btnAlterWizardProd.Enabled = DdlSourceEnabled;
                        this.Activate();
                        this.BringToFront();
                    }
                }
            }
            catch { }

            // Timeout after 60 seconds
            if (_puWatcherTimer != null)
            {
                // Simple timeout: stop after 120 ticks (60s at 500ms)
            }
        }

        // FilterByDiagramSelection (post-hoc regex DDL text filter) removed
        // 2026-05-28. "Only Selected Objects" is now honoured natively by
        // erwin's Alter Script Wizard Object Filter page: the bridge answers
        // its "Use current diagram selections?" popup with Yes (see
        // NativeBridgeService.SetUseDiagramSelection + native-bridge
        // DismissDiagramSelectionPopup). The old text filter only understood
        // the CompleteCompare "-- NEW:"/"-- ===="" marker format and silently
        // stripped the raw alter DDL produced by the OnFE fast path, which is
        // why selecting the option yielded an empty script. Win32Helper.
        // GetDiagramSelectedEntities is retained as a general utility.

        // BtnBrowseFEOption_Click removed 2026-05-27 together with txtFEOptionXml
        // (dead UI - value was never consumed by production Generate DDL paths).
        // Auto-apply uses XML_OPTION TYPE='DDL' resolved from the MetaRepo via
        // XmlOptionLoaderService.

        /// <summary>
        /// Resolve the FE Option XML for DDL generation: pulls the
        /// CONFIG-scoped row from MetaRepo's XML_OPTION (TYPE='DDL') and
        /// materializes it to a temp file. Returns the temp file path (caller
        /// is responsible for deleting it), or null when no XML is configured
        /// for the active config or any step fails.
        ///
        /// Connection source: <see cref="Services.DatabaseService.Instance.CreateConnection"/>
        /// which honours the addin's Bootstrap configuration (HKCU / HKLM
        /// fallback) - same MetaRepo connection GlossaryService uses for
        /// CONNECTION_DEF lookups. Falls back silently when MetaRepo is not
        /// configured or ConfigContext hasn't resolved yet, since the
        /// production pipelines treat null/empty XML as "no option XML".
        /// </summary>
        private string ResolveDdlFEOptionXml(Action<string> log)
        {
            try
            {
                if (!Services.ConfigContextService.Instance.IsInitialized)
                {
                    log?.Invoke("DDL FE Option: ConfigContext not initialized - skipping XML_OPTION lookup");
                    return null;
                }
                int activeConfigId = Services.ConfigContextService.Instance.ActiveConfigId;
                if (!Services.DatabaseService.Instance.IsConfigured)
                {
                    log?.Invoke("DDL FE Option: MetaRepo (Bootstrap) not configured - skipping XML_OPTION lookup");
                    return null;
                }
                string xml;
                using (var conn = Services.DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();
                    xml = Services.XmlOptionLoaderService.ResolveXml(conn, activeConfigId, "DDL", log);
                }
                if (string.IsNullOrEmpty(xml))
                {
                    log?.Invoke($"DDL FE Option: no XML_OPTION TYPE='DDL' row for CONFIG_ID={activeConfigId} (erwin will use its own defaults)");
                    return null;
                }
                // STABLE per-config path, NOT a GUID temp file. erwin's
                // FEModel_DDL stores this PATH (not the content) into the
                // model's FE-options state during WarmupDdlFEOptions, and the
                // Alter Script Wizard re-reads it later when the native-bridge
                // pipeline opens it. A GUID temp file that we deleted right
                // after warmup left the wizard with a dangling reference ->
                // erwin "xml was not found" -> wizard failed to open. That
                // only surfaced 2026-05-28 with a smaller option set: the
                // large XML's post-warmup delete lost the race against
                // erwin's still-open read handle (so the file survived), the
                // small XML's delete won the race (file gone, wizard broke).
                // A fixed path we overwrite each run and never delete removes
                // the race entirely and keeps exactly one file per config.
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"erwin_addin_ddl_fe_opt_cfg{activeConfigId}.xml");
                System.IO.File.WriteAllText(path, xml);
                log?.Invoke($"DDL FE Option: resolved from MetaRepo CONFIG_ID={activeConfigId} ({xml.Length} chars) -> {path}");
                return path;
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL FE Option: lookup threw (continuing without XML): {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Apply the admin-authored XML_OPTION TYPE='DDL' to erwin's per-model
        /// FE options state. We invoke <c>currentPU.FEModel_DDL(throwaway, xmlPath)</c>
        /// with a throwaway destination DDL file; the SCAPI method's side
        /// effect is to populate the model's internal FE options structure
        /// from the XML, which the Alter Script Wizard then reads when our
        /// native-bridge pipeline opens it. This is the cheapest way to
        /// "set wizard options programmatically" - the alternative would be
        /// to manipulate FEWPageOptions through the native bridge or simulate
        /// UIA clicks on the wizard's "Load Option Set..." button, both of
        /// which need significantly more code.
        ///
        /// Best-effort: any failure is logged and swallowed. The wizard then
        /// proceeds with whatever FE options the user last configured manually
        /// (or erwin's defaults on a fresh model). Only the throwaway DDL file
        /// is removed in the finally block; the option XML lives at a stable
        /// per-config path (see <see cref="ResolveDdlFEOptionXml"/>) that the
        /// wizard re-reads on open, so deleting it here would dangle erwin's
        /// retained reference.
        /// </summary>
        private void WarmupDdlFEOptions(Action<string> log)
        {
            if (_currentModel == null) return;

            string xmlPath = ResolveDdlFEOptionXml(log);
            if (string.IsNullOrEmpty(xmlPath)) return;

            string throwawayDdl = null;
            try
            {
                throwawayDdl = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"erwin_addin_fewarmup_{Guid.NewGuid():N}.sql");
                bool ok = false;
                try
                {
                    ok = _currentModel.FEModel_DDL(throwawayDdl, xmlPath);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"DDL FE Option warm-up: FEModel_DDL threw (continuing): {ex.GetType().Name}: {ex.Message}");
                    return;
                }
                if (ok)
                {
                    log?.Invoke("DDL FE Option warm-up: applied to model FE options (wizard will pick up on next open).");
                }
                else
                {
                    log?.Invoke("DDL FE Option warm-up: FEModel_DDL returned false - options may not have applied.");
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(throwawayDdl))
                {
                    try { if (System.IO.File.Exists(throwawayDdl)) System.IO.File.Delete(throwawayDdl); } catch { /* throwaway DDL cleanup best-effort */ }
                }
                // Intentionally do NOT delete xmlPath: erwin's FEModel_DDL
                // retained it as the model's FE-options source and the Alter
                // Script Wizard re-reads it when the pipeline opens. It is a
                // stable per-config path we overwrite each run, so there's
                // nothing to leak and deleting it dangles the wizard's ref.
            }
        }

        // BtnCopyDDL_Click was removed together with the inline rtbDDLOutput
        // viewer (2026-05-16). Copy now lives inside Forms.DdlApprovalDialog
        // alongside Cancel / Sent to Approve, operating on the popup's own
        // RichTextBox so users can copy a selection or the full DDL there.

        // DB connection state (From DB mode)
        private string _dbConnectionString = "";
        private string _dbPassword = "";
        private string _dbLabel = "";
        private long _dbTargetServer = 0;
        private int _dbTargetVersion = 0;
        private string _dbSchema = "";
        // Raw fields for in-process RE pipeline (DSN created at runtime).
        private string _dbHost = "";
        private string _dbName = "";
        private string _dbUser = "";
        private bool _dbUseWindowsAuth = false;
        private int _dbTypeCode = 0;
        private bool _dbUseNative = true;
        private string _dbDsnName = "";

        /// <summary>
        /// Production From-DB alter DDL pipeline (FROM_DB_PLAN.md).
        /// Reuses the cross-version Mart-Mart CC wizard infrastructure but
        /// with the right side being a silent-RE'd in-memory PU (locator='')
        /// instead of a Mart locator. The locator='' PU is removable via
        /// PUs.Remove(pu,false) without RPC_E_SERVERFAULT
        /// (reference_rescript_pu_removable.md), so the orphan/lock issue
        /// of cross-version Mart-Mart does not apply here.
        ///
        /// Steps:
        ///   1. validate DB connection + schema (prompt Configure if missing)
        ///   2. default tables = every entity in active model when none picked
        ///   3. silent RE creates a fresh PU populated from DB schema
        ///   4. ActivateMartMdiChild flips active erwin tab back to dirty Mart
        ///      so CC's left side is the dirty active model
        ///   5. DriveCCDbAndApplyAsync drives the CC wizard's "Open Models in
        ///      Memory" picker against the RE'd PU as right + Apply-to-Right
        ///   6. NativeBridgeService.GenerateMartMartDdlViaOnFE captures alter
        ///      DDL via OnFE+GA pipeline (reference_dirty_alter_ddl_pipeline)
        ///   7. CloseSession + PUs.Remove(rePU,false) leaves session clean
        /// </summary>
        private async System.Threading.Tasks.Task<(string script, string err)>
            RunFromDbDdlPipelineAsync(Action<string> log)
        {
            // Step 1: DB connection sanity. Empty conn string or stale (5= empty) -> Configure.
            bool needsConfigure = string.IsNullOrEmpty(_dbConnectionString)
                || _dbConnectionString.Contains("|5=\n") || _dbConnectionString.EndsWith("|5=");
            if (needsConfigure)
            {
                _dbConnectionString = "";
                BtnConfigureDB_Click(null, EventArgs.Empty);
                if (string.IsNullOrEmpty(_dbConnectionString))
                    return (null, "DB connection not configured.");
            }
            if (string.IsNullOrWhiteSpace(_dbHost) || string.IsNullOrWhiteSpace(_dbName) || _dbTypeCode == 0)
                return (null, "DB host/database/type missing - run Configure first.");

            // Step 1b: schema sanity. RE returns empty when no schema/owner
            // filter is set - so block early with a clear error instead of
            // discovering it after RE wastes ~5s.
            if (string.IsNullOrWhiteSpace(_dbSchema))
            {
                ErwinAddIn.ShowTopMostMessage(
                    "DB schema/owner is required for reverse engineering.\n\n" +
                    "Open 'Configure...' on the From DB row and set the schema filter (e.g. dbo).",
                    "From DB", isError: false);
                BtnConfigureDB_Click(null, EventArgs.Empty);
                if (string.IsNullOrWhiteSpace(_dbSchema))
                    return (null, "DB schema filter missing.");
            }

            // Step 1c: dialect must match. The RE'd PU's dialect (its
            // Target_Server encoded Int32) drives how erwin's CC engine
            // and OnFE/GA interpret schema objects. If the active model
            // and the user's Configure DB-type pick resolve to DIFFERENT
            // encoded Target_Server values, RE produces a cross-dialect
            // PU - CC compare then yields a meaningless diff that AVs
            // OnFE/GA when it tries to emit alter DDL (08:35:22 crash on
            // 24-table run, log: GA ENTER without GA wrote, native
            // process death). Surface this mismatch up front rather than
            // silent-overriding (feedback_no_silent_fallback) so the user
            // consciously fixes the DB-type pick.
            long activeTargetServer = 0;
            int activeTargetVersion = 0;
            dynamic pb = null;
            try
            {
                pb = _currentModel.PropertyBag();
                object tsRaw = pb?.Value("Target_Server");
                if (tsRaw != null && long.TryParse(tsRaw.ToString(), out long ts))
                    activeTargetServer = ts;
                object tsvRaw = pb?.Value("Target_Server_Version");
                if (tsvRaw != null && int.TryParse(tsvRaw.ToString(), out int tsv))
                    activeTargetVersion = tsv;
            }
            catch (Exception pbEx)
            {
                return (null, $"Could not read active model dialect: {pbEx.GetType().Name}: {pbEx.Message}");
            }
            finally
            {
                // Release the active-model PropertyBag RCW (2026-06-02 teardown
                // crash fix - see ReleaseComSafe). This is a transient bag read,
                // NOT the long-lived monitoring _session, so dropping the wrapper
                // is safe and matches ValidationCoordinatorService's practice.
                ReleaseComSafe(pb);
            }
            if (activeTargetServer == 0)
                return (null, "Active model has no Target_Server property - cannot derive dialect for RE.");
            log($"[From-DB] active dialect: Target_Server={activeTargetServer}, Version={activeTargetVersion}");
            log($"[From-DB] Configure DB type: code={_dbTypeCode}, target_server={_dbTargetServer}, version={_dbTargetVersion}");

            if (activeTargetServer != _dbTargetServer)
                return (null,
                    $"DB type does not match active model dialect.\n" +
                    $"  Active model Target_Server = {activeTargetServer}\n" +
                    $"  Configure dialog selection = {_dbTargetServer} (DB type code {_dbTypeCode})\n" +
                    "Open 'Configure...' on the From DB row and pick the DB type whose dialect matches the active model.");

            // Step 2: reverse-engineer EVERY entity in the active model. The
            // old "Select Tables..." picker was deleted 2026-05-30 - one-click
            // UX was confusing + the "Only Selected Objects" post-filter
            // (chkFilterObjects + diagram selection -> Physical_Name regex)
            // covers the narrower-scope use case without a separate dialog.
            var tableList = CollectModelTablePhysicalNames();
            if (tableList.Count == 0)
                return (null, "Active model has no entities to reverse-engineer.");
            log($"[From-DB] using all {tableList.Count} model table(s).");

            // ALL addin monitoring services suspended for the entire
            // pipeline. Validation alone wasn't enough: log 13:12-13:15
            // showed PropertyApplicator/UdpRuntime/GlossaryService trapped
            // in an infinite "PLEASE CHANGE IT" rename loop after Apply-
            // to-Right (every column rename fired Glossary validation
            // FAILED -> Renamed -> PLEASE_CHANGE_IT__N -> Renamed -> ...
            // each iteration spawned a popup; erwin UI thread overload =
            // crash). Stopping ALL monitoring breaks the loop completely.
            _validationCoordinatorService?.SuspendValidation();
            try { _tableTypeMonitorService?.StopMonitoring(); } catch (Exception ex) { log($"[From-DB] StopMonitoring err: {ex.Message}"); }
            try { _validationService?.StopMonitoring(); } catch (Exception ex) { log($"[From-DB] ColumnValidation StopMonitoring err: {ex.Message}"); }
            // Also stop the reconnect timer + raise the pipeline guard (same as
            // the Review/CC path). Without this, the silent RE'd in-memory model
            // is picked up by ReconnectTimer_Tick mid-pipeline, which re-inits
            // the addin against a model that has NO config row and pops the
            // "Add-in loaded with controls disabled" warning over the RE
            // (observed 2026-05-29). The guard makes the tick a no-op.
            _martMartPipelineActive = true;
            StopReconnectTimer();
            log("[From-DB] all monitoring services suspended + reconnect timer stopped for pipeline duration");

            // Overlay + transparency toggle. Skipped when the dev "Generate
            // DDL (debug)" button (#if !PACKAGED) was used to launch this
            // run, which flipped DebugMode.Enabled=true at the top of the
            // click handler. The old hardcoded DEBUG_FROMDB_VISIBLE flag is
            // now driven by the runtime DebugMode service so the dev can
            // toggle between fast/silent and slow/visible without recompile.
            // When DebugMode.KeepDialogsVisible is true: no busy overlay, no
            // transparency tricks, and the pipeline injects Pause()s between
            // phases so the user can actually read the screens.
            // From-DB is one of the pixel-jump routes forced into INTERACTIVE wizard mode
            // at the top of BtnAlterWizardProd_Click (visible windows + short settle
            // pauses), so dbgVisible reflects DebugMode.Enabled=true here, exactly like the
            // debug button: no busy overlay, and the FE wizard (made visible by the run's
            // SyncDebugVisibility) is reachable by the pixel-jump.
            bool dbgVisible = Services.DebugMode.KeepDialogsVisible;
            Form overlay = dbgVisible ? null : ShowBusyOverlay("Generating From-DB DDL, please wait...");
            Action<bool> overlayToggle = dbgVisible ? (Action<bool>)null : (visible =>
            {
                try
                {
                    if (InvokeRequired)
                        Invoke(new Action(() => ToggleBusyOverlay(overlay, visible)));
                    else
                        ToggleBusyOverlay(overlay, visible);
                }
                catch (Exception toggleEx)
                {
                    log($"[From-DB] overlay toggle failed: {toggleEx.Message}");
                }
            });

            dynamic rePU = null;
            string rePuName = null;
            Services.MartMartAutomation.CCSession sess = null;
            string script = null;
            string err = null;
            try
            {
                LogSessionPUs("PRE-RUN", log);

                // Step 3: silent RE on configured DB. Synchronous SCAPI call -
                // run on threadpool to keep UI responsive (and to match the
                // Mart-Mart same-version path that uses Task.Run for OnFE).
                log("[From-DB] silent RE starting...");
                // Pass the ACTIVE dialect (not the Configure-dialog DB type)
                // as the RE'd PU's Target_Server. _dbTargetServer/_dbTargetVersion
                // are kept only for the connection string. After Step 1c's
                // mismatch guard the two are equal anyway, but passing
                // activeTargetServer here documents the semantic: the RE'd
                // PU's dialect is dictated by the active model.
                long reTargetServer = activeTargetServer;
                int reTargetVersion = activeTargetVersion;
                rePU = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        // Physical model_type (not the silent_re_pattern's
                        // default "Combined"): the active mart model is
                        // viewed in Physical mode, and CC compare's
                        // actionSummary semantics in Logical-Combined yield
                        // CREATE-style "everything new" diffs that GA emits
                        // as full DDL instead of alter DDL (10:04 run:
                        // 4305-line CREATE TABLE dump rather than alter).
                        // User observation 2026-04-27: toolbar combo only
                        // shows "Logical" - need Physical for the alter DDL
                        // path.
                        return Services.DdlGenerationService.ReverseEngineerToSession(
                            scapi: _scapi,
                            currentPU: _currentModel,
                            host: _dbHost,
                            database: _dbName,
                            user: _dbUser,
                            password: _dbPassword,
                            useWindowsAuth: _dbUseWindowsAuth,
                            dbTypeCode: _dbTypeCode,
                            targetServerCode: reTargetServer,
                            targetServerVersion: reTargetVersion,
                            schema: _dbSchema,
                            selectedTables: tableList,
                            log: log,
                            modelType: "Physical");
                    }
                    catch (Exception reEx)
                    {
                        log($"[From-DB] silent RE threw: {reEx.GetType().Name}: {reEx.Message}");
                        return null;
                    }
                });

                if (rePU == null)
                    return (null, "Silent RE returned null - DB schema/filter mismatch?");

                try { rePuName = rePU.Name?.ToString() ?? ""; } catch { rePuName = ""; }
                log($"[From-DB] silent RE PU created: '{rePuName}'");

                // Diag: verify the RE'd PU actually picked up the dialect we
                // asked for. If RE engine overrode it (silent RE pattern's
                // ReverseEngineer-returned-null behavior is undocumented),
                // surface it so a future GA AV can be traced back to dialect
                // drift instead of being misdiagnosed.
                dynamic rePb = null;
                try
                {
                    rePb = rePU.PropertyBag();
                    object reTs = rePb?.Value("Target_Server");
                    object reTsv = rePb?.Value("Target_Server_Version");
                    log($"[From-DB] RE'd PU dialect: Target_Server={reTs}, Version={reTsv}");
                    if (reTs != null && long.TryParse(reTs.ToString(), out long actualTs)
                        && actualTs != activeTargetServer)
                    {
                        log($"[From-DB] WARNING: RE'd PU dialect drift - asked={activeTargetServer}, got={actualTs}");
                    }
                }
                catch (Exception diagEx)
                {
                    log($"[From-DB] dialect verify err: {diagEx.GetType().Name}: {diagEx.Message}");
                }
                finally
                {
                    // Release the RE'd-PU PropertyBag RCW (2026-06-02 teardown
                    // crash fix - see ReleaseComSafe).
                    ReleaseComSafe(rePb);
                }

                // Step 3b: VERIFY the RE'd PU is non-empty before driving CC.
                // SCAPI's RE returns success even when filter excluded all
                // tables (typical: schema-prefixed name vs bare-name mismatch).
                int entityCount = 0;
                int viewCount = 0;
                dynamic verifySess = null;
                dynamic mo = null;
                dynamic root = null;
                try
                {
                    verifySess = _scapi.Sessions.Add();
                    verifySess.Open(rePU, 0, 0);
                    mo = verifySess.ModelObjects;
                    root = mo.Root;
                    try
                    {
                        dynamic ents = mo.Collect(root, "Entity");
                        try
                        {
                            // Release each per-entity proxy as we count it - on a
                            // many-table model this foreach materializes hundreds
                            // of transient STA RCWs that must NOT outlive the
                            // session (see ReleaseComSafe).
                            foreach (dynamic e in ents) { entityCount++; ReleaseComSafe(e); }
                        }
                        finally { ReleaseComSafe(ents); }
                    }
                    catch (Exception entEx) { log($"[From-DB] Collect Entity err: {entEx.Message}"); }
                    try
                    {
                        dynamic views = mo.Collect(root, "View");
                        try
                        {
                            foreach (dynamic v in views) { viewCount++; ReleaseComSafe(v); }
                        }
                        finally { ReleaseComSafe(views); }
                    }
                    catch { /* views optional */ }
                }
                catch (Exception verEx)
                {
                    log($"[From-DB] verify session err: {verEx.GetType().Name}: {verEx.Message}");
                }
                finally
                {
                    // 2026-06-02 teardown crash fix (dump erwin.exe.29840.dmp):
                    // release the verify-session graph RCWs (root/mo + the
                    // per-item proxies above) BEFORE verifySess.Close(), while
                    // the native session objects are still alive. Close() tears
                    // those native objects down; any RCW left abandoned to GC is
                    // finalized later by the CLR finalizer thread, which
                    // cross-apartment-marshals IUnknown::Release onto erwin's STA
                    // AFTER the object is freed -> use-after-free AV escalated to
                    // a fatal ExecutionEngineException. The codebase's own
                    // ValidationCoordinatorService.ReleaseCom does this for the
                    // active-model walk; the From-DB pipeline never did, which is
                    // why this path crashed at teardown.
                    ReleaseComSafe(root);
                    ReleaseComSafe(mo);
                    if (verifySess != null)
                    {
                        try { verifySess.Close(); } catch { }
                        ReleaseComSafe(verifySess);
                    }
                    log($"[From-DB] verify-session RCWs released ({entityCount} entity + {viewCount} view proxies)");
                }
                log($"[From-DB] RE'd PU contents: {entityCount} entity(ies), {viewCount} view(s)");
                if (entityCount == 0)
                    return (null, "RE'd PU is empty - check schema/owner filter and table-name format.");

                // Step 4: switch the active erwin MDI tab back to the dirty
                // Mart model. After silent RE the RE'd PU is the foreground
                // child, which would make CC's Left=RE'd / Right=dirty. CC
                // pipeline assumes Left=dirty / Right=other.
                bool tabSwitched = await System.Threading.Tasks.Task.Run(() =>
                    Services.MartMartAutomation.ActivateMartMdiChild(log));
                if (!tabSwitched)
                    return (null, "Could not activate the active Mart MDI tab.");

                // Step 5: drive CC wizard with rePuName as the right-side
                // selection in the "Open Models in Memory" picker, then
                // Apply-to-Right.
                if (string.IsNullOrEmpty(rePuName))
                    return (null, "RE'd PU name is empty - cannot select it in CC picker.");

                // 2026-06-02 teardown crash fix (dump erwin.exe.46352.dmp).
                // Apply-to-Right (inside DriveCCDbAndApplyAsync below) MUTATES the
                // active model: erwin frees/reshapes entity objects in the live
                // model graph. Any SCAPI RCW captured BEFORE this point that is
                // already unreachable (notably the ~entity proxies that
                // CollectModelTablePhysicalNames walked at the top of the
                // pipeline and abandoned to GC) would, when the finalizer later
                // runs it, cross-apartment-marshal IUnknown::Release onto erwin's
                // STA against an object Apply-to-Right already freed -> use-after-
                // free AV in coreclr!SafeReleasePreemp -> fatal
                // ExecutionEngineException ~6s after the pipeline "completes"
                // (confirmed native stack: RCWCleanupList::CleanupAllWrappers ->
                // RCW::ReleaseAllInterfaces -> SafeReleasePreemp). Force those
                // abandoned RCWs to finalize NOW, while their objects are still
                // alive. Run the barrier on a background thread so the erwin STA
                // stays free to PUMP and service the cross-apartment Release the
                // finalizer marshals to it - calling WaitForPendingFinalizers on
                // the STA itself would deadlock (STA blocked, cannot service the
                // marshaled call). Explicit FinalReleaseComObject above handles
                // the RCWs still rooted in this method's locals; this barrier
                // handles the ones that already went unreachable in helpers.
                await DrainComFinalizersAsync(log, "pre-apply: active-model SCAPI proxies");

                log($"[From-DB] DriveCCDbAndApply(reModelName='{rePuName}')");
                sess = await Services.MartMartAutomation.DriveCCDbAndApplyAsync(rePuName, log, overlayToggle, dbgPauseBeforeApply: Services.DebugMode.KeepDialogsVisible);
                if (sess != null && sess.CompareNoDifferences)
                    // Model matches the DB schema (empty RD grid): informational,
                    // not a failure - the tail shows the dbMode no-diff status.
                    return (string.Empty, null);
                if (sess == null || !sess.Applied)
                    return (null, "Programmatic CC + Apply-to-Right failed - see Debug Log.");

                // Step 6: drive ELA::OnFE directly via the bridge orchestrator.
                //
                // 2026-04-27 trace verdict (reference_from_db_pipeline_working):
                // posting WM_COMMAND 1056 to RD spawns the wizard via erwin's
                // internal handler, but the handler reads its lastApplyEdrId
                // state from CC internals that our synthetic mouse click did
                // NOT populate. Result: OnFE(ms, true, 0) -> stripped DDL.
                //
                // Manual click captured flags=0x2370 (= EDR id 9072) from CC
                // state. We capture the same id from the bridge's EDR hook
                // (CCInsp_GetLastEdrStartId) and feed it explicitly to OnFE,
                // bypassing erwin's stateful handler.
                //
                // Bridge orchestrator handles wizard lifecycle entirely:
                //   - Spawns FEW worker, waits for FEWPageOptions ctor
                //   - Calls InvokePreviewStringOnlyCommand (no Next-loop)
                //   - GA detour captures DDL into capture buffer
                //   - Posts IDCANCEL to wizard so OnFE returns
                // No "Use current diagram?" popup because we never click Next.
                // 2026-04-27 forensic verdict (4-test triangulation):
                // Direct OnFE invocation (CCInsp_GenerateAlterDdlViaOnFE) does
                // NOT reproduce the manual button's behavior even with ms=LEFT
                // and flags=lastEdrStartId+1. erwin's internal Right-Alter-Script
                // handler does additional setup before invoking OnFE that we
                // can't see (RE intel; ELC2 internal prep, gbl_pxAs validation,
                // handler-specific call ordering). GA never fires.
                //
                // SOLUTION: trigger the handler itself via WM_COMMAND 1056
                // (the toolbar button's command id). Manual Apply by the user
                // (DEBUG_FROMDB_VISIBLE pause; user clicks Apply-to-Right by
                // mouse) populates the CC engine state correctly so that when
                // we post 1056, the handler reads valid state and invokes OnFE
                // with proper args -> wizard -> Next-loop -> Preview -> GA -> DDL.
                //
                // Same pattern as From-Mart cross-version DOES NOT apply here
                // because cross-version uses the bridge orchestrator (direct
                // OnFE) and a real mart-PU on the right; we have a synthetic
                // RE'd PU which has no saved baseline.
                IntPtr capturedWizard = IntPtr.Zero;
                try
                {
                    // cmd 1057 = RIGHT Alter Script (the RE'd DB is the RIGHT side;
                    // Apply-to-Right cascaded Mart->DB so 1057 is enabled). 1056
                    // is the LEFT button and is DISABLED here - clicking it never
                    // opened the FE Wizard (verified 2026-05-29, same as Review).
                    log("[From-DB] Posting WM_COMMAND 1057 (RIGHT Alter Script) to RD - invokes OnFE for the RE'd DB side.");
                    Services.NativeBridgeService.ClearCapturedDdl();
                    Services.MartMartAutomation.ClearLastCapturedWizardDdl();

                    capturedWizard = await Services.MartMartAutomation
                        .ClickRightAlterScriptInRdAsync(sess.ResolveDifferences, 1057, log);
                    if (capturedWizard == IntPtr.Zero)
                    {
                        err = "Right Alter Script (cmd 1057) click failed - FE Wizard did not appear.";
                    }
                    else
                    {
                        // NOTE: a "hide the FE wizard during the Next-loop"
                        // optimization was tried 2026-05-29 and REVERTED - hiding
                        // the wizard via WS_EX_LAYERED appeared to hang erwin on
                        // teardown (the user could not see the wizard and the run
                        // froze; cf. reference_layered_wizard_compositor_leak).
                        // The wizard stays visible during the Next-loop.
                        log("[From-DB] FE Wizard opened - jumping to Preview for DDL capture");
                        bool previewOk = await Services.MartMartAutomation
                            .ClickWizardPreviewTabAsync(capturedWizard, log, overlayToggle);
                        if (!previewOk)
                        {
                            err = "Wizard Next-loop / Preview tab failed.";
                        }
                        else
                        {
                            // ClickWizardPreviewTab stashes DDL into LastCapturedWizardDdl
                            // mid-loop to avoid double-consume race with the bridge
                            // capture buffer. Try stash first, fall back to buffer poll.
                            string stashed = Services.MartMartAutomation.LastCapturedWizardDdl;
                            if (!string.IsNullOrEmpty(stashed))
                            {
                                script = stashed;
                                log($"[From-DB] DDL stashed by Next-loop ({stashed.Length} chars)");
                                Services.MartMartAutomation.ClearLastCapturedWizardDdl();
                            }
                            else
                            {
                                log("[From-DB] no stashed DDL - polling capture buffer");
                                for (int i = 0; i < 30; i++)
                                {
                                    await System.Threading.Tasks.Task.Delay(200);
                                    string ddl = await System.Threading.Tasks.Task.Run(() =>
                                        Services.NativeBridgeService.ConsumeLastCapturedDdl());
                                    if (!string.IsNullOrEmpty(ddl))
                                    {
                                        script = ddl;
                                        log($"[From-DB] DDL captured ({ddl.Length} chars) after {(i + 1) * 200}ms");
                                        break;
                                    }
                                }
                                if (script == null)
                                    err = "DDL not captured within 6s after Next-loop.";
                            }
                        }
                    }
                }
                finally
                {
                    // 2026-06-02 teardown crash fix (CONFIRMED via full dump
                    // erwin.exe.48868.dmp + SOS !dumprcw = Accessibility.IAccessible
                    // / ToolkitPro CXTPAccessible). Drain the UIA-derived IAccessible
                    // RCWs (from AutomationElement.FromHandle on the CC picker + FE
                    // wizard) HERE, while the FE wizard (capturedWizard) AND the
                    // CC/RD windows are ALL still alive - the FE wizard is closed
                    // just below, the CC/RD in the outer finally. Draining now makes
                    // the finalizer's cross-apartment IAccessible::Release land on a
                    // live XTP object instead of a freed one. See DrainComFinalizersAsync.
                    await DrainComFinalizersAsync(log, "pre-FE-close: CC/FE wizard IAccessible");

                    if (capturedWizard != IntPtr.Zero)
                    {
                        try
                        {
                            await Services.MartMartAutomation
                                .DismissUseCurrentDiagramPopupAsync(log);
                        }
                        catch (Exception popEx)
                        {
                            log($"[From-DB] popup dismiss err: {popEx.GetType().Name}: {popEx.Message}");
                        }
                        try
                        {
                            await Services.MartMartAutomation
                                .CloseFEWizardCleanAsync(capturedWizard, log);
                        }
                        catch (Exception destEx)
                        {
                            log($"[From-DB] wizard close err: {destEx.GetType().Name}: {destEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                err = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                // Step 7: cleanup. Order matters: close CC/RD first so the CC
                // engine's transient ms references are released before we
                // remove the underlying PU.
                //
                // Note (2026-05-29): a "background the teardown so the DDL
                // approval modal opens ~11s sooner" optimization (Option A) was
                // implemented, then reverted at the user's request: the modal
                // opened EARLY, then erwin's Save Models mouse-sim stole the
                // foreground, then the modal returned to front. That appear ->
                // behind -> reappear sequence read as "the dialog is showing
                // twice" and a user could mistakenly think the run was finished
                // on the first appearance. Holding the modal until teardown
                // fully completes is the cleaner UX (one clean appearance).
                // The 3 dead-wait trims (UseCurrentDiagram 500->200, FE wizard
                // IDCANCEL fixed-1500 -> poll, Mart Offline 1500->500) STAY -
                // they shave ~2.3s without flicker.

                // 2026-06-02 teardown crash fix (dump erwin.exe.33336.dmp):
                // release the RE'd PU's RCW FIRST - here at the top of the
                // finally, while the CC wizard, the RE'd model child and the
                // PU's native object are ALL still alive, so the drop is a clean
                // in-apartment refcount decrement. rePU was the LAST abandoned
                // SCAPI RCW held by a method local after the verify-session graph
                // was already released; if left to GC, ~6s after this method
                // returns the CLR finalizer thread cross-apartment-releases it
                // onto erwin's STA AFTER teardown has torn the RE'd model down ->
                // use-after-free -> fatal ExecutionEngineException (same dump
                // signature, just moved later because this RCW outlives the
                // others). NOTE: an RCW refcount drop is NOT PUs.Remove - the PU
                // still lingers as a session orphan (Remove would invalidate the
                // active mart root, hence it stays skipped below).
                // 2026-06-02 teardown crash fix (ROOT-CAUSE, via user insight +
                // SCAPI research). The ONLY remaining UIA in the whole teardown is
                // the "Save Models" dialog raised by WM_CLOSE on the DIRTY RE'd
                // model (confirmed: [SM-CLOSE] is the sole UIA in the teardown log;
                // every wizard close is Win32 IDCANCEL). Its XTP IAccessible RCWs
                // (full dump erwin.exe.48868.dmp = ToolkitPro CXTPAccessible /
                // Accessibility.IAccessible) are abandoned to GC; when the dialog
                // is destroyed the finalizer cross-apartment-releases a freed
                // IAccessible on erwin's STA -> AV -> fatal ExecutionEngineException.
                // SUPPRESS the dialog at the source by marking the throwaway RE'd
                // model UNMODIFIED. NOTE rePU.Save(tempPath) does NOT work - it is
                // a Save-AS copy and does not reset the dirty flag (verified: saved
                // =True yet the '*' + dialog persisted). The correct lever is the
                // documented read/WRITE property ISCPersistenceUnit.DirtyBit /
                // ISCModelSet.DirtyBit (erwin-api-ref-15 6399-6403 + 6241-6245;
                // live PROPPUT memid 0x60020002 on r10.10). Clear it on the model
                // set (which drives the MDI '*' title) AND the PU so WM_CLOSE finds
                // nothing dirty -> no Save Models dialog -> CloseSaveModelsWithout-
                // Saving never runs -> no UIA RCW. If erwin keeps a separate
                // per-MDI dirty state and ignores this, the old WM_CLOSE +
                // uncheck/OK fallback still runs (no worse). Release the ModelSet
                // RCW in-apartment here (same discipline as rePU) so it does not
                // become another dangling cross-apartment finalizer release.
                if (rePU != null)
                {
                    try
                    {
                        dynamic reMs = null;
                        try { reMs = rePU.ModelSet(); }
                        catch (Exception msEx) { log($"[From-DB] rePU.ModelSet() err: {msEx.Message}"); }
                        if (reMs != null)
                        {
                            try { reMs.DirtyBit = false; }
                            catch (Exception e1) { log($"[From-DB] ModelSet DirtyBit clear err: {e1.Message}"); }
                            ReleaseComSafe(reMs);
                        }
                        try { rePU.DirtyBit = false; }
                        catch (Exception e2) { log($"[From-DB] PU DirtyBit clear err: {e2.Message}"); }
                        log("[From-DB] RE'd model DirtyBit=false (PU + ModelSet) - aims to suppress the Save Models dialog (the sole teardown UIA / IAccessible crash source).");
                    }
                    catch (Exception dbEx)
                    {
                        log($"[From-DB] DirtyBit clear failed ({dbEx.GetType().Name}: {dbEx.Message}) - Save Models dialog may still appear (fallback path).");
                    }
                }

                ReleaseComSafe(rePU);
                rePU = null;

                if (sess != null)
                {
                    try
                    {
                        await Services.MartMartAutomation
                            .CloseDbCCSessionCleanAsync(sess, log);
                    }
                    catch (Exception closeEx)
                    {
                        log($"[From-DB] CloseDbCCSessionClean failed: {closeEx.GetType().Name}: {closeEx.Message}");
                    }
                }

                // Close the busy overlay BEFORE closing the RE'd model: in
                // production the overlay is a TopMost form that can cover the
                // "Save Models" dialog, so the uncheck mouse-click would land
                // on the overlay and silently no-op.
                try { overlay?.Close(); } catch { }

                // Discard the throwaway RE'd model (WM_CLOSE + "Save Models"
                // uncheck+OK). Done AFTER the CC/RD teardown so the wizard no
                // longer references it.
                // 2026-06-02 (user decision): INITIATE the close - WM_CLOSE the
                // RE'd model child so erwin raises its "Save Models" dialog - but
                // do NOT auto-dismiss that dialog; leave it for the USER to click.
                // The dialog is the SOLE remaining teardown UIA: auto-dismissing it
                // (CloseSaveModelsWithoutSaving) needs UI Automation whose abandoned
                // XTP IAccessible RCWs crash erwin's finalizer (full dump
                // erwin.exe.48868.dmp = ToolkitPro CXTPAccessible /
                // Accessibility.IAccessible). Every in-process way to dismiss it
                // WITHOUT UIA was exhausted: rePU.Save (Save-As, does not clear
                // dirty); ISCPersistenceUnit/ModelSet.DirtyBit=false (dispatch
                // succeeds but erwin's GUI keeps a SEPARATE per-MDI dirty flag and
                // still prompts - verified, the '*' persisted); and the dialog's OK
                // ignores WM_COMMAND IDOK + its XTP grid only reads via UIA. So the
                // WM_CLOSE goes out, the dialog appears, and the user dismisses it
                // (a plain user click = erwin's own UI, no .NET UIA, no abandoned
                // RCW, no crash). The model actually closes once the user clicks
                // Don't Save, so tabs do not accumulate. A future bridge-based GUI
                // SetModifiedFlag could re-enable full auto-close.
                try
                {
                    await System.Threading.Tasks.Task.Run(() =>
                        Services.MartMartAutomation.CloseReModelMdiChild(rePuName, log, win32AutoDismiss: true));
                }
                catch (Exception ex) { log($"[From-DB] CloseReModelMdiChild err: {ex.Message}"); }

                // PU.Remove on a CC-touched silent RE'd PU invalidates the active
                // mart PU's root object -> Session lost cascade -> next user click
                // crash. It stays SKIPPED; the RE'd PU lingers as a session orphan.
                // Its managed RCW was already released at the top of this finally
                // (a refcount drop, NOT a Remove), which fixes the teardown crash
                // without removing the PU from the session.
                log("[From-DB] PU.Remove skipped (would invalidate active mart PU root); RE'd PU RCW released, PU lingers as a session orphan until erwin restart.");
                log("[From-DB] If CC's 'Open Models in Memory' picker shows duplicates next run, restart erwin to reset session.");

                // Resume monitoring on the UI/STA thread (2026-06-02 crash fix).
                // The previous Task.Run ran on a threadpool MTA worker. Two of the
                // three resume calls are pure flag flips, but the third -
                // _validationService.StartMonitoring() (ColumnValidationService) -
                // does a SYNCHRONOUS SCAPI walk (TakeSnapshot -> ModelObjects.Collect)
                // and arms two WinForms timers. From an MTA thread that forces a
                // cross-apartment COM marshal into erwin's STA RCWs WHILE the CC/RD
                // session is tearing down, dereferencing an about-to-die proxy ->
                // use-after-free -> 0xC0000005 in coreclr (verified offset, same on
                // the Review teardown). BeginInvoke posts to the form's own UI/STA
                // thread (where the RCWs + Forms.Timers live) and returns at once, so
                // teardown is not blocked and the SCAPI stays in-apartment.
                if (this.IsHandleCreated && !this.IsDisposed)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        try { _validationCoordinatorService?.ResumeValidation(); }
                        catch (Exception ex) { try { log($"[From-DB] ResumeValidation err: {ex.Message}"); } catch { } }
                        try { _tableTypeMonitorService?.StartMonitoring(); }
                        catch (Exception ex) { try { log($"[From-DB] StartMonitoring err: {ex.Message}"); } catch { } }
                        try { _validationService?.StartMonitoring(); }
                        catch (Exception ex) { try { log($"[From-DB] ColumnValidation StartMonitoring err: {ex.Message}"); } catch { } }
                        try { log("[From-DB] monitoring resumed (UI/STA thread)"); } catch { }
                    }));
                }
                else
                {
                    try { log("[From-DB] form handle unavailable - monitoring resume skipped (resumes on next model load)"); } catch { }
                }
                // Clear the pipeline guard + restart the reconnect timer LAST
                // so no tick touches SCAPI while cleanup is still settling.
                _martMartPipelineActive = false;
                try { StartReconnectTimer(); } catch (Exception ex) { log($"[From-DB] StartReconnectTimer err: {ex.Message}"); }
                log("[From-DB] pipeline complete - monitoring + reconnect resume scheduled to background");
            }

            return (script, err);
        }

        /// <summary>
        /// Best-effort deterministic release of a SCAPI COM RCW. The From-DB
        /// pipeline uses it to drop every Runtime Callable Wrapper it creates
        /// WHILE the underlying native object is still alive, instead of
        /// abandoning it to the GC. An abandoned SCAPI RCW is finalized later by
        /// the CLR finalizer thread (MTA); because the wrapped object lives in
        /// erwin's STA, the finalizer cross-apartment-marshals IUnknown::Release
        /// onto erwin's STA - and if teardown already freed that object, the
        /// Release dereferences freed memory (RDI=0x0BADF00D heap poison) inside
        /// coreclr!SafeReleasePreemp, which the CLR escalates to a fatal
        /// ExecutionEngineException (crash dump erwin.exe.29840.dmp, 2026-06-02).
        /// Mirrors the long-standing ValidationCoordinatorService.ReleaseCom.
        /// Never throws - releasing a scratch wrapper must not break teardown.
        /// </summary>
        private static void ReleaseComSafe(object comObject)
        {
            if (comObject == null) return;
            try
            {
                if (Marshal.IsComObject(comObject))
                    Marshal.FinalReleaseComObject(comObject);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[From-DB] ReleaseComSafe error: {ex.Message}");
            }
        }

        /// <summary>
        /// Drain pending COM RCW finalizations on a BACKGROUND thread WHILE the
        /// underlying native objects are still alive. The From-DB pipeline
        /// abandons two classes of RCW to the GC: SCAPI entity proxies (freed
        /// when Apply-to-Right mutates the active model) and UIA-derived
        /// IAccessible RCWs - the XTP <c>CXTPAccessible::XAccessible</c> MSAA
        /// objects obtained when <c>AutomationElement.FromHandle</c> drives the
        /// CC "Open Models in Memory" picker and the FE-wizard navigation (freed
        /// when those XTP wizard windows are destroyed at teardown). When erwin
        /// frees the native object, the CLR finalizer later cross-apartment-
        /// marshals <c>IUnknown::Release</c> onto erwin's STA against a freed
        /// object (NULL vtable) -> AV in <c>coreclr!SafeReleasePreemp</c> ->
        /// fatal <c>ExecutionEngineException</c> ~6s after the pipeline reports
        /// complete. (Identified from full dump erwin.exe.48868.dmp via SOS
        /// !dumprcw = Accessibility.IAccessible / ToolkitPro1850 CXTPAccessible.)
        /// Forcing the finalizers to run NOW, while the objects live, makes that
        /// Release land on a live object. MUST run on a background thread so
        /// erwin's STA stays free to PUMP and service the marshaled cross-
        /// apartment Release - a GC.WaitForPendingFinalizers on the STA itself
        /// would deadlock (STA blocked, cannot service the marshaled call).
        /// </summary>
        private static async System.Threading.Tasks.Task DrainComFinalizersAsync(Action<string> log, string where)
        {
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
                log?.Invoke($"[From-DB] COM finalizer drain complete ({where} - native objects still alive)");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[From-DB] COM finalizer drain err ({where}): {ex.Message}");
            }
        }

        /// <summary>
        /// LEGACY From-DB path (DdlHelper subprocess + text diff). Parked as
        /// of 2026-04-27: production "Generate DDL" routes to
        /// RunFromDbDdlPipelineAsync now. Kept for fallback/comparison until
        /// the new in-process pipeline is fully proven; remove after sign-off.
        /// </summary>
        private void RunFromDbCompare(string feOptionXml)
        {
            // Validation: connection
            bool needsConfigure = string.IsNullOrEmpty(_dbConnectionString)
                || _dbConnectionString.Contains("|5=\n") || _dbConnectionString.EndsWith("|5=");
            if (needsConfigure)
            {
                _dbConnectionString = "";
                BtnConfigureDB_Click(null, EventArgs.Empty);
                if (string.IsNullOrEmpty(_dbConnectionString)) return;
            }

            // Tables: always ALL model tables (the "Select Tables..." picker
            // was deleted 2026-05-30; narrower-scope comparison now goes
            // through "Only Selected Objects" + diagram selection).
            var tableList = CollectModelTablePhysicalNames();
            if (tableList.Count == 0)
            {
                ErwinAddIn.ShowTopMostMessage("Active model has no tables to compare.", "From DB");
                return;
            }
            Log($"DDL: From DB using all {tableList.Count} model tables.");

            // Schema validation: RE returns empty if no schema/owner filter
            if (string.IsNullOrWhiteSpace(_dbSchema))
            {
                ErwinAddIn.ShowTopMostMessage(
                    "DB schema/owner is required for reverse engineering.\n\n" +
                    "Open 'Configure...' and set the schema filter (e.g. dbo).",
                    "From DB", isError: false);
                BtnConfigureDB_Click(null, EventArgs.Empty);
                if (string.IsNullOrWhiteSpace(_dbSchema)) return;
            }

            btnAlterWizardProd.Enabled = false;
            lblDDLStatus.Text = $"Reverse engineering {tableList.Count} table(s) from DB...";
            lblDDLStatus.ForeColor = Color.Gray;
            Application.DoEvents();

            _validationCoordinatorService?.SuspendValidation();

            Form waitDialog = null;
            try
            {
                try
                {
                    waitDialog = new Form
                    {
                        Size = new System.Drawing.Size(380, 120),
                        FormBorderStyle = FormBorderStyle.None,
                        StartPosition = FormStartPosition.CenterScreen,
                        TopMost = true,
                        ShowInTaskbar = false,
                        BackColor = Color.White
                    };
                    var titlePanel = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(0, 122, 204) };
                    titlePanel.Controls.Add(new Label
                    {
                        Text = "DDL Generation",
                        ForeColor = Color.White,
                        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                        Location = new Point(10, 5),
                        AutoSize = true
                    });
                    waitDialog.Controls.Add(titlePanel);
                    waitDialog.Controls.Add(new Label
                    {
                        Text = $"Reverse engineering from DB\n({tableList.Count} table(s))...\nPlease wait.",
                        Dock = DockStyle.Fill,
                        TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                        Font = new Font("Segoe UI", 10f)
                    });
                    waitDialog.Paint += (s2, e2) =>
                    {
                        using (var pen = new Pen(Color.FromArgb(0, 122, 204), 1))
                            e2.Graphics.DrawRectangle(pen, 0, 0, waitDialog.Width - 1, waitDialog.Height - 1);
                    };
                    waitDialog.Show();
                    Application.DoEvents();
                }
                catch { }

                string diff = DdlGenerationService.GenerateDiffWithDatabase(
                    _scapi,
                    _currentModel,
                    _dbHost,
                    _dbName,
                    _dbUser,
                    _dbPassword,
                    _dbUseWindowsAuth,
                    _dbTypeCode,
                    _dbTargetServer,
                    _dbTargetVersion,
                    _dbSchema,
                    tableList,
                    _dbLabel,
                    feOptionXml,
                    (Action<string>)Log);

                try { waitDialog?.Close(); waitDialog?.Dispose(); } catch { }

                if (!string.IsNullOrEmpty(diff))
                {
                    ShowDDLResult(diff, $"DDL Diff vs {_dbLabel}", "DDL-Diff-DB");
                    lblDDLStatus.Text = $"Diff computed vs {_dbLabel}.";
                    lblDDLStatus.ForeColor = Color.DarkGreen;
                }
                else
                {
                    lblDDLStatus.Text = "From DB comparison failed. See log.";
                    lblDDLStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                try { waitDialog?.Close(); waitDialog?.Dispose(); } catch { }
                lblDDLStatus.Text = $"Error: {ex.Message}";
                lblDDLStatus.ForeColor = Color.Red;
                Log($"DDL From DB error: {ex.Message}");
            }
            finally
            {
                _validationCoordinatorService?.ResumeValidation();
                btnAlterWizardProd.Enabled = DdlSourceEnabled;
                this.TopMost = true;
                this.Activate();
                this.BringToFront();
                this.TopMost = false;
            }
        }

        /// <summary>
        /// Returns the physical_name of every entity in the active model. Empty if no session.
        /// </summary>
        private List<string> CollectModelTablePhysicalNames()
        {
            var result = new List<string>();
            if (_currentModel == null) return result;
            dynamic sess = null;
            bool ownSess = false;
            try
            {
                bool hasSess = false;
                try { hasSess = _currentModel.HasSession(); } catch { }
                if (hasSess)
                {
                    int sc = 0; try { sc = _scapi.Sessions.Count; } catch { }
                    for (int i = 0; i < sc; i++)
                    {
                        try
                        {
                            dynamic s = _scapi.Sessions.Item(i);
                            bool open = false; try { open = s.IsOpen(); } catch { }
                            string puN = ""; try { puN = s.PersistenceUnit?.Name?.ToString() ?? ""; } catch { }
                            string curN = ""; try { curN = _currentModel.Name?.ToString() ?? ""; } catch { }
                            if (open && puN == curN) { sess = s; break; }
                        }
                        catch { }
                    }
                }
                if (sess == null)
                {
                    sess = _scapi.Sessions.Add();
                    sess.Open(_currentModel, 0, 0);
                    ownSess = true;
                }
                dynamic mo = sess.ModelObjects;
                dynamic ents = mo.Collect(mo.Root, "Entity");
                foreach (dynamic ent in ents)
                {
                    try
                    {
                        string phys = "";
                        try { phys = ent.Properties("Physical_Name")?.Value?.ToString() ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(phys))
                            try { phys = ent.Name?.ToString() ?? ""; } catch { }
                        if (!string.IsNullOrWhiteSpace(phys) && !result.Contains(phys))
                            result.Add(phys);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log($"DDL: CollectModelTablePhysicalNames error: {ex.Message}"); }
            finally
            {
                if (ownSess && sess != null) { try { sess.Close(); } catch { } }
            }
            return result;
        }

        /// <summary>
        /// Maps a set of entity NAMES (logical, as shown in erwin's Overview
        /// pane when entities are selected on the diagram) to their SCAPI
        /// Physical_Name (the table identifier that appears inside ALTER
        /// TABLE [schema].[NAME] in the generated DDL). Names that have no
        /// matching entity fall through to the logical name itself, so the
        /// downstream regex filter still has a chance to match. Used by the
        /// From-DB post-process DDL filter ("Only Selected Objects").
        /// </summary>
        private Dictionary<string, string> MapEntityNamesToPhysicalNames(IEnumerable<string> entityNames)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entityNames == null) return map;
            var wanted = new HashSet<string>(entityNames, StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return map;
            if (_currentModel == null)
            {
                foreach (var n in wanted) map[n] = n;
                return map;
            }
            dynamic sess = null; bool ownSess = false;
            try
            {
                bool hasSess = false;
                try { hasSess = _currentModel.HasSession(); } catch { }
                if (hasSess)
                {
                    int sc = 0; try { sc = _scapi.Sessions.Count; } catch { }
                    for (int i = 0; i < sc; i++)
                    {
                        try
                        {
                            dynamic s = _scapi.Sessions.Item(i);
                            bool open = false; try { open = s.IsOpen(); } catch { }
                            string puN = ""; try { puN = s.PersistenceUnit?.Name?.ToString() ?? ""; } catch { }
                            string curN = ""; try { curN = _currentModel.Name?.ToString() ?? ""; } catch { }
                            if (open && puN == curN) { sess = s; break; }
                        }
                        catch { }
                    }
                }
                if (sess == null)
                {
                    sess = _scapi.Sessions.Add();
                    sess.Open(_currentModel, 0, 0);
                    ownSess = true;
                }
                dynamic mo = sess.ModelObjects;
                dynamic ents = mo.Collect(mo.Root, "Entity");
                foreach (dynamic ent in ents)
                {
                    try
                    {
                        string name = "";
                        try { name = ent.Name?.ToString() ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(name) || !wanted.Contains(name)) continue;
                        string phys = "";
                        try { phys = ent.Properties("Physical_Name")?.Value?.ToString() ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(phys)) phys = name;
                        map[name] = phys;
                    }
                    catch { }
                }
                // Any wanted name that did not resolve falls back to itself so
                // the regex still has a chance (entity might have been renamed).
                foreach (var n in wanted) if (!map.ContainsKey(n)) map[n] = n;
            }
            catch (Exception ex) { Log($"DDL: MapEntityNamesToPhysicalNames err: {ex.Message}"); }
            finally
            {
                if (ownSess && sess != null) { try { sess.Close(); } catch { } }
            }
            return map;
        }

        /// <summary>
        /// Splits alter DDL into <c>go</c>-delimited statement blocks and keeps
        /// only those that reference one of the supplied physical table names.
        /// Matches against the bracketed identifier (e.g. <c>[dbo].[NAME]</c>)
        /// AND against an unbracketed word boundary (case-insensitive), so the
        /// filter survives dialect-specific quoting differences. Blocks that
        /// touch no listed table - DROP statements for unrelated objects,
        /// pre-script / post-script blobs, etc - are dropped.
        /// </summary>
        private static string FilterFromDbDdlByPhysicalNames(string ddl, HashSet<string> physicalNames, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(ddl) || physicalNames == null || physicalNames.Count == 0)
                return ddl;
            // Split on lines that are just "go" (case-insensitive, optional
            // surrounding whitespace, optional CR).
            var blocks = System.Text.RegularExpressions.Regex.Split(ddl, @"(?im)^\s*go\s*\r?$");
            var keptBlocks = new List<string>();
            int matched = 0, skipped = 0;
            foreach (var blockRaw in blocks)
            {
                string block = blockRaw?.Trim() ?? "";
                if (block.Length == 0) continue;
                bool hit = false;
                foreach (var phys in physicalNames)
                {
                    if (string.IsNullOrWhiteSpace(phys)) continue;
                    string esc = System.Text.RegularExpressions.Regex.Escape(phys);
                    // Match [NAME] (bracketed, common in SQL Server output) OR
                    // a free-standing identifier (word boundaries) for dialects
                    // without bracket quoting.
                    string pattern = $@"(\[{esc}\]|\b{esc}\b)";
                    if (System.Text.RegularExpressions.Regex.IsMatch(block, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        hit = true; break;
                    }
                }
                if (hit) { keptBlocks.Add(block); matched++; }
                else { skipped++; }
            }
            log?.Invoke($"  [DDL-FILTER] kept {matched} block(s), dropped {skipped}; against {physicalNames.Count} physical name(s).");
            if (keptBlocks.Count == 0) return ""; // honest: nothing matched
            var sb = new System.Text.StringBuilder();
            foreach (var b in keptBlocks)
            {
                sb.Append(b);
                if (!b.EndsWith("\n")) sb.AppendLine();
                sb.AppendLine("go");
            }
            return sb.ToString();
        }

        private void OnRightSourceChanged()
        {
            bool fromMart = rbFromMart.Checked;
            bool fromDB = rbFromDB.Checked;

            // RIGHT version combo is only meaningful for the Mart compare path
            // (Review = active vs older version); From-DB compares against the
            // live RE'd DB and has no version pick. RebuildRightCombo populates
            // a "(no lower version)" placeholder when no older v exists - keep
            // that disabled state by gating on the first item starting with 'v'.
            // (Note: with the cmbLeftModel-deleted refactor 2026-05-30, items
            // always start with 'v' for any _martVersion >= 1, so the gate is
            // currently a defensive no-op; left in place for safety.)
            bool hasRealVersions = cmbRightModel.Items.Count > 0
                && (cmbRightModel.Items[0]?.ToString()?.StartsWith("v") ?? false);
            cmbRightModel.Enabled = fromMart && hasRealVersions;
            // Keep the 1-version label / combo display in sync with the source radio
            // (From-DB hides the label and shows the disabled combo).
            ApplyRightTargetSingleChoiceDisplay();
            btnConfigureDB.Visible = fromDB;

            // "Only Selected Objects" is honoured by BOTH paths (2026-05-30):
            //  - From-Mart: erwin's Alter Script Wizard Object Filter page
            //    raises a "Use current diagram selections?" popup; the bridge
            //    answers Yes via SetUseDiagramSelection so the wizard scopes
            //    natively (when the user enables the checkbox, the caller
            //    routes through the Next-loop so the Object Filter page is
            //    actually visited - see ClickWizardPreviewTab's
            //    requireObjectFilterPass parameter).
            //  - From-DB: the wizard does not raise that popup, so we instead
            //    post-process the captured DDL with a regex filter against the
            //    SCAPI Physical_Name of every entity currently selected on the
            //    diagram (Win32Helper.GetDiagramSelectedEntities).
            // Either way the checkbox stays enabled regardless of the radio.
            chkFilterObjects.Enabled = true;

            if (fromDB)
            {
                if (string.IsNullOrEmpty(_dbConnectionString) || string.IsNullOrEmpty(_dbLabel))
                {
                    lblDDLStatus.Text = "Click 'Configure...' to set database connection.";
                    lblDDLStatus.ForeColor = Color.Gray;
                }
                else
                {
                    lblDDLStatus.Text = $"DB: {_dbLabel}";
                    lblDDLStatus.ForeColor = Color.DarkGreen;
                }
            }
            else // Mart
            {
                lblDDLStatus.Text = "";
            }
        }

        private void BtnConfigureDB_Click(object sender, EventArgs e)
        {
            using (var dlg = new Forms.DbConnectionForm())
            {
                // erwin's main window is TopMost; without explicit TopMost
                // here the modal child opens behind erwin and stays
                // invisible (user only escapes via ESC). Mirrors what
                // SelectDbTables and other dialogs in this form already do.
                dlg.TopMost = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _dbConnectionString = dlg.ConnectionString;
                    _dbPassword = dlg.Password;
                    _dbLabel = dlg.DisplayLabel;
                    _dbTargetServer = dlg.TargetServerCode;
                    _dbTargetVersion = dlg.TargetServerVersion;
                    _dbSchema = dlg.SchemaFilter;
                    _dbHost = dlg.ServerHost;
                    _dbName = dlg.DatabaseName;
                    _dbUser = dlg.UserName;
                    _dbUseWindowsAuth = dlg.UseWindowsAuth;
                    _dbTypeCode = dlg.DbTypeCode;
                    _dbUseNative = dlg.UseNative;
                    _dbDsnName = dlg.DsnName;
                    lblDDLStatus.Text = $"DB configured: {_dbLabel}";
                    lblDDLStatus.ForeColor = Color.DarkGreen;
                    Log($"DDL: DB configured: {_dbLabel}, host={_dbHost}, db={_dbName}, user={_dbUser}, winAuth={_dbUseWindowsAuth}, schema='{_dbSchema}'");
                }
            }
        }


        private int _martVersion = 0;
        private string _martLocator = "";

        // DDL Generation gates (admin "DDL Generation Functionality", 2026-06-04).
        // Each is the EFFECTIVE value of an independent two-level toggle (model
        // CONFIG_PROPERTY -> corporate CORPORATE_PROPERTY -> built-in false), resolved
        // once per connect in ApplyDdlGenerationGates() and consumed by the source
        // radios + RebuildRightCombo. Default false = that DDL source is OFF until the
        // admin enables it (matches the admin's "Effective: Off (built-in default)").
        //   _ddlAllowLastSaved        -> From-Mart combo entry v==activeV (dirty vs last saved)
        //   _ddlAllowPreviousVersions -> From-Mart combo entries v<activeV (older versions)
        //   _ddlAllowFromDb           -> the "From DB" radio
        private bool _ddlAllowLastSaved;
        private bool _ddlAllowPreviousVersions;
        private bool _ddlAllowFromDb;

        // True when at least one admin DDL source gate is on. With all sources
        // off Generate DDL has nothing to run against: ApplyDdlGenerationGates
        // disables the button, and every post-run re-enable restores THIS value
        // instead of a literal true so a finally block can never undo the gate.
        private bool DdlSourceEnabled => _ddlAllowLastSaved || _ddlAllowPreviousVersions || _ddlAllowFromDb;

        private int _pendingDDLVersion = 0;

        // Gates the legacy bridge CC + Apply-to-Right cross-version path
        // (active dirty vs older Mart version). OFF since 2026-05-28: that path
        // orphans the loaded version PU and can lock erwin. Faz 2 replaces it
        // with the Review-wizard-driven flow. static readonly (not const) so
        // the gated block stays reachable to the compiler - keeps the helper
        // methods (CloseSelectedVersionPU, ParseActivePuCatalog, ...) referenced
        // and avoids unused-member errors under TreatWarningsAsErrors. Flip to
        // true only to A/B the old path against the new Review path.
        private static readonly bool LegacyCrossVersionEnabled = false;

        // _pendingDDLFeOption field removed 2026-05-27 (was always ""; never set).
        // PuWatcherTimer_Tick now resolves DDL FE option XML from XML_OPTION
        // TYPE='DDL' on demand instead of relying on a stale instance field.
        private Timer _puWatcherTimer;
        private int _puWatcherInitialCount;

        /// <summary>
        /// Populate Left/Right model version combo boxes.
        /// Version is read from PU locator or erwin window title.
        /// </summary>
        /// <summary>
        /// Resolve the admin "DDL Generation Functionality" gates (three INDEPENDENT
        /// two-level toggles: model CONFIG_PROPERTY -> corporate CORPORATE_PROPERTY ->
        /// built-in false) into <see cref="_ddlAllowLastSaved"/> /
        /// <see cref="_ddlAllowPreviousVersions"/> / <see cref="_ddlAllowFromDb"/> and
        /// apply them to the Generate DDL tab: HIDE a source radio when its function is
        /// off (RebuildRightCombo omits the matching combo entries), and keep the
        /// checked radio on a VISIBLE source. Called per connect from
        /// PopulateVersionCombos. Default false = a source stays OFF until the admin
        /// enables it (matches the admin's "Effective: Off (built-in default)").
        /// </summary>
        private void ApplyDdlGenerationGates()
        {
            try
            {
                var ctx = ConfigContextService.Instance;
                _ddlAllowLastSaved        = ctx.GetEffectiveBool("DDL_COMPARE_LAST_SAVED", false);
                _ddlAllowPreviousVersions = ctx.GetEffectiveBool("DDL_COMPARE_PREVIOUS_VERSIONS", false);
                _ddlAllowFromDb           = ctx.GetEffectiveBool("DDL_COMPARE_FROM_DB", false);
            }
            catch (Exception ex)
            {
                // A real DB read error is SURFACED (logged), not silently masked. Fall
                // back to ALL sources enabled for this cycle so a transient DB blip never
                // locks the user out of Generate DDL; the gate re-applies on the next
                // successful connect.
                Log($"[DDL-GATES] DB read error - showing all sources this cycle: {ex.Message}");
                _ddlAllowLastSaved = _ddlAllowPreviousVersions = _ddlAllowFromDb = true;
            }

            Log($"[DDL-GATES] last-saved={_ddlAllowLastSaved}, prev-versions={_ddlAllowPreviousVersions}, from-db={_ddlAllowFromDb}");

            bool martVisible = _ddlAllowLastSaved || _ddlAllowPreviousVersions;
            rbFromMart.Visible = martVisible;
            rbFromDB.Visible = _ddlAllowFromDb;

            // Keep the selected source on a VISIBLE radio (hiding a radio does not
            // uncheck it). Setting Checked raises OnRightSourceChanged, which refreshes
            // the dependent combo / Configure-DB button state.
            if (rbFromMart.Checked && !martVisible)
            {
                if (_ddlAllowFromDb) rbFromDB.Checked = true; else rbFromMart.Checked = false;
            }
            else if (rbFromDB.Checked && !_ddlAllowFromDb)
            {
                if (martVisible) rbFromMart.Checked = true; else rbFromDB.Checked = false;
            }
            else if (!rbFromMart.Checked && !rbFromDB.Checked)
            {
                if (martVisible) rbFromMart.Checked = true;
                else if (_ddlAllowFromDb) rbFromDB.Checked = true;
            }

            // No source enabled at all -> say so and gate the action.
            if (!martVisible && !_ddlAllowFromDb)
            {
                lblDDLStatus.Text = "No DDL source is enabled.";
                lblDDLStatus.ForeColor = System.Drawing.Color.FromArgb(180, 0, 0);
                btnAlterWizardProd.Enabled = false;
            }
        }

        private void PopulateVersionCombos()
        {
            cmbRightModel.Items.Clear();

            // Resolve + apply the admin "DDL Generation Functionality" gates (source
            // radio visibility) before building the right combo, which reads the same
            // gate flags to decide which version entries to offer.
            ApplyDdlGenerationGates();

            try
            {
                // Try PU locator first
                string locator = "";
                try { locator = _currentModel.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }
                _martLocator = locator;

                int version = DdlGenerationService.ParseVersionFromLocator(locator);

                // If locator didn't have version, try erwin window title
                // Format: "erwin DM - [Mart://Mart/KKB/KKB_Demo : v4 : ER_Diagram_164 * ]"
                if (version <= 1)
                {
                    try
                    {
                        var hWnd = Services.Win32Helper.GetErwinMainWindow();
                        if (hWnd != IntPtr.Zero)
                        {
                            var sb = new System.Text.StringBuilder(512);
                            Services.Win32Helper.GetWindowTextPublic(hWnd, sb, sb.Capacity);
                            string title = sb.ToString();

                            var match = System.Text.RegularExpressions.Regex.Match(title, @":\s*v(\d+)\s*:");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int titleVer))
                            {
                                version = titleVer;
                                Log($"DDL: Version from window title = v{version}");
                            }
                        }
                    }
                    catch { }
                }

                _martVersion = version;
                string modelName = _connectedModelName ?? "Model";
                Log($"DDL: Model='{modelName}', Version={version}, Locator='{locator}'");

                // LEFT is ALWAYS the open model "(with last changes)". A
                // separate "(last saved)" entry was tried (Faz 3 Complete
                // Compare) but proven REDUNDANT 2026-05-29: erwin's Complete
                // Compare uses the model's OPEN state (dirty if dirty, else the
                // last-saved state) on the LEFT - identical to what the Review
                // pipeline already captures. So to compare last-saved vs an
                // older version the user simply opens the model with no unsaved
                // changes and runs the normal flow. The compare target (an
                // older version) is chosen on the RIGHT. (See
                // project_complete_compare_redundant memory; the Faz 3
                // useCompleteCompare code path is kept dormant, not deleted.)
                string vTag = version >= 1 ? $"v{version} " : "";
                string activeLabel = $"{modelName} {vTag}(with last changes)";
                // lblOpenedModel is the VISIBLE source-side display (the prior
                // single-entry cmbLeftModel ComboBox was DELETED 2026-05-30 -
                // one-entry combo was pointless UX). Plain text inside the
                // "Source (Left)" group, no "Opened Model:" prefix needed.
                lblOpenedModel.Text = activeLabel;

                // RIGHT version combo no longer cascades via a hidden
                // SelectedIndexChanged event - call it directly here once the
                // active version is known.
                RebuildRightCombo();
            }
            catch (Exception ex)
            {
                Log($"PopulateVersionCombos error: {ex.Message}");
                lblOpenedModel.Text = "Active Model (with last changes)";
                cmbRightModel.Items.Add("(Mart Baseline)");
                cmbRightModel.SelectedIndex = 0;
            }
        }

        // LeftIsActiveModel / ParseLeftVersion / OnLeftModelChanged were
        // DELETED 2026-05-30 along with the cmbLeftModel ComboBox they read.
        // The Source (Left) is always the open Mart model, so each method's
        // invariant collapsed: LeftIsActiveModel -> always true, ParseLeftVersion
        // -> always _martVersion, OnLeftModelChanged -> direct RebuildRightCombo
        // call from PopulateVersionCombos. The lone external caller of
        // LeftIsActiveModel (BtnAlterWizardProd_Click martMode branch) now
        // inlines `bool leftIsActive = true;` (kept as a local for readability +
        // to keep the dormant Faz-3 useCompleteCompare branch compilable).

        /// <summary>
        /// Rebuilds the Target (Right) combo with the list of older Mart
        /// versions to compare against. Since the Source (Left) is always the
        /// open active model (version <c>_martVersion</c>), the list is
        /// v(activeV)..v1 (default = highest, which is "dirty vs last saved").
        /// </summary>
        private void RebuildRightCombo()
        {
            cmbRightModel.Items.Clear();

            int activeV = _martVersion > 0 ? _martVersion : 1;
            for (int v = activeV; v >= 1; v--)
            {
                // DDL Generation gates (admin "DDL Generation Functionality"): the
                // highest entry (v == activeV) is the "dirty vs last saved" same-version
                // compare (DDL_COMPARE_LAST_SAVED); the lower entries (v < activeV) are
                // the earlier versions (DDL_COMPARE_PREVIOUS_VERSIONS). Omit a class
                // entirely when its effective toggle is off (hide, per the admin design).
                bool isLastSaved = (v == activeV);
                if (isLastSaved && !_ddlAllowLastSaved) continue;
                if (!isLastSaved && !_ddlAllowPreviousVersions) continue;
                cmbRightModel.Items.Add($"v{v} (Version {v})");
            }

            if (cmbRightModel.Items.Count > 0)
            {
                cmbRightModel.SelectedIndex = 0; // highest available version
                cmbRightModel.Enabled = true;
            }
            else
            {
                // Empty because both Mart toggles are off (or no version exists).
                cmbRightModel.Items.Add("(no Mart source enabled)");
                cmbRightModel.SelectedIndex = 0;
                cmbRightModel.Enabled = false;
            }

            ApplyRightTargetSingleChoiceDisplay();
        }

        /// <summary>
        /// Mirror the Source side: a Target (Right) with exactly ONE real version to
        /// compare against is shown as a plain label, not a 1-entry dropdown (the
        /// Source side did this 2026-05-30 by replacing cmbLeftModel with
        /// lblOpenedModel). The combo stays populated + selected BEHIND the label so
        /// ParseRightVersion() keeps reading the real selection. Only applies on the
        /// From-Mart path with a real "v.." entry; From-DB / "(no Mart source
        /// enabled)" keep the (disabled) combo as before. Called from
        /// RebuildRightCombo and OnRightSourceChanged. (2026-06-06)
        /// </summary>
        private void ApplyRightTargetSingleChoiceDisplay()
        {
            bool singleRealChoice =
                rbFromMart.Checked
                && cmbRightModel.Items.Count == 1
                && (cmbRightModel.Items[0]?.ToString()?.StartsWith("v") ?? false);

            lblRightModel.Text = singleRealChoice ? (cmbRightModel.Items[0]?.ToString() ?? "") : "";
            lblRightModel.Visible = singleRealChoice;
            cmbRightModel.Visible = !singleRealChoice;
        }

        #endregion

        #region Compare / DDL Helpers

        /// <summary>
        /// File-only INFO log. Delegates to <see cref="AddinLogger.Log"/>
        /// so the form-driven runtime messages share the same formatting
        /// and append target as the static load-timeline messages.
        /// Never touches a WinForms control - the Debug Log tab was
        /// removed 2026-05-07 because TextBox.AppendText raised a UIA
        /// TextChanged event that crashed erwin's UIA proxy on a
        /// timer-hot-path schedule. The user reads the log via the
        /// "Log file" link on the General tab or directly at
        /// %TEMP%\erwin-addin-debug.log.
        /// </summary>
        private void Log(string message) => AddinLogger.Log(message);

        /// <summary>
        /// Verbose / development-only log. Compiled away in PACKAGED
        /// builds (and any non-DEBUG configuration) via
        /// <see cref="AddinLogger.LogDebug"/>'s Conditional attribute.
        /// </summary>
        private void LogDebug(string message) => AddinLogger.LogDebug(message);

        #region Integrate tab (environment promotion)

        // Current body of the Integrate tab; replaced wholesale on every rebuild
        // (model switch / reconnect) so stale environment data never lingers.
        private Panel _integrateContentPanel;

        // Theme tokens mirrored from the General tab so the Integrate tab matches
        // the rest of the add-in (accent blue, neutral text, error red).
        private static readonly Color IntegrateAccent = Color.FromArgb(0, 102, 204);
        private static readonly Color IntegrateTextSecondary = Color.FromArgb(120, 120, 120);
        private static readonly Color IntegrateError = Color.FromArgb(204, 0, 0);

        /// <summary>
        /// True when the active model's config has the Integrate feature switched
        /// on, resolved exactly like the admin side (CONFIG_PROPERTY -&gt;
        /// CORPORATE_PROPERTY -&gt; false). Requires a resolved config on a
        /// MART-HOSTED model: config-less / mismatch / local-file models never
        /// qualify. The IsMartModel guard matters because a local .erwin can be
        /// config-initialized (2026-06-13) with MartPath set to its file path,
        /// whose parent folder could coincidentally match an environment name;
        /// every other Mart-driven feature in this form guards the same way.
        /// </summary>
        private bool IsIntegrateEnabled()
        {
            var ctx = ConfigContextService.Instance;
            if (!ctx.IsInitialized || !ctx.IsMartModel || ctx.ActiveConfigId <= 0) return false;
            return ctx.GetEffectiveBool("INTEGRATE_ENABLED", false);
        }

        /// <summary>
        /// Shows or hides the Integrate tab. WinForms TabPage has no usable
        /// Visible, so a conditional tab is toggled by membership in
        /// tabControl.TabPages. Appended last to preserve tab order; idempotent.
        /// </summary>
        private void SetIntegrateTabVisible(bool show)
        {
            if (tabControl == null || tabIntegrate == null) return;
            bool present = tabControl.TabPages.Contains(tabIntegrate);
            if (show && !present) tabControl.TabPages.Add(tabIntegrate);
            else if (!show && present) tabControl.TabPages.Remove(tabIntegrate);
        }

        /// <summary>
        /// Connect-time entry point: shows and (re)builds the Integrate tab only
        /// for configs with INTEGRATE_ENABLED; otherwise leaves it hidden. A
        /// failure to read the enabled flag is surfaced to the log and the tab
        /// stays hidden rather than breaking the model connect.
        /// </summary>
        private void RefreshIntegrateTab()
        {
            bool enabled;
            try
            {
                enabled = IsIntegrateEnabled();
            }
            catch (Exception ex)
            {
                Log($"Integrate: enabled-flag read failed, tab hidden: {ex.Message}");
                SetIntegrateTabVisible(false);
                return;
            }

            if (!enabled)
            {
                SetIntegrateTabVisible(false);
                return;
            }

            SetIntegrateTabVisible(true);
            RebuildIntegrateTab();
        }

        /// <summary>
        /// Builds the Integrate tab body from the admin ENVIRONMENT /
        /// ENVIRONMENT_RELATION contract for the active config and the model's
        /// current environment (its Mart parent folder). States: not in a managed
        /// environment / no promotions / a single static target / a target combo,
        /// each with a per-target Integrate button or an approval notice. A
        /// MetaRepo read failure is rendered in the tab, never swallowed.
        /// </summary>
        private void RebuildIntegrateTab()
        {
            // Replace any previous body (a model switch / reconnect rebuilds this).
            if (_integrateContentPanel != null)
            {
                tabIntegrate.Controls.Remove(_integrateContentPanel);
                _integrateContentPanel.Dispose();
                _integrateContentPanel = null;
            }

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            _integrateContentPanel = content;
            tabIntegrate.Controls.Add(content);

            var lblTitle = new Label
            {
                Text = "Integrate",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = IntegrateAccent,
                AutoSize = true,
                Location = new Point(12, 12)
            };
            content.Controls.Add(lblTitle);

            var lblSubtitle = new Label
            {
                Text = "Promote this model between deployment environments.",
                Font = new Font("Segoe UI", 9f),
                ForeColor = IntegrateTextSecondary,
                AutoSize = true,
                Location = new Point(14, 44)
            };
            content.Controls.Add(lblSubtitle);

            content.Controls.Add(new Panel
            {
                Location = new Point(14, 66),
                Size = new Size(60, 3),
                BackColor = IntegrateAccent
            });

            const int bodyTop = 96;

            IReadOnlyList<IntegrationEnvironment> environments;
            IntegrationEnvironment current;
            try
            {
                var ctx = ConfigContextService.Instance;
                environments = IntegrationEnvironmentService.GetEnvironments(ctx.ActiveConfigId);
                current = IntegrationPlanner.ResolveCurrentEnvironment(ctx.MartPath, environments);
            }
            catch (Exception ex)
            {
                Log($"Integrate: failed to read environments: {ex.Message}");
                content.Controls.Add(BuildIntegrateMessage(
                    $"Could not read deployment environments from the repository:\n{ex.Message}",
                    IntegrateError, bodyTop));
                return;
            }

            if (current == null)
            {
                content.Controls.Add(BuildIntegrateMessage(
                    "This model is not in a managed environment.", IntegrateTextSecondary, bodyTop));
                return;
            }

            // Surface an admin data anomaly rather than resolving it silently:
            // duplicate environment names within a config collapse to the same
            // Mart folder and cannot be told apart from the path, so the lowest
            // SORT_ORDER won the match above.
            int sameNameCount = 0;
            foreach (var e in environments)
                if (string.Equals(e.Name, current.Name, StringComparison.OrdinalIgnoreCase)) sameNameCount++;
            if (sameNameCount > 1)
                Log($"Integrate: {sameNameCount} environments share the name '{current.Name}' in config " +
                    $"{ConfigContextService.Instance.ActiveConfigId}; resolved to the lowest SORT_ORDER. " +
                    $"Environment names should be unique per config.");

            IReadOnlyList<IntegrationRelation> relations;
            IReadOnlyList<PromotionTarget> targets;
            try
            {
                var ctx = ConfigContextService.Instance;
                relations = IntegrationEnvironmentService.GetRelations(ctx.ActiveConfigId);
                targets = IntegrationPlanner.BuildTargets(current.Id, relations, environments);
            }
            catch (Exception ex)
            {
                Log($"Integrate: failed to read promotion transitions: {ex.Message}");
                content.Controls.Add(BuildIntegrateMessage(
                    $"Could not read promotion transitions from the repository:\n{ex.Message}",
                    IntegrateError, bodyTop));
                return;
            }

            // Full topology diagram (mirrors the admin Integrate screen): every
            // environment + transition, the current environment highlighted, a
            // promote button on each allowed (non-approval) transition out of it.
            // Hosted on an auto-scroll surface so wide / deep pipelines stay reachable.
            content.AutoScroll = true;
            var diagram = new EnvironmentPipelineDiagram { Location = new Point(14, bodyTop) };
            diagram.IntegrateRequested += target => OnIntegrateClicked(current, target);
            diagram.SetData(environments, relations, current.Id);
            content.Controls.Add(diagram);

            int legendTop = bodyTop + diagram.Height + 12;
            var legend = new Label
            {
                Text = "▶  Promote        ⚠  Requires approval",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = IntegrateTextSecondary,
                AutoSize = true,
                Location = new Point(16, legendTop)
            };
            content.Controls.Add(legend);

            int actionable = 0;
            foreach (var t in targets) if (!t.RequiresApproval) actionable++;

            string hintText = "";
            if (targets.Count == 0) hintText = $"No promotions available from {current.Name}.";
            else if (actionable == 0) hintText = $"All promotions from {current.Name} require approval.";

            Label hint = null;
            if (hintText.Length > 0)
            {
                hint = new Label
                {
                    Text = hintText,
                    Font = new Font("Segoe UI", 9.5f),
                    ForeColor = IntegrateTextSecondary,
                    AutoSize = true,
                    Location = new Point(16, legendTop + 22)
                };
                content.Controls.Add(hint);
            }

            // Center the diagram horizontally and keep the legend/hint aligned to
            // its left edge, re-centering when the tab is resized. Falls back to
            // left when the diagram is wider than the visible area so horizontal
            // scrolling starts at the first environment.
            void CenterIntegrateDiagram()
            {
                int avail = content.ClientSize.Width;
                int x = diagram.Width < avail ? Math.Max(14, (avail - diagram.Width) / 2) : 14;
                diagram.Left = x;
                legend.Left = x + 2;
                if (hint != null) hint.Left = x + 2;
            }
            CenterIntegrateDiagram();
            content.SizeChanged += (s, e) => CenterIntegrateDiagram();
        }

        /// <summary>Builds a single-line informational/empty-state label for the tab body.</summary>
        private Label BuildIntegrateMessage(string text, Color color, int top) => new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 10f),
            ForeColor = color,
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Location = new Point(14, top)
        };

        /// <summary>
        /// Integrate button handler. SEAM: this iteration does NOT run the merge.
        /// It reserves the call site (<see cref="Services.MartMartAutomation.PromoteViaMartMerge"/>),
        /// logs, and tells the user the execution steps are pending. No destructive action.
        /// </summary>
        private void OnIntegrateClicked(IntegrationEnvironment current, IntegrationEnvironment target)
        {
            Services.MartMartAutomation.PromoteViaMartMerge(current.Name, target.Name, Log);
            AddinMessageDialog.Show(
                this,
                $"Promotion of this model from {current.Name} to {target.Name} via Mart Merge will run here.\n\nThe execution steps are not implemented yet.",
                "Integrate (steps pending)",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        #endregion

        /// <summary>
        /// Parses the right-side Mart version number from the cmbRightModel
        /// selection (e.g. "v1 (Version 1)" -> 1). Returns -1 if not parseable.
        /// </summary>
        private int ParseRightVersion()
        {
            try
            {
                string sel = cmbRightModel.SelectedItem?.ToString() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(sel, @"^v(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int v)) return v;
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Extracts the Mart catalog path (folder/model) from the active PU's
        /// locator, e.g. "Mart://Mart/Kursat/MetaRepo?..." -> "Kursat/MetaRepo".
        /// The host segment after "Mart://" is the catalog ROOT and is matched
        /// generically ([^/]+), NOT the literal "Mart", so a renamed root (e.g.
        /// "Mart://TestRoot/Kursat/MetaRepo") still yields the same stem.
        /// Returns "" if the active PU is not a Mart-opened model.
        /// </summary>
        private string ParseActivePuCatalog()
        {
            try
            {
                string locator = _currentModel?.PropertyBag()?.Value("Locator")?.ToString() ?? "";
                var mm = System.Text.RegularExpressions.Regex.Match(locator, @"Mart://[^/]+/([^?&]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mm.Success) return mm.Groups[1].Value;
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Reads the active PU's version number from its locator
        /// (<c>...?version=N</c>). Returns -1 if not determinable.
        /// </summary>
        private int ParseActivePuVersion()
        {
            try
            {
                string locator = _currentModel?.PropertyBag()?.Value("Locator")?.ToString() ?? "";
                var mm = System.Text.RegularExpressions.Regex.Match(locator, @"[?&]version=(\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mm.Success && int.TryParse(mm.Groups[1].Value, out int v)) return v;
            }
            catch { }
            return -1;
        }

        #endregion

        #region UI Helpers

        private void BtnClose_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Only block user-initiated close (X button, Alt+F4).
            // Allow: erwin shutdown, Windows shutdown, TaskKill, internal ForceClose.
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.WindowState = FormWindowState.Minimized;
                return;
            }

            // The add-in is genuinely closing now (erwin/Windows shutdown,
            // TaskKill, or our ForceClose). Stamp the session END_TIME from this
            // managed close event - it is far more reliable than
            // AppDomain.ProcessExit, which erwin's native COM-host teardown does
            // NOT raise dependably (observed: a manual erwin close left END_TIME
            // NULL). Bounded internally so a slow repo cannot hang the close.
            try { Services.SessionTrackingService.Instance.NotifyHostClosing(); }
            catch (Exception ex) { Log($"SessionTracking NotifyHostClosing error: {ex.Message}"); }

            base.OnFormClosing(e);
        }


        /// <summary>
        /// Actually closes the form (called when erwin shuts down or corporate check fails).
        /// </summary>
        internal void ForceClose()
        {
            _allowClose = true;
            Close();
        }

        /// <summary>
        /// Closes the SELECTED RIGHT-SIDE version PU (e.g. v1) that CC loaded,
        /// leaving the active v3 model untouched. This evicts erwin's CC
        /// engine cache of the dirtied v1 so subsequent Generate DDL runs
        /// don't see "compare-to-itself". Iterates SCAPI.PersistenceUnits,
        /// finds the one whose locator matches the target catalog+version,
        /// closes it.
        /// </summary>
        /// <summary>
        /// DIAG: enumerates SCAPI.PersistenceUnits and logs each one's
        /// Name / Locator / Modified flag so we can see exactly which PUs
        /// survive across DDL-generate runs and whether they are dirty.
        /// </summary>
        private void LogSessionPUs(string phase, Action<string> log)
        {
            try
            {
                dynamic pus = _scapi?.PersistenceUnits;
                if (pus == null)
                {
                    log?.Invoke($"  [DIAG {phase}] no SCAPI session");
                    return;
                }
                int count = 0;
                try { count = (int)pus.Count; } catch { }
                log?.Invoke($"  [DIAG {phase}] SCAPI session has {count} PU(s):");
                for (int i = 0; i < count; i++)
                {
                    dynamic pu = null;
                    try { pu = pus.Item(i); } catch { continue; }
                    if (pu == null) continue;

                    string name = "?";
                    try { name = (pu.Name?.ToString()) ?? "?"; } catch { }

                    string locator = "?";
                    try { locator = pu.PropertyBag()?.Value("Locator")?.ToString() ?? "?"; } catch { }

                    // Try several property names; SCAPI docs are inconsistent.
                    string dirty = "?";
                    foreach (var prop in new[] { "Modified", "IsModified", "IsDirty", "Dirty", "HasChanges" })
                    {
                        try
                        {
                            var val = pu.PropertyBag()?.Value(prop);
                            if (val != null) { dirty = $"{prop}={val}"; break; }
                        }
                        catch { }
                    }
                    // Some impls expose Modified as an object property too.
                    if (dirty == "?")
                    {
                        try { dirty = $"pu.Modified={pu.Modified}"; }
                        catch { }
                    }
                    if (dirty == "?")
                    {
                        try { dirty = $"pu.IsModified={pu.IsModified}"; }
                        catch { }
                    }

                    bool isActive = false;
                    try { isActive = object.ReferenceEquals(pu, _currentModel); } catch { }

                    log?.Invoke($"    PU[{i}]{(isActive ? " *ACTIVE*" : "")} name='{name}' {dirty}");
                    log?.Invoke($"           locator='{locator}'");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [DIAG {phase}] err: {ex.Message}");
            }
        }

        /// <summary>
        /// True when a PU with exactly this locator is still loaded in the
        /// SCAPI session. Used by the cross-version pipeline finally to decide
        /// whether the reconnect guard for the pipeline-opened version copy can
        /// be disarmed. Read errors return TRUE (keep the guard armed): a guard
        /// that outlives the copy is self-pruned by the reconnect tick, while a
        /// guard dropped on a still-loaded copy re-opens the adopt-and-dirty
        /// hole this fix closes (2026-06-10 incident).
        /// </summary>
        private bool IsPuLocatorStillLoaded(string locator, Action<string> log)
        {
            try
            {
                dynamic pus = _scapi?.PersistenceUnits;
                if (pus == null) return false;
                int count = (int)pus.Count;
                bool anyReadFailed = false;
                for (int i = 0; i < count; i++)
                {
                    dynamic pu = null;
                    try { pu = pus.Item(i); } catch (Exception ex) { log?.Invoke($"[REVIEW] leftover check: PU[{i}] read err: {ex.Message}"); anyReadFailed = true; continue; }
                    if (pu == null) continue;
                    string loc = string.Empty;
                    try { loc = Services.PuLocatorReader.Read(pu, allowWindowTitleFallback: false) ?? string.Empty; }
                    catch (Exception ex) { log?.Invoke($"[REVIEW] leftover check: PU[{i}] locator err: {ex.Message}"); anyReadFailed = true; }
                    if (string.Equals(loc, locator, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                // A failed per-PU read means the copy may be hiding behind the
                // failure - honor the keep-armed-on-doubt contract.
                if (anyReadFailed)
                {
                    log?.Invoke("[REVIEW] leftover check inconclusive (a PU read failed) - keeping reconnect guard armed.");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[REVIEW] leftover check failed ({ex.Message}) - keeping reconnect guard armed.");
                return true;
            }
        }

        private void CloseSelectedVersionPU(int rightVersion, string catalog, Action<string> log)
        {
            try
            {
                dynamic pus = _scapi?.PersistenceUnits;
                if (pus == null)
                {
                    log?.Invoke("close right PU: no SCAPI session");
                    return;
                }
                int count = (int)pus.Count;
                log?.Invoke($"close right PU: scanning {count} open PU(s) for v{rightVersion} of '{catalog}'");
                for (int i = count - 1; i >= 0; i--)
                {
                    dynamic pu = null;
                    try { pu = pus.Item(i); } catch { continue; }
                    if (pu == null) continue;
                    string locator = "";
                    try { locator = pu.PropertyBag()?.Value("Locator")?.ToString() ?? ""; } catch { }
                    string name = "";
                    try { name = pu.Name?.ToString() ?? ""; } catch { }
                    // Skip the active model; we only want to evict the right-
                    // side stale PU that CC loaded, never the active left.
                    bool isActive = false;
                    try { isActive = object.ReferenceEquals(pu, _currentModel); } catch { }
                    // Match the right-side PU: same catalog, and Locator
                    // carries version info like "...?version=1" or similar.
                    bool looksRight = !isActive
                        && !string.IsNullOrEmpty(locator)
                        && locator.IndexOf(catalog, StringComparison.OrdinalIgnoreCase) >= 0
                        && locator.IndexOf($"version={rightVersion}", StringComparison.OrdinalIgnoreCase) >= 0;
                    log?.Invoke($"  PU[{i}] name='{name}'{(isActive ? " *ACTIVE*" : "")} locator='{locator}' {(looksRight ? "<-- TARGET" : "")}");
                    if (looksRight)
                    {
                        // Per SCAPI reference guide 15.0 p.267:
                        //   Application.PersistenceUnits.Remove(pu, False)
                        // The 2nd arg is "save before remove" - False means
                        // drop the in-memory dirty state without prompting
                        // a save dialog. Directly invoking pu.Close() does
                        // NOT exist on the COM object (no-such-member error);
                        // the collection's Remove is the right API.
                        try
                        {
                            pus.Remove(pu, false);
                            log?.Invoke($"  removed right-side PU[{i}] (save=false)");
                        }
                        catch (Exception cex)
                        {
                            log?.Invoke($"  Remove() err: {cex.Message}");
                            // Belt-and-braces fallback with the save-prompt
                            // watcher in case another SCAPI build exposes
                            // Close() instead.
                            var watcher = Services.MartMartAutomation.DismissErwinPopupInBackground(4000, log);
                            try { pu.Close(); log?.Invoke($"  fallback Close() ok"); }
                            catch (Exception cex2) { log?.Invoke($"  fallback Close() err: {cex2.Message}"); }
                            try { watcher?.Wait(500); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"CloseSelectedVersionPU err: {ex.Message}");
            }
        }

        /// <summary>
        /// Modern, borderless "please wait" overlay shown during the
        /// Mart-Mart automation. Drop shadow + accent bar + primary/
        /// secondary text. Toggled via <see cref="ToggleBusyOverlay"/>.
        /// </summary>
        private Form ShowBusyOverlay(string message)
        {
            // Debug mode: skip the overlay entirely so the user can see
            // erwin's dialogs / wizard state while the pipeline runs. All
            // callers null-check the returned Form already; ToggleBusyOverlay
            // also no-ops on null. The pipeline becomes visible+slow when
            // the dev "Generate DDL (debug)" button (#if !PACKAGED) is used.
            if (Services.DebugMode.Enabled) return null;

            var accent = Color.FromArgb(46, 125, 50);       // Elite Soft green
            var dark   = Color.FromArgb(40, 42, 54);
            var subtle = Color.FromArgb(100, 110, 130);

            var f = new Form
            {
                Text = "",
                ClientSize = new Size(840, 360),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.FromArgb(180, 180, 180),  // acts as 1px border
                Padding = new Padding(1),
            };

            // Main panel (content area).
            var inner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(0),
            };
            f.Controls.Add(inner);

            // Accent stripe at the top (visual interest).
            var stripe = new Panel
            {
                Dock = DockStyle.Top,
                Height = 6,
                BackColor = accent,
            };
            inner.Controls.Add(stripe);

            // Spinner dots - centred vertically in upper third.
            var spinner = new Label
            {
                Text = "• • •",
                AutoSize = false,
                Size = new Size(160, 60),
                Location = new Point((f.ClientSize.Width - 160) / 2, 90),
                Font = new Font("Segoe UI", 28F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = accent,
                BackColor = Color.Transparent,
            };
            inner.Controls.Add(spinner);

            // Primary message - centred middle.
            var lbl = new Label
            {
                Text = message,
                AutoSize = false,
                Size = new Size(f.ClientSize.Width - 40, 60),
                Location = new Point(20, 180),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 17F, FontStyle.Regular),
                ForeColor = dark,
                BackColor = Color.Transparent,
            };
            inner.Controls.Add(lbl);

            // Secondary hint - lower third.
            var hint = new Label
            {
                Text = "Please do not interact with erwin during this operation.",
                AutoSize = false,
                Size = new Size(f.ClientSize.Width - 40, 40),
                Location = new Point(20, 260),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Regular),
                ForeColor = subtle,
                BackColor = Color.Transparent,
            };
            inner.Controls.Add(hint);

            // Animated dots: cycle "•    ", "• •  ", "• • •", ...
            int step = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 350 };
            timer.Tick += (s, e) =>
            {
                step = (step + 1) % 4;
                spinner.Text = step switch
                {
                    0 => "•",
                    1 => "• •",
                    2 => "• • •",
                    _ => "• • • •",
                };
            };
            f.FormClosed += (s, e) => timer.Stop();
            timer.Start();

            f.Show(this);
            f.BringToFront();
            Application.DoEvents();
            return f;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
        private const int GWL_EXSTYLE_MCF = -20;
        private const long WS_EX_LAYERED_MCF = 0x00080000;
        private const long WS_EX_TRANSPARENT_MCF = 0x00000020;
        private const uint LWA_ALPHA_MCF = 0x00000002;

        /// <summary>
        /// Toggles the form between "interactive" and "click pass-through"
        /// state. visible=true keeps the form fully opaque AND interactive
        /// (the user can use the addin). visible=false keeps the form
        /// VISIBLE (no flicker, no hide/show) but adds WS_EX_TRANSPARENT so
        /// any synthesized mouse input at form-covered screen coordinates
        /// passes through to whatever lies below (the RD listview).
        /// Replaces the previous ShowWindow(SW_HIDE) approach which incurred
        /// ~200-300ms per direction for the main form's compositor recalc +
        /// child layout + repaint, observed as ~475ms unaccounted time
        /// during the Apply-to-Right click sequence.
        /// </summary>
        private void ToggleBusyOverlay(Form overlay, bool visible)
        {
            try
            {
                // Small overlay popup: still hide/show as before - it's a
                // tiny window, ShowWindow on it is fast (<5ms).
                int cmd = visible ? SW_SHOW : SW_HIDE;
                if (overlay != null && !overlay.IsDisposed && overlay.Handle != IntPtr.Zero)
                    ShowWindow(overlay.Handle, cmd);

                // Main form: stay visible always, just toggle mouse-transparency.
                if (this.Handle != IntPtr.Zero)
                {
                    IntPtr h = this.Handle;
                    long ex = GetWindowLongPtr(h, GWL_EXSTYLE_MCF).ToInt64();
                    if (visible)
                    {
                        // FULLY restore: clear BOTH WS_EX_TRANSPARENT AND
                        // WS_EX_LAYERED. Leaving WS_EX_LAYERED set on the
                        // form breaks ShowDialog children: modal dialogs
                        // (DbConnectionForm "Configure", model pickers,
                        // etc.) inherit the parent's layered state and end
                        // up invisible-but-modal - user sees nothing,
                        // pressing ESC dismisses the hidden dialog and
                        // returns to the addin. Same hidden-modal pattern
                        // we hit during Mart Offline cleanup.
                        long newEx = ex & ~(WS_EX_TRANSPARENT_MCF | WS_EX_LAYERED_MCF);
                        SetWindowLongPtr(h, GWL_EXSTYLE_MCF, new IntPtr(newEx));
                    }
                    else
                    {
                        // Make form click-through: WS_EX_TRANSPARENT requires
                        // WS_EX_LAYERED to take effect. Alpha stays 255 (fully
                        // opaque) - no visual change, just mouse routing.
                        long newEx = ex | WS_EX_LAYERED_MCF | WS_EX_TRANSPARENT_MCF;
                        SetWindowLongPtr(h, GWL_EXSTYLE_MCF, new IntPtr(newEx));
                        SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA_MCF);
                    }
                }
            }
            catch { }
        }

        // Static so ErwinAddIn.Execute() can show the splash before the
        // ModelConfigForm instance exists - covers the ~1.5s of
        // CheckLicense + ctor + Show overhead with visible feedback.
        // Caller is responsible for closing the returned form (typically
        // handed off to ConnectToModel which disposes it in finally).
        internal static Form ShowLoadingDialog(string message)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            System.IO.Stream imgStream = null;
            Image splashImage = null;

            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith("erwin-addi-in-splash.png", StringComparison.OrdinalIgnoreCase))
                {
                    imgStream = assembly.GetManifestResourceStream(name);
                    break;
                }
            }

            if (imgStream != null)
                splashImage = Image.FromStream(imgStream);

            int formWidth = 420;
            int formHeight = 220;

            var loadingForm = new Form
            {
                Text = "Elite Soft Erwin Add-In",
                ClientSize = new Size(formWidth, formHeight),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.None,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.FromArgb(208, 208, 208),
                Padding = new Padding(1)
            };

            var innerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };
            loadingForm.Controls.Add(innerPanel);

            if (splashImage != null)
            {
                var pictureBox = new PictureBox
                {
                    Image = splashImage,
                    SizeMode = PictureBoxSizeMode.StretchImage,
                    Dock = DockStyle.Fill
                };
                innerPanel.Controls.Add(pictureBox);

                var label = new Label
                {
                    Text = message,
                    Font = new Font("Segoe UI", 12, FontStyle.Bold),
                    ForeColor = Color.Black,
                    BackColor = Color.Transparent,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Size = new Size(formWidth, 40),
                    Location = new Point(0, formHeight - 45)
                };
                pictureBox.Controls.Add(label);
            }
            else
            {
                var label = new Label
                {
                    Text = message,
                    Font = new Font("Segoe UI", 14, FontStyle.Bold),
                    ForeColor = Color.FromArgb(60, 60, 60),
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill
                };
                innerPanel.Controls.Add(label);
            }

            loadingForm.Show();
            Application.DoEvents();
            return loadingForm;
        }

        private void UpdateConnectionStatus(string status, Color color)
        {
            lblConnectionStatus.Text = status;
            lblConnectionStatus.ForeColor = color;
        }

        private void UpdateStatus(string message, Color color)
        {
            lblStatus.Text = message;
            lblStatus.ForeColor = color;
        }

        private void ShowError(string message, string title)
        {
            UpdateConnectionStatus(StatusDisconnected, Color.Red);
            _isConnected = false;
            UpdateStatus("Connection failed.", Color.Red);
            AddinMessageDialog.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        #endregion

        #region Helper Methods

        private dynamic GetRootObject(dynamic modelObjects)
        {
            dynamic rootObj = null;
            try
            {
                dynamic saCollection = modelObjects.Collect("Subject_Area");
                if (saCollection.Count > 0)
                {
                    rootObj = saCollection.Item(0);
                }
            }
            catch { }

            if (rootObj == null)
            {
                try { rootObj = modelObjects.Root; } catch { }
            }

            return rootObj;
        }

        private void TrySetProperty(dynamic obj, string propertyName, object value)
        {
            try
            {
                obj.Properties(propertyName).Value = value;
            }
            catch (Exception ex)
            {
                Log($"Could not set {propertyName}: {ex.Message}");
            }
        }

        #endregion

        #region Resource Cleanup

        private void CloseCurrentSession()
        {
            if (_session != null && _isConnected)
            {
                // Only close if we believe the session is still alive.
                // Calling Close() on a dead COM object causes a native crash in erwin.
                try { _session.Close(); } catch { }
            }
            _session = null;
        }

        private void DisposeServices()
        {
            _validationService?.Dispose();
            _tableTypeMonitorService?.Dispose();
            _validationCoordinatorService?.Dispose();
            _propertyApplicatorService?.Dispose();
            _propertyApplicatorService = null;
            _udpRuntimeService?.Dispose();
            _udpRuntimeService = null;
        }

        /// <summary>
        /// Called when the SCAPI session becomes invalid (model closed from erwin).
        /// Safely stops all services without trying to access the dead session.
        /// </summary>
        private void HandleModelUdpChanged(string udpName, string newValue)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, string>(HandleModelUdpChanged), udpName, newValue); } catch { }
                return;
            }

            if (_dependencySetService == null || !_dependencySetService.IsLoaded) return;
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;

            var targets = _dependencySetService.GetAffectedUdps(udpName);
            if (targets.Count == 0) return;

            Log($"Model UDP '{udpName}' = '{newValue}' -> cascade: [{string.Join(", ", targets.Select(t => t.UdpName))}]");

            try
            {
                var modelUdpValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [udpName] = newValue
                };

                // Pre-compute new options for each target
                var updates = new List<(CascadeTarget target, string validValues, int count)>();
                foreach (var target in targets)
                {
                    var opts = _dependencySetService.GetListUdpOptions(target.UdpName, modelUdpValues);
                    if (opts != null && opts.Count > 0)
                        updates.Add((target, string.Join(",", opts), opts.Count));
                }

                if (updates.Count == 0) return;

                // Single metamodel session, single PT scan, single transaction
                dynamic mmSession = null;
                try
                {
                    mmSession = _scapi.Sessions.Add();
                    mmSession.Open(_currentModel, 1);
                    dynamic mmObjects = mmSession.ModelObjects;
                    dynamic mmRoot = mmObjects.Root;

                    // Build UDP name -> (target, validValues, count) lookup
                    var udpUpdateMap = new Dictionary<string, (CascadeTarget target, string validValues, int count)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var u in updates)
                        udpUpdateMap[u.target.UdpName] = u;

                    int remaining = udpUpdateMap.Count;
                    var ptMatches = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);

                    // Single PT scan - match by UDP name suffix (after last dot)
                    dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (remaining == 0) break;
                        if (pt == null) continue;
                        try
                        {
                            string ptName = pt.Name ?? "";
                            int lastDot = ptName.LastIndexOf('.');
                            if (lastDot < 0) continue;
                            string udpSuffix = ptName.Substring(lastDot + 1);

                            if (udpUpdateMap.ContainsKey(udpSuffix) && !ptMatches.ContainsKey(udpSuffix))
                            {
                                ptMatches[udpSuffix] = pt;
                                remaining--;
                            }
                        }
                        catch { }
                    }

                    // Single transaction for all updates
                    int transId = mmSession.BeginNamedTransaction("CascadeUpdate");
                    var updatedNames = new List<string>();
                    try
                    {
                        foreach (var kvp in udpUpdateMap)
                        {
                            if (!ptMatches.TryGetValue(kvp.Key, out var targetPt))
                            {
                                Log($"Cascade: PT not found for '{kvp.Key}'");
                                continue;
                            }

                            targetPt.Properties("tag_Udp_Data_Type").Value = 6;
                            targetPt.Properties("tag_Udp_Values_List").Value = kvp.Value.validValues;
                            updatedNames.Add($"{kvp.Key}({kvp.Value.count})");
                        }
                        mmSession.CommitTransaction(transId);
                    }
                    catch (Exception ex)
                    {
                        try { mmSession.RollbackTransaction(transId); } catch { }
                        Log($"Cascade transaction failed: {ex.Message}");
                    }

                    if (updatedNames.Count > 0)
                        Log($"Cascade updated: {string.Join(", ", updatedNames)}");
                }
                finally
                {
                    try { mmSession?.Close(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"HandleModelUdpChanged error: {ex.Message}");
            }
        }

        private void HandleModelChanged(string newModelName)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(HandleModelChanged), newModelName); } catch { }
                return;
            }

            SwitchToModel(newModelName);
        }

        private void HandleSessionLost()
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(HandleSessionLost)); } catch { }
                return;
            }

            Log("Model closed - session lost. Cleaning up services.");
            DdlGenerationService.ClearBaseline();

            try
            {
                _glossaryRefreshTimer?.Stop();
                _glossaryRefreshTimer?.Dispose();
                _glossaryRefreshTimer = null;

                DisposeServices();
                _validationService = null;
                _tableTypeMonitorService = null;
                _validationCoordinatorService = null;

                // Don't try _session.Close() — session is already dead (native crash risk)
                _session = null;
                _currentModel = null;
                _isConnected = false;
                _lastConnectedLocator = null;
                _knownLocators.Clear();

                // Clear stale model references to prevent user from selecting dead COM objects
                _openModels.Clear();
                _connectedModelName = null;
                _globalDataLoaded = false;
                lblActiveModel.Text = "(Waiting for model...)";

                // Reset General tab info. ForeColor MUST be reset too -
                // a prior degraded-mode cycle would have left Corporate red,
                // and the disconnect path would otherwise paint "-" in red
                // until the next successful connect repaints it.
                _lblCorporateValue.Text = "-";
                _lblCorporateValue.ForeColor = Color.FromArgb(120, 120, 120);

                // Reset UI to disconnected state
                UpdateConnectionStatus(StatusDisconnected, Color.Red);
                btnValidateAll.Enabled = false;
                lblPlatformStatus.Text = "";
                UpdateStatus("Model closed. Waiting for a model to open...", Color.Gray);

                // Start reconnect timer to poll for new models
                InitializeGlossaryRefreshTimer();
                StartReconnectTimer();
            }
            catch { }
        }

        private void CleanupResources()
        {
            try
            {
                StopReconnectTimer();

                _glossaryRefreshTimer?.Stop();
                _glossaryRefreshTimer?.Dispose();
                _glossaryRefreshTimer = null;

                DisposeServices();
                _validationService = null;
                _tableTypeMonitorService = null;
                _validationCoordinatorService = null;

                try { _session?.Close(); } catch { }
                try { _scapi?.Sessions?.Clear(); } catch { }
            }
            catch { }
        }

        #endregion

        /// <summary>
        /// Tab-switch hook: logs every tab transition so a future erwin AV can
        /// be correlated to the tab the user was on. Earlier crashes
        /// (2026-05-09, coreclr AV during a tab entry) had no log breadcrumbs -
        /// the addin's own Execute log ended cleanly, and only the Windows WER
        /// report identified the host module. Logging here gives us a definitive
        /// last-tab marker.
        /// </summary>
        private void tabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                var sel = tabControl.SelectedTab;
                Log($"[TAB] -> {sel?.Text ?? "(null)"} ({sel?.Name ?? "-"})");
            }
            catch (Exception ex)
            {
                Log($"tabControl_SelectedIndexChanged: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
