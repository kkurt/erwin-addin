using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Services;

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

        // Services
        private ColumnValidationService _validationService;
        private TableTypeMonitorService _tableTypeMonitorService;

        // TEMPORARY discovery — logs erwin Column Editor + child window classes/messages
        // so we can pick the right strategy for filtering the Physical Data Type dropdown.
        // Remove once that filter is implemented.
        private ColumnEditorInspector _columnEditorInspector;
        private ValidationCoordinatorService _validationCoordinatorService;
        private PropertyApplicatorService _propertyApplicatorService;
        private UdpRuntimeService _udpRuntimeService;
        private DependencySetRuntimeService _dependencySetService;

        // Metamodel Property_Type names collected by EnsureAllUdpsExist; reused
        // by UdpRuntimeService.Initialize so we don't pay for the same ~700ms
        // metamodel walk twice during a single connect cycle. Reset on every
        // connect so a fresh walk happens after model switches.
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
        }

        #endregion

        #region Form Lifecycle

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
                Application.DoEvents();

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
            Form loadingDialog = ShowLoadingDialog("Please wait...");

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
                UpdateConnectionStatus(StatusConnecting, Color.Gray);
                EnableControls(false);
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
                StopReconnectTimer();
                UpdateConnectionStatus(StatusConnected, Color.DarkGreen);
                using (AddinLogger.BeginScope("LoadExistingValues"))
                    LoadExistingValues();
                EnableControls(true);
                UpdateStatus("Connected to model.", Color.DarkGreen);

                // Skip validations for RE models (temporary models created by Reverse Engineer)
                // RE models are detected by: no Locator AND model name starts with "Model_"
                // Normal local models get validations via MODEL_PATH UDP matching
                string puLocator = "";
                try { puLocator = _currentModel.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }
                bool isReModel = string.IsNullOrEmpty(puLocator) &&
                    (_connectedModelName.StartsWith("Model_") || _connectedModelName.StartsWith("Model "));

                if (isReModel)
                {
                    Log($"Skipping validations for RE model '{_connectedModelName}'");
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
                    _globalDataLoaded = true;
                }

                // If ForceClose was triggered during init, stop further processing
                if (_allowClose || this.IsDisposed) return;

            }
            catch (Exception ex)
            {
                _isConnected = false;
                UpdateConnectionStatus(StatusDisconnected, Color.Red);
                EnableControls(false);
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
        private static void UpdateLoadingMessage(Form loadingDialog, string newMessage)
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
            if (_isConnected)
            {
                StopReconnectTimer();
                return;
            }

            try
            {
                dynamic persistenceUnits = _scapi.PersistenceUnits;
                int count = persistenceUnits.Count;
                if (count > 0)
                {
                    Log($"Model detected ({count} open). Reconnecting...");
                    StopReconnectTimer();

                    _openModels.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        dynamic model = persistenceUnits.Item(i);
                        _openModels.Add(model);
                    }

                    if (_openModels.Count > 0)
                    {
                        ConnectToModel(0);
                    }
                }
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

            // Config guard — resolve CONFIG row from the active model's mart path
            var ctx = ConfigContextService.Instance;
            ctx.OnLog -= Log;
            ctx.OnLog += Log;

            // PU.Locator is unreliable on r10.10 Mart-bound PUs (often ""),
            // so we use the shared fallback chain: direct -> PropertyBag() ->
            // PropertyBag(null,true) -> erwin main-window title.
            string locator = PuLocatorReader.Read(_currentModel, (Action<string>)Log);
            Log($"PuLocatorReader returned: '{locator}' (length={locator.Length})");

            bool ok;
            using (AddinLogger.BeginScope("ConfigContext.Initialize"))
                ok = ctx.Initialize(locator);
            if (!ok)
            {
                ErwinAddIn.ShowTopMostMessage(
                    ctx.LastError ?? "No CONFIG mapped to this model's mart path.\nPlease run Admin panel first.",
                    "Configuration Error");
                Log("Config not resolved -- closing extension.");
                this.ForceClose();
                return;
            }
            Log($"Config: {ctx.ActiveConfigName} (ID={ctx.ActiveConfigId}), corporate='{ctx.CorporateName ?? "(none)"}', mart='{ctx.MartPath}'");

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

            using (AddinLogger.BeginScope("EnsureAllUdpsExist"))
                EnsureAllUdpsExist();
            using (AddinLogger.BeginScope("SetModelPathValue"))
                SetModelPathValue();

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

            _udpRuntimeService = new UdpRuntimeService(_session, _scapi, _currentModel);
            _udpRuntimeService.OnLog += Log;
            _udpRuntimeService.SetDependencySetService(_dependencySetService);
            using (AddinLogger.BeginScope("UdpRuntimeService.Initialize"))
            {
                if (_udpRuntimeService.Initialize(_cachedPropertyTypeNames))
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
                _validationCoordinatorService.StartMonitoring();

            // Column Editor lifecycle hook (kept disabled).
            // 2026-05-06: this Inspector is a discovery / diagnostic tool that
            // only logs WinEvents and dumps the column editor window tree. It
            // has NO validation logic - no glossary lookup, no ValidationCoord
            // calls, no per-keystroke hook. Re-enabling adds verbose log noise
            // (hundreds of lines per editor session) without delivering live
            // validation. Live popups are driven by MonitorTimer's periodic
            // scan; for finer latency, raise MaxEntitiesPerTick or wire a real
            // EN_CHANGE subclass on the editor's text fields (separate task).
            // if (_columnEditorInspector == null)
            // {
            //     _columnEditorInspector = new ColumnEditorInspector();
            //     _columnEditorInspector.OnLog += Log;
            //     _columnEditorInspector.Start();
            // }

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


        #region General Tab

        // Labels to update after corporate initialization
        private Label _lblCorporateValue;
        private Label _lblDbValue;
        private Label _lblRegistryValue;

        // Hidden tabs registry — Ctrl+Shift+RightClick on a tab header hides it,
        // Ctrl+Shift+LeftClick on the copyright label on the General tab restores all.
        // tabGeneral itself is never hidden because the restore mechanism lives on it.
        private readonly List<TabPage> _hiddenTabs = new List<TabPage>();

        private void InitializeGeneralTab()
        {
            var font = new Font("Segoe UI", 9.5f);
            var fontBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var fontTitle = new Font("Segoe UI", 14f, FontStyle.Bold);
            var clrAccent = Color.FromArgb(0, 120, 212);
            var clrCardBg = Color.White;
            var clrLabelDim = Color.FromArgb(100, 100, 100);

            // --- Header ---
            var lblTitle = new Label
            {
                Text = "Elite Soft Erwin AddIn",
                Font = fontTitle,
                ForeColor = clrAccent,
                AutoSize = true,
                Location = new Point(24, 20)
            };
            tabGeneral.Controls.Add(lblTitle);

            var lblCopyright = new Label
            {
                Text = "\u00A9 2026 Elite Soft. All rights reserved.",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(160, 160, 160),
                AutoSize = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point(24, 430)
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

                    if (tabDebug != null && !tabControl.TabPages.Contains(tabDebug))
                    {
                        tabControl.TabPages.Add(tabDebug);
                        tabControl.SelectedTab = tabDebug;
                        restored++;
                    }

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

            // --- Info Card ---
            var card = CreateInfoCard("", 24, 60, 812, 106, clrCardBg);
            AddCardRow(card, "Corporate:", "", fontBold, font, 0, out _, out _lblCorporateValue);
            AddCardRow(card, "Database:", "", fontBold, font, 1, out _, out _lblDbValue);
            AddCardRow(card, "Registry:", "", fontBold, font, 2, out _, out _lblRegistryValue);
            tabGeneral.Controls.Add(card);

            // Initial state
            _lblCorporateValue.Text = "(not loaded)";
            _lblDbValue.Text = "(not loaded)";
            _lblRegistryValue.Text = "(not loaded)";

#if PACKAGED
            // Packaged distribution: hide Debug Log tab from end users.
            // Revealed at runtime via Ctrl+Shift+LeftClick on the copyright label above.
            if (tabDebug != null && tabControl.TabPages.Contains(tabDebug))
                tabControl.TabPages.Remove(tabDebug);
#endif
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

        private void AddCardRow(Panel card, string label, string value, Font labelFont, Font valueFont, int row, out Label lblLabel, out Label lblValue)
        {
            int y = 14 + row * 26;

            lblLabel = new Label
            {
                Text = label,
                Font = labelFont,
                ForeColor = Color.FromArgb(80, 80, 80),
                AutoSize = true,
                Location = new Point(16, y)
            };
            card.Controls.Add(lblLabel);

            lblValue = new Label
            {
                Text = value,
                Font = valueFont,
                ForeColor = Color.FromArgb(40, 40, 40),
                AutoSize = true,
                Location = new Point(120, y)
            };
            card.Controls.Add(lblValue);
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
                    _lblCorporateValue.Text = string.IsNullOrEmpty(ctx.CorporateName)
                        ? $"Config: {ctx.ActiveConfigName}"
                        : $"{ctx.CorporateName} / {ctx.ActiveConfigName}";
                }

                var config = DatabaseService.Instance.GetConfig();
                if (config != null && config.IsConfigured)
                {
                    _lblDbValue.Text = $"{config.Host}/{config.Database} ({DatabaseService.Instance.GetDbType()})";
                }

                var bootstrapService = new RegistryBootstrapService();
                _lblRegistryValue.Text = bootstrapService.GetConfigFilePath().StartsWith("HKLM") ? "Machine" : "User";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateGeneralTab error: {ex.Message}");
            }
        }

        #endregion

        private void InitializeValidationUI()
        {
            listValidationResults.Columns.Add("Type", 70);
            listValidationResults.Columns.Add("Object Name", 220);
            listValidationResults.Columns.Add("Rule", 110);
            listValidationResults.Columns.Add("", 36);     // Status icon column (narrow)
            listValidationResults.Columns.Add("Message", 380);

            btnValidateAll.Enabled = false;
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

                // Apply "Errors Only" checkbox filter
                if (chkErrorsOnly.Checked)
                {
                    var toRemove = new List<ListViewItem>();
                    foreach (ListViewItem item in listValidationResults.Items)
                    {
                        if (item.SubItems[3].Text == "\u2713") toRemove.Add(item);
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
            var item = new ListViewItem(objectType);
            item.SubItems.Add(objectName);
            item.SubItems.Add(rule);
            item.SubItems.Add(isValid ? "\u2713" : "\u2717");
            item.SubItems.Add(isValid ? "" : message);
            item.ForeColor = isValid ? Color.DarkGreen : Color.Red;
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

        private void BtnTestConnection_Click(object sender, EventArgs e)
        {
            try
            {
                lblGlossaryStatus.Text = "Testing glossary connection...";
                lblGlossaryStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                if (!DatabaseService.Instance.IsConfigured)
                {
                    lblGlossaryStatus.Text = "Repository database not configured. Please configure in ErwinAdmin.";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    return;
                }

                // Test glossary loading via DG_TABLE_MAPPING
                var glossary = GlossaryService.Instance;
                glossary.LoadGlossary();

                if (glossary.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Glossary connection successful! ({glossary.Count} entries)";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;
                }
                else
                {
                    lblGlossaryStatus.Text = $"Glossary: {glossary.LastError}";
                    lblGlossaryStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                lblGlossaryStatus.Text = $"Connection failed: {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.Red;
            }
        }

        private void BtnReloadGlossary_Click(object sender, EventArgs e)
        {
            try
            {
                lblGlossaryStatus.Text = "Reconnecting to glossary database...";
                lblGlossaryStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                DatabaseService.Instance.ClearCache();

                if (!DatabaseService.Instance.IsConfigured)
                {
                    lblGlossaryStatus.Text = "Repository database not configured. Please configure in ErwinAdmin.";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    ClearGlossaryConnectionLabels();
                    return;
                }

                lblGlossaryStatus.Text = "Loading glossary...";
                Application.DoEvents();

                GlossaryService.Instance.Reload();

                if (GlossaryService.Instance.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Glossary loaded: {GlossaryService.Instance.Count} entries";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;

                    _lastGlossaryRefreshTime = DateTime.Now;
                    UpdateLastRefreshLabel();
                    UpdateValidationStatus();
                }
                else
                {
                    lblGlossaryStatus.Text = $"Failed to load glossary: {GlossaryService.Instance.LastError}";
                    lblGlossaryStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                lblGlossaryStatus.Text = $"Error: {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.Red;
            }
        }

        private void LoadGlossary()
        {
            try
            {
                if (!DatabaseService.Instance.IsConfigured)
                {
                    lblGlossaryStatus.Text = "Repository database not configured. Please configure in ErwinAdmin.";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    ClearGlossaryConnectionLabels();
                    return;
                }

                var glossary = GlossaryService.Instance;
                if (!glossary.IsLoaded)
                {
                    glossary.LoadGlossary();
                }

                UpdateGlossaryConnectionLabels();

                if (glossary.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Glossary loaded: {glossary.Count} entries";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;
                }
                else
                {
                    lblGlossaryStatus.Text = $"Glossary not loaded: {glossary.LastError}";
                    lblGlossaryStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                Log($"LoadGlossary error: {ex.Message}");
                lblGlossaryStatus.Text = $"Error: {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.Red;
            }
        }

        private void UpdateGlossaryConnectionLabels()
        {
            if (GlossaryService.Instance.IsLoaded)
            {
                lblHostValue.Text = "Configured";
                lblPortValue.Text = "-";
                lblDatabaseValue.Text = $"{GlossaryService.Instance.Count} entries";
            }
            else
            {
                ClearGlossaryConnectionLabels();
            }
        }

        private void ClearGlossaryConnectionLabels()
        {
            lblHostValue.Text = "(not loaded)";
            lblPortValue.Text = "-";
            lblDatabaseValue.Text = "(not loaded)";
        }

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
                }
            }
            catch (Exception ex)
            {
                Log($"LoadPredefinedColumns error: {ex.Message}");
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

        /// <summary>
        /// Ensures all UDPs exist using a SINGLE metamodel session.
        /// Creates TABLE_TYPE (Entity, List) and OWNER/KVKK/PCIDSS/CLASSIFICATION (Attribute, Text).
        /// </summary>
        private void EnsureAllUdpsExist()
        {
            dynamic metamodelSession = null;
            try
            {
                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // SCD_SL_M1 = Metamodel level

                dynamic mmObjects = metamodelSession.ModelObjects;
                dynamic mmRoot = mmObjects.Root;

                // Collect all existing Property_Type names in one pass.
                // Stored on a field so UdpRuntimeService can reuse it instead of
                // walking the same ~1500-entry metamodel collection again.
                var existingUdps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        try { existingUdps.Add(pt.Name ?? ""); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Metamodel Property_Type enumeration failed: {ex.Message}");
                }

                _cachedPropertyTypeNames = existingUdps;
                Log($"Found {existingUdps.Count} existing Property_Type entries");

                // TABLE_TYPE UDP is now managed by UdpRuntimeService (MC_UDP_DEFINITION)

                // --- MODEL_PATH UDP (hidden, read-only — stores repository path) ---
                // Determine model path BEFORE creating UDP (to set as default value in metamodel)
                string modelPathForUdp = ReadModelPath();

                if (!existingUdps.Contains("Model.Physical.MODEL_PATH"))
                {
                    int transId = metamodelSession.BeginNamedTransaction("CreateModelPathUDP");
                    try
                    {
                        dynamic udpType = mmObjects.Add("Property_Type");
                        udpType.Properties("Name").Value = "Model.Physical.MODEL_PATH";
                        TrySetProperty(udpType, "tag_Udp_Owner_Type", "Model");
                        TrySetProperty(udpType, "tag_Is_Physical", true);
                        TrySetProperty(udpType, "tag_Is_Logical", false);
                        TrySetProperty(udpType, "tag_Udp_Data_Type", 1); // Text type
                        // Set model path as default value — ensures it's embedded even before model session can see the UDP
                        if (!string.IsNullOrEmpty(modelPathForUdp))
                            TrySetProperty(udpType, "tag_Udp_Default_Value", modelPathForUdp);
                        TrySetProperty(udpType, "tag_Order", "999");
                        TrySetProperty(udpType, "tag_Is_Locally_Defined", true);
                        metamodelSession.CommitTransaction(transId);
                        // Keep the cached set in sync with the metamodel so the
                        // downstream UdpRuntimeService consumer sees the new entry.
                        _cachedPropertyTypeNames?.Add("Model.Physical.MODEL_PATH");
                        Log($"MODEL_PATH UDP created (default='{modelPathForUdp ?? ""}')");
                    }
                    catch (Exception ex)
                    {
                        try { metamodelSession.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"MODEL_PATH rollback error: {rbEx.Message}"); }
                        if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                            Log("MODEL_PATH UDP already exists (unique constraint)");
                        else
                            Log($"Error creating MODEL_PATH UDP: {ex.Message}");
                    }
                }

                Log("All UDPs ensured");
            }
            catch (Exception ex)
            {
                Log($"EnsureAllUdpsExist error: {ex.Message}");
            }
            finally
            {
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); } catch { }
                }
            }
        }

        /// <summary>
        /// Set MODEL_PATH UDP value from the model's repository or file path.
        /// Only writes if the current value is empty (first time).
        /// </summary>
        private void SetModelPathValue()
        {
            // Use a fresh session to see newly created UDP (main session may have stale schema)
            dynamic freshSession = null;
            try
            {
                // Determine path first (before opening new session)
                string modelPath = ReadModelPath();

                freshSession = _scapi.Sessions.Add();
                freshSession.Open(_currentModel);

                dynamic modelObjects = freshSession.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null)
                {
                    Log("MODEL_PATH: Fresh session root is null");
                    return;
                }

                // Read current MODEL_PATH value
                string currentModelPath = "";
                try
                {
                    currentModelPath = root.Properties("Model.Physical.MODEL_PATH").Value?.ToString() ?? "";
                }
                catch (Exception ex)
                {
                    Log($"MODEL_PATH UDP not readable: {ex.Message}");
                    return;
                }

                if (!string.IsNullOrEmpty(currentModelPath))
                {
                    Log($"MODEL_PATH already set: '{currentModelPath}'");
                    return;
                }

                if (string.IsNullOrEmpty(modelPath))
                {
                    Log("MODEL_PATH: Could not determine model path");
                    return;
                }

                // Write to UDP
                int transId = freshSession.BeginNamedTransaction("SetModelPath");
                try
                {
                    root.Properties("Model.Physical.MODEL_PATH").Value = modelPath;
                    freshSession.CommitTransaction(transId);
                    Log($"MODEL_PATH set to: '{modelPath}'");
                }
                catch (Exception ex)
                {
                    try { freshSession.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"MODEL_PATH rollback error: {rbEx.Message}"); }
                    Log($"MODEL_PATH write failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"SetModelPathValue error: {ex.Message}");
            }
            finally
            {
                if (freshSession != null)
                {
                    try { freshSession.Close(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SetModelPath: Session close error: {ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// Read model full path from PersistenceUnit (repository) or file system.
        /// Probes multiple COM properties to find the complete path including folder structure.
        /// </summary>
        private string ReadModelPath()
        {
            try
            {
                // Phase 1: Probe PersistenceUnit COM object for full path
                if (_currentModel != null)
                {
                    // Try all possible PersistenceUnit properties that might contain full path
                    string[] puProps = { "FullName", "Path", "Location", "ConnectionString",
                                         "Source", "FileName", "FilePath", "URL", "URI" };
                    foreach (var prop in puProps)
                    {
                        try
                        {
                            dynamic val = null;
                            // Try as direct property first
                            try { val = ((dynamic)_currentModel).GetType().GetProperty(prop)?.GetValue(_currentModel); } catch { }
                            // Try as COM late-bound property
                            if (val == null) try { val = ((dynamic)_currentModel).GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, _currentModel, null); } catch { }

                            string strVal = val?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(strVal) && strVal != _currentModel.Name?.ToString())
                            {
                                Log($"MODEL_PATH: PersistenceUnit.{prop} = '{strVal}'");
                                return strVal;
                            }
                        }
                        catch { }
                    }

                    // Log PersistenceUnit.Name for debugging
                    try
                    {
                        string puName = _currentModel.Name?.ToString() ?? "";
                        Log($"MODEL_PATH: PersistenceUnit.Name = '{puName}' (no full path found on PU)");
                    }
                    catch { }
                }

                // Phase 2: Try session/root properties for Mart path
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                // Try Mart-specific properties on root
                string[] rootProps = { "Long_Id", "Source_File_Name", "File_Name", "File_Path",
                                        "Model_File_Name", "Mart_Model_Path", "Repository_Path" };
                foreach (var propName in rootProps)
                {
                    try
                    {
                        string val = root.Properties(propName).Value?.ToString();
                        if (!string.IsNullOrEmpty(val) && !val.StartsWith("%"))
                        {
                            Log($"MODEL_PATH: root.{propName} = '{val}'");
                            // Skip Long_Id as path (it's a GUID), just log it
                            if (propName == "Long_Id") continue;
                            return val;
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MODEL_PATH: root.{propName} error: {ex.Message}"); }
                }

                // Phase 3: Try to get Mart folder path from SCAPI session
                try
                {
                    // Some erwin versions expose path on the session itself
                    string[] sessionProps = { "ModelPath", "FileName", "Source" };
                    foreach (var prop in sessionProps)
                    {
                        try
                        {
                            string val = _session.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, (object)_session, null)?.ToString();
                            if (!string.IsNullOrEmpty(val))
                            {
                                Log($"MODEL_PATH: session.{prop} = '{val}'");
                                return val;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Phase 4: Fallback — use model root Name
                try
                {
                    string modelName = root.Properties("Name").Value?.ToString();
                    if (!string.IsNullOrEmpty(modelName))
                    {
                        Log($"MODEL_PATH: Fallback to root.Name = '{modelName}'");
                        return modelName;
                    }
                }
                catch (Exception ex) { Log($"MODEL_PATH: Name fallback error: {ex.Message}"); }

                return null;
            }
            catch (Exception ex) { Log($"ReadModelPath error: {ex.Message}"); return null; }
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
                }
            }
            catch (Exception ex)
            {
                Log($"LoadDomainDefs error: {ex.Message}");
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
                }
                else
                {
                    Log($"NAMING_STANDARD not loaded: {service.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log($"LoadNamingStandards error: {ex.Message}");
            }
        }

        private void EnsureDomainParentUdpExists()
        {
            try
            {
                var domainDefService = DomainDefService.Instance;
                if (!domainDefService.IsLoaded || domainDefService.Count == 0)
                {
                    Log("DOMAIN_DEF service not loaded - skipping Domain_Parent UDP creation");
                    return;
                }

                if (CheckDomainParentUdpExists())
                {
                    Log("Domain_Parent UDP already exists - skipping creation");
                    return;
                }

                Log("Domain_Parent UDP not found - creating...");
                if (CreateDomainParentUdp(domainDefService.GetNamesAsCommaSeparated()))
                {
                    Log("Domain_Parent UDP created successfully!");
                }
                else
                {
                    Log("Failed to create Domain_Parent UDP (may already exist)");
                }
            }
            catch (Exception ex)
            {
                Log($"EnsureDomainParentUdpExists error: {ex.Message}");
            }
        }

        private bool CheckDomainParentUdpExists()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                // Method 1: Check Property_Type objects
                try
                {
                    dynamic propertyTypes = modelObjects.Collect(root, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        string ptName = "";
                        try { ptName = pt.Name ?? ""; } catch { continue; }

                        if (ptName.Equals("Attribute.Physical.Domain_Parent", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Property_Type enumeration failed: {ex.Message}");
                }

                // Method 2: Try to access UDP on an attribute
                try
                {
                    dynamic entities = modelObjects.Collect(root, "Entity");
                    foreach (dynamic entity in entities)
                    {
                        if (entity == null) continue;

                        dynamic attrs = null;
                        try { attrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                        if (attrs == null) continue;

                        foreach (dynamic attr in attrs)
                        {
                            if (attr == null) continue;
                            try
                            {
                                var udpValue = attr.Properties("Attribute.Physical.Domain_Parent");
                                if (udpValue != null) return true;
                            }
                            catch { }
                            break;
                        }
                        break;
                    }
                }
                catch { }

                return false;
            }
            catch (Exception ex)
            {
                Log($"CheckDomainParentUdpExists error: {ex.Message}");
                return false;
            }
        }

        private bool CreateDomainParentUdp(string listValues)
        {
            dynamic metamodelSession = null;
            try
            {
                Log($"Creating Domain_Parent UDP with values: {listValues}");

                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // SCD_SL_M1 = Metamodel level

                int transId = metamodelSession.BeginNamedTransaction("CreateDomainParentUDP");

                try
                {
                    dynamic mmObjects = metamodelSession.ModelObjects;
                    dynamic udpType = mmObjects.Add("Property_Type");

                    udpType.Properties("Name").Value = "Attribute.Physical.Domain_Parent";

                    TrySetProperty(udpType, "tag_Udp_Owner_Type", "Attribute");
                    TrySetProperty(udpType, "tag_Is_Physical", true);
                    TrySetProperty(udpType, "tag_Is_Logical", false);
                    TrySetProperty(udpType, "tag_Udp_Data_Type", 6); // List type
                    TrySetProperty(udpType, "tag_Udp_Values_List", listValues);

                    string defaultValue = listValues.Split(',').FirstOrDefault()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(defaultValue))
                    {
                        TrySetProperty(udpType, "tag_Udp_Default_Value", defaultValue);
                    }

                    TrySetProperty(udpType, "tag_Order", "1");
                    TrySetProperty(udpType, "tag_Is_Locally_Defined", true);

                    metamodelSession.CommitTransaction(transId);
                    Log("Domain_Parent UDP transaction committed");
                    return true;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                    {
                        Log("Domain_Parent UDP already exists (detected via unique constraint)");
                        try { metamodelSession.RollbackTransaction(transId); } catch { }
                        return true;
                    }

                    Log($"Error creating Domain_Parent UDP: {ex.Message}");
                    try { metamodelSession.RollbackTransaction(transId); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                {
                    Log("Domain_Parent UDP already exists (detected via unique constraint)");
                    return true;
                }

                Log($"Metamodel session error: {ex.Message}");
                return false;
            }
            finally
            {
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); } catch { }
                }
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
                    MessageBox.Show("Please select a table first.", "No Table Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!chkArchiveTable.Checked && !chkIsolatedTable.Checked)
                {
                    MessageBox.Show("Please select at least one option (Archive or Isolated).", "No Option Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                        MessageBox.Show($"Table '{newTableName}' already exists.", "Table Exists", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        #region Configuration Tab

        private void LoadExistingValues()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic rootObj = GetRootObject(modelObjects);

                if (rootObj != null)
                {
                    try
                    {
                        string modelName = rootObj.Properties("Name").Value?.ToString();
                        if (!string.IsNullOrEmpty(modelName))
                        {
                            txtName.Text = modelName;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"LoadExistingValues error: {ex.Message}");
            }
        }

        private void OnConfigChanged(object sender, EventArgs e)
        {
            string dbName = txtDatabaseName.Text.Trim();
            string schemaName = txtSchemaName.Text.Trim();

            if (!string.IsNullOrEmpty(dbName) && !string.IsNullOrEmpty(schemaName))
            {
                txtName.Text = $"{dbName}.{schemaName}";
            }
            else if (!string.IsNullOrEmpty(dbName))
            {
                txtName.Text = dbName;
            }
            else if (!string.IsNullOrEmpty(schemaName))
            {
                txtName.Text = schemaName;
            }
            else
            {
                txtName.Text = "";
            }
        }

        private void BtnApply_Click(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("Not connected to a model.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtDatabaseName.Text))
            {
                MessageBox.Show("Database Name cannot be empty.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtDatabaseName.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(txtSchemaName.Text))
            {
                MessageBox.Show("Schema Name cannot be empty.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtSchemaName.Focus();
                return;
            }

            try
            {
                UpdateStatus("Saving...", Color.DarkBlue);
                Application.DoEvents();

                int transId = _session.BeginNamedTransaction("SaveConfig");

                try
                {
                    dynamic modelObjects = _session.ModelObjects;
                    dynamic rootObj = GetRootObject(modelObjects);

                    bool nameSaved = false;

                    if (rootObj != null && !string.IsNullOrWhiteSpace(txtName.Text))
                    {
                        try
                        {
                            rootObj.Properties("Name").Value = txtName.Text.Trim();
                            nameSaved = true;
                        }
                        catch { }
                    }

                    _session.CommitTransaction(transId);

                    bool modelSaved = false;
                    try
                    {
                        _currentModel.Save();
                        modelSaved = true;
                    }
                    catch { }

                    if (nameSaved && modelSaved)
                    {
                        UpdateStatus("Saved!", Color.DarkGreen);
                        MessageBox.Show(
                            $"Configuration saved successfully!\n\nDatabase: {txtDatabaseName.Text}\nSchema: {txtSchemaName.Text}\nName: {txtName.Text}",
                            "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        UpdateStatus("Warning!", Color.Orange);
                        string msg = "Values could not be saved:\n";
                        if (!nameSaved) msg += "- Name property could not be saved\n";
                        if (!modelSaved) msg += "- Model could not be saved\n";
                        MessageBox.Show(msg, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("Error!", Color.Red);
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region Approval / Review

        private async void BtnAlterWizardProd_Click(object sender, EventArgs e)
        {
            if (!_isConnected || _currentModel == null)
            {
                ErwinAddIn.ShowTopMostMessage("No model connected.", "Generate DDL");
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
                        script = await System.Threading.Tasks.Task.Run(() =>
                            Services.NativeBridgeService.GenerateAlterDdl(log));
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

        private void ApplySqlHighlighting(string sql)
        {
            rtbDDLOutput.SuspendLayout();
            rtbDDLOutput.Clear();
            // Pad with trailing blank lines so the last real line isn't
            // clipped at the bottom of the RichTextBox viewport when the
            // user scrolls all the way down (common RTB rendering issue).
            rtbDDLOutput.Text = sql + "\n\n\n";

            // Set default color
            rtbDDLOutput.SelectAll();
            rtbDDLOutput.SelectionColor = Color.FromArgb(220, 220, 220);

            // IMPORTANT: Use RichTextBox's own text for regex matching
            // RichTextBox converts \r\n to \n internally, so indices differ from original string
            string rtbText = rtbDDLOutput.Text;

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
            HighlightRegex(rtbText, @"\b(CREATE|ALTER|DROP|TABLE|ADD|COLUMN|CONSTRAINT|PRIMARY|KEY|FOREIGN|REFERENCES|NOT|NULL|DEFAULT|IDENTITY|CLUSTERED|NONCLUSTERED|INDEX|UNIQUE|ON|DELETE|UPDATE|CASCADE|SET|CHECK|WITH|ASC|DESC|BEGIN|END|DECLARE|IF|EXISTS|SELECT|FROM|WHERE|AND|OR|RETURN|GOTO|TRIGGER|FOR|INSERT|AS|RAISERROR|ROLLBACK|TRANSACTION|INTO|ACTION)\b", clrKeyword);

            // 2. Data types (teal)
            HighlightRegex(rtbText, @"\b(int|bigint|smallint|tinyint|bit|varchar|nvarchar|char|nchar|text|ntext|datetime|smalldatetime|date|time|timestamp|decimal|numeric|float|real|money|smallmoney|varbinary|binary|image|uniqueidentifier|VARCHAR2|NUMBER|CLOB|BLOB|COLLATE)\b", clrType);

            // 3. Numbers (light green)
            HighlightRegex(rtbText, @"(?<![a-zA-Z_])\d+(?![a-zA-Z_])", clrNumber);

            // 4. GO (purple)
            HighlightRegex(rtbText, @"(?m)^go$", clrGo);

            // 5. String literals (orange)
            HighlightRegex(rtbText, @"'[^']*'", clrString);

            // 6. Comments (green) - overrides keywords inside comments
            HighlightRegex(rtbText, @"--[^\n]*", clrComment);

            // 7. Diff markers (override comment color)
            HighlightRegex(rtbText, @"-- NEW:.*", clrDiffNew);
            HighlightRegex(rtbText, @"-- DROPPED:.*", clrDiffDrop);
            HighlightRegex(rtbText, @"-- CHANGED:.*", clrDiffChange);
            HighlightRegex(rtbText, @"-- =+.*=+", clrSection);
            HighlightRegex(rtbText, @"-- Summary:.*", clrSection);
            HighlightRegex(rtbText, @"-- WARNING:.*", clrDiffDrop);

            rtbDDLOutput.SelectionStart = 0;
            rtbDDLOutput.SelectionLength = 0;
            rtbDDLOutput.ResumeLayout();
        }

        private void HighlightRegex(string rtbText, string pattern, Color color)
        {
            try
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(rtbText, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.Multiline))
                {
                    rtbDDLOutput.Select(m.Index, m.Length);
                    rtbDDLOutput.SelectionColor = color;
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

        #region Debug Log

        private static readonly string _addinLogPath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erwin-addin-debug.log");

        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string line = $"[{timestamp}] {message}\r\n";

            // Always tee to file - even when the form is disposed or the
            // call lands off-thread - so cleanup output survives a window
            // close and can be inspected post-hoc.
            try { System.IO.File.AppendAllText(_addinLogPath, line); } catch { }

            if (IsDisposed || txtDebugLog == null || txtDebugLog.IsDisposed) return;

            if (InvokeRequired)
            {
                try { Invoke(new Action(() => Log(message))); } catch { }
                return;
            }

            _fullLogText += line;

            // Only append if no search filter is active
            string filter = txtLogSearch?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(filter) || line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                txtDebugLog.AppendText(line);
                txtDebugLog.SelectionStart = txtDebugLog.Text.Length;
                txtDebugLog.ScrollToCaret();
            }
        }

        private string _fullLogText = "";


        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            _fullLogText = "";
            txtDebugLog.Clear();
        }

        private void BtnCopyLog_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(txtDebugLog.Text))
            {
                Clipboard.SetText(txtDebugLog.Text);
                UpdateStatus("Log copied to clipboard!", Color.DarkGreen);
            }
        }

        private void TxtLogSearch_TextChanged(object sender, EventArgs e)
        {
            string filter = txtLogSearch.Text.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                txtDebugLog.Text = _fullLogText;
            }
            else
            {
                var lines = _fullLogText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                var filtered = lines.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                txtDebugLog.Text = string.Join("\r\n", filtered);
            }
        }

        /// <summary>
        /// <summary>
        /// D1-spike: snapshots CERwinFEData.AS / .MS and ELC2 gbl_pxActionSummary
        /// right now. Used to observe what state erwin's CC engine populates
        /// during the manual Complete Compare flow.
        /// </summary>
        private void BtnDumpCCState_Click(object sender, EventArgs e)
        {
            btnDumpCCState.Enabled = false;
            try
            {
                Log("");
                Log("=== Dump CC state ===");
                string report = Services.NativeBridgeService.DumpCCState();
                foreach (var line in report.Split('\n'))
                    Log(line.TrimEnd());
            }
            finally { btnDumpCCState.Enabled = true; }
        }

        /// <summary>
        /// D1-spike: after user does manual CC + Apply-to-Right, this calls
        /// ELA::OnFE(ms, false, 0) directly. OnFE internally computes AS via
        /// GetTrasactionSummary, writes gbl_pxActionSummary, and opens the
        /// Alter Script wizard. Our FEW-CTOR hook + hide + InvokePreview
        /// chain then captures the DDL and closes the wizard.
        /// </summary>
        /// <summary>
        /// D3-spike: try to open Mart v1 as a 2nd PU while v3 is active. Tests
        /// whether SCAPI.PersistenceUnits.Add works despite 'Mart UI is active'
        /// restriction. If successful, logs PU name and we have a starting
        /// point for full Mart-Mart automation.
        /// </summary>
        /// <summary>
        /// D4-spike: call ShowERwinCCWiz(ms1=v3, ms2=v1, true, true) and see if
        /// erwin runs CC + Apply silently. If so, Mart-Mart becomes fully
        /// programmatic. Otherwise the CC wizard will open as usual and we
        /// have to go with UI automation for the last mile.
        ///
        /// Prereq: user must have opened v1 via Mart -> Open already, so our
        /// EDR hook captured v1's modelSet.
        /// </summary>
        /// <summary>
        /// Toggle stack-trace logging on EDR RegsiterStartTransactionId.
        /// Turn ON just before pressing Apply-to-Right so we can see which
        /// ELC2 internal function triggers the transaction recording.
        /// </summary>
        /// <summary>
        /// Phase 1 discovery: programmatically drive CC wizard up to Resolve
        /// Differences, dump its UIA tree, then cancel. Target: find the
        /// 'Copy to Right' arrow's AutomationId/Name so we can invoke it in
        /// Phase 2 without pixel math.
        /// </summary>
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

        private void BtnToggleEdrST_Click(object sender, EventArgs e)
        {
            _edrStOn = !_edrStOn;
            Services.NativeBridgeService.SetEdrStackTrace(_edrStOn);
            btnToggleEdrST.Text = _edrStOn ? "EDR stack-trace: ON" : "Toggle EDR stack-trace";
            btnToggleEdrST.BackColor = _edrStOn
                ? System.Drawing.Color.FromArgb(255, 210, 140)
                : System.Drawing.Color.FromArgb(245, 245, 200);
            Log($"");
            Log($"EDR stack-trace mode: {(_edrStOn ? "ON" : "OFF")}");
            if (_edrStOn)
                Log("  Now: do CC + click Apply-to-Right in Resolve Differences.");
        }

        /// <summary>
        /// Diagnostic toggle: installs a bridge-level WinEvent hook that
        /// LOGS every #32770 / Afx window creation in erwin's process to
        /// the bridge log under [MONITOR] tags. Used to map dialog
        /// sequences during manual reverse-engineering of new wizard
        /// flows (e.g. "From DB" Generate DDL discovery). Off by default.
        /// </summary>
        private void BtnToggleMonitor_Click(object sender, EventArgs e)
        {
            _monitorOn = !_monitorOn;
            Action<string> log = msg => Log(msg);
            if (_monitorOn)
            {
                int rc = Services.NativeBridgeService.MonitorHookInstall(log);
                Log($"");
                Log($"[MONITOR] hook install rc={rc}");
                Log("  Logging every #32770/Afx CREATE+RENAME in erwin process.");
                Log("  See bridge log (%TEMP%\\erwin-native-bridge.log) under [MONITOR] tag.");
                Log("  Now: do your manual flow (Review -> From DB -> ...). Toggle off when done.");
                btnToggleMonitor.Text = "Dialog Monitor: ON";
                btnToggleMonitor.BackColor = System.Drawing.Color.FromArgb(255, 210, 140);
            }
            else
            {
                int seen = Services.NativeBridgeService.MonitorHookUninstall(log);
                Log($"");
                Log($"[MONITOR] hook uninstalled. {seen} CREATE event(s) logged during session.");
                btnToggleMonitor.Text = "Toggle Dialog Monitor";
                btnToggleMonitor.BackColor = System.Drawing.Color.FromArgb(245, 245, 200);
            }
        }

        /// <summary>
        /// Probe (Phase 1 of From DB pipeline): runs SCAPI silent RE on the
        /// configured DB, captures the freshly-created reverse-engineered
        /// ModelSet pointer via the bridge's MS-TRACK, then invokes
        /// CWizInterface::ShowERwinCCWiz(activeMs, reMs, b1=0, b2=1) directly
        /// through the bridge. Goal: bypass the 8-step RE wizard navigation
        /// entirely and skip straight to "Resolve Differences" - same RD
        /// dialog the manual flow reaches at 22:03:34. If RD opens with the
        /// expected Apply-to-Right arrow, the rest of the pipeline (mouse
        /// click, OnFE+GA, cleanup) is reusable from the proven Mart-Mart
        /// implementation.
        /// </summary>
        private async void BtnFromDbProbe_Click(object sender, EventArgs e)
        {
            btnFromDbProbe.Enabled = false;
            try
            {
                Log("");
                Log("=== From DB Probe: Silent RE + CallShowERwinCCWiz ===");

                if (!_isConnected || _currentModel == null)
                {
                    Log("[PROBE] No model connected.");
                    return;
                }
                bool dbConfigured =
                    !string.IsNullOrWhiteSpace(_dbHost)
                    && !string.IsNullOrWhiteSpace(_dbName)
                    && _dbTypeCode != 0;
                if (!dbConfigured)
                {
                    Log("[PROBE] DB not configured. Click Configure on From DB first.");
                    return;
                }

                Action<string> log = msg =>
                {
                    if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                    else Log(msg);
                };

                // Captured rePU reference so the outer finally can remove
                // the silent-RE'd PU from the session - prevents accumulation
                // of stale RE'd PUs across probe runs (each one was generating
                // GDM-1001 cascades because addin's UDP/validation services
                // saw multiple Model_1 candidates and got confused).
                dynamic capturedRePU = null;

                await System.Threading.Tasks.Task.Run(() =>
                {
                    // Step 1: capture active model's ms (left-side, dirty v3)
                    IntPtr activeMs = Services.NativeBridgeService.EnsureActiveModelSetCaptured(_currentModel, log);
                    if (activeMs == IntPtr.Zero)
                    {
                        log("[PROBE] Could not capture active ModelSet - aborting.");
                        return;
                    }
                    log($"[PROBE] activeMs = 0x{activeMs.ToInt64():X}");

                    // Step 2: snapshot bridge's seen-ms list before RE so we
                    // can identify which entry is the new RE-created ms.
                    var seenBefore = Services.NativeBridgeService.GetSeenModelSets();
                    log($"[PROBE] seenMs before RE: count={seenBefore.Length}");

                    // Step 3: run SCAPI silent RE - existing infrastructure.
                    // Uses _dbHost/_dbName/etc. captured by Configure dialog.
                    // Schema filter + selected tables (or all model tables).
                    IEnumerable<string> tables = _dbSelectedTables != null && _dbSelectedTables.Count > 0
                        ? _dbSelectedTables
                        : new List<string>();
                    if (!tables.GetEnumerator().MoveNext())
                    {
                        log("[PROBE] No tables selected - aborting (use Select Tables first).");
                        return;
                    }

                    log("[PROBE] starting silent RE...");
                    dynamic rePU = null;
                    try
                    {
                        rePU = Services.DdlGenerationService.ReverseEngineerToSession(
                            scapi: _scapi,
                            currentPU: _currentModel,
                            host: _dbHost,
                            database: _dbName,
                            user: _dbUser,
                            password: _dbPassword,
                            useWindowsAuth: _dbUseWindowsAuth,
                            dbTypeCode: _dbTypeCode,
                            targetServerCode: _dbTargetServer,
                            targetServerVersion: _dbTargetVersion,
                            schema: _dbSchema,
                            selectedTables: tables,
                            log: log);
                    }
                    catch (Exception ex)
                    {
                        log($"[PROBE] silent RE threw: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }
                    if (rePU == null)
                    {
                        log("[PROBE] silent RE returned null - aborting.");
                        return;
                    }
                    string rePuName = ""; try { rePuName = rePU.Name?.ToString() ?? ""; } catch { }
                    log($"[PROBE] silent RE PU created: '{rePuName}'");
                    capturedRePU = rePU;   // keep reference so outer finally can Remove it

                    // Step 4: identify the new ms via bridge MS-TRACK delta.
                    var seenAfter = Services.NativeBridgeService.GetSeenModelSets();
                    log($"[PROBE] seenMs after RE: count={seenAfter.Length}");
                    IntPtr reMs = IntPtr.Zero;
                    var beforeSet = new HashSet<IntPtr>(seenBefore);
                    for (int i = seenAfter.Length - 1; i >= 0; --i)
                    {
                        if (!beforeSet.Contains(seenAfter[i]))
                        {
                            reMs = seenAfter[i];
                            break;
                        }
                    }
                    if (reMs == IntPtr.Zero)
                        log("[PROBE] No new ms tracked after RE.");
                    else
                        log($"[PROBE] reMs = 0x{reMs.ToInt64():X}");

                    // Step 5: VERIFY the RE'd PU actually contains tables.
                    // SCAPI's pu.ReverseEngineer can return success but with
                    // an empty PU when the filter (Synch_Table_Filter_By_Name)
                    // doesn't match any DB tables - usually a name format
                    // mismatch (schema-prefixed vs bare).
                    int entityCount = 0;
                    int viewCount = 0;
                    dynamic verifySess = null;
                    bool ownVerifySess = false;
                    try
                    {
                        verifySess = _scapi.Sessions.Add();
                        verifySess.Open(rePU, 0, 0);
                        ownVerifySess = true;
                        dynamic mo = verifySess.ModelObjects;
                        dynamic root = mo.Root;
                        try
                        {
                            dynamic ents = mo.Collect(root, "Entity");
                            foreach (dynamic _ in ents) entityCount++;
                        }
                        catch (Exception exE) { log($"[PROBE] Collect Entity err: {exE.Message}"); }
                        try
                        {
                            dynamic views = mo.Collect(root, "View");
                            foreach (dynamic _ in views) viewCount++;
                        }
                        catch { }
                    }
                    catch (Exception ex2)
                    {
                        log($"[PROBE] verify session err: {ex2.GetType().Name}: {ex2.Message}");
                    }
                    finally
                    {
                        if (ownVerifySess && verifySess != null)
                        {
                            try { verifySess.Close(); } catch { }
                        }
                    }
                    log($"[PROBE] RE'd PU contents: {entityCount} entity(ies), {viewCount} view(s)");

                    if (entityCount > 0)
                    {
                        log("[PROBE] *** RE PRODUCED TABLES - silent RE is working ***");
                    }
                    else
                    {
                        log("[PROBE] *** RE'd PU is EMPTY - filter or connection issue ***");
                        log("[PROBE] Check: Synch_Table_Filter_By_Name format, Synch_Owned_Only_Name vs DB schema, ODBC driver.");
                        return;
                    }

                    // Step 6: switch active back to dirty mart tab. After
                    // silent RE the RE'd PU is the foreground MDI child,
                    // which would make CC wizard's Left=RE'd / Right=dirty.
                    // Mart-Mart pipeline assumes Left=dirty, Right=other,
                    // so we flip the active tab back to dirty mart.
                    log("[PROBE] step 6: activate dirty mart MDI child");
                    bool tabSwitched = Services.MartMartAutomation.ActivateMartMdiChild(log);
                    if (!tabSwitched)
                    {
                        log("[PROBE] Could not activate mart MDI child - check log.");
                        return;
                    }

                    // Step 7: drive CC wizard + select RE'd PU from "Open
                    // Models in Memory" list + Compare + Apply-to-Right.
                    log($"[PROBE] step 7: DriveCCDbAndApply(reModelName='{rePuName}')");
                }).ConfigureAwait(true);

                // Run on UI thread so overlayToggle can synchronously
                // marshal back. Same pattern as Mart-Mart Generate DDL flow.
                var overlay = ShowBusyOverlay("Generating From-DB DDL, please wait...");
                Services.MartMartAutomation.CCSession sess = null;
                try
                {
                    Action<bool> toggle = visible =>
                    {
                        try
                        {
                            if (InvokeRequired)
                                Invoke(new Action(() => ToggleBusyOverlay(overlay, visible)));
                            else
                                ToggleBusyOverlay(overlay, visible);
                        }
                        catch { }
                    };
                    string reModelName = "Model_1";  // SCAPI silent RE always names new PU "Model_1"
                    sess = await Services.MartMartAutomation.DriveCCDbAndApplyAsync(reModelName, log, toggle);
                    if (sess == null || !sess.Applied)
                    {
                        Log("[PROBE] DriveCCDbAndApply did not apply differences.");
                    }
                    else
                    {
                        Log("[PROBE] *** Apply-to-Right SUCCEEDED in From-DB pipeline ***");
                        Log("[PROBE] step 8: OnFE -> alter DDL");
                        string ddl = await System.Threading.Tasks.Task.Run(() =>
                            Services.NativeBridgeService.GenerateMartMartDdlViaOnFE(msg =>
                            {
                                if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                                else Log(msg);
                            }));
                        if (string.IsNullOrEmpty(ddl))
                            Log("[PROBE] OnFE returned no DDL");
                        else
                        {
                            Log($"[PROBE] *** SUCCESS: {ddl.Length} chars of alter DDL ***");
                            rtbDDLOutput.Text = ddl;
                        }
                    }
                }
                finally
                {
                    Services.MartMartAutomation.CloseSession(sess, msg =>
                    {
                        if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                        else Log(msg);
                    });
                    try { overlay?.Close(); } catch { }
                }

                // Cleanup: remove the silent-RE'd PU from the session so it
                // doesn't accumulate across probe runs. Without this each
                // run leaves a "Model_1" PU in session, addin's UDP/
                // validation services pick them up, and erwin emits GDM-1001
                // cascades when the orphan PUs reference objects no longer
                // present. PUs.Remove(pu, false) was previously avoided for
                // CC-loaded v1 PUs (memory: reference_pus_create_session...)
                // but a SCAPI-Create()'d PU should be safely removable since
                // the CC engine doesn't hold internal refs to it.
                if (capturedRePU != null)
                {
                    try
                    {
                        Log("[PROBE] removing silent-RE'd PU from session...");
                        _scapi.PersistenceUnits.Remove(capturedRePU, false);
                        Log("[PROBE] RE'd PU removed.");
                    }
                    catch (Exception remEx)
                    {
                        Log($"[PROBE] PU remove failed (will accumulate): {remEx.GetType().Name}: {remEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[PROBE] threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                btnFromDbProbe.Enabled = true;
            }
        }

        // SPIKE: test ReverseEngineerScript SCAPI method.
        // Hypothesis: a PU we create + populate via ReverseEngineerScript is
        // SCAPI-managed (not CC-engine-managed), so PUs.Remove(pu, false)
        // should succeed without RPC_E_SERVERFAULT (unlike CC-loaded PUs).
        // If proven, the same approach unblocks From-DB and cross-version
        // Mart-Mart pipelines (load a clean baseline PU, compare, remove
        // cleanly, no orphan, no erwin lock-up).
        private async void BtnREScriptProbe_Click(object sender, EventArgs e)
        {
            btnREScriptProbe.Enabled = false;
            try
            {
                Log("");
                Log("=== ReverseEngineerScript Spike ===");
                if (!_isConnected || _currentModel == null)
                {
                    Log("[REScript] No model connected.");
                    return;
                }

                Action<string> log = msg =>
                {
                    if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                    else Log(msg);
                };

                await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        // Step 1: emit current active model's CREATE DDL via
                        // FEModel_DDL (already proven to work, doesn't
                        // corrupt active PU).
                        string tempDir = System.IO.Path.GetTempPath();
                        string sqlFile = System.IO.Path.Combine(tempDir,
                            $"erwin_rescript_spike_{Guid.NewGuid():N}.sql");
                        log($"[REScript] step 1: FEModel_DDL on active -> {sqlFile}");
                        bool feOk = false;
                        try { feOk = (bool)_currentModel.FEModel_DDL(sqlFile, ""); }
                        catch (Exception fex) { log($"[REScript] FEModel_DDL threw: {fex.Message}"); return; }
                        if (!feOk || !System.IO.File.Exists(sqlFile))
                        {
                            log("[REScript] FEModel_DDL returned false / file missing - aborting");
                            return;
                        }
                        long sz = new System.IO.FileInfo(sqlFile).Length;
                        log($"[REScript] DDL written ok ({sz} bytes)");

                        // Step 2: snapshot session PUs (BEFORE import) so
                        // we can spot the new one.
                        log("[REScript] step 2: pre-import session PUs:");
                        DumpSessionPUs(log);

                        // Step 3: create a blank PU + import the SQL via
                        // ReverseEngineerScript. PropertyBag must be created
                        // via the SCAPI ProgID (matching the working RE flow
                        // in DdlGenerationService.cs:973-979 - the SCAPI app
                        // object does NOT expose a PropertyBag() factory).
                        log("[REScript] step 3: create blank PU + ReverseEngineerScript");
                        Type pbType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0");
                        if (pbType == null)
                        {
                            log("[REScript] SCAPI PropertyBag ProgID not registered - aborting");
                            return;
                        }
                        dynamic propBag;
                        try { propBag = Activator.CreateInstance(pbType); }
                        catch (Exception pex) { log($"[REScript] CreateInstance err: {pex.Message}"); return; }

                        // Reuse active model's Target_Server. The SCAPI sample
                        // for RE expects an INT platform code (13=Oracle,
                        // 16=SQL Server, etc). We pull it from the active PU's
                        // PropertyBag (NOT root.Target_Server which isn't
                        // accessible via dynamic dispatch on the PU).
                        propBag.Add("Model_Type", "Combined");
                        int activeTargetCode = 0;
                        try
                        {
                            object tsRaw = _currentModel.PropertyBag()?.Value("Target_Server");
                            log($"[REScript]   active PU.PropertyBag.Target_Server raw = '{tsRaw}' ({tsRaw?.GetType().Name})");
                            if (tsRaw != null && int.TryParse(tsRaw.ToString(), out int parsed))
                                activeTargetCode = parsed;
                        }
                        catch (Exception pex) { log($"[REScript] PropertyBag Target_Server err: {pex.Message}"); }

                        // Fallback: walk session, find currentModel by reference,
                        // grab its locator-derived target server. Or try
                        // reading the property via Item() on currentPU.PropertyBag.
                        if (activeTargetCode == 0)
                        {
                            // The active model is Oracle19c per session log.
                            // Hardcode 13 (Oracle) so the spike has SOMETHING to
                            // import with. If user's model is SQL Server, this
                            // would mismatch - we'll fix once Root() access is
                            // sorted out.
                            log("[REScript]   could not parse Target_Server; defaulting to 13 (Oracle) for spike");
                            activeTargetCode = 13;
                        }
                        log($"[REScript]   using Target_Server = {activeTargetCode}");
                        propBag.Add("Target_Server", activeTargetCode);
                        propBag.Add("Target_Server_Version", 11);   // arbitrary, sample uses 11

                        dynamic newPU;
                        try
                        {
                            // SCAPI ISCPersistenceUnitCollection::Create
                            // with a fresh PropertyBag yields an empty PU
                            // that's session-attached but not yet populated.
                            newPU = _scapi.PersistenceUnits.Create(propBag);
                        }
                        catch (Exception cex)
                        {
                            log($"[REScript] PUs.Create threw: {cex.Message}");
                            return;
                        }
                        log($"[REScript]   blank PU created: '{(newPU?.Name?.ToString() ?? "?")}'");

                        // Per SCAPI sample, RE keys are added AFTER Create()
                        // and on the SAME PropertyBag (ClearAll + re-add).
                        try
                        {
                            propBag.ClearAll();
                            propBag.Add("System_Objects", false);
                            propBag.Add("Case_Option", 25091);
                            propBag.Add("Logical_Case_Option", 25046);
                            propBag.Add("Infer_Primary_Keys", false);
                            propBag.Add("Infer_Relations", false);
                            propBag.Add("Infer_Relations_Indexes", false);
                            propBag.Add("Force_Physical_Name_Option", false);
                        }
                        catch (Exception kex) { log($"[REScript] RE keys add err: {kex.Message}"); }

                        try
                        {
                            // Re-use propBag for RE options. Per SCAPI ref
                            // the PropertyBag here drives RE behaviour.
                            // Empty Disposition string for default.
                            log("[REScript]   calling ReverseEngineerScript...");
                            object reResult = newPU.ReverseEngineerScript(propBag, sqlFile, "");
                            log($"[REScript]   ReverseEngineerScript returned: {reResult}");
                        }
                        catch (Exception rex)
                        {
                            log($"[REScript] ReverseEngineerScript threw: {rex.GetType().Name}: {rex.Message}");
                            // Try to remove the orphan blank PU before exit.
                            try { _scapi.PersistenceUnits.Remove(newPU, false); log("[REScript]   removed blank-empty PU after failure"); }
                            catch (Exception remErr) { log($"[REScript]   remove blank-empty also failed: {remErr.Message}"); }
                            return;
                        }

                        // Step 4: snapshot AFTER import - did the new PU
                        // become populated? Does it appear separately?
                        log("[REScript] step 4: post-import session PUs:");
                        DumpSessionPUs(log);

                        // Step 5: try to remove the imported PU. If this
                        // succeeds, the user's pipeline idea works for
                        // From-DB + cross-version. If it fails with
                        // RPC_E_SERVERFAULT, ReverseEngineerScript-loaded
                        // PUs face the same limitation as CC-loaded ones.
                        log("[REScript] step 5: PUs.Remove(newPU, false)");
                        try
                        {
                            _scapi.PersistenceUnits.Remove(newPU, false);
                            log("[REScript]   *** Remove() SUCCEEDED - PU eviction works ***");
                        }
                        catch (Exception remEx)
                        {
                            log($"[REScript]   Remove() threw: {remEx.GetType().Name}: {remEx.Message}");
                        }

                        log("[REScript] step 6: post-remove session PUs:");
                        DumpSessionPUs(log);

                        try { System.IO.File.Delete(sqlFile); } catch { }
                    }
                    catch (Exception ex)
                    {
                        log($"[REScript] OUTER threw: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }
            finally
            {
                btnREScriptProbe.Enabled = true;
            }
        }

        // Lightweight helper for the spike: dump session PUs with name +
        // locator. Different from LogSessionPUs which depends on log Action.
        private void DumpSessionPUs(Action<string> log)
        {
            try
            {
                dynamic pus = _scapi?.PersistenceUnits;
                if (pus == null) { log?.Invoke("  (no SCAPI session)"); return; }
                int count = (int)pus.Count;
                log?.Invoke($"  session has {count} PU(s):");
                for (int i = 0; i < count; i++)
                {
                    dynamic pu;
                    try { pu = pus.Item(i); }
                    catch (Exception itEx) { log?.Invoke($"    PU[{i}] Item() err: {itEx.Message}"); continue; }
                    string name = "?";
                    try { name = pu.Name?.ToString() ?? "?"; } catch { }
                    string locator = "";
                    try { locator = pu.PropertyBag()?.Value("Locator")?.ToString() ?? ""; } catch { }
                    bool isActive = false;
                    try { isActive = object.ReferenceEquals(pu, _currentModel); } catch { }
                    log?.Invoke($"    PU[{i}] name='{name}'{(isActive ? " *ACTIVE*" : "")} locator='{locator}'");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  DumpSessionPUs err: {ex.Message}");
            }
        }

        // SPIKE Phase 1: full cross-version pipeline using ReverseEngineerScript-imported
        // clean PU. End-to-end: load v<N> from Mart -> FEModel_DDL -> import as clean PU
        // (proven removable per spike) -> CC compare via "Open Models in Memory" picker
        // -> OnFE -> ALTER DDL -> PUs.Remove(cleanPU, false). The existing Generate DDL
        // button (Mart cross-version path with DriveCCAndApplyAsync) is NOT touched -
        // this probe runs alongside as proof-of-concept for the new pipeline.
        private async void BtnREScriptXVProbe_Click(object sender, EventArgs e)
        {
            btnREScriptXVProbe.Enabled = false;
            try
            {
                Log("");
                Log("=== REScript Cross-Version Probe ===");
                if (!_isConnected || _currentModel == null) { Log("[XV] No model connected."); return; }

                int v = ParseRightVersion();
                int activeV = ParseActivePuVersion();
                string catalog = ParseActivePuCatalog();
                if (v <= 0 || string.IsNullOrEmpty(catalog))
                {
                    Log("[XV] Pick a right-version on cmbRightModel + ensure active is Mart-opened.");
                    return;
                }
                if (v == activeV)
                {
                    Log($"[XV] Right version v{v} == active v{activeV} - pick a DIFFERENT version for cross-version test.");
                    return;
                }
                Log($"[XV] active=v{activeV}, right=v{v}, catalog='{catalog}'");

                Action<string> log = msg =>
                {
                    if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                    else Log(msg);
                };

                dynamic cleanV1PU = null;
                string sqlFile = null;
                Services.MartMartAutomation.CCSession sess = null;

                try
                {
                    // Step 1: load v<N> via existing helper (PUs.Add + FEModel_DDL).
                    // This intentionally uses the proven SCAPI path that the rest
                    // of the addin already uses. The temp Mart-loaded PU may leak
                    // (RPC_E_SERVERFAULT on Remove for CC-loaded PUs is the
                    // documented limitation) - we accept that for THIS spike;
                    // the goal is to validate the right-side cleanup works.
                    log($"[XV] step 1: load v{v} DDL via TryOpenMartVersionDirectly");
                    LogSessionPUs("XV-step1-pre", log);
                    string v1Ddl = await System.Threading.Tasks.Task.Run(() =>
                        Services.DdlGenerationService.TryOpenMartVersionDirectly(_scapi, _currentModel, v, "", log));
                    LogSessionPUs("XV-step1-post", log);
                    if (string.IsNullOrEmpty(v1Ddl))
                    {
                        Log("[XV] v1 DDL load FAILED - cannot proceed");
                        return;
                    }
                    Log($"[XV] v{v} DDL loaded: {v1Ddl.Length} chars");

                    sqlFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"erwin_xv_v{v}_{Guid.NewGuid():N}.sql");
                    System.IO.File.WriteAllText(sqlFile, v1Ddl);
                    Log($"[XV] v{v}.sql written -> {sqlFile}");

                    // Step 2: clean PU + ReverseEngineerScript (proven by REScript spike)
                    Log("[XV] step 2: clean PU + ReverseEngineerScript");
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        Type pbType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0");
                        if (pbType == null) throw new InvalidOperationException("PropertyBag ProgID missing");
                        dynamic pb = Activator.CreateInstance(pbType);
                        pb.Add("Model_Type", "Combined");
                        int targetCode = 0;
                        try
                        {
                            object raw = _currentModel.PropertyBag()?.Value("Target_Server");
                            if (raw != null && int.TryParse(raw.ToString(), out int parsed)) targetCode = parsed;
                        }
                        catch { }
                        if (targetCode > 0)
                        {
                            pb.Add("Target_Server", targetCode);
                            pb.Add("Target_Server_Version", 11);
                        }
                        cleanV1PU = _scapi.PersistenceUnits.Create(pb);
                        log($"[XV]   blank PU created: '{(cleanV1PU?.Name?.ToString() ?? "?")}'");
                        pb.ClearAll();
                        bool reOk = (bool)cleanV1PU.ReverseEngineerScript(pb, sqlFile, "");
                        log($"[XV]   ReverseEngineerScript returned: {reOk}");
                    });
                    LogSessionPUs("XV-step2-post", log);
                    if (cleanV1PU == null)
                    {
                        Log("[XV] cleanV1PU creation failed");
                        return;
                    }
                    string cleanName = (string)cleanV1PU.Name;

                    // Step 3: drive CC + Apply-to-Right via "Open Models in Memory" picker
                    Log($"[XV] step 3: DriveCCDbAndApplyAsync(reModelName='{cleanName}')");
                    var overlay = ShowBusyOverlay("REScript cross-version compare, please wait...");
                    Action<bool> toggle = visible =>
                    {
                        try
                        {
                            if (InvokeRequired) Invoke(new Action(() => ToggleBusyOverlay(overlay, visible)));
                            else ToggleBusyOverlay(overlay, visible);
                        }
                        catch { }
                    };
                    string ddl = null;
                    try
                    {
                        sess = await Services.MartMartAutomation.DriveCCDbAndApplyAsync(cleanName, log, toggle);
                        if (sess == null || !sess.Applied)
                        {
                            Log("[XV] Apply-to-Right did not fire. (no DDL)");
                        }
                        else
                        {
                            ddl = await System.Threading.Tasks.Task.Run(() =>
                                Services.NativeBridgeService.GenerateMartMartDdlViaOnFE(log));
                        }
                    }
                    finally
                    {
                        Services.MartMartAutomation.CloseSession(sess, log);
                        try { overlay?.Close(); } catch { }
                    }

                    if (!string.IsNullOrEmpty(ddl))
                    {
                        Log($"[XV] *** ALTER DDL: {ddl.Length} chars ***");
                        rtbDDLOutput.Text = ddl;
                    }
                    else
                    {
                        Log("[XV] no DDL captured");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[XV] threw: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    // Step 4: KEY TEST - clean up our REScript-loaded PU.
                    // Spike proved this works for in-memory PUs. After CC engine
                    // touched it, does it still remove cleanly?
                    if (cleanV1PU != null)
                    {
                        Log("[XV] step 4: cleanup cleanV1PU");
                        LogSessionPUs("XV-pre-remove", log);
                        try
                        {
                            await System.Threading.Tasks.Task.Delay(800);   // settle CC engine
                            _scapi.PersistenceUnits.Remove(cleanV1PU, false);
                            Log("[XV]   *** cleanV1PU REMOVED ***");
                        }
                        catch (Exception remEx)
                        {
                            Log($"[XV]   Remove err: {remEx.GetType().Name}: {remEx.Message}");
                        }
                        LogSessionPUs("XV-post-remove", log);
                    }
                    if (sqlFile != null) { try { System.IO.File.Delete(sqlFile); } catch { } }
                }
            }
            finally
            {
                btnREScriptXVProbe.Enabled = true;
            }
        }

        // Architecture 2 probe: drive the Forward Engineer Alter Script wizard
        // directly, no CC + no Apply-to-Right click. See DriveFEAlterScriptWizard
        // in MartMartAutomation.cs for rationale.
        private async void BtnFEAlterProbe_Click(object sender, EventArgs e)
        {
            btnFEAlterProbe.Enabled = false;
            try
            {
                Log("");
                Log("=== FE Alter Script Probe (Architecture 2: no clicks) ===");
                if (!_isConnected || _currentModel == null)
                {
                    Log("[FE-PROBE] No model connected.");
                    return;
                }

                Action<string> log = msg =>
                {
                    if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                    else Log(msg);
                };

                string ddl = await Services.MartMartAutomation.DriveFEAlterScriptWizardAsync(log);
                if (string.IsNullOrEmpty(ddl))
                {
                    Log("[FE-PROBE] no DDL captured - see bridge + addin logs above.");
                }
                else
                {
                    Log($"[FE-PROBE] *** SUCCESS: {ddl.Length} chars of alter DDL ***");
                    var preview = ddl.Length > 300 ? ddl.Substring(0, 300) : ddl;
                    Log($"[FE-PROBE] preview: {preview.Replace("\n", "\\n")}");
                    rtbDDLOutput.Text = ddl;
                }
            }
            catch (Exception ex)
            {
                Log($"[FE-PROBE] threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                btnFEAlterProbe.Enabled = true;
            }
        }

        private async void BtnCallOnFE_Click(object sender, EventArgs e)
        {
            btnCallOnFE.Enabled = false;
            try
            {
                Log("");
                Log("=== Mart-Mart via ELA::OnFE ===");
                string ddl = await System.Threading.Tasks.Task.Run(() =>
                    Services.NativeBridgeService.GenerateMartMartDdlViaOnFE(msg =>
                    {
                        if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                        else Log(msg);
                    }));
                if (string.IsNullOrEmpty(ddl))
                {
                    Log("[OnFE] FAILED or no DDL. See bridge log.");
                }
                else
                {
                    Log($"[OnFE] SUCCESS: {ddl.Length} chars");
                    var preview = ddl.Length > 300 ? ddl.Substring(0, 300) : ddl;
                    Log($"[OnFE] preview: {preview.Replace("\n", "\\n")}");
                    rtbDDLOutput.Text = ddl;
                }
            }
            finally { btnCallOnFE.Enabled = true; }
        }

        /// <summary>
        /// Proof-of-concept: after user opens the Alter Script wizard manually
        /// (but BEFORE they click Preview), press this button to directly invoke
        /// FEWPageOptions::InvokePreviewStringOnlyCommand on the captured
        /// pointer. If it returns DDL, we've proven the programmatic path works
        /// while the wizard is alive.
        /// </summary>
        private async void BtnInvokePreviewDirect_Click(object sender, EventArgs e)
        {
            btnInvokePreviewDirect.Enabled = false;
            try
            {
                Log("");
                Log("=== Generate DDL (auto-open wizard hidden + invoke) ===");
                // CRITICAL: run on a background thread so the UI/main thread stays
                // free to pump messages. Otherwise our SendInput Ctrl+Alt+T keystroke
                // queues up but erwin can't dispatch it through MFC accelerator
                // translation because we're blocking the message pump.
                string ddl = await System.Threading.Tasks.Task.Run(() =>
                    Services.NativeBridgeService.GenerateAlterDdl(msg =>
                    {
                        if (InvokeRequired) BeginInvoke(new Action(() => Log(msg)));
                        else Log(msg);
                    }));
                if (string.IsNullOrEmpty(ddl))
                {
                    Log("[INVOKE-DIRECT] FAILED or no wizard open. See bridge log.");
                }
                else
                {
                    Log($"[INVOKE-DIRECT] SUCCESS: {ddl.Length} chars");
                    Log($"[INVOKE-DIRECT] first 200: {ddl.Substring(0, Math.Min(200, ddl.Length)).Replace("\n", "\\n")}");
                    rtbDDLOutput.Text = ddl;
                }
            }
            finally { btnInvokePreviewDirect.Enabled = true; }
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

        private Form ShowLoadingDialog(string message)
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

        private void EnableControls(bool enabled)
        {
            txtDatabaseName.Enabled = enabled;
            txtSchemaName.Enabled = enabled;
            txtName.Enabled = enabled;
            btnApply.Enabled = enabled;
        }

        private void ShowError(string message, string title)
        {
            UpdateConnectionStatus(StatusDisconnected, Color.Red);
            _isConnected = false;
            EnableControls(false);
            UpdateStatus("Connection failed.", Color.Red);
            MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _columnEditorInspector?.Dispose();
            _columnEditorInspector = null;
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

                // Clear stale model references to prevent user from selecting dead COM objects
                _openModels.Clear();
                _connectedModelName = null;
                _globalDataLoaded = false;
                lblActiveModel.Text = "(Waiting for model...)";

                // Reset General tab info
                _lblCorporateValue.Text = "-";
                _lblDbValue.Text = "-";
                _lblRegistryValue.Text = "-";

                // Reset UI to disconnected state
                UpdateConnectionStatus(StatusDisconnected, Color.Red);
                EnableControls(false);
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
                if (tabControl.SelectedTab != tabAlterCompare) return;

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
