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

        // State tracking
        private Timer _glossaryRefreshTimer;
        private Timer _reconnectTimer;
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
                    var titleSb = new System.Text.StringBuilder(256);
                    Services.Win32Helper.GetClassNamePublic(fg, classSb, classSb.Capacity);
                    Services.Win32Helper.GetWindowTextPublic(fg, titleSb, titleSb.Capacity);
                    uint fgPid = Services.Win32Helper.GetWindowThreadProcessIdPublic(fg);
                    Log($"[FOCUS] form Deactivate -> fg=0x{fg.ToInt64():X} class='{classSb}' title='{titleSb}' pid={fgPid}");
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
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
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
                ShowError($"Failed to load models:\n{ex.Message}", "Connection Error");
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
                        _knownLocators.Add(loc);
                    }
                }
                catch (Exception kex)
                {
                    Log($"ConnectToModel: failed to seed known-locators set: {kex.Message}");
                    // Defensive: ensure at least the active locator is recorded
                    // so the tick has SOMETHING to compare against.
                    _knownLocators.Add(puLocator);
                }

                // Record the open PU count so the tick can detect a PU being
                // CLOSED (the locator-diff path only catches PUs being added).
                _lastConnectPuCount = openPuCount;

                // Snapshot the current active-window-title locator so the tick
                // can detect a user-driven MDI tab switch between two open
                // PUs without a PU set change. Reading the title here (not in
                // the tick body alone) ensures the baseline matches the PU we
                // just bound to.
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
                    // Still populate DDL combos for RE models
                    cmbLeftModel.Items.Clear();
                    cmbLeftModel.Items.Add($"Active Model ({_connectedModelName})");
                    cmbLeftModel.SelectedIndex = 0;
                    lblOpenedModel.Text = $"Opened Model: {_connectedModelName} (with last changes)";
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

            for (int i = 0; i < count; i++)
            {
                string puLoc = string.Empty;
                try { puLoc = Services.PuLocatorReader.Read(persistenceUnits.Item(i), allowWindowTitleFallback: false) ?? string.Empty; }
                catch (Exception ex) { Log($"TabSwitch: PU[{i}] locator read error: {ex.Message}"); }

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
                    Log($"TabSwitch: matched PU[{i}] by Mart stem '{puStem}'");
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
                string rawTitle = hWnd != IntPtr.Zero ? Services.Win32Helper.GetWindowTextSafe(hWnd) : "(no erwin main window)";
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
            try
            {
                dynamic persistenceUnits = _scapi.PersistenceUnits;
                int count = persistenceUnits.Count;
                if (count == 0) return;

                // Disconnected path: we have no connect cycle yet, no known
                // locators are recorded - just bind to PU 0 and let
                // ConnectToModel seed the set.
                if (!_isConnected)
                {
                    Log($"Model detected ({count} open). Connecting...");

                    _openModels.Clear();
                    for (int i = 0; i < count; i++)
                        _openModels.Add(persistenceUnits.Item(i));

                    if (_openModels.Count > 0)
                        ConnectToModel(0);
                    return;
                }

                // PU count drop detection: when count < known-set size, the
                // user just closed at least one PU. The add-in's _currentModel
                // may now point at the closed PU or a stale CONFIG (verified
                // 2026-05-14 11:46: degraded on Model_12, user closed it and
                // switched focus back to the Mart model, but tick saw every
                // remaining PU's locator empty and treated it as "no change",
                // leaving the add-in stuck in degraded mode). Force a full
                // re-init so ConfigContext re-resolves against whatever PU is
                // still open. ConnectToModel(0) is the simplest pick because
                // titleFallback becomes safe again when only one PU remains.
                if (count < _knownLocators.Count)
                {
                    Log($"PU count dropped {_knownLocators.Count} -> {count}; clearing _globalDataLoaded and reconnecting active PU so ConfigContext re-resolves.");
                    _globalDataLoaded = false;

                    _openModels.Clear();
                    for (int i = 0; i < count; i++)
                        _openModels.Add(persistenceUnits.Item(i));

                    if (_openModels.Count > 0)
                        ConnectToModel(0);
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
                    if (count < _lastConnectPuCount)
                    {
                        Log($"PU closed: count {_lastConnectPuCount} -> {count}; reconnecting to PU 0 with full re-init.");
                        _globalDataLoaded = false;
                        _openModels.Clear();
                        for (int i = 0; i < count; i++)
                            _openModels.Add(persistenceUnits.Item(i));
                        ConnectToModel(0);
                        return;
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
                    if (count > 1)
                    {
                        string currentTitleLoc = Services.PuLocatorReader.ReadFromWindowTitle() ?? string.Empty;

                        // Diagnostic heartbeat: log the title locator every
                        // ~10 s (20 ticks at 500 ms) so we can see WHAT
                        // ReadFromWindowTitle is returning when the user
                        // claims a tab switch went unnoticed. Without this,
                        // a silent log makes both "user did not switch tabs"
                        // and "we read the wrong title locator" look identical.
                        _tabPollDebugTickCounter++;
                        if (_tabPollDebugTickCounter >= 20)
                        {
                            _tabPollDebugTickCounter = 0;
                            // One-line summary on idle. Full ground-truth dump
                            // (raw title + per-PU name/locator) is reserved
                            // for actual tab-switch firings - cheap there
                            // because it runs at most a few times per minute.
                            // The previous per-heartbeat dump produced 4
                            // log lines every 10 s for the lifetime of the
                            // process; trimmed 2026-05-14 once the locator
                            // matcher proved stable in production logs.
                            Log($"[TabPoll] count={count} titleLoc='{currentTitleLoc}' boundName='{_connectedModelName}'");
                        }

                        if (!string.Equals(currentTitleLoc, _lastObservedTitleLocator, StringComparison.OrdinalIgnoreCase))
                        {
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
        }

        #endregion

        #region Validation Service

        /// <summary>
        /// Full initialization: corporate + global data + model-specific services.
        /// Called on first connect.
        /// </summary>
        private void InitializeValidationService()
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
                // Degraded mode: keep the form alive without validation/glossary.
                // Calling ForceClose() here disposed the form mid-Show(), which then
                // surfaced as "Cannot access a disposed object" from Execute(). The
                // user wants the add-in to load even when the active model is not
                // mart-bound or has no CONFIG mapping, so they can still use the
                // non-validation features (DDL compare/version tabs, debug log, etc).
                string reason = ctx.LastError
                    ?? "No configuration is defined for the model you are trying to load. Add-in controls will be disabled.";
                string contextPath = ctx.LastErrorPath ?? "";
                Log($"Config not resolved: {reason} (path='{contextPath}') -- running in degraded mode (no validation/glossary).");
                UpdateStatus("Connected (no config: add-in controls disabled).", Color.Red);
                btnValidateAll.Enabled = false;
                // In degraded mode the active PU has no Mart locator. Calling
                // dynamic dispatch SCAPI methods (PropertyBag().Value("Locator"),
                // FEModel_DDL, ...) on a non-Mart PU triggers a NULL deref deep
                // in EM_GDM/mfc140 whose IDispatchInvoke unwind crashes the host
                // process (verified 2026-05-08 13:48 against a PowerDesigner-
                // imported local .erwin file). Disable every action button that
                // would touch SCAPI on the active PU, so the user can't trigger
                // the AV from the UI. The form stays open for non-action surfaces
                // (General tab, log file link, version compare for OTHER models).
                btnAlterWizardProd.Enabled = false;
                btnMartReview.Enabled = false;
                lblDDLStatus.Text = "Disabled until a Mart-bound model with CONFIG mapping is loaded.";
                lblDDLStatus.ForeColor = Color.Red;
                using (AddinLogger.BeginScope("UpdateGeneralTab(degraded)"))
                    UpdateGeneralTab();
                // Defer the modal dialog so Form.Load can finish and Show()
                // can return - calling ShowDialog directly here would nest a
                // modal pump inside the parent form's Load handler, the
                // ModelConfigForm window would never finish painting, and
                // the addin would appear "not loaded" with no visible
                // warning. BeginInvoke lands the call back on the UI thread
                // after the current message completes, by which time the
                // form is fully shown and can host the modal child.
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try { ShowConfigWarningDialog(reason, contextPath); }
                        catch (Exception ex) { Log($"ShowConfigWarningDialog (deferred) failed: {ex.Message}"); }
                    }));
                }
                catch (Exception ex) { Log($"BeginInvoke for config warning failed: {ex.Message}"); }

                // Track this degraded locator and re-arm the reconnect timer so
                // we can detect when the user closes this unmapped model and
                // opens a Mart-bound one with a valid CONFIG mapping. Without
                // these two lines the form stays stuck in degraded mode UI
                // forever after the user switches models, which was the bug
                // reported on 2026-05-09 (local model -> Mart model still
                // showed "no config" + Glossary "(not loaded)").
                _inDegradedMode = true;
                _lastDegradedLocator = locator ?? "";
                StartReconnectTimer();
                return;
            }
            Log($"Config: {ctx.ActiveConfigName} (ID={ctx.ActiveConfigId}), corporate='{ctx.CorporateName ?? "(none)"}', mart='{ctx.MartPath}'");
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
                    var bs = new RegistryBootstrapService();
                    new AddInPropertyMetadataService(bs).GetObjectTypes();
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
            _validationCoordinatorService.SetTableTypeMonitor(_tableTypeMonitorService);
            if (_udpRuntimeService.IsInitialized)
                _validationCoordinatorService.SetUdpRuntimeService(_udpRuntimeService);
            if (_dependencySetService != null && _dependencySetService.IsLoaded)
                _validationCoordinatorService.SetDependencySetService(_dependencySetService);
            using (AddinLogger.BeginScope("ValidationCoordinator.StartMonitoring"))
                _validationCoordinatorService.StartMonitoring(_cachedPropertyTypeNames);

            // DTProbe was a one-shot metamodel discovery spike (~5.7s on every connect).
            // Output is deterministic per erwin install, not per model, so we no longer
            // pay the cost on each load. The MetamodelDatatypeProbe class is preserved
            // and can be invoked manually from a DEV button if the dropdown source
            // question is revisited.

            using (AddinLogger.BeginScope("LoadTablesComboBox"))
                LoadTablesComboBox();
            UpdateValidationStatus();
            Log("Validation service initialized.");
            using (AddinLogger.BeginScope("UpdateGeneralTab"))
                UpdateGeneralTab();
            using (AddinLogger.BeginScope("PopulateVersionCombos"))
                PopulateVersionCombos();

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
        private void RunUdpSyncIfNeeded()
        {
            var ctx = ConfigContextService.Instance;
            if (!ctx.IsInitialized || ctx.ActiveConfigId <= 0)
            {
                Log("UDP sync skipped: no active CONFIG");
                return;
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
                    _cachedPropertyTypeNames = walk.AllNames;

                    diff = UdpSyncEngine.ComputeDiff(snapshot, walk.Map);
                    Log($"UDP sync diff: creates={diff.Creates.Count}, updates={diff.Updates.Count}");
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
                    try
                    {
                        bool apply = UdpSyncDialog.ShowFor(diffLocal, this);
                        if (!apply)
                        {
                            Log($"UDP sync cancelled by user (creates={diffLocal.Creates.Count}, updates={diffLocal.Updates.Count})");
                            return;
                        }

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
                    }
                }));
            }
            catch (Exception ex)
            {
                Log($"UdpSync BeginInvoke failed: {ex.Message}");
                AddConnectWarning($"UDP sync deferral failed: {ex.Message}");
            }
        }


        #region General Tab

        // Labels to update after corporate initialization
        private Label _lblCorporateValue;
        private Label _lblDbValue;
        private Label _lblRegistryValue;
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
            // 5 rows fit comfortably in 158px (5 * 26 + 28 padding).
            const int repoY = 84;
            const int repoH = 158;
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
            AddCardRow(card, "Database:",  "(not loaded)", fontBold, font, 1, out _, out _lblDbValue);
            AddCardRow(card, "Registry:",  "(not loaded)", fontBold, font, 2, out _, out _lblRegistryValue);
            // Warnings row surfaces any service-load failure (schema mismatch,
            // missing rows, ConfigContext degraded mode, ...) so the user does
            // not have to dig through the log file to discover that a
            // background service silently no-op'd. Width is wide because the
            // text can carry multiple semicolon-separated reasons.
            AddCardRow(card, "Warnings:",  "(none)",       fontBold, font, 3, out _, out _lblWarningsValue);
            _lblWarningsValue.MaximumSize = new Size(cardW - 160, 0);
            _lblWarningsValue.AutoSize = true;
            _lblWarningsValue.ForeColor = Color.FromArgb(120, 120, 120);
            // Log file row: clickable link that opens the folder in Explorer
            // with the log file pre-selected. The Debug Log tab was removed
            // 2026-05-07 (UIA event raise from TextBox.AppendText crashed
            // erwin host); the link is the supported way to view the log.
            AddCardRow(card, "Log file:",  AddinLogger.FilePath, fontBold, font, 4, out _, out _lblLogPathValue);
            _lblLogPathValue.ForeColor = clrAccent;
            _lblLogPathValue.Cursor = Cursors.Hand;
            _lblLogPathValue.Click += (s, ev) => OpenLogFolder();
            tabGeneral.Controls.Add(card);

            // --- Middle Card: Active Model ---
            // The legacy grpModel GroupBox sat here with an etched-border that
            // jarred against the modern card chrome above and below. We unhost
            // its child labels and re-place them inside a section card body so
            // the visual rhythm stays consistent. grpModel itself is no longer
            // added to the tab.
            const int modelY = repoY + repoH + 30 + 16;  // +30 for card chrome, +16 spacing
            const int modelH = 60;
            var modelCard = CreateSectionCard("Active Model", cardX, modelY, cardW, modelH, clrCardBg, clrCardHeader);
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

            lblActiveModel.Location = new Point(120, 14);
            lblActiveModel.Font = fontBold;
            lblActiveModel.ForeColor = Color.FromArgb(40, 40, 40);
            lblActiveModel.AutoSize = true;
            modelBody.Controls.Add(lblActiveModel);

            lblConnectionStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblConnectionStatus.Location = new Point(cardW - 220, 14);
            lblConnectionStatus.AutoSize = false;
            lblConnectionStatus.Size = new Size(200, 20);
            lblConnectionStatus.TextAlign = ContentAlignment.MiddleRight;
            modelBody.Controls.Add(lblConnectionStatus);

            lblPlatformStatus.Location = new Point(16, 38);
            lblPlatformStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblPlatformStatus.AutoSize = false;
            lblPlatformStatus.Size = new Size(cardW - 36, 18);
            lblPlatformStatus.Font = new Font("Segoe UI", 8.5f);
            lblPlatformStatus.ForeColor = Color.FromArgb(120, 120, 120);
            modelBody.Controls.Add(lblPlatformStatus);

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
            lblCopyright.Location = new Point(24, glossY + glossH + 30 + 12);

            // Hide Alter Compare tab on startup. The feature is functional but
            // the multi-version compare flow (PlanTargetVersions) is not yet
            // wired up for production usage and the tab adds visual noise.
            // Adding it to _hiddenTabs makes it restorable via the Ctrl+Shift+
            // LeftClick gesture on the copyright label below, same as any
            // other manually-hidden tab.
            if (tabAlterCompare != null && tabControl.TabPages.Contains(tabAlterCompare))
            {
                tabControl.TabPages.Remove(tabAlterCompare);
                _hiddenTabs.Add(tabAlterCompare);
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
            int btnX = (cardW - 2) - btnW - 12;
            int btnY = 12;

            var btn = new Button
            {
                Text = "Reload Config",
                Location = new Point(btnX, btnY),
                Size = new Size(btnW, btnH),
                FlatStyle = FlatStyle.System,
                UseVisualStyleBackColor = true,
                TabStop = false,
            };
            btn.Click += BtnReloadConfig_Click;
            host.Controls.Add(btn);
            return btnX;
        }

        private void BtnReloadConfig_Click(object sender, EventArgs e)
        {
            var btn = sender as Button;
            // Re-entrancy guard: full reload is ~4-5s; disabling the button
            // prevents the user from queuing a second pass that would interleave
            // with the first DisposeServices/LoadGlossary cycle.
            if (btn != null) btn.Enabled = false;
            Form overlay = null;
            try
            {
                Log("Reload Config: user triggered full validation re-init.");
                overlay = ShowBusyOverlay("Reloading config from database, please wait...");
                Application.DoEvents();

                // Force the full pipeline (not the fast model-switch path).
                _globalDataLoaded = false;
                using (AddinLogger.BeginScope("InitializeValidationService(reload)"))
                    InitializeValidationService();

                if (Services.ConfigContextService.Instance.IsInitialized)
                    _globalDataLoaded = true;

                using (AddinLogger.BeginScope("UpdateGeneralTab(reload)"))
                    UpdateGeneralTab();

                Log("Reload Config: complete.");
            }
            catch (Exception ex)
            {
                // Surface the failure rather than swallowing - reload is a
                // dev-only diagnostic, the stack trace is what we need.
                Log($"Reload Config failed: {ex}");
                AddinMessageDialog.Show(this,
                    $"Reload Config failed:\r\n{ex.Message}",
                    "Reload Config",
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
#endif

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
                    _lblCorporateValue.Text = string.IsNullOrEmpty(ctx.LastError)
                        ? "(no config for this model)"
                        : $"(no config: {ctx.LastError})";
                    _lblCorporateValue.ForeColor = Color.Red;
                }

                var config = DatabaseService.Instance.GetConfig();
                if (config != null && config.IsConfigured)
                {
                    _lblDbValue.Text = $"{config.Host}/{config.Database} ({DatabaseService.Instance.GetDbType()})";
                }

                var bootstrapService = new RegistryBootstrapService();
                _lblRegistryValue.Text = bootstrapService.GetConfigFilePath().StartsWith("HKLM") ? "Machine" : "User";

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
            const int defaultInterval = 1; // 1 minute default
            try
            {
                if (!DatabaseService.Instance.IsConfigured) return defaultInterval;

                var config = new RegistryBootstrapService().GetConfig();
                if (config == null || !config.IsConfigured) return defaultInterval;

                // Read GLOSSARY_LOAD_INTERVAL from CONFIG_PROPERTY scoped on the active config.
                // No "All Models" (ID=1) fallback after the schema rename — admin must put a
                // row on the per-config record if they want a custom interval.
                int cfgId = ConfigContextService.Instance.IsInitialized
                    ? ConfigContextService.Instance.ActiveConfigId
                    : 0;
                if (cfgId <= 0) return defaultInterval;

                using (var context = new EliteSoft.MetaAdmin.Shared.Data.RepoDbContext(config))
                {
                    var prop = context.ConfigProperties
                        .FirstOrDefault(p => p.ConfigId == cfgId && p.Key == "GLOSSARY_LOAD_INTERVAL");
                    if (prop != null && int.TryParse(prop.Value, out int minutes) && minutes > 0)
                        return minutes;
                }
            }
            catch (Exception ex)
            {
                Log($"GetGlossaryLoadInterval error: {ex.Message}");
            }
            return defaultInterval;
        }

        private void GlossaryRefreshTimer_Tick(object sender, EventArgs e)
        {
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

                var bootstrapService = new RegistryBootstrapService();
                var config = bootstrapService.GetConfig();
                if (config == null || !config.IsConfigured)
                {
                    Log("PropertyApplicator: DB not configured, skipping");
                    return;
                }

                var metadataService = new AddInPropertyMetadataService(bootstrapService);
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

                    // Per-rule diagnostic dump. Surfaces the EXACT stored
                    // regex pattern, length operator/value, and error
                    // message prefix so admin-side "the rule looks right
                    // but the addin keeps rejecting names" bugs can be
                    // triaged from the file log alone. Verified necessary
                    // 2026-05-15: a regex that admins typed as `^.{0,3}$`
                    // was rejecting every name, and without per-rule
                    // logging we could not see what the addin had actually
                    // loaded vs. what the admin UI displayed.
                    foreach (var rule in service.AllRules)
                    {
                        string regex = rule.RegexpPattern ?? "";
                        string lenOp = string.IsNullOrEmpty(rule.LengthOperator) ? "-" : rule.LengthOperator;
                        string lenVal = rule.LengthValue?.ToString() ?? "-";
                        string udpCond = string.IsNullOrEmpty(rule.DependsOnUdpName)
                            ? "(none)"
                            : $"{rule.DependsOnUdpName}={rule.DependsOnUdpValue ?? "(any)"}";
                        string msg = rule.ErrorMessage ?? "";
                        if (msg.Length > 80) msg = msg.Substring(0, 77) + "...";
                        Log($"  rule#{rule.Id} {rule.ObjectType}.{rule.PropertyCode} " +
                            $"prefix='{rule.Prefix}' suffix='{rule.Suffix}' " +
                            $"len{lenOp}{lenVal} regex(len={regex.Length})='{regex}' " +
                            $"auto={rule.AutoApply} cond={udpCond} msg='{msg}'");
                    }
                }
                else
                {
                    Log($"NAMING_STANDARD not loaded: {service.LastError}");
                    AddConnectWarning($"NamingStandards: {service.LastError}");
                }
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
            bool invoked = Services.Win32Helper.InvokeToolbarButton(hWnd, "Review", Log);

            if (invoked)
                Log("[REVIEW] Review triggered.");
            else
            {
                Log("[REVIEW] 'Review' button not found.");
                ErwinAddIn.ShowTopMostMessage("'Review' button not found in erwin.", "Review");
            }
        }

        private async void BtnAlterWizardProd_Click(object sender, EventArgs e)
        {
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
            rtbDDLOutput.Text = "";
            Application.DoEvents();

            bool martMode = rbFromMart.Checked;
            bool dbMode = rbFromDB.Checked;

            Action<string> log = msg =>
            {
                if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                else Log(msg);
            };

            string script = null;
            string err = null;
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
                    var (dbScript, dbErr) = await RunFromDbDdlPipelineAsync(log);
                    script = dbScript;
                    err = dbErr;
                }
                else if (martMode)
                {
                    int v = ParseRightVersion();
                    int activeV = ParseActivePuVersion();
                    bool sameVersion = (v > 0 && activeV > 0 && v == activeV);

                    if (sameVersion)
                    {
                        // Same version on both sides = "active dirty vs last
                        // saved". OnFE pipeline alone handles this with zero
                        // GUI flashes (no CC wizard, no Mart picker). This is
                        // the proven flash-free path used by Debug Log's
                        // "Normal Alter DDL (dirty vs save)" button.
                        log($"[ROUTE] Same version v{v} on both sides - OnFE fast path (dirty vs last saved, no flashes)");
                        // Splash: the Next-loop + GA detour takes ~1-3 s on
                        // Mart-bound models; without the overlay the user
                        // sees a frozen ribbon and may double-click. Cross-
                        // version + From-DB paths already use the same
                        // ShowBusyOverlay helper - we just hadn't wired it
                        // into the fast path. Restored 2026-05-08.
                        var fastOverlay = ShowBusyOverlay("Generating DDL, please wait...");
                        try
                        {
                            script = await System.Threading.Tasks.Task.Run(() =>
                                Services.NativeBridgeService.GenerateAlterDdl(log));
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
                    }
                    else
                    {
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
                            sess = await Services.MartMartAutomation.DriveCCAndApplyAsync(v, catalog, log, toggle);
                            if (sess == null || !sess.Applied)
                            {
                                err = "Programmatic CC + Apply-to-Right failed. See Debug Log.";
                            }
                            else
                            {
                                script = await System.Threading.Tasks.Task.Run(() =>
                                    Services.NativeBridgeService.GenerateMartMartDdlViaOnFE(log));
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
                            // Resume monitoring fire-and-forget background even
                            // if the pipeline threw. StartMonitoring internally
                            // triggers TakeSnapshot (model walk = several
                            // seconds UI freeze). Same background-resume
                            // pattern as From-DB pipeline.
                            _ = System.Threading.Tasks.Task.Run(() =>
                            {
                                try { _validationCoordinatorService?.ResumeValidation(); }
                                catch (Exception ex) { try { log($"[XV] bg ResumeValidation err: {ex.Message}"); } catch { } }
                                try { _tableTypeMonitorService?.StartMonitoring(); }
                                catch (Exception ex) { try { log($"[XV] bg StartMonitoring err: {ex.Message}"); } catch { } }
                                try { _validationService?.StartMonitoring(); }
                                catch (Exception ex) { try { log($"[XV] bg ColumnValidation StartMonitoring err: {ex.Message}"); } catch { } }
                                try { log("[XV] monitoring resumed (background)"); } catch { }
                            });
                            log("[XV] pipeline complete - monitoring resume scheduled to background");
                        }
                    }
                    } // close: else (different versions) of sameVersion check
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

            if (err != null)
            {
                lblDDLStatus.Text = $"Error: {err}";
                lblDDLStatus.ForeColor = Color.Red;
                rtbDDLOutput.Text = $"-- FAILED: {err}\n";
            }
            else if (script == null)
            {
                if (martMode)
                {
                    lblDDLStatus.Text = "Mart-Mart automation failed (see Debug Log).";
                    lblDDLStatus.ForeColor = Color.Red;
                    rtbDDLOutput.Text = "-- FAILED: programmatic CC + Apply-to-Right did not produce DDL.\n" +
                                        "-- Check Debug Log for the step that failed (CC wizard open, \n" +
                                        "-- Mart picker navigation, Apply-to-Right click, or native DDL capture).\n";
                }
                else if (dbMode)
                {
                    lblDDLStatus.Text = "From-DB automation failed (see Debug Log).";
                    lblDDLStatus.ForeColor = Color.Red;
                    rtbDDLOutput.Text = "-- FAILED: From-DB CC + Apply-to-Right did not produce DDL.\n" +
                                        "-- Check Debug Log for the step that failed (silent RE, MDI tab\n" +
                                        "-- activation, Open-Models-in-Memory picker, Apply-to-Right, OnFE).\n";
                }
                else
                {
                    lblDDLStatus.Text = "erwin did not return a DDL buffer (see Debug Log).";
                    lblDDLStatus.ForeColor = Color.Red;
                    rtbDDLOutput.Text = "-- FAILED: native bridge returned null. Check Debug Log.\n";
                }
            }
            else if (script.Length == 0)
            {
                lblDDLStatus.Text = "No differences detected.";
                lblDDLStatus.ForeColor = Color.OrangeRed;
                if (martMode)
                    rtbDDLOutput.Text = "-- No differences between current model and Mart baseline.\n";
                else if (dbMode)
                    rtbDDLOutput.Text = "-- No differences between current model and DB schema.\n";
                else
                    rtbDDLOutput.Text = "-- No differences between model and last save.\n";
            }
            else
            {
                ShowDDLResult(script, "Alter DDL");
                Log($"DDL produced ({script.Length} chars). Use Copy button to grab it.");
                // The cross-version path now evicts the orphan right-version
                // PU before reaching here (see CloseSelectedVersionPU call
                // inside the cross-version branch). This DIAG dump is the
                // last-stop sanity check that no PUs remain leaked across
                // runs.
                if (martMode || dbMode)
                    LogSessionPUs("POST-RUN", log);
            }

            btnAlterWizardProd.Enabled = true;

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

        private void ShowDDLResult(string content, string label)
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

            // Apply diagram selection filter if "Only Selected Objects" is checked
            displayContent = FilterByDiagramSelection(displayContent);

            ApplySqlHighlighting(displayContent);
            int lineCount = displayContent.Split('\n').Length;
            if (!chkFilterObjects.Checked || lblDDLStatus.Text == "")
            {
                lblDDLStatus.Text = $"{label}: {lineCount} lines.";
                lblDDLStatus.ForeColor = Color.DarkGreen;
            }
        }

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

        private void ApplySqlHighlighting(string sql) => ApplySqlHighlighting(rtbDDLOutput, sql);

        // Generic helper: applies VS-Code-flavored SQL syntax highlighting to
        // any RichTextBox. The DDL Generation tab and the Alter Compare tab
        // both render alter scripts and reuse this so their dark-theme output
        // stays visually identical (same keyword/type/comment/diff colors).
        private void ApplySqlHighlighting(RichTextBox rtb, string sql)
        {
            rtb.SuspendLayout();
            rtb.Clear();
            // Pad with trailing blank lines so the last real line isn't
            // clipped at the bottom of the RichTextBox viewport when the
            // user scrolls all the way down (common RTB rendering issue).
            rtb.Text = sql + "\n\n\n";

            // Set default color
            rtb.SelectAll();
            rtb.SelectionColor = Color.FromArgb(220, 220, 220);

            // IMPORTANT: Use RichTextBox's own text for regex matching
            // RichTextBox converts \r\n to \n internally, so indices differ from original string
            string rtbText = rtb.Text;

            var clrKeyword = Color.FromArgb(86, 156, 214);     // VS Code blue
            var clrType = Color.FromArgb(78, 201, 176);         // VS Code teal
            var clrComment = Color.FromArgb(106, 153, 85);      // VS Code green
            var clrString = Color.FromArgb(206, 145, 120);      // VS Code orange
            var clrNumber = Color.FromArgb(181, 206, 168);      // VS Code light green
            var clrGo = Color.FromArgb(197, 134, 192);          // VS Code purple
            var clrDiffNew = Color.FromArgb(80, 220, 80);       // Bright green
            var clrDiffDrop = Color.FromArgb(240, 80, 80);      // Bright red
            var clrDiffChange = Color.FromArgb(255, 180, 50);   // Bright orange
            var clrSection = Color.FromArgb(220, 220, 100);     // Yellow

            // 1. Keywords (blue)
            HighlightRegex(rtb, rtbText, @"\b(CREATE|ALTER|DROP|TABLE|ADD|COLUMN|CONSTRAINT|PRIMARY|KEY|FOREIGN|REFERENCES|NOT|NULL|DEFAULT|IDENTITY|CLUSTERED|NONCLUSTERED|INDEX|UNIQUE|ON|DELETE|UPDATE|CASCADE|SET|CHECK|WITH|ASC|DESC|BEGIN|END|DECLARE|IF|EXISTS|SELECT|FROM|WHERE|AND|OR|RETURN|GOTO|TRIGGER|FOR|INSERT|AS|RAISERROR|ROLLBACK|TRANSACTION|INTO|ACTION)\b", clrKeyword);

            // 2. Data types (teal)
            HighlightRegex(rtb, rtbText, @"\b(int|bigint|smallint|tinyint|bit|varchar|nvarchar|char|nchar|text|ntext|datetime|smalldatetime|date|time|timestamp|decimal|numeric|float|real|money|smallmoney|varbinary|binary|image|uniqueidentifier|VARCHAR2|NUMBER|CLOB|BLOB|COLLATE)\b", clrType);

            // 3. Numbers (light green)
            HighlightRegex(rtb, rtbText, @"(?<![a-zA-Z_])\d+(?![a-zA-Z_])", clrNumber);

            // 4. GO (purple)
            HighlightRegex(rtb, rtbText, @"(?m)^go$", clrGo);

            // 5. String literals (orange)
            HighlightRegex(rtb, rtbText, @"'[^']*'", clrString);

            // 6. Comments (green) - overrides keywords inside comments
            HighlightRegex(rtb, rtbText, @"--[^\n]*", clrComment);

            // 7. Diff markers (override comment color)
            HighlightRegex(rtb, rtbText, @"-- NEW:.*", clrDiffNew);
            HighlightRegex(rtb, rtbText, @"-- DROPPED:.*", clrDiffDrop);
            HighlightRegex(rtb, rtbText, @"-- CHANGED:.*", clrDiffChange);
            HighlightRegex(rtb, rtbText, @"-- =+.*=+", clrSection);
            HighlightRegex(rtb, rtbText, @"-- Summary:.*", clrSection);
            HighlightRegex(rtb, rtbText, @"-- WARNING:.*", clrDiffDrop);

            rtb.SelectionStart = 0;
            rtb.SelectionLength = 0;
            rtb.ResumeLayout();
        }

        private static void HighlightRegex(RichTextBox rtb, string rtbText, string pattern, Color color)
        {
            try
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(rtbText, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.Multiline))
                {
                    rtb.Select(m.Index, m.Length);
                    rtb.SelectionColor = color;
                }
            }
            catch { }
        }

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
                    string feOpt = _pendingDDLFeOption;
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
                        ShowDDLResult(diff, "DDL Diff");
                    }
                    catch (Exception ex)
                    {
                        lblDDLStatus.Text = $"Error: {ex.Message}";
                        lblDDLStatus.ForeColor = Color.Red;
                    }
                    finally
                    {
                        _validationCoordinatorService?.ResumeValidation();
                        btnAlterWizardProd.Enabled = true;
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

        /// <summary>
        /// Reads diagram selection from erwin's Overview pane and filters DDL diff content
        /// to only include statements related to the selected entities.
        /// Returns the original content if checkbox is unchecked or no entity is selected.
        /// </summary>
        private string FilterByDiagramSelection(string content)
        {
            if (!chkFilterObjects.Checked || string.IsNullOrEmpty(content))
                return content;

            // Read current diagram selection
            var hWnd = Services.Win32Helper.GetErwinMainWindow();
            if (hWnd == IntPtr.Zero) return content;

            string modelName = "";
            try
            {
                dynamic pu = _scapi.PersistenceUnits.Item(0);
                modelName = pu.Name?.ToString() ?? "";
            }
            catch { }
            if (string.IsNullOrEmpty(modelName))
                modelName = _connectedModelName ?? "";

            if (string.IsNullOrEmpty(modelName)) return content;

            var selectedEntities = Services.Win32Helper.GetDiagramSelectedEntities(hWnd, modelName);
            if (selectedEntities.Count == 0)
            {
                Log("DDL: 'Only Selected Objects' checked but no entity selected in diagram.");
                lblDDLStatus.Text = "No entity selected in diagram. Showing all.";
                lblDDLStatus.ForeColor = Color.OrangeRed;
                return content;
            }

            Log($"DDL: Filtering by diagram selection: {string.Join(", ", selectedEntities)}");

            var selected = new HashSet<string>(selectedEntities, StringComparer.OrdinalIgnoreCase);
            var sb = new System.Text.StringBuilder();
            bool inSelectedBlock = false;
            bool inHeader = true;

            foreach (var line in content.Split('\n'))
            {
                string trimmed = line.Trim();

                // Keep header lines
                if (inHeader && trimmed.StartsWith("--") && !trimmed.StartsWith("-- NEW:") &&
                    !trimmed.StartsWith("-- DROPPED:") && !trimmed.StartsWith("-- CHANGED:") &&
                    !trimmed.StartsWith("-- ===="))
                {
                    sb.AppendLine(line.TrimEnd());
                    continue;
                }

                if (trimmed.StartsWith("-- ===="))
                {
                    inHeader = false;
                    sb.AppendLine(line.TrimEnd());
                    continue;
                }

                // Check diff marker lines
                if (trimmed.StartsWith("-- NEW:") || trimmed.StartsWith("-- DROPPED:") || trimmed.StartsWith("-- CHANGED:"))
                {
                    int colonIdx = trimmed.IndexOf(':', 3);
                    string key = colonIdx > 0 ? trimmed.Substring(colonIdx + 1).Trim() : "";

                    string objName = "";
                    if (key.Contains(":"))
                    {
                        string afterColon = key.Split(':')[1];
                        objName = afterColon.Contains(".") ? afterColon.Split('.')[0] : afterColon;
                    }

                    inSelectedBlock = selected.Contains(objName);

                    // Check trigger naming: tD_TableName, tU_TableName, tI_TableName
                    if (!inSelectedBlock && !string.IsNullOrEmpty(objName))
                    {
                        string triggerTable = objName;
                        if (triggerTable.StartsWith("tD_") || triggerTable.StartsWith("tU_") || triggerTable.StartsWith("tI_"))
                            triggerTable = triggerTable.Substring(3);
                        inSelectedBlock = selected.Contains(triggerTable);
                    }
                }

                if (inSelectedBlock)
                    sb.AppendLine(line.TrimEnd());

                if (trimmed.Equals("go", StringComparison.OrdinalIgnoreCase) && inSelectedBlock)
                    inSelectedBlock = true; // keep going until next marker
            }

            string filtered = sb.ToString();
            lblDDLStatus.Text = $"Filtered: {string.Join(", ", selectedEntities)}";
            lblDDLStatus.ForeColor = Color.DarkGreen;
            return filtered;
        }

        private void BtnBrowseFEOption_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select FE Option Set XML";
                dlg.Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtFEOptionXml.Text = dlg.FileName;
                    txtFEOptionXml.ForeColor = System.Drawing.SystemColors.WindowText;
                }
            }
        }

        private void BtnCopyDDL_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(rtbDDLOutput.Text))
            {
                Clipboard.SetText(rtbDDLOutput.Text);
                lblDDLStatus.Text = "DDL copied to clipboard!";
                lblDDLStatus.ForeColor = Color.DarkGreen;
            }
        }

        // DB connection state (From DB mode)
        private string _dbConnectionString = "";
        private string _dbPassword = "";
        private string _dbLabel = "";
        private long _dbTargetServer = 0;
        private int _dbTargetVersion = 0;
        private string _dbSchema = "";
        private List<string> _dbSelectedTables = new List<string>();
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
            try
            {
                dynamic pb = _currentModel.PropertyBag();
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

            // Step 2: tables must be explicitly selected. No silent
            // "all-tables" fallback (feedback_no_silent_fallback) - user
            // must pick the comparison set up front so the resulting alter
            // DDL is never wider than what they asked for.
            var tableList = new List<string>(_dbSelectedTables ?? new List<string>());
            if (tableList.Count == 0)
                return (null, "No tables selected. Use 'Select Tables...' on the From DB row first.");
            log($"[From-DB] using {tableList.Count} selected table(s).");

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
            log("[From-DB] all monitoring services suspended for pipeline duration");

            // GECICI DEBUG: kullanici manuel RD inceleme istedi (2026-04-27).
            // Splash overlay devre disi - RD ekrani gorunur olsun.
            // Apply-to-Right click oncesi MessageBox ile pause.
            const bool DEBUG_FROMDB_VISIBLE = false;
            Form overlay = DEBUG_FROMDB_VISIBLE ? null : ShowBusyOverlay("Generating From-DB DDL, please wait...");
            Action<bool> overlayToggle = DEBUG_FROMDB_VISIBLE ? (Action<bool>)null : (visible =>
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
                try
                {
                    dynamic rePb = rePU.PropertyBag();
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

                // Step 3b: VERIFY the RE'd PU is non-empty before driving CC.
                // SCAPI's RE returns success even when filter excluded all
                // tables (typical: schema-prefixed name vs bare-name mismatch).
                int entityCount = 0;
                int viewCount = 0;
                dynamic verifySess = null;
                try
                {
                    verifySess = _scapi.Sessions.Add();
                    verifySess.Open(rePU, 0, 0);
                    dynamic mo = verifySess.ModelObjects;
                    dynamic root = mo.Root;
                    try
                    {
                        dynamic ents = mo.Collect(root, "Entity");
                        foreach (dynamic _ in ents) entityCount++;
                    }
                    catch (Exception entEx) { log($"[From-DB] Collect Entity err: {entEx.Message}"); }
                    try
                    {
                        dynamic views = mo.Collect(root, "View");
                        foreach (dynamic _ in views) viewCount++;
                    }
                    catch { /* views optional */ }
                }
                catch (Exception verEx)
                {
                    log($"[From-DB] verify session err: {verEx.GetType().Name}: {verEx.Message}");
                }
                finally
                {
                    if (verifySess != null) { try { verifySess.Close(); } catch { } }
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
                log($"[From-DB] DriveCCDbAndApply(reModelName='{rePuName}')");
                sess = await Services.MartMartAutomation.DriveCCDbAndApplyAsync(rePuName, log, overlayToggle, dbgPauseBeforeApply: DEBUG_FROMDB_VISIBLE);
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
                    log("[From-DB] Posting WM_COMMAND 1056 to RD - erwin's handler will invoke OnFE with correct state.");
                    Services.NativeBridgeService.ClearCapturedDdl();
                    Services.MartMartAutomation.ClearLastCapturedWizardDdl();

                    capturedWizard = await Services.MartMartAutomation
                        .ClickRightAlterScriptInRdAsync(sess.ResolveDifferences, log);
                    if (capturedWizard == IntPtr.Zero)
                    {
                        err = "Right Alter Script button click failed - FE Wizard did not appear.";
                    }
                    else
                    {
                        log("[From-DB] FE Wizard opened - driving Next-loop to Preview");
                        bool previewOk = await Services.MartMartAutomation
                            .ClickWizardPreviewTabAsync(capturedWizard, log);
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
                // remove the underlying PU. PUs.Remove on a SCAPI-Create()'d
                // locator='' PU is safe (reference_rescript_pu_removable).
                if (sess != null)
                {
                    // CC + RD cleanup. Validation servisleri pipeline
                    // boyunca suspended oldugu icin rename loop riski yok;
                    // IDCANCEL + fallback ForceDestroy guvenli sayilir.
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
                if (rePU != null)
                {
                    // PU.Remove on a CC-touched silent RE'd PU not only throws
                    // RPC_E_SERVERFAULT (CC engine holds back-references after
                    // wizard frame is gone) but - critically - the CALL ITSELF
                    // invalidates the active mart PU's root object. 09:12 log
                    // shows: PU remove failed -> ValidationCoordinatorService
                    // "root model object {GUID} is not available" -> Session
                    // lost -> add-in reconnect cascade -> next user click
                    // crash. Same root cause as
                    // reference_cross_version_orphan_unsolved.md (Mart-Mart
                    // cross-version "erwin locks within ~3min").
                    //
                    // Skipping the Remove call entirely keeps the active mart
                    // PU usable. The RE'd PU stays in session as an orphan
                    // (Model_N accumulates across runs) - acceptable trade-off
                    // until the CC-engine back-reference is properly cleared
                    // by a future bridge change.
                    log("[From-DB] SKIPPING PU.Remove on RE'd PU - call would invalidate active mart PU root and trigger Session lost cascade.");
                    log("[From-DB] WARNING: RE'd PU stays in SCAPI session as an orphan.");
                    log("[From-DB] If CC's 'Open Models in Memory' picker shows duplicates next run, restart erwin to reset session.");
                }
                try { overlay?.Close(); } catch { }

                // Monitoring resume FIRE-AND-FORGET background. StartMonitoring
                // ic islerinde TakeSnapshot tetikliyor (model walk: 24 entity
                // + 182 attribute = 7sn UI freeze, 13:55:43->13:55:50
                // verified). UI thread'i bloklamayalim - DDL TextBox'a HEMEN
                // yansisin, monitoring arka planda hazir hale gelsin.
                // Validation guard'lari volatile flag uzerinden thread-safe.
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try { _validationCoordinatorService?.ResumeValidation(); }
                    catch (Exception ex) { try { log($"[From-DB] bg ResumeValidation err: {ex.Message}"); } catch { } }
                    try { _tableTypeMonitorService?.StartMonitoring(); }
                    catch (Exception ex) { try { log($"[From-DB] bg StartMonitoring err: {ex.Message}"); } catch { } }
                    try { _validationService?.StartMonitoring(); }
                    catch (Exception ex) { try { log($"[From-DB] bg ColumnValidation StartMonitoring err: {ex.Message}"); } catch { } }
                    try { log("[From-DB] monitoring resumed (background)"); } catch { }
                });
                log("[From-DB] pipeline complete - monitoring resume scheduled to background");
            }

            return (script, err);
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

            // Tables: default to ALL model tables when user hasn't picked explicitly
            var tableList = new List<string>(_dbSelectedTables ?? new List<string>());
            if (tableList.Count == 0)
            {
                tableList = CollectModelTablePhysicalNames();
                if (tableList.Count == 0)
                {
                    ErwinAddIn.ShowTopMostMessage("Active model has no tables to compare.", "From DB");
                    return;
                }
                Log($"DDL: From DB using default (all {tableList.Count} model tables).");
            }

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
            rtbDDLOutput.Text = "";
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
                    ShowDDLResult(diff, $"DDL Diff vs {_dbLabel}");
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
                btnAlterWizardProd.Enabled = true;
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

        private void OnRightSourceChanged()
        {
            bool fromMart = rbFromMart.Checked;
            bool fromDB = rbFromDB.Checked;

            // Right-side version combo is locked for now; user will re-enable it when
            // the multi-version compare path is wired up.
            cmbRightModel.Enabled = false;
            btnConfigureDB.Visible = fromDB;
            btnSelectDbTables.Visible = fromDB;
            lblSelectedTableCount.Visible = fromDB;

            UpdateSelectedTableCountLabel();

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

        private void UpdateSelectedTableCountLabel()
        {
            if (_dbSelectedTables != null && _dbSelectedTables.Count > 0)
                lblSelectedTableCount.Text = $"{_dbSelectedTables.Count} table(s) selected";
            else
                lblSelectedTableCount.Text = "(defaults to ALL model tables)";
        }

        /// <summary>
        /// Fetches the live DB's BASE TABLE list via ODBC and shows a
        /// CheckedListBox picker. User chooses which DB tables to include
        /// in the From-DB compare. No fallback to model tables: if the DB
        /// is not configured or the query fails, we surface a clear error
        /// rather than silently swapping in a different data source.
        /// </summary>
        private void BtnSelectDbTables_Click(object sender, EventArgs e)
        {
            // Require DB connection to be configured first - no fallback.
            bool dbConfigured =
                (!string.IsNullOrWhiteSpace(_dbHost) || !string.IsNullOrWhiteSpace(_dbConnectionString))
                && _dbTypeCode != 0;
            if (!dbConfigured)
            {
                ErwinAddIn.ShowTopMostMessage(
                    "Configure the DB connection first (click Configure on the From DB row).",
                    "Select DB Tables");
                return;
            }

            // _dbConnectionString is erwin's pipe-separated locator (used by
            // the RE pipeline), NOT an ADO.NET connection string. The ODBC
            // connection string for the table-listing query is rebuilt from
            // the captured DbConnectionForm fields - same fields the Test
            // Connection button uses on that form, so a successful Test
            // there guarantees this query path can connect too.
            List<string> dbTables = Services.DbTableBrowserService.FetchTables(
                dbTypeCode: _dbTypeCode,
                host: _dbHost,
                database: _dbName,
                dsnName: _dbDsnName,
                useNative: _dbUseNative,
                useWindowsAuth: _dbUseWindowsAuth,
                user: _dbUser,
                password: _dbPassword,
                schemaFilter: _dbSchema,
                log: Log);

            if (dbTables == null)
            {
                // FetchTables logged the specific failure (bad connection
                // string, ODBC driver missing, query failed, etc).
                ErwinAddIn.ShowTopMostMessage(
                    "Could not list tables from the DB. See Debug Log for details.",
                    "Select DB Tables");
                return;
            }

            if (dbTables.Count == 0)
            {
                string suffix = string.IsNullOrWhiteSpace(_dbSchema)
                    ? ""
                    : $" matching schema filter '{_dbSchema}'";
                ErwinAddIn.ShowTopMostMessage(
                    $"DB has no base tables{suffix}.",
                    "Select DB Tables");
                return;
            }

            // Default: previously selected stay checked; first time, all checked.
            var preChecked = new HashSet<string>(
                (_dbSelectedTables != null && _dbSelectedTables.Count > 0)
                    ? _dbSelectedTables
                    : dbTables,
                StringComparer.OrdinalIgnoreCase);

            var result = ShowTableSelectionDialog(dbTables, preChecked);
            if (result != null)
            {
                _dbSelectedTables = result;
                UpdateSelectedTableCountLabel();
                Log($"DDL: {result.Count} of {dbTables.Count} DB table(s) selected for From DB compare.");
            }
        }

        /// <summary>
        /// Lightweight inline dialog: CheckedListBox + Select All/None + OK/Cancel.
        /// Returns selected table list or null on cancel.
        /// </summary>
        private List<string> ShowTableSelectionDialog(List<string> tables, HashSet<string> preChecked)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Select DB Tables to Compare";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.Size = new System.Drawing.Size(420, 520);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.TopMost = true;

                var lbl = new Label
                {
                    Text = $"Model tables ({tables.Count}). Checked = include in DB reverse engineer.",
                    Location = new System.Drawing.Point(12, 10),
                    Size = new System.Drawing.Size(380, 30)
                };
                dlg.Controls.Add(lbl);

                var clb = new CheckedListBox
                {
                    Location = new System.Drawing.Point(12, 45),
                    Size = new System.Drawing.Size(378, 380),
                    CheckOnClick = true,
                    IntegralHeight = false
                };
                foreach (var t in tables)
                    clb.Items.Add(t, preChecked.Contains(t));
                dlg.Controls.Add(clb);

                var btnAll = new Button
                {
                    Text = "Select All",
                    Location = new System.Drawing.Point(12, 432),
                    Size = new System.Drawing.Size(90, 26)
                };
                btnAll.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, true);
                };
                dlg.Controls.Add(btnAll);

                var btnNone = new Button
                {
                    Text = "Select None",
                    Location = new System.Drawing.Point(108, 432),
                    Size = new System.Drawing.Size(90, 26)
                };
                btnNone.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, false);
                };
                dlg.Controls.Add(btnNone);

                var btnOk = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new System.Drawing.Point(208, 432),
                    Size = new System.Drawing.Size(85, 26)
                };
                dlg.Controls.Add(btnOk);
                dlg.AcceptButton = btnOk;

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new System.Drawing.Point(300, 432),
                    Size = new System.Drawing.Size(85, 26)
                };
                dlg.Controls.Add(btnCancel);
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;

                var selected = new List<string>();
                foreach (var item in clb.CheckedItems) selected.Add(item.ToString());
                return selected;
            }
        }

        private int _martVersion = 0;
        private string _martLocator = "";
        private int _pendingDDLVersion = 0;
        private string _pendingDDLFeOption = "";
        private Timer _puWatcherTimer;
        private int _puWatcherInitialCount;

        /// <summary>
        /// Populate Left/Right model version combo boxes.
        /// Version is read from PU locator or erwin window title.
        /// </summary>
        private void PopulateVersionCombos()
        {
            cmbLeftModel.Items.Clear();
            cmbRightModel.Items.Clear();

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

                // Left model: Active Model
                string leftLabel = version > 1 ? $"Active Model (v{version})" : "Active Model";
                cmbLeftModel.Items.Add(leftLabel);
                cmbLeftModel.SelectedIndex = 0;
                string vTag = version > 1 ? $"v{version} " : "";
                lblOpenedModel.Text = $"Opened Model: {modelName} {vTag}(with last changes)";

                // Right model: list versions from Mart (only for Mart models)
                var versions = DdlGenerationService.GetMartVersions(modelName, (object)_currentModel, (Action<string>)Log);

                if (versions.Count > 0)
                {
                    // Newest-first ordering: highest version on top so first item is the most recent.
                    for (int i = versions.Count - 1; i >= 0; i--)
                    {
                        var v = versions[i];
                        string label = $"v{v.Version}" + (!string.IsNullOrEmpty(v.Name) ? $" ({v.Name})" : "");
                        cmbRightModel.Items.Add(label);
                    }

                    cmbRightModel.SelectedIndex = 0;
                }
                else
                {
                    cmbRightModel.Items.Add("Mart Baseline (connect-time)");
                    cmbRightModel.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Log($"PopulateVersionCombos error: {ex.Message}");
                cmbLeftModel.Items.Add("Active Model");
                cmbLeftModel.SelectedIndex = 0;
                lblOpenedModel.Text = "Opened Model: (active, with last changes)";
                cmbRightModel.Items.Add("(Mart Baseline)");
                cmbRightModel.SelectedIndex = 0;
            }
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
        /// Returns "" if the active PU is not a Mart-opened model.
        /// </summary>
        private string ParseActivePuCatalog()
        {
            try
            {
                string locator = _currentModel?.PropertyBag()?.Value("Locator")?.ToString() ?? "";
                var mm = System.Text.RegularExpressions.Regex.Match(locator, @"Mart://Mart/([^?&]+)",
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
                _lblDbValue.Text = "-";
                _lblRegistryValue.Text = "-";

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

        #region Alter Compare Tab (Phase 3.F)

        /// <summary>
        /// Launches the version-vs-active compare dialog. Baseline is always
        /// the currently active PU (dirty or clean); the target is a Mart
        /// version picked inside the dialog. See <see cref="Forms.CompareVersionsForm"/>.
        /// </summary>
        /// <summary>
        /// <summary>
        /// Tab-switch hook: only initialize the Alter Compare tab the FIRST
        /// time it's shown for a given active model, or when the model has
        /// changed. Switching back to the tab after a successful compare
        /// must NOT wipe the user's results - they need them visible to
        /// copy / save / inspect.
        /// </summary>
        private void tabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                // Defensive: log every tab transition so a future erwin AV can
                // be correlated to the tab the user was on. Earlier crashes
                // (2026-05-09, coreclr AV during Alter Compare entry) had no
                // log breadcrumbs - the addin's own Execute log ended cleanly,
                // and only the Windows WER report identified the host module.
                // Logging here gives us a definitive last-tab marker.
                var sel = tabControl.SelectedTab;
                Log($"[TAB] -> {sel?.Text ?? "(null)"} ({sel?.Name ?? "-"})");

                if (sel != tabAlterCompare) return;

                // First entry, or active model changed since last init.
                bool needsInit = !ReferenceEquals(_alterTabInitFor, _currentModel as object);
                if (needsInit)
                {
                    RefreshAlterCompareTab();
                    _alterTabInitFor = _currentModel as object;
                }
            }
            catch (Exception ex)
            {
                Log($"tabControl_SelectedIndexChanged: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ====================================================================
        // Alter Compare tab — inline UI (Phase 3.G)
        //
        // Active PU (live + dirty buffer) is the baseline; user picks a Mart
        // version to diff against; VersionCompareService runs the pipeline,
        // emits alter SQL, and the tab fills in the changes ListView + SQL
        // textbox in place. No popup dialog is involved.
        // ====================================================================

        private readonly List<Services.VersionCompareService.TargetVersion> _alterTargetVersions = new();
        private string _alterLastSql = string.Empty;
        private string _alterLastDialect = "MSSQL";

        /// <summary>
        /// Snapshot of the model reference last seen by RefreshAlterCompareTab.
        /// When the user switches tabs without changing models, the tab keeps
        /// its previous state (combo selection, ListView contents, SQL text)
        /// instead of resetting on every tab activation.
        /// </summary>
        private object _alterTabInitFor;

        /// <summary>
        /// Initialize the Alter Compare tab for the current active model:
        /// read PU metadata, populate the version combo, set status. Only
        /// touches metadata UI; does NOT clear lvAlterChanges / txtAlterSql
        /// so that switching tabs after a successful compare keeps results
        /// visible. Compare-button click is what populates results, and
        /// pre-populates a fresh state when it runs.
        /// </summary>
        private void RefreshAlterCompareTab()
        {
            try
            {
                if (!_isConnected || _currentModel == null || _scapi == null)
                {
                    lblAlterActiveInfo.Text = "Active: (no model loaded)";
                    lblAlterDialectInfo.Text = "Dialect: -";
                    cmbAlterTargetVersion.Items.Clear();
                    _alterTargetVersions.Clear();
                    btnAlterCompare.Enabled = false;
                    lblAlterCompareStatus.Text = "Open a model to begin.";
                    // Stale results from a different model would mislead the
                    // user, so clear them when there is no model anymore.
                    lvAlterChanges.Items.Clear();
                    txtAlterSql.Clear();
                    _alterLastSql = string.Empty;
                    btnSaveAlterSql.Enabled = false;
                    btnCopyAlterSql.Enabled = false;
                    return;
                }

                // Degraded mode (ConfigContext failed to resolve a Mart CONFIG
                // mapping for this model) means the active PU is non-Mart or
                // unmapped. Calling SCAPI dispatch (PropertyBag, version
                // queries, dialect probing) on such a PU triggers a NULL deref
                // deep in EM_GDM whose IDispatchInvoke unwind crashes erwin -
                // verified 2026-05-09 10:34 against the same PowerDesigner-
                // imported local file path that already crashed the host on
                // 2026-05-08. Show a plain "config required" stub instead of
                // running the SCAPI calls that would AV.
                if (!Services.ConfigContextService.Instance.IsInitialized)
                {
                    lblAlterActiveInfo.Text = "Active: (model loaded, but no CONFIG mapping)";
                    lblAlterDialectInfo.Text = "Dialect: - (config required)";
                    cmbAlterTargetVersion.Items.Clear();
                    _alterTargetVersions.Clear();
                    btnAlterCompare.Enabled = false;
                    lblAlterCompareStatus.Text =
                        "Alter Compare is disabled until a Mart-bound model with a CONFIG mapping is open.";
                    lvAlterChanges.Items.Clear();
                    txtAlterSql.Clear();
                    _alterLastSql = string.Empty;
                    btnSaveAlterSql.Enabled = false;
                    btnCopyAlterSql.Enabled = false;
                    return;
                }

                var service = new Services.VersionCompareService(_scapi, _currentModel, (Action<string>)Log);
                var dirty = service.ProbeDirty();
                var (target, major, minor) = service.ReadActiveTargetServer();
                var dialect = Services.VersionCompareService.ResolveDialect(target);
                _alterLastDialect = dialect;
                int currentVersion = service.ReadActiveVersion();
                string dirtyTag = dirty.IsDirty ? "Dirty" : "Clean";

                string dirtyHint = dirty.IsDirty
                    ? "  -  unsaved changes will NOT be in the diff (save first to capture them)"
                    : "";
                lblAlterActiveInfo.Text = $"Active: v{currentVersion} ({dirtyTag}){dirtyHint}";
                lblAlterDialectInfo.Text = string.IsNullOrEmpty(target)
                    ? $"Dialect: {dialect}"
                    : $"Dialect: {dialect}  (model target: {target} v{major}.{minor})";

                _alterTargetVersions.Clear();
                cmbAlterTargetVersion.Items.Clear();
                foreach (var row in Services.VersionCompareService.PlanTargetVersions(currentVersion, dirty.IsDirty))
                {
                    _alterTargetVersions.Add(row);
                    cmbAlterTargetVersion.Items.Add(row.Label);
                }
                if (cmbAlterTargetVersion.Items.Count > 0) cmbAlterTargetVersion.SelectedIndex = 0;

                btnAlterCompare.Enabled = cmbAlterTargetVersion.Items.Count > 0;
                lblAlterCompareStatus.Text = btnAlterCompare.Enabled
                    ? "Pick a target version and click Compare."
                    : "No earlier Mart version available to compare against.";

                // First-init for a new model: clear previous results (they
                // belonged to the old PU). Subsequent same-model refreshes
                // are guarded by tabControl_SelectedIndexChanged, so this
                // path only runs when the user's active model actually
                // changed.
                lvAlterChanges.Items.Clear();
                txtAlterSql.Clear();
                _alterLastSql = string.Empty;
                btnSaveAlterSql.Enabled = false;
                btnCopyAlterSql.Enabled = false;
            }
            catch (Exception ex)
            {
                Log($"RefreshAlterCompareTab failed: {ex.GetType().Name}: {ex.Message}");
                lblAlterCompareStatus.Text = $"Init error: {ex.Message}";
                btnAlterCompare.Enabled = false;
            }
        }

        private async void btnAlterCompare_Click(object sender, EventArgs e)
        {
            if (cmbAlterTargetVersion.SelectedIndex < 0
                || cmbAlterTargetVersion.SelectedIndex >= _alterTargetVersions.Count)
                return;
            if (!_isConnected || _currentModel == null || _scapi == null)
            {
                ErwinAddIn.ShowTopMostMessage("No active erwin model.", "Alter Compare");
                return;
            }

            int targetVersion = _alterTargetVersions[cmbAlterTargetVersion.SelectedIndex].Version;
            SetAlterBusy(true, $"Comparing against Mart v{targetVersion}...");
            lvAlterChanges.Items.Clear();
            txtAlterSql.Clear();
            _alterLastSql = string.Empty;
            btnSaveAlterSql.Enabled = false;
            btnCopyAlterSql.Enabled = false;

            try
            {
                var service = new Services.VersionCompareService(_scapi, _currentModel, (Action<string>)Log);
                var outcome = await Task.Run(() =>
                    service.CompareAsync(targetVersion, System.Threading.CancellationToken.None)).ConfigureAwait(true);

                PopulateAlterChanges(outcome);
                lblAlterCompareStatus.Text =
                    $"Done. {outcome.Result.Changes.Count} change(s), {outcome.Script.Statements.Count} statement(s) emitted for {outcome.Dialect}.";
            }
            catch (Exception ex)
            {
                Log($"AlterCompare failed: {ex.GetType().FullName}: {ex.Message}");
                Log("--- stack trace ---");
                Log(ex.ToString());
                if (ex.InnerException is not null)
                {
                    Log("--- inner exception ---");
                    Log(ex.InnerException.ToString());
                }
                lblAlterCompareStatus.Text = "Compare failed. See Debug Log for details.";
                ErwinAddIn.ShowTopMostMessage(
                    $"Compare failed:\n\n{ex.GetType().Name}: {ex.Message}\n\n(Full stack in Debug Log.)",
                    "Alter Compare");
            }
            finally
            {
                SetAlterBusy(false);
            }
        }

        private void PopulateAlterChanges(Services.CompareOutcome outcome)
        {
            lvAlterChanges.BeginUpdate();
            try
            {
                foreach (var change in outcome.Result.Changes)
                {
                    var row = new ListViewItem(new[]
                    {
                        change.GetType().Name,
                        change.Target.Class,
                        change.Target.Name,
                        DescribeAlterChangeDetail(change),
                    });
                    lvAlterChanges.Items.Add(row);
                }
            }
            finally
            {
                lvAlterChanges.EndUpdate();
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"-- ALTER SQL for {outcome.Dialect} ({outcome.Script.Statements.Count} statement(s))");
            sb.AppendLine($"-- From: Mart version selected above   To: active model (current dirty state)");
            sb.AppendLine();
            foreach (var stmt in outcome.Script.Statements)
            {
                if (!string.IsNullOrWhiteSpace(stmt.Comment)) sb.AppendLine("-- " + stmt.Comment);
                sb.AppendLine(stmt.Sql);
                if (outcome.Dialect == "MSSQL") sb.AppendLine("GO");
                sb.AppendLine();
            }
            _alterLastSql = sb.ToString();
            _alterLastDialect = outcome.Dialect;
            // Plain TextBox - syntax highlighting reverted (see Designer.cs note
            // on the txtAlterSql declaration: RichTextBox.Clear() during tab
            // refresh raised a UIA event that crashed erwin host).
            txtAlterSql.Text = _alterLastSql;
            txtAlterSql.SelectionStart = 0;
            txtAlterSql.ScrollToCaret();
            bool hasSql = !string.IsNullOrEmpty(_alterLastSql);
            btnSaveAlterSql.Enabled = hasSql;
            btnCopyAlterSql.Enabled = hasSql;
        }

        private static string DescribeAlterChangeDetail(EliteSoft.Erwin.AlterDdl.Core.Models.Change change) => change switch
        {
            EliteSoft.Erwin.AlterDdl.Core.Models.EntityRenamed er => $"from '{er.OldName}'",
            EliteSoft.Erwin.AlterDdl.Core.Models.SchemaMoved sm => $"{sm.OldSchema} -> {sm.NewSchema}",
            EliteSoft.Erwin.AlterDdl.Core.Models.AttributeAdded aa => $"in {aa.ParentEntity.Name}",
            EliteSoft.Erwin.AlterDdl.Core.Models.AttributeDropped ad => $"from {ad.ParentEntity.Name}",
            EliteSoft.Erwin.AlterDdl.Core.Models.AttributeRenamed ar => $"{ar.ParentEntity.Name}: '{ar.OldName}' -> '{ar.Target.Name}'",
            EliteSoft.Erwin.AlterDdl.Core.Models.AttributeTypeChanged at => $"{at.ParentEntity.Name}.{at.Target.Name}: {at.LeftType} -> {at.RightType}",
            EliteSoft.Erwin.AlterDdl.Core.Models.AttributeNullabilityChanged an => $"{an.ParentEntity.Name}.{an.Target.Name}: {(an.LeftNullable ? "NULL" : "NOT NULL")} -> {(an.RightNullable ? "NULL" : "NOT NULL")}",
            EliteSoft.Erwin.AlterDdl.Core.Models.AttributeDefaultChanged ad => $"{ad.ParentEntity.Name}.{ad.Target.Name}: '{ad.LeftDefault}' -> '{ad.RightDefault}'",
            EliteSoft.Erwin.AlterDdl.Core.Models.AttributeIdentityChanged ai => $"{ai.ParentEntity.Name}.{ai.Target.Name}: {ai.LeftHasIdentity} -> {ai.RightHasIdentity}",
            EliteSoft.Erwin.AlterDdl.Core.Models.KeyGroupAdded ka => $"{ka.Kind} on {ka.ParentEntity.Name}",
            EliteSoft.Erwin.AlterDdl.Core.Models.KeyGroupDropped kd => $"{kd.Kind} on {kd.ParentEntity.Name}",
            EliteSoft.Erwin.AlterDdl.Core.Models.KeyGroupRenamed kr => $"{kr.Kind} on {kr.ParentEntity.Name}: '{kr.OldName}' -> '{kr.Target.Name}'",
            EliteSoft.Erwin.AlterDdl.Core.Models.ForeignKeyRenamed fr => $"from '{fr.OldName}'",
            EliteSoft.Erwin.AlterDdl.Core.Models.TriggerRenamed tr => $"from '{tr.OldName}'",
            EliteSoft.Erwin.AlterDdl.Core.Models.SequenceRenamed sr => $"from '{sr.OldName}'",
            EliteSoft.Erwin.AlterDdl.Core.Models.ViewRenamed vr => $"from '{vr.OldName}'",
            _ => string.Empty,
        };

        private void btnCopyAlterSql_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_alterLastSql)) return;
            try
            {
                Clipboard.SetText(_alterLastSql);
                lblAlterCompareStatus.Text = $"Copied {_alterLastSql.Length:N0} chars to clipboard.";
            }
            catch (Exception ex)
            {
                Log($"Copy SQL failed: {ex.GetType().Name}: {ex.Message}");
                ErwinAddIn.ShowTopMostMessage($"Copy failed:\n\n{ex.Message}", "Copy Error");
            }
        }

        private void btnSaveAlterSql_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_alterLastSql)) return;
            using var dlg = new SaveFileDialog
            {
                Filter = "SQL script (*.sql)|*.sql|All files (*.*)|*.*",
                FileName = $"alter-{_alterLastDialect.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.sql",
                Title = "Save Alter SQL",
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, _alterLastSql,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                lblAlterCompareStatus.Text = $"Saved to {dlg.FileName}";
            }
            catch (Exception ex)
            {
                ErwinAddIn.ShowTopMostMessage(
                    $"Save failed:\n\n{ex.Message}",
                    "Save Error");
            }
        }

        private void SetAlterBusy(bool busy, string status = null)
        {
            btnAlterCompare.Enabled = !busy && cmbAlterTargetVersion.Items.Count > 0;
            cmbAlterTargetVersion.Enabled = !busy;
            bool hasSql = !busy && !string.IsNullOrEmpty(_alterLastSql);
            btnSaveAlterSql.Enabled = hasSql;
            btnCopyAlterSql.Enabled = hasSql;
            progressAlterCompare.Visible = busy;
            progressAlterCompare.MarqueeAnimationSpeed = busy ? 30 : 0;
            if (status != null) lblAlterCompareStatus.Text = status;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        #endregion
    }
}
