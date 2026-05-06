using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Centralized validation coordinator with a single timer.
    /// Monitors for changes and triggers appropriate validations:
    /// - Glossary validation: when column Physical_Name changes (new or edited)
    /// - Domain validation: when Parent_Domain_Ref changes OR when Physical_Name changes with existing domain
    /// Results are collected and displayed in a single popup.
    /// </summary>
    public class ValidationCoordinatorService : IDisposable
    {
        private readonly dynamic _session;
        private readonly dynamic _scapi;
        private Timer _monitorTimer;
        private Timer _windowMonitorTimer;
        private TableTypeMonitorService _tableTypeMonitor;
        private UdpRuntimeService _udpRuntimeService;
        private DependencySetRuntimeService _dependencySetService;
        private bool _isMonitoring;
        private bool _disposed;
        private bool _isProcessingChange;
        // volatile: pipeline UI thread'inden set ediliyor, validation
        // event handler'lar erwin'in baska thread'lerinde calisabilir.
        // .NET memory model bool yazi/okumayi default reorder edebilir,
        // diger thread cached eski deger goruyor olabilir. volatile ile
        // her okuma main memory'den, set sonrasi tum thread'ler gorur.
        private volatile bool _validationSuspended;
        private bool _popupVisible;
        private volatile bool _isCheckingForChanges;
        private bool _columnEditorWasOpen;
        private bool _sessionLost;

        // (Phase-2D 2026-05-06: chunked-cycle batch state retired - the full-model
        // periodic scan is gone, replaced by per-table lazy baseline. Snapshot dict
        // and _tablesBaselined drive everything.)

        // Phase-2C active-editor scoped scan (2026-05-06):
        // While the user has the Column Editor open, MonitorTimer ignores the
        // chunked full-cycle and scans ONLY the entity whose name appears in the
        // editor title. One entity * ~30 attrs * fingerprint pass = ~30 ms work,
        // so the user sees validation popups within one tick (1 s) instead of
        // waiting a full 280-entity cycle (~19 s worst case). When the editor
        // closes the field is cleared and the normal full-cycle scan resumes.
        private string _activeColumnEditorTable;

        // Phase-2D per-table lazy baseline (2026-05-06):
        // The startup-wide silent populate is gone. Each table is silently baselined
        // the first time the user opens its Column Editor (~75 ms one-shot per table).
        // From then on, scoped scan does diff detection against the per-table snapshot.
        // This eliminates the 19-second background populate entirely - work is bounded
        // by the tables the user actually touches.
        private readonly HashSet<string> _tablesBaselined =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Win32 API for window enumeration
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // Snapshot of all attributes
        private Dictionary<string, AttributeValidationSnapshot> _attributeSnapshots;

        // Term-type policy popup deduplication. erwin's Column Editor combo commits the
        // same user pick twice (combo commit + later sync), so without this we revert + popup
        // for the same attempt twice in ~3 seconds. Key = attribute ObjectId, value = the
        // exact attempted value plus when it was first seen.
        private readonly Dictionary<string, (string attempt, DateTime when)> _termTypeRecentAttempts
            = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private const double TermTypeDedupSeconds = 3.0;

        // Snapshot of Key_Group (Index) names for naming standard checks
        private Dictionary<string, string> _keyGroupSnapshots;

        // Cache of Domain ObjectId -> Domain Name
        private Dictionary<string, string> _domainCache;

        // Pending validation results to show in single popup
        private List<CollectedValidationResult> _pendingResults;

        // Monitor interval - Phase-2D (2026-05-07): with the chunked full-model scan
        // retired and only scoped per-table scan running while a Column Editor is
        // open, per-tick work is tiny (~30 ms for one entity). Drop tick to 250 ms
        // so worst-case popup latency from edit to popup is ~250 ms (avg ~125 ms).
        // CPU stays low because scoped scan is bounded.
        private const int MonitorIntervalMs = 250;
        private const int MaxEntitiesPerTick = 30; // unused after Phase-2D, kept for compat

        // Model change detection
        private string _lastKnownModelName;
        private int _modelCheckCounter;
        private const int ModelCheckEveryNTicks = 4; // Check every 2 seconds (4 * 500ms)

        // (Phase-2D 2026-05-07: entity-level periodic walk removed. The 30-tick
        // counter above made the 280-entity TableTypeMonitor scan fire every
        // 7.5 s on big models, which user reported as "I cannot select tables
        // immediately" - the STA was busy walking entities at unpredictable
        // moments. Reactive trigger via entity-properties hook is the future
        // path; for now, entity-level changes wait until user opens the
        // corresponding editor.)

        // Model UDP change detection
        private Dictionary<string, string> _lastModelUdpValues;
        private HashSet<string> _modelUdpPaths; // Actual Property_Type paths for model UDPs

        // Event for logging
        public event Action<string> OnLog;

        // Event fired when session becomes invalid (model closed)
        public event Action OnSessionLost;

        // Event fired when active model changes in erwin
        public event Action<string> OnModelChanged;

        // Event fired when a model-level UDP value changes (for cascade update)
        public event Action<string, string> OnModelUdpChanged; // udpName, newValue


        public ValidationCoordinatorService(dynamic session, dynamic scapi)
        {
            _session = session;
            _scapi = scapi;
            _attributeSnapshots = new Dictionary<string, AttributeValidationSnapshot>();
            _keyGroupSnapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _domainCache = new Dictionary<string, string>();
            _pendingResults = new List<CollectedValidationResult>();

            _monitorTimer = new Timer();
            _monitorTimer.Interval = MonitorIntervalMs;
            _monitorTimer.Tick += MonitorTimer_Tick;

            _windowMonitorTimer = new Timer();
            // 100 ms keeps editor open/close transition latency tight.
            // EnumWindows + GetWindowText is microsecond-scale on user32 - the per-tick
            // cost is dominated by IsColumnEditorOpen's title parse (still <1 ms).
            // Phase-2D close-race fix: with 100 ms detection + ~70 ms FinalValidate work,
            // total Close-to-popup latency stays under ~200 ms.
            _windowMonitorTimer.Interval = 100;
            _windowMonitorTimer.Tick += WindowMonitorTimer_Tick;
        }

        #region Public Methods

        public void SetTableTypeMonitor(TableTypeMonitorService monitor)
        {
            _tableTypeMonitor = monitor;
        }

        public void SetUdpRuntimeService(UdpRuntimeService service)
        {
            _udpRuntimeService = service;
        }

        public void SetDependencySetService(DependencySetRuntimeService service)
        {
            _dependencySetService = service;
        }

        public Dictionary<string, string> GetModelUdpValues()
        {
            return _lastModelUdpValues ?? new Dictionary<string, string>();
        }

        public void StartMonitoring(HashSet<string> preFetchedPropertyTypeNames = null)
        {
            if (_isMonitoring) return;
            _isMonitoring = true;

            // Initialize model change tracking (use same source as detection: window title)
            try
            {
                _lastKnownModelName = GetErwinActiveModelName();
                if (string.IsNullOrEmpty(_lastKnownModelName))
                    _lastKnownModelName = _session.ModelObjects.Root?.Name ?? "";
                _modelCheckCounter = 0;
            }
            catch { }

            // Initialize model UDP change tracking. Pass the cached metamodel
            // Property_Type set when available (populated earlier by
            // EnsureAllUdpsExist) so we skip a duplicate ~700 ms metamodel walk
            // here.
            InitializeModelUdpTracking(preFetchedPropertyTypeNames);

            // Phase-2D (2026-05-06): no startup baseline at all. Per-table silent
            // populate happens on demand the first time the user opens that table's
            // Column Editor.
            // Phase-3B (2026-05-07): domain cache also lazy-populated. The previous
            // upfront BuildDomainCache call cost ~300 ms walking every Domain in
            // the model for a cache that GetDomainParentValue already fills on first
            // miss. Same end state (warm cache after first few validations) without
            // the startup tax.
            _attributeSnapshots.Clear();
            _keyGroupSnapshots.Clear();
            _domainCache.Clear();
            _tablesBaselined.Clear();

            _monitorTimer.Start();
            _windowMonitorTimer.Start();
            Log("ValidationCoordinatorService: Monitoring started (Phase-2D: per-table lazy baseline, no startup populate)");
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _monitorTimer.Stop();
            _windowMonitorTimer.Stop();
            Log("ValidationCoordinatorService: Monitoring stopped");
        }

        public void SuspendValidation()
        {
            _validationSuspended = true;
            // Suspend sirasinda biriken validation sonuclari temizle.
            // Aksi takdirde resume sonrasi timer tick'inde
            // ShowConsolidatedPopup eski sonuclari popup olarak goster
            // (verified 2026-04-27 13:23 From-DB pipeline sonrasi DOMAIN
            // VALIDATION popup spam).
            _pendingResults.Clear();
            Log("ValidationCoordinatorService: Validation suspended (pendingResults cleared)");
        }

        public void ResumeValidation()
        {
            _validationSuspended = false;
            // Resume oncesi pendingResults temizle.
            _pendingResults.Clear();
            // TakeSnapshot CAGRILMIYOR (kasitli):
            // Eski (pipeline-oncesi) snapshot kullanilirsa loop-stable
            // state korunur. Yeni snapshot (Apply-to-Right ile degisen
            // column'lari "yeni baseline" olarak kabul eder) sonraki
            // tick'te diff yakalar -> ValidateGlossary -> popup. Test
            // 13:37:37 dogrulamasi: TakeSnapshot kaldirildiktan sonra
            // resume sonrasi popup beklenmiyor.
            Log("ValidationCoordinatorService: Validation resumed (pendingResults cleared, snapshot UNCHANGED)");
        }

        /// <summary>
        /// Phase-1B (2026-05-06): drop the existing snapshot dictionaries and arm the
        /// silent-population flag so the next MonitorTimer cycle re-baselines without
        /// firing ProcessNewAttribute on existing attributes. Replaces sync TakeSnapshot()
        /// in editor-close + bulk-create paths where the synchronous walk used to freeze
        /// big-model UI for ~21 seconds.
        /// </summary>
        public void RebaselineDeferred()
        {
            _attributeSnapshots.Clear();
            _keyGroupSnapshots.Clear();
            _tablesBaselined.Clear();
            _pendingResults.Clear();
            // Phase-3B (2026-05-07): keep _domainCache populated across rebaselines.
            // Domain ObjectId -> Name mapping is stable for the model session, and
            // GetDomainParentValue lazy-fills cache misses anyway. The previous
            // upfront BuildDomainCache rebuild here cost ~300 ms with no benefit.

            Log("ValidationCoordinatorService: Snapshot dropped, deferred rebaseline scheduled (next cycle silent)");
        }

        public void TakeSnapshot()
        {
            try
            {
                _attributeSnapshots.Clear();
                _keyGroupSnapshots.Clear();
                _domainCache.Clear();

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Build domain cache first
                BuildDomainCache(modelObjects, root);

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                try
                {
                    foreach (dynamic entity in allEntities)
                    {
                        if (entity == null) continue;

                        string tableName = GetTableName(entity);

                        dynamic entityAttrs = null;
                        try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                        catch (Exception ex) { Log($"TakeSnapshot: Failed to collect attributes for entity: {ex.Message}"); continue; }
                        if (entityAttrs == null) continue;

                        try
                        {
                            foreach (dynamic attr in entityAttrs)
                            {
                                if (attr == null) continue;

                                string objectId = "";
                                try { objectId = attr.ObjectId?.ToString() ?? ""; }
                                catch (Exception ex) { Log($"TakeSnapshot: Failed to get ObjectId: {ex.Message}"); continue; }

                                if (string.IsNullOrEmpty(objectId)) continue;

                                var snapshot = CreateSnapshot(attr, tableName, modelObjects);
                                _attributeSnapshots[objectId] = snapshot;
                            }
                        }
                        finally { ReleaseCom(entityAttrs); }

                        // Snapshot Key_Groups for this entity (so they're not flagged as "new" on first check)
                        try
                        {
                            dynamic keyGroups = modelObjects.Collect(entity, "Key_Group");
                            if (keyGroups != null)
                            {
                                foreach (dynamic kg in keyGroups)
                                {
                                    if (kg == null) continue;
                                    try
                                    {
                                        string kgId = kg.ObjectId?.ToString() ?? "";
                                        string kgName = kg.Name ?? "";
                                        if (!string.IsNullOrEmpty(kgId))
                                            _keyGroupSnapshots[kgId] = kgName;
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
                finally { ReleaseCom(allEntities); }

                Log($"ValidationCoordinatorService: Snapshot taken - {_attributeSnapshots.Count} attributes, {_keyGroupSnapshots.Count} key groups");
            }
            catch (Exception ex)
            {
                Log($"ValidationCoordinatorService.TakeSnapshot error: {ex.Message}");
            }
        }

        #endregion

        #region Timer Event

        /// <summary>
        /// Check if any model is still open via SCAPI root (safe — doesn't touch the session).
        /// Returns false if model was closed, triggering session lost without crashing.
        /// </summary>
        private bool IsModelStillOpen()
        {
            try
            {
                return _scapi.PersistenceUnits.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Called externally to suppress model change detection during reconnect.
        /// </summary>
        #region Model UDP Change Detection

        /// <summary>
        /// Scan metamodel for Model.Physical.* Property_Type names and read initial values.
        /// </summary>
        private void InitializeModelUdpTracking(HashSet<string> preFetchedPropertyTypeNames = null)
        {
            _modelUdpPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastModelUdpValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Phase-3A (2026-05-07): if the caller already walked the metamodel
                // (EnsureAllUdpsExist on the connect path), reuse that set instead of
                // opening a second metamodel session here. The metamodel walk is the
                // single biggest cost in StartMonitoring (~700 ms on r10.10 with ~1500
                // Property_Type entries); skipping it shaves the same amount.
                if (preFetchedPropertyTypeNames != null)
                {
                    foreach (var name in preFetchedPropertyTypeNames)
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name.StartsWith("Model.Physical.", StringComparison.OrdinalIgnoreCase))
                            _modelUdpPaths.Add(name);
                    }
                    Log($"InitializeModelUdpTracking: reused cached Property_Type set ({preFetchedPropertyTypeNames.Count} entries) - skipping metamodel walk");
                }
                else
                {
                    // Fallback path: open metamodel session and walk.
                    dynamic mmSession = null;
                    try
                    {
                        mmSession = _scapi.Sessions.Add();
                        mmSession.Open(_scapi.PersistenceUnits.Item(0), 1);
                        dynamic mmObjects = mmSession.ModelObjects;
                        dynamic mmRoot = mmObjects.Root;

                        dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                        foreach (dynamic pt in propertyTypes)
                        {
                            if (pt == null) continue;
                            try
                            {
                                string name = pt.Name ?? "";
                                if (name.StartsWith("Model.Physical.", StringComparison.OrdinalIgnoreCase))
                                    _modelUdpPaths.Add(name);
                            }
                            catch { }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { mmSession?.Close(); } catch { }
                    }
                }

                // Read initial values
                foreach (var path in _modelUdpPaths)
                {
                    try
                    {
                        string val = root.Properties(path)?.Value?.ToString() ?? "";
                        string udpName = path.Substring("Model.Physical.".Length);
                        if (!string.IsNullOrEmpty(val))
                            _lastModelUdpValues[udpName] = val;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"InitializeModelUdpTracking error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if any model-level UDP values changed since last check.
        /// Called from MonitorTimer_Tick (same tick as model change detection).
        /// </summary>
        private void CheckModelUdpChanges()
        {
            if (_modelUdpPaths == null || _modelUdpPaths.Count == 0) return;

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                foreach (var path in _modelUdpPaths)
                {
                    try
                    {
                        string val = root.Properties(path)?.Value?.ToString() ?? "";
                        string udpName = path.Substring("Model.Physical.".Length);
                        string prevVal = "";
                        _lastModelUdpValues.TryGetValue(udpName, out prevVal);

                        if (!string.Equals(val, prevVal ?? "", StringComparison.Ordinal) && !string.IsNullOrEmpty(val))
                        {
                            _lastModelUdpValues[udpName] = val;
                            Log($"[ModelUDP] '{udpName}' changed: '{prevVal}' -> '{val}'");
                            OnModelUdpChanged?.Invoke(udpName, val);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        #endregion

        private void CheckForModelChanges()
        {
            try
            {
                string erwinActiveModel = GetErwinActiveModelName();
                if (string.IsNullOrEmpty(erwinActiveModel)) return;

                // Same model? No action.
                if (string.Equals(erwinActiveModel, _lastKnownModelName, StringComparison.OrdinalIgnoreCase))
                    return;

                Log($"[ModelWatch] Active model changed: '{_lastKnownModelName}' -> '{erwinActiveModel}'");
                _lastKnownModelName = erwinActiveModel;
                OnModelChanged?.Invoke(erwinActiveModel);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckForModelChanges error: {ex.Message}");
            }
        }

        /// <summary>
        /// Parse erwin's main window title to get the active model name.
        /// erwin title format: "erwin DM - [ModelName*] - ..." or "erwin DM - ModelName - ..."
        /// </summary>
        /// <summary>
        /// Get erwin's active model name from the erwin process's main window title.
        /// Process.MainWindowTitle always reflects the active/focused model tab.
        /// </summary>
        private string GetErwinActiveModelName()
        {
            try
            {
                var erwinProcesses = System.Diagnostics.Process.GetProcessesByName("erwin");
                if (erwinProcesses.Length == 0) return "";

                string windowTitle = erwinProcesses[0].MainWindowTitle;
                if (string.IsNullOrEmpty(windowTitle)) return "";

                // Parse: "erwin DM - [ModelName : ModelName* (Read-Only)] - ..."
                int firstDash = windowTitle.IndexOf(" - ");
                if (firstDash < 0) return "";

                string afterDash = windowTitle.Substring(firstDash + 3).Trim();

                // Take part before " : "
                int colonIdx = afterDash.IndexOf(" : ");
                string modelPart = colonIdx >= 0 ? afterDash.Substring(0, colonIdx).Trim() : afterDash.Trim();

                // If no colon, take part before next " - "
                if (colonIdx < 0)
                {
                    int secondDash = modelPart.IndexOf(" - ");
                    if (secondDash >= 0) modelPart = modelPart.Substring(0, secondDash).Trim();
                }

                // Clean brackets and unsaved indicator
                if (modelPart.StartsWith("[")) modelPart = modelPart.Substring(1);
                if (modelPart.EndsWith("*")) modelPart = modelPart.Substring(0, modelPart.Length - 1);

                return modelPart.Trim();
            }
            catch
            {
                return "";
            }
        }

        private void HandleSessionLost()
        {
            if (_sessionLost) return;
            _sessionLost = true;
            _isMonitoring = false;
            // Stop timers directly — don't call any COM methods
            try { _monitorTimer?.Stop(); } catch { }
            try { _windowMonitorTimer?.Stop(); } catch { }
            Log("Session lost - model was closed. Monitoring stopped.");
            try { OnSessionLost?.Invoke(); } catch { }
        }

        private void MonitorTimer_Tick(object sender, EventArgs e)
        {
            if (_sessionLost || !_isMonitoring || _disposed || _isProcessingChange || _validationSuspended || _isCheckingForChanges) return;

            // Safety: check if model is still open BEFORE touching the session.
            // This avoids calling methods on a dead COM object (which causes native crash in erwin).
            if (!IsModelStillOpen()) { HandleSessionLost(); return; }

            // Periodic model change + model UDP change detection (every N ticks)
            _modelCheckCounter++;
            if (_modelCheckCounter >= ModelCheckEveryNTicks)
            {
                _modelCheckCounter = 0;
                CheckForModelChanges();
                CheckModelUdpChanges();
            }

            try
            {
                _isCheckingForChanges = true;

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Phase-2D (2026-05-06): scoped per-table path is the ONLY path.
                // When a Column Editor is open, scan that one entity. The first time
                // the table is touched in this session, do a silent populate of just
                // its attrs (no validation), then mark the table as baselined. From
                // then on, the same scoped scan does diff detection and fires popups.
                if (!string.IsNullOrEmpty(_activeColumnEditorTable))
                {
                    dynamic scopedEntities = modelObjects.Collect(root, "Entity");
                    if (scopedEntities == null) return;
                    try
                    {
                        foreach (dynamic entity in scopedEntities)
                        {
                            if (entity == null) continue;
                            string nameForMatch;
                            try
                            {
                                string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                                nameForMatch = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                            }
                            catch { try { nameForMatch = entity.Name ?? ""; } catch { continue; } }

                            if (!string.Equals(nameForMatch, _activeColumnEditorTable, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // First-time touch for this table -> silent populate (no popups).
                            // Cost ~75 ms for ~30 attrs; user is opening the editor anyway,
                            // so the latency is masked by erwin's own dialog open animation.
                            if (!_tablesBaselined.Contains(_activeColumnEditorTable))
                            {
                                SilentPopulateEntity(entity, modelObjects, nameForMatch);
                                _tablesBaselined.Add(_activeColumnEditorTable);
                                Log($"ValidationCoordinatorService: silent baselined '{_activeColumnEditorTable}' on first edit (count={_attributeSnapshots.Count})");
                                return;
                            }

                            _pendingResults.Clear();
                            CheckEntityForChanges(entity, modelObjects);
                            if (_pendingResults.Count > 0)
                                ShowConsolidatedPopup();
                            return;
                        }
                        // Title parsed but no entity matched - skip this tick.
                        return;
                    }
                    finally
                    {
                        ReleaseCom(scopedEntities);
                    }
                }

                // Editor closed: no scan at all. Phase-2D is purely reactive - any
                // periodic full-model walk reintroduces UI hitches (the previous
                // 7.5 s TableTypeMonitor walk made table selection feel sticky).
                // Entity-level changes (entity rename, TABLE_TYPE UDP) trigger
                // when the user opens the corresponding editor; pure-diagram
                // renames without an editor open are an accepted blind spot.
                // Future work: reactive hook on entity Properties dialog open.
            }
            catch (COMException) { HandleSessionLost(); }
            catch (InvalidComObjectException) { HandleSessionLost(); }
            catch (Exception ex)
            {
                // Any COM-related error means session is dead
                if (ex is System.Runtime.InteropServices.ExternalException ||
                    ex.Message.Contains("RPC") || ex.Message.Contains("COM") ||
                    ex.Message.Contains("disconnected") || ex.Message.Contains("0x800"))
                    HandleSessionLost();
                else
                    System.Diagnostics.Debug.WriteLine($"ValidationCoordinatorService.MonitorTimer_Tick error: {ex.Message}");
            }
            finally
            {
                _isCheckingForChanges = false;
            }
        }

        private void WindowMonitorTimer_Tick(object sender, EventArgs e)
        {
            if (_sessionLost || !_isMonitoring || _disposed || _validationSuspended) return;

            // Safety: check if model is still open BEFORE touching the session.
            if (!IsModelStillOpen()) { HandleSessionLost(); return; }

            try
            {
                bool editorIsOpen = IsColumnEditorOpen(out string activeTable);
                // Capture the table that was active BEFORE we overwrite the field,
                // so the close-transition handler below can run a final scoped
                // scan against it. This catches the "user typed and clicked Close
                // without Tab/Enter" race - erwin commits the typed value as part
                // of the Close click, but that commit lands AFTER the most recent
                // MonitorTimer scoped tick, so the per-keystroke scan path missed
                // it. The final pass below validates one last time against the
                // (now-closed) table's snapshot.
                string previousTable = _activeColumnEditorTable;
                _activeColumnEditorTable = editorIsOpen ? activeTable : null;

                if (_columnEditorWasOpen && !editorIsOpen)
                {
                    Log("Column Editor closed - final validation pass + PLEASE CHANGE IT cleanup");

                    // Final scoped validation for the just-closed table. Only runs
                    // if the table was previously baselined (so a meaningful diff
                    // can be computed); if user opened the editor and immediately
                    // closed without baselining, there's nothing to validate.
                    if (!string.IsNullOrEmpty(previousTable)
                        && _tablesBaselined.Contains(previousTable))
                    {
                        try { FinalValidateClosedTable(previousTable); }
                        catch (Exception ex) { Log($"FinalValidateClosedTable err: {ex.Message}"); }
                    }

                    // Scoped delete: walk only the just-closed table's attrs, not all
                    // 280 * 30. Cuts the post-popup wait from ~5 s to ~30 ms.
                    DeletePleaseChangeItColumns(previousTable);
                }

                _columnEditorWasOpen = editorIsOpen;
            }
            catch (COMException) { HandleSessionLost(); }
            catch (InvalidComObjectException) { HandleSessionLost(); }
            catch (Exception ex)
            {
                if (ex is System.Runtime.InteropServices.ExternalException ||
                    ex.Message.Contains("RPC") || ex.Message.Contains("0x800"))
                    HandleSessionLost();
            }
        }

        private bool IsColumnEditorOpen()
        {
            return IsColumnEditorOpen(out _);
        }

        /// <summary>
        /// Phase-2C (2026-05-06): also extract the table name from the editor title.
        /// erwin titles look like:
        ///   "SQL Server Table 'TEST_DATA_TAB_178' Column 'ID' Editor"
        ///   "Oracle Table 'Test_tablosu' Column 'Col2' Editor"
        /// We pull the substring between the FIRST pair of single quotes - that is
        /// the active table the user is editing. The MonitorTimer scopes its scan to
        /// that one entity while the editor is open, dropping per-edit validation
        /// latency from full-cycle (~19 s) to single-entity (~0.03 s).
        /// </summary>
        private bool IsColumnEditorOpen(out string activeTable)
        {
            string foundTable = null;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                StringBuilder title = new StringBuilder(512);
                GetWindowText(hWnd, title, 512);
                string windowTitle = title.ToString();

                if (windowTitle.Contains("Column") && windowTitle.Contains("Editor"))
                {
                    int firstQuote = windowTitle.IndexOf('\'');
                    if (firstQuote >= 0)
                    {
                        int secondQuote = windowTitle.IndexOf('\'', firstQuote + 1);
                        if (secondQuote > firstQuote + 1)
                        {
                            foundTable = windowTitle.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                            return false;
                        }
                    }
                    // Editor open but title couldn't be parsed - signal "open" with empty
                    // name so the scoped path falls back to full-cycle.
                    foundTable = string.Empty;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            activeTable = foundTable;
            return foundTable != null;
        }

        /// <summary>
        /// Phase-2D scoped cleanup (2026-05-07): when scopeTable is non-empty, only
        /// walk that single entity's attributes; otherwise walk all entities (legacy
        /// fallback). The big-model close path used to walk all 280 * 30 = 8400 attrs
        /// here just to find the 1-2 PLEASE CHANGE IT placeholders the user left in
        /// the table they were editing - ~4-5 seconds of STA freeze right after the
        /// popup OK click. Scoping to the closed editor's table cuts that to ~30 ms.
        /// </summary>
        private void DeletePleaseChangeItColumns(string scopeTable = null)
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                var columnsToDelete = new List<dynamic>();

                try
                {
                    foreach (dynamic entity in allEntities)
                    {
                        if (entity == null) continue;

                        // Phase-2D: when called for a specific table (the just-closed
                        // editor's table), skip every other entity. The match is name-based
                        // (Physical_Name with %generated fallback to entity.Name) so it
                        // mirrors what IsColumnEditorOpen extracted from the editor title.
                        if (!string.IsNullOrEmpty(scopeTable))
                        {
                            string entityName;
                            try
                            {
                                string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                                entityName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                            }
                            catch { try { entityName = entity.Name ?? ""; } catch { continue; } }

                            if (!string.Equals(entityName, scopeTable, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        dynamic entityAttrs = null;
                        try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                        catch (Exception ex) { Log($"DeletePleaseChangeIt: Failed to collect attributes: {ex.Message}"); continue; }
                        if (entityAttrs == null) continue;

                        try
                        {
                            foreach (dynamic attr in entityAttrs)
                            {
                                if (attr == null) continue;

                                string physicalName = "";
                                try
                                {
                                    string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                                    string attrName = attr.Name ?? "";
                                    physicalName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : attrName;
                                }
                                catch (Exception ex) { Log($"DeletePleaseChangeIt: Failed to read attr name: {ex.Message}"); continue; }

                                // Catch the canonical name AND erwin's sibling-collision variants
                                // (PLEASE_CHANGE_IT__792 etc.) so the cleanup-on-editor-close pass
                                // doesn't leave orphan placeholders behind.
                                if (IsPleaseChangeItPlaceholder(physicalName))
                                {
                                    columnsToDelete.Add(attr);
                                }
                            }
                        }
                        finally { ReleaseCom(entityAttrs); }

                        // When scoped, the matching entity is unique; bail after processing it.
                        if (!string.IsNullOrEmpty(scopeTable)) break;
                    }
                }
                finally { ReleaseCom(allEntities); }

                if (columnsToDelete.Count > 0)
                {
                    int transId = _session.BeginNamedTransaction("DeleteInvalidColumns");
                    try
                    {
                        foreach (var attr in columnsToDelete)
                        {
                            try
                            {
                                string attrName = attr.Name ?? "unknown";
                                modelObjects.Remove(attr);
                                Log($"Deleted 'PLEASE CHANGE IT' column: {attrName}");
                            }
                            catch (Exception ex)
                            {
                                Log($"Failed to delete column: {ex.Message}");
                            }
                        }
                        _session.CommitTransaction(transId);
                        Log($"Deleted {columnsToDelete.Count} 'PLEASE CHANGE IT' column(s)");
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); }
                        catch (Exception rbEx) { Log($"DeletePleaseChangeIt: Rollback failed: {rbEx.Message}"); }
                        Log($"DeletePleaseChangeIt: Transaction failed: {ex.Message}");
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DeletePleaseChangeItColumns error: {ex.Message}");
            }
        }

        #endregion

        #region Change Detection

        /// <summary>
        /// Phase-2D close-race fix (2026-05-07): final scoped validation pass on the
        /// table whose Column Editor just closed. Required because erwin commits the
        /// typed value when the user clicks Close without first pressing Tab/Enter,
        /// and that commit lands AFTER the MonitorTimer's last scoped tick - leaving
        /// the edit invisible to the per-keystroke scan path. Runs CheckEntityForChanges
        /// once and shows any pending popups, then returns. Idempotent: if no diff,
        /// nothing fires.
        /// </summary>
        private void FinalValidateClosedTable(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return;
            if (_validationSuspended) return;
            if (_sessionLost || _disposed) return;

            dynamic modelObjects = null;
            dynamic root = null;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelObjects?.Root;
            }
            catch (Exception ex) { Log($"FinalValidateClosedTable: ModelObjects err: {ex.Message}"); return; }
            if (root == null) return;

            dynamic allEntities = null;
            try { allEntities = modelObjects.Collect(root, "Entity"); }
            catch (Exception ex) { Log($"FinalValidateClosedTable: Collect err: {ex.Message}"); return; }
            if (allEntities == null) return;

            try
            {
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;
                    string nameForMatch;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        nameForMatch = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                    }
                    catch { try { nameForMatch = entity.Name ?? ""; } catch { continue; } }

                    if (!string.Equals(nameForMatch, tableName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    _pendingResults.Clear();
                    CheckEntityForChanges(entity, modelObjects);
                    if (_pendingResults.Count > 0)
                        ShowConsolidatedPopup();
                    return;
                }
            }
            finally
            {
                ReleaseCom(allEntities);
            }
        }

        /// <summary>
        /// Phase-2D (2026-05-06): silently snapshot every attribute of a single entity
        /// without firing ProcessNewAttribute / ProcessAttributeChanges. Used by the
        /// per-table lazy baseline path in MonitorTimer_Tick: the first time the user
        /// opens a Column Editor for a table, we capture its attrs as the "known good"
        /// state so subsequent fingerprint diffs detect real edits. Cost ~30 attrs *
        /// 5 properties * ~0.5 ms = ~75 ms; runs while erwin is opening the editor
        /// dialog so the latency is masked.
        /// </summary>
        private void SilentPopulateEntity(dynamic entity, dynamic modelObjects, string tableName)
        {
            if (entity == null) return;
            dynamic entityAttrs = null;
            try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
            catch (Exception ex) { Log($"SilentPopulateEntity: Collect failed: {ex.Message}"); return; }
            if (entityAttrs == null) return;

            try
            {
                int n = 0;
                foreach (dynamic attr in entityAttrs)
                {
                    if (attr == null) continue;

                    string objectId = "";
                    try { objectId = attr.ObjectId?.ToString() ?? ""; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SilentPopulate ObjectId: {ex.Message}"); continue; }
                    if (string.IsNullOrEmpty(objectId)) continue;

                    var snap = CreateSnapshot(attr, tableName, modelObjects);
                    _attributeSnapshots[objectId] = snap;
                    n++;
                }
                System.Diagnostics.Debug.WriteLine($"SilentPopulateEntity: '{tableName}' baselined {n} attributes");
            }
            finally
            {
                ReleaseCom(entityAttrs);
            }

            // Also baseline this entity's Key_Groups so naming-standard checks don't
            // misfire on the next scoped scan (the existing CheckEntityKeyGroups logic
            // treats unknown keys as "new" and silent-populates them anyway, but doing
            // it here keeps the lazy-baseline contract explicit).
            try
            {
                dynamic keyGroups = modelObjects.Collect(entity, "Key_Group");
                if (keyGroups != null)
                {
                    try
                    {
                        foreach (dynamic kg in keyGroups)
                        {
                            if (kg == null) continue;
                            try
                            {
                                string kgId = kg.ObjectId?.ToString() ?? "";
                                string kgName = kg.Name ?? "";
                                if (!string.IsNullOrEmpty(kgId))
                                    _keyGroupSnapshots[kgId] = kgName;
                            }
                            catch { }
                        }
                    }
                    finally { ReleaseCom(keyGroups); }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SilentPopulateEntity Key_Group err: {ex.Message}"); }
        }

        private void CheckEntityForChanges(dynamic entity, dynamic modelObjects)
        {
            if (_validationSuspended) return;
            try
            {
                string tableName = GetTableName(entity);

                // Get predefined column names for this entity's TABLE_TYPE (to skip glossary validation)
                var predefinedColumnNames = GetPredefinedColumnNames(entity);

                dynamic entityAttrs = null;
                try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                catch (Exception ex) { Log($"CheckEntityForChanges: Failed to collect attributes: {ex.Message}"); return; }
                if (entityAttrs == null) return;

                try
                {
                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string objectId = "";
                        try { objectId = attr.ObjectId?.ToString() ?? ""; }
                        catch (Exception ex) { Log($"CheckEntityForChanges: Failed to get ObjectId: {ex.Message}"); continue; }
                        if (string.IsNullOrEmpty(objectId)) continue;

                        // Phase-2A fingerprint pass (2026-05-05): for known attributes, read
                        // only Physical_Name + Physical_Data_Type (2 of the 5 fields full
                        // CreateSnapshot reads) and short-circuit if both match the stored
                        // snapshot. Eliminates ~60% of per-cycle COM traffic on big models
                        // when nothing has changed (the common case). Skipped during initial
                        // silent population so existing attrs land in the snapshot dict via
                        // the slow path.
                        if (_attributeSnapshots.TryGetValue(objectId, out var existingSnap))
                        {
                            string fpRawPhys = null;
                            try { fpRawPhys = attr.Properties("Physical_Name").Value?.ToString(); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CheckEntityForChanges: fingerprint Physical_Name error: {ex.Message}"); }

                            string fpRawType = null;
                            try { fpRawType = attr.Properties("Physical_Data_Type").Value?.ToString(); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CheckEntityForChanges: fingerprint Physical_Data_Type error: {ex.Message}"); }

                            if (fpRawPhys != null && fpRawType != null)
                            {
                                // Match the same '%generated' fallback semantics as CreateSnapshot.
                                // We avoid an extra attr.Name read by reusing the previously
                                // resolved AttributeName as the % fallback proxy: edits to the
                                // attribute Name field while Physical_Name is '%generated' are an
                                // accepted blind spot in the fast pass (rare for user-typed names).
                                string fpResolvedPhys = (!string.IsNullOrEmpty(fpRawPhys) && !fpRawPhys.StartsWith("%"))
                                    ? fpRawPhys
                                    : (existingSnap.AttributeName ?? string.Empty);

                                if (string.Equals(existingSnap.PhysicalName ?? string.Empty, fpResolvedPhys, StringComparison.Ordinal)
                                    && string.Equals(existingSnap.PhysicalDataType ?? string.Empty, fpRawType ?? string.Empty, StringComparison.Ordinal))
                                {
                                    // No change in the two fields that drive validation —
                                    // skip CreateSnapshot, ProcessAttributeChanges, etc.
                                    continue;
                                }
                            }
                        }

                        var currentState = CreateSnapshot(attr, tableName, modelObjects);
                        bool isNew = !_attributeSnapshots.ContainsKey(objectId);

                        if (isNew)
                        {
                            _attributeSnapshots[objectId] = currentState;
                            // Phase-2D: per-table silent populate happens BEFORE the first
                            // CheckEntityForChanges call for a given table (see scoped path
                            // in MonitorTimer_Tick). So when we reach this branch with isNew
                            // true, the attribute genuinely DID NOT exist at baseline time -
                            // it's user-added during the active edit session. Validate it.
                            ProcessNewAttribute(attr, currentState, predefinedColumnNames);
                        }
                        else
                        {
                            var previousState = _attributeSnapshots[objectId];

                            // Carry over BEFORE ProcessAttributeChanges runs, so the term-type
                            // policy inside it can see the canonical concept resolved at glossary
                            // apply time. ValidateGlossary in the rename branch may overwrite this
                            // (column renamed to a different glossary term); for the data-type-only
                            // branch, this carry-over is the only source of TermTypeCanonical.
                            currentState.TermTypeCanonical = previousState.TermTypeCanonical;
                            foreach (var kvp in previousState.UdpValues)
                                currentState.UdpValues[kvp.Key] = kvp.Value;
                            if (string.IsNullOrEmpty(currentState.PhysicalDataType))
                                currentState.PhysicalDataType = previousState.PhysicalDataType;

                            ProcessAttributeChanges(attr, previousState, currentState, predefinedColumnNames);
                            _attributeSnapshots[objectId] = currentState;
                        }

                        // Check column-level UDP changes for dependency cascade
                        CheckAttributeUdpDependencies(attr, objectId, currentState.PhysicalName);
                    }
                }
                finally { ReleaseCom(entityAttrs); }

                // Check Key_Group (Index) naming for this entity's indexes
                CheckEntityKeyGroups(entity, modelObjects, tableName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckEntityForChanges error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check column-level UDP changes for dependency cascade.
        /// Only checks UDPs that are source in a cascade map (efficient).
        /// </summary>
        private void CheckAttributeUdpDependencies(dynamic attr, string objectId, string columnName)
        {
            if (_dependencySetService == null || !_dependencySetService.IsLoaded) return;
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;

            var snapshot = _attributeSnapshots.ContainsKey(objectId) ? _attributeSnapshots[objectId] : null;
            if (snapshot == null) return;

            // Only read UDPs that have cascade dependencies (not all)
            var cascadeSourceUdps = _dependencySetService.GetAllCascadeSourceUdps();
            if (cascadeSourceUdps == null || cascadeSourceUdps.Count == 0) return;

            try
            {
                foreach (var udpName in cascadeSourceUdps)
                {
                    try
                    {
                        string path = $"Attribute.Physical.{udpName}";
                        string currentVal = attr.Properties(path)?.Value?.ToString() ?? "";

                        string prevVal = "";
                        snapshot.UdpValues.TryGetValue(udpName, out prevVal);
                        prevVal = prevVal ?? "";

                        if (currentVal != prevVal && !string.IsNullOrEmpty(currentVal))
                        {
                            snapshot.UdpValues[udpName] = currentVal;
                            Log($"Column UDP '{udpName}' changed on '{columnName}': '{prevVal}' -> '{currentVal}'");

                            // Trigger dependency evaluation
                            _udpRuntimeService.HandleUdpValueChange(attr, udpName, currentVal, "Column");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void ProcessNewAttribute(dynamic attr, AttributeValidationSnapshot currentState, HashSet<string> predefinedColumnNames)
        {
            // Suspended ise hicbir validation yapma. Timer-tick disindan
            // (event handler) cagrilan path'ler icin sigorta.
            if (_validationSuspended) return;
            _isProcessingChange = true;
            try
            {
                // New attribute
                bool hasValidDomain = IsValidDomain(currentState.DomainParentValue);

                if (hasValidDomain)
                {
                    // Domain is set -> only domain validation (skip glossary)
                    ValidateDomain(attr, currentState, null);
                }
                else
                {
                    // No domain -> glossary validation
                    ValidateGlossary(attr, currentState, predefinedColumnNames);
                }

                // Apply Column UDP defaults (with Glossary mapping if configured)
                ApplyColumnUdpDefaults(attr, currentState.PhysicalName);

                // Validate Column naming standards
                ValidateColumnNamingStandard(attr, currentState);
            }
            finally
            {
                _isProcessingChange = false;
            }
        }

        private void ProcessAttributeChanges(dynamic attr, AttributeValidationSnapshot previousState, AttributeValidationSnapshot currentState, HashSet<string> predefinedColumnNames)
        {
            if (_validationSuspended) return;
            bool physicalNameChanged = previousState.PhysicalName != currentState.PhysicalName;
            bool domainChanged = previousState.DomainParentValue != currentState.DomainParentValue;
            bool hasValidDomain = IsValidDomain(currentState.DomainParentValue);
            bool hadValidDomain = IsValidDomain(previousState.DomainParentValue);
            // Term-type guard fires when the user edits Physical_Data_Type without renaming
            // the column. If the rename path runs, glossary will re-apply the type itself
            // (authoritative), so we skip the policy in that branch to avoid reverting our
            // own write.
            bool dataTypeChanged = !string.Equals(previousState.PhysicalDataType ?? string.Empty,
                                                  currentState.PhysicalDataType ?? string.Empty,
                                                  StringComparison.Ordinal);

            // No changes relevant to validation
            if (!physicalNameChanged && !domainChanged && !dataTypeChanged) return;

            _isProcessingChange = true;
            try
            {
                if (physicalNameChanged)
                {
                    Log($"Physical name changed: {previousState.TableName}.{previousState.PhysicalName} -> {currentState.PhysicalName}");

                    if (hasValidDomain)
                    {
                        // Domain is set -> only domain validation (skip glossary)
                        ValidateDomain(attr, currentState, previousState.DomainParentValue);
                    }
                    else
                    {
                        // No domain -> glossary validation
                        ValidateGlossary(attr, currentState, predefinedColumnNames);
                    }

                    // Re-apply Column UDP defaults with new column name (Glossary mapping resolves by name)
                    ApplyColumnUdpDefaults(attr, currentState.PhysicalName);

                    // Validate Column naming standards
                    ValidateColumnNamingStandard(attr, currentState);
                }

                // Domain changed (and new domain is valid) - need domain validation
                if (domainChanged && hasValidDomain)
                {
                    Log($"Domain changed: {currentState.TableName}.{currentState.PhysicalName} -> {currentState.DomainParentValue}");
                    ValidateDomain(attr, currentState, previousState.DomainParentValue);
                }

                // Term-type policy: only fires when ONLY Physical_Data_Type changed (not on rename
                // — the rename branch above re-applied glossary defaults authoritatively, so any
                // diff there is intentional, not a user edit to constrain).
                if (dataTypeChanged && !physicalNameChanged)
                {
                    Log($"Physical_Data_Type changed: {currentState.TableName}.{currentState.PhysicalName} '{previousState.PhysicalDataType}' -> '{currentState.PhysicalDataType}' (canonical='{currentState.TermTypeCanonical ?? "(none)"}')");
                    EnforceTermTypePolicy(attr, previousState, currentState);
                }
            }
            finally
            {
                _isProcessingChange = false;
            }
        }

        /// <summary>
        /// Apply the term-type policy when a column's Physical_Data_Type changed and the
        /// snapshot carries a canonical concept resolved at glossary-apply time:
        ///   BUSINESS_TERM       -> revert both base type and length to the previous value
        ///   AMORPH_DATA_TYPE    -> accept new base type, revert length to previous
        ///   AMORPH_DATA_LENGTH  -> accept new length, revert base type to previous
        ///   AMORPH (or null)    -> no-op (no constraint)
        /// On revert we also pop a popup explaining what was disallowed; on full acceptance
        /// (e.g. AMORPH_DATA_TYPE with only the type changed) we stay silent.
        /// </summary>
        private void EnforceTermTypePolicy(dynamic attr, AttributeValidationSnapshot prev, AttributeValidationSnapshot curr)
        {
            string canonical = curr.TermTypeCanonical;
            if (string.IsNullOrEmpty(canonical)) return;
            if (canonical.Equals("AMORPH", StringComparison.OrdinalIgnoreCase)) return;

            var oldParts = DataTypeParser.Parse(prev.PhysicalDataType);
            var newParts = DataTypeParser.Parse(curr.PhysicalDataType);

            bool baseChanged = !string.Equals(oldParts.Base, newParts.Base, StringComparison.OrdinalIgnoreCase);
            bool lenChanged = !string.Equals(oldParts.Length ?? string.Empty, newParts.Length ?? string.Empty, StringComparison.Ordinal);

            if (!baseChanged && !lenChanged) return;

            // Decide which parts to keep from new vs revert from old. Suffix is always taken
            // from the new value so user-added modifiers (e.g. " WITH TIME ZONE") survive
            // length-only or type-only locking, unless the policy locks both.
            string keepBase = oldParts.Base;
            string keepLength = oldParts.Length;
            string keepSuffix = oldParts.Suffix;
            string disallowed;

            switch (canonical.ToUpperInvariant())
            {
                case "BUSINESS_TERM":
                    disallowed = (baseChanged && lenChanged) ? "Type and length"
                                : baseChanged ? "Type" : "Length";
                    // keep everything from old parts
                    break;
                case "AMORPH_DATA_TYPE":
                    if (!lenChanged) return; // user only touched type, which is allowed
                    keepBase = newParts.Base;
                    keepSuffix = newParts.Suffix;
                    disallowed = "Length";
                    break;
                case "AMORPH_DATA_LENGTH":
                    if (!baseChanged) return; // user only touched length, which is allowed
                    // Type change is forbidden; keep both old base AND old length. erwin's combo
                    // clears the length when the user picks a different type from the dropdown,
                    // so newParts.Length is usually null here — adopting that null would erase
                    // the length entirely (visible as "VARCHAR2" with no parens). The user's
                    // intent was a type swap, not a length edit, so revert everything to old.
                    disallowed = "Type";
                    break;
                default:
                    Log($"TermType policy: unknown canonical '{canonical}' for {curr.TableName}.{curr.PhysicalName} — skipping enforcement");
                    return;
            }

            string corrected = DataTypeParser.Format(new DataTypeParser.Parts(keepBase, keepLength, keepSuffix));

            // Already at the right value — nothing to write. This guards against feedback
            // loops where erwin normalises the format slightly between writes.
            if (string.Equals(corrected, curr.PhysicalDataType ?? string.Empty, StringComparison.Ordinal)) return;

            // Suppress duplicate popups for the same attempt. erwin's Column Editor combo
            // commits the user pick twice (combo commit + a second sync later), so without
            // this guard we'd revert+popup the same attempt back-to-back. Silent revert is
            // still correct on the duplicate so the model never holds the disallowed value.
            string attemptedFormatted = curr.PhysicalDataType ?? string.Empty;
            bool suppressPopup = false;
            if (!string.IsNullOrEmpty(curr.ObjectId)
                && _termTypeRecentAttempts.TryGetValue(curr.ObjectId, out var lastAttempt)
                && string.Equals(lastAttempt.attempt, attemptedFormatted, StringComparison.Ordinal)
                && (DateTime.UtcNow - lastAttempt.when).TotalSeconds < TermTypeDedupSeconds)
            {
                suppressPopup = true;
            }
            if (!string.IsNullOrEmpty(curr.ObjectId))
            {
                _termTypeRecentAttempts[curr.ObjectId] = (attemptedFormatted, DateTime.UtcNow);
            }

            int trans = _session.BeginNamedTransaction("EnforceTermTypePolicy");
            try
            {
                attr.Properties("Physical_Data_Type").Value = corrected;
                _session.CommitTransaction(trans);

                // Update the snapshot so the next tick sees the corrected state as baseline
                // and doesn't re-fire as another "Physical_Data_Type changed" diff.
                curr.PhysicalDataType = corrected;

                Log($"TermType policy [{canonical}]: {curr.TableName}.{curr.PhysicalName} {disallowed} reverted ('{prev.PhysicalDataType}' -> '{corrected}', user attempted '{attemptedFormatted}'{(suppressPopup ? ", popup suppressed (duplicate within " + TermTypeDedupSeconds + "s)" : "")})");

                if (!suppressPopup)
                {
                    MessageBox.Show(
                        $"Column '{curr.TableName}.{curr.PhysicalName}' is tagged '{canonical}' in the glossary.\n\n" +
                        $"{disallowed} cannot be changed and was reverted.\n\n" +
                        $"Restored: {corrected}",
                        "Term Type Constraint",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                try { _session.RollbackTransaction(trans); }
                catch (Exception rbEx) { Log($"EnforceTermTypePolicy rollback error: {rbEx.Message}"); }
                Log($"EnforceTermTypePolicy write failed for {curr.PhysicalName}: {ex.Message}");
            }
        }

        #endregion

        #region Validation Methods

        /// <summary>
        /// Match any of the auto-generated "needs-rename" placeholder names: the canonical
        /// 'PLEASE CHANGE IT' we write, plus the variants erwin produces when sibling
        /// attribute names collide (e.g. 'PLEASE_CHANGE_IT__792'). Treat them all as
        /// already-flagged so we don't re-validate them and re-trigger the rename loop.
        /// </summary>
        private static bool IsPleaseChangeItPlaceholder(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("PLEASE_CHANGE_IT", StringComparison.OrdinalIgnoreCase);
        }

        private void ValidateGlossary(dynamic attr, AttributeValidationSnapshot state, HashSet<string> predefinedColumnNames)
        {
            if (_validationSuspended) return;
            // Skip special names
            if (string.IsNullOrEmpty(state.PhysicalName) ||
                state.PhysicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                state.PhysicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase) ||
                IsPleaseChangeItPlaceholder(state.PhysicalName))
            {
                return;
            }

            // Skip predefined columns - they are auto-added by TABLE_TYPE and don't need glossary validation
            if (predefinedColumnNames != null && predefinedColumnNames.Contains(state.PhysicalName))
            {
                Log($"Glossary validation skipped (predefined column): {state.TableName}.{state.PhysicalName}");
                return;
            }

            var glossary = GlossaryService.Instance;
            if (!glossary.IsLoaded)
            {
                glossary.LoadGlossary();
            }

            if (!glossary.IsLoaded)
            {
                // Can't validate - skip
                return;
            }

            if (!glossary.HasEntry(state.PhysicalName))
            {
                Log($"Glossary validation FAILED: {state.TableName}.{state.PhysicalName}");
                _pendingResults.Add(new CollectedValidationResult
                {
                    ValidationType = CollectedValidationResultType.Glossary,
                    TableName = state.TableName,
                    ColumnName = state.PhysicalName,
                    Message = "Column name not found in glossary. Please use a column name from the glossary.",
                    Attribute = attr,
                    ObjectId = state.ObjectId
                });
            }
            else
            {
                var udpValues = glossary.GetUdpValues(state.PhysicalName);
                string mappingSummary = udpValues != null ? string.Join(", ", udpValues.Select(kv => $"{kv.Key}={kv.Value}")) : "";
                Log($"Glossary validation passed: {state.TableName}.{state.PhysicalName} ({mappingSummary})");

                // Apply glossary UDP values dynamically
                if (attr != null && udpValues != null && udpValues.Count > 0)
                {
                    ApplyGlossaryUdpValues(attr, udpValues, state.PhysicalName);
                }

                // Term-type metadata for downstream policy enforcement: cache the canonical
                // concept on the snapshot and refresh PhysicalDataType from erwin (the
                // glossary apply above may have just written a new value via the
                // DATA_TYPE -> Physical_Data_Type mapping).
                state.TermTypeCanonical = glossary.GetTermTypeCanonical(state.PhysicalName);
                if (attr != null)
                {
                    try
                    {
                        string freshDataType = attr.Properties("Physical_Data_Type").Value?.ToString();
                        if (!string.IsNullOrEmpty(freshDataType))
                            state.PhysicalDataType = freshDataType;
                    }
                    catch (Exception ex) { Log($"Glossary post-apply data type read error: {ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// Validate column name against naming standards and add failures to pending results.
        /// </summary>
        private void ValidateColumnNamingStandard(dynamic attr, AttributeValidationSnapshot state)
        {
            if (_validationSuspended) return;
            if (!NamingStandardService.Instance.IsLoaded) return;
            if (string.IsNullOrEmpty(state.PhysicalName) ||
                state.PhysicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                state.PhysicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // attr MUST be passed so UDP-conditional rules (DEPENDS_ON_UDP_ID) can read
            // the live UDP value off the column. Without it, UDP-conditional rules are skipped.
            // Cast to object to keep the call compile-time resolved (dynamic dispatch breaks
            // the LINQ lambdas inside the engine — same trick as CheckEntityKeyGroups).
            object attrBoxed = attr;

            // Step 1: silently apply AUTO_APPLY=true rules
            if (attrBoxed != null)
            {
                string afterAuto = NamingValidationEngine.ApplyNamingStandards("Column", state.PhysicalName, attrBoxed, autoOnly: true);
                if (!string.Equals(afterAuto, state.PhysicalName, StringComparison.Ordinal))
                {
                    int transId = _session.BeginNamedTransaction("ApplyAutoColumnNaming");
                    try
                    {
                        attr.Properties("Physical_Name").Value = afterAuto;
                        _session.CommitTransaction(transId);
                        Log($"Column naming auto-applied (silent): '{state.TableName}.{state.PhysicalName}' -> '{afterAuto}'");
                        state.PhysicalName = afterAuto;
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"ApplyAutoColumnNaming rollback error: {rbEx.Message}"); }
                        Log($"Column naming silent auto-apply failed: {ex.Message}");
                    }
                }
            }

            // Step 2: prompt for AUTO_APPLY=false rules that would change the name
            if (attrBoxed != null)
            {
                string afterAll = NamingValidationEngine.ApplyNamingStandards("Column", state.PhysicalName, attrBoxed, autoOnly: false);
                if (!string.Equals(afterAll, state.PhysicalName, StringComparison.Ordinal))
                {
                    var answer = MessageBox.Show(
                        $"Naming standard suggests changes for column '{state.TableName}.{state.PhysicalName}':\n\n" +
                        $"'{state.PhysicalName}' -> '{afterAll}'\n\n" +
                        $"Apply?",
                        "Naming Standard",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (answer == DialogResult.Yes)
                    {
                        int transId = _session.BeginNamedTransaction("ApplyManualColumnNaming");
                        try
                        {
                            attr.Properties("Physical_Name").Value = afterAll;
                            _session.CommitTransaction(transId);
                            Log($"Column naming applied (user confirmed): '{state.TableName}.{state.PhysicalName}' -> '{afterAll}'");
                            state.PhysicalName = afterAll;
                            return;
                        }
                        catch (Exception ex)
                        {
                            try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"ApplyManualColumnNaming rollback error: {rbEx.Message}"); }
                            Log($"Column naming manual apply failed: {ex.Message}");
                        }
                    }
                }
            }

            // Step 3: Validate remaining issues (warning popup for un-fixable rules)
            var results = NamingValidationEngine.ValidateObjectName("Column", state.PhysicalName, attrBoxed);
            foreach (var r in results.Where(r => !r.IsValid))
            {
                Log($"Naming standard violation ({r.RuleName}): {state.TableName}.{state.PhysicalName} — {r.ErrorMessage}");
                _pendingResults.Add(new CollectedValidationResult
                {
                    ValidationType = CollectedValidationResultType.NamingStandard,
                    TableName = state.TableName,
                    ColumnName = state.PhysicalName,
                    Message = r.ErrorMessage
                });
            }
        }

        /// <summary>
        /// Check if any Column UDP values changed and trigger dependency evaluation.
        /// </summary>
        private void CheckForColumnUdpValueChanges(dynamic attr, AttributeValidationSnapshot previousState, AttributeValidationSnapshot currentState)
        {
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;
            if (currentState.UdpValues == null || currentState.UdpValues.Count == 0) return;

            foreach (var kvp in currentState.UdpValues)
            {
                string oldValue = "";
                previousState.UdpValues?.TryGetValue(kvp.Key, out oldValue);
                oldValue = oldValue ?? "";

                if (kvp.Value != oldValue)
                {
                    Log($"Column UDP '{kvp.Key}' changed on '{currentState.TableName}.{currentState.PhysicalName}': '{oldValue}' -> '{kvp.Value}'");
                    _udpRuntimeService.HandleUdpValueChange(attr, kvp.Key, kvp.Value, "Column");

                    // Update previous state so re-read after dependency writes picks up new values
                    if (previousState.UdpValues == null)
                        previousState.UdpValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    previousState.UdpValues[kvp.Key] = kvp.Value;
                }
            }

            // Re-read after dependency writes to update snapshot
            var updatedValues = _udpRuntimeService.ReadUdpValues((object)attr, "Column");
            currentState.UdpValues = updatedValues;
        }

        /// <summary>
        /// Apply Column UDP defaults (with Glossary mapping if configured).
        /// Called when a new column is added or column name changes.
        /// </summary>
        /// <summary>
        /// Check Key_Group (Index) naming for a specific entity's indexes.
        /// Called during entity batch scan — no separate Collect needed.
        /// </summary>
        private void CheckEntityKeyGroups(dynamic entity, dynamic modelObjects, string tableName)
        {
            try
            {
                dynamic keyGroups = null;
                try { keyGroups = modelObjects.Collect(entity, "Key_Group"); }
                catch { return; }
                if (keyGroups == null) return;

                foreach (dynamic kg in keyGroups)
                {
                    if (kg == null) continue;
                    try
                    {
                        string kgId = kg.ObjectId?.ToString() ?? "";
                        string kgName = kg.Name ?? "";
                        if (string.IsNullOrEmpty(kgId) || string.IsNullOrEmpty(kgName)) continue;

                        bool isNew = !_keyGroupSnapshots.ContainsKey(kgId);
                        bool nameChanged = !isNew && _keyGroupSnapshots[kgId] != kgName;

                        if (isNew)
                        {
                            // New index — just snapshot, don't validate yet
                            // (user hasn't had a chance to set the name)
                            _keyGroupSnapshots[kgId] = kgName;
                        }
                        else if (nameChanged)
                        {
                            // Name changed — auto-apply prefix/suffix if configured
                            if (NamingValidationEngine.HasAutoApplyChanges("Index", kgName, (object)kg))
                            {
                                string newName = NamingValidationEngine.ApplyNamingStandards("Index", kgName, (object)kg);
                                var answer = MessageBox.Show(
                                    $"Naming standard requires changes for index '{tableName}.{kgName}':\n\n" +
                                    $"'{kgName}' → '{newName}'\n\nApply automatically?",
                                    "Naming Standard — Auto Apply",
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                                if (answer == DialogResult.Yes)
                                {
                                    int transId = _session.BeginNamedTransaction("ApplyIndexNaming");
                                    try
                                    {
                                        kg.Properties("Name").Value = newName;
                                        _session.CommitTransaction(transId);
                                        Log($"Index naming auto-applied: '{kgName}' → '{newName}'");
                                        kgName = newName;
                                    }
                                    catch (Exception ex)
                                    {
                                        try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"Index naming rollback error: {rbEx.Message}"); }
                                        Log($"Index naming auto-apply failed: {ex.Message}");
                                    }
                                }
                            }

                            // Validate (after auto-apply or if user declined)
                            List<NamingValidationResult> results = NamingValidationEngine.ValidateObjectName("Index", kgName, (object)kg);
                            foreach (var r in results.Where(r => !r.IsValid))
                            {
                                Log($"Index naming violation ({r.RuleName}): {tableName}.{kgName} — {r.ErrorMessage}");
                                _pendingResults.Add(new CollectedValidationResult
                                {
                                    ValidationType = CollectedValidationResultType.NamingStandard,
                                    TableName = tableName,
                                    ColumnName = kgName,
                                    Message = $"[Index] {r.ErrorMessage}"
                                });
                            }

                            _keyGroupSnapshots[kgId] = kgName;
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CheckEntityKeyGroups item error: {ex.Message}"); }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CheckEntityKeyGroups error: {ex.Message}"); }
        }

        private void ApplyColumnUdpDefaults(dynamic attr, string columnPhysicalName)
        {
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;
            if (string.IsNullOrEmpty(columnPhysicalName) ||
                columnPhysicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                columnPhysicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase) ||
                columnPhysicalName.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                _udpRuntimeService.ApplyDefaults(attr, "Column", columnPhysicalName);
            }
            catch (Exception ex)
            {
                Log($"Column UDP defaults error for '{columnPhysicalName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Apply glossary-mapped UDP values dynamically to a column attribute.
        /// Target UDP names come from DG_TABLE_MAPPING_COLUMN configuration.
        /// </summary>
        /// <summary>
        /// Apply glossary-mapped values to a column attribute.
        /// Target fields may have prefixes from admin: [UDP], [Erwin Property], [DB Property]
        /// Prefix-less targets are treated as UDPs for backward compatibility.
        /// </summary>
        private void ApplyGlossaryUdpValues(dynamic attr, Dictionary<string, string> udpValues, string columnName)
        {
            try
            {
                int transId = _session.BeginNamedTransaction("ApplyGlossaryUdpValues");
                try
                {
                    var glossary = GlossaryService.Instance;
                    foreach (var kvp in udpValues)
                    {
                        if (string.IsNullOrEmpty(kvp.Value)) continue;

                        string targetField = kvp.Key;
                        string targetType = glossary.GetTargetType(targetField);

                        switch (targetType?.ToUpper())
                        {
                            case "ERWIN_PROPERTY":
                                string erwinPropName = MapPropertyCodeToErwin(targetField);
                                TrySetProperty(attr, erwinPropName, kvp.Value);
                                break;

                            case "UDP":
                                TrySetUdp(attr, targetField, kvp.Value);
                                break;

                            case "DB_PROPERTY":
                                Log($"Glossary: Skipping DB property '{targetField}' for '{columnName}'");
                                break;

                            default:
                                // No type info (backward compat) — treat as UDP
                                TrySetUdp(attr, targetField, kvp.Value);
                                break;
                        }
                    }

                    _session.CommitTransaction(transId);
                    Log($"Glossary values applied for '{columnName}': {udpValues.Count} mapping(s)");
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"ApplyGlossaryUdpValues: Rollback failed: {rbEx.Message}"); }
                    Log($"Error applying glossary values: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"ApplyGlossaryUdpValues error: {ex.Message}");
            }
        }

        /// <summary>
        /// Map admin PROPERTY_CODE to erwin SCAPI property name.
        /// Admin stores uppercase codes, erwin uses PascalCase/specific names.
        /// </summary>
        private string MapPropertyCodeToErwin(string propertyCode)
        {
            switch (propertyCode?.ToUpper())
            {
                case "PHYSICAL_DATA_TYPE": return "Physical_Data_Type";
                case "COMMENTS": return "Definition";
                case "COMMENT": return "Definition";
                case "DEFINITION": return "Definition";
                case "NULL_OPTION_TYPE": return "Null_Option_Type";
                case "PHYSICAL_NAME": return "Physical_Name";
                default: return propertyCode;  // Pass through as-is
            }
        }

        private void TrySetProperty(dynamic attr, string propertyName, string value)
        {
            try
            {
                attr.Properties(propertyName).Value = value;
                Log($"Set {propertyName} to '{value}' from glossary");
            }
            catch (Exception ex)
            {
                Log($"Error setting {propertyName}: {ex.Message}");
            }
        }

        private void TrySetUdp(dynamic attr, string udpName, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            string[] formats = {
                $"Attribute.Physical.{udpName}",
                udpName,
                $"Attribute.{udpName}",
                $"UDP.{udpName}"
            };

            foreach (var format in formats)
            {
                try
                {
                    attr.Properties(format).Value = value;
                    Log($"Set {udpName} to '{value}' using '{format}'");
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TrySetUdp: Format '{format}' failed: {ex.Message}");
                }
            }

            Log($"Could not set {udpName} - no valid property format found");
        }

        private void ValidateDomain(dynamic attr, AttributeValidationSnapshot state, string oldDomainValue)
        {
            if (_validationSuspended) return;
            // Ensure DomainDefService is loaded
            if (!DomainDefService.Instance.IsLoaded)
            {
                DomainDefService.Instance.LoadDomainDefs();
            }

            if (!DomainDefService.Instance.IsLoaded)
            {
                return;
            }

            var validationResult = DomainDefService.Instance.ValidateColumnName(state.DomainParentValue, state.PhysicalName);

            if (validationResult.IsValid)
            {
                Log($"Domain validation passed: {state.TableName}.{state.PhysicalName} -> {state.DomainParentValue}");

                // Auto-set Description and Data Type from domain entry
                if (validationResult.DomainEntry != null)
                {
                    ApplyDomainProperties(attr, validationResult.DomainEntry);
                }
            }
            else
            {
                Log($"Domain validation FAILED: {state.TableName}.{state.PhysicalName} -> {state.DomainParentValue}");

                string message;
                string pattern = null;

                if (validationResult.DomainEntry == null)
                {
                    message = "Domain not found in DOMAIN_DEF table. Please add it to the database or select a different domain.";
                }
                else
                {
                    message = "Column name does not match domain pattern. Please rename the column or select a different domain.";
                    pattern = validationResult.DomainEntry.Regexp;
                }

                _pendingResults.Add(new CollectedValidationResult
                {
                    ValidationType = CollectedValidationResultType.Domain,
                    TableName = state.TableName,
                    ColumnName = state.PhysicalName,
                    DomainName = state.DomainParentValue,
                    Pattern = pattern,
                    Message = message
                });
            }
        }

        private void ApplyDomainProperties(dynamic attr, DomainDefEntry domainEntry)
        {
            try
            {
                bool hasDescription = !string.IsNullOrEmpty(domainEntry.Description);
                bool hasDataType = !string.IsNullOrEmpty(domainEntry.DataType);

                if (!hasDescription && !hasDataType) return;

                int transId = _session.BeginNamedTransaction("ApplyDomainProperties");
                try
                {
                    if (hasDescription)
                    {
                        try
                        {
                            attr.Properties("Definition").Value = domainEntry.Description;
                            Log($"Set Definition to '{domainEntry.Description}'");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error setting Definition: {ex.Message}");
                        }
                    }

                    if (hasDataType)
                    {
                        try
                        {
                            attr.Properties("Physical_Data_Type").Value = domainEntry.DataType;
                            Log($"Set Physical_Data_Type to '{domainEntry.DataType}'");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error setting Physical_Data_Type: {ex.Message}");
                        }
                    }

                    _session.CommitTransaction(transId);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"ApplyDomainProperties: Rollback failed: {rbEx.Message}"); }
                    Log($"Error applying domain properties: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"ApplyDomainProperties error: {ex.Message}");
            }
        }

        #endregion

        #region Popup Display

        private void ShowConsolidatedPopup()
        {
            if (_pendingResults.Count == 0 || _popupVisible) return;
            // Suspend flag check: From-DB pipeline gibi long-running ops
            // sirasinda popup spawn etme. Pipeline finish'inde resume
            // edilince fresh snapshot alindiktan sonra normal flow.
            if (_validationSuspended)
            {
                _pendingResults.Clear();
                return;
            }

            _popupVisible = true;
            try
            {
                var sb = new StringBuilder();

                var domainResults = _pendingResults.FindAll(r => r.ValidationType == CollectedValidationResultType.Domain);
                var glossaryResults = _pendingResults.FindAll(r => r.ValidationType == CollectedValidationResultType.Glossary);

                if (domainResults.Count > 0)
                {
                    sb.AppendLine("=== DOMAIN VALIDATION ===");
                    sb.AppendLine();
                    foreach (var result in domainResults)
                    {
                        sb.AppendLine($"Table: {result.TableName}");
                        sb.AppendLine($"Column: {result.ColumnName}");
                        if (!string.IsNullOrEmpty(result.DomainName))
                        {
                            sb.AppendLine($"Domain: {result.DomainName}");
                        }
                        if (!string.IsNullOrEmpty(result.Pattern))
                        {
                            sb.AppendLine($"Pattern: {result.Pattern}");
                        }
                        sb.AppendLine($"Issue: {result.Message}");
                        sb.AppendLine();
                    }
                }

                if (glossaryResults.Count > 0)
                {
                    if (domainResults.Count > 0)
                    {
                        sb.AppendLine("---");
                        sb.AppendLine();
                    }
                    sb.AppendLine("=== GLOSSARY VALIDATION ===");
                    sb.AppendLine();
                    foreach (var result in glossaryResults)
                    {
                        sb.AppendLine($"Table: {result.TableName}");
                        sb.AppendLine($"Column: {result.ColumnName}");
                        sb.AppendLine($"Issue: {result.Message}");
                        sb.AppendLine();
                    }
                }

                var namingResults = _pendingResults.FindAll(r => r.ValidationType == CollectedValidationResultType.NamingStandard);

                if (namingResults.Count > 0)
                {
                    if (domainResults.Count > 0 || glossaryResults.Count > 0)
                    {
                        sb.AppendLine("---");
                        sb.AppendLine();
                    }
                    sb.AppendLine("=== NAMING STANDARD VALIDATION ===");
                    sb.AppendLine();
                    foreach (var result in namingResults)
                    {
                        sb.AppendLine($"Table: {result.TableName}");
                        sb.AppendLine($"Column: {result.ColumnName}");
                        sb.AppendLine($"Issue: {result.Message}");
                        sb.AppendLine();
                    }
                }

                int totalErrors = _pendingResults.Count;
                sb.AppendLine($"Total: {totalErrors} validation error(s)");

                string title = "Validation Warnings";
                if (domainResults.Count > 0 && glossaryResults.Count == 0 && namingResults.Count == 0)
                {
                    title = "Domain Validation Warning";
                }
                else if (glossaryResults.Count > 0 && domainResults.Count == 0 && namingResults.Count == 0)
                {
                    title = "Glossary Validation Warning";
                }
                else if (namingResults.Count > 0 && domainResults.Count == 0 && glossaryResults.Count == 0)
                {
                    title = "Naming Standard Warning";
                }

                MessageBox.Show(
                    sb.ToString(),
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                // Dispatch based on editor state at popup dismissal:
                //   - Editor still open (_activeColumnEditorTable set) -> rename to
                //     "PLEASE CHANGE IT" so the user sees a placeholder in the live
                //     editor and can fix it. WindowMonitor's close handler will
                //     delete the placeholder when the editor finally closes.
                //   - Editor closed (_activeColumnEditorTable null because the
                //     WindowMonitor close-transition cleared it during the popup) ->
                //     delete directly; there's no editor open to show a placeholder.
                if (string.IsNullOrEmpty(_activeColumnEditorTable))
                {
                    DeleteInvalidGlossaryColumns(glossaryResults);
                }
                else
                {
                    RenameInvalidGlossaryColumns(glossaryResults);
                }
            }
            finally
            {
                _popupVisible = false;
                _pendingResults.Clear();
            }
        }

        /// <summary>
        /// In-editor placeholder path: when the user dismisses the validation popup
        /// while the Column Editor is still open, rename the offending columns to
        /// "PLEASE CHANGE IT" so the placeholder is visible in the live editor and
        /// the user can fix the name in place. WindowMonitor's scoped DeletePleaseChange
        /// removes the placeholder when the editor finally closes.
        /// Snapshot post-commit re-read is required because erwin enforces sibling-
        /// unique attribute names and may auto-rename collisions to PLEASE_CHANGE_IT__NNN.
        /// </summary>
        private void RenameInvalidGlossaryColumns(List<CollectedValidationResult> glossaryResults)
        {
            if (glossaryResults.Count == 0) return;

            int transId;
            try { transId = _session.BeginNamedTransaction("RenameInvalidColumns"); }
            catch (Exception ex) { Log($"RenameInvalidGlossary: BeginTransaction failed: {ex.Message}"); return; }

            try
            {
                foreach (var result in glossaryResults)
                {
                    if (result.Attribute == null) continue;
                    try
                    {
                        // Both logical and physical name must be set, otherwise
                        // CheckForChanges reads stale Physical_Name and loops.
                        result.Attribute.Properties("Name").Value = "PLEASE CHANGE IT";
                        try { result.Attribute.Properties("Physical_Name").Value = "PLEASE CHANGE IT"; }
                        catch (Exception phEx) { Log($"RenameInvalidGlossary: Failed to set Physical_Name: {phEx.Message}"); }
                        Log($"Renamed column {result.TableName}.{result.ColumnName} to 'PLEASE CHANGE IT'");
                    }
                    catch (Exception ex)
                    {
                        Log($"Error renaming column {result.ColumnName}: {ex.Message}");
                    }
                }

                _session.CommitTransaction(transId);

                // Sibling-collision read-back: if two siblings both got 'PLEASE CHANGE IT',
                // erwin auto-renames the second to e.g. 'PLEASE_CHANGE_IT__792'. Without
                // updating the snapshot to the actual name, the next monitor tick sees a
                // diff and re-fires ValidateGlossary, which fails again because '__792'
                // isn't in the glossary - infinite loop. Read back the actual values.
                foreach (var result in glossaryResults)
                {
                    if (result.Attribute == null) continue;
                    if (string.IsNullOrEmpty(result.ObjectId)) continue;
                    if (!_attributeSnapshots.ContainsKey(result.ObjectId)) continue;

                    string actualName = "PLEASE CHANGE IT";
                    string actualPhys = "PLEASE CHANGE IT";
                    try { actualName = result.Attribute.Name?.ToString() ?? actualName; }
                    catch (Exception ex) { Log($"RenameInvalidGlossary: read-back Name error: {ex.Message}"); }
                    try
                    {
                        string val = result.Attribute.Properties("Physical_Name").Value?.ToString();
                        if (!string.IsNullOrEmpty(val) && !val.StartsWith("%"))
                            actualPhys = val;
                    }
                    catch (Exception ex) { Log($"RenameInvalidGlossary: read-back Physical_Name error: {ex.Message}"); }

                    _attributeSnapshots[result.ObjectId].PhysicalName = actualPhys;
                    _attributeSnapshots[result.ObjectId].AttributeName = actualName;

                    if (!actualPhys.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
                        Log($"  erwin auto-renamed sibling collision: {result.TableName}.{result.ColumnName} -> {actualPhys}");
                }
            }
            catch (Exception ex)
            {
                try { _session.RollbackTransaction(transId); }
                catch (Exception rbEx) { Log($"RenameInvalidGlossary: Rollback failed: {rbEx.Message}"); }
                Log($"RenameInvalidGlossaryColumns transaction error: {ex.Message}");
            }
        }

        /// <summary>
        /// Close-time direct-delete path: when the user dismisses the validation popup
        /// AFTER the Column Editor has already closed (and there is no editor to show
        /// a PLEASE CHANGE IT placeholder in), simply remove the offending columns.
        /// One transaction, no rename intermediary, no race with WindowMonitor cleanup.
        /// </summary>
        private void DeleteInvalidGlossaryColumns(List<CollectedValidationResult> glossaryResults)
        {
            if (glossaryResults.Count == 0) return;

            int transId;
            try { transId = _session.BeginNamedTransaction("DeleteInvalidGlossaryColumns"); }
            catch (Exception ex) { Log($"DeleteInvalidGlossary: BeginTransaction failed: {ex.Message}"); return; }

            try
            {
                dynamic mo = _session.ModelObjects;
                int deletedCount = 0;
                foreach (var result in glossaryResults)
                {
                    if (result.Attribute == null) continue;
                    try
                    {
                        mo.Remove(result.Attribute);
                        if (!string.IsNullOrEmpty(result.ObjectId))
                            _attributeSnapshots.Remove(result.ObjectId);
                        Log($"Deleted invalid column: {result.TableName}.{result.ColumnName}");
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        Log($"DeleteInvalidGlossary: failed to delete '{result.TableName}.{result.ColumnName}': {ex.Message}");
                    }
                }
                _session.CommitTransaction(transId);
                if (deletedCount > 0)
                    Log($"DeleteInvalidGlossary: removed {deletedCount} invalid column(s)");
            }
            catch (Exception ex)
            {
                try { _session.RollbackTransaction(transId); }
                catch (Exception rbEx) { Log($"DeleteInvalidGlossary: Rollback failed: {rbEx.Message}"); }
                Log($"DeleteInvalidGlossaryColumns transaction error: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        private AttributeValidationSnapshot CreateSnapshot(dynamic attr, string tableName, dynamic modelObjects)
        {
            string objectId = "";
            string attrName = "";
            string physicalName = "";
            string domainParentValue = "";

            try { objectId = attr.ObjectId?.ToString() ?? ""; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CreateSnapshot: ObjectId error: {ex.Message}"); }
            try { attrName = attr.Name ?? ""; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CreateSnapshot: Name error: {ex.Message}"); }
            try
            {
                string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                physicalName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : attrName;
            }
            catch (Exception ex) { physicalName = attrName; System.Diagnostics.Debug.WriteLine($"CreateSnapshot: Physical_Name error: {ex.Message}"); }

            domainParentValue = GetDomainParentValue(attr, modelObjects);

            // Physical_Data_Type is read on every snapshot so the diff path can compare
            // current vs previous and decide whether a term-type policy must intervene.
            // Read failures keep the previous behaviour (no policy enforcement) by leaving
            // the field empty.
            string physicalDataType = "";
            try { physicalDataType = attr.Properties("Physical_Data_Type").Value?.ToString() ?? ""; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CreateSnapshot: Physical_Data_Type error: {ex.Message}"); }

            var snapshot = new AttributeValidationSnapshot
            {
                ObjectId = objectId,
                AttributeName = attrName,
                PhysicalName = physicalName,
                DomainParentValue = domainParentValue,
                TableName = tableName,
                PhysicalDataType = physicalDataType
            };

            // Column UDP values are read lazily in ProcessAttributeChanges only when needed
            // (not in every snapshot — too expensive for 100+ attributes per tick).
            // TermTypeCanonical is also intentionally NOT resolved here; it's set when a
            // glossary entry is applied to the column (ApplyGlossaryUdpValues path) and
            // refreshed when Physical_Name changes (column renamed to a different glossary term).

            return snapshot;
        }

        private string GetTableName(dynamic entity)
        {
            string tableName = "";
            string entityName = "";

            try { entityName = entity.Name ?? ""; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetTableName: Name error: {ex.Message}"); }
            try
            {
                string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
            }
            catch (Exception ex) { tableName = entityName; System.Diagnostics.Debug.WriteLine($"GetTableName: Physical_Name error: {ex.Message}"); }

            return tableName;
        }

        private string GetDomainParentValue(dynamic attr, dynamic modelObjects)
        {
            try
            {
                var parentDomainRefProp = attr.Properties("Parent_Domain_Ref");
                if (parentDomainRefProp != null)
                {
                    string refValue = parentDomainRefProp.Value?.ToString() ?? "";

                    if (!string.IsNullOrEmpty(refValue) && refValue != "Blob")
                    {
                        // If it looks like an ObjectId, try to resolve it
                        if (refValue.StartsWith("{") && refValue.Contains("+"))
                        {
                            if (_domainCache.TryGetValue(refValue, out string domainName))
                            {
                                return domainName;
                            }

                            // Cache miss — resolve directly from model
                            try
                            {
                                dynamic domainObj = modelObjects.Item(refValue);
                                if (domainObj != null)
                                {
                                    string name = domainObj.Name?.ToString() ?? "";
                                    if (!string.IsNullOrEmpty(name))
                                    {
                                        _domainCache[refValue] = name;
                                        return name;
                                    }
                                }
                            }
                            catch { }

                            Log($"Domain ref not resolved: {refValue}");
                        }
                        else
                        {
                            return refValue;
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetDomainParentValue error: {ex.Message}"); }

            return "";
        }

        private void BuildDomainCache(dynamic modelObjects, dynamic root)
        {
            try
            {
                dynamic allDomains = modelObjects.Collect(root, "Domain");
                if (allDomains == null) return;

                foreach (dynamic domain in allDomains)
                {
                    if (domain == null) continue;

                    try
                    {
                        string objectId = domain.ObjectId?.ToString() ?? "";
                        string name = domain.Name?.ToString() ?? "";

                        if (!string.IsNullOrEmpty(objectId) && !string.IsNullOrEmpty(name))
                        {
                            _domainCache[objectId] = name;
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildDomainCache: Domain entry error: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log($"BuildDomainCache error: {ex.Message}"); }
        }

        /// <summary>
        /// Gets all predefined column names (used to skip glossary validation on auto-added columns).
        /// </summary>
        private HashSet<string> GetPredefinedColumnNames(dynamic entity)
        {
            try
            {
                if (!PredefinedColumnService.Instance.IsLoaded)
                    PredefinedColumnService.Instance.LoadPredefinedColumns();

                var allPredefined = PredefinedColumnService.Instance.GetAll();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in allPredefined)
                {
                    names.Add(col.Name);
                }

                return names.Count > 0 ? names : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetPredefinedColumnNames error: {ex.Message}");
                return null;
            }
        }

        private bool IsValidDomain(string domainValue)
        {
            return !string.IsNullOrEmpty(domainValue) &&
                   domainValue != "(SELECT)" &&
                   domainValue != "<default>" &&
                   domainValue != "Blob";
        }

        private static void ReleaseCom(object comObject)
        {
            if (comObject != null)
            {
                try { Marshal.ReleaseComObject(comObject); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReleaseCom error: {ex.Message}"); }
            }
        }

        private void Log(string message)
        {
            if (_disposed) return;
            try { OnLog?.Invoke(message); } catch { }
            System.Diagnostics.Debug.WriteLine(message);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
            _monitorTimer?.Dispose();
            _windowMonitorTimer?.Dispose();
        }

        #endregion

        #region Inner Classes

        private class AttributeValidationSnapshot
        {
            public string ObjectId { get; set; }
            public string AttributeName { get; set; }
            public string PhysicalName { get; set; }
            public string DomainParentValue { get; set; }
            public string TableName { get; set; }
            /// <summary>Column UDP values for dependency change detection</summary>
            public Dictionary<string, string> UdpValues { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Last seen Physical_Data_Type. Used to detect user edits and to revert the
            /// disallowed portion when a term-type policy applies.
            /// </summary>
            public string PhysicalDataType { get; set; }

            /// <summary>
            /// Canonical term-type concept (BUSINESS_TERM / AMORPH_DATA_TYPE /
            /// AMORPH_DATA_LENGTH / AMORPH) resolved from the Glossary row. Null means
            /// "no term-type constraint" — the column isn't in the glossary, the column
            /// is in the glossary but its TERM_TYPE value is empty/unmapped, or admin
            /// hasn't configured the term-type column. Snapshotted at glossary apply time
            /// so it survives later UDP cascades; refreshed on Physical_Name changes.
            /// </summary>
            public string TermTypeCanonical { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Represents a collected validation result
    /// </summary>
    public class CollectedValidationResult
    {
        public CollectedValidationResultType ValidationType { get; set; }
        public string TableName { get; set; }
        public string ColumnName { get; set; }
        public string Message { get; set; }
        public string DomainName { get; set; }
        public string Pattern { get; set; }
        public dynamic Attribute { get; set; }
        public string ObjectId { get; set; }
    }

    /// <summary>
    /// Type of validation
    /// </summary>
    public enum CollectedValidationResultType
    {
        Glossary,
        Domain,
        NamingStandard
    }
}
