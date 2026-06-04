using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Forms;

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

        // Entity Editor (Table Properties dialog) lifecycle + live UDP
        // snapshot. Title shape is "SQL Server Table 'TableName' Editor" -
        // same #32770 dialog class as the Column Editor but without
        // "Column 'X'" in the title. While the editor is open we read the
        // entity's UDP values every tick and re-run the scoped naming check
        // the moment any tracked UDP changes - mirrors the Glossary flow
        // that fires on Physical_Name change rather than on editor close.
        // Snapshot is keyed on UDP name (case-insensitive); cleared on
        // editor close so the next open re-baselines.
        private bool _entityEditorWasOpen;
        private string _activeEntityEditorTable;
        // _entityEditorUdpSnapshot field removed 2026-05-22 along with the
        // Table-UDP delta enforcement that was its only consumer.
        // Reentrancy guard. MessageBox.Show pumps the message loop while
        // modal, which fires this same WindowMonitorTimer again. Without
        // this flag the second tick re-enters RunScopedTableNamingCheck
        // (snapshot still old) and stacks another popup, then a third,
        // ad infinitum. Verified 2026-05-07: 30+ nested popups in one
        // session of clicking the TABLE_TYPE combo to "LOG".
        private bool _scopedCheckInProgress;
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

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // Snapshot of all attributes
        private Dictionary<string, AttributeValidationSnapshot> _attributeSnapshots;

        // _udpEditorWasOpen field removed 2026-05-22 along with the
        // EnsureLockedTableDefinitionsExist call it gated.

        // Session-level Required-popup dismissal for Column-level rules
        // (mirrors TableTypeMonitorService._dismissedRequiredKeys). Key is
        // "{attrObjectId}|{propertyCode}". Populated when the user cancels
        // a Required popup in Update mode; consulted at the start of every
        // Required loop to suppress the same popup on subsequent ticks.
        // Cleared per-key when the user later supplies a valid value via
        // the popup's OK path. Not persisted - admin intent for Required
        // rules is "always nag", so the suppression is a per-session
        // courtesy only.
        private readonly HashSet<string> _dismissedRequiredColumnKeys = new HashSet<string>(StringComparer.Ordinal);

        // Scoped entity cache (2026-05-24): when the locked-column rename
        // watch runs, it has already walked the entity collection and
        // bound `entity` for the matching table. Without this cache, the
        // downstream EnforceLockedColumnRename -> ResolveEntityByName
        // call would do a SECOND full walk for the SAME table on every
        // locked rename. Set inside ScanForLockedColumnRenames before
        // ProcessAttributeChanges; cleared in finally. Strictly a hot-
        // path cache - never read outside the scoped call frame.
        private string _scanContextTableName;
        private dynamic _scanContextEntity;

        // Locked-column dialog suspension flag (2026-05-25). True while a
        // LockedColumnDialog is on screen. Heartbeat (MonitorTimer) and
        // window-state (WindowMonitorTimer) ticks check this and early-
        // return so their SCAPI walks do not run inside the dialog's
        // nested message pump - those walks were blocking the dialog
        // from processing OK clicks for several seconds. User complaint
        // 2026-05-24 ("3-4 sn sonra tıklamam gerçekleşti"). Strictly
        // per-process, not persisted.
        private volatile bool _lockedDialogShowing;

        // De-duplication for locked-column dialogs (2026-05-25). Multiple
        // detection paths (close-edge re-evaluate, heartbeat attrsShrunk
        // restore, scan rename) can race for the same column-action pair
        // on the same tick / dialog cycle. Add the (entity|column|action)
        // key when we enqueue a deferred dialog, remove it when the
        // apply phase completes. While the key is pending, parallel
        // detections short-circuit so the user does not see two dialogs
        // for the same edit.
        private readonly HashSet<string> _pendingLockedDialogKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase-2E (2026-05-12): diagram-side heartbeat for add detection when no
        // editor dialog is open. Pure count snapshots, no per-property reads -
        // the eliminated Phase-2D full-walk was expensive because it read every
        // attribute's full property set every tick. Count-only walk on a 280-entity
        // model is ~5 ms; we run it once per second (every 4 ticks) so amortized
        // cost is negligible. Covers: diagram inline add, Model Explorer "New
        // Column" / "New Table", reverse engineer batches, anything that mutates
        // the model from outside the Column/Entity Editor dialogs.
        //
        // Snapshot key: entity ObjectId (string form). The first cut used
        // Physical_Name and broke when an entity's name resolution changed
        // between ticks (e.g. erwin's default "%Entity1" placeholder converts
        // to a real Physical_Name on first rename, which made the SAME entity
        // look "new" on the next heartbeat - log "silent baselined new/unseen
        // entity 'TEST_DATA_TAB_1'" with entities=0 delta, 2026-05-12). ObjectId
        // is invariant for the lifetime of the entity so it sidesteps the race.
        private long _lastTotalAttributeCount = -1;
        private long _lastTotalEntityCount = -1;
        private readonly Dictionary<string, int> _entityAttrCountSnapshot =
            new Dictionary<string, int>(StringComparer.Ordinal); // ObjectId key, case-sensitive
        private readonly HashSet<string> _entityIdSnapshot =
            new HashSet<string>(StringComparer.Ordinal);
        // Per-entity attribute ObjectId set, populated only on count-delta ticks.
        // SCAPI ObjectId is a GUID-shaped string (NOT a numeric SC_OBJID; the
        // 2026-05-12 log proved long.TryParse fails on every attribute), so the
        // earlier "strip the highest numeric id" trick was broken. With set
        // semantics we instead compute current.Except(previous) to learn EXACTLY
        // which attrs are new on a given tick, regardless of id encoding.
        private readonly Dictionary<string, HashSet<string>> _entityAttrIdSnapshot =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Per-entity set of attribute ObjectIds whose Physical_Name is still a
        // placeholder (empty / '<default>' / 'PLEASE CHANGE IT'). Model Explorer
        // "New Column" creates the attribute with name '<default>' first and
        // then the user types the real name as a separate edit - this rename
        // does NOT change the attribute count, so the count-delta heartbeat
        // never fires for it. We track the placeholder attrs here so the
        // heartbeat keeps rescanning their owner entity until the name resolves
        // (whereupon CheckEntityForChanges' fingerprint diff trips
        // ProcessAttributeChanges -> ValidateGlossary). Verified 2026-05-12 log
        // line 18:14:35: 'physName=<default> isNew=True ... ValidateGlossary
        // skipped' produced zero validation result on a Model Explorer flow.
        private readonly Dictionary<string, HashSet<string>> _pendingNamedAttrs =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Timestamp of when each pending attribute was first seen, used to
        // tell apart "user is still typing" from "user committed the
        // placeholder and walked away". The inline-edit close detector in
        // WindowMonitorTimer_Tick uses this together with the focused-window
        // class to decide whether to force-validate. See ValidateCommittedPendingAttrs.
        private readonly Dictionary<string, DateTime> _pendingAttrAddedAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private int _heartbeatTickCounter;
        private const int HeartbeatEveryNTicks = 4; // 250 ms * 4 = 1 s

        // Inline-edit state machine for the Model Explorer "New Column" and
        // diagram in-place-edit flows. Both spawn a Win32 Edit control while
        // the user is typing; when the user commits (Enter/Tab/click-away)
        // the Edit window is destroyed. We watch the transition open->closed
        // to fire ValidateCommittedPendingAttrs: any attr still wearing its
        // placeholder name at that moment was committed as-is and needs the
        // glossary popup (which ValidateGlossary normally skips on placeholders).
        private bool _wasInlineEditOpen;

        // Phase-2G entity-level mirror of Phase-2E/2F (2026-05-13):
        // - _entityDisplayNameSnapshot lets us spot Entity renames at heartbeat
        //   time (count delta won't fire on a pure rename - Physical_Name
        //   changes but Attribute/Entity counts stay constant).
        // - _pendingNamedEntities tracks newly-seen entities still wearing a
        //   placeholder Physical_Name. They are held back from
        //   RunScopedTableNamingCheck until inline-edit close so we don't
        //   pop a "name does not match naming standard" warning while the
        //   user is still typing.
        private readonly Dictionary<string, string> _entityDisplayNameSnapshot =
            new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>
        /// Table naming-standard checks that were deferred because an
        /// edit dialog (Column Editor / Entity Editor) was open at the
        /// time the rename / new-entity event fired. Flushed when the
        /// next editor-close transition is observed so the Required popup
        /// surfaces AFTER the user's gesture, not in the middle of it.
        /// </summary>
        /// <summary>
        /// Naming-check entries deferred because an edit dialog was open
        /// when the heartbeat tried to fire them. The bool carries the
        /// original isNew flag (2026-05-24) so a deferred new-entity
        /// check still gets the "Discard New Table" Cancel button when
        /// it eventually surfaces. Equality on the entity name alone
        /// (via the comparer) keeps the set deduplicated across multiple
        /// ticks that observed the same pending entity.
        /// </summary>
        private readonly HashSet<(string Name, bool IsNew)> _pendingTableNamingChecks =
            new HashSet<(string, bool)>(new PendingNamingCheckComparer());

        private sealed class PendingNamingCheckComparer : IEqualityComparer<(string Name, bool IsNew)>
        {
            public bool Equals((string Name, bool IsNew) x, (string Name, bool IsNew) y) =>
                string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            public int GetHashCode((string Name, bool IsNew) obj) =>
                obj.Name == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
        }

        /// <summary>
        /// Edit-session baseline (2026-05-17) for naming-rule-watched
        /// properties on the entity currently in focus. Captured when
        /// the Column Editor or Entity Editor opens; diffed and cleared
        /// when the same editor closes. Two separate buckets because
        /// the user can have both editors open in sequence and each
        /// "open" event must establish its own baseline.
        /// </summary>
        private Dictionary<string, string> _columnEditorEntityBaseline;
        private string _columnEditorEntityName;
        private Dictionary<string, string> _entityEditorBaseline;
        private string _entityEditorName;

        private readonly HashSet<string> _pendingNamedEntities =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Entity IDs that are currently inside a placeholder-commit
        /// gesture (= ValidateCommittedPendingAttrs has lifted them out
        /// of <see cref="_pendingNamedEntities"/> and is about to drain
        /// their isNew=true scoped naming check). Kept SEPARATE from
        /// <see cref="_pendingNamedEntities"/> because the Required-UDP
        /// modal pump may fire ScanForRenamesEventDriven INSIDE
        /// ValidateCommittedPendingAttrs - at that point
        /// <see cref="_pendingNamedEntities"/> has already been
        /// cleared for the entity (line ~2670), so the rename-detect
        /// branch's <c>wasPending</c> bool reads false and the
        /// deferred scoped check would otherwise lose the isNew=true
        /// signal of the creation gesture.
        /// <para>
        /// 2026-05-31 history: was first wired to widen
        /// <c>MatchesApplyOn</c> via an orthogonal <c>creationGesture</c>
        /// flag (commits 2aca8cb + c50a5be). User rejected that
        /// semantic - ApplyOn=Update rules MUST NEVER fire on a new
        /// entity. The widening was reverted; the bridge now serves
        /// the simpler and correct purpose of PROPAGATING
        /// <c>isNew=true</c> from the placeholder commit into the
        /// nested rename hook, so the strict ApplyOn gate naturally
        /// filters Update rules out (engine semantic stays untouched).
        /// </para>
        /// Cleared inside ValidateCommittedPendingAttrs.finally after
        /// the drain completes.
        /// </summary>
        private readonly HashSet<string> _creationGestureEntityIds =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// True when the column's Physical_Name is still in erwin's
        /// placeholder state (just created, no user-typed name yet).
        /// Mirrors the early-return guard in ValidateGlossary so the
        /// pending tracker uses the same criterion.
        /// </summary>
        private static bool IsPlaceholderColumnName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name.Equals("<default>", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("<default>", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("PLEASE_CHANGE_IT", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// True when the entity's Physical_Name is one of erwin's auto-assigned
        /// defaults that the user has not yet renamed. Empirically these are:
        ///   "E/284", "E/285", ...     diagram "New Entity" placeholder
        ///   "<default>"                Model Explorer "New Entity" placeholder
        ///   ""                         pre-commit state
        /// We hold these in _pendingNamedEntities and skip RunScopedTableNamingCheck
        /// until the user commits a real name, otherwise the naming standard
        /// regex either flags the default as a violation (annoying popup the
        /// user will fix in the same gesture) or accepts the default and the
        /// user never sees their real name validated.
        /// </summary>
        private static bool IsPlaceholderEntityName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name.Equals("<default>", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("<default>", StringComparison.OrdinalIgnoreCase)) return true;
            // PLEASE_CHANGE_IT / PLEASE CHANGE IT - the auto-applied placeholder
            // we write when an AUTO_APPLY=true naming rule fails (Phase-2H).
            // Treat it as a placeholder so the next heartbeat tick doesn't see
            // it as a "rename" and re-queue another naming check, infinitely.
            if (name.StartsWith("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("PLEASE_CHANGE_IT", StringComparison.OrdinalIgnoreCase)) return true;
            // erwin's diagram-side auto name pattern: "E/<number>" (uppercase
            // E, forward slash, integer suffix). Verified 2026-05-13 log:
            // "[NAMING] newly seen entity 'E/284'" right after a diagram New
            // Entity click. Match conservatively (exact prefix + digits-only
            // tail) so user-typed names like "E/B Test" or "E/123_FOO" still
            // count as real.
            if (name.Length >= 3 && name[0] == 'E' && name[1] == '/')
            {
                bool allDigits = true;
                for (int i = 2; i < name.Length; i++)
                {
                    if (!char.IsDigit(name[i])) { allDigits = false; break; }
                }
                if (allDigits) return true;
            }
            return false;
        }

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
            // Inject the creation-gesture probe so the monitor's direct
            // ValidateNamingStandard re-runs (Required-input loop +
            // OnNewEntityDetected pipeline) can honour the same
            // placeholder-commit override that RunScopedTableNamingCheck
            // does. Without this the Update-only rules (rule#22 _PRM)
            // would fire on the post-Required-prompt re-run because the
            // monitor sees isNew=false there. Verified 2026-06-01.
            if (monitor != null)
                monitor.CreationGestureProbe = IsEntityInCreationGesture;
        }

        /// <summary>
        /// True when an entity with the given <paramref name="tableName"/>
        /// is currently inside a placeholder-commit gesture (its ObjectId
        /// is in <see cref="_creationGestureEntityIds"/>). Cheap when the
        /// bridge set is empty (one Count read). When the bridge has
        /// entries, walks the entity collection once to resolve
        /// name -> id and returns true on the first match. Returns false
        /// on any error so the gate stays strict outside the gesture.
        /// </summary>
        internal bool IsEntityInCreationGesture(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return false;
            if (_creationGestureEntityIds.Count == 0) return false;
            if (_session == null || _sessionLost) return false;

            dynamic mmObj = null, mmRoot = null, mmEntities = null;
            try
            {
                mmObj = _session.ModelObjects;
                mmRoot = mmObj?.Root;
                if (mmRoot == null) return false;
                mmEntities = mmObj.Collect(mmRoot, "Entity");
                if (mmEntities == null) return false;
                foreach (dynamic e in mmEntities)
                {
                    if (e == null) continue;
                    string nm = null;
                    try { nm = GetTableName(e); } catch { continue; }
                    if (!EntityNameMatchesTitle(nm, tableName)) continue;
                    string eid = null;
                    try { eid = e.ObjectId?.ToString(); } catch { break; }
                    return !string.IsNullOrEmpty(eid) && _creationGestureEntityIds.Contains(eid);
                }
                return false;
            }
            catch (Exception ex) { Log($"IsEntityInCreationGesture: probe threw {ex.GetType().Name}: {ex.Message}"); return false; }
            finally
            {
                ReleaseCom(mmEntities);
                ReleaseCom(mmRoot);
            }
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
                // Resolve erwin's REAL main frame (caption starts "erwin DM") via the
                // hang-proof finder, then read its title with a timeout-bounded
                // WM_GETTEXT. The old Process.GetProcessesByName("erwin")[0].MainWindowTitle
                // had two faults that froze erwin's UI thread (hang dump 2026-06-03,
                // this heartbeat path): (1) MainWindowHandle picks the first owner-less
                // top-level window, which after a version-compare teardown can be a
                // leftover visible #32770 dialog parked on a non-pumping worker thread;
                // (2) MainWindowTitle then does a synchronous GetWindowText to it,
                // blocking forever. GetErwinMainWindow now skips hung windows and
                // GetWindowTextNoHang can never block.
                IntPtr mainHwnd = Win32Helper.GetErwinMainWindow();
                if (mainHwnd == IntPtr.Zero) return "";

                string windowTitle = Win32Helper.GetWindowTextNoHang(mainHwnd);
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
            // 2026-05-25: while a locked-column dialog is up, skip the
            // entire heartbeat. SCAPI walks during the dialog's nested
            // message pump otherwise hog the UI thread and block OK
            // click processing (user-reported 3-4 sec freeze).
            if (_lockedDialogShowing) return;

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

                            if (!EntityNameMatchesTitle(nameForMatch, _activeColumnEditorTable))
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

                // Editor closed: diagram heartbeat (Phase-2E, 2026-05-12).
                // The Phase-2D blind spot (pure-diagram / Model Explorer add) is
                // closed by a count-only delta check every HeartbeatEveryNTicks
                // ticks. We deliberately do NOT read attribute property sets here -
                // the old 7.5 s freeze came from full-property walks. A count walk
                // on a 280-entity model is ~5 ms; only when a delta is detected
                // do we run the existing scoped (~30 ms) scan on the changed
                // entities. AddItem hook covers macro-level transaction commits
                // (model open, editor open) but inline diagram edits and Model
                // Explorer New Column / New Table do NOT route through it (D2
                // spike, 2026-05-12 - empirically verified that only 5 records
                // fire across 5 add scenarios). Count delta is the universal
                // sensor regardless of which UI path the user took.
                _heartbeatTickCounter++;
                if (_heartbeatTickCounter >= HeartbeatEveryNTicks)
                {
                    _heartbeatTickCounter = 0;
                    DiagramHeartbeatTick(modelObjects, root);
                }
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

        /// <summary>
        /// Phase-2E heartbeat (2026-05-12). Detects model mutations performed
        /// outside the Column/Entity Editor dialogs (diagram inline add, Model
        /// Explorer New Column, Model Explorer New Entity, RE batches, anything
        /// else that bypasses our editor-scoped scan path).
        ///
        /// Strategy:
        ///   1. Read TWO cheap totals: Attribute count and Entity count across
        ///      the whole model. SCAPI ICollection.Count is internal, no
        ///      property reads, so this is the single fastest delta signal.
        ///   2. If both totals match the previous tick AND the entity set is
        ///      identical, there is no change - bail.
        ///   3. Otherwise walk entities once, compute per-entity attribute
        ///      counts, diff against the saved per-entity snapshot, and run
        ///      the existing scoped validation path against any entity whose
        ///      count grew (added attribute) or which is newly present.
        ///   4. Refresh totals + snapshots so the next tick is back to the
        ///      cheap-early-exit path.
        ///
        /// The first call after StartMonitoring sees -1/-1 and silently
        /// populates the snapshot without validating (the model's existing
        /// columns are not "new" from the user's perspective; only changes
        /// after monitoring started count).
        /// </summary>
        private void DiagramHeartbeatTick(dynamic modelObjects, dynamic root)
        {
            long totalAttrs;
            long totalEntities;
            dynamic attrCollection = null;
            dynamic entityCollection = null;
            try
            {
                attrCollection = modelObjects.Collect(root, "Attribute");
                entityCollection = modelObjects.Collect(root, "Entity");
                if (attrCollection == null || entityCollection == null) return;
                totalAttrs = (long)attrCollection.Count;
                totalEntities = (long)entityCollection.Count;
            }
            catch (Exception ex)
            {
                Log($"DiagramHeartbeat: count read err: {ex.Message}");
                ReleaseCom(attrCollection);
                ReleaseCom(entityCollection);
                return;
            }

            // First tick after monitoring started: baseline silently.
            bool isFirstTick = (_lastTotalAttributeCount < 0 || _lastTotalEntityCount < 0);
            // Phase-2G (2026-05-13): the old count-only early-exit silently
            // dropped entity-name renames because a rename produces zero
            // count delta. Verified by 13:49:35 log: user renamed 'E/284' ->
            // '123' between two tablo-add events and the rename was only
            // detected when the SECOND tablo-add forced a walk (the rename
            // fire arrived ~2 minutes late, surfaced alongside the second
            // tablo's check). We now walk every heartbeat tick regardless of
            // count delta - the per-entity work is one Physical_Name read +
            // one Collect("Attribute").Count, ~140ms on a 280-entity model.
            // needsIdWalk (per-attribute ObjectId enumeration) still only
            // fires on count delta, so the expensive walk path is unchanged.
            //
            // The historical 7.5s freeze that motivated the early-exit came
            // from a different per-property scan, NOT from the count read.

            try
            {
                long delta = totalAttrs - _lastTotalAttributeCount;
                long entityDelta = totalEntities - _lastTotalEntityCount;
                if (!isFirstTick && (delta != 0 || entityDelta != 0))
                {
                    // 2026-05-24: stopped logging zero-delta ticks. They fire
                    // every second on every model and flooded the log file
                    // (~90% of lines were stable-tick noise). Only log when
                    // something actually changed.
                    Log($"DiagramHeartbeat: delta detected attrs={delta:+#;-#;0} entities={entityDelta:+#;-#;0} (was {_lastTotalAttributeCount}/{_lastTotalEntityCount}, now {totalAttrs}/{totalEntities})");
                }

                // Stable-tick early-exit (2026-05-17, evolved): when both
                // counts are unchanged AND nothing is pending, the walk
                // below produces zero useful output - rename detection
                // is now event-driven (ScanForRenamesEventDriven called
                // from inline-edit / editor close transitions), and
                // count-delta gates already handle new entities / new
                // attributes. The walk is therefore safe to SKIP
                // indefinitely on stable ticks; the previous "every 4th
                // tick anyway" throttle was a holdover from when rename
                // detection lived inside the walk.
                //
                // Pending sets force a walk because their drain depends on
                // the per-entity rename-diff that runs further down.
                if (!isFirstTick && delta == 0 && entityDelta == 0
                    && _pendingNamedEntities.Count == 0
                    && _pendingNamedAttrs.Count == 0)
                {
                    _lastTotalAttributeCount = totalAttrs;
                    _lastTotalEntityCount = totalEntities;
                    return;
                }

                var currentEntityIds = new HashSet<string>(StringComparer.Ordinal);
                // (entity dispatch, displayName, entityId, kind, newAttrIds)
                //   kind 'A' = attr count grew on a known entity (most common),
                //   kind 'N' = newly-seen entity.
                // newAttrIds: ObjectIds present now but absent from the previous
                // tick's per-entity id set. Used to strip exactly the right
                // entries from _attributeSnapshots so CheckEntityForChanges sees
                // them as new without false-positive spam on pre-existing attrs.
                var entitiesToRescan = new List<(dynamic entity, string name, string id, char kind, HashSet<string> newAttrIds)>();
                // Phase-2G (2026-05-13): table-name validation queue. Filled
                // by the entity-level diff inside the walk (newly-seen entity
                // with a non-placeholder name OR entity Physical_Name changed
                // since the last tick), drained after the walk so we never
                // call RunScopedTableNamingCheck while still iterating the
                // COM collection - it Collects("Entity") itself and the
                // re-entry can wedge the COM enumerator.
                //
                // The IsNew flag (2026-05-24) tells the downstream
                // ValidateNamingStandard whether this entity was just
                // created in THIS tick so the Required-popup chain uses
                // RequiredOperationMode.Create (Cancel button reads
                // "Discard New Table") instead of Update ("Revert Change").
                // Required-property prompts that fire from
                // OnNewEntityDetected already get Create mode; threading
                // the same flag here keeps the popup family consistent.
                var entitiesToNamingCheck = new List<(string name, bool isNew)>();

                foreach (dynamic entity in entityCollection)
                {
                    if (entity == null) continue;
                    string entityId;
                    try { entityId = entity.ObjectId?.ToString() ?? ""; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(entityId)) continue;

                    string displayName;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        displayName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? entityId);
                    }
                    catch { try { displayName = entity.Name ?? entityId; } catch { displayName = entityId; } }

                    currentEntityIds.Add(entityId);

                    bool isKnownEntity = _entityIdSnapshot.Contains(entityId);
                    int prevCount = _entityAttrCountSnapshot.TryGetValue(entityId, out int p2) ? p2 : -1;

                    // Fast path: count-only read (cheap, no per-attr iteration)
                    // until we know we need to do a set diff. Most entities show
                    // no count delta tick to tick, so we skip the per-attr walk.
                    int currentAttrCount = 0;
                    HashSet<string> currentAttrIds = null;
                    dynamic entityAttrs = null;
                    try
                    {
                        entityAttrs = modelObjects.Collect(entity, "Attribute");
                        if (entityAttrs != null) currentAttrCount = (int)entityAttrs.Count;
                    }
                    catch (Exception ex) { Log($"DiagramHeartbeat: per-entity count err for '{displayName}': {ex.Message}"); }

                    bool attrsGrew = isKnownEntity && currentAttrCount > prevCount;
                    bool attrsShrunk = isKnownEntity && currentAttrCount < prevCount;
                    bool isNewlySeenEntity = !isKnownEntity;
                    bool isPendingOwner = _pendingNamedAttrs.ContainsKey(entityId);
                    // Walk attribute ids whenever the entity hasn't been
                    // snapshotted yet OR its count grew OR its count shrank.
                    // Critically we run the walk on the FIRST tick too (when
                    // no previous snapshot exists for any entity), otherwise
                    // the next tick's diff would see "no prior ids" and treat
                    // every existing attr as new - producing a popup for every
                    // column in the table (verified 2026-05-12: log "stripped
                    // 31 new attr(s)" then "diff fired 31 result(s)" on a 31-
                    // attr table after a 1-attr add). We do not validate on
                    // the first tick - just snapshot - so the cost is one
                    // ObjectId read per attr per entity, paid once at start.
                    //
                    // attrsShrunk (2026-05-24, Phase 5) drives the locked-
                    // column delete-restore path: we need the current attr-id
                    // set so we can diff against the previous set and find
                    // exactly which attribute ObjectIds disappeared.
                    bool needsIdWalk = attrsGrew || isNewlySeenEntity || attrsShrunk;

                    HashSet<string> newAttrIds = null;
                    HashSet<string> missingAttrIds = null;
                    if (needsIdWalk && entityAttrs != null)
                    {
                        currentAttrIds = new HashSet<string>(StringComparer.Ordinal);
                        try
                        {
                            foreach (dynamic a in entityAttrs)
                            {
                                string aid;
                                try { aid = a.ObjectId?.ToString(); }
                                catch (Exception aex) { Log($"DiagramHeartbeat: attr id read err on '{displayName}': {aex.Message}"); continue; }
                                if (!string.IsNullOrEmpty(aid)) currentAttrIds.Add(aid);
                            }
                        }
                        catch (Exception walkEx) { Log($"DiagramHeartbeat: attr walk err on '{displayName}': {walkEx.Message}"); }

                        if (_entityAttrIdSnapshot.TryGetValue(entityId, out var prevIds))
                        {
                            newAttrIds = new HashSet<string>(currentAttrIds, StringComparer.Ordinal);
                            newAttrIds.ExceptWith(prevIds);

                            // Phase 5 (2026-05-24): when the count shrank we
                            // also need to know which ObjectIds disappeared so
                            // we can detect locked-column deletion. ExceptWith
                            // is symmetric to the new-id calc above. Only
                            // computed when attrsShrunk to avoid useless
                            // allocations on grow-only ticks.
                            if (attrsShrunk)
                            {
                                missingAttrIds = new HashSet<string>(prevIds, StringComparer.Ordinal);
                                missingAttrIds.ExceptWith(currentAttrIds);
                            }
                        }
                        else
                        {
                            // No prior set means either (a) the very first
                            // heartbeat after monitoring started, or (b) a
                            // brand-new entity created later. In case (a) the
                            // attrs are pre-existing and should NOT validate;
                            // case (b) actually wants every attr validated.
                            // We rely on isFirstTick to disambiguate below.
                            newAttrIds = new HashSet<string>(currentAttrIds, StringComparer.Ordinal);
                        }
                    }
                    ReleaseCom(entityAttrs);

                    // Locked-column delete restore (Phase 5, 2026-05-24).
                    // Triggers ONLY when attrs shrank AND we have a missing-
                    // id set. Pre-filtered against locked names so non-locked
                    // deletions pay zero cost - admin's normal column-removal
                    // workflow is not disturbed.
                    if (!isFirstTick && attrsShrunk && missingAttrIds != null && missingAttrIds.Count > 0)
                    {
                        try { RestoreDeletedLockedColumns(entity, displayName, missingAttrIds); }
                        catch (Exception restEx) { Log($"RestoreDeletedLockedColumns err on '{displayName}': {restEx.Message}"); }
                    }

                    // Validation only fires AFTER the first tick (we want to
                    // baseline the pre-existing model silently on startup).
                    // Three triggers for rescan:
                    //   'A' attrsGrew      - new attr appeared since last tick
                    //   'N' isNewlySeenEntity - entity not in our snapshot yet
                    //   'P' isPendingOwner - entity has a placeholder-named
                    //                        attribute we're still watching for
                    //                        a rename (Model Explorer flow).
                    // The pending-owner trigger fires WITHOUT requiring a count
                    // delta, so a rename inside the entity gets caught by
                    // CheckEntityForChanges' fingerprint diff.
                    if (!isFirstTick && (newAttrIds != null && newAttrIds.Count > 0 || isPendingOwner))
                    {
                        char kind = attrsGrew ? 'A' : (isNewlySeenEntity ? 'N' : 'P');
                        entitiesToRescan.Add((entity, displayName, entityId, kind, newAttrIds ?? new HashSet<string>(StringComparer.Ordinal)));
                    }

                    _entityAttrCountSnapshot[entityId] = currentAttrCount;
                    if (currentAttrIds != null)
                    {
                        _entityAttrIdSnapshot[entityId] = currentAttrIds;
                    }

                    // Phase-2G: entity-name validation routing. Three cases
                    // matter after the very first tick:
                    //   1) newly-seen entity + real name -> fire naming check now
                    //   2) newly-seen entity + placeholder name -> hold in
                    //      _pendingNamedEntities; the user is still naming the
                    //      table from inline edit. ValidateCommittedPendingAttrs
                    //      drains it when the user commits.
                    //   3) known entity, name changed -> fire naming check now
                    //      (also clears pending if the previous name was a
                    //      placeholder).
                    bool prevNameKnown = _entityDisplayNameSnapshot.TryGetValue(entityId, out var prevDisplayName);
                    if (!isFirstTick)
                    {
                        if (isNewlySeenEntity)
                        {
                            if (IsPlaceholderEntityName(displayName))
                            {
                                _pendingNamedEntities.Add(entityId);
                                Log($"[PENDING-ENTITY] entityId={entityId} name='{displayName}' - placeholder, holding for inline-edit commit / rename");
                            }
                            else
                            {
                                entitiesToNamingCheck.Add((displayName, isNew: true));
                                Log($"[NAMING] newly seen entity '{displayName}' - queuing naming check (isNew)");
                                FireNewEntityPipeline(entity, displayName);
                            }
                        }
                        else if (prevNameKnown && !string.Equals(prevDisplayName, displayName, StringComparison.Ordinal))
                        {
                            // Rename. If the new name is STILL a placeholder
                            // (rare - user just shuffled defaults), stay
                            // pending. Otherwise fire the naming check now
                            // and drop the pending entry.
                            if (IsPlaceholderEntityName(displayName))
                            {
                                _pendingNamedEntities.Add(entityId);
                                Log($"[PENDING-ENTITY] entityId={entityId} '{prevDisplayName}' -> '{displayName}' (still placeholder) - keeping pending");
                            }
                            else
                            {
                                // Resolve isNew BEFORE queuing: a pending
                                // placeholder entity that just got a real
                                // name is functionally a creation gesture
                                // (we are about to run FireNewEntityPipeline
                                // on it below), so the Cancel button must
                                // say "Discard New Table". A genuine rename
                                // of an already-real entity stays
                                // isNew=false → "Revert Change".
                                bool wasPending = _pendingNamedEntities.Remove(entityId);
                                bool entityIsNew = wasPending;

                                entitiesToNamingCheck.Add((displayName, entityIsNew));
                                Log($"[NAMING] entity renamed '{prevDisplayName}' -> '{displayName}' - queuing naming check (isNew={entityIsNew})");

                                if (wasPending)
                                {
                                    Log($"[PENDING-ENTITY] entityId={entityId} dropped from pending after rename to '{displayName}'");
                                    // The entity was first seen with a placeholder name, so
                                    // the new-entity pipeline was deferred. Fire it now that
                                    // we have a real physical name to feed the wizard.
                                    FireNewEntityPipeline(entity, displayName);
                                }
                            }
                        }
                        // Per-tick drift loop intentionally removed
                        // 2026-05-17: walking every entity every second to
                        // diff ~4 SCAPI-read watched properties scaled to
                        // ~3-5 s per tick on a 286-entity model (verified
                        // by user log: heartbeat interval dropped from 1 s
                        // to 3-5 s). The drift check now runs as an
                        // edit-session diff in WindowMonitorTimer_Tick:
                        // snapshot watched properties when Column / Entity
                        // editor OPENS, diff vs current when it CLOSES.
                        // That captures the user's actual change gesture
                        // exactly once and costs O(rules × 1 entity) per
                        // edit session, not O(rules × N entities) per
                        // heartbeat tick.
                    }
                    _entityDisplayNameSnapshot[entityId] = displayName;
                }

                // Garbage collect snapshot entries for entities that no longer exist.
                if (_entityIdSnapshot.Count > 0)
                {
                    var removed = new List<string>();
                    foreach (var prev in _entityIdSnapshot)
                    {
                        if (!currentEntityIds.Contains(prev))
                            removed.Add(prev);
                    }
                    foreach (var id in removed)
                    {
                        _entityIdSnapshot.Remove(id);
                        _entityAttrCountSnapshot.Remove(id);
                        _entityAttrIdSnapshot.Remove(id);
                        _entityDisplayNameSnapshot.Remove(id);
                        _pendingNamedEntities.Remove(id);
                        // Drop pending attr timestamps belonging to the
                        // disappearing entity so the dictionary doesn't grow
                        // unbounded across long sessions.
                        if (_pendingNamedAttrs.TryGetValue(id, out var orphanedAttrs))
                        {
                            foreach (var orphAttrId in orphanedAttrs) _pendingAttrAddedAt.Remove(orphAttrId);
                        }
                        _pendingNamedAttrs.Remove(id);
                    }
                }
                _entityIdSnapshot.Clear();
                foreach (var id in currentEntityIds) _entityIdSnapshot.Add(id);
                _lastTotalAttributeCount = totalAttrs;
                _lastTotalEntityCount = totalEntities;

                // Validate the entities flagged as changed. Two cases:
                //
                //   kind 'A' (attr count grew on a KNOWN entity):
                //     Existing scoped pipeline (silent populate -> diff ->
                //     validate). _attributeSnapshots may not have this entity's
                //     attrs yet IF the entity was never opened in the Column
                //     Editor (Phase-2D's _tablesBaselined doesn't cover diagram-
                //     only flow). In that case CheckEntityForChanges will mark
                //     ALL existing attrs as new and fire glossary popups for
                //     each, which is excessive false-positive noise. So we
                //     silent-baseline FIRST when _tablesBaselined doesn't know
                //     this table, then a follow-up tick (~1 s) will catch the
                //     real delta when the user keeps adding columns.
                //
                //     But for the FIRST added column we must avoid the one-tick
                //     miss: pre-baseline-then-diff produces zero diff. Trick:
                //     drive CheckEntityForChanges directly. Its diff logic
                //     against _attributeSnapshots (per-attribute-id keyed)
                //     treats any attr whose ID is missing as new. With no
                //     baseline taken yet, every existing attr is "new" - the
                //     pre-existing 30 columns spam glossary popups. Not OK.
                //
                //     So we baseline first (to anchor the existing 30), then
                //     bump the per-entity count snapshot DOWN by 1 to force the
                //     next heartbeat to re-detect growth. That guarantees the
                //     user sees the popup on the next heartbeat (~1 s
                //     latency). Acceptable trade-off for now; a future native
                //     hook can drop it to ~30 ms.
                //
                //   kind 'N' (entity newly seen by us - could be brand-new in
                //     model OR an entity whose ID wasn't in our snapshot yet
                //     because we missed it on the first walk):
                //     Same trick - silent baseline + nudge count snapshot so
                //     the next heartbeat fires if the user keeps adding.
                foreach (var (entity, name, id, kind, newAttrIds) in entitiesToRescan)
                {
                    try
                    {
                        bool needBaseline = !_tablesBaselined.Contains(name);
                        if (needBaseline)
                        {
                            // No baseline yet (Column Editor never opened this
                            // table). SilentPopulate writes every current attr
                            // including the just-added ones - if we leave it
                            // at that, CheckEntityForChanges sees zero diff
                            // and skips validation. We then strip exactly the
                            // attrs we know are new from _attributeSnapshots
                            // (set-diff between this tick's ObjectIds and the
                            // previous tick's). The diff sees those as missing
                            // and fires ProcessNewAttribute -> ValidateGlossary
                            // on each, producing a single consolidated popup
                            // through ShowConsolidatedPopup.
                            SilentPopulateEntity(entity, modelObjects, name);
                            _tablesBaselined.Add(name);
                        }

                        // Strip the known-new ObjectIds from _attributeSnapshots
                        // whether or not we just baselined - the snapshot may
                        // have been written by the Column Editor scoped path
                        // before the user added the column outside the editor,
                        // and in that case the new attr is ALREADY baselined
                        // and would otherwise stay silent.
                        int stripped = 0;
                        foreach (var newId in newAttrIds)
                        {
                            if (_attributeSnapshots.Remove(newId)) stripped++;
                        }
                        if (stripped > 0)
                        {
                            Log($"DiagramHeartbeat: stripped {stripped} new attr(s) from baseline on '{name}' kind={kind}");
                        }
                        else
                        {
                            Log($"DiagramHeartbeat: nothing to strip on '{name}' kind={kind} newAttrIds={newAttrIds.Count} (snapshot may be empty)");
                        }

                        _pendingResults.Clear();
                        CheckEntityForChanges(entity, modelObjects);
                        if (_pendingResults.Count > 0)
                        {
                            Log($"DiagramHeartbeat: diff fired {_pendingResults.Count} result(s) on '{name}' kind={kind}");
                            ShowConsolidatedPopup();
                        }
                    }
                    catch (Exception ex) { Log($"DiagramHeartbeat: rescan err for '{name}': {ex.Message}"); }
                }

                // Phase-2G: drain the entity-naming queue. RunScopedTableNamingCheck
                // re-opens its own Collect("Entity") iterator, so we must be out
                // of the heartbeat walk before calling it - otherwise the second
                // enumerator on the same COM collection can return inconsistent
                // results on r10.10. Reentrancy guard inside
                // RunScopedTableNamingCheck (_scopedCheckInProgress) protects
                // against the modal popup pumping our own timer back into here.
                // Editor-open gate (user feedback 2026-05-17): if any
                // edit dialog is open the user is still mid-gesture on
                // the very entity we'd validate. Defer the popups into
                // _pendingTableNamingChecks; the editor-close transition
                // handler in WindowMonitorTimer_Tick drains them.
                bool editorOpen = IsColumnEditorOpen() || IsEntityEditorOpen(out _);
                foreach (var (entityName, entityIsNew) in entitiesToNamingCheck)
                {
                    if (editorOpen)
                    {
                        // Keep the isNew flag on the deferred entry so the
                        // editor-close flush can still pick "Discard New
                        // Table" over "Revert Change".
                        if (_pendingTableNamingChecks.Add((entityName, entityIsNew)))
                            Log($"DiagramHeartbeat: deferring naming check on '{entityName}' (editor open, isNew={entityIsNew})");
                        continue;
                    }
                    try { RunScopedTableNamingCheck(entityName, isNew: entityIsNew); }
                    catch (Exception ex) { Log($"DiagramHeartbeat: naming check err for '{entityName}': {ex.Message}"); }
                }
            }
            finally
            {
                ReleaseCom(attrCollection);
                ReleaseCom(entityCollection);
            }
        }

        /// <summary>
        /// Read the watched-property values for the entity matching
        /// <paramref name="tableName"/> via SCAPI. Returns an empty
        /// dict when the entity is not found or naming rules are not
        /// loaded - callers compare-by-equality so an empty baseline
        /// matches an empty current and produces no false fire.
        /// Capped at one SCAPI read per active naming-rule property,
        /// so the cost is bounded by the rule set (≤10 reads in
        /// practice) regardless of model size.
        /// </summary>
        /// <summary>
        /// Compare an entity's stored Physical_Name against the name parsed
        /// out of an editor window title. erwin transforms certain characters
        /// (notably '/' in auto-generated placeholder names like 'E/33') to
        /// '_' when it renders the title, so a literal string compare misses
        /// the underlying entity. Normalises '/' -> '_' in physName before
        /// the equality test; underscores in user-typed names compare
        /// cleanly, only the slash-bearing autogenerated case benefits from
        /// the fold (verified 2026-05-21 against log lines
        ///   liveName='E/33'
        ///   Entity Editor state -> OPEN activeTable='E_33'
        /// where the strict compare returned zero matches and baseline
        /// capture came back empty).
        /// </summary>
        private static bool EntityNameMatchesTitle(string physName, string titleName)
        {
            if (string.IsNullOrEmpty(physName) || string.IsNullOrEmpty(titleName)) return false;
            if (string.Equals(physName, titleName, StringComparison.OrdinalIgnoreCase)) return true;
            if (physName.IndexOf('/') < 0) return false;
            return string.Equals(physName.Replace('/', '_'), titleName, StringComparison.OrdinalIgnoreCase);
        }

        private Dictionary<string, string> ReadEntityWatchedProperties(string tableName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(tableName)) return result;
            if (!NamingStandardService.Instance.IsLoaded) return result;

            var codes = NamingStandardService.Instance.GetPropertyCodes("Table")
                ?.Where(c => !string.IsNullOrEmpty(c)
                          && !string.Equals(c, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                ?.ToList();
            if (codes == null || codes.Count == 0) return result;

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return result;
                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return result;
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;
                    string physName;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        physName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                    }
                    catch { continue; }
                    if (!EntityNameMatchesTitle(physName, tableName)) continue;

                    foreach (var code in codes)
                    {
                        try { result[code] = entity.Properties(code)?.Value?.ToString() ?? ""; }
                        catch { result[code] = ""; }
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Log($"ReadEntityWatchedProperties err for '{tableName}': {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Compare the baseline captured at editor-open against the
        /// current SCAPI state. Fires <see cref="RunScopedTableNamingCheck"/>
        /// when any watched property changed - the validator will surface
        /// any Required-violation popup that the diff exposed (e.g. the
        /// user cleared Owner on an existing table).
        /// </summary>
        /// <summary>
        /// Look the entity up by physical name in the live model and let
        /// <c>TableTypeMonitorService.ReevaluateConditionalPredefinedColumns</c>
        /// re-apply any predefined-column rule whose UDP condition now
        /// matches. Walking by name uses the same Collect("Entity") + match
        /// path RunScopedTableNamingCheck does, so cost is identical.
        /// Best-effort: per-entity errors are logged but do not propagate.
        /// </summary>
        private void RunScopedReevaluateConditionalPredefinedColumns(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return;
            if (_validationSuspended) return;
            if (_sessionLost || _disposed) return;
            if (_tableTypeMonitor == null) return;

            dynamic modelObjects = null;
            dynamic root = null;
            dynamic allEntities = null;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelObjects?.Root;
                if (root == null) return;
                allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

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

                    if (!EntityNameMatchesTitle(nameForMatch, tableName)) continue;

                    // Locked-column delete-restore detection (close-edge,
                    // 2026-05-25 - "show dialog BEFORE the column re-
                    // appears"). Walk the entity's current attrs once,
                    // build the set of locked rule names already present,
                    // then ask the predefined-column service which locked
                    // rules CURRENTLY APPLY but have no column in the
                    // entity. Those are the deletes the user just made.
                    // For each we enqueue a deferred (dialog + restore)
                    // and SKIP the normal reevaluate call - the deferred
                    // apply does the SCAPI add itself once the user
                    // acknowledges.
                    var currentLockedAttrNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        dynamic walkAttrs = modelObjects.Collect(entity, "Attribute");
                        if (walkAttrs != null)
                        {
                            try
                            {
                                foreach (dynamic a in walkAttrs)
                                {
                                    if (a == null) continue;
                                    try
                                    {
                                        string aName = a.Name ?? "";
                                        if (!string.IsNullOrEmpty(aName))
                                            currentLockedAttrNames.Add(aName);
                                    }
                                    catch { /* skip this attr */ }
                                }
                            }
                            finally { ReleaseCom(walkAttrs); }
                        }
                    }
                    catch (Exception walkEx) { Log($"RunScopedReevaluateConditionalPredefinedColumns walk err: {walkEx.Message}"); }

                    // Which locked rules currently apply to this entity AND
                    // are missing a corresponding column? FindApplicableLocked-
                    // Rule already handles the conditional UDP check.
                    var missingLockedRules = new List<PredefinedColumn>();
                    foreach (var lockedRule in PredefinedColumnService.Instance.GetLocked())
                    {
                        if (string.IsNullOrEmpty(lockedRule.ColumnName)) continue;
                        if (currentLockedAttrNames.Contains(lockedRule.ColumnName)) continue;
                        var applicable = PredefinedColumnService.Instance.FindApplicableLockedRule(entity, lockedRule.ColumnName);
                        if (applicable == null) continue;
                        if (applicable.Id != lockedRule.Id) continue;
                        missingLockedRules.Add(applicable);
                    }

                    // Per-rule snapshot lookup. A locked column is treated as
                    // "user-deleted -> defer dialog" only when a snapshot for
                    // that SPECIFIC column name still exists on this entity
                    // (user deleted, heartbeat hasn't dropped the snapshot
                    // yet). If no snapshot for that column name exists, the
                    // column never lived on the entity in this session -
                    // typical when the user just flipped a UDP value that
                    // newly activates a conditional rule (TableClass=Parametre
                    // -> Log activates the COL1 rule; COL1 never existed
                    // before so this is a first-time add, NOT a restore).
                    // Verified 2026-05-26 21:13 from erwin-addin-debug.log:
                    // user added VpTEST3, set TableClass=Parametre, added
                    // musteri_no, then flipped TableClass to Log. The OLD
                    // coarse "any snapshot for the entity" check fired
                    // because musteri_no's snapshot existed; the close-edge
                    // restore loop then popped a 'Column Restored - deletion
                    // was undone' dialog even though the user had never
                    // touched COL1. The dropped rules below fall through to
                    // the normal reevaluate path which silently adds them.
                    string capturedEntityNameOuter = nameForMatch;
                    var deferableRules = new List<PredefinedColumn>();
                    foreach (var rule in missingLockedRules)
                    {
                        bool snapshotExistsForColumn = false;
                        foreach (var snapKv in _attributeSnapshots)
                        {
                            var snap = snapKv.Value;
                            if (snap == null) continue;
                            if (!string.Equals(snap.TableName, capturedEntityNameOuter, StringComparison.Ordinal)) continue;
                            if (string.Equals(snap.PhysicalName, rule.ColumnName, StringComparison.OrdinalIgnoreCase))
                            {
                                snapshotExistsForColumn = true;
                                break;
                            }
                        }
                        if (snapshotExistsForColumn)
                            deferableRules.Add(rule);
                        else
                            Log($"Locked predefined-column missing on '{capturedEntityNameOuter}.{rule.ColumnName}' but no prior snapshot - treating as first-time add (no restore dialog).");
                    }

                    if (deferableRules.Count > 0)
                    {
                        // Defer dialog + restore for each missing locked
                        // column. Skip the normal reevaluate so the column
                        // does NOT reappear until the user has clicked OK.
                        string capturedEntityName = nameForMatch;
                        foreach (var ruleToRestore in deferableRules)
                        {
                            var capturedRule = ruleToRestore;
                            string detail = $"Datatype: {capturedRule.DataType}\nNullable: {(capturedRule.Nullable ? "yes" : "no")}"
                                + (string.IsNullOrEmpty(capturedRule.DefaultValue) ? "" : $"\nDefault: \"{capturedRule.DefaultValue}\"")
                                + (capturedRule.IsPrimaryKey ? "\nPK: yes" : "");
                            string dedupe = $"delete|{capturedEntityName}|{capturedRule.ColumnName}";
                            Log($"Locked predefined-column delete intercepted (close-edge): '{capturedEntityName}.{capturedRule.ColumnName}' (locked rule#{capturedRule.Id}, deferring dialog+restore)");
                            EnqueueLockedColumnDialogAndApply(
                                capturedRule.ColumnName,
                                capturedEntityName,
                                Forms.LockedColumnAction.Delete,
                                detail,
                                dedupe,
                                () =>
                                {
                                    try
                                    {
                                        dynamic restoreEntity = ResolveEntityByName(capturedEntityName);
                                        if (restoreEntity == null)
                                        {
                                            Log($"Locked delete restore (close-edge): entity '{capturedEntityName}' not found at apply time");
                                            return;
                                        }
                                        _tableTypeMonitor.RestoreSpecificPredefinedColumn(restoreEntity, capturedRule, capturedEntityName);
                                        Log($"Locked delete restore applied (close-edge): rule#{capturedRule.Id} '{capturedRule.ColumnName}' on '{capturedEntityName}'");
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"Locked delete restore (close-edge) FAILED for '{capturedRule.ColumnName}' on '{capturedEntityName}': {ex.Message}");
                                    }
                                });
                        }
                        // Fall through but skip the normal reevaluate for
                        // this entity - the deferred path owns the add.
                        return;
                    }

                    // No missing locked columns (or brand-new entity which
                    // is handled by the normal first-add path). Let the
                    // standard reevaluate handle any non-locked conditional
                    // rules.
                    _tableTypeMonitor.ReevaluateConditionalPredefinedColumns(entity, nameForMatch);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"RunScopedReevaluateConditionalPredefinedColumns err for '{tableName}': {ex.Message}");
            }
            finally
            {
                ReleaseCom(allEntities);
            }
        }

        /// <summary>
        /// Returns true when the scoped naming check was actually fired
        /// (i.e. a watched property drift was detected). Callers use this
        /// to skip a redundant follow-up RunScopedTableNamingCheck after
        /// editor close.
        /// </summary>
        private bool DiffWatchedPropertiesAndFire(string tableName, Dictionary<string, string> baseline)
        {
            if (string.IsNullOrEmpty(tableName) || baseline == null) return false;
            var current = ReadEntityWatchedProperties(tableName);
            string changedCode = null;
            string oldVal = null, newVal = null;
            foreach (var kv in baseline)
            {
                string nowVal = current.TryGetValue(kv.Key, out var cv) ? (cv ?? "") : "";
                if (!string.Equals(kv.Value ?? "", nowVal, StringComparison.Ordinal))
                {
                    changedCode = kv.Key;
                    oldVal = kv.Value ?? "";
                    newVal = nowVal;
                    break;
                }
            }
            if (changedCode == null) return false;
            Log($"Editor close: watched property drift on '{tableName}': {changedCode} '{oldVal}' -> '{newVal}' - running scoped naming check");
            // Pass the editor-open baseline through so the downstream
            // Required-popup Cancel branch can revert to the pre-edit value
            // even when TableTypeMonitor._entitySnapshots has no entry for
            // this entity (the legacy periodic-walk path that used to fill
            // those snapshots was removed in Phase-2D; auto-generated /
            // user-created entities never get a snapshot otherwise). The
            // baseline dict we hold here IS the source of truth for what
            // the user just changed away from.
            try { RunScopedTableNamingCheck(tableName, baseline); }
            catch (Exception ex) { Log($"DiffWatchedPropertiesAndFire: scoped check err for '{tableName}': {ex.Message}"); }
            return true;
        }

        /// <summary>
        /// Event-driven Physical_Name rename scan (2026-05-17). Replaces
        /// the per-tick per-entity Physical_Name read in
        /// <see cref="DiagramHeartbeatTick"/> which dominated tick cost
        /// (~1.5-2 s on 286-entity models). Walks the model ONCE at
        /// specific user-gesture boundaries:
        /// <list type="bullet">
        /// <item><description>Inline edit close (diagram / Model Explorer
        /// label edit finished) - catches "user double-clicked an
        /// existing real-named entity and typed a new name". The previous
        /// <see cref="_pendingNamedEntities"/> mechanism only covered
        /// placeholder-named entities.</description></item>
        /// <item><description>Column Editor close - catches renames typed
        /// in the parent table's Physical_Name field on the way out.</description></item>
        /// <item><description>Entity Editor close - same path for the
        /// Table Properties dialog.</description></item>
        /// </list>
        /// Compares each entity's current Physical_Name against
        /// <see cref="_entityDisplayNameSnapshot"/>; fires
        /// <see cref="RunScopedTableNamingCheck"/> on the new name for any
        /// diff and refreshes the snapshot so the next call sees a fresh
        /// baseline.
        /// </summary>
        private void ScanForRenamesEventDriven(string trigger)
        {
            if (_sessionLost || _disposed || _validationSuspended) return;
            if (!IsModelStillOpen()) return;

            dynamic modelObjects = null;
            dynamic root = null;
            dynamic allEntities = null;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelObjects.Root;
                if (root == null) return;
                allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                var renames = new List<(string entityId, string oldName, string newName)>();
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;
                    string entityId;
                    try { entityId = entity.ObjectId?.ToString() ?? ""; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(entityId)) continue;

                    string displayName;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        displayName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? entityId);
                    }
                    catch { try { displayName = entity.Name ?? entityId; } catch { displayName = entityId; } }

                    if (_entityDisplayNameSnapshot.TryGetValue(entityId, out var prev)
                        && !string.Equals(prev, displayName, StringComparison.Ordinal))
                    {
                        renames.Add((entityId, prev, displayName));
                    }
                    _entityDisplayNameSnapshot[entityId] = displayName;
                }

                if (renames.Count == 0) return;
                Log($"ScanForRenamesEventDriven [{trigger}]: {renames.Count} rename(s) detected");
                foreach (var (id, oldName, newName) in renames)
                {
                    Log($"  rename detected '{oldName}' -> '{newName}' (id={id})");
                    if (IsPlaceholderEntityName(newName))
                    {
                        // User cleared the name back to a placeholder during
                        // the same gesture - defer until they pick a real name.
                        _pendingNamedEntities.Add(id);
                        continue;
                    }

                    // Mirror the heartbeat-tick rename branch (line ~1245):
                    // a rename FROM a placeholder name that the user has
                    // been carrying in `_pendingNamedEntities` is the
                    // placeholder-commit gesture - functionally a creation,
                    // not a post-create edit. The downstream
                    // ApplyNamingStandards / NamingValidationEngine paths
                    // filter rules by ApplyOn=Create using the `isNew`
                    // flag, so getting this wrong silently drops every
                    // apply=Create rule (verified 2026-05-31: TableClass=
                    // History flow lost the Vp prefix rule#17 and the
                    // Required Name_Qualifier dialog rule#1019 because the
                    // rename detection here always passed isNew=false).
                    bool wasPending = _pendingNamedEntities.Remove(id);
                    // Bridge: ValidateCommittedPendingAttrs may have
                    // already lifted this id out of _pendingNamedEntities
                    // and added it to _creationGestureEntityIds. When the
                    // Required-UDP modal pump dispatches us mid-drain,
                    // wasPending reads false but the gesture is still
                    // active - the bridge set tells us so.
                    //
                    // Bridge purpose 2026-05-31 (corrected semantic, see
                    // header comment on _creationGestureEntityIds): we
                    // PROPAGATE isNew=true to the deferred scoped check.
                    // The strict ApplyOn gate then naturally filters out
                    // Update rules (rule#22 _PRM, ApplyOn=Update, MUST
                    // NEVER fire on a new entity per user rule). Only
                    // Create + Both rules fire -> ONE engine pass ->
                    // ONE consolidated modal (Vp prefix is Create / Both
                    // so it lands together with whatever Create-side
                    // Suffix the active TableClass dictates, if any).
                    bool inCreationGesture = _creationGestureEntityIds.Contains(id);
                    bool entityIsNew = wasPending || inCreationGesture;
                    if (entityIsNew)
                        Log($"  rename '{oldName}' -> '{newName}' is placeholder commit (wasPending={wasPending}, inCreationGesture={inCreationGesture}) - treating as new-entity creation flow (isNew=true)");

                    try { RunScopedTableNamingCheck(newName, isNew: entityIsNew); }
                    catch (Exception ex) { Log($"ScanForRenamesEventDriven scoped check err for '{newName}': {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Log($"ScanForRenamesEventDriven [{trigger}] err: {ex.Message}");
            }
            finally
            {
                ReleaseCom(allEntities);
                ReleaseCom(root);
                ReleaseCom(modelObjects);
            }
        }

        /// <summary>
        /// Event-driven locked-column rename watch (Gap C, 2026-05-24).
        /// Designed for sub-second popup latency on big models. Walks
        /// ONLY entities that currently hold a snapshot with a locked-
        /// rule column name; everything else is filtered out before any
        /// SCAPI read.
        ///
        /// <para>Algorithm:</para>
        /// <list type="number">
        /// <item>If no locked predefined-column rules are loaded, return.</item>
        /// <item>Filter <see cref="_attributeSnapshots"/> to entries whose
        ///       PhysicalName matches a locked rule's ColumnName, grouped
        ///       by snapshot.TableName. Result: a small set of candidate
        ///       table names (usually 1-3).</item>
        /// <item>Walk entities, match by candidate table name (skip the
        ///       rest), and for each candidate entity scan only its
        ///       attributes whose ObjectId is in the filtered snapshot
        ///       set. Read Physical_Name once per such attr; on drift
        ///       fire <see cref="ProcessAttributeChanges"/> which runs
        ///       the locked-column interceptor.</item>
        /// </list>
        ///
        /// The previous unscoped variant walked 286 entities * ~30 attrs
        /// = ~8400 SCAPI reads per inline-edit-close event and the
        /// resulting popup latency was several seconds (user complaint
        /// 2026-05-24). The scoped variant typically reads &lt; 50 props
        /// and lands the popup in &lt; 200 ms.
        /// </summary>
        private void ScanForLockedColumnRenames(string trigger)
        {
            if (_sessionLost || _disposed || _validationSuspended) return;
            if (!IsModelStillOpen()) return;
            if (!PredefinedColumnService.Instance.IsLoaded) return;

            // 1. Locked rule name set. Empty -> no work.
            var lockedNames = new HashSet<string>(
                PredefinedColumnService.Instance.GetLocked().Select(r => r.ColumnName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            lockedNames.Remove(string.Empty);
            if (lockedNames.Count == 0) return;

            // 2. Snapshot pre-filter. Map candidate attribute ObjectId ->
            //    snapshot, and collect the distinct entity table names
            //    that contain such an attribute. Skip empty PhysicalName
            //    (placeholder) - the user cannot have renamed THAT into a
            //    locked-rule violation on this gesture.
            var candidateAttrs = new Dictionary<string, AttributeValidationSnapshot>(StringComparer.Ordinal);
            var candidateTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _attributeSnapshots)
            {
                var snap = kv.Value;
                if (snap == null) continue;
                string snapName = snap.PhysicalName ?? string.Empty;
                if (snapName.Length == 0) continue;
                if (!lockedNames.Contains(snapName)) continue;
                candidateAttrs[kv.Key] = snap;
                if (!string.IsNullOrEmpty(snap.TableName))
                    candidateTables.Add(snap.TableName);
            }
            if (candidateAttrs.Count == 0) return;

            dynamic modelObjects = null;
            dynamic root = null;
            dynamic allEntities = null;
            int renamesProcessed = 0;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelObjects?.Root;
                if (root == null) return;
                allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                // Track remaining candidates so we can break the entity
                // walk the moment every candidate has been visited - no
                // point scanning all 286 entities when our target is one.
                var remainingTables = new HashSet<string>(candidateTables, StringComparer.OrdinalIgnoreCase);

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;
                    if (remainingTables.Count == 0) break;

                    // Cheap match against candidate set BEFORE touching attrs.
                    string tableName;
                    try { tableName = GetTableName(entity); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(tableName)) continue;

                    // Candidate table set uses OrdinalIgnoreCase (HashSet ctor
                    // above). EntityNameMatchesTitle is more permissive
                    // (handles "/" -> "_" normalization for generated names)
                    // so fall back to that if direct contains misses - small
                    // perf cost only when candidate has the slash form.
                    string matchedCandidate = null;
                    if (remainingTables.Contains(tableName))
                    {
                        matchedCandidate = tableName;
                    }
                    else
                    {
                        foreach (var ct in remainingTables)
                        {
                            if (EntityNameMatchesTitle(ct, tableName) || EntityNameMatchesTitle(tableName, ct))
                            {
                                matchedCandidate = ct;
                                break;
                            }
                        }
                    }
                    if (matchedCandidate == null) continue;
                    remainingTables.Remove(matchedCandidate);

                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                    catch { continue; }
                    if (entityAttrs == null) continue;

                    HashSet<string> predefinedColumnNames = null;
                    try
                    {
                        foreach (dynamic attr in entityAttrs)
                        {
                            if (attr == null) continue;

                            string objectId;
                            try { objectId = attr.ObjectId?.ToString() ?? ""; }
                            catch { continue; }
                            if (string.IsNullOrEmpty(objectId)) continue;
                            if (!candidateAttrs.TryGetValue(objectId, out var existingSnap)) continue;

                            string liveName;
                            try { liveName = attr.Properties("Physical_Name").Value?.ToString() ?? ""; }
                            catch { continue; }

                            string liveResolved = (!string.IsNullOrEmpty(liveName) && !liveName.StartsWith("%"))
                                ? liveName
                                : (existingSnap.AttributeName ?? string.Empty);

                            string snapPhys = existingSnap.PhysicalName ?? string.Empty;
                            if (string.Equals(snapPhys, liveResolved, StringComparison.Ordinal)) continue;

                            // Drift on a locked-named attr - fire processing.
                            predefinedColumnNames ??= GetPredefinedColumnNames(entity);
                            var currentState = CreateSnapshot(attr, tableName, modelObjects);
                            currentState.TermTypeCanonical = existingSnap.TermTypeCanonical;
                            foreach (var kvp in existingSnap.UdpValues)
                                currentState.UdpValues[kvp.Key] = kvp.Value;
                            if (string.IsNullOrEmpty(currentState.PhysicalDataType))
                                currentState.PhysicalDataType = existingSnap.PhysicalDataType;

                            Log($"ScanForLockedColumnRenames [{trigger}]: locked-name rename on '{tableName}': '{snapPhys}' -> '{liveResolved}' (id={objectId})");

                            // Stash the entity we already have so the
                            // downstream EnforceLockedColumnRename ->
                            // ResolveEntityByName call sees a cache hit
                            // instead of doing another full Collect walk.
                            _scanContextTableName = tableName;
                            _scanContextEntity = entity;
                            try
                            {
                                ProcessAttributeChanges(attr, existingSnap, currentState, predefinedColumnNames);
                                renamesProcessed++;
                            }
                            catch (Exception ex)
                            {
                                Log($"ScanForLockedColumnRenames: ProcessAttributeChanges err on '{tableName}.{snapPhys}': {ex.Message}");
                            }
                            finally
                            {
                                _scanContextTableName = null;
                                _scanContextEntity = null;
                            }

                            if (_attributeSnapshots.TryGetValue(objectId, out var postSnap)
                                && string.Equals(postSnap.PhysicalName ?? string.Empty, snapPhys, StringComparison.Ordinal))
                            {
                                // Interceptor reverted - keep the original snapshot.
                            }
                            else
                            {
                                _attributeSnapshots[objectId] = currentState;
                            }
                        }
                    }
                    finally
                    {
                        ReleaseCom(entityAttrs);
                    }
                }

                if (renamesProcessed > 0)
                    Log($"ScanForLockedColumnRenames [{trigger}]: processed {renamesProcessed} locked-column rename(s)");
            }
            catch (Exception ex)
            {
                Log($"ScanForLockedColumnRenames [{trigger}] err: {ex.Message}");
            }
            finally
            {
                ReleaseCom(allEntities);
                ReleaseCom(root);
                ReleaseCom(modelObjects);
            }
        }

        /// <summary>
        /// Phase 5 (2026-05-24): locked-column delete restore. Called from
        /// the DiagramHeartbeat per-entity walk when the attribute count
        /// shrank AND we have a set of ObjectIds that disappeared since
        /// the previous tick. For every missing id whose snapshot
        /// PhysicalName matches an applicable locked predefined-column
        /// rule, re-create the column via the existing predefined-column
        /// add path. Shows <see cref="Forms.LockedColumnDialog"/> with
        /// <see cref="Forms.LockedColumnAction.Delete"/> per restored
        /// column.
        ///
        /// Pre-filter compliance: returns immediately when no locked
        /// rules are loaded OR when none of the missing ids correspond
        /// to a locked-named snapshot. The pre-filter is in-memory and
        /// runs before any SCAPI write.
        /// </summary>
        private void RestoreDeletedLockedColumns(dynamic entity, string entityDisplayName, HashSet<string> missingAttrIds)
        {
            if (_tableTypeMonitor == null) return;
            if (!PredefinedColumnService.Instance.IsLoaded) return;
            if (missingAttrIds == null || missingAttrIds.Count == 0) return;

            // Pre-filter: locked-rule name set. Empty -> no work.
            var lockedNames = new HashSet<string>(
                PredefinedColumnService.Instance.GetLocked().Select(r => r.ColumnName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            lockedNames.Remove(string.Empty);
            if (lockedNames.Count == 0) return;

            // Collect missing-id snapshots whose names match a locked rule.
            // Dictionary keyed by ObjectId so we can purge stale snapshot
            // entries AFTER restoration (otherwise the next tick's diff
            // would see the new ObjectId from the re-created column as a
            // brand-new attribute, but the OLD snapshot entry would still
            // be around, polluting the rename watch's candidate set).
            var candidates = new List<(string objectId, string columnName, AttributeValidationSnapshot snap)>();
            foreach (var missingId in missingAttrIds)
            {
                if (!_attributeSnapshots.TryGetValue(missingId, out var snap)) continue;
                if (snap == null) continue;
                string snapName = snap.PhysicalName ?? string.Empty;
                if (snapName.Length == 0) continue;
                if (!lockedNames.Contains(snapName)) continue;
                candidates.Add((missingId, snapName, snap));
            }
            if (candidates.Count == 0) return;

            // Restore each locked column. The entity reference we already
            // have from the heartbeat walk is exactly what
            // RestoreSpecificPredefinedColumn needs.
            int restored = 0;
            foreach (var (objectId, columnName, snap) in candidates)
            {
                var rule = PredefinedColumnService.Instance.FindApplicableLockedRule(entity, columnName);
                if (rule == null)
                {
                    // The rule's UDP condition no longer matches the entity's
                    // current state - lock is RELEASED by design (conditional
                    // semantics agreed 2026-05-24). Drop the stale snapshot so
                    // we do not re-trigger restoration on next tick.
                    _attributeSnapshots.Remove(objectId);
                    Log($"Locked predefined-column '{columnName}' deleted from '{entityDisplayName}' - rule no longer applies, lock released");
                    continue;
                }

                Log($"Locked predefined-column delete intercepted: '{entityDisplayName}.{columnName}' (locked rule#{rule.Id}, deferring dialog+restore)");

                // The old snapshot id is stale (refers to the deleted
                // SCAPI Attribute). Drop it NOW so the next heartbeat
                // does not see this missing-id and re-fire restoration
                // before the deferred apply lands.
                _attributeSnapshots.Remove(objectId);

                string detail = $"Datatype: {rule.DataType}\nNullable: {(rule.Nullable ? "yes" : "no")}"
                    + (string.IsNullOrEmpty(rule.DefaultValue) ? "" : $"\nDefault: \"{rule.DefaultValue}\"")
                    + (rule.IsPrimaryKey ? "\nPK: yes" : "");

                var capturedRule = rule;
                string capturedEntityName = entityDisplayName;
                string capturedColumnName = columnName;
                string dedupe = $"delete|{capturedEntityName}|{capturedColumnName}";

                EnqueueLockedColumnDialogAndApply(
                    capturedColumnName,
                    capturedEntityName,
                    Forms.LockedColumnAction.Delete,
                    detail,
                    dedupe,
                    () =>
                    {
                        try
                        {
                            // Re-resolve the entity by name - the dynamic
                            // reference we held earlier might be stale by
                            // the time the BeginInvoke fires (especially
                            // if heartbeat cycles touched COM state).
                            dynamic restoreEntity = ResolveEntityByName(capturedEntityName);
                            if (restoreEntity == null)
                            {
                                Log($"Locked delete restore: entity '{capturedEntityName}' not found at apply time");
                                return;
                            }
                            _tableTypeMonitor.RestoreSpecificPredefinedColumn(restoreEntity, capturedRule, capturedEntityName);
                            Log($"Locked delete restore applied: rule#{capturedRule.Id} '{capturedColumnName}' on '{capturedEntityName}'");
                        }
                        catch (Exception ex)
                        {
                            Log($"Locked delete restore FAILED for '{capturedColumnName}' on '{capturedEntityName}': {ex.Message}");
                        }
                    });
                restored++;
            }

            if (restored > 0)
                Log($"RestoreDeletedLockedColumns: queued {restored} locked column restore(s) on '{entityDisplayName}'");
        }

        /// <summary>
        /// Drain <see cref="_pendingTableNamingChecks"/> by firing the
        /// scoped naming check for each entity. Called by the
        /// WindowMonitorTimer when it observes a Column Editor or Entity
        /// Editor close transition. Best-effort - per-entity errors are
        /// logged but do not block draining the rest.
        /// </summary>
        private void FlushPendingTableNamingChecks()
        {
            if (_pendingTableNamingChecks.Count == 0) return;
            var queued = _pendingTableNamingChecks.ToArray();
            _pendingTableNamingChecks.Clear();
            Log($"Editor close: flushing {queued.Length} deferred naming check(s): {string.Join(", ", queued.Select(q => $"{q.Name}(isNew={q.IsNew})"))}");
            foreach (var (entityName, isNew) in queued)
            {
                try { RunScopedTableNamingCheck(entityName, isNew: isNew); }
                catch (Exception ex) { Log($"FlushPendingTableNamingChecks: err for '{entityName}': {ex.Message}"); }
            }
        }

        private void WindowMonitorTimer_Tick(object sender, EventArgs e)
        {
            if (_sessionLost || !_isMonitoring || _disposed || _validationSuspended) return;
            // 2026-05-25: skip window-state polling while a locked-column
            // dialog is up. Same reason as MonitorTimer_Tick - SCAPI
            // reads inside the dialog's nested pump steal time slices
            // from input processing.
            if (_lockedDialogShowing) return;

            // Safety: check if model is still open BEFORE touching the session.
            if (!IsModelStillOpen()) { HandleSessionLost(); return; }

            // Locked-UDP-definition watcher DISABLED 2026-05-22: every
            // version of this watcher (periodic, event-driven on UDP
            // Editor close) eventually produced an erwin crash. The
            // root cause is the same as the Table-UDP value enforcement
            // - erwin r10.10 SCAPI's Property collection refuses
            // entity-level writes and reads on UDPs in sparse state,
            // and our re-create / re-write attempts leave the metamodel
            // in a state where erwin's own UI render crashes on next
            // touch. Until we have a verified-safe SCAPI write path,
            // Table-level Locked UDP protection is removed entirely.
            // Column-level Locked enforcement and naming-standard
            // enforcement are unaffected.

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

                // Glossary-style scoped check: when the user opens the Column
                // Editor on a table, evaluate naming standards for that table
                // (UDP-conditional rules in particular - e.g. TABLE_TYPE='LOG'
                // -> 'LOG_' prefix). Without this, conditional table-level
                // rules only fire for newly-created entities or via a periodic
                // scan, both of which were dropped in Phase-1A. We do NOT walk
                // every entity - only the one currently in focus.
                if (!_columnEditorWasOpen && editorIsOpen && !string.IsNullOrEmpty(activeTable))
                {
                    try { RunScopedTableNamingCheck(activeTable); }
                    catch (Exception ex) { Log($"RunScopedTableNamingCheck err: {ex.Message}"); }

                    // Edit-session baseline (2026-05-17): capture the
                    // watched-property values BEFORE the user starts
                    // editing so the close-transition diff can detect
                    // anything they clear / change.
                    try
                    {
                        _columnEditorEntityBaseline = ReadEntityWatchedProperties(activeTable);
                        _columnEditorEntityName = activeTable;
                    }
                    catch (Exception ex) { Log($"Column Editor open baseline err for '{activeTable}': {ex.Message}"); }
                }

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

                    // Editor-close flush (2026-05-17): drain naming-standard
                    // checks that DiagramHeartbeat deferred while the editor
                    // was open.
                    try { FlushPendingTableNamingChecks(); }
                    catch (Exception ex) { Log($"FlushPendingTableNamingChecks err: {ex.Message}"); }

                    // Event-driven rename scan: Column Editor's parent-
                    // table Physical_Name field can rename the entity too.
                    try { ScanForRenamesEventDriven("column-editor-close"); }
                    catch (Exception ex) { Log($"ScanForRenamesEventDriven (column) err: {ex.Message}"); }

                    // Predefined-column re-evaluation must run BEFORE the
                    // naming check: ApplyNamingStandards may rename the
                    // entity (e.g. _HISTORY -> _LOG when TableClass UDP
                    // changes), and RunScopedReevaluateConditionalPredefinedColumns
                    // matches entities by the captured name. Re-evaluating
                    // first uses the still-valid pre-rename name; the
                    // predefined-column add targets the entity object, not
                    // its name, so a subsequent rename is harmless. Bug
                    // fix 2026-05-24.
                    if (!string.IsNullOrEmpty(_columnEditorEntityName))
                    {
                        try { RunScopedReevaluateConditionalPredefinedColumns(_columnEditorEntityName); }
                        catch (Exception ex) { Log($"Column Editor close predefined-column re-eval err: {ex.Message}"); }
                    }

                    // Locked-column property drift scan (2026-05-25). The
                    // heartbeat fingerprint short-circuits on anything
                    // other than Physical_Name / Physical_Data_Type, so
                    // Nullable / Default / PK changes never trip
                    // ProcessAttributeChanges. We catch them here on the
                    // close edge; pre-filtered by locked snapshot name so
                    // entities without any locked column pay zero cost.
                    if (!string.IsNullOrEmpty(_columnEditorEntityName))
                    {
                        try { ScanForLockedColumnPropertyDrift("column-editor-close", _columnEditorEntityName); }
                        catch (Exception ex) { Log($"ScanForLockedColumnPropertyDrift (column-editor) err: {ex.Message}"); }
                    }

                    // Edit-session diff (2026-05-17): if the user changed
                    // a watched property (e.g. cleared Owner) during the
                    // edit session, fire the scoped naming check now so the
                    // Required popup surfaces on close. This replaces the
                    // expensive per-tick drift loop that scaled badly on
                    // big models.
                    bool columnWatchedDriftFired = false;
                    if (_columnEditorEntityBaseline != null
                        && !string.IsNullOrEmpty(_columnEditorEntityName))
                    {
                        try { columnWatchedDriftFired = DiffWatchedPropertiesAndFire(_columnEditorEntityName, _columnEditorEntityBaseline); }
                        catch (Exception ex) { Log($"Column Editor close diff err: {ex.Message}"); }
                    }

                    // UDP-conditional naming re-evaluation, Column Editor
                    // variant (2026-05-24). Mirrors the Entity Editor branch
                    // so a parent-table UDP change made via this editor's
                    // own UDP grid (e.g. TableClass on the parent of the
                    // columns being edited) still triggers the engine's
                    // forward-apply + reverse-strip pair.
                    if (!columnWatchedDriftFired && !string.IsNullOrEmpty(_columnEditorEntityName))
                    {
                        try { RunScopedTableNamingCheck(_columnEditorEntityName); }
                        catch (Exception ex) { Log($"Column Editor close UDP-conditional check err: {ex.Message}"); }
                    }
                    _columnEditorEntityBaseline = null;
                    _columnEditorEntityName = null;
                }

                _columnEditorWasOpen = editorIsOpen;

                // --- Entity Editor (Table Properties) live UDP watcher ---
                // Same #32770 dialog class as Column Editor, title lacks
                // "Column 'X'". User opens it to change Entity-level UDPs
                // (TABLE_TYPE etc). We mirror the Glossary live-fire model:
                // every tick, read the entity's tracked UDP values and run
                // the naming check the moment any of them differs from the
                // open-time baseline. Eliminates the "wait until close"
                // delay - popup appears as soon as the UDP combo selection
                // commits.
                bool entityEditorIsOpen = IsEntityEditorOpen(out string entityActiveTable);
                _activeEntityEditorTable = entityEditorIsOpen ? entityActiveTable : null;

                // State-transition diagnostic (2026-05-21): without this we
                // cannot tell whether the dialog was missed by IsEntityEditorOpen
                // (title format mismatch) or by something downstream. Logs only
                // on the edge, not every tick.
                if (_entityEditorWasOpen != entityEditorIsOpen)
                {
                    Log($"Entity Editor state -> {(entityEditorIsOpen ? "OPEN" : "CLOSED")} activeTable='{entityActiveTable ?? "(null)"}'");
                }

                if (!_entityEditorWasOpen && entityEditorIsOpen && !string.IsNullOrEmpty(entityActiveTable))
                {
                    // Edit-session baseline (2026-05-17): captured ONCE
                    // on open for the naming-watched property set
                    // (Definition, Name_Qualifier, ...). Per the
                    // 2026-05-22 ROLLBACK decision we no longer capture
                    // the Table UDP snapshot here: reading
                    // entity.Properties("Entity.Physical.<udp>") on an
                    // entity whose UDP is in sparse / half-bound state
                    // appears to leave erwin's internal property cache
                    // in a state that crashes the UDP-tab grid renderer
                    // on subsequent open. Three iterations of Locked
                    // Table UDP enforcement (live-tick, close-edge,
                    // event-driven UDP-Editor) all produced reliable
                    // erwin crashes; we stop touching Table UDPs from
                    // the watchdog until a safe write path is found.
                    // Column-level Locked enforcement and naming-standard
                    // enforcement are unaffected (different code paths,
                    // proven safe in production).
                    try
                    {
                        _entityEditorBaseline = ReadEntityWatchedProperties(entityActiveTable);
                        _entityEditorName = entityActiveTable;
                        Log($"Entity Editor open baseline for '{entityActiveTable}': {_entityEditorBaseline.Count} watched property/ies [{string.Join(", ", _entityEditorBaseline.Select(kv => $"{kv.Key}='{kv.Value}'"))}]");
                    }
                    catch (Exception ex) { Log($"Entity Editor open baseline err for '{entityActiveTable}': {ex.Message}"); }
                }

                // Editor-close flush (2026-05-17): same drain as the
                // Column Editor branch above - the user closed the
                // Table Properties dialog, so any naming check that
                // DiagramHeartbeat deferred is now safe to surface.
                //
                // Re-entrancy is hostile here (2026-05-22): the close
                // handler shows modal dialogs (LockedUdpDialog,
                // AddinMessageDialog, RequiredFieldDialog), which pump
                // the message loop. While the dialog is up, this same
                // WindowMonitorTimer fires another tick. If we did not
                // flip _entityEditorWasOpen and steal the captured
                // snapshots up front, the re-entrant tick saw the same
                // close-edge conditions, ran EnforceLockedTableUdps
                // again with the still-set snapshot, and stacked
                // dialogs ad infinitum (user-visible bug: "çoklu popup
                // ve actionlogda sürekli WritingUdpValues"). Take the
                // state first, clear it, only THEN do any work that
                // can pump.
                if (_entityEditorWasOpen && !entityEditorIsOpen)
                {
                    // 2026-05-22 re-entrancy fix: take state up front,
                    // clear the instance fields, only THEN do any work
                    // that can pump (LockedUdpDialog modal etc.). See
                    // the comment block before this branch for why.
                    _entityEditorWasOpen = false;
                    var closedName = _entityEditorName;
                    var closedBaseline = _entityEditorBaseline;
                    _entityEditorBaseline = null;
                    _entityEditorName = null;

                    try { FlushPendingTableNamingChecks(); }
                    catch (Exception ex) { Log($"FlushPendingTableNamingChecks (entity) err: {ex.Message}"); }

                    // Event-driven rename scan: the Entity Editor was the
                    // ONE place where a Physical_Name edit produced a
                    // rename invisible to the heartbeat's count-delta gate
                    // (Gap B). Walk once on close to catch it.
                    try { ScanForRenamesEventDriven("entity-editor-close"); }
                    catch (Exception ex) { Log($"ScanForRenamesEventDriven (entity) err: {ex.Message}"); }

                    // Table-UDP delta enforcement DISABLED 2026-05-22:
                    // erwin crashed on UDP-tab click after our reads /
                    // writes touched a Locked Table UDP that erwin's
                    // sparse storage left half-bound. See open-edge
                    // comment for the full diagnosis. Naming-property
                    // diff below still fires (Definition / Name_Qualifier
                    // are built-in props, not subject to the UDP quirk).

                    // Predefined-column re-evaluation (2026-05-24): UDP grid
                    // edits inside the Table Properties dialog can newly
                    // satisfy a conditional predefined-column rule (e.g.
                    // TableClass changed to Log -> Log-conditional column
                    // must land on the entity). The legacy
                    // CheckForUdpValueChanges path used to do this; it is
                    // dead code, so we hook the editor-close edge instead.
                    // Idempotent through ApplyPredefinedColumnsToEntity's
                    // in-entity name check.
                    //
                    // MUST run BEFORE DiffWatchedPropertiesAndFire and
                    // RunScopedTableNamingCheck: ApplyNamingStandards may
                    // rename the entity (e.g. _HISTORY -> _LOG when
                    // TableClass changes), and our lookup matches by the
                    // captured closedName. Re-evaluating first sees the
                    // pre-rename name and finds the entity; the predefined-
                    // column add targets the entity reference, not its name.
                    if (!string.IsNullOrEmpty(closedName))
                    {
                        try { RunScopedReevaluateConditionalPredefinedColumns(closedName); }
                        catch (Exception ex) { Log($"Entity Editor close predefined-column re-eval err: {ex.Message}"); }
                    }

                    // Locked-column property drift scan (2026-05-25). Mirror
                    // of the Column Editor branch. Entity Editor's UDP grid
                    // can also affect rule applicability (TableClass switch
                    // flips which locked columns apply), so we scan here too.
                    if (!string.IsNullOrEmpty(closedName))
                    {
                        try { ScanForLockedColumnPropertyDrift("entity-editor-close", closedName); }
                        catch (Exception ex) { Log($"ScanForLockedColumnPropertyDrift (entity-editor) err: {ex.Message}"); }
                    }

                    // Edit-session diff for Table Properties dialog.
                    bool watchedDriftFired = false;
                    if (closedBaseline != null && !string.IsNullOrEmpty(closedName))
                    {
                        try { watchedDriftFired = DiffWatchedPropertiesAndFire(closedName, closedBaseline); }
                        catch (Exception ex) { Log($"Entity Editor close diff err: {ex.Message}"); }
                    }

                    // UDP-conditional naming re-evaluation (2026-05-24): the
                    // engine's ApplyNamingStandards forward-apply + reverse-
                    // strip pair handles "TableClass switched from Log to
                    // History" - rule#20 (cond=Log) becomes inapplicable and
                    // its '_LOG' suffix is stripped, rule#21 (cond=History)
                    // becomes applicable and adds '_HISTORY'.
                    if (!watchedDriftFired && !string.IsNullOrEmpty(closedName))
                    {
                        try { RunScopedTableNamingCheck(closedName); }
                        catch (Exception ex) { Log($"Entity Editor close UDP-conditional check err: {ex.Message}"); }
                    }
                }
                else
                {
                    // Normal (non-edge) state update.
                    _entityEditorWasOpen = entityEditorIsOpen;
                }

                // --- Inline-edit (Model Explorer label edit + diagram column
                // inline edit) close detection (Phase-2F, 2026-05-13).
                //
                // When the user adds a new column from Model Explorer or
                // double-clicks a column in the diagram, erwin pops up a
                // standard Win32 Edit class control. Typing happens in this
                // Edit window; pressing Enter / Tab / clicking-away destroys
                // it AND commits the value (or accepts the default name
                // unchanged - the bug we are fixing). We treat the transition
                // open -> closed as "the user committed", and force-validate
                // any pending placeholder-named attribute on the spot.
                //
                // The reason this is needed even with the existing pending
                // mechanism: when the user accepts the placeholder name as-is,
                // the attribute's Physical_Name never changes, so the
                // CheckEntityForChanges fingerprint diff never trips
                // ProcessAttributeChanges, and ValidateGlossary stays in its
                // placeholder-skip branch forever. The close-transition signal
                // is the missing UI commit edge that lets us tell apart "user
                // is still typing" from "user walked away".
                IntPtr erwinHwnd = IntPtr.Zero;
                try { erwinHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
                catch { /* leave zero - IsInlineEditActive bails on zero */ }
                bool inlineEditOpen = Win32Helper.IsInlineEditActive(erwinHwnd);
                if (_wasInlineEditOpen && !inlineEditOpen)
                {
                    try { ValidateCommittedPendingAttrs(); }
                    catch (Exception ex) { Log($"ValidateCommittedPendingAttrs err: {ex.Message}"); }

                    // Event-driven rename scan (Gap A): the inline edit
                    // just committed. ValidateCommittedPendingAttrs only
                    // covers attributes/entities that were ALREADY pending
                    // (placeholder name). A user double-clicking an
                    // existing real-named entity to rename it would not
                    // be in any pending set, so without this scan the
                    // rename was previously caught only by the now-removed
                    // per-tick Physical_Name walk.
                    try { ScanForRenamesEventDriven("inline-edit-close"); }
                    catch (Exception ex) { Log($"ScanForRenamesEventDriven (inline) err: {ex.Message}"); }

                    // Locked-column rename watch (2026-05-24, Gap C): the
                    // heartbeat's count-delta check never sees an existing-
                    // column rename because attr count is unchanged - the
                    // per-entity walk is only triggered when attrsGrew.
                    // Diagram inline-edit of an existing column commits in
                    // this same close edge, so we walk locked-column
                    // candidates vs live names here. Scope is intentionally
                    // narrow (only entities that hold a locked-rule-named
                    // snapshot) to keep the popup delay sub-second; on big
                    // models the broad walk took several seconds.
                    try { ScanForLockedColumnRenames("inline-edit-close"); }
                    catch (Exception ex) { Log($"ScanForLockedColumnRenames (inline) err: {ex.Message}"); }
                }
                _wasInlineEditOpen = inlineEditOpen;
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

        /// <summary>
        /// Phase-2F (2026-05-13): force-validate every pending placeholder-named
        /// attribute. Called by WindowMonitorTimer_Tick when an inline-edit
        /// (Model Explorer label edit / diagram column inline edit) closes -
        /// that is the moment the user committed the attribute, with whatever
        /// name was in the edit box (including the unchanged "_default_"
        /// placeholder, which is the bug we are closing).
        ///
        /// For every pending attr we:
        ///   - locate the owner entity via Collect(root, "Entity") and match by
        ///     ObjectId (single linear walk, ~280 entities, ~5 ms);
        ///   - locate the attr inside it via Collect(entity, "Attribute");
        ///   - build a fresh AttributeValidationSnapshot for the current state;
        ///   - call ValidateGlossary with bypassPlaceholderSkip=true so the
        ///     placeholder name itself triggers the "not in glossary" popup;
        ///   - drop the entry from _pendingNamedAttrs / _pendingAttrAddedAt.
        ///
        /// We do NOT update _attributeSnapshots here - the existing record was
        /// written when CheckEntityForChanges first saw the attr, and the only
        /// thing that may have changed is that the user committed without
        /// editing. The next heartbeat (or Column Editor scoped scan) catches
        /// any real follow-up edit through the normal fingerprint diff path.
        /// </summary>
        private void ValidateCommittedPendingAttrs()
        {
            // Entity-level commit edge work also lives here (Phase-2G) so the
            // entry guard checks BOTH pending sets.
            if (_pendingNamedAttrs.Count == 0 && _pendingNamedEntities.Count == 0) return;
            if (_validationSuspended) return;
            if (_session == null || _sessionLost) return;

            dynamic modelObjects = null;
            dynamic root = null;
            dynamic allEntities = null;
            // Declared at method scope so the finally block can drain
            // _creationGestureEntityIds for every queued id even if the
            // try body throws mid-walk.
            var entitiesToNamingCheck = new List<(string EntityId, string FallbackName, bool IsNew)>();
            try
            {
                try { modelObjects = _session.ModelObjects; root = modelObjects?.Root; }
                catch (Exception ex) { Log($"ValidateCommittedPendingAttrs: ModelObjects err: {ex.Message}"); return; }
                if (root == null) return;
                try { allEntities = modelObjects.Collect(root, "Entity"); }
                catch (Exception ex) { Log($"ValidateCommittedPendingAttrs: entity collect err: {ex.Message}"); return; }
                if (allEntities == null) return;

                // Snapshot the owner keys so we can mutate _pendingNamedAttrs
                // while iterating without throwing on a modified collection.
                var ownerIds = new List<string>(_pendingNamedAttrs.Keys);

                int totalFired = 0;
                _pendingResults.Clear();
                // Queue table-name validations for after the walk - same reason
                // as DiagramHeartbeatTick: RunScopedTableNamingCheck reopens
                // its own Collect("Entity") enumerator.
                // ValidateCommittedPendingAttrs path: every entity here is a
                // pending placeholder whose inline-edit just closed - it is
                // effectively a creation gesture, so isNew=true uniformly.
                //
                // We queue by ObjectId, NOT by name. Between this queue point
                // and the drain (~line 2783) a nested FireNewEntityPipeline ->
                // Required UDP prompt -> FlushPendingTableNamingChecks chain
                // can fire an ApplyOn=Both AutoApply=true Suffix rule (e.g.
                // rule#21 TableClass=History -> '_HISTORY' suffix) and silently
                // rename the entity. A name-keyed queue would then call
                // RunScopedTableNamingCheck with the obsolete name, the
                // entity-walk lookup inside it would miss, and every
                // isNew=true rule (rule#17 Vp prefix, rule#1019 Required
                // Name_Qualifier, rule#1022 Length Definition) would never
                // evaluate. Verified 2026-05-31 against owner_test +
                // TableClass=History. FallbackName is kept only for the
                // entity-disappeared race (delete during the modal pump).
                // (Hoisted to method scope above so the finally block can
                // clear _creationGestureEntityIds for every queued id
                // even if this body throws mid-walk.)

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;
                    string entityId = null;
                    try { entityId = entity.ObjectId?.ToString(); } catch { continue; }
                    if (string.IsNullOrEmpty(entityId)) continue;
                    bool hasPendingAttrs = _pendingNamedAttrs.TryGetValue(entityId, out var pendSet)
                                           && pendSet != null && pendSet.Count > 0;
                    bool hasPendingEntity = _pendingNamedEntities.Contains(entityId);
                    if (!hasPendingAttrs && !hasPendingEntity) continue;

                    string tableName = GetTableName(entity);

                    // Phase-2G: entity-level commit edge. Read the live name
                    // off the entity (don't use the cached snapshot, the user
                    // may have just renamed it) and queue a naming check
                    // regardless of placeholder state - the naming standard
                    // regex will reject the placeholder deterministically and
                    // accept a real name. Either result is the right one.
                    if (hasPendingEntity)
                    {
                        string liveEntityName = tableName;
                        Log($"[PENDING-ENTITY] commit edge for entityId={entityId} liveName='{liveEntityName}' - queuing naming check (isNew=true)");
                        entitiesToNamingCheck.Add((entityId, liveEntityName, IsNew: true));
                        _pendingNamedEntities.Remove(entityId);
                        // Bridge for the nested ScanForRenamesEventDriven
                        // path fired during the Required-UDP modal pump:
                        // _pendingNamedEntities is now empty for this id,
                        // but the gesture is still active until the drain
                        // at line ~2850 runs. Cleared in the finally block.
                        _creationGestureEntityIds.Add(entityId);

                        // Fire the new-entity pipeline (model standards,
                        // question wizard, UDP defaults, unconditional
                        // predefined columns, PK promotion) NOW that the
                        // inline-edit Win32 Edit control closed. The close
                        // is an explicit user gesture (Enter / Tab / click
                        // away), so whatever liveEntityName is at this
                        // point is what the user committed - even a
                        // placeholder-shaped name like "E/38" counts,
                        // because the user pressed Enter to accept it.
                        // Verified missing-fire on 2026-05-14: accepting
                        // 'E/38' as-is left the table with no predefined
                        // columns, while renaming to 'DENEME' fired the
                        // pipeline correctly. Without this every "accept
                        // default name" path silently skipped standards,
                        // UDP defaults, unconditional predefined columns,
                        // and PK promotion. The naming standard regex
                        // still runs alongside (entitiesToNamingCheck) and
                        // will flag the placeholder name with a separate
                        // warning - both outcomes are independent.
                        FireNewEntityPipeline(entity, liveEntityName);
                    }

                    if (!hasPendingAttrs) continue; // entity-only pending was handled above
                    var predefined = GetPredefinedColumnNames(entity);

                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                    catch (Exception ex) { Log($"ValidateCommittedPendingAttrs: attr collect err on '{tableName}': {ex.Message}"); continue; }
                    if (entityAttrs == null) continue;

                    try
                    {
                        foreach (dynamic attr in entityAttrs)
                        {
                            if (attr == null) continue;
                            string aid = null;
                            try { aid = attr.ObjectId?.ToString(); } catch { continue; }
                            if (string.IsNullOrEmpty(aid)) continue;
                            if (!pendSet.Contains(aid)) continue;

                            var snapshot = CreateSnapshot(attr, tableName, modelObjects);
                            string currentName = snapshot?.PhysicalName ?? "";

                            if (!IsPlaceholderColumnName(currentName))
                            {
                                // The user typed a real name. The heartbeat
                                // would normally catch this through
                                // CheckEntityForChanges' fingerprint diff
                                // (rename branch fires ValidateGlossary), but
                                // the inline-edit close edge arrives before
                                // the next heartbeat tick - and if we just
                                // clean up here the heartbeat will never see
                                // this entity as pending again, so the rename
                                // would slip through unvalidated. Drive
                                // ProcessAttributeChanges ourselves using the
                                // stored placeholder snapshot as the previous
                                // state. Carry over UDP / term-type fields
                                // the same way CheckEntityForChanges does.
                                if (_attributeSnapshots.TryGetValue(aid, out var previousState))
                                {
                                    snapshot.TermTypeCanonical = previousState.TermTypeCanonical;
                                    foreach (var kvp in previousState.UdpValues)
                                        snapshot.UdpValues[kvp.Key] = kvp.Value;
                                    if (string.IsNullOrEmpty(snapshot.PhysicalDataType))
                                        snapshot.PhysicalDataType = previousState.PhysicalDataType;

                                    Log($"[PENDING-NAME] entity='{tableName}' attr id={aid} renamed to '{currentName}' during inline-edit - running rename validation");
                                    ProcessAttributeChanges(attr, previousState, snapshot, predefined);
                                    _attributeSnapshots[aid] = snapshot;
                                }
                                else
                                {
                                    // No baseline (race?). Run new-attribute
                                    // validation to be safe; ProcessNewAttribute
                                    // handles the glossary vs domain branch.
                                    Log($"[PENDING-NAME] entity='{tableName}' attr id={aid} renamed to '{currentName}' (no prior snapshot) - running new-attr validation");
                                    _attributeSnapshots[aid] = snapshot;
                                    ProcessNewAttribute(attr, snapshot, predefined);
                                }

                                pendSet.Remove(aid);
                                _pendingAttrAddedAt.Remove(aid);
                                totalFired += _pendingResults.Count;
                                continue;
                            }

                            Log($"[PENDING-NAME] entity='{tableName}' attr id={aid} committed with placeholder name='{currentName}' - force-validating glossary");
                            ValidateGlossary(attr, snapshot, predefined, bypassPlaceholderSkip: true);
                            totalFired += _pendingResults.Count;

                            pendSet.Remove(aid);
                            _pendingAttrAddedAt.Remove(aid);
                        }
                    }
                    finally { ReleaseCom(entityAttrs); }

                    if (pendSet.Count == 0) _pendingNamedAttrs.Remove(entityId);
                }

                // Owners whose entities were not found in the walk above are
                // stale; clear them too so the dict doesn't accumulate ghosts.
                foreach (var orphanOwner in ownerIds)
                {
                    if (!_pendingNamedAttrs.ContainsKey(orphanOwner)) continue;
                    // We did not visit this owner above (entity disappeared);
                    // drop its pending attrs from the timestamp dict.
                    foreach (var aid in _pendingNamedAttrs[orphanOwner]) _pendingAttrAddedAt.Remove(aid);
                    _pendingNamedAttrs.Remove(orphanOwner);
                    Log($"[PENDING-NAME] cleared stale pending owner entityId={orphanOwner} (entity no longer in model)");
                }

                if (_pendingResults.Count > 0)
                {
                    Log($"ValidateCommittedPendingAttrs: fired {_pendingResults.Count} result(s) on inline-edit close");
                    ShowConsolidatedPopup();
                }
                else if (totalFired == 0 && _pendingNamedAttrs.Count == 0)
                {
                    // Pure-cleanup path: every pending entry was already renamed
                    // before the close edge fired. Nothing to surface.
                }

                // Phase-2G: drain table-name validation queue after the walk
                // and after the popup. RunScopedTableNamingCheck itself shows
                // its own modal popup; sequencing this AFTER ShowConsolidatedPopup
                // keeps the two streams (glossary results from attrs, naming
                // standard result from the entity) from racing the same modal
                // pump and re-entering the timer through the message loop.
                //
                // Bug fix 2026-05-31 (History+Both+Required scenario): between
                // queueing (line 2652) and this drain, a nested
                // FireNewEntityPipeline -> Required UDP prompt ->
                // FlushPendingTableNamingChecks chain can auto-rename the
                // entity via an ApplyOn=Both AutoApply=true conditional Suffix
                // rule. Re-walk the model by ObjectId here and use the LIVE
                // Physical_Name; fall back to the queued name only if the
                // entity was deleted out from under us during the modal pump.
                if (entitiesToNamingCheck.Count > 0)
                {
                    var liveNameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    dynamic resolveAllEntities = null;
                    try
                    {
                        resolveAllEntities = modelObjects.Collect(root, "Entity");
                        if (resolveAllEntities != null)
                        {
                            var queuedIds = new HashSet<string>(
                                entitiesToNamingCheck.Select(q => q.EntityId),
                                StringComparer.OrdinalIgnoreCase);
                            foreach (dynamic e in resolveAllEntities)
                            {
                                if (e == null) continue;
                                string eid = null;
                                try { eid = e.ObjectId?.ToString(); } catch { continue; }
                                if (string.IsNullOrEmpty(eid) || !queuedIds.Contains(eid)) continue;
                                string liveName = null;
                                try { liveName = GetTableName(e); } catch { /* keep null - falls back */ }
                                if (!string.IsNullOrEmpty(liveName))
                                    liveNameById[eid] = liveName;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"ValidateCommittedPendingAttrs: live-name re-resolve walk threw {ex.GetType().Name}: {ex.Message} - falling back to queued names for all entries");
                    }
                    finally { ReleaseCom(resolveAllEntities); }

                    foreach (var (entityId, fallbackName, isNew) in entitiesToNamingCheck)
                    {
                        string resolvedName = liveNameById.TryGetValue(entityId, out var live) ? live : fallbackName;
                        if (!string.Equals(resolvedName, fallbackName, StringComparison.Ordinal))
                            Log($"ValidateCommittedPendingAttrs: entityId={entityId} renamed from queued '{fallbackName}' to live '{resolvedName}' between queue + drain - using live name for scoped naming check (isNew={isNew})");
                        // entitiesToNamingCheck entries originate exclusively
                        // from a [PENDING-ENTITY] placeholder-commit, so
                        // isNew is true uniformly - the strict ApplyOn
                        // gate filters Update rules correctly without any
                        // gesture-widening flag.
                        try { RunScopedTableNamingCheck(resolvedName, isNew: isNew); }
                        catch (Exception ex) { Log($"ValidateCommittedPendingAttrs: naming check err for '{resolvedName}': {ex.Message}"); }
                    }
                }
            }
            finally
            {
                // Drain complete (or thrown out) - the creation gesture is
                // over for every entity we tracked. Clear the bridge set so
                // a subsequent unrelated rename detection on the same id
                // does not incorrectly widen its rule gate. Best-effort:
                // try/catch wraps the whole loop because the finally must
                // never throw on top of an in-flight exception.
                try
                {
                    foreach (var (eid, _, _) in entitiesToNamingCheck)
                    {
                        if (!string.IsNullOrEmpty(eid))
                            _creationGestureEntityIds.Remove(eid);
                    }
                }
                catch (Exception ex) { Log($"ValidateCommittedPendingAttrs.finally: _creationGestureEntityIds cleanup threw {ex.GetType().Name}: {ex.Message}"); }

                ReleaseCom(allEntities);
                ReleaseCom(root);
                ReleaseCom(modelObjects);
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

                string windowTitle = Win32Helper.GetWindowTextNoHang(hWnd);

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
        /// Detects erwin's UDP Editor dialog (Tools menu &gt; User Defined
        /// Properties). Title shape verified 2026-05-22 against r10.10:
        ///   "User Defined Properties : Physical"
        ///   "User Defined Properties : Logical"
        /// We match on the leading text since the suffix varies by the
        /// Logical/Physical view selector. Window class is the standard
        /// #32770 dialog so we filter by title only.
        /// </summary>
        private bool IsUdpEditorOpen()
        {
            bool found = false;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                string t = Win32Helper.GetWindowTextNoHang(hWnd);
                if (t.StartsWith("User Defined Properties", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Detects the Entity Editor (Table Properties) dialog on the same
        /// <c>#32770</c> class the Column Editor uses. Title shape verified
        /// 2026-05-07 against erwin r10.10 with English UI:
        ///   "SQL Server Table 'TEST_DATA_TAB_178' Editor"
        /// Distinguishing feature: <b>no</b> "Column '<i>name</i>'" segment.
        /// We extract the table name from the first single-quoted token; if
        /// the title is unparseable we still report "open" with an empty
        /// name so the close-transition handler can no-op cleanly.
        /// </summary>
        private bool IsEntityEditorOpen(out string activeTable)
        {
            string foundTable = null;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                string windowTitle = Win32Helper.GetWindowTextNoHang(hWnd);

                // Entity Editor: "Table '...' Editor" with NO "Column '...'".
                if (windowTitle.Contains("Table '")
                    && windowTitle.EndsWith("Editor", StringComparison.Ordinal)
                    && !windowTitle.Contains("Column '"))
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

                            if (!EntityNameMatchesTitle(entityName, scopeTable))
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
        /// Forward a "new entity discovered with a real name" event to
        /// <see cref="TableTypeMonitorService.OnNewEntityDetected"/>. Restores the
        /// wizard / model-standards / UDP-defaults pipeline that the legacy
        /// <c>TableTypeMonitorService.CheckForTableTypeChanges</c> isNew branch
        /// used to drive; that periodic path was removed in Phase-2D (2026-05-06)
        /// but the diagram-side replacement only kept the naming-check delegation.
        ///
        /// Errors are swallowed at this boundary because the caller is in the
        /// middle of walking COM enumerators and a wizard-side exception must
        /// not abort the rest of the heartbeat tick (other entities still need
        /// their snapshots updated).
        /// </summary>
        private void FireNewEntityPipeline(dynamic entity, string physicalName)
        {
            if (_tableTypeMonitor == null) return;
            // Modal-stacking guard (2026-05-23): OnNewEntityDetected synchronously
            // opens RequiredUdpForm.ShowDialog when admin marked any UDP
            // IS_REQUIRED=true on the entity's class (e.g. TableClass on FibaEmre).
            // Its modal pump runs the WindowMonitorTimer which would otherwise
            // see an inline-edit close or editor-open transition fired by the
            // same user gesture and open RequiredFieldDialog for a naming-rule
            // violation ON TOP of the first popup. Reusing the existing
            // _scopedCheckInProgress flag tells RunScopedTableNamingCheck (the
            // only entry to RequiredFieldDialog from the other code paths) to
            // skip while the new-entity pipeline owns the modal. The deferred
            // naming check still surfaces afterwards via the entitiesToNamingCheck
            // drain at the end of DiagramHeartbeatTick.
            bool acquired = !_scopedCheckInProgress;
            if (acquired) _scopedCheckInProgress = true;
            try
            {
                _tableTypeMonitor.OnNewEntityDetected(entity, physicalName);
            }
            catch (Exception ex)
            {
                Log($"FireNewEntityPipeline error on '{physicalName}': {ex.Message}");
            }
            finally
            {
                if (acquired) _scopedCheckInProgress = false;

                // Rapid-create flush (2026-05-24): when the user creates
                // multiple new tables in quick succession, each one's
                // PromptForMissingRequiredUdps opens a modal whose pump
                // re-runs the WindowMonitorTimer; that timer triggers a
                // nested ValidateCommittedPendingAttrs which queues its own
                // entitiesToNamingCheck and tries to drain them via
                // RunScopedTableNamingCheck - but the outer FireNewEntityPipeline
                // here still holds _scopedCheckInProgress, so every nested
                // drain falls through to the defer-to-pending branch added
                // in RunScopedTableNamingCheck. Without an explicit flush
                // here those deferred checks only surface when the user
                // closes the editor, which is too late if they expect the
                // Description popup chain to fire before walking away. The
                // outermost FireNewEntityPipeline finally is the right point
                // to drain - the gate is now released, so each pending
                // entry's scoped naming check can run cleanly (each opens
                // its own RequiredFieldDialog, sequenced through the same
                // gate which it now claims).
                if (acquired)
                {
                    try { FlushPendingTableNamingChecks(); }
                    catch (Exception ex) { Log($"FireNewEntityPipeline: FlushPendingTableNamingChecks err: {ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// Phase-2D close-race fix (2026-05-07): final scoped validation pass on the
        /// table whose Column Editor just closed. Required because erwin commits the
        /// typed value when the user clicks Close without first pressing Tab/Enter,
        /// and that commit lands AFTER the MonitorTimer's last scoped tick - leaving
        /// the edit invisible to the per-keystroke scan path. Runs CheckEntityForChanges
        /// once and shows any pending popups, then returns. Idempotent: if no diff,
        /// nothing fires.
        /// </summary>
        /// <summary>
        /// Scoped naming-standard check on the table the user just opened in
        /// the Column Editor. Mirrors the Glossary flow that scopes work to
        /// the active editor's parent: we walk Entities once, find the match
        /// by Physical_Name (case-insensitive), then delegate to
        /// <see cref="TableTypeMonitorService.ValidateNamingStandard"/> which
        /// owns the AUTO_APPLY=true silent path, AUTO_APPLY=false YesNo
        /// prompt, and the final regex/length warning. UDP-conditional rules
        /// (DEPENDS_ON_UDP_ID) read live UDP values off the entity and apply
        /// only when the condition matches.
        /// </summary>
        private void RunScopedTableNamingCheck(string tableName, IDictionary<string, string> baselineOverride = null, bool isNew = false)
        {
            if (string.IsNullOrEmpty(tableName)) return;
            if (_validationSuspended) return;
            if (_sessionLost || _disposed) return;
            if (_tableTypeMonitor == null) return;
            if (!NamingStandardService.Instance.IsLoaded) return;

            // Creation-gesture bridge (2026-06-01): see
            // IsEntityInCreationGesture XML doc. Centralised so the
            // direct ValidateNamingStandard caller (Required-input
            // re-run loop inside TableTypeMonitorService) can use the
            // same probe via the injected delegate.
            if (!isNew && IsEntityInCreationGesture(tableName))
            {
                Log($"RunScopedTableNamingCheck: '{tableName}' is in active creation gesture - overriding isNew=False to True so Create-only rules evaluate");
                isNew = true;
            }
            // Reentrancy guard. The downstream ValidateNamingStandard opens
            // a modal dialog whose pump fires our WindowMonitorTimer again -
            // re-entry would stack popups indefinitely. When the gate is
            // held by an OUTER FireNewEntityPipeline (e.g. rapid table
            // create where each PromptForMissingRequiredUdps modal pumps a
            // nested ValidateCommittedPendingAttrs), we DEFER the entity
            // into the pending queue instead of dropping it. The outermost
            // FireNewEntityPipeline.finally and the editor-close transitions
            // both flush the queue, so the deferred entries surface as soon
            // as the gate releases. baselineOverride is intentionally lost
            // on the deferred path - the Edit-session diff that owns it
            // (DiffWatchedPropertiesAndFire) only ever runs from
            // WindowMonitorTimer's already-non-reentrant editor-close
            // branch, so a defer collision there is impossible in practice.
            if (_scopedCheckInProgress)
            {
                if (_pendingTableNamingChecks.Add((tableName, isNew)))
                    Log($"RunScopedTableNamingCheck: deferring '{tableName}' (isNew={isNew}) - check already in progress");
                return;
            }
            _scopedCheckInProgress = true;
            try { RunScopedTableNamingCheckCore(tableName, baselineOverride, isNew); }
            finally { _scopedCheckInProgress = false; }
        }

        private void RunScopedTableNamingCheckCore(string tableName, IDictionary<string, string> baselineOverride = null, bool isNew = false)
        {

            dynamic modelObjects = null;
            dynamic root = null;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelObjects?.Root;
            }
            catch (Exception ex) { Log($"RunScopedTableNamingCheck: ModelObjects err: {ex.Message}"); return; }
            if (root == null) return;

            dynamic allEntities = null;
            try { allEntities = modelObjects.Collect(root, "Entity"); }
            catch (Exception ex) { Log($"RunScopedTableNamingCheck: Collect err: {ex.Message}"); return; }
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

                    if (!EntityNameMatchesTitle(nameForMatch, tableName))
                        continue;

                    Log($"Scoped naming check on '{nameForMatch}' (isNew={isNew})");
                    _tableTypeMonitor.ValidateNamingStandard("Table", nameForMatch, entity, baselineOverride: baselineOverride, isNew: isNew);
                    return;
                }
            }
            catch (Exception ex) { Log($"RunScopedTableNamingCheck err: {ex.Message}"); }
        }

        /// <summary>
        /// Read every UDP value the active naming-standard rules condition on
        /// for the entity matching <paramref name="tableName"/>. Returns an
        /// empty dictionary if nothing matches or if no rules carry a UDP
        /// condition. Case-insensitive on both UDP name and table name.
        /// Called every tick while the Entity Editor is open, so the body is
        /// kept lean: no rule load, just one Collect + filter + targeted
        /// property reads per relevant UDP.
        /// </summary>
        private Dictionary<string, string> ReadEntityRelevantUdpValues(string tableName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(tableName)) return result;

            var udpNames = NamingStandardService.Instance.GetRelevantUdpNames();
            if (udpNames.Count == 0) return result;

            try
            {
                dynamic modelObjects = _session?.ModelObjects;
                dynamic root = modelObjects?.Root;
                if (root == null) return result;
                dynamic entities = modelObjects.Collect(root, "Entity");
                if (entities == null) return result;

                foreach (dynamic entity in entities)
                {
                    if (entity == null) continue;
                    string physName;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        physName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                    }
                    catch { continue; }

                    if (!EntityNameMatchesTitle(physName, tableName))
                        continue;

                    foreach (var udp in udpNames)
                    {
                        try
                        {
                            string val = entity.Properties($"Entity.Physical.{udp}").Value?.ToString() ?? "";
                            result[udp] = val;
                        }
                        catch { result[udp] = ""; }
                    }
                    break;
                }
            }
            catch (Exception ex) { Log($"ReadEntityRelevantUdpValues err: {ex.Message}"); }
            return result;
        }

        /// <summary>
        /// Read every Table UDP value the addin needs to track live for the
        /// entity matching <paramref name="tableName"/>. Superset of
        /// <see cref="ReadEntityRelevantUdpValues"/>: also includes UDPs
        /// flagged IS_LOCKED or IS_REQUIRED so the live-editor delta loop
        /// can enforce them even when no naming rule references them.
        /// Returns an empty dict if no UDPs match - caller compares by
        /// equality so an empty baseline matches an empty current and
        /// produces no false fire.
        /// </summary>
        private Dictionary<string, string> ReadEntityTrackedUdpValues(string tableName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(tableName)) return result;

            var udpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in NamingStandardService.Instance.GetRelevantUdpNames())
                if (!string.IsNullOrEmpty(n)) udpNames.Add(n);

            if (UdpDefinitionService.Instance.IsLoaded)
            {
                foreach (var def in UdpDefinitionService.Instance.GetByObjectType("Table"))
                {
                    if (def == null || string.IsNullOrEmpty(def.Name)) continue;
                    if (def.IsLocked || def.IsRequired) udpNames.Add(def.Name);
                }
            }
            if (udpNames.Count == 0) return result;

            try
            {
                dynamic modelObjects = _session?.ModelObjects;
                dynamic root = modelObjects?.Root;
                if (root == null) return result;
                dynamic entities = modelObjects.Collect(root, "Entity");
                if (entities == null) return result;

                foreach (dynamic entity in entities)
                {
                    if (entity == null) continue;
                    string physName;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        physName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                    }
                    catch { continue; }

                    if (!EntityNameMatchesTitle(physName, tableName))
                        continue;

                    foreach (var udp in udpNames)
                    {
                        try
                        {
                            string val = entity.Properties($"Entity.Physical.{udp}").Value?.ToString() ?? "";
                            result[udp] = val;
                        }
                        catch { result[udp] = ""; }
                    }
                    break;
                }
            }
            catch (Exception ex) { Log($"ReadEntityTrackedUdpValues err: {ex.Message}"); }
            return result;
        }

        /// <summary>
        /// Apply Locked-UDP enforcement to a live Entity Editor delta. For
        /// every UDP whose baseline value was non-empty and whose new value
        /// differs, if the admin definition flags <c>IS_LOCKED=true</c>,
        /// revert the change in-place via SCAPI. The initial empty -> value
        /// seed is intentionally NOT blocked (mirrors the Column-level
        /// behaviour in <see cref="EnforceLockedAttributeUdps"/>: wizards
        /// and defaults can still populate the field on a new entity).
        /// Reverts surface a single dialog so the user knows the change
        /// was rejected; the snapshot caller re-reads UDP values after
        /// this method so the next tick does not re-fire the same delta.
        /// </summary>
        private void EnforceLockedTableUdps(string tableName, Dictionary<string, string> baseline, Dictionary<string, string> current)
        {
            if (string.IsNullOrEmpty(tableName) || baseline == null || current == null) return;
            if (!UdpDefinitionService.Instance.IsLoaded) return;
            if (_tableTypeMonitor == null) return;

            dynamic targetEntity = null;
            try
            {
                dynamic modelObjects = _session?.ModelObjects;
                dynamic root = modelObjects?.Root;
                if (root == null) return;
                dynamic entities = modelObjects.Collect(root, "Entity");
                if (entities == null) return;

                foreach (dynamic entity in entities)
                {
                    if (entity == null) continue;
                    string physName;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        physName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                    }
                    catch { continue; }
                    if (EntityNameMatchesTitle(physName, tableName))
                    {
                        targetEntity = entity;
                        break;
                    }
                }
            }
            catch (Exception ex) { Log($"EnforceLockedTableUdps: entity lookup err: {ex.Message}"); return; }

            if (targetEntity == null) return;

            foreach (var kv in current)
            {
                string newVal = kv.Value ?? "";
                baseline.TryGetValue(kv.Key, out var baseVal);
                baseVal = baseVal ?? "";
                if (string.Equals(baseVal, newVal, StringComparison.Ordinal)) continue;

                var def = UdpDefinitionService.Instance.GetByName("Table", kv.Key);
                if (def == null || !def.IsLocked) continue;
                if (string.IsNullOrEmpty(baseVal)) continue; // initial seed allowed

                try
                {
                    var failed = _udpRuntimeService?.WriteUdpValuesWithFailures(
                        (object)targetEntity,
                        new Dictionary<string, string> { [kv.Key] = baseVal },
                        "Table") ?? new List<string> { kv.Key };

                    if (failed.Contains(kv.Key))
                    {
                        // SCAPI rejected the revert (Properties Collection
                        // Filter quirk on UDPs the entity never bound). Fail
                        // loud so the user knows the lock did not stick and
                        // can intervene manually via erwin's UDP grid - the
                        // earlier "reverted" log line was a misreport that
                        // hid the same bug for several sessions.
                        Log($"EnforceLockedTableUdps: SCAPI rejected revert for '{kv.Key}' on '{tableName}' - lock NOT enforced");
                        Forms.AddinMessageDialog.Show(
                            $"'{kv.Key}' UDP'si kilitli ancak SCAPI yazimi reddetti, eski deger geri yazilamadi.\n\n" +
                            $"Yeni deger ('{newVal}') modelde kalacak.\n\n" +
                            $"Eski degeri ('{baseVal}') geri yazmak icin erwin'in UDP grid'inden manuel olarak girin.",
                            "UDP Kilitli - Revert Basarisiz",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Warning);
                        continue;
                    }

                    Log($"Locked Table UDP '{kv.Key}' on '{tableName}' reverted '{newVal}' -> '{baseVal}'");
                    Forms.LockedUdpDialog.Show(kv.Key, newVal, baseVal);

                    // Belt-and-suspenders re-revert after the modal closes:
                    // erwin's UDP grid sometimes re-commits the user's edit
                    // (the "empty" cell value) during the modal pump,
                    // overwriting our first revert. Re-asserting the baseline
                    // here makes the lock stick whatever erwin did under us.
                    // Cheap: same write call, no-op on the model side when the
                    // value already matches.
                    try
                    {
                        _udpRuntimeService?.WriteUdpValuesWithFailures(
                            (object)targetEntity,
                            new Dictionary<string, string> { [kv.Key] = baseVal },
                            "Table");
                    }
                    catch (Exception postEx)
                    {
                        Log($"EnforceLockedTableUdps: post-dialog re-revert failed for '{kv.Key}' on '{tableName}': {postEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"EnforceLockedTableUdps: revert failed for '{kv.Key}' on '{tableName}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Returns true if any tracked UDP value differs between the two
        /// snapshots. New keys appearing on the right side count as a delta;
        /// missing keys do not (rule set may have shrunk between ticks).
        /// </summary>
        private static bool HasUdpDelta(Dictionary<string, string> oldSnap, Dictionary<string, string> newSnap)
        {
            if (newSnap == null) return false;
            foreach (var kv in newSnap)
            {
                oldSnap.TryGetValue(kv.Key, out var oldVal);
                if (!string.Equals(oldVal ?? "", kv.Value ?? "", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

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

                    if (!EntityNameMatchesTitle(nameForMatch, tableName))
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
                    // Capture current values of locked Column UDPs so the first
                    // diff in CheckEntityForChanges has something non-empty to
                    // compare against. CreateSnapshot intentionally skips UDP
                    // reads (lazy-on-demand on big models); the locked subset is
                    // small enough to read up-front without breaking that budget.
                    BaselineLockedAttributeUdps(attr, snap);
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
                                    //
                                    // BUT: locked Column UDP enforcement must still run.
                                    // Editing a UDP cell in the Column Editor does NOT
                                    // touch Physical_Name or Physical_Data_Type, so the
                                    // Phase-2A short-circuit hides every UDP change from
                                    // the slow path below. Run the lock check here before
                                    // continuing so locked UDPs are still reverted.
                                    EnforceLockedAttributeUdps(attr, objectId, existingSnap.PhysicalName);

                                    // Same blind-spot for cascade-source UDPs (e.g.
                                    // DATA_CATEGORY -> KVKK / INTEGRITY / CONFIDENTIALITY
                                    // auto-fill via DependencySetRuntimeService). Editing
                                    // a non-locked cascade-source UDP does not touch
                                    // Physical_Name / Physical_Data_Type either, so the
                                    // fast-pass was silently bypassing the cascade
                                    // evaluation in the slow path - user reported
                                    // 2026-05-31 that selecting DATA_CATEGORY on a
                                    // column did NOT auto-fill its dependent UDPs.
                                    // CheckAttributeUdpDependencies is read-only +
                                    // idempotent (compare-against-snapshot, fire handler
                                    // only on a real delta), so calling it here in the
                                    // fast path costs ~1-3 property reads per cascade-
                                    // source UDP without re-running CreateSnapshot.
                                    CheckAttributeUdpDependencies(attr, objectId, existingSnap.PhysicalName);
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

                            // Phase-2E pending-name tracking. Model Explorer's "New Column"
                            // creates the attribute with name '<default>' as a distinct edit
                            // from the user typing the real name, so ValidateGlossary skipped
                            // it on this tick. Remember the entity so the heartbeat keeps
                            // rescanning until the user types something - the next tick's
                            // CheckEntityForChanges fingerprint diff will then fire
                            // ProcessAttributeChanges (rename branch) on the rename.
                            if (IsPlaceholderColumnName(currentState.PhysicalName))
                            {
                                string entOwnerId = null;
                                try { entOwnerId = entity.ObjectId?.ToString(); } catch { /* tracked below */ }
                                if (!string.IsNullOrEmpty(entOwnerId))
                                {
                                    if (!_pendingNamedAttrs.TryGetValue(entOwnerId, out var pendSet))
                                    {
                                        pendSet = new HashSet<string>(StringComparer.Ordinal);
                                        _pendingNamedAttrs[entOwnerId] = pendSet;
                                    }
                                    pendSet.Add(objectId);
                                    _pendingAttrAddedAt[objectId] = DateTime.UtcNow;
                                    Log($"[PENDING-NAME] entity='{tableName}' attr id={objectId} name='{currentState.PhysicalName}' - heartbeat will keep watching until renamed");
                                }
                            }
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

                            // Pending cleanup: was the attr's name a placeholder and is
                            // it now resolved? Drop it from the watch list so we stop
                            // forcing rescans on this entity.
                            if (IsPlaceholderColumnName(previousState.PhysicalName) && !IsPlaceholderColumnName(currentState.PhysicalName))
                            {
                                string entOwnerId = null;
                                try { entOwnerId = entity.ObjectId?.ToString(); } catch { /* best effort */ }
                                if (!string.IsNullOrEmpty(entOwnerId) && _pendingNamedAttrs.TryGetValue(entOwnerId, out var pendSet))
                                {
                                    if (pendSet.Remove(objectId))
                                    {
                                        Log($"[PENDING-NAME] entity='{tableName}' attr id={objectId} resolved to '{currentState.PhysicalName}' - removed from pending");
                                        if (pendSet.Count == 0) _pendingNamedAttrs.Remove(entOwnerId);
                                    }
                                }
                                _pendingAttrAddedAt.Remove(objectId);
                            }
                        }

                        // Locked Column UDP enforcement runs BEFORE the
                        // dependency cascade check on purpose: dependency
                        // writes done by CheckAttributeUdpDependencies update
                        // the snapshot inline, so by the time the next tick's
                        // enforcement runs, current == snapshot for those
                        // system writes - they bypass the lock. Reordering
                        // these two calls would route dependency-driven writes
                        // through the lock and revert them, breaking the
                        // user-only intent.
                        EnforceLockedAttributeUdps(attr, objectId, currentState.PhysicalName);

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

        /// <summary>
        /// One-shot baseline of every IsLocked=true Column UDP for the given
        /// attribute. Called from <see cref="SilentPopulateEntity"/> right after
        /// the attribute's <see cref="AttributeValidationSnapshot"/> is created,
        /// so the first follow-up tick has a non-empty "previous value" to
        /// compare against. Without this baseline, the very first time a user
        /// touches a locked UDP we would see (snapshot="" -> current="X") and
        /// classify it as an initial assignment, missing the intended block.
        ///
        /// Cost: O(locked UDP count) property reads per attribute baselined.
        /// Locked UDPs are a small subset of the definition table; a typical
        /// config has 0-5 of them.
        /// </summary>
        private void BaselineLockedAttributeUdps(dynamic attr, AttributeValidationSnapshot snapshot)
        {
            if (attr == null || snapshot == null) return;
            if (!UdpDefinitionService.Instance.IsLoaded) return;

            var lockedDefs = UdpDefinitionService.Instance.GetLockedByObjectType("Column");
            if (lockedDefs.Count == 0) return;

            foreach (var def in lockedDefs)
            {
                try
                {
                    string path = $"Attribute.Physical.{def.Name}";
                    string currentVal = attr.Properties(path)?.Value?.ToString() ?? "";
                    snapshot.UdpValues[def.Name] = currentVal;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"BaselineLockedAttributeUdps: '{def.Name}' read err: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Lock enforcement for IsLocked=true Column UDPs. Runs from
        /// <see cref="CheckEntityForChanges"/>, BEFORE
        /// <see cref="CheckAttributeUdpDependencies"/>: this ordering matters
        /// because the dependency cascade can write into a locked UDP as a
        /// system side-effect, and the same-tick re-read inside
        /// CheckAttributeUdpDependencies absorbs that write into the snapshot
        /// silently. Running enforcement first means we only ever see the
        /// user-initiated diff here; system writes flow past the lock check
        /// untouched. This mirrors the user-only-intent contract already
        /// documented for the (now dead) TableTypeMonitor path.
        ///
        /// Semantic per UDP:
        ///   snapshot empty + current empty   -> nothing to do
        ///   snapshot empty + current non-empty -> "initial set", accept and
        ///                                        update snapshot
        ///   snapshot non-empty + current == snapshot -> no change
        ///   snapshot non-empty + current != snapshot -> revert via
        ///                                        WriteUdpValues + MessageBox,
        ///                                        snapshot stays at old value
        /// </summary>
        private void EnforceLockedAttributeUdps(dynamic attr, string objectId, string columnName)
        {
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;
            if (!UdpDefinitionService.Instance.IsLoaded) return;

            var lockedDefs = UdpDefinitionService.Instance.GetLockedByObjectType("Column");
            if (lockedDefs.Count == 0) return;

            if (!_attributeSnapshots.TryGetValue(objectId, out var snapshot)) return;

            foreach (var def in lockedDefs)
            {
                string currentVal;
                try
                {
                    string path = $"Attribute.Physical.{def.Name}";
                    currentVal = attr.Properties(path)?.Value?.ToString() ?? "";
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"EnforceLockedAttributeUdps: '{def.Name}' read err: {ex.Message}");
                    continue;
                }

                string prevVal = "";
                snapshot.UdpValues.TryGetValue(def.Name, out prevVal);
                prevVal = prevVal ?? "";

                if (currentVal == prevVal) continue;

                if (string.IsNullOrEmpty(prevVal))
                {
                    // Initial assignment from glossary/wizard/manual seed - accept.
                    snapshot.UdpValues[def.Name] = currentVal;
                    Log($"Locked Column UDP '{def.Name}' initial set on '{columnName}': '' -> '{currentVal}' (accepted)");
                    continue;
                }

                // User edit on a non-empty locked UDP - revert.
                try
                {
                    _udpRuntimeService.WriteUdpValues(
                        attr,
                        new Dictionary<string, string> { [def.Name] = prevVal },
                        "Column");
                    Log($"Locked Column UDP '{def.Name}' on '{columnName}' reverted '{currentVal}' -> '{prevVal}'");

                    Forms.LockedUdpDialog.Show(def.Name, currentVal, prevVal);
                }
                catch (Exception ex)
                {
                    Log($"Locked Column UDP revert failed for '{def.Name}' on '{columnName}': {ex.Message}");
                }
                // Snapshot stays at prevVal so the next tick sees the
                // re-applied revert value and treats it as "no change".
            }
        }

        /// <summary>
        /// Common helper for the locked-column enforcement family
        /// (2026-05-25). Sequence: <c>BeginInvoke -> set guard ->
        /// ShowDialog (sync, modal) -> on OK run <paramref name="applyAction"/>
        /// -> clear guard</c>. The user explicitly asked for this order
        /// ("Değişikliği uygulamadan önce popup'ı göstersin, sonra
        /// uygulasın"); previously we applied SCAPI writes immediately
        /// and only logged / surfaced the dialog afterward, which gave a
        /// 'something changed silently' feel. Now the dialog appears
        /// FIRST and the SCAPI write (revert / restore) lands only after
        /// the user acknowledges.
        ///
        /// <paramref name="dedupeKey"/> protects against duplicate
        /// dialogs when multiple detection paths race for the same
        /// edit; pass null to disable de-dup.
        /// </summary>
        private void EnqueueLockedColumnDialogAndApply(
            string columnName,
            string entityName,
            Forms.LockedColumnAction action,
            string detail,
            string dedupeKey,
            Action applyAction)
        {
            if (!string.IsNullOrEmpty(dedupeKey))
            {
                lock (_pendingLockedDialogKeys)
                {
                    if (!_pendingLockedDialogKeys.Add(dedupeKey))
                    {
                        Log($"Locked column dialog suppressed (duplicate): {dedupeKey}");
                        return;
                    }
                }
            }

            void Run()
            {
                _lockedDialogShowing = true;
                try
                {
                    Forms.LockedColumnDialog.Show(columnName, entityName, action, detail);
                    if (applyAction != null)
                    {
                        try { applyAction(); }
                        catch (Exception applyEx)
                        {
                            Log($"Locked column apply error for '{entityName}.{columnName}' ({action}): {applyEx.Message}");
                        }
                    }
                }
                finally
                {
                    _lockedDialogShowing = false;
                    if (!string.IsNullOrEmpty(dedupeKey))
                    {
                        lock (_pendingLockedDialogKeys) { _pendingLockedDialogKeys.Remove(dedupeKey); }
                    }
                }
            }

            Form host = null;
            try
            {
                if (Application.OpenForms.Count > 0)
                    host = Application.OpenForms[0];
            }
            catch { /* fall through to inline */ }

            if (host != null && host.IsHandleCreated && !host.IsDisposed)
            {
                try
                {
                    host.BeginInvoke(new Action(Run));
                    return;
                }
                catch (Exception ex)
                {
                    Log($"EnqueueLockedColumnDialogAndApply BeginInvoke fallback: {ex.Message}");
                }
            }
            Run();
        }

        /// <summary>
        /// Phase 3 of locked predefined-column enforcement (2026-05-24):
        /// rename revert. Called from <see cref="ProcessAttributeChanges"/>
        /// when the user renamed a column. If the PREVIOUS column name
        /// matches an applicable locked predefined-column rule, we
        /// defer the dialog and the actual SCAPI revert via
        /// <see cref="EnqueueLockedColumnDialogAndApply"/> so the user
        /// sees the popup BEFORE the name flips back. Returns true when
        /// we intercepted (caller should stop processing this attribute
        /// change for THIS tick).
        /// </summary>
        private bool EnforceLockedColumnRename(dynamic attr, AttributeValidationSnapshot previousState, AttributeValidationSnapshot currentState)
        {
            try
            {
                if (!PredefinedColumnService.Instance.IsLoaded) return false;
                if (string.IsNullOrEmpty(previousState?.PhysicalName)) return false;

                dynamic entity = ResolveEntityByName(previousState.TableName);
                var rule = PredefinedColumnService.Instance.FindApplicableLockedRule(entity, previousState.PhysicalName);
                if (rule == null) return false;

                Log($"Locked predefined-column rename intercepted: '{previousState.TableName}.{previousState.PhysicalName}' -> '{currentState.PhysicalName}' (locked rule#{rule.Id}, deferring dialog+revert)");

                // Mutate the snapshots NOW (before the dialog defers) so the
                // heartbeat's fingerprint diff sees the snapshot already
                // tracking the rule-authored name. Without this the next
                // tick (before the deferred revert lands) would see
                // snapshot=COL1 vs live=COL1_NEW and re-fire ProcessAttribute-
                // Changes -> EnforceLockedColumnRename -> queue another
                // dialog. We rely on the dedupe key on the deferred call
                // for safety, but bringing snapshots forward avoids the
                // extra work entirely.
                currentState.PhysicalName = previousState.PhysicalName;
                string objId = previousState.ObjectId;
                if (!string.IsNullOrEmpty(objId) && _attributeSnapshots.TryGetValue(objId, out var snap))
                {
                    snap.PhysicalName = previousState.PhysicalName;
                }

                // Capture the local fields needed inside the deferred apply.
                string attemptedName = currentState.PhysicalName ?? string.Empty;
                string keptName = previousState.PhysicalName ?? string.Empty;
                string tableName = previousState.TableName ?? string.Empty;
                int ruleId = rule.Id;
                dynamic capturedAttr = attr;
                string detail = $"Attempted rename: \"{attemptedName}\"\nRestored name: \"{keptName}\"";
                string dedupe = $"rename|{objId}|{keptName}";

                EnqueueLockedColumnDialogAndApply(
                    keptName,
                    tableName,
                    Forms.LockedColumnAction.Rename,
                    detail,
                    dedupe,
                    () =>
                    {
                        int transId = _session.BeginNamedTransaction("RevertLockedColumnRename");
                        try
                        {
                            capturedAttr.Properties("Physical_Name").Value = keptName;
                            _session.CommitTransaction(transId);
                            Log($"Locked rename revert applied: rule#{ruleId} '{attemptedName}' -> '{keptName}' on '{tableName}'");
                        }
                        catch (Exception ex)
                        {
                            try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"RevertLockedColumnRename rollback error: {rbEx.Message}"); }
                            Log($"Locked rename revert FAILED for '{keptName}' on '{tableName}': {ex.Message}");
                        }
                    });
                return true;
            }
            catch (Exception ex)
            {
                Log($"EnforceLockedColumnRename error on '{previousState?.TableName}.{previousState?.PhysicalName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Phase 4 of locked predefined-column enforcement (2026-05-24,
        /// extended 2026-05-25). Thin wrapper kept for backward source-
        /// compat with the <see cref="ProcessAttributeChanges"/> call
        /// site; delegates to the shared helper that ALSO powers the
        /// editor-close-edge scan (<see cref="ScanForLockedColumnPropertyDrift"/>).
        /// Returns true iff drift was detected (caller short-circuits).
        /// </summary>
        private bool EnforceLockedColumnPropertyChange(dynamic attr, AttributeValidationSnapshot previousState, AttributeValidationSnapshot currentState)
        {
            string objId = currentState?.ObjectId ?? string.Empty;
            string columnName = currentState?.PhysicalName ?? string.Empty;
            string tableName = currentState?.TableName ?? string.Empty;
            return CheckAndEnqueueLockedPropertyDrift(attr, null, columnName, tableName, objId, currentState);
        }

        /// <summary>
        /// Shared locked-column property drift detector and enqueuer
        /// (2026-05-25). Reads every locked-rule-relevant property
        /// (Physical_Data_Type, Null_Option_Type, Physical_Default_Value,
        /// PK membership) on the given attribute, diffs against the
        /// applicable locked rule, and if anything drifted defers a
        /// dialog + revert through <see cref="EnqueueLockedColumnDialogAndApply"/>.
        /// Returns true when drift was found (and a deferred apply was
        /// enqueued). <paramref name="resolvedEntity"/> may be passed by
        /// callers that already have a live entity reference to avoid a
        /// second Collect walk; pass null and we resolve by table name.
        /// </summary>
        private bool CheckAndEnqueueLockedPropertyDrift(dynamic attr, dynamic resolvedEntity, string columnName, string tableName, string objId, AttributeValidationSnapshot currentStateOpt)
        {
            try
            {
                if (!PredefinedColumnService.Instance.IsLoaded) return false;
                if (string.IsNullOrEmpty(columnName)) return false;

                dynamic entity = resolvedEntity ?? ResolveEntityByName(tableName);
                var rule = PredefinedColumnService.Instance.FindApplicableLockedRule(entity, columnName);
                if (rule == null) return false;

                // Drifted built-in properties. We read live SCAPI values so
                // the diff is accurate regardless of snapshot freshness.
                var driftedProps = new List<(string label, string accessor, string ruleValue, string liveValue)>();

                // Physical_Data_Type
                string liveDataType = "";
                try { liveDataType = attr.Properties("Physical_Data_Type").Value?.ToString() ?? ""; }
                catch { /* leave empty */ }
                string ruleDataType = rule.DataType ?? "";
                if (!string.IsNullOrEmpty(ruleDataType)
                    && !string.Equals(liveDataType, ruleDataType, StringComparison.Ordinal))
                {
                    driftedProps.Add(("Datatype", "Physical_Data_Type", ruleDataType, liveDataType));
                }

                // Nullability detection (2026-05-25). erwin r10.10 exposes
                // TWO related accessors:
                //   * Null_Option_Type - integer enum (0/1, semantic varies)
                //   * Null_Option      - string ("NULL" / "NOT NULL", clear semantic)
                // We compare on the string form which is unambiguous, and
                // fall back to the integer form only when the string read
                // fails. The integer write (Null_Option_Type = 0 or 1) in
                // ApplyPredefinedColumnsToEntity still works because erwin
                // accepts BOTH accessor names; we just don't trust the
                // integer interpretation any more for diff purposes.
                string liveNullStr = "";
                try { liveNullStr = (attr.Properties("Null_Option").Value?.ToString() ?? "").Trim().ToUpperInvariant(); }
                catch (Exception nullStrEx)
                {
                    Log($"CheckAndEnqueueLockedPropertyDrift: Null_Option (string) read err on '{tableName}.{columnName}': {nullStrEx.Message}");
                }
                string liveNullInt = "";
                try { liveNullInt = attr.Properties("Null_Option_Type").Value?.ToString() ?? ""; }
                catch (Exception nullIntEx)
                {
                    Log($"CheckAndEnqueueLockedPropertyDrift: Null_Option_Type (int) read err on '{tableName}.{columnName}': {nullIntEx.Message}");
                }
                // Canonicalise live to "NULL" or "NOT NULL" preferring the
                // string accessor; if that came back empty we infer from
                // the integer using the most-common erwin mapping (0=NULL,
                // 1=NOT NULL) and label uncertainty in the log.
                string liveNullCanon;
                if (liveNullStr == "NULL" || liveNullStr == "NOT NULL")
                    liveNullCanon = liveNullStr;
                else if (liveNullInt == "1")
                    liveNullCanon = "NOT NULL";
                else if (liveNullInt == "0")
                    liveNullCanon = "NULL";
                else
                    liveNullCanon = ""; // unknown
                string ruleNullCanon = rule.Nullable ? "NULL" : "NOT NULL";
                Log($"CheckAndEnqueueLockedPropertyDrift: '{tableName}.{columnName}' Nullability live(str='{liveNullStr}', int='{liveNullInt}') canon='{liveNullCanon}' rule.Nullable={rule.Nullable} expected='{ruleNullCanon}'");
                if (!string.IsNullOrEmpty(liveNullCanon)
                    && !string.Equals(liveNullCanon, ruleNullCanon, StringComparison.Ordinal))
                {
                    // We write through Null_Option_Type (integer) because
                    // that is the accessor the existing ApplyPredefined
                    // add-path uses and we know it sticks. Value: 0 for
                    // nullable, 1 for not-null - matches the add-path
                    // convention.
                    string writeValue = rule.Nullable ? "0" : "1";
                    driftedProps.Add(("Nullability", "Null_Option_Type", writeValue, liveNullCanon));
                }

                // Default value SCAPI accessor varies by erwin build
                // ("Physical_Default_Value" is invalid on r10.10). Probe
                // and cache via ErwinUtilities. When no accessor is
                // available the comparison is skipped entirely (we
                // cannot read OR write, so flagging drift is pointless).
                string defAccessor = ErwinUtilities.ResolveAttributeDefaultAccessor(attr);
                if (!string.IsNullOrEmpty(defAccessor))
                {
                    string liveDefault = "";
                    try { liveDefault = attr.Properties(defAccessor).Value?.ToString() ?? ""; }
                    catch { /* leave empty */ }
                    string ruleDefault = rule.DefaultValue ?? "";
                    if (!string.Equals(liveDefault, ruleDefault, StringComparison.Ordinal))
                    {
                        driftedProps.Add(("Default value", defAccessor, ruleDefault, liveDefault));
                    }
                }

                // PK membership drift. Tracked separately because it is
                // not a simple property write - apply phase goes through
                // Key_Group / Key_Group_Member SCAPI. Live = is column
                // currently a PK member? Rule = should it be?
                bool livePk = false;
                if (!string.IsNullOrEmpty(objId) && entity != null && _tableTypeMonitor != null)
                {
                    try { livePk = _tableTypeMonitor.IsAttributeInPrimaryKey(entity, objId); }
                    catch (Exception pkEx) { Log($"CheckAndEnqueueLockedPropertyDrift PK read err for '{tableName}.{columnName}': {pkEx.Message}"); }
                }
                bool pkDrift = livePk != rule.IsPrimaryKey;
                if (pkDrift)
                {
                    driftedProps.Add((
                        "Primary Key",
                        "PK", // accessor sentinel - apply phase uses it as a marker, not a SCAPI property name
                        rule.IsPrimaryKey ? "yes" : "no",
                        livePk ? "yes" : "no"));
                }

                if (driftedProps.Count == 0) return false;

                Log($"Locked predefined-column property drift on '{tableName}.{columnName}': {driftedProps.Count} prop(s) - deferring dialog+revert");

                // Patch snapshot now so the heartbeat does not re-fire on
                // the same drift before the apply lands.
                if (currentStateOpt != null && !string.IsNullOrEmpty(ruleDataType))
                    currentStateOpt.PhysicalDataType = ruleDataType;
                if (!string.IsNullOrEmpty(objId) && _attributeSnapshots.TryGetValue(objId, out var snap))
                {
                    if (!string.IsNullOrEmpty(ruleDataType)) snap.PhysicalDataType = ruleDataType;
                }

                string detail = string.Join("\n", driftedProps.Select(d =>
                    $"{d.label}: \"{d.liveValue}\" -> \"{d.ruleValue}\""));
                int ruleIdLocal = rule.Id;
                dynamic capturedAttr = attr;
                bool capturedRulePk = rule.IsPrimaryKey;
                string capturedColumnName = columnName;
                string capturedTableName = tableName;
                string capturedObjId = objId;
                var capturedProps = driftedProps.ToList();
                string dedupe = $"prop|{objId}|{columnName}|{string.Join(",", capturedProps.Select(p => p.accessor))}";

                EnqueueLockedColumnDialogAndApply(
                    capturedColumnName,
                    capturedTableName,
                    Forms.LockedColumnAction.PropertyChange,
                    detail,
                    dedupe,
                    () =>
                    {
                        // Built-in property reverts in one transaction.
                        var simpleReverts = capturedProps.Where(p => !string.Equals(p.accessor, "PK", StringComparison.Ordinal)).ToList();
                        if (simpleReverts.Count > 0)
                        {
                            int transId = _session.BeginNamedTransaction("RevertLockedColumnProperty");
                            try
                            {
                                foreach (var (label, accessor, ruleValue, liveValue) in simpleReverts)
                                {
                                    try
                                    {
                                        capturedAttr.Properties(accessor).Value = ruleValue;
                                        Log($"Locked property revert applied: rule#{ruleIdLocal} {label} ({accessor}) '{liveValue}' -> '{ruleValue}' on '{capturedTableName}.{capturedColumnName}'");
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"  {label} ({accessor}): revert FAILED - {ex.Message}");
                                    }
                                }
                                _session.CommitTransaction(transId);
                            }
                            catch (Exception ex)
                            {
                                try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"RevertLockedColumnProperty rollback error: {rbEx.Message}"); }
                                Log($"Locked property revert FAILED: {ex.Message}");
                            }
                        }

                        // PK revert via Key_Group plumbing (separate
                        // transaction boundaries inside the helpers).
                        bool pkRevertNeeded = capturedProps.Any(p => string.Equals(p.accessor, "PK", StringComparison.Ordinal));
                        if (pkRevertNeeded && _tableTypeMonitor != null)
                        {
                            try
                            {
                                dynamic applyEntity = ResolveEntityByName(capturedTableName);
                                if (applyEntity == null)
                                {
                                    Log($"Locked PK revert: entity '{capturedTableName}' not found at apply time");
                                }
                                else if (capturedRulePk)
                                {
                                    _tableTypeMonitor.EnsureAttributeInPrimaryKey(applyEntity, capturedAttr, capturedColumnName);
                                    Log($"Locked PK revert applied: rule#{ruleIdLocal} '{capturedColumnName}' restored to PK on '{capturedTableName}'");
                                }
                                else
                                {
                                    _tableTypeMonitor.RemoveAttributeFromPrimaryKey(applyEntity, capturedObjId, capturedColumnName);
                                    Log($"Locked PK revert applied: rule#{ruleIdLocal} '{capturedColumnName}' removed from PK on '{capturedTableName}'");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Locked PK revert FAILED for '{capturedTableName}.{capturedColumnName}': {ex.Message}");
                            }
                        }
                    });
                return true;
            }
            catch (Exception ex)
            {
                Log($"CheckAndEnqueueLockedPropertyDrift error on '{tableName}.{columnName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Editor-close-edge scan for locked-column property drift
        /// (2026-05-25). The heartbeat's Phase-2A fingerprint short-
        /// circuit checks only Physical_Name + Physical_Data_Type, so a
        /// user editing ONLY Nullable, Default, or PK never reaches
        /// <see cref="ProcessAttributeChanges"/>. This scan fires once
        /// per Column / Entity Editor close edge to catch those changes.
        ///
        /// Pre-filter compliance (per [[no-full-walks-in-change-detection]]):
        /// returns immediately when no locked rules are loaded; walks
        /// ONLY attributes whose <see cref="_attributeSnapshots"/> entry
        /// already carries a locked-rule name; does NOT enumerate all
        /// entities or all attributes.
        /// </summary>
        private void ScanForLockedColumnPropertyDrift(string trigger, string scopedEntityName)
        {
            if (_sessionLost || _disposed || _validationSuspended) return;
            if (!PredefinedColumnService.Instance.IsLoaded) return;

            // 1. Locked rule name set. Empty -> no work.
            var lockedNames = new HashSet<string>(
                PredefinedColumnService.Instance.GetLocked().Select(r => r.ColumnName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            lockedNames.Remove(string.Empty);
            if (lockedNames.Count == 0) return;

            // 2. Snapshot pre-filter to candidate attribute ObjectIds.
            //    If scopedEntityName is provided (we know which table the
            //    user just closed), further restrict to that table.
            var candidateAttrs = new Dictionary<string, AttributeValidationSnapshot>(StringComparer.Ordinal);
            var candidateTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _attributeSnapshots)
            {
                var snap = kv.Value;
                if (snap == null) continue;
                string snapName = snap.PhysicalName ?? string.Empty;
                if (snapName.Length == 0) continue;
                if (!lockedNames.Contains(snapName)) continue;
                if (!string.IsNullOrEmpty(scopedEntityName)
                    && !string.Equals(snap.TableName, scopedEntityName, StringComparison.Ordinal)
                    && !EntityNameMatchesTitle(snap.TableName ?? "", scopedEntityName)
                    && !EntityNameMatchesTitle(scopedEntityName, snap.TableName ?? "")) continue;
                candidateAttrs[kv.Key] = snap;
                if (!string.IsNullOrEmpty(snap.TableName))
                    candidateTables.Add(snap.TableName);
            }
            if (candidateAttrs.Count == 0) return;

            dynamic modelObjects = null;
            dynamic root = null;
            dynamic allEntities = null;
            int driftsQueued = 0;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelObjects?.Root;
                if (root == null) return;
                allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                var remainingTables = new HashSet<string>(candidateTables, StringComparer.OrdinalIgnoreCase);
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;
                    if (remainingTables.Count == 0) break;

                    string entityName;
                    try { entityName = GetTableName(entity); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(entityName)) continue;

                    string matchedCandidate = null;
                    if (remainingTables.Contains(entityName))
                        matchedCandidate = entityName;
                    else
                    {
                        foreach (var ct in remainingTables)
                        {
                            if (EntityNameMatchesTitle(ct, entityName) || EntityNameMatchesTitle(entityName, ct))
                            {
                                matchedCandidate = ct;
                                break;
                            }
                        }
                    }
                    if (matchedCandidate == null) continue;
                    remainingTables.Remove(matchedCandidate);

                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                    catch { continue; }
                    if (entityAttrs == null) continue;

                    try
                    {
                        foreach (dynamic attr in entityAttrs)
                        {
                            if (attr == null) continue;
                            string aId;
                            try { aId = attr.ObjectId?.ToString() ?? ""; }
                            catch { continue; }
                            if (string.IsNullOrEmpty(aId)) continue;
                            if (!candidateAttrs.TryGetValue(aId, out var attrSnap)) continue;

                            if (CheckAndEnqueueLockedPropertyDrift(attr, entity, attrSnap.PhysicalName, entityName, aId, attrSnap))
                                driftsQueued++;
                        }
                    }
                    finally { ReleaseCom(entityAttrs); }
                }

                if (driftsQueued > 0)
                    Log($"ScanForLockedColumnPropertyDrift [{trigger}]: queued {driftsQueued} property revert(s)");
            }
            catch (Exception ex)
            {
                Log($"ScanForLockedColumnPropertyDrift [{trigger}] err: {ex.Message}");
            }
            finally
            {
                ReleaseCom(allEntities);
                ReleaseCom(root);
                ReleaseCom(modelObjects);
            }
        }

        /// <summary>
        /// Resolve a live SCAPI Entity by physical name. Best-effort:
        /// returns null on miss or error so callers that need the entity
        /// ONLY for conditional-rule UDP reads degrade to "unconditional-
        /// only" matching rather than crashing.
        ///
        /// Checks <see cref="_scanContextEntity"/> first - if a caller
        /// up the stack already resolved the entity for THIS exact
        /// table name we skip the full Collect walk entirely. This is
        /// the hot path on locked-column rename: ScanForLockedColumnRenames
        /// already iterated entities and bound the matching one, so the
        /// downstream EnforceLockedColumnRename should not pay the walk
        /// cost a second time.
        /// </summary>
        private dynamic ResolveEntityByName(string tableName)
        {
            if (string.IsNullOrEmpty(tableName) || _session == null) return null;

            // Fast path: scoped cache hit. Compare with EntityNameMatchesTitle
            // semantics so generated names ("E/33" vs "E_33") match.
            if (_scanContextEntity != null && !string.IsNullOrEmpty(_scanContextTableName)
                && (string.Equals(_scanContextTableName, tableName, StringComparison.OrdinalIgnoreCase)
                    || EntityNameMatchesTitle(_scanContextTableName, tableName)
                    || EntityNameMatchesTitle(tableName, _scanContextTableName)))
            {
                return _scanContextEntity;
            }

            dynamic modelObjects = null;
            dynamic root = null;
            dynamic allEntities = null;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelObjects?.Root;
                if (root == null) return null;
                allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return null;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;
                    string physName;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        physName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                    }
                    catch { try { physName = entity.Name ?? ""; } catch { continue; } }

                    if (EntityNameMatchesTitle(physName, tableName)) return entity;
                }
            }
            catch (Exception ex)
            {
                Log($"ResolveEntityByName err for '{tableName}': {ex.Message}");
            }
            // NOTE: cannot Release here - caller may still need the entity.
            // Caller (locked-column check) is short-lived and entity goes
            // out of scope at end of method, .NET COM RCW cleanup handles it.
            return null;
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

                // Validate Column naming standards. isNew=true so the
                // Required-popup Cancel branch deletes the new column
                // (no pre-edit value to revert to).
                ValidateColumnNamingStandard(attr, currentState, isNew: true);
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

            // Locked predefined-column enforcement (2026-05-24).
            //
            // Two intercepts, both gated by name-match against the locked
            // rule set so non-locked columns pay zero cost:
            //   * Rename: previousState.PhysicalName matches a locked
            //     rule -> revert Physical_Name back to it.
            //   * Property change (datatype/nullable/default): the column
            //     name (current = previous when no rename) matches a
            //     locked rule -> revert any drifted property to the
            //     rule's authored value.
            // Either intercept returning true short-circuits the rest of
            // ProcessAttributeChanges - the locked column's downstream
            // state is now authored, no glossary / naming / UDP cascade
            // is needed against the typed-but-rejected value.
            if (physicalNameChanged && EnforceLockedColumnRename(attr, previousState, currentState))
            {
                return;
            }
            if (!physicalNameChanged && EnforceLockedColumnPropertyChange(attr, previousState, currentState))
            {
                return;
            }

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

                    // C3 polymorphic-condition replay (2026-05-17): a naming rule can
                    // condition on the live Physical_Data_Type value (e.g. "DateTime
                    // columns must end with _DATE"). When the user only changes the
                    // type - without renaming - the rule's source value just flipped,
                    // so we re-run the column naming validator against the FINAL
                    // post-policy type. ReadBuiltinPropertyValue reads SCAPI directly,
                    // so even if EnforceTermTypePolicy reverted the type, the engine
                    // sees the truth and gates correctly. No-op when no condition-
                    // bearing rule applies, so cheap to call unconditionally.
                    ValidateColumnNamingStandard(attr, currentState);

                    // ValidateColumnNamingStandard parks Req=false violations in
                    // _pendingResults. On rename-driven paths the consolidated
                    // popup gets flushed by ValidateCommittedPendingAttrs when
                    // the inline-edit closes, but a pure type-change has no
                    // such edge event - the user changed a combo and Tab'd
                    // away. Flush here so the warning surfaces immediately on
                    // the same gesture that caused it. (Required violations
                    // already drained themselves via the inline input dialog.)
                    if (_pendingResults.Count > 0)
                    {
                        ShowConsolidatedPopup();
                    }
                }

                // Watched-property drift detection (2026-05-17): mirror of
                // the Table path in TableTypeMonitorService. Any naming-rule
                // target on Column other than Physical_Name / Physical_Data_Type
                // (those already have first-class diff branches above) is
                // diffed here. Single change is enough to fire the validator;
                // it re-snapshots downstream if it writes anything back.
                if (!physicalNameChanged && !dataTypeChanged)
                {
                    bool drift = DetectWatchedColumnPropertyChange(attr, previousState, currentState,
                        out string changedCode, out string oldVal, out string newVal);
                    if (drift)
                    {
                        Log($"Watched column property changed on {currentState.TableName}.{currentState.PhysicalName}: {changedCode} '{oldVal}' -> '{newVal}' - re-running naming check");
                        ValidateColumnNamingStandard(attr, currentState);
                        if (_pendingResults.Count > 0)
                        {
                            ShowConsolidatedPopup();
                        }
                    }
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
                    AddinMessageDialog.Show(
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

        private void ValidateGlossary(dynamic attr, AttributeValidationSnapshot state, HashSet<string> predefinedColumnNames, bool bypassPlaceholderSkip = false)
        {
            if (_validationSuspended) { Log($"ValidateGlossary skipped (suspended) on {state?.TableName}.{state?.PhysicalName}"); return; }
            // Skip special names UNLESS bypassPlaceholderSkip is set. The
            // bypass path is used by the inline-edit close detection in
            // WindowMonitorTimer_Tick: when the user commits a column whose
            // name is still a placeholder (Enter without typing), the
            // committed-state validation must surface the "not in glossary"
            // popup with the placeholder text instead of silently skipping.
            if (!bypassPlaceholderSkip)
            {
                if (string.IsNullOrEmpty(state.PhysicalName) ||
                    state.PhysicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                    state.PhysicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase) ||
                    IsPleaseChangeItPlaceholder(state.PhysicalName))
                {
                    Log($"ValidateGlossary skipped (placeholder/empty name='{state.PhysicalName ?? "<null>"}') on {state?.TableName}");
                    return;
                }
            }
            else if (string.IsNullOrEmpty(state.PhysicalName))
            {
                // Even in bypass mode we cannot validate a truly empty name;
                // there is no string to look up. Log so the caller can see
                // that the bypass attempt was a no-op and skip cleanly.
                Log($"ValidateGlossary skipped (bypass: name still null/empty) on {state?.TableName}");
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
        /// <para>
        /// <paramref name="isNew"/> controls the Required-popup Cancel branch
        /// (added 2026-05-20): true means "this attribute was just created
        /// in the active edit session" - Cancel discards the new column.
        /// False means the user is editing an existing column - Cancel
        /// reverts the changed property to its pre-edit value captured
        /// from <c>_attributeSnapshots</c> at method entry.
        /// </para>
        /// </summary>
        private void ValidateColumnNamingStandard(dynamic attr, AttributeValidationSnapshot state, bool isNew = false)
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

            // Capture baseline state at method entry for the Required-popup
            // Cancel-Revert path. For !isNew callers, _attributeSnapshots
            // still holds the pre-edit snapshot (it's updated AFTER this
            // method returns by ProcessAttributeChanges). For isNew callers
            // baselines are irrelevant - Cancel deletes the attribute.
            string baselinePhysicalName = state.PhysicalName ?? "";
            Dictionary<string, string> baselineWatched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string snapshotId = "";
            if (!isNew && attr != null)
            {
                try
                {
                    snapshotId = attr.ObjectId?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(snapshotId) && _attributeSnapshots.TryGetValue(snapshotId, out var baselineSnap))
                    {
                        baselinePhysicalName = baselineSnap.PhysicalName ?? state.PhysicalName ?? "";
                        if (baselineSnap.WatchedProperties != null)
                        {
                            foreach (var kvp in baselineSnap.WatchedProperties)
                                baselineWatched[kvp.Key] = kvp.Value ?? "";
                        }
                    }
                }
                catch (Exception ex) { Log($"ValidateColumnNamingStandard: baseline capture failed for '{state.TableName}.{state.PhysicalName}': {ex.Message}"); }
            }

            // Step 1: silently apply AUTO_APPLY=true rules
            if (attrBoxed != null)
            {
                string afterAuto = NamingValidationEngine.ApplyNamingStandards("Column", state.PhysicalName, attrBoxed, autoOnly: true, isNew: isNew);
                if (!string.Equals(afterAuto, state.PhysicalName, StringComparison.Ordinal))
                {
                    int transId = _session.BeginNamedTransaction("ApplyAutoColumnNaming");
                    try
                    {
                        attr.Properties("Physical_Name").Value = afterAuto;
                        _session.CommitTransaction(transId);
                        Log($"Column naming auto-applied (silent): '{state.TableName}.{state.PhysicalName}' -> '{afterAuto}'");
                        // Modal popup (was a transient ToastNotification until
                        // 2026-05-27): user explicitly asked for an OK-to-
                        // dismiss confirmation so silent rename auto-apply
                        // cannot be missed. Owner is null so the dialog
                        // anchors to ErwinAddIn.ActiveForm's screen per
                        // AddinMessageDialog's own multi-monitor logic.
                        AddinMessageDialog.Show(
                            $"Column '{state.TableName}.{state.PhysicalName}' -> '{afterAuto}'",
                            "Naming standard applied",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
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
                string afterAll = NamingValidationEngine.ApplyNamingStandards("Column", state.PhysicalName, attrBoxed, autoOnly: false, isNew: isNew);
                if (!string.Equals(afterAll, state.PhysicalName, StringComparison.Ordinal))
                {
                    var answer = AddinMessageDialog.Show(
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
            var results = NamingValidationEngine.ValidateObjectName("Column", state.PhysicalName, attrBoxed, isNew: isNew);
            var failures = results.Where(r => !r.IsValid).ToList();

            // Required-input pass (2026-05-17 C3 follow-up, updated 2026-05-20):
            // Req=true violations get an inline input dialog. Cancel routes
            // through the Create-delete / Update-revert contract; on Cancel
            // the remaining Required popups are suppressed and the
            // consolidated warning at the bottom is skipped (the user has
            // explicitly abandoned this column-level edit).
            bool requiredCancelHandled = false;
            if (attr != null && failures.Count > 0)
            {
                // Same Required-property-promotion rule as the Table path
                // (2026-05-24): any non-Required failure (Length / Regexp /
                // non-AutoApply Prefix-Suffix) on a column property that
                // also carries IS_REQUIRED=true goes through the modal
                // input popup with the re-prompt loop instead of the
                // consolidated warning.
                var requiredProps = NamingStandardService.Instance.GetRequiredPropertyCodes("Column");
                var requiredFailures = failures
                    .Where(f => f.Rule != null
                                && !string.IsNullOrEmpty(f.Rule.PropertyCode)
                                && (string.Equals(f.RuleName, "Required", StringComparison.Ordinal)
                                    || (requiredProps != null && requiredProps.Contains(f.Rule.PropertyCode))))
                    .GroupBy(f => f.Rule.PropertyCode, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.FirstOrDefault(x => string.Equals(x.RuleName, "Required", StringComparison.Ordinal)) ?? g.First())
                    .ToList();

                // Session-level dismissal pre-pass (added 2026-05-21):
                // suppress popups the user already cancelled in this session
                // for the same (attr, property) pair. Strips them from both
                // requiredFailures (no popup) and failures (no consolidated
                // warning entry). Empty snapshotId means we have no stable
                // key to filter by - fall through to the normal popup path.
                if (!string.IsNullOrEmpty(snapshotId))
                {
                    var dismissedNow = requiredFailures
                        .Where(rf => _dismissedRequiredColumnKeys.Contains($"{snapshotId}|{rf.Rule.PropertyCode}"))
                        .ToList();
                    foreach (var dismissed in dismissedNow)
                    {
                        Log($"Required field popup suppressed (session-dismissed): Column.{dismissed.Rule.PropertyCode} on '{state.TableName}.{state.PhysicalName}'");
                        requiredFailures.Remove(dismissed);
                        failures.Remove(dismissed);
                    }
                }

                foreach (var rf in requiredFailures)
                {
                    string fieldLabel = $"Column.{rf.Rule.PropertyCode}";
                    var cancelMode = isNew ? Forms.RequiredOperationMode.Create : Forms.RequiredOperationMode.Update;

                    // Pre-fill the dialog with the column's current value
                    // (same rationale as the Table path).
                    string seedValue = "";
                    try { seedValue = attr?.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                    catch { seedValue = ""; }

                    var rc = EliteSoft.Erwin.AddIn.Forms.RequiredFieldDialog.Show(
                        title: "Required field",
                        message: rf.ErrorMessage,
                        fieldLabel: fieldLabel,
                        out string typed,
                        owner: null,
                        initialValue: seedValue,
                        mode: cancelMode,
                        objectKind: "Column");

                    if (rc != DialogResult.OK || string.IsNullOrEmpty(typed))
                    {
                        Log($"Required field dialog cancelled: {state.TableName}.{state.PhysicalName} field={fieldLabel} (mode={cancelMode})");
                        if (isNew)
                        {
                            requiredCancelHandled = TryDeleteNewAttribute(attr, state.TableName, state.PhysicalName);
                        }
                        else
                        {
                            string baseline = string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase)
                                ? baselinePhysicalName
                                : (baselineWatched.TryGetValue(rf.Rule.PropertyCode, out var bv) ? bv : "");
                            requiredCancelHandled = TryRevertAttributeProperty(attr, snapshotId, state.TableName, state.PhysicalName, rf.Rule.PropertyCode, baseline);
                            // Keep the in-flight currentState aligned with the
                            // revert so any code that runs after this method
                            // (snapshot replace in ProcessAttributeChanges)
                            // does not capture the rolled-back invalid value.
                            if (requiredCancelHandled)
                            {
                                if (string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                                    state.PhysicalName = baseline ?? "";
                                state.WatchedProperties[rf.Rule.PropertyCode] = baseline ?? "";
                            }

                            // Record session dismissal even when the revert
                            // wrote nothing (baseline equal to current). The
                            // next tick's drift detector would otherwise
                            // re-fire the same popup against the still-empty
                            // value.
                            if (!string.IsNullOrEmpty(snapshotId))
                            {
                                _dismissedRequiredColumnKeys.Add($"{snapshotId}|{rf.Rule.PropertyCode}");
                                Log($"Required field dismissed for session: {snapshotId}|{rf.Rule.PropertyCode}");
                            }
                        }
                        if (requiredCancelHandled) break;
                        // Delete/revert failed - drop through so other Required
                        // failures still get a chance and the consolidated
                        // warning at the end surfaces whatever the user did
                        // not resolve.
                        continue;
                    }

                    int transId = _session.BeginNamedTransaction("RequiredColumnFieldFill");
                    string writeAccessor = NamingValidationEngine.WriteAccessorFor(rf.Rule.PropertyCode);
                    try
                    {
                        // Read-vs-write accessor split (Name_Qualifier -> Schema_Ref etc).
                        attr.Properties(writeAccessor).Value = typed;
                        _session.CommitTransaction(transId);
                        Log($"Required field filled by user: {state.TableName}.{state.PhysicalName} {fieldLabel} = '{typed}'"
                            + (writeAccessor != rf.Rule.PropertyCode ? $" (write accessor='{writeAccessor}')" : ""));
                        failures.Remove(rf);

                        // Clear any stale session dismissal - if the user
                        // later empties this property again the popup should
                        // legitimately fire instead of being silenced by an
                        // earlier Cancel record.
                        if (!string.IsNullOrEmpty(snapshotId))
                            _dismissedRequiredColumnKeys.Remove($"{snapshotId}|{rf.Rule.PropertyCode}");

                        if (string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                            state.PhysicalName = typed;

                        // Refresh the WatchedProperties snapshot so the
                        // next-tick column drift diff doesn't fire on the
                        // value we just wrote (mirrors the Table-path fix).
                        try
                        {
                            string readBack;
                            try { readBack = attr.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? typed; }
                            catch { readBack = typed; }
                            state.WatchedProperties[rf.Rule.PropertyCode] = readBack;
                        }
                        catch (Exception snapEx)
                        {
                            Log($"Required column watched-snapshot refresh failed: {snapEx.Message}");
                        }

                        // Pattern-rule re-prompt loop (2026-05-24): mirror
                        // of the Table-path fix. If the same column-level
                        // PropertyCode carries a non-Required rule (Length /
                        // Regexp), the value the user just provided may
                        // satisfy Required while still failing the pattern.
                        // Keep re-opening the input dialog with the next
                        // violation's message until the property clears all
                        // rules or the user cancels.
                        string currentTyped = typed;
                        while (true)
                        {
                            string liveValue;
                            try { liveValue = attr.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                            catch { liveValue = currentTyped; }

                            var freshResults = NamingValidationEngine.ValidateObjectName(
                                "Column", liveValue, attrBoxed, rf.Rule.PropertyCode, isNew: isNew);
                            var freshFailure = freshResults?.FirstOrDefault(r => !r.IsValid);
                            if (freshFailure == null)
                            {
                                failures.RemoveAll(f => f.Rule != null
                                    && string.Equals(f.Rule.PropertyCode, rf.Rule.PropertyCode, StringComparison.OrdinalIgnoreCase));
                                break;
                            }

                            Log($"Required field re-prompt for {fieldLabel}: '{liveValue}' still violates rule#{freshFailure.Rule?.Id} ({freshFailure.RuleName})");
                            var rc2 = EliteSoft.Erwin.AddIn.Forms.RequiredFieldDialog.Show(
                                title: "Required field",
                                message: freshFailure.ErrorMessage,
                                fieldLabel: fieldLabel,
                                out string typed2,
                                owner: null,
                                initialValue: liveValue,
                                mode: cancelMode,
                                objectKind: "Column");

                            if (rc2 != DialogResult.OK || string.IsNullOrEmpty(typed2))
                            {
                                Log($"Required field re-prompt cancelled for {fieldLabel} (mode={cancelMode})");
                                if (isNew)
                                {
                                    requiredCancelHandled = TryDeleteNewAttribute(attr, state.TableName, state.PhysicalName);
                                }
                                else
                                {
                                    string baseline = baselineWatched.TryGetValue(rf.Rule.PropertyCode, out var bv) ? bv : "";
                                    requiredCancelHandled = TryRevertAttributeProperty(attr, snapshotId, state.TableName, state.PhysicalName, rf.Rule.PropertyCode, baseline);
                                    if (!string.IsNullOrEmpty(snapshotId))
                                        _dismissedRequiredColumnKeys.Add($"{snapshotId}|{rf.Rule.PropertyCode}");
                                }
                                failures.RemoveAll(f => f.Rule != null
                                    && string.Equals(f.Rule.PropertyCode, rf.Rule.PropertyCode, StringComparison.OrdinalIgnoreCase));
                                break;
                            }

                            int loopTransId = _session.BeginNamedTransaction("RequiredColumnFieldFillRepeat");
                            try
                            {
                                attr.Properties(writeAccessor).Value = typed2;
                                _session.CommitTransaction(loopTransId);
                                Log($"Required field re-filled by user: {state.TableName}.{state.PhysicalName} {fieldLabel} = '{typed2}'");
                                currentTyped = typed2;
                                state.WatchedProperties[rf.Rule.PropertyCode] = typed2;
                            }
                            catch (Exception loopEx)
                            {
                                try { _session.RollbackTransaction(loopTransId); } catch { }
                                Log($"Required column re-write failed for {fieldLabel}: {loopEx.Message}");
                                EliteSoft.Erwin.AddIn.Forms.AddinMessageDialog.Show(
                                    $"Failed to write '{typed2}' to {fieldLabel}.\n\nSCAPI error:\n{loopEx.Message}",
                                    "Required field write failed",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Error);
                                break;
                            }
                        }

                        if (requiredCancelHandled) break;
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"RequiredColumnFieldFill rollback error: {rbEx.Message}"); }
                        Log($"Required column field write failed for {fieldLabel}: {ex.Message}");

                        bool isSchemaRef = string.Equals(writeAccessor, "Schema_Ref", StringComparison.OrdinalIgnoreCase);
                        string userMessage = isSchemaRef
                            ? $"Cannot set '{typed}' as Owner. erwin's Schema_Ref expects an existing Schema object, " +
                              $"so the value must match an Owner already defined in this model. " +
                              $"Open Database Object Properties to create the Schema first.\n\n" +
                              $"SCAPI error:\n{ex.Message}"
                            : $"Failed to write '{typed}' to {fieldLabel}.\n\nSCAPI error:\n{ex.Message}";
                        EliteSoft.Erwin.AddIn.Forms.AddinMessageDialog.Show(
                            userMessage,
                            "Required field write failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }

            // Same short-circuit as the Table path: the user explicitly
            // discarded/reverted this column, so the remaining failures
            // belong to an object that is gone or already rolled back.
            // Don't queue them into _pendingResults - the consolidated
            // popup would be confusing noise.
            if (requiredCancelHandled)
            {
                Log($"ValidateColumnNamingStandard: Cancel-handled, suppressing remaining warnings for '{state.TableName}.{state.PhysicalName}'");
                return;
            }

            foreach (var r in failures)
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
        /// Remove a newly-created attribute in response to the user
        /// discarding its Required popup (Create mode). Uses the same
        /// <c>modelObjects.Remove</c> primitive as
        /// <see cref="ColumnValidationService"/>'s PLEASE_CHANGE_IT
        /// cleanup, wrapped in a named transaction. Clears the snapshot
        /// so the next monitor tick does not see a phantom drift.
        /// </summary>
        private bool TryDeleteNewAttribute(dynamic attr, string tableName, string columnName)
        {
            if (attr == null) return false;
            string objectId = "";
            try { objectId = attr.ObjectId?.ToString() ?? ""; }
            catch (Exception ex) { Log($"TryDeleteNewAttribute: ObjectId read failed for '{tableName}.{columnName}': {ex.Message}"); }

            int transId = 0;
            bool transOpen = false;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                transId = _session.BeginNamedTransaction("DiscardNewAttribute");
                transOpen = true;

                modelObjects.Remove(attr);

                _session.CommitTransaction(transId);
                transOpen = false;

                if (!string.IsNullOrEmpty(objectId))
                    _attributeSnapshots.Remove(objectId);

                Log($"TryDeleteNewAttribute: removed '{tableName}.{columnName}' (objectId={objectId})");
                return true;
            }
            catch (Exception ex)
            {
                Log($"TryDeleteNewAttribute: remove failed for '{tableName}.{columnName}': {ex.Message}");
                if (transOpen)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"TryDeleteNewAttribute: rollback failed: {rbEx.Message}"); }
                }
                return false;
            }
        }

        /// <summary>
        /// Revert a property on an existing attribute to its pre-edit
        /// baseline value, in response to the user cancelling the Required
        /// popup (Update mode). Mirrors
        /// <c>TableTypeMonitorService.TryRevertEntityProperty</c>.
        /// </summary>
        private bool TryRevertAttributeProperty(dynamic attr, string objectId, string tableName, string columnName, string propertyCode, string oldValue)
        {
            if (attr == null || string.IsNullOrEmpty(propertyCode)) return false;

            string writeAccessor = NamingValidationEngine.WriteAccessorFor(propertyCode);
            string newValue = oldValue ?? "";
            int transId = 0;
            bool transOpen = false;
            try
            {
                transId = _session.BeginNamedTransaction("RevertRequiredColumnField");
                transOpen = true;

                attr.Properties(writeAccessor).Value = newValue;

                _session.CommitTransaction(transId);
                transOpen = false;

                if (!string.IsNullOrEmpty(objectId) && _attributeSnapshots.TryGetValue(objectId, out var snap))
                {
                    if (string.Equals(propertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                        snap.PhysicalName = newValue;
                    snap.WatchedProperties[propertyCode] = newValue;
                }

                Log($"TryRevertAttributeProperty: '{tableName}.{columnName}' {propertyCode} reverted to '{newValue}'"
                    + (writeAccessor != propertyCode ? $" (write accessor='{writeAccessor}')" : ""));
                return true;
            }
            catch (Exception ex)
            {
                if (transOpen)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"TryRevertAttributeProperty: rollback failed: {rbEx.Message}"); }
                    transOpen = false;
                }

                // SCVT_OBJID quirk: see TableTypeMonitorService.TryRevertEntityProperty
                // for the full diagnosis. Empty-baseline revert on a Schema_Ref-
                // style object reference is a conceptual no-op and erwin
                // physically rejects "" - treat as success so the caller
                // records the session dismissal cleanly.
                bool emptyBaseline = string.IsNullOrEmpty(newValue);
                bool looksLikeObjIdRejection = ex.Message != null
                    && (ex.Message.IndexOf("SCVT_OBJID", StringComparison.OrdinalIgnoreCase) >= 0
                        || ex.Message.IndexOf("from string ''", StringComparison.OrdinalIgnoreCase) >= 0);
                if (emptyBaseline && looksLikeObjIdRejection)
                {
                    Log($"TryRevertAttributeProperty: '{tableName}.{columnName}' {propertyCode} no-op revert (empty baseline on SCVT_OBJID property)");
                    return true;
                }

                Log($"TryRevertAttributeProperty: revert failed for '{tableName}.{columnName}'.{propertyCode}: {ex.Message}");
                return false;
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
                                var answer = AddinMessageDialog.Show(
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

            // Phase-2I: admin flag gate. When USE_EXTERNAL_DOMAIN is OFF the
            // service still reports IsLoaded=true (so callers don't keep
            // retrying the load) but holds zero entries and answers "skip".
            // IsValidDomain also short-circuits on this; the check here is a
            // belt-and-suspenders guard for direct callers.
            if (!DomainDefService.Instance.IsExternalEnabled)
            {
                Log($"ValidateDomain skipped (USE_EXTERNAL_DOMAIN=false) on {state?.TableName}.{state?.PhysicalName}");
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

                AddinMessageDialog.Show(
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
                // 2026-05-07: explicit timing markers around the post-OK work so the
                // user can see exactly how long delete / rename takes vs how long
                // the popup itself was on-screen.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool editorOpen = !string.IsNullOrEmpty(_activeColumnEditorTable);
                Log($"[POPUP] OK clicked - dispatching {(editorOpen ? "rename" : "delete")} for {glossaryResults.Count} column(s)");
                if (editorOpen)
                {
                    RenameInvalidGlossaryColumns(glossaryResults);
                }
                else
                {
                    DeleteInvalidGlossaryColumns(glossaryResults);
                }
                sw.Stop();
                Log($"[POPUP] post-OK action took {sw.ElapsedMilliseconds} ms");
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

            // Snapshot every property the active naming-rule set targets on
            // Column, so the next tick can detect a user clearing / editing
            // one of them without renaming the column. Physical_Name and
            // Physical_Data_Type are already first-class snapshot fields,
            // so skip them here to avoid double-tracking.
            ReadWatchedColumnProperties(attr, snapshot);

            // Column UDP values are read lazily in ProcessAttributeChanges only when needed
            // (not in every snapshot — too expensive for 100+ attributes per tick).
            // TermTypeCanonical is also intentionally NOT resolved here; it's set when a
            // glossary entry is applied to the column (ApplyGlossaryUdpValues path) and
            // refreshed when Physical_Name changes (column renamed to a different glossary term).

            return snapshot;
        }

        /// <summary>
        /// Diff watched-property values between two column snapshots. Falls
        /// back to re-reading the live SCAPI value when
        /// <paramref name="currentState"/> happens to not carry a snapshot
        /// for a code that <paramref name="previousState"/> does (e.g. a
        /// new rule was added at runtime between snapshots). Returns true
        /// on the first delta found, with the property code + values
        /// reported via out parameters for the diagnostic log line.
        /// </summary>
        private bool DetectWatchedColumnPropertyChange(dynamic attr,
            AttributeValidationSnapshot previousState, AttributeValidationSnapshot currentState,
            out string changedCode, out string oldValue, out string newValue)
        {
            changedCode = "";
            oldValue = "";
            newValue = "";
            if (previousState == null || currentState == null) return false;
            try
            {
                foreach (var code in NamingStandardService.Instance.GetPropertyCodes("Column"))
                {
                    if (string.IsNullOrEmpty(code)) continue;
                    if (string.Equals(code, "Physical_Name", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(code, "Physical_Data_Type", StringComparison.OrdinalIgnoreCase)) continue;

                    string previous = previousState.WatchedProperties.TryGetValue(code, out var pv) ? (pv ?? "") : "";
                    string current;
                    if (!currentState.WatchedProperties.TryGetValue(code, out var cv))
                    {
                        try { current = attr?.Properties(code)?.Value?.ToString() ?? ""; }
                        catch { current = ""; }
                    }
                    else
                    {
                        current = cv ?? "";
                    }

                    if (!string.Equals(previous, current, StringComparison.Ordinal))
                    {
                        changedCode = code;
                        oldValue = previous;
                        newValue = current;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DetectWatchedColumnPropertyChange error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Populate <see cref="AttributeValidationSnapshot.WatchedProperties"/>
        /// from the live SCAPI state of <paramref name="attr"/>. Mirrors
        /// <c>TableTypeMonitorService.RefreshWatchedProperties</c> for the
        /// Column path. Read failures are stored as empty strings so the
        /// next-tick diff treats them the same way Step 3b does in
        /// <c>ValidateColumnNamingStandard</c>.
        /// </summary>
        private void ReadWatchedColumnProperties(dynamic attr, AttributeValidationSnapshot snapshot)
        {
            if (attr == null || snapshot == null) return;
            try
            {
                snapshot.WatchedProperties.Clear();
                foreach (var code in NamingStandardService.Instance.GetPropertyCodes("Column"))
                {
                    if (string.IsNullOrEmpty(code)) continue;
                    if (string.Equals(code, "Physical_Name", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(code, "Physical_Data_Type", StringComparison.OrdinalIgnoreCase)) continue;

                    string value;
                    try { value = attr.Properties(code)?.Value?.ToString() ?? ""; }
                    catch { value = ""; }
                    snapshot.WatchedProperties[code] = value;
                }
            }
            catch (Exception ex)
            {
                Log($"ReadWatchedColumnProperties error: {ex.Message}");
            }
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
            // Phase-2I (2026-05-13): the USE_EXTERNAL_DOMAIN admin flag gates
            // domain validation as a whole. When it is OFF, we report every
            // domain as "not valid for validation purposes" so the caller
            // (ProcessNewAttribute / ProcessAttributeChanges) falls through to
            // the glossary branch. The column's actual erwin domain field is
            // untouched; we just refuse to act on it.
            if (!DomainDefService.Instance.IsExternalEnabled) return false;

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
            /// Live SCAPI value cache for every PROPERTY_CODE that has at
            /// least one active naming-standard rule on Column (other than
            /// Physical_Name / Physical_Data_Type, both already first-class
            /// snapshot fields). Diffed on each monitor tick - any change
            /// re-fires <see cref="ValidateColumnNamingStandard"/> so e.g.
            /// clearing a Required-flagged Comment / Definition / Schema_Ref
            /// on an existing column triggers the Required popup just like
            /// the new-attribute / rename paths.
            /// </summary>
            public Dictionary<string, string> WatchedProperties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
