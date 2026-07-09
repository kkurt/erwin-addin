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
        // Model Editor ("Model 'X' Editor") lifecycle. On the open->close edge we
        // run a READ-ONLY model-level naming validation (e.g. rule#1028
        // MODEL.Definition req=True -> "model description required") and warn.
        // Nothing validated model-level rules on close before, so an empty
        // required model description was never flagged (2026-06-05).
        private bool _modelEditorWasOpen;
        // Model name captured when the Model Editor dialog OPENED, used as the
        // "Revert Change" target if the user renames the model to a rule-violating
        // value inside the editor and reverts on close (2026-06-24).
        private string _modelEditorOpenName;
        // _entityEditorUdpSnapshot field removed 2026-05-22 along with the
        // Table-UDP delta enforcement that was its only consumer.
        // Reentrancy guard. MessageBox.Show pumps the message loop while
        // modal, which fires this same WindowMonitorTimer again. Without
        // this flag the second tick re-enters RunScopedTableNamingCheck
        // (snapshot still old) and stacks another popup, then a third,
        // ad infinitum. Verified 2026-05-07: 30+ nested popups in one
        // session of clicking the TABLE_TYPE combo to "LOG".
        private bool _scopedCheckInProgress;
        // Mirror of _scopedCheckInProgress for the COLUMN naming path
        // (ValidateColumnNamingStandard). Its Required-field popup is modal and
        // pumps the loop; without this guard a reentrant timer tick re-runs the
        // same column's rename/Definition validation and stacks popups ad
        // infinitum (2026-06-06 loop after the COLUMN.Definition Step-3b).
        private bool _columnNamingCheckInProgress;
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

        // True for the entire lifetime of ANY enforcement modal in the datatype/term-type
        // pipeline (the "Datatype not allowed" picker plus its warn-only siblings and the
        // "Term Type Constraint" dialog). Those modals pump the message loop, so both timers
        // must bail while one is up or a re-entrant tick stacks a naming Required popup ON TOP
        // of it (2026-07-08, generalized 2026-07-09). Unlike the naming guards, these dialogs
        // set NONE of _columnNamingCheckInProgress/_scopedCheckInProgress, and they can be shown
        // from a path where _isProcessingChange is already false (ValidateCommittedPendingAttrs
        // -> Enforce, after ProcessAttributeChanges reset it), so a DEDICATED flag is the only
        // reliable guard. Set via ShowValidationModal / try-finally around the picker Show.
        private volatile bool _validationModalShowing;

        // Post-gesture attribute recheck queue (2026-07-09). erwin commits some writes on a
        // DELAYED transaction - most notably the auto-uniquify rename ('Pre_Abc' ->
        // 'Pre_Abc__1070') that follows a name collision - and that commit can land while one
        // of our modals pumps (both timers gated) or AFTER the gesture drained the pending-new
        // signal. At that point NOTHING re-observes the attribute: the heartbeat is count-only
        // (a rename has no count delta) and ScanForRenamesEventDriven walks entities only, so
        // the snapshot-vs-live drift sat unread forever and Name rules (e.g. a no-digits
        // Regexp) never ran on the '__NNNN' name (user bug 2026-07-09, 'Pre_Abc__1070').
        // Every site that writes/validates a name schedules the attribute here; MonitorTimer
        // drains due entries and routes any drift through the NORMAL ProcessAttributeChanges
        // machinery. Key = attribute ObjectId (dedup), value = owning table + due time.
        private readonly Dictionary<string, (string TableName, DateTime DueUtc)> _attrRecheckQueue =
            new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);

        // Inline-edit recheck candidates (2026-07-09): attributes whose snapshot matched the
        // TEXT the user started editing in-place (Model Explorer F2 / Properties-pane grid -
        // both use a plain Win32 'Edit', see Win32Helper.IsInlineEditActive). Captured on the
        // inline-edit OPEN edge from in-memory snapshots only (no SCAPI walk), scheduled into
        // _attrRecheckQueue on the CLOSE edge. This is what makes an EXISTING column's rename /
        // retype from the Properties pane or Model Explorer visible at all - neither has any
        // other observer (no editor open, no count delta).
        private readonly List<(string ObjectId, string TableName)> _inlineEditRecheckCandidates =
            new List<(string, string)>();

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
        // Tuple carries IsNew (identity: Cancel deletes vs reverts) AND Revalidate (2026-07-10:
        // should apply=Create rules re-fire, true for any real rename). The comparer dedupes by
        // Name only, so the first-queued flags win for a given entity within a flush window.
        private readonly HashSet<(string Name, bool IsNew, bool Revalidate)> _pendingTableNamingChecks =
            new HashSet<(string, bool, bool)>(new PendingNamingCheckComparer());

        private sealed class PendingNamingCheckComparer : IEqualityComparer<(string Name, bool IsNew, bool Revalidate)>
        {
            public bool Equals((string Name, bool IsNew, bool Revalidate) x, (string Name, bool IsNew, bool Revalidate) y) =>
                string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            public int GetHashCode((string Name, bool IsNew, bool Revalidate) obj) =>
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

        // When each entity entered _pendingNamedEntities. Backs the stale-
        // pending fallback (2026-06-13): a drag-create (click + immediate
        // second click/drag) can open+close erwin's in-place editor within one
        // 100 ms WindowMonitor tick - or skip it entirely - so the inline-edit
        // close edge never fires, the rename path never fires (name stays the
        // default E_<n>), and the entity stayed pending FOREVER with zero
        // checks (user-reported bypass: 'E_41'). Entries older than
        // StalePendingEntityMs with no inline edit open are force-committed.
        private readonly Dictionary<string, DateTime> _pendingEntityAddedAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // 1.5 s: erwin opens the in-place editor essentially instantly on a
        // click-create, and an OPEN editor always suppresses the force (the
        // !inlineEditOpen gate), so this delay only covers the editor's own
        // appearance latency. 4 s felt sluggish for the drag-create case
        // (user feedback 2026-06-13).
        private const int StalePendingEntityMs = 1500;

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
        /// Creation-cascade continuation (2026-07-02). A Create-context scoped check can
        /// RENAME the entity (auto/confirmed Prefix/Suffix apply). That rename re-fires
        /// the scoped check via ScanForRenamesEventDriven on a LATER tick - after
        /// ValidateCommittedPendingAttrs' finally has already cleared
        /// <see cref="_creationGestureEntityIds"/> - so the follow-up check used to run
        /// as isNew=false and silently dropped every remaining ApplyOn=Create rule
        /// (e.g. Create-only Prefix applied, then the Create-only Suffix never fired).
        /// User semantic: ALL checks in the chain triggered by one creation gesture must
        /// see the object's INITIAL state (Create), regardless of how many add-in
        /// renames the chain contains.
        /// Armed in RunScopedTableNamingCheckCore when a Create-context check renamed
        /// the entity; disarmed at the first Create-context check that no longer renames
        /// (fixed point - the cascade is over) or when the entity disappears. Read by
        /// <see cref="IsEntityInCreationGesture"/> and the rename-scan isNew bridge, so
        /// every follow-up trigger (rename scan, editor close, heartbeat) upgrades to
        /// isNew=true while the cascade is live. Keyed by entity ObjectId, which is
        /// stable across renames.
        /// </summary>
        private readonly HashSet<string> _creationCascadeEntityIds =
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

        // Same double-commit dedup for the allowed-datatype whitelist revert (kept
        // separate from the term-type dict so the two machines never share/clobber
        // each other's "last attempt" state). Reuses TermTypeDedupSeconds.
        private readonly Dictionary<string, (string attempt, DateTime when)> _allowedDatatypeRecentAttempts
            = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);

        // The datatype the USER picked in the AllowedDatatypePickerForm for this
        // attribute, remembered so erwin's delayed SECOND combo-commit (the dedup
        // duplicate, popup suppressed) re-enforces the USER's choice - not the
        // automatic fallback, which would silently clobber what they just picked.
        private readonly Dictionary<string, (string pick, DateTime when)> _allowedDatatypeUserPicks
            = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);

        // Snapshot of Key_Group (Index) names for naming standard checks
        private Dictionary<string, string> _keyGroupSnapshots;

        // PK Key_Group ObjectIds already seen by ApplyPrimaryKeyRules this session.
        // First sight == the Create moment for APPLY_ON gating of PRIMARY KEY
        // Template rules. Cleared on every rebaseline (ObjectIds are model-scoped,
        // not globally unique) so a model switch cannot mis-gate APPLY_ON.
        private readonly HashSet<string> _pkTemplateSeen = new HashSet<string>(StringComparer.Ordinal);

        // "pkId|ruleId" of PK Template writes that already FAILED this session
        // (e.g. the rule's target PropertyCode is not writable on a Key_Group).
        // Skipped on later ticks so a misconfigured rule does not re-attempt the
        // throwing write - and spam the log - every heartbeat. Cleared on rebaseline.
        private readonly HashSet<string> _pkTemplateWriteFailed = new HashSet<string>(StringComparer.Ordinal);

        // "pkId|propertyCode" -> last seen value, for the non-template PK naming pass
        // (Prefix/Suffix/Length/Regexp/Required). Mirrors _keyGroupSnapshots: baseline
        // on first sight, validate/auto-apply only on a value change. Cleared on rebaseline.
        private readonly Dictionary<string, string> _pkPropertySnapshots = new Dictionary<string, string>(StringComparer.Ordinal);

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
        // SCAPI model-object Name baseline, distinct from _lastKnownModelName
        // (which tracks the window-title name for tab-switch detection). Used by
        // ScanForModelRenameEventDriven to detect a Model Explorer inline rename
        // of the MODEL node. A fresh ValidationCoordinatorService is created per
        // connect, so this is always the current model (null until baselined).
        private string _modelNameSnapshot;
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

        // One-shot MODEL-level required-UDP enforcement on model open. Runs once
        // per connect, after a short settle so the connect overlay is gone.
        private bool _modelRequiredUdpsChecked;
        private int _connectSettleTicks;
        private const int ConnectSettleTicks = 4; // ~1s at 250ms tick before the model-UDP prompt

        // Event for logging
        public event Action<string> OnLog;

        // Event fired when session becomes invalid (model closed)
        public event Action OnSessionLost;

        // Event fired when active model changes in erwin
        public event Action<string> OnModelChanged;

        // Event fired when a model-level UDP value changes (for cascade update)
        public event Action<string, string> OnModelUdpChanged; // udpName, newValue

        /// <summary>
        /// Raised on the UDP editor's open->closed transition (Tools > User
        /// Defined Properties - the only place a UDP DEFINITION can be
        /// deleted). ModelConfigForm runs the admin-UDP recovery there
        /// (sync re-check + instance-value restore). 2026-06-12.
        /// </summary>
        public event Action OnUdpEditorClosed;

        /// <summary>
        /// Raised on the UDP editor's closed->open transition. ModelConfigForm
        /// snapshots admin-UDP values here (BEFORE any definition delete can wipe
        /// them) so the close-edge recovery can restore them. 2026-06-12 Part A.
        /// </summary>
        public event Action OnUdpEditorOpened;
        private bool _udpEditorWasOpen;

        // Admin Model-UDP values snapshotted on UDP-editor open, restored on
        // close (Part A). _lastModelUdpValues is normally reliable, but copying
        // it at open time guarantees the pre-delete values survive even if a
        // tick mutates the live cache between delete and restore.
        private Dictionary<string, string> _udpRecoveryModel;


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
            // Live while EITHER the placeholder-commit gesture OR the
            // rename-continuation cascade holds the entity (see
            // _creationCascadeEntityIds doc).
            if (_creationGestureEntityIds.Count == 0 && _creationCascadeEntityIds.Count == 0) return false;
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
                    return !string.IsNullOrEmpty(eid)
                        && (_creationGestureEntityIds.Contains(eid) || _creationCascadeEntityIds.Contains(eid));
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

            // Let the (static) naming engine resolve MODEL-scoped condition UDPs
            // against THIS model's root. A naming-rule condition can depend on a UDP
            // that lives on the model rather than the rule's target object (e.g. an
            // "Application" model UDP gating a TABLE prefix model-wide); the engine
            // falls back to this root only when the entity/column read reports the
            // property is not on that class. Refreshed every connect so it always
            // points at the active model.
            NamingValidationEngine.ModelRootProvider = () => _session?.ModelObjects?.Root;

            // Re-arm the one-shot MODEL required-UDP check for this connect, so a
            // model switch / reconnect re-validates the new model. (Harmless if the
            // values are already filled - the check finds nothing missing.)
            _modelRequiredUdpsChecked = false;
            _connectSettleTicks = 0;

            // Initialize model change tracking (use same source as detection: window title)
            try
            {
                _lastKnownModelName = GetErwinActiveModelName();
                if (string.IsNullOrEmpty(_lastKnownModelName))
                    _lastKnownModelName = _session.ModelObjects.Root?.Name ?? "";
                _modelCheckCounter = 0;
                // Baseline the SCAPI model-object Name (raw, not the window-title
                // form) for the inline-edit-close rename scan. A null here is fine:
                // ScanForModelRenameEventDriven lazily baselines on first sight.
                _modelNameSnapshot = _session.ModelObjects.Root?.Name ?? "";
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
            _pkTemplateSeen.Clear();
            _pkTemplateWriteFailed.Clear();
            _pkPropertySnapshots.Clear();
            _domainCache.Clear();
            _tablesBaselined.Clear();

            // Capture the DIAGRAM-HEARTBEAT baseline (entity ids + per-entity attr
            // ids/counts + totals) synchronously NOW, at connect, while the model
            // is loaded. This is the change-DETECTION baseline only - the per-table
            // validation baseline (_attributeSnapshots) stays Phase-2D lazy. Without
            // it the heartbeat's first tick (which only fires after the connect
            // settle + model-UDP one-shot, ~10 s later) silently absorbs any table
            // the user adds in that window into the baseline, so it is never
            // validated - the user-reported model-switch-back bug (2026-06-29).
            BaselineDiagramHeartbeat();

            _monitorTimer.Start();
            _windowMonitorTimer.Start();
            Log("ValidationCoordinatorService: Monitoring started (Phase-2D: per-table lazy validation baseline; heartbeat baselined at connect)");
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
            _pkTemplateSeen.Clear();
            _pkTemplateWriteFailed.Clear();
            _pkPropertySnapshots.Clear();
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
                _pkTemplateSeen.Clear();
                _pkTemplateWriteFailed.Clear();
                _pkPropertySnapshots.Clear();
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
                            catch (Exception ex) { Log($"InitializeModelUdpTracking: Property_Type read skipped: {ex.Message}"); }
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
        /// One-shot MODEL-level required-UDP enforcement, invoked from the first
        /// settled heartbeat after the model connects. Delegates to the table-type
        /// monitor (which owns the UDP runtime + the Required dialog). Best-effort:
        /// a failure is logged and never disrupts the heartbeat.
        /// </summary>
        private void CheckModelRequiredUdpsOnce(dynamic root)
        {
            try
            {
                if (_tableTypeMonitor == null || root == null) return;

                string modelName = "";
                try { modelName = root.Name?.ToString() ?? ""; } catch { /* root.Name may be unavailable */ }
                if (string.IsNullOrEmpty(modelName))
                {
                    try { modelName = root.Properties("Physical_Name")?.Value?.ToString() ?? ""; }
                    catch { /* fall through to default */ }
                }
                if (string.IsNullOrEmpty(modelName)) modelName = "Model";

                _tableTypeMonitor.PromptForMissingRequiredModelUdps(root, modelName);

                // Object-type-only Required rules ("an object of this type must
                // exist", Property "(none)"): warn-only model-open check, same
                // one-shot guard as the required-UDP prompt above. Runs after it
                // so the required-UDP dialog (if any) is dealt with first.
                _tableTypeMonitor.CheckRequiredObjectTypesExist(root);
            }
            catch (Exception ex)
            {
                Log($"CheckModelRequiredUdpsOnce error (best-effort): {ex.Message}");
            }
        }

        /// <summary>
        /// Restores MODEL-level UDP values after their definitions were
        /// recreated by the UDP-editor-close recovery. Source = the open-time
        /// recovery copy when present (captures pre-delete values), else the
        /// live baseline. Only writes names tracked with a non-empty value.
        /// </summary>
        public void RestoreModelUdpValues(IReadOnlyCollection<string> udpNames)
        {
            if (udpNames == null || udpNames.Count == 0) return;
            if (_udpRuntimeService == null) { Log("RestoreModelUdpValues: UDP runtime not available."); return; }

            var source = (_udpRecoveryModel != null && _udpRecoveryModel.Count > 0)
                ? _udpRecoveryModel
                : _lastModelUdpValues;

            var toWrite = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in udpNames)
            {
                if (!string.IsNullOrEmpty(name)
                    && source.TryGetValue(name, out var val)
                    && !string.IsNullOrEmpty(val))
                {
                    toWrite[name] = val;
                }
            }
            if (toWrite.Count == 0) { Log("RestoreModelUdpValues: no tracked model values to restore."); return; }

            try
            {
                dynamic root = _session.ModelObjects.Root;
                if (root == null) return;
                _udpRuntimeService.WriteUdpValues((object)root, toWrite, "Model");
                Log($"RestoreModelUdpValues: restored {toWrite.Count} model UDP value(s): {string.Join(", ", toWrite.Keys)}");
            }
            catch (Exception ex)
            {
                Log($"RestoreModelUdpValues failed: {ex.Message}");
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

                        // Required-UDP clear protection (2026-06-12): clears
                        // (non-empty -> empty) were previously ignored entirely
                        // by the IsNullOrEmpty(val) gate below, so a required
                        // MODEL UDP (e.g. a List value like Application/Owner)
                        // could be silently emptied. Restore the previous value
                        // + warn, mirroring the table/view observers.
                        if (string.IsNullOrEmpty(val) && !string.IsNullOrEmpty(prevVal)
                            && _udpRuntimeService != null)
                        {
                            var def = UdpDefinitionService.Instance.IsLoaded
                                ? UdpDefinitionService.Instance.GetByName("Model", udpName)
                                : null;
                            if (def != null && def.IsRequired)
                            {
                                try
                                {
                                    _udpRuntimeService.WriteUdpValues(
                                        (object)root,
                                        new Dictionary<string, string> { [udpName] = prevVal },
                                        "Model");
                                    Log($"[ModelUDP] '{udpName}' is required - cleared value restored to '{prevVal}'");

                                    Forms.AddinMessageDialog.Show(
                                        $"UDP '{udpName}' is required. The value cannot be cleared; '{prevVal}' was restored.",
                                        "UDP Required",
                                        System.Windows.Forms.MessageBoxButtons.OK,
                                        System.Windows.Forms.MessageBoxIcon.Warning);
                                }
                                catch (Exception revertEx)
                                {
                                    Log($"[ModelUDP] required restore failed for '{udpName}': {revertEx.Message}");
                                }
                                // _lastModelUdpValues keeps prevVal - the rejected
                                // clear never becomes baseline.
                                continue;
                            }
                        }

                        if (!string.Equals(val, prevVal ?? "", StringComparison.Ordinal) && !string.IsNullOrEmpty(val))
                        {
                            _lastModelUdpValues[udpName] = val;
                            Log($"[ModelUDP] '{udpName}' changed: '{prevVal}' -> '{val}'");
                            OnModelUdpChanged?.Invoke(udpName, val);
                        }
                    }
                    catch (Exception ex) { Log($"[ModelUDP] check error for '{path}': {ex.Message}"); }
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
            if (_sessionLost || !_isMonitoring || _disposed || _isProcessingChange || _validationSuspended || _isCheckingForChanges || _columnNamingCheckInProgress || _scopedCheckInProgress || _validationModalShowing) return;
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

                // View + Subject Area scan (2026-06-12). Its historical driver
                // (TableTypeMonitorService.CheckForTableTypeChanges) lost its
                // caller in Phase-2D, so view checks never ran. The scan can
                // open MODAL dialogs (new-view pipeline: Required UDP form,
                // naming popups) whose pump re-fires this timer - hold
                // _scopedCheckInProgress for the duration so reentrant ticks
                // bounce off the entry guard, same pattern as
                // FireNewEntityPipeline.
                if (_tableTypeMonitor != null)
                {
                    bool acquired = !_scopedCheckInProgress;
                    if (acquired) _scopedCheckInProgress = true;
                    try
                    {
                        _tableTypeMonitor.RunViewAndSubjectAreaScan();
                    }
                    catch (Exception ex)
                    {
                        Log($"View/SA scan error: {ex.Message}");
                    }
                    finally
                    {
                        if (acquired) _scopedCheckInProgress = false;
                    }
                }
            }

            try
            {
                _isCheckingForChanges = true;

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // One-shot: enforce MODEL-level required UDPs once the model is
                // open + settled. The guards above guarantee a live, non-suspended
                // session; the settle delay lets the connect overlay close first so
                // the prompt is not hidden behind it. "Model open" == Update context
                // (Apply On = Update / Both). Runs exactly once per connect; the
                // modal runs inside this try so _isCheckingForChanges suppresses
                // reentrant ticks, and the finally resets it on return.
                if (!_modelRequiredUdpsChecked)
                {
                    if (++_connectSettleTicks < ConnectSettleTicks) return;
                    _modelRequiredUdpsChecked = true;
                    // The unattended DDL worker opens models headlessly; the
                    // model-required-UDP prompt (a modal) would block it forever.
                    // Skip it while a worker job owns the connect (see
                    // ModelConfigForm.DdlWorkerActiveUnattended).
                    if (ModelConfigForm.DdlWorkerActiveUnattended)
                        Log("Model-required-UDP prompt skipped (DDL worker unattended).");
                    else
                        CheckModelRequiredUdpsOnce(root);
                    return; // resume normal scanning on the next tick
                }

                // Post-gesture recheck drain (2026-07-09): targeted live-vs-snapshot re-diff of
                // attributes whose gesture may have raced one of erwin's DELAYED commits (auto-
                // uniquify rename during/after a modal). Runs in BOTH modes (editor open or not);
                // when the Column Editor is open the scoped scan below covers the same entity, so
                // a drained entry simply no-ops on the already-advanced snapshot.
                DrainAttributeRecheckQueue(modelObjects, root);

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

                    // Selection-scoped fingerprint (2026-07-10): the count-only heartbeat above
                    // cannot see a Physical_Data_Type change on an EXISTING column that the user
                    // makes purely via the Properties-pane dropdown (no "Edit" control focus, so
                    // the inline-edit candidate mechanism never captures it - the one gap left
                    // after the F fix). erwin's Overview pane exposes the currently-selected
                    // entity; fingerprint just that ONE entity's columns each heartbeat so a
                    // pane/combo datatype or name edit is caught within ~1 s. Bounded to the
                    // single selected entity - never a model-wide walk.
                    SelectionScopedAttributeCheck(modelObjects, root);
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
        /// Captures the diagram-heartbeat baseline (entity ids, per-entity attribute
        /// id sets + counts, and the total Attribute/Entity counters) for the model
        /// active RIGHT NOW. Called synchronously from StartMonitoring at connect so
        /// the heartbeat's first tick treats only objects added AFTER connect as new.
        /// Without it the first tick - delayed by the connect settle + model-UDP
        /// one-shot (~10 s) - silently baselines whatever the model contains then,
        /// absorbing any table the user adds in that window so it is never validated
        /// (user-reported model-switch-back bug, 2026-06-29). Pure snapshot, no
        /// validation. On any failure the snapshots are cleared and the counters
        /// reset to -1, so the original first-tick baseline path still runs (safe
        /// fallback that only widens the absorb window back to the old behaviour).
        /// </summary>
        private void BaselineDiagramHeartbeat()
        {
            dynamic entityCollection = null;
            dynamic attrCollection = null;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects?.Root;
                if (root == null) { _lastTotalAttributeCount = -1; _lastTotalEntityCount = -1; return; }

                _entityIdSnapshot.Clear();
                _entityAttrIdSnapshot.Clear();
                _entityAttrCountSnapshot.Clear();
                _entityDisplayNameSnapshot.Clear();

                entityCollection = modelObjects.Collect(root, "Entity");
                attrCollection = modelObjects.Collect(root, "Attribute");
                if (entityCollection == null) { _lastTotalAttributeCount = -1; _lastTotalEntityCount = -1; return; }

                // Partial-read guard (mirrors ModelConfigForm's incomplete-metamodel
                // read guard, 2026-06-08): a transient reconnect while erwin is still
                // reloading the model (e.g. Mart Save-As + Cancel) hands us an empty
                // entity collection. Do NOT trust a connect-time baseline then - reset
                // to -1 so the deferred first-tick baseline runs after the ~10 s settle
                // (by when the reload has finished). A genuinely empty model is harmless
                // either way (first tick simply baselines 0).
                if ((long)entityCollection.Count == 0)
                {
                    _lastTotalAttributeCount = -1;
                    _lastTotalEntityCount = -1;
                    Log("DiagramHeartbeat: connect-time baseline saw 0 entities (empty model or mid-reload) - deferring to first-tick baseline");
                    return;
                }

                foreach (dynamic entity in entityCollection)
                {
                    if (entity == null) continue;
                    string entityId;
                    try { entityId = entity.ObjectId?.ToString(); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(entityId)) continue;
                    _entityIdSnapshot.Add(entityId);

                    var attrIds = new HashSet<string>(StringComparer.Ordinal);
                    dynamic entityAttrs = null;
                    try
                    {
                        entityAttrs = modelObjects.Collect(entity, "Attribute");
                        if (entityAttrs != null)
                        {
                            foreach (dynamic a in entityAttrs)
                            {
                                string aid;
                                try { aid = a.ObjectId?.ToString(); }
                                catch { continue; }
                                if (!string.IsNullOrEmpty(aid)) attrIds.Add(aid);
                            }
                        }
                    }
                    catch (Exception ex) { Log($"BaselineDiagramHeartbeat attr-walk err on '{entityId}': {ex.Message}"); }
                    finally { ReleaseCom(entityAttrs); }

                    _entityAttrIdSnapshot[entityId] = attrIds;
                    _entityAttrCountSnapshot[entityId] = attrIds.Count;

                    // Display name baseline (same derivation the heartbeat uses) so a
                    // rename of a pre-existing entity between connect and the first
                    // tick is still diffable rather than silently absorbed.
                    string displayName;
                    try
                    {
                        // Identical derivation to DiagramHeartbeatTick (Physical_Name
                        // via Properties(...).Value) so the first tick does not see a
                        // phantom rename from a different read path.
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        displayName = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? entityId);
                    }
                    catch { try { displayName = entity.Name ?? entityId; } catch { displayName = entityId; } }
                    _entityDisplayNameSnapshot[entityId] = displayName;
                }

                // Totals are read the SAME way the heartbeat reads them (Collect.Count)
                // so an unchanged model yields delta==0 on the first tick (no spurious
                // validation).
                _lastTotalEntityCount = (long)entityCollection.Count;
                _lastTotalAttributeCount = attrCollection != null ? (long)attrCollection.Count : 0;
                Log($"DiagramHeartbeat: connect-time baseline captured ({_lastTotalEntityCount} entities, {_lastTotalAttributeCount} attrs); post-connect additions will be detected");
            }
            catch (Exception ex)
            {
                _entityIdSnapshot.Clear();
                _entityAttrIdSnapshot.Clear();
                _entityAttrCountSnapshot.Clear();
                _entityDisplayNameSnapshot.Clear();
                _lastTotalAttributeCount = -1;
                _lastTotalEntityCount = -1;
                Log($"DiagramHeartbeat: connect-time baseline failed ({ex.Message}); deferring to first-tick baseline");
            }
            finally
            {
                ReleaseCom(entityCollection);
                ReleaseCom(attrCollection);
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
                var entitiesToNamingCheck = new List<(string name, bool isNew, bool revalidate)>();

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
                                _pendingEntityAddedAt[entityId] = DateTime.UtcNow;
                                Log($"[PENDING-ENTITY] entityId={entityId} name='{displayName}' - placeholder, holding for inline-edit commit / rename");
                            }
                            else
                            {
                                entitiesToNamingCheck.Add((displayName, isNew: true, revalidate: true));
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
                                _pendingEntityAddedAt[entityId] = DateTime.UtcNow;
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
                                _pendingEntityAddedAt.Remove(entityId);
                                // Placeholder-origin (prev name "E/17"/"<default>"/"") is a
                                // creation commit even if the pending set was already
                                // drained - keeps Create-only rules alive whose condition
                                // (Owner/Schema) is filled during Required-UDP after the
                                // first check (see the inline-edit-close scan bridge).
                                // erwin auto-uniquify ('Foo' -> 'Foo__1' on a name collision) is an
                                // erwin-assigned name, not a user rename: re-validate as create so
                                // apply=Create rules re-fire on the '__NNNN' name (same fix as columns).
                                bool entityIsNew = wasPending
                                    || _creationGestureEntityIds.Contains(entityId)
                                    || _creationCascadeEntityIds.Contains(entityId)
                                    || IsPlaceholderEntityName(prevDisplayName)
                                    || NamingValidationEngine.IsAutoUniquifyRename(prevDisplayName, displayName);

                                // 2026-07-10: a MANUAL table rename (real prev name, not an
                                // erwin/placeholder auto-name) must re-run apply=Create naming rules
                                // on the new name - but identity stays isNew=false so a Required-popup
                                // Cancel REVERTS the name, it does NOT delete the pre-existing table.
                                bool entityRevalidate = entityIsNew
                                    || NamingValidationEngine.RenameRequiresRevalidation(prevDisplayName, displayName, IsPlaceholderEntityName);

                                entitiesToNamingCheck.Add((displayName, entityIsNew, entityRevalidate));
                                Log($"[NAMING] entity renamed '{prevDisplayName}' -> '{displayName}' - queuing naming check (isNew={entityIsNew}, revalidate={entityRevalidate})");

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
                        _pendingEntityAddedAt.Remove(id);
                        // A deleted entity can never be disarmed by a scoped check
                        // (nothing resolves to it by name any more) - GC its gesture/
                        // cascade ids here so a leaked id cannot keep the
                        // IsEntityInCreationGesture fast path (Count==0) defeated and
                        // force a full entity walk on every later naming check.
                        _creationGestureEntityIds.Remove(id);
                        _creationCascadeEntityIds.Remove(id);
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
                foreach (var (entityName, entityIsNew, entityRevalidate) in entitiesToNamingCheck)
                {
                    if (editorOpen)
                    {
                        // Keep the isNew AND revalidate flags on the deferred entry so the
                        // editor-close flush can still pick "Discard New Table" over "Revert
                        // Change" (isNew) and re-run apply=Create rules on a rename (revalidate).
                        if (_pendingTableNamingChecks.Add((entityName, entityIsNew, entityRevalidate)))
                            Log($"DiagramHeartbeat: deferring naming check on '{entityName}' (editor open, isNew={entityIsNew}, revalidate={entityRevalidate})");
                        continue;
                    }
                    try { RunScopedTableNamingCheck(entityName, isNew: entityIsNew, revalidateAsNew: entityRevalidate); }
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
                    // Ordered (name, objectId) capture for the order-enforcement
                    // pass below - Collect("Attribute") returns columns in their
                    // on-screen order, which is exactly what we diff against the
                    // locked block.
                    var orderedAttrs = new List<(string Name, string ObjectId)>();
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
                                        {
                                            currentLockedAttrNames.Add(aName);
                                            string aObjId = "";
                                            try { aObjId = a.ObjectId?.ToString() ?? ""; } catch { /* objectId unavailable */ }
                                            orderedAttrs.Add((aName, aObjId));
                                        }
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

                    // Locked-column ORDER enforcement (2026-06-07): predefined
                    // locked columns must stay as a contiguous block at the START
                    // of the table in SORT_ORDER; every user-added column must sit
                    // AFTER them. Detect any non-locked column the user wedged in
                    // front of / between the locked block and push it to the end.
                    // Only locked columns CURRENTLY PRESENT and APPLICABLE define
                    // the block (conditional locks respected). A plain column is
                    // moved automatically (delete + re-add at end); a key/FK column
                    // is warn-only (deleting it would destroy the relationship).
                    try
                    {
                        var presentApplicableLocked = new List<string>();
                        foreach (var lr in PredefinedColumnService.Instance.GetLocked())
                        {
                            if (string.IsNullOrEmpty(lr.ColumnName)) continue;
                            if (!currentLockedAttrNames.Contains(lr.ColumnName)) continue;
                            var applic = PredefinedColumnService.Instance.FindApplicableLockedRule(entity, lr.ColumnName);
                            if (applic != null && applic.Id == lr.Id)
                                presentApplicableLocked.Add(lr.ColumnName);
                        }

                        var orderedNames = orderedAttrs.Select(t => t.Name).ToList();
                        var wedged = PredefinedColumnService.ComputeColumnsWedgedInLockedBlock(orderedNames, presentApplicableLocked);

                        if (wedged.Count > 0)
                        {
                            string entityNameForOrder = nameForMatch;
                            foreach (var wedgedNameRaw in wedged)
                            {
                                string capturedName = wedgedNameRaw;
                                string capturedEntity = entityNameForOrder;
                                string objId = orderedAttrs
                                    .FirstOrDefault(t => string.Equals(t.Name, capturedName, StringComparison.OrdinalIgnoreCase)).ObjectId;
                                bool isKey = !string.IsNullOrEmpty(objId) && _tableTypeMonitor.IsColumnKeyMember(entity, objId);
                                string dedupe = $"order|{capturedEntity}|{capturedName}";

                                if (isKey)
                                {
                                    string detail = "This is a key/foreign-key column, so it was NOT moved automatically.\nPlease move it after the locked columns yourself.";
                                    Log($"Locked column order: '{capturedEntity}.{capturedName}' is wedged in the locked block but is a key/FK column - warn only (no auto-move).");
                                    EnqueueLockedColumnDialogAndApply(
                                        capturedName, capturedEntity, Forms.LockedColumnAction.OrderEnforced, detail, dedupe, null);
                                }
                                else
                                {
                                    string detail = "It was moved to the end of the table to keep the locked column order.";
                                    Log($"Locked column order: '{capturedEntity}.{capturedName}' wedged in the locked block - deferring dialog + move-to-end.");
                                    EnqueueLockedColumnDialogAndApply(
                                        capturedName, capturedEntity, Forms.LockedColumnAction.OrderEnforced, detail, dedupe,
                                        () =>
                                        {
                                            try
                                            {
                                                dynamic moveEntity = ResolveEntityByName(capturedEntity);
                                                if (moveEntity == null)
                                                {
                                                    Log($"Locked column order move: entity '{capturedEntity}' not found at apply time");
                                                    return;
                                                }
                                                _tableTypeMonitor.MoveColumnToEnd(moveEntity, capturedName, capturedEntity);
                                            }
                                            catch (Exception ex)
                                            {
                                                Log($"Locked column order move FAILED for '{capturedName}' on '{capturedEntity}': {ex.Message}");
                                            }
                                        });
                                }
                            }
                            // Deferred dialogs + moves own the mutation for this
                            // close; skip the normal reevaluate so it cannot race
                            // the pending column deletes/re-adds.
                            return;
                        }
                    }
                    catch (Exception orderEx) { Log($"Locked column order enforcement err for '{nameForMatch}': {orderEx.Message}"); }

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
                    _pendingEntityAddedAt.Remove(id);
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
                    bool inCreationGesture = _creationGestureEntityIds.Contains(id)
                        || _creationCascadeEntityIds.Contains(id);
                    // A rename whose OLD name is an erwin placeholder ("E/17", "<default>",
                    // "") is unambiguously the first real naming of a new entity - the
                    // creation commit - even if the pending/gesture sets were already
                    // drained. Required-UDP flow (which can set the very properties a
                    // Create-only rule conditions on, e.g. Owner/Schema) runs BETWEEN the
                    // commit-edge check and this inline-edit-close scan, so without this
                    // the settle check ran isNew=false and Create rules whose condition
                    // only just became true (SCHEMA.Name=DM) never fired (Furkan rule#1175).
                    bool fromPlaceholder = IsPlaceholderEntityName(oldName);
                    // erwin auto-uniquify ('Foo' -> 'Foo__1') is an erwin-assigned name: re-validate
                    // as create so apply=Create rules re-fire on the '__NNNN' name (same fix as columns).
                    bool fromUniquify = NamingValidationEngine.IsAutoUniquifyRename(oldName, newName);
                    bool entityIsNew = wasPending || inCreationGesture || fromPlaceholder || fromUniquify;
                    if (entityIsNew)
                        Log($"  rename '{oldName}' -> '{newName}' is placeholder commit (wasPending={wasPending}, inCreationGesture={inCreationGesture}, fromPlaceholder={fromPlaceholder}, fromUniquify={fromUniquify}) - treating as new-entity creation flow (isNew=true)");

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
            Log($"Editor close: flushing {queued.Length} deferred naming check(s): {string.Join(", ", queued.Select(q => $"{q.Name}(isNew={q.IsNew},revalidate={q.Revalidate})"))}");
            foreach (var (entityName, isNew, revalidate) in queued)
            {
                try { RunScopedTableNamingCheck(entityName, isNew: isNew, revalidateAsNew: revalidate); }
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
            // 2026-06-06: skip while a column naming Required-field popup is up.
            // That dialog pumps the loop; re-entering here re-detects the same
            // pending rename and stacks another popup (see _columnNamingCheckInProgress).
            if (_columnNamingCheckInProgress) return;
            // 2026-06-06: same hazard for the TABLE naming path - its auto-apply
            // "Naming standard applied" modal (TableTypeMonitorService) sets
            // _scopedCheckInProgress and pumps the loop; without this bail the tick
            // re-enters, validates the just-added columns, and stacks a column
            // Required-field popup ON TOP of the table modal (before its OK).
            if (_scopedCheckInProgress) return;

            // 2026-07-08: same hazard for the DATATYPE picker. EnforceAllowedDatatypeWhitelist's
            // "Datatype not allowed" modal pumps the loop but sets NONE of the naming guards above,
            // so without this the tick re-enters, detects erwin's auto-uniquify rename inline-edit
            // edge, and stacks a column naming Required-field popup ON TOP of the picker (they must
            // come sequentially, not overlap). Bailing here (before any window-edge state is read)
            // leaves the inline-edit-close edge intact, so the naming check fires on the next tick
            // AFTER the picker closes. (The naming Comment/Required dialog is already covered by
            // _columnNamingCheckInProgress; only the datatype picker needed its own flag.)
            // 2026-07-09: the flag now also covers the warn-only whitelist dialogs and the
            // Term Type Constraint dialog (ShowValidationModal).
            if (_validationModalShowing) return;

            // 2026-07-09: also bail while the change pipeline itself runs (MonitorTimer's
            // CheckEntityForChanges / ProcessAttributeChanges). Their dialogs pump this timer,
            // and the close-edge work below (ValidateCommittedPendingAttrs,
            // FinalValidateClosedTable) would re-enter the same attribute mid-flight and stack a
            // second modal. MonitorTimer_Tick already honors these flags; this timer must too.
            // The bail consumes no edge STATE, so edges fire on the first tick afterwards.
            if (_isProcessingChange || _isCheckingForChanges) return;

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

            // UDP editor close-edge (2026-06-12): the only window where a UDP
            // DEFINITION can be deleted. On the open->closed transition raise
            // the recovery event; the form re-runs the admin UDP sync and
            // restores instance values from session snapshots. The event
            // handler shows modal UI, so it is raised LAST-wins safe: a
            // reentrant tick during its pump sees wasOpen=false and no-ops.
            try
            {
                bool udpEditorOpen = IsUdpEditorOpen();
                if (udpEditorOpen && !_udpEditorWasOpen)
                {
                    _udpEditorWasOpen = true;
                    // Capture the pre-delete admin Model-UDP values now, while the
                    // definitions still exist, then let the form snapshot table/
                    // view values too. Silent (read-only) - no modal here.
                    _udpRecoveryModel = new Dictionary<string, string>(_lastModelUdpValues, StringComparer.OrdinalIgnoreCase);
                    Log($"UDP editor opened - captured {_udpRecoveryModel.Count} model UDP value(s); requesting table/view snapshot.");
                    try { OnUdpEditorOpened?.Invoke(); }
                    catch (Exception evEx) { Log($"OnUdpEditorOpened handler error: {evEx.Message}"); }
                }
                else if (!udpEditorOpen && _udpEditorWasOpen)
                {
                    _udpEditorWasOpen = false;
                    Log("UDP editor closed - raising admin-UDP recovery check.");
                    try { OnUdpEditorClosed?.Invoke(); }
                    catch (Exception evEx) { Log($"OnUdpEditorClosed handler error: {evEx.Message}"); }
                }
            }
            catch (Exception ex) { Log($"UDP editor edge check error: {ex.Message}"); }

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

                // --- Model Editor ("Model 'X' Editor") close detection (2026-06-05).
                // Model-level naming rules (e.g. MODEL.Definition req=True,
                // MODEL.Name regex/prefix) had NO event-driven trigger - the only
                // model validation was the on-demand Validation tab, and even that
                // checked the name only. So closing the Model Editor with an empty
                // required description never warned. Mirror the Column/Entity Editor
                // close edge: on the open->closed transition run model validation
                // (ValidateModelOnEditorClose) - Required violations open a
                // RequiredFieldDialog that WRITES the typed value back (with a regex
                // re-prompt loop); other violations are warn-only popups. (The same
                // validator is now also reached from ScanForModelRenameEventDriven
                // on a Model Explorer inline rename.)
                bool modelEditorIsOpen = IsModelEditorOpen();
                if (_modelEditorWasOpen && !modelEditorIsOpen)
                {
                    // Re-entrancy: flip state BEFORE the popup pumps the loop (same
                    // hazard documented on the Entity Editor branch above).
                    _modelEditorWasOpen = false;
                    // Revert target = the model name captured when the editor opened
                    // (its pre-edit value). A "Revert Change" on a rule-violating
                    // rename done IN the editor now restores it, same as the inline
                    // path. nameOnly stays false: the editor can also change
                    // Definition etc., so all model properties are still validated;
                    // only the NAME gets the revert.
                    string editorOldName = _modelEditorOpenName;
                    try { ValidateModelOnEditorClose(nameRevertValue: editorOldName, nameOnly: false); }
                    catch (Exception ex) { Log($"ValidateModelOnEditorClose err: {ex.Message}"); }
                    // Keep the inline-rename baseline in sync with whatever the editor
                    // (and the validation) left, so the inline scan does not then
                    // see a phantom rename.
                    try { _modelNameSnapshot = _session.ModelObjects.Root?.Name ?? _modelNameSnapshot; }
                    catch { /* keep prior baseline */ }
                    _modelEditorOpenName = null;
                }
                else
                {
                    // Editor OPEN transition: capture the current (pre-edit) model
                    // name as the revert target for when it closes.
                    if (!_modelEditorWasOpen && modelEditorIsOpen)
                    {
                        try { _modelEditorOpenName = _session.ModelObjects.Root?.Name ?? ""; }
                        catch { _modelEditorOpenName = null; }
                    }
                    _modelEditorWasOpen = modelEditorIsOpen;
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

                // Inline-edit OPEN edge (2026-07-09): remember WHAT the user started editing.
                // The in-place Edit's initial text is the OLD value; matching it against the
                // in-memory snapshots identifies the attribute(s) an EXISTING column's rename /
                // retype could belong to - the Properties-pane grid and the Model Explorer F2
                // editor are both plain Win32 'Edit' controls, and neither has any other
                // observer (no editor window, no attr-count delta).
                if (!_wasInlineEditOpen && inlineEditOpen)
                {
                    try { CaptureInlineEditCandidates(erwinHwnd); }
                    catch (Exception ex) { Log($"[INLINE-EDIT] open-edge capture err: {ex.Message}"); }
                }

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

                    // Model rename (2026-06-24): renaming the MODEL node in Model
                    // Explorer commits on this same inline-edit close edge but never
                    // opens the Model Editor dialog, so MODEL.Name / MODEL.Definition
                    // rules were never enforced. Mirror the entity rename scan. The
                    // Model Editor DIALOG does not trigger this inline edge (its
                    // textbox is not the tree's in-place Edit) - it is handled by the
                    // editor open/close block above; and even if it ever did, that
                    // block already validated the model and refreshed _modelNameSnapshot
                    // this same tick, so this scan finds no diff and self-suppresses.
                    try { ScanForModelRenameEventDriven("inline-edit-close"); }
                    catch (Exception ex) { Log($"ScanForModelRenameEventDriven (inline) err: {ex.Message}"); }

                    // View name-commit (2026-06-14): the SAME inline-edit close
                    // edge that commits a pending table name commits a pending
                    // VIEW name (IsInlineEditActive is object-type-agnostic - the
                    // diagram/tree inline editor is a plain Win32 'Edit' for both).
                    // _scopedCheckInProgress MUST wrap the call (mirrors the
                    // MonitorTimer view-scan site): CommitPendingViews opens the
                    // naming / Required-UDP modals whose pump re-fires this timer,
                    // and the early bail on _scopedCheckInProgress makes the
                    // reentrant tick a no-op.
                    if (_tableTypeMonitor != null)
                    {
                        bool acquired = !_scopedCheckInProgress;
                        if (acquired) _scopedCheckInProgress = true;
                        try { _tableTypeMonitor.CommitPendingViews(); }
                        catch (Exception ex) { Log($"CommitPendingViews (inline) err: {ex.Message}"); }
                        finally { if (acquired) _scopedCheckInProgress = false; }
                    }

                    // Existing-COLUMN rename/retype coverage (2026-07-09): the scans above
                    // handle entities, models, views and locked columns, but a plain existing
                    // column edited via Model Explorer F2 or the Properties-pane grid had NO
                    // observer. Schedule the candidates captured on the open edge for a
                    // targeted live-vs-snapshot recheck (drained by MonitorTimer).
                    try { FlushInlineEditCandidates(); }
                    catch (Exception ex) { Log($"[INLINE-EDIT] close-edge flush err: {ex.Message}"); }
                }

                // Stale-pending fallback (2026-06-13): a drag-create (click +
                // immediate second click/drag) can open+close erwin's in-place
                // editor within one 100 ms tick - or skip it entirely - so the
                // close edge above never fires, the rename path never fires
                // (the name stays the default E_<n>), and the new entity sat
                // pending FOREVER with ZERO checks (user-reported bypass:
                // 'E_41'). With no inline edit open, anything pending longer
                // than StalePendingEntityMs counts as committed-as-is.
                if (!inlineEditOpen && _pendingNamedEntities.Count > 0)
                {
                    bool anyStale = false;
                    var nowUtc = DateTime.UtcNow;
                    foreach (var pendId in _pendingNamedEntities)
                    {
                        if (!_pendingEntityAddedAt.TryGetValue(pendId, out var addedAt)
                            || (nowUtc - addedAt).TotalMilliseconds >= StalePendingEntityMs)
                        {
                            anyStale = true;
                            break;
                        }
                    }
                    if (anyStale)
                    {
                        Log("[PENDING-ENTITY] stale pending entity with no inline edit open - forcing commit validation (drag-create bypass guard).");
                        try { ValidateCommittedPendingAttrs(); }
                        catch (Exception ex) { Log($"ValidateCommittedPendingAttrs (stale) err: {ex.Message}"); }
                    }
                }

                // View analogue of the drag-create guard above: a view dropped
                // fast enough that no inline 'Edit' was ever caught open would
                // otherwise sit pending forever. With no editor open, force-commit
                // any view pending past the same StalePendingEntityMs window. The
                // '!inlineEditOpen' precondition is mandatory - never commit while
                // the user is still typing a name. _scopedCheckInProgress wraps
                // the modal-opening drain, same as the close edge above.
                if (!inlineEditOpen && _tableTypeMonitor != null
                    && _tableTypeMonitor.HasStalePendingViews(StalePendingEntityMs))
                {
                    Log("[PENDING-VIEW] stale pending view with no inline edit open - forcing commit validation (drag-create bypass guard).");
                    bool acquired = !_scopedCheckInProgress;
                    if (acquired) _scopedCheckInProgress = true;
                    try { _tableTypeMonitor.CommitPendingViews(); }
                    catch (Exception ex) { Log($"CommitPendingViews (stale) err: {ex.Message}"); }
                    finally { if (acquired) _scopedCheckInProgress = false; }
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
        /// Detects erwin's Model Editor dialog. Title shape (r10.10, English UI):
        ///   "Model 'MetaRepo' Editor"
        /// The leading <c>Model '</c> anchor keeps it distinct from the Column
        /// Editor (".. Column 'x' .. Editor"), the Entity Editor ("Table 'x'
        /// Editor"), erwin's main window ("erwin DM - ..") and the add-in's own
        /// warning popup (title "Model Validation", no quote). GetWindowTextNoHang
        /// can never block on a non-pumping thread.
        /// </summary>
        private bool IsModelEditorOpen()
        {
            bool found = false;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                string t = Win32Helper.GetWindowTextNoHang(hWnd);
                if (t.StartsWith("Model '", StringComparison.Ordinal)
                    && t.EndsWith("Editor", StringComparison.Ordinal))
                {
                    found = true;
                    return false; // stop enumeration
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Event-driven MODEL rename detection (2026-06-24). The model's naming
        /// rules (MODEL.Name regex/prefix/required, MODEL.Definition required) were
        /// only enforced when the "Model 'X' Editor" dialog closed
        /// (<see cref="ValidateModelOnEditorClose"/>). Renaming the model via the
        /// Model Explorer inline label edit never opens that dialog, so no model
        /// naming check fired - the exact analog of the column-add-via-Model-Explorer
        /// bug. This runs on the SAME inline-edit-close edge that commits entity /
        /// column / view renames (so it does NOT fire on a tab switch): if the root
        /// model object's Name changed since the baseline, run the model naming
        /// validation and re-baseline.
        /// </summary>
        private void ScanForModelRenameEventDriven(string source)
        {
            // Same entry guards as the sibling ScanForRenamesEventDriven.
            if (_sessionLost || _disposed || _validationSuspended) return;
            // ValidateModelOnEditorClose opens modal dialogs; skip if a naming
            // check (column or model) is already mid-flight to avoid re-entry.
            if (_columnNamingCheckInProgress) return;
            if (!IsModelStillOpen()) return;

            dynamic root;
            try { root = _session.ModelObjects?.Root; }
            catch (Exception ex) { Log($"ScanForModelRenameEventDriven: cannot read model root: {ex.Message}"); return; }
            if (root == null) return;

            string currentName;
            try { currentName = root.Name?.ToString() ?? ""; }
            catch (Exception ex) { Log($"ScanForModelRenameEventDriven: cannot read model name: {ex.Message}"); return; }

            // Lazy baseline backstop (StartMonitoring normally sets it at connect).
            if (_modelNameSnapshot == null)
            {
                _modelNameSnapshot = currentName;
                return;
            }
            if (string.Equals(currentName, _modelNameSnapshot, StringComparison.Ordinal))
                return;

            string oldName = _modelNameSnapshot;
            Log($"[ModelName] Model renamed ({source}): '{oldName}' -> '{currentName}' - running model naming validation");
            // Advance the baseline BEFORE validating so a reentrant tick during the
            // modal does not re-fire; refresh it AFTER to whatever the validator
            // left (a Required-field fill / regex re-prompt / "Revert Change" can
            // change the name). nameOnly: only the name changed, so do not also
            // nag about other model properties. nameRevertValue: on "Revert Change"
            // restore the pre-rename name instead of keeping the invalid one.
            _modelNameSnapshot = currentName;
            try { ValidateModelOnEditorClose(nameRevertValue: oldName, nameOnly: true); }
            catch (Exception ex) { Log($"ScanForModelRenameEventDriven: validation error: {ex.Message}"); }
            try { _modelNameSnapshot = root.Name?.ToString() ?? currentName; }
            catch { /* keep the tentative baseline */ }
        }

        /// <summary>
        /// Model-level naming ENFORCEMENT, fired when the Model Editor closes.
        /// Reads the model root object's rule-targeted properties (Name,
        /// Definition, ...) and, for every REQUIRED rule that is violated, opens
        /// the same RequiredFieldDialog the Column/Table paths use so the user can
        /// type a valid value, which is written back to the model (Definition =
        /// description, Name = model rename). A regex rule (e.g. rule#1027
        /// MODEL.Name ^[A-Z_]+$) re-prompts until the typed value clears it; Cancel
        /// leaves the model as-is. Guarded by _columnNamingCheckInProgress so the
        /// modal's message pump does not re-enter the monitor timers.
        ///
        /// Config FibaEmre_SQL MODEL rules (verified 2026-06-06): rule#1027 Name
        /// Regexp ^[A-Z_]+$ (req), rule#1028 Definition Required (req).
        /// </summary>
        /// <param name="nameRevertValue">When non-null and the user cancels
        /// ("Revert Change") a violation on the model NAME, the name is written
        /// back to this value. Both triggers supply it: the inline-rename scan
        /// passes the pre-rename name; the Model-Editor-close path passes the name
        /// captured when the editor opened. Pass null to keep the legacy "leave
        /// as-is" on cancel.</param>
        /// <param name="nameOnly">When true, only the model Name is validated
        /// (used by the inline-rename scan: only the name changed, so other model
        /// properties like a Required Definition are not dragged in).</param>
        private void ValidateModelOnEditorClose(string nameRevertValue = null, bool nameOnly = false)
        {
            if (_validationSuspended || !NamingStandardService.Instance.IsLoaded) return;
            if (_columnNamingCheckInProgress) return; // a naming required-popup is already up
            if (!IsModelStillOpen()) return;

            dynamic root;
            try { root = _session.ModelObjects.Root; }
            catch (Exception ex) { Log($"ValidateModelOnEditorClose: cannot read model root: {ex.Message}"); return; }
            if (root == null) return;
            object rootBoxed = root;

            // Read a model property by the rule's accessor. The model NAME lives on
            // the root object's Name accessor, not a property-collection member;
            // everything else (Definition, ...) is a direct SCAPI property read. An
            // unsurfaced property is treated as empty so a Required rule still fires.
            string ReadVal(string code)
            {
                if (string.Equals(code, "Name", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                {
                    try { return root.Name?.ToString() ?? ""; } catch { return ""; }
                }
                try { return root.Properties(code)?.Value?.ToString() ?? ""; }
                catch (Exception ex)
                {
                    Log($"Naming standard: SCAPI did not surface 'Model.{code}' (treating as empty): {ex.Message}");
                    return "";
                }
            }

            _columnNamingCheckInProgress = true;
            try
            {
                var requiredProps = NamingStandardService.Instance.GetRequiredPropertyCodes("Model");
                string modelName;
                try { modelName = root.Name?.ToString() ?? ""; } catch { modelName = ""; }

                foreach (var code in NamingStandardService.Instance.GetPropertyCodes("Model"))
                {
                    bool isNameCode = string.Equals(code, "Name", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(code, "Physical_Name", StringComparison.OrdinalIgnoreCase);
                    // nameOnly (inline-rename scan): only the model name changed, so
                    // do not drag in other model properties (e.g. a Required Definition).
                    if (nameOnly && !isNameCode) continue;

                    string value = ReadVal(code);
                    var res = NamingValidationEngine.ValidateObjectName("Model", value, rootBoxed, code, isNew: false);
                    var fail = res?.FirstOrDefault(r => !r.IsValid);
                    if (fail == null) continue;

                    Log($"NamingValidate: 'Model.{code}' on '{modelName}' liveValue='{value}' -> rule#{fail.Rule?.Id} ({fail.RuleName})");

                    bool isRequired = string.Equals(fail.RuleName, "Required", StringComparison.Ordinal)
                                      || (requiredProps != null && requiredProps.Contains(code));
                    if (!isRequired)
                    {
                        // No current MODEL rule is non-required, but stay honest: a
                        // non-required violation is a warning, not a forced input.
                        AddinMessageDialog.Show(fail.ErrorMessage, "Model Validation",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }

                    // Show the OBJECT NAME (the model) + a friendly property label,
                    // e.g. "Fiba_SQLEmred (Comment)" instead of raw "Model.Definition".
                    string fieldLabel = $"{modelName} ({NamingValidationEngine.FriendlyPropertyLabel(code)})";
                    string writeAccessor = NamingValidationEngine.WriteAccessorFor(code);

                    var currentFail = fail;
                    while (currentFail != null)
                    {
                        string seed = ReadVal(code);
                        var rc = EliteSoft.Erwin.AddIn.Forms.RequiredFieldDialog.Show(
                            title: "Required field",
                            message: currentFail.ErrorMessage,
                            fieldLabel: fieldLabel,
                            out string typed,
                            owner: null,
                            initialValue: seed,
                            mode: Forms.RequiredOperationMode.Update,
                            objectKind: "Model");

                        if (rc != DialogResult.OK || string.IsNullOrEmpty(typed))
                        {
                            // "Revert Change" (Update mode) on the model NAME: restore
                            // the pre-rename name instead of leaving the rule-violating
                            // name the user just typed. Both triggers supply a revert
                            // value (inline scan = pre-rename name; editor-close = the
                            // name captured when the editor opened).
                            if (isNameCode && !string.IsNullOrEmpty(nameRevertValue))
                            {
                                int rTx = _session.BeginNamedTransaction("RevertModelName");
                                try
                                {
                                    try { root.Properties(writeAccessor).Value = nameRevertValue; }
                                    catch { root.Name = nameRevertValue; }
                                    _session.CommitTransaction(rTx);
                                    Log($"Model name reverted: {fieldLabel} -> '{nameRevertValue}'");
                                }
                                catch (Exception ex)
                                {
                                    try { _session.RollbackTransaction(rTx); } catch (Exception rbEx) { Log($"RevertModelName rollback err: {rbEx.Message}"); }
                                    Log($"Model name revert failed for {fieldLabel}: {ex.Message}");
                                }
                            }
                            else
                            {
                                Log($"Model required field cancelled: {fieldLabel} (left as-is)");
                            }
                            // 2026-05-24 rule (force valid Required values, now applied
                            // to MODEL too): if the reverted / left-as-is value STILL
                            // violates, re-prompt the SAME property - the user cannot
                            // escape a Required violation by clicking Revert. Only when
                            // the value is now valid do we STOP the whole chain (so a
                            // valid Name revert does not then pop the Definition dialog).
                            string afterRevert = ReadVal(code);
                            List<NamingValidationResult> revertRes;
                            // Safeguard (mirror of RevalidatePropertyAfterRevert): a
                            // re-validation fault treats the value as valid so it can
                            // never trap the user in the re-prompt loop.
                            try { revertRes = NamingValidationEngine.ValidateObjectName("Model", afterRevert, rootBoxed, code, isNew: false); }
                            catch (Exception rvEx) { Log($"Model post-revert re-validation error for {fieldLabel}, treating as valid: {rvEx.Message}"); revertRes = null; }
                            var revertStillFail = revertRes?.FirstOrDefault(r => !r.IsValid);
                            if (revertStillFail != null)
                            {
                                Log($"Model required re-prompt after Cancel (post-revert still invalid): {fieldLabel}");
                                currentFail = revertStillFail;
                                continue;
                            }
                            return;
                        }

                        int transId = _session.BeginNamedTransaction("RequiredModelField");
                        try
                        {
                            if (isNameCode)
                            {
                                // Model name: try the property-collection accessor,
                                // fall back to the root's direct Name setter.
                                try { root.Properties(writeAccessor).Value = typed; }
                                catch { root.Name = typed; }
                            }
                            else
                            {
                                root.Properties(writeAccessor).Value = typed;
                            }
                            _session.CommitTransaction(transId);
                            Log($"Model required field filled: {fieldLabel} = '{typed}'");
                        }
                        catch (Exception ex)
                        {
                            try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"RequiredModelField rollback err: {rbEx.Message}"); }
                            Log($"Model required field write failed for {fieldLabel}: {ex.Message}");
                            AddinMessageDialog.Show(
                                $"'{typed}' degeri {fieldLabel} alanina yazilamadi.\n\nSCAPI hata:\n{ex.Message}",
                                "Model alani yazilamadi",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            break;
                        }

                        // Re-validate: a value can satisfy "required" yet still fail a
                        // regex (the Name must also match ^[A-Z_]+$). Re-prompt until
                        // the property clears all its rules or the user cancels.
                        string after = ReadVal(code);
                        var freshRes = NamingValidationEngine.ValidateObjectName("Model", after, rootBoxed, code, isNew: false);
                        currentFail = freshRes?.FirstOrDefault(r => !r.IsValid);
                    }
                }
            }
            finally { _columnNamingCheckInProgress = false; }
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
                        _pendingEntityAddedAt.Remove(entityId);
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
                                    bool attrDiscarded = ProcessAttributeChanges(attr, previousState, snapshot, predefined);
                                    if (!attrDiscarded)
                                    {
                                        // A brand-new column just got its real name. ProcessAttributeChanges
                                        // only checks the datatype whitelist on a type CHANGE with no name
                                        // change, so a new column left at erwin's default type (e.g. char(18))
                                        // slips through here (name changed, type did not). Enforce the whitelist
                                        // now against the committed type so a disallowed default is caught at
                                        // creation. prev=null -> forces an allowed type when the default is
                                        // disallowed. (New-column path only - existing columns are never
                                        // retroactively re-typed; see feedback_rules_new_objects_only.)
                                        EnforceAllowedDatatypeWhitelist(attr, null, snapshot, isNew: true);
                                        _attributeSnapshots[aid] = snapshot;
                                        // erwin's auto-uniquify may rename this just-committed
                                        // column on a DELAYED transaction after the drain, with
                                        // nothing else watching - schedule the targeted recheck
                                        // (2026-07-09, 'Pre_Abc__1070').
                                        ScheduleAttributeRecheck(snapshot);
                                    }
                                    else
                                    {
                                        // User discarded the pending-new column's mandatory field: the
                                        // column was deleted inside ProcessAttributeChanges. Drop the stale
                                        // snapshot it left and skip the whitelist on the dead COM object.
                                        _attributeSnapshots.Remove(aid);
                                        Log($"[REQUIRED-DISCARD] entity='{tableName}' attr id={aid} discarded via Required-field Cancel - removed");
                                    }
                                }
                                else
                                {
                                    // No baseline (race?). Run new-attribute
                                    // validation to be safe; ProcessNewAttribute
                                    // handles the glossary vs domain branch.
                                    Log($"[PENDING-NAME] entity='{tableName}' attr id={aid} renamed to '{currentName}' (no prior snapshot) - running new-attr validation");
                                    _attributeSnapshots[aid] = snapshot;
                                    ProcessNewAttribute(attr, snapshot, predefined);
                                    ScheduleAttributeRecheck(snapshot); // late auto-uniquify safety net (2026-07-09)
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
        private void RunScopedTableNamingCheck(string tableName, IDictionary<string, string> baselineOverride = null, bool isNew = false, bool revalidateAsNew = false)
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
                if (_pendingTableNamingChecks.Add((tableName, isNew, revalidateAsNew)))
                    Log($"RunScopedTableNamingCheck: deferring '{tableName}' (isNew={isNew}, revalidate={revalidateAsNew}) - check already in progress");
                return;
            }
            _scopedCheckInProgress = true;
            try { RunScopedTableNamingCheckCore(tableName, baselineOverride, isNew, revalidateAsNew); }
            finally { _scopedCheckInProgress = false; }
        }

        private void RunScopedTableNamingCheckCore(string tableName, IDictionary<string, string> baselineOverride = null, bool isNew = false, bool revalidateAsNew = false)
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

                    // Read the id BEFORE ValidateNamingStandard, while the entity is
                    // guaranteed alive: a Required-popup Cancel inside the call can
                    // DELETE the entity, and a dead COM proxy may throw on ObjectId -
                    // which would skip the disarm below and leak an armed cascade id
                    // for the rest of the connect (adversarial review 2026-07-02).
                    string cascadeId = null;
                    try { cascadeId = entity.ObjectId?.ToString(); } catch { /* keep null */ }

                    // Authoritative Create-context upgrade by OBJECT ID: the name-based
                    // wrapper probe is first-match-wins and can hit a same-named sibling;
                    // this id-based check binds the cascade to the exact entity we are
                    // about to validate.
                    if (!isNew && !string.IsNullOrEmpty(cascadeId)
                        && (_creationGestureEntityIds.Contains(cascadeId) || _creationCascadeEntityIds.Contains(cascadeId)))
                    {
                        Log($"Scoped naming check: '{nameForMatch}' is in the creation gesture/cascade (id match) - upgrading isNew to True");
                        isNew = true;
                    }

                    Log($"Scoped naming check on '{nameForMatch}' (isNew={isNew}, revalidate={revalidateAsNew})");
                    _tableTypeMonitor.ValidateNamingStandard("Table", nameForMatch, entity, baselineOverride: baselineOverride, isNew: isNew, revalidateAsNew: revalidateAsNew);

                    // Creation-cascade continuation: if THIS Create-context check just
                    // renamed the entity (a Prefix/Suffix rule fired), the rename will
                    // re-trigger the scoped check on a later tick - and that follow-up
                    // must STILL see Create (see _creationCascadeEntityIds doc). Arm the
                    // cascade on rename; disarm at the first stable (no-rename) check -
                    // the fixed point where the chain is over and later edits are
                    // genuine updates. A deleted entity (GetTableName empty/throw) also
                    // disarms, so a cancelled new table cannot leak an armed id.
                    // Live name AFTER validation, read once for the cascade decision
                    // AND the discard gate below. Empty = the entity no longer exists
                    // (a live entity always carries at least a placeholder name).
                    string liveAfter = null;
                    try { liveAfter = GetTableName(entity); }
                    catch { /* deleted during the check (required-cancel) */ }

                    if (isNew && !string.IsNullOrEmpty(cascadeId))
                    {
                        bool renamedDuringCheck = !string.IsNullOrEmpty(liveAfter)
                            && !string.Equals(liveAfter, nameForMatch, StringComparison.Ordinal);
                        if (renamedDuringCheck)
                        {
                            if (_creationCascadeEntityIds.Add(cascadeId))
                                Log($"Creation cascade armed: '{nameForMatch}' -> '{liveAfter}' renamed during Create-context check; follow-up checks stay isNew=true");
                        }
                        else if (_creationCascadeEntityIds.Remove(cascadeId))
                        {
                            Log($"Creation cascade complete for '{nameForMatch}' - name stable, later checks run as Update");
                        }
                    }

                    // Discard gate: a Required prompt inside ValidateNamingStandard can
                    // DELETE the entity (user clicked Discard). Every pending warning
                    // for it must be cancelled - a dead proxy's Key_Group collect can
                    // return EMPTY instead of throwing, which would surface a bogus
                    // "PK required" popup for a table that no longer exists.
                    if (string.IsNullOrEmpty(liveAfter))
                    {
                        Log($"Scoped naming check: '{nameForMatch}' no longer exists after validation (discarded) - skipping remaining checks");
                        return;
                    }

                    // PRIMARY KEY existence is per-table (not the model-wide
                    // CheckRequiredObjectTypesExist): warn if THIS table owns no PK.
                    // Same scoped trigger + isNew as the name check, so it honours
                    // APPLY_ON and fires on editor-close / Model-Explorer name-commit.
                    _tableTypeMonitor.CheckTablePrimaryKeyRequired(entity, modelObjects, nameForMatch, isNew);
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

        // Last entity name checked by the selection-scoped fingerprint, so a stable selection
        // does not log on every heartbeat (the fingerprint itself is idempotent).
        private string _lastSelectionScopedEntity;
        // Cached handle of the Overview-pane selection Static, so each heartbeat re-reads ONE
        // window instead of enumerating every child (re-found only when the cached read fails).
        private IntPtr _overviewSelectionStatic = IntPtr.Zero;
        // Backoff so that when the Static cannot be found (e.g. the Overview pane is closed) we do
        // NOT run a full child-window enumeration on every heartbeat. Counts heartbeats to skip.
        private int _selectionStaticRetryTicks;
        private const int SelectionStaticRetryHeartbeats = 10; // ~10 s between re-find attempts

        // Round-robin cursor over the baselined working set (see SelectionScopedAttributeCheck).
        private int _rollingRescanCursor;
        // Baselined entities re-fingerprinted per heartbeat IN ADDITION to the selected one. Bounds
        // the cost while guaranteeing full working-set coverage every ceil(workingSet/N) heartbeats.
        private const int RollingRescanPerHeartbeat = 3;

        /// <summary>
        /// Pane-edit fingerprint (2026-07-10, hardened after a live "sometimes escapes" report).
        /// An EXISTING column's Physical_Data_Type or name change made purely via the Properties-pane
        /// dropdown leaves no Win32 "Edit" focus, so the inline-edit candidate path never sees it.
        /// This catches it by re-running the fingerprint diff (<see cref="CheckEntityForChanges"/>) on
        /// a BOUNDED target set each heartbeat: the currently-selected entity (Overview pane, fast
        /// path for the common case, ~1 s) PLUS a small round-robin slice of the already-baselined
        /// working set. The round-robin is the safety net: the Overview does not reliably reflect a
        /// Model-Explorer column selection (verified from the log - it showed a different entity than
        /// the one being edited), so relying on selection alone missed edits; rotating through the
        /// touched entities re-checks every one within a few seconds regardless. NOT a full model
        /// walk - the candidate set is only entities we have already snapshotted (hard rule). A
        /// stable entity short-circuits in the fingerprint fast path, so a rescan of an untouched
        /// entity produces NO popup - only genuine unobserved drift fires. Runs only when the Column
        /// Editor is closed (the open case is covered by the 250 ms scoped path).
        /// </summary>
        private void SelectionScopedAttributeCheck(dynamic modelObjects, dynamic root)
        {
            if (!string.IsNullOrEmpty(_activeColumnEditorTable)) return; // covered by the scoped path

            // Build the bounded target set: selected entity (if resolvable) + round-robin slice.
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string selectedName = TryReadSelectedEntityName(root);
            if (!string.IsNullOrEmpty(selectedName)) targets.Add(selectedName);

            if (_tablesBaselined.Count > 0)
            {
                var working = new List<string>(_tablesBaselined);
                int take = Math.Min(RollingRescanPerHeartbeat, working.Count);
                for (int i = 0; i < take; i++)
                    targets.Add(working[(_rollingRescanCursor + i) % working.Count]);
                _rollingRescanCursor = (_rollingRescanCursor + take) % working.Count;
            }

            if (targets.Count == 0) return;

            dynamic entities = null;
            try
            {
                entities = modelObjects.Collect(root, "Entity");
                if (entities == null) return;
                foreach (dynamic entity in entities)
                {
                    if (targets.Count == 0) break;
                    if (entity == null) continue;
                    string nameForMatch;
                    try
                    {
                        string p = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        nameForMatch = (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) ? p : (entity.Name ?? "");
                    }
                    catch { try { nameForMatch = entity.Name ?? ""; } catch { continue; } }

                    // Match a target either by exact working-set name or by the selection title.
                    bool isTarget = targets.Remove(nameForMatch);
                    if (!isTarget && !string.IsNullOrEmpty(selectedName)
                        && EntityNameMatchesTitle(nameForMatch, selectedName))
                    {
                        isTarget = true;
                        targets.Remove(selectedName);
                    }
                    if (!isTarget) continue;

                    if (!_tablesBaselined.Contains(nameForMatch))
                    {
                        SilentPopulateEntity(entity, modelObjects, nameForMatch);
                        _tablesBaselined.Add(nameForMatch);
                        if (!string.Equals(_lastSelectionScopedEntity, nameForMatch, StringComparison.Ordinal))
                        {
                            _lastSelectionScopedEntity = nameForMatch;
                            Log($"[SEL-SCOPE] baselined entity '{nameForMatch}' (count={_attributeSnapshots.Count})");
                        }
                        continue;
                    }

                    _pendingResults.Clear();
                    CheckEntityForChanges(entity, modelObjects);
                    if (_pendingResults.Count > 0) ShowConsolidatedPopup();
                }
            }
            catch (Exception ex) { Log($"[SEL-SCOPE] rescan failed: {ex.Message}"); }
            finally { ReleaseCom(entities); }
        }

        /// <summary>
        /// Reads the single selected entity name from erwin's Overview pane (cached Static handle +
        /// backoff so a closed Overview never triggers a per-heartbeat child-window enumeration).
        /// Returns null when nothing / a multi-select / an unresolvable selection is shown.
        /// </summary>
        private string TryReadSelectedEntityName(dynamic root)
        {
            string modelName;
            try { modelName = root?.Name?.ToString() ?? _lastKnownModelName; }
            catch { modelName = _lastKnownModelName; }
            if (string.IsNullOrEmpty(modelName)) return null;

            try
            {
                IntPtr hwnd = Win32Helper.GetErwinMainWindow();
                if (hwnd == IntPtr.Zero) return null;

                // Re-read the cached Overview Static (one cheap WM_GETTEXT). When nothing is
                // selected the Static still shows just "MODELNAME" (model-prefixed, no parens), so
                // the common cases - stable selection AND nothing selected - never re-enumerate.
                string selText = Win32Helper.IsWindowValid(_overviewSelectionStatic)
                    ? Win32Helper.GetWindowTextNoHang(_overviewSelectionStatic)
                    : null;
                bool cachedUsable = !string.IsNullOrEmpty(selText)
                    && selText.TrimStart().StartsWith(modelName, StringComparison.OrdinalIgnoreCase);

                if (!cachedUsable)
                {
                    if (_selectionStaticRetryTicks > 0) { _selectionStaticRetryTicks--; return null; }
                    _overviewSelectionStatic = Win32Helper.FindDiagramSelectionStatic(hwnd, modelName);
                    if (!Win32Helper.IsWindowValid(_overviewSelectionStatic))
                    {
                        _selectionStaticRetryTicks = SelectionStaticRetryHeartbeats;
                        return null;
                    }
                    selText = Win32Helper.GetWindowTextNoHang(_overviewSelectionStatic);
                }
                return Win32Helper.ParseSelectedEntityFromOverviewText(selText);
            }
            catch (Exception ex) { Log($"[SEL-SCOPE] selection read failed: {ex.Message}"); return null; }
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
                                    EnforceLockedGlossaryFields(attr, objectId, existingSnap.PhysicalName);

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

                        // Template value-generation rules fire on the column's
                        // "create moment" (a new column committed with a real
                        // name, directly or via placeholder -> real rename) and
                        // on update, gated per rule by APPLY_ON. Capture the
                        // intent inside the branches below, then apply once after
                        // the if/else (placeholder columns never fire - they wait
                        // for their real name).
                        bool fireTemplate = false;
                        bool templateTreatAsNew = false;

                        if (isNew)
                        {
                            _attributeSnapshots[objectId] = currentState;
                            // Phase-2D: per-table silent populate happens BEFORE the first
                            // CheckEntityForChanges call for a given table (see scoped path
                            // in MonitorTimer_Tick). So when we reach this branch with isNew
                            // true, the attribute genuinely DID NOT exist at baseline time -
                            // it's user-added during the active edit session. Validate it.
                            ProcessNewAttribute(attr, currentState, predefinedColumnNames);

                            // A brand-new column that already carries a real name
                            // is a create moment for Template rules (no placeholder
                            // phase). Placeholder columns fire later, on their
                            // rename commit in the else branch.
                            if (!IsPlaceholderColumnName(currentState.PhysicalName))
                            {
                                fireTemplate = true;
                                templateTreatAsNew = true;
                            }

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

                            bool attrDiscarded = ProcessAttributeChanges(attr, previousState, currentState, predefinedColumnNames);
                            if (attrDiscarded)
                            {
                                // The user discarded a pending-new column's mandatory field
                                // (Model Explorer add -> placeholder -> real name -> Required
                                // Cancel): the column was deleted inside ProcessAttributeChanges
                                // and no longer exists. Drop our tracking and skip every
                                // remaining per-attribute step, all of which touch the dead COM
                                // object.
                                _attributeSnapshots.Remove(objectId);
                                string discardOwnerId = null;
                                try { discardOwnerId = entity.ObjectId?.ToString(); } catch { /* best effort */ }
                                if (!string.IsNullOrEmpty(discardOwnerId) && _pendingNamedAttrs.TryGetValue(discardOwnerId, out var discardPend))
                                {
                                    if (discardPend.Remove(objectId) && discardPend.Count == 0)
                                        _pendingNamedAttrs.Remove(discardOwnerId);
                                }
                                _pendingAttrAddedAt.Remove(objectId);
                                Log($"[REQUIRED-DISCARD] entity='{tableName}' attr id={objectId} discarded via Required-field Cancel - removed, skipping post-work");
                                continue;
                            }

                            // New column committed via the heartbeat rename branch (placeholder ->
                            // real name): ProcessAttributeChanges skips the datatype whitelist on a
                            // name change, so a new column left at erwin's default type slips
                            // through. Enforce it now (dedup makes this a no-op if the inline-edit
                            // close path already handled the same column). New-column only.
                            if (IsPlaceholderColumnName(previousState.PhysicalName) && !IsPlaceholderColumnName(currentState.PhysicalName))
                                EnforceAllowedDatatypeWhitelist(attr, null, currentState, isNew: true);

                            _attributeSnapshots[objectId] = currentState;
                            ScheduleAttributeRecheck(currentState); // late auto-uniquify safety net (2026-07-09)

                            // Template create/update moment: fire for any column
                            // that now has a real name. treatAsNew is true only
                            // when this tick committed the name (placeholder ->
                            // real); otherwise it is an update.
                            if (!IsPlaceholderColumnName(currentState.PhysicalName))
                            {
                                fireTemplate = true;
                                templateTreatAsNew = IsPlaceholderColumnName(previousState.PhysicalName);
                            }

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
                        EnforceLockedGlossaryFields(attr, objectId, currentState.PhysicalName);

                        // Check column-level UDP changes for dependency cascade
                        CheckAttributeUdpDependencies(attr, objectId, currentState.PhysicalName);

                        // Template value-generation (writes a target property such
                        // as Definition, NOT the column name). Runs last so the
                        // column's name and type are settled. Gated per rule by
                        // APPLY_ON + DEPENDS_ON; no-fallback render inside.
                        if (fireTemplate)
                            ApplyColumnTemplateRules(entity, attr, objectId, currentState.PhysicalName, templateTreatAsNew);
                    }
                }
                finally { ReleaseCom(entityAttrs); }

                // Check Key_Group (Index) naming for this entity's indexes
                CheckEntityKeyGroups(entity, modelObjects, tableName);

                // PRIMARY KEY governance object type (2026-06-26): apply Template
                // naming rules to this entity's PK constraint (the Key_Group with
                // Key_Group_Type == "PK"). Cheap early-out inside when no PK
                // Template rules exist.
                ApplyPrimaryKeyRules(entity, modelObjects, tableName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckEntityForChanges error: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies active Template naming rules for a column: renders the target
        /// property value from the rule's template and writes it via SCAPI.
        /// Gated per rule by APPLY_ON (<paramref name="treatAsNew"/>) and
        /// DEPENDS_ON, reusing the exact predicates from the validate-only path.
        /// NO-FALLBACK: any token that cannot resolve aborts that rule's write
        /// and is logged with the rule's ERROR_MESSAGE - a half-rendered value
        /// never reaches the model. Each rule writes in its own named
        /// transaction; a write failure is logged and rolled back, not swallowed.
        /// </summary>
        /// <param name="entity">Owning Entity (parent table), in scope so a
        /// <c>{Table.*}</c> token resolves without a reverse lookup.</param>
        /// <param name="attr">The column (Attribute) SCAPI object.</param>
        /// <param name="objectId">The column's stable ObjectId (diagnostics).</param>
        /// <param name="columnName">The column's physical name (log/prompt).</param>
        /// <param name="treatAsNew">True at the create moment, false on update;
        /// fed to APPLY_ON gating.</param>
        private void ApplyColumnTemplateRules(dynamic entity, dynamic attr, string objectId, string columnName, bool treatAsNew)
        {
            IReadOnlyList<NamingStandardRule> rules;
            try
            {
                rules = NamingStandardService.Instance.GetTemplateRules("Column");
            }
            catch (Exception ex)
            {
                Log($"[TEMPLATE-ERROR] column='{columnName}': loading Template rules failed: {ex.Message}");
                return;
            }
            if (rules == null || rules.Count == 0) return;

            // PK membership is not a readable Attribute property (erwin keeps it in
            // the Key_Group graph), so a rule conditioned on "column is PK" is
            // resolved here via the Key_Group_Member walk. Computed lazily and once
            // per column - only when a rule actually asks for it.
            bool? pkMembership = null;

            foreach (var rule in rules)
            {
                try
                {
                    // Same APPLY_ON (Create/Update/Both) and DEPENDS_ON gating as
                    // the validate-only path.
                    if (!NamingValidationEngine.MatchesApplyOn(rule, treatAsNew)) continue;

                    if (pkMembership == null && NamingValidationEngine.IsPkMembershipCondition(rule))
                        pkMembership = IsAttributePrimaryKeyMember(entity, objectId);

                    if (!NamingValidationEngine.IsRuleApplicable(rule, "Column", attr, pkMembership))
                    {
                        // Don't skip silently: a conditional Template that never fires
                        // is the #1 "why didn't my rule apply?" question. Log the live
                        // condition value (runs per column-change, so it is bounded).
                        LogDebug($"[TEMPLATE-COND] column='{columnName}' rule#{rule.Id} not applied: {NamingValidationEngine.DescribeApplicability(rule, "Column", attr, pkMembership)}");
                        continue;
                    }

                    string targetCode = rule.PropertyCode;

                    // Self-referential template guard (see ApplyPrimaryKeyRules): a
                    // template that reads its own target property would grow without
                    // bound under FILL_MODE=Always. Refuse it - a related token like
                    // {Table.Physical_Name} is the correct way to seed the value.
                    if (NamingTemplateEngine.ReferencesOwnProperty(rule.ValueTemplate, targetCode))
                    {
                        Log($"[TEMPLATE-SKIP] column='{columnName}' rule#{rule.Id}: template '{rule.ValueTemplate}' references its own target property '{targetCode}' (self-referential - would loop); skipping. Use a related token like {{Table.Physical_Name}} instead.");
                        continue;
                    }

                    string rendered;
                    try
                    {
                        rendered = NamingTemplateEngine.Render(
                            rule.ValueTemplate,
                            ownCode => ReadScapiProperty(attr, ownCode),
                            (alias, code) => ResolveColumnRelatedProperty(entity, alias, code));
                    }
                    catch (TemplateResolutionException tex)
                    {
                        // No-fallback: token unresolved (own/related property
                        // absent or empty, unknown alias, unsupported navigation).
                        // Surface the rule's ERROR_MESSAGE (or a default) and skip
                        // without writing.
                        string msg = !string.IsNullOrWhiteSpace(rule.ErrorMessage)
                            ? rule.ErrorMessage
                            : $"could not resolve token '{{{tex.Token}}}'";
                        Log($"[TEMPLATE-SKIP] column='{columnName}' rule#{rule.Id} target='{targetCode}': {msg}");
                        continue;
                    }

                    // FILL_MODE: Always overwrites; OnlyIfEmpty keeps a human value.
                    string currentVal = ReadScapiProperty(attr, targetCode);
                    bool shouldWrite = NamingTemplateEngine.ShouldWrite(rule.TemplateFillMode, currentVal, out bool unknownMode);
                    if (unknownMode)
                    {
                        Log($"[TEMPLATE-SKIP] column='{columnName}' rule#{rule.Id}: unknown TEMPLATE_FILL_MODE '{rule.TemplateFillMode}'");
                        continue;
                    }
                    if (!shouldWrite) continue;

                    // Idempotent: never dirty the model with a no-op write.
                    if (string.Equals(currentVal, rendered, StringComparison.Ordinal)) continue;

                    // AUTO_APPLY=false -> confirm (mirrors the naming Yes/No
                    // prompt); =true -> silent. UI text is English; the admin
                    // ERROR_MESSAGE (which may be Turkish) goes only to the log.
                    if (!rule.AutoApply)
                    {
                        var answer = AddinMessageDialog.Show(
                            $"Apply naming template to column '{columnName}'?\n\nSet {targetCode} to:\n{rendered}",
                            "Apply Naming Template",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                        if (answer != DialogResult.Yes) continue;
                    }

                    int transId = _session.BeginNamedTransaction("ApplyColumnTemplate");
                    try
                    {
                        attr.Properties(targetCode).Value = rendered;
                        _session.CommitTransaction(transId);
                        Log($"[TEMPLATE-APPLY] column='{columnName}' rule#{rule.Id} {targetCode}='{rendered}'");
                    }
                    catch (Exception wex)
                    {
                        try { _session.RollbackTransaction(transId); }
                        catch (Exception rex) { Log($"[TEMPLATE-ERROR] column='{columnName}' rule#{rule.Id}: rollback failed: {rex.Message}"); }
                        Log($"[TEMPLATE-ERROR] column='{columnName}' rule#{rule.Id}: writing '{targetCode}' failed: {wex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    // Unknown-alias / DB / unexpected errors from rendering or
                    // condition evaluation. Surface, never swallow; move on.
                    Log($"[TEMPLATE-ERROR] column='{columnName}' rule#{rule.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// True when the attribute (by ObjectId) is a member of its entity's
        /// primary key. erwin exposes NO readable PK-membership property on the
        /// Attribute, so this walks the entity's Key_Group of type "PK" and its
        /// Key_Group_Member rows, matching <c>Attribute_Ref</c> (mirrors
        /// <see cref="TableTypeMonitorService.IsAttributeInPrimaryKey"/>). Used to
        /// resolve a "column is PK" DEPENDS_ON condition for naming Template rules.
        /// </summary>
        private bool IsAttributePrimaryKeyMember(dynamic entity, string attrObjectId)
        {
            if (entity == null || string.IsNullOrEmpty(attrObjectId)) return false;
            bool pkKgFound = false;
            bool matched = false;
            var memberRefs = new List<string>();
            var kgTypesSeen = new List<string>();
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic groups = modelObjects.Collect(entity, "Key_Group");
                if (groups != null)
                {
                    foreach (dynamic kg in groups)
                    {
                        if (kg == null) continue;
                        string kgType;
                        try { kgType = kg.Properties("Key_Group_Type").Value?.ToString(); }
                        catch { continue; }
                        kgTypesSeen.Add(kgType ?? "<null>");
                        if (!string.Equals(kgType, "PK", StringComparison.OrdinalIgnoreCase)) continue;

                        pkKgFound = true;
                        dynamic members = modelObjects.Collect(kg, "Key_Group_Member");
                        if (members != null)
                        {
                            foreach (dynamic m in members)
                            {
                                string memberRef;
                                try { memberRef = m.Properties("Attribute_Ref").Value?.ToString(); }
                                catch { continue; }
                                memberRefs.Add(memberRef ?? "<null>");
                                if (string.Equals(memberRef, attrObjectId, StringComparison.OrdinalIgnoreCase))
                                    matched = true;
                            }
                        }
                        break; // exactly one PK Key_Group per entity
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"IsAttributePrimaryKeyMember err for '{attrObjectId}': {ex.Message}");
            }
            // Diagnostic (per column-change, bounded): shows WHY membership is false -
            // no PK key-group, empty members, or Attribute_Ref vs ObjectId mismatch.
            LogDebug($"[PK-WALK] attr='{attrObjectId}' pkKgFound={pkKgFound} matched={matched} kgTypes=[{string.Join(",", kgTypesSeen)}] pkMembers=[{string.Join(",", memberRefs)}]");
            return matched;
        }

        /// <summary>
        /// Reads a built-in property value off a SCAPI object by PROPERTY_CODE.
        /// Returns "" when the property is unset (erwin throws on sparse storage)
        /// or absent; the Template renderer treats "" as an unresolved token
        /// (no-fallback) and the FILL_MODE check treats "" as empty.
        /// </summary>
        private static string ReadScapiProperty(dynamic obj, string propertyCode)
        {
            if (obj == null || string.IsNullOrEmpty(propertyCode)) return "";
            try
            {
                return obj.Properties(propertyCode)?.Value?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                // Unset/absent property is normal (sparse storage); record at
                // debug level only so OnlyIfEmpty reads do not spam the log.
                System.Diagnostics.Debug.WriteLine($"ReadScapiProperty('{propertyCode}'): {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Resolves a <c>{Alias.PropertyCode}</c> token for a COLUMN: maps the
        /// alias through the global <c>MC_OBJECT_RELATION</c> catalog, then
        /// navigates to the related object and reads the property. v1 supports
        /// COLUMN -> TABLE (the alias's TO type is "TABLE"), where the related
        /// object is the parent entity already in scope. An unknown alias or an
        /// unsupported navigation is a hard error (no write), never a silent skip.
        /// </summary>
        private string ResolveColumnRelatedProperty(dynamic entity, string alias, string propertyCode)
        {
            // ResolveAlias may load the catalog; a DB error propagates and is
            // surfaced as a DB error by the caller's per-rule catch (not masked
            // as "unknown alias").
            string toType = ObjectRelationCatalog.Instance.ResolveAlias("Column", alias);
            if (string.IsNullOrEmpty(toType))
            {
                throw new TemplateResolutionException(
                    $"{alias}.{propertyCode}",
                    $"alias '{alias}' is not defined in MC_OBJECT_RELATION for object type 'Column'");
            }

            if (string.Equals(toType, "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                // Parent table is the owning entity, already in scope - no reverse walk.
                return ReadScapiProperty(entity, propertyCode);
            }

            throw new TemplateResolutionException(
                $"{alias}.{propertyCode}",
                $"alias '{alias}' navigates Column -> {toType}, which has no runtime navigation in this version");
        }

        /// <summary>
        /// Applies active Template naming rules for the "PRIMARY KEY" governance
        /// object type to an entity's primary-key constraint. PRIMARY KEY maps to
        /// erwin's <c>Key_Group</c> class filtered to <c>Key_Group_Type == "PK"</c>
        /// (INDEX/AK key groups share the class, so the type filter is mandatory -
        /// there is exactly one PK Key_Group per entity). Renders and writes the
        /// rule's configured target property (<c>rule.PropertyCode</c>, set by the
        /// admin - e.g. <c>PK_{Table.Name}</c>) via SCAPI. The applier is generic
        /// about the property code; whether a given code (e.g. <c>Physical_Name</c>
        /// vs <c>Name</c>) is writable on a Key_Group is an erwin-metamodel fact the
        /// admin rule must get right - a non-writable code throws and is suppressed
        /// (logged once per session). Same APPLY_ON / DEPENDS_ON gating, FILL_MODE,
        /// idempotency and NO-FALLBACK contract as <see cref="ApplyColumnTemplateRules"/>.
        /// </summary>
        /// <param name="entity">Owning Entity (the parent table) - in scope so a
        /// <c>{Table.*}</c> token resolves without a reverse lookup.</param>
        /// <param name="modelObjects">SCAPI ModelObjects for the Key_Group collect.</param>
        /// <param name="tableName">The entity's name (log/prompt).</param>
        private void ApplyPrimaryKeyRules(dynamic entity, dynamic modelObjects, string tableName)
        {
            IReadOnlyList<NamingStandardRule> templateRules;
            IReadOnlyList<string> pkPropertyCodes;
            try
            {
                templateRules = NamingStandardService.Instance.GetTemplateRules("PRIMARY KEY");
                pkPropertyCodes = NamingStandardService.Instance.GetPropertyCodes("PRIMARY KEY");
            }
            catch (Exception ex)
            {
                Log($"[PK-TEMPLATE-ERROR] table='{tableName}': loading PRIMARY KEY rules failed: {ex.Message}");
                return;
            }
            bool hasTemplate = templateRules != null && templateRules.Count > 0;
            bool hasNonTemplate = pkPropertyCodes != null && pkPropertyCodes.Count > 0;
            // Pre-filter (no full walk): no PK naming rules at all -> no Key_Group collect.
            if (!hasTemplate && !hasNonTemplate) return;

            // Find THE primary-key Key_Group: Key_Group_Type == "PK". INDEX/AK key
            // groups are the same SCAPI class, so this filter is mandatory.
            dynamic pkKg = null;
            try
            {
                dynamic keyGroups = modelObjects.Collect(entity, "Key_Group");
                if (keyGroups != null)
                {
                    foreach (dynamic kg in keyGroups)
                    {
                        if (kg == null) continue;
                        string kgType;
                        try { kgType = kg.Properties("Key_Group_Type").Value?.ToString(); }
                        catch { continue; }
                        if (string.Equals(kgType, "PK", StringComparison.OrdinalIgnoreCase)) { pkKg = kg; break; }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[PK-TEMPLATE] table='{tableName}': Key_Group collect failed: {ex.Message}");
                return;
            }
            if (pkKg == null) return; // table has no primary key

            string pkId;
            try { pkId = pkKg.ObjectId?.ToString() ?? ""; } catch { pkId = ""; }
            // First sight of this PK == the Create moment for APPLY_ON gating.
            bool treatAsNew = !string.IsNullOrEmpty(pkId) && _pkTemplateSeen.Add(pkId);

            foreach (var rule in templateRules)
            {
                try
                {
                    // A write that already failed this session (e.g. the rule's
                    // target PropertyCode is not writable on a Key_Group) is not
                    // retried every tick - that would only spam the log.
                    string failKey = pkId + "|" + rule.Id;
                    if (_pkTemplateWriteFailed.Contains(failKey)) continue;

                    if (!NamingValidationEngine.MatchesApplyOn(rule, treatAsNew)) continue;
                    if (!NamingValidationEngine.IsRuleApplicable(rule, "PRIMARY KEY", pkKg))
                    {
                        // Log the condition reason once per PK (first sight only) so a
                        // never-firing conditional rule is diagnosable without spamming
                        // the per-heartbeat scoped path.
                        if (treatAsNew)
                            LogDebug($"[PK-TEMPLATE-COND] table='{tableName}' rule#{rule.Id} not applied: {NamingValidationEngine.DescribeApplicability(rule, "PRIMARY KEY", pkKg)}");
                        continue;
                    }

                    string targetCode = rule.PropertyCode;

                    // Self-referential template guard: a template that reads its own
                    // target property (e.g. value 'PK_{Physical_Name}' targeting
                    // Physical_Name) feeds its previous output back in every render,
                    // so under FILL_MODE=Always it grows without bound and writes a
                    // transaction every heartbeat (cursor flicker). Refuse it once,
                    // suppress further attempts, and tell the admin the fix.
                    if (NamingTemplateEngine.ReferencesOwnProperty(rule.ValueTemplate, targetCode))
                    {
                        _pkTemplateWriteFailed.Add(failKey);
                        Log($"[PK-TEMPLATE-SKIP] table='{tableName}' rule#{rule.Id}: template '{rule.ValueTemplate}' references its own target property '{targetCode}' (self-referential - would loop); suppressing. Use a related token like {{Table.Physical_Name}} instead.");
                        continue;
                    }

                    string rendered;
                    try
                    {
                        rendered = NamingTemplateEngine.Render(
                            rule.ValueTemplate,
                            ownCode => ReadScapiProperty(pkKg, ownCode),
                            (alias, code) => ResolvePrimaryKeyRelatedProperty(entity, alias, code));
                    }
                    catch (TemplateResolutionException tex)
                    {
                        string msg = !string.IsNullOrWhiteSpace(rule.ErrorMessage)
                            ? rule.ErrorMessage
                            : $"could not resolve token '{{{tex.Token}}}'";
                        Log($"[PK-TEMPLATE-SKIP] table='{tableName}' rule#{rule.Id} target='{targetCode}': {msg}");
                        continue;
                    }

                    string currentVal = ReadScapiProperty(pkKg, targetCode);
                    bool shouldWrite = NamingTemplateEngine.ShouldWrite(rule.TemplateFillMode, currentVal, out bool unknownMode);
                    if (unknownMode)
                    {
                        Log($"[PK-TEMPLATE-SKIP] table='{tableName}' rule#{rule.Id}: unknown TEMPLATE_FILL_MODE '{rule.TemplateFillMode}'");
                        continue;
                    }
                    if (!shouldWrite) continue;
                    if (string.Equals(currentVal, rendered, StringComparison.Ordinal)) continue; // idempotent

                    if (!rule.AutoApply)
                    {
                        var answer = AddinMessageDialog.Show(
                            $"Apply naming template to the primary key of '{tableName}'?\n\nSet {targetCode} to:\n{rendered}",
                            "Apply Naming Template",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                        if (answer != DialogResult.Yes) continue;
                    }

                    int transId = _session.BeginNamedTransaction("ApplyPrimaryKeyTemplate");
                    try
                    {
                        pkKg.Properties(targetCode).Value = rendered;
                        _session.CommitTransaction(transId);
                        Log($"[PK-TEMPLATE-APPLY] table='{tableName}' rule#{rule.Id} {targetCode}='{rendered}'");
                    }
                    catch (Exception wex)
                    {
                        try { _session.RollbackTransaction(transId); }
                        catch (Exception rex) { Log($"[PK-TEMPLATE-ERROR] table='{tableName}' rule#{rule.Id}: rollback failed: {rex.Message}"); }
                        // Likely a persistent config error (PropertyCode not writable
                        // on a Key_Group). Record it so we do not retry + spam the log;
                        // cleared on the next rebaseline / reconnect.
                        _pkTemplateWriteFailed.Add(failKey);
                        Log($"[PK-TEMPLATE-ERROR] table='{tableName}' rule#{rule.Id}: writing '{targetCode}' failed (suppressing further attempts this session): {wex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[PK-TEMPLATE-ERROR] table='{tableName}' rule#{rule.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Non-template naming rules (Prefix/Suffix/Length/Regexp/Required) on the
            // PK's rule-targeted properties. Mirrors the Index flow
            // (CheckEntityKeyGroups): baseline on first sight, then auto-apply
            // prefix/suffix + validate-warn on a value change (snapshot-gated; no
            // required-field force-fill - warn-only, like the Index path). Generic
            // over PropertyCode (the admin sets the target, usually Physical_Name).
            if (hasNonTemplate)
            {
                foreach (var propertyCode in pkPropertyCodes)
                {
                    try
                    {
                        string curVal = ReadScapiProperty(pkKg, propertyCode);
                        string snapKey = pkId + "|" + propertyCode;
                        bool propIsNew = !_pkPropertySnapshots.ContainsKey(snapKey);
                        bool propChanged = !propIsNew && !string.Equals(_pkPropertySnapshots[snapKey], curVal, StringComparison.Ordinal);

                        if (propIsNew)
                        {
                            // First sight: baseline only. Do not validate a pre-existing
                            // value the user has not touched (same as the Index flow).
                            _pkPropertySnapshots[snapKey] = curVal;
                            continue;
                        }
                        if (!propChanged) continue;

                        // Auto-apply prefix/suffix (AutoApply rules) on the changed value.
                        if (NamingValidationEngine.HasAutoApplyChanges("PRIMARY KEY", curVal, (object)pkKg, propertyCode: propertyCode))
                        {
                            string newVal = NamingValidationEngine.ApplyNamingStandards("PRIMARY KEY", curVal, (object)pkKg, propertyCode: propertyCode);
                            if (!string.Equals(newVal, curVal, StringComparison.Ordinal))
                            {
                                var answer = AddinMessageDialog.Show(
                                    $"Naming standard requires a change for the primary key of '{tableName}':\n\n{propertyCode}: '{curVal}' -> '{newVal}'\n\nApply automatically?",
                                    "Naming Standard - Auto Apply",
                                    MessageBoxButtons.YesNo,
                                    MessageBoxIcon.Question);
                                if (answer == DialogResult.Yes)
                                {
                                    int tx = _session.BeginNamedTransaction("ApplyPrimaryKeyNaming");
                                    try
                                    {
                                        pkKg.Properties(propertyCode).Value = newVal;
                                        _session.CommitTransaction(tx);
                                        Log($"[PK-NAMING-APPLY] table='{tableName}' {propertyCode} '{curVal}' -> '{newVal}'");
                                        curVal = newVal;
                                    }
                                    catch (Exception wex)
                                    {
                                        try { _session.RollbackTransaction(tx); }
                                        catch (Exception rex) { Log($"[PK-NAMING-ERROR] table='{tableName}': rollback failed: {rex.Message}"); }
                                        Log($"[PK-NAMING-ERROR] table='{tableName}': writing '{propertyCode}' failed: {wex.Message}");
                                    }
                                }
                            }
                        }

                        // Validate the (possibly auto-applied) value. Violations go to the
                        // consolidated warning popup - warn-only, no force-fill.
                        var results = NamingValidationEngine.ValidateObjectName("PRIMARY KEY", curVal, (object)pkKg, propertyCode);
                        if (results != null)
                        {
                            foreach (var r in results)
                            {
                                if (r.IsValid) continue;
                                Log($"[PK-NAMING] violation ({r.RuleName}): {tableName}.{curVal} - {r.ErrorMessage}");
                                _pendingResults.Add(new CollectedValidationResult
                                {
                                    ValidationType = CollectedValidationResultType.NamingStandard,
                                    TableName = tableName,
                                    ColumnName = curVal,
                                    Message = $"[Primary Key] {r.ErrorMessage}"
                                });
                            }
                        }

                        _pkPropertySnapshots[snapKey] = curVal;
                    }
                    catch (Exception ex)
                    {
                        Log($"[PK-NAMING-ERROR] table='{tableName}' property='{propertyCode}': {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Resolves a <c>{Alias.PropertyCode}</c> token for a PRIMARY KEY: maps the
        /// alias via the global <c>MC_OBJECT_RELATION</c> catalog then reads the
        /// related object's property. v1 supports PRIMARY KEY -> TABLE (the owning
        /// entity, already in scope). Unknown alias / unsupported navigation is a
        /// hard error (no-fallback), never a silent skip.
        /// </summary>
        private string ResolvePrimaryKeyRelatedProperty(dynamic entity, string alias, string propertyCode)
        {
            string toType = ObjectRelationCatalog.Instance.ResolveAlias("PRIMARY KEY", alias);
            if (string.IsNullOrEmpty(toType))
            {
                throw new TemplateResolutionException(
                    $"{alias}.{propertyCode}",
                    $"alias '{alias}' is not defined in MC_OBJECT_RELATION for object type 'PRIMARY KEY'");
            }

            if (string.Equals(toType, "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                // Parent table is the owning entity, already in scope - no reverse walk.
                return ReadScapiProperty(entity, propertyCode);
            }

            throw new TemplateResolutionException(
                $"{alias}.{propertyCode}",
                $"alias '{alias}' navigates PRIMARY KEY -> {toType}, which has no runtime navigation in this version");
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
        /// Lock enforcement for glossary-mapped fields flagged IS_LOCKED=1 in
        /// DG_TABLE_MAPPING_COLUMN (MAPPING_CODE='GLOSSARY'). A locked field is
        /// glossary-owned: the authoritative value is the glossary value for the
        /// column's matched term, so any user edit that drifts off it is reverted.
        ///
        /// erwin SCAPI has NO per-attribute read-only (a property's read-only flag
        /// can be queried but not set), so this is reconciliation: the applicator
        /// (<see cref="ApplyGlossaryUdpValues"/>) seeds the value, and this pass
        /// reverts a user edit back to the glossary value + notifies. Runs on the
        /// same tick as <see cref="EnforceLockedAttributeUdps"/>, BEFORE the
        /// dependency cascade, for the same user-only-intent reason.
        ///
        /// Enforceable target types: UDP and ERWIN_PROPERTY. DB_PROPERTY apply is
        /// a no-op today (nothing is written), so a locked DB_PROPERTY is logged
        /// and skipped - there is no applied value to revert.
        ///
        /// Additive: when nothing is locked (IS_LOCKED=0 everywhere) this returns
        /// immediately, so unlocked fields keep exactly today's behaviour.
        /// </summary>
        private void EnforceLockedGlossaryFields(dynamic attr, string objectId, string columnName)
        {
            if (attr == null) return;
            var glossary = GlossaryService.Instance;
            if (!glossary.IsLoaded) return;

            var lockedMappings = glossary.GetLockedMappings();
            if (lockedMappings.Count == 0) return;              // IS_LOCKED=0 everywhere -> no-op

            var glossaryValues = glossary.GetUdpValues(columnName);
            if (glossaryValues == null) return;                 // column has no glossary match

            foreach (var (targetField, targetType) in lockedMappings)
            {
                glossaryValues.TryGetValue(targetField, out string glossaryVal);
                glossaryVal = glossaryVal ?? "";

                if (string.IsNullOrEmpty(glossaryVal))
                {
                    // Locked field with no glossary value for this term (miss): do
                    // not fabricate, do not enforce an empty (the miss is flagged at
                    // apply time). Nothing to revert to.
                    continue;
                }

                string upperType = targetType?.ToUpperInvariant() ?? "";
                if (upperType == "DB_PROPERTY")
                {
                    Log($"Glossary lock: '{targetField}' is DB_PROPERTY (apply is a no-op) - not enforceable, skipped.");
                    continue;
                }

                string currentVal = ReadGlossaryFieldValue(attr, targetField, upperType);
                if (currentVal == null) continue;               // unreadable - already logged
                if (string.Equals(currentVal, glossaryVal, StringComparison.Ordinal)) continue;

                // User drifted a locked field off its glossary value - revert it.
                try
                {
                    WriteGlossaryFieldValue(attr, targetField, upperType, glossaryVal);
                    Log($"Glossary lock: '{targetField}' on '{columnName}' reverted '{currentVal}' -> '{glossaryVal}'");
                    Forms.LockedUdpDialog.Show(targetField, currentVal, glossaryVal);
                }
                catch (Exception ex)
                {
                    Log($"Glossary lock revert failed for '{targetField}' on '{columnName}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Reads a glossary target field's current value by target type so the lock
        /// check can compare it to the glossary value. UDP (and the prefix-less
        /// default) read the canonical Column-UDP path (the same one
        /// <see cref="EnforceLockedAttributeUdps"/> uses); ERWIN_PROPERTY reads the
        /// mapped erwin property. Returns null on a read error (logged) so the
        /// caller skips this field this tick rather than mis-reverting.
        /// </summary>
        private string ReadGlossaryFieldValue(dynamic attr, string targetField, string upperType)
        {
            try
            {
                if (upperType == "ERWIN_PROPERTY")
                {
                    string erwinName = MapPropertyCodeToErwin(targetField);
                    return attr.Properties(erwinName)?.Value?.ToString() ?? "";
                }
                // UDP + prefix-less default: canonical Column-UDP property path.
                return attr.Properties($"Attribute.Physical.{targetField}")?.Value?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                Log($"Glossary lock: read '{targetField}' failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes the glossary value back (the revert), reusing the applicator's
        /// setters so the write path stays identical to a normal glossary apply:
        /// <see cref="TrySetProperty"/> for ERWIN_PROPERTY, <see cref="TrySetUdp"/>
        /// for UDP (and the prefix-less default).
        /// </summary>
        private void WriteGlossaryFieldValue(dynamic attr, string targetField, string upperType, string value)
        {
            if (upperType == "ERWIN_PROPERTY")
            {
                TrySetProperty(attr, MapPropertyCodeToErwin(targetField), value);
                return;
            }
            TrySetUdp(attr, targetField, value);
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

                // Nullability's rule value is stored as the integer write token (0/1) erwin
                // accepts on Null_Option_Type; show it as NULL / NOT NULL so the popup reads
                // clearly. Other properties already carry human-readable rule values.
                string detail = string.Join("\n", driftedProps.Select(d =>
                {
                    string shownRule = string.Equals(d.accessor, "Null_Option_Type", StringComparison.Ordinal)
                        ? (d.ruleValue == "1" ? "NOT NULL" : d.ruleValue == "0" ? "NULL" : d.ruleValue)
                        : d.ruleValue;
                    return $"{d.label}: \"{d.liveValue}\" -> \"{shownRule}\"";
                }));
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

                // New column: enforce the datatype whitelist against its committed type so a
                // disallowed erwin default (e.g. char(18)) is caught at creation, not only on a
                // later type edit. EnforceAllowedDatatypeWhitelist self-skips the '<default>'
                // placeholder, so the check effectively waits for the real name.
                EnforceAllowedDatatypeWhitelist(attr, null, currentState, isNew: true);
            }
            finally
            {
                _isProcessingChange = false;
            }
        }

        /// <returns>True when the attribute was DISCARDED (deleted) by a Required-field
        /// Cancel on a pending-new column, so the caller must skip all post-work that
        /// would touch the now-dead COM object.</returns>
        private bool ProcessAttributeChanges(dynamic attr, AttributeValidationSnapshot previousState, AttributeValidationSnapshot currentState, HashSet<string> predefinedColumnNames)
        {
            if (_validationSuspended) return false;
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
            if (!physicalNameChanged && !domainChanged && !dataTypeChanged) return false;

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
                return false;
            }
            if (!physicalNameChanged && EnforceLockedColumnPropertyChange(attr, previousState, currentState))
            {
                return false;
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

                    // Validate Column naming standards. If the user discards a pending-new
                    // column's mandatory field, the column is deleted in here; stop
                    // processing it so nothing downstream touches the now-dead COM object.
                    if (ValidateColumnNamingStandard(attr, currentState)) return true;
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

                    // Datatype whitelist (admin "Datatype Library" -> DATATYPE_LIBRARY for the
                    // active config). Runs AFTER the term-type policy so a glossary-authoritative
                    // type is settled first, and BEFORE the naming re-run below so the engine sees
                    // the final (possibly reverted) value. No-op when the config has no
                    // configured datatype list. isNew:false - this branch is a type change on an
                    // EXISTING column (rename path handled separately); ValidateColumnNamingStandard
                    // just below re-checks the picked value too, so the picker gate + that re-run
                    // both cover this site.
                    EnforceAllowedDatatypeWhitelist(attr, previousState, currentState, isNew: false);

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

                return false;
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
                    // ShowValidationModal (2026-07-09): both timers bail while this pumps, same
                    // protection as the datatype picker - without it a WindowMonitor tick could
                    // stack a naming popup on top of this dialog.
                    ShowValidationModal(
                        $"Column '{curr.TableName}.{curr.PhysicalName}' is tagged '{canonical}' in the glossary.\n\n" +
                        $"{disallowed} cannot be changed and was reverted.\n\n" +
                        $"Restored: {corrected}",
                        "Term Type Constraint",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    // A rename (erwin auto-uniquify) can land while the modal pumps. Sync it so
                    // the caller's dialogs/validators cite the live name - ProcessAttributeChanges
                    // re-runs the column naming pass right after enforcement, so the refreshed
                    // name IS re-validated (Core's IsAutoUniquifyRename baseline bridge decides
                    // Create-vs-Update). The scheduled recheck covers a commit landing even later.
                    RefreshNameAfterModal(attr, curr, "Term Type Constraint");
                    ScheduleAttributeRecheck(curr);
                }
            }
            catch (Exception ex)
            {
                try { _session.RollbackTransaction(trans); }
                catch (Exception rbEx) { Log($"EnforceTermTypePolicy rollback error: {rbEx.Message}"); }
                Log($"EnforceTermTypePolicy write failed for {curr.PhysicalName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-reads the LIVE Physical_Name of a column. erwin commits renames on a DELAYED
        /// transaction - most notably the auto-uniquify '__NNNN' suffix after a name collision -
        /// so any name captured into a snapshot before a modal pumped can be stale by the time
        /// it is displayed or validated. Mirrors CreateSnapshot's placeholder handling: a '%...'
        /// macro placeholder falls back to the logical Name. Returns <paramref name="fallback"/>
        /// when the live read fails (e.g. the attribute was deleted).
        /// </summary>
        private string ReadLivePhysicalName(dynamic attr, string fallback)
        {
            try
            {
                string live = attr?.Properties("Physical_Name").Value?.ToString();
                if (!string.IsNullOrEmpty(live))
                {
                    if (!live.StartsWith("%", StringComparison.Ordinal)) return live;
                    string logical = attr?.Name?.ToString();
                    if (!string.IsNullOrEmpty(logical)) return logical;
                }
            }
            catch (Exception ex) { Log($"Live Physical_Name re-read failed (using '{fallback}'): {ex.Message}"); }
            return fallback ?? string.Empty;
        }

        /// <summary>
        /// Detects a rename that landed while a modal pumped: re-reads the live Physical_Name and,
        /// when it differs from the snapshot, syncs the snapshot and returns true so the caller
        /// re-validates with the live name. Callers MUST guarantee a naming validation runs on the
        /// refreshed name afterwards - syncing without validating would silently absorb the rename,
        /// which is exactly the 'Pre_Abc__1070' bug this exists to fix.
        /// </summary>
        private bool RefreshNameAfterModal(dynamic attr, AttributeValidationSnapshot state, string modalContext)
        {
            if (state == null) return false;
            string live = ReadLivePhysicalName(attr, state.PhysicalName);
            if (string.Equals(live, state.PhysicalName ?? string.Empty, StringComparison.Ordinal)) return false;
            Log($"Live name drift caught ({modalContext}): '{state.TableName}.{state.PhysicalName}' -> '{live}' - re-validating with the live name");
            state.PhysicalName = live;
            return true;
        }

        /// <summary>
        /// Shows an enforcement warning under <see cref="_validationModalShowing"/> so BOTH timers
        /// bail while the modal pumps (same protection the datatype picker gets). Without the flag
        /// a WindowMonitor tick during the pump can run editor-close/inline-edit-close work that
        /// re-enters the same attribute mid-flight and stacks a second modal (2026-07-08 overlap).
        /// </summary>
        private DialogResult ShowValidationModal(string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            _validationModalShowing = true;
            try { return AddinMessageDialog.Show(message, title, buttons, icon); }
            finally { _validationModalShowing = false; }
        }

        /// <summary>
        /// Schedules a targeted live-vs-snapshot recheck of ONE attribute (see
        /// <see cref="_attrRecheckQueue"/>). Idempotent per attribute: rescheduling pushes the due
        /// time out, which is what we want - the LAST write of a gesture restarts the settle window
        /// erwin's delayed commits need.
        /// </summary>
        private void ScheduleAttributeRecheck(string objectId, string tableName, int delayMs = 1500)
        {
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(tableName)) return;
            _attrRecheckQueue[objectId] = (tableName, DateTime.UtcNow.AddMilliseconds(delayMs));
        }

        private void ScheduleAttributeRecheck(AttributeValidationSnapshot state, int delayMs = 1500)
            => ScheduleAttributeRecheck(state?.ObjectId, state?.TableName, delayMs);

        /// <summary>
        /// Drains due entries of <see cref="_attrRecheckQueue"/>: for each, re-reads the live
        /// attribute and routes any name/type drift through the NORMAL ProcessAttributeChanges
        /// machinery (rename branch -> glossary + naming with the auto-uniquify isNew bridge).
        /// Targeted by ObjectId - never a model-wide walk (hard rule: no full walks in change
        /// detection). Runs inside MonitorTimer_Tick, so all its guards already held.
        /// </summary>
        private void DrainAttributeRecheckQueue(dynamic modelObjects, dynamic root)
        {
            if (_attrRecheckQueue.Count == 0) return;
            var now = DateTime.UtcNow;
            List<string> dueIds = null;
            foreach (var kv in _attrRecheckQueue)
            {
                if (kv.Value.DueUtc <= now) (dueIds ??= new List<string>()).Add(kv.Key);
            }
            if (dueIds == null) return;

            foreach (var objectId in dueIds)
            {
                string tableName = _attrRecheckQueue[objectId].TableName;
                _attrRecheckQueue.Remove(objectId);

                // A pending-new placeholder is still owned by the [PENDING-NAME] machinery;
                // its own commit path validates the final name.
                if (IsAttributePendingNew(objectId)) continue;
                if (!_attributeSnapshots.TryGetValue(objectId, out var previousState) || previousState == null) continue;

                try { RecheckAttributeAgainstSnapshot(modelObjects, root, objectId, tableName, previousState); }
                catch (Exception ex) { Log($"[RECHECK] failed for '{tableName}' attr {objectId}: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Live-vs-snapshot compare for one attribute, resolved by owning-entity name + ObjectId
        /// (bounded: one entity's attribute list, never the whole model). On drift, runs the same
        /// carry-over + ProcessAttributeChanges + snapshot-advance sequence the pending-commit
        /// path uses, so a late-landing erwin rename gets the full rule chain ('Physical name
        /// changed' log line included).
        /// </summary>
        private void RecheckAttributeAgainstSnapshot(dynamic modelObjects, dynamic root, string objectId, string tableName, AttributeValidationSnapshot previousState)
        {
            dynamic entities = modelObjects.Collect(root, "Entity");
            if (entities == null) return;
            try
            {
                foreach (dynamic entity in entities)
                {
                    if (entity == null) continue;
                    string entName = GetTableName(entity);
                    if (!string.Equals(entName, tableName, StringComparison.Ordinal)
                        && !string.Equals(entName, previousState.TableName ?? string.Empty, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    dynamic attrs = modelObjects.Collect(entity, "Attribute");
                    if (attrs == null) return;
                    try
                    {
                        foreach (dynamic attr in attrs)
                        {
                            if (attr == null) continue;
                            string aid;
                            try { aid = attr.ObjectId?.ToString(); }
                            catch { continue; }
                            if (!string.Equals(aid, objectId, StringComparison.Ordinal)) continue;

                            var snapshot = CreateSnapshot(attr, entName, modelObjects);
                            bool nameDrift = !string.Equals(previousState.PhysicalName ?? string.Empty, snapshot.PhysicalName ?? string.Empty, StringComparison.Ordinal);
                            bool typeDrift = !string.Equals(previousState.PhysicalDataType ?? string.Empty, snapshot.PhysicalDataType ?? string.Empty, StringComparison.Ordinal);
                            if (!nameDrift && !typeDrift) return;

                            Log($"[RECHECK] '{entName}' attr {objectId}: snapshot-vs-live drift" +
                                (nameDrift ? $" name '{previousState.PhysicalName}' -> '{snapshot.PhysicalName}'" : string.Empty) +
                                (typeDrift ? $" type '{previousState.PhysicalDataType}' -> '{snapshot.PhysicalDataType}'" : string.Empty) +
                                " - running change validation");

                            // Same carry-over the pending-commit path does: term type and UDPs are
                            // not re-derivable from a bare snapshot.
                            snapshot.TermTypeCanonical = previousState.TermTypeCanonical;
                            foreach (var kvp in previousState.UdpValues) snapshot.UdpValues[kvp.Key] = kvp.Value;
                            if (string.IsNullOrEmpty(snapshot.PhysicalDataType)) snapshot.PhysicalDataType = previousState.PhysicalDataType;

                            var predefined = GetPredefinedColumnNames(entity);
                            _pendingResults.Clear();
                            bool discarded = ProcessAttributeChanges(attr, previousState, snapshot, predefined);
                            if (discarded)
                            {
                                _attributeSnapshots.Remove(objectId);
                                Log($"[RECHECK] '{entName}' attr {objectId} discarded during re-validation - snapshot removed");
                            }
                            else
                            {
                                _attributeSnapshots[objectId] = snapshot;
                            }
                            if (_pendingResults.Count > 0) ShowConsolidatedPopup();
                            return;
                        }
                    }
                    finally { ReleaseCom(attrs); }
                    return; // entity found but attribute gone (deleted) - nothing to validate
                }
            }
            finally { ReleaseCom(entities); }
        }

        /// <summary>
        /// Inline-edit OPEN edge: captures which attributes the user might be editing in-place, by
        /// matching the edit control's INITIAL text (the old value) against in-memory snapshots.
        /// Name matches take priority over datatype matches (type strings like 'varchar(18)' are
        /// shared by many columns); the candidate set is capped so the later recheck stays bounded.
        /// In-memory string compares only - no SCAPI call happens here.
        /// </summary>
        private void CaptureInlineEditCandidates(IntPtr erwinHwnd)
        {
            _inlineEditRecheckCandidates.Clear();
            string editText;
            try { editText = Win32Helper.GetFocusedInlineEditText(erwinHwnd); }
            catch (Exception ex) { Log($"[INLINE-EDIT] candidate capture failed: {ex.Message}"); return; }
            if (string.IsNullOrWhiteSpace(editText)) return;

            var snaps = new List<(string ObjectId, string TableName, string PhysicalName, string PhysicalDataType)>(_attributeSnapshots.Count);
            foreach (var kv in _attributeSnapshots)
            {
                var s = kv.Value;
                if (s == null) continue;
                snaps.Add((kv.Key, s.TableName, s.PhysicalName, s.PhysicalDataType));
            }

            var (candidates, overflowed) = SelectInlineEditCandidates(snaps, editText.Trim(), InlineEditCandidateCap);
            if (overflowed)
            {
                Log($"[INLINE-EDIT] more than {InlineEditCandidateCap} snapshot matches for edited text '{editText.Trim()}' - extra candidates skipped so the recheck stays bounded");
            }
            if (candidates.Count > 0)
            {
                _inlineEditRecheckCandidates.AddRange(candidates);
                Log($"[INLINE-EDIT] captured {candidates.Count} recheck candidate(s) for edited text '{editText.Trim()}'");
            }
        }

        public const int InlineEditCandidateCap = 8;

        /// <summary>
        /// Pure candidate selection for <see cref="CaptureInlineEditCandidates"/> (unit-tested).
        /// Exact-name matches first, then exact-datatype matches while the cap allows; returns
        /// whether matches had to be dropped. Ordinal compares - the edit shows the value verbatim.
        /// </summary>
        public static (List<(string ObjectId, string TableName)> Candidates, bool Overflowed) SelectInlineEditCandidates(
            IReadOnlyList<(string ObjectId, string TableName, string PhysicalName, string PhysicalDataType)> snapshots,
            string editText,
            int cap)
        {
            var result = new List<(string, string)>();
            bool overflowed = false;
            if (string.IsNullOrEmpty(editText) || snapshots == null) return (result, false);

            foreach (bool namePass in new[] { true, false })
            {
                foreach (var s in snapshots)
                {
                    if (string.IsNullOrEmpty(s.ObjectId) || string.IsNullOrEmpty(s.TableName)) continue;
                    bool nameHit = string.Equals(s.PhysicalName ?? string.Empty, editText, StringComparison.Ordinal);
                    bool hit = namePass ? nameHit
                                        : (!nameHit && string.Equals(s.PhysicalDataType ?? string.Empty, editText, StringComparison.Ordinal));
                    if (!hit) continue;
                    if (result.Count >= cap) { overflowed = true; return (result, true); }
                    result.Add((s.ObjectId, s.TableName));
                }
            }
            return (result, overflowed);
        }

        /// <summary>
        /// Inline-edit CLOSE edge: schedules the captured candidates for a live-vs-snapshot
        /// recheck. Short delay - the in-place editor commits on focus loss, but erwin may finish
        /// the write on a delayed transaction just after the edge.
        /// </summary>
        private void FlushInlineEditCandidates()
        {
            if (_inlineEditRecheckCandidates.Count == 0) return;
            foreach (var (objectId, table) in _inlineEditRecheckCandidates)
            {
                ScheduleAttributeRecheck(objectId, table, delayMs: 400);
            }
            Log($"[INLINE-EDIT] scheduled {_inlineEditRecheckCandidates.Count} attribute recheck(s) after inline-edit close");
            _inlineEditRecheckCandidates.Clear();
        }

        /// <summary>
        /// Enforce the admin "Datatype Library" whitelist (config-scoped DATATYPE_LIBRARY for the
        /// active config) on a column's Physical_Data_Type. When the config has a non-empty
        /// list and the user picks a type not in it, write an allowed type and warn once (user
        /// decision 2026-06-19: hard enforce, never leave a disallowed type). The written value is
        /// the previous value when that is itself allowed (a normal revert), otherwise a forced
        /// fallback allowed type (a brand-new column whose first pick is disallowed). No-op when
        /// the list is empty (no restriction). Mirrors <see cref="EnforceTermTypePolicy"/>'s single-transaction
        /// revert + snapshot-advance + double-commit dedup. See
        /// reference_datatype_library_unimplemented.
        /// </summary>
        /// <summary>Base token to preselect in the datatype picker: the value now in
        /// the model (fallback: the intended target), stripped of any parameter -
        /// "char(18)" -> "char" - so the combo lands on the matching whitelist entry.</summary>
        private static string AllowedDatatypePickerFormPreselect(string live, string target)
        {
            string src = !string.IsNullOrEmpty(live) ? live : (target ?? "");
            int p = src.IndexOf('(');
            return (p > 0 ? src.Substring(0, p) : src).Trim();
        }

        private void EnforceAllowedDatatypeWhitelist(dynamic attr, AttributeValidationSnapshot prev, AttributeValidationSnapshot curr, bool isNew = false)
        {
            // No whitelist configured for this config -> nothing to enforce.
            if (!AllowedDatatypeService.Instance.HasRestriction) return;

            // Defer while the column still carries erwin's transient '<default>' placeholder name:
            // the popup would otherwise surface '<default>', and the name-commit path re-checks the
            // final type once the user names the column. This is the single placeholder guard for
            // every caller (change path, new-attribute path, name-commit paths).
            if (IsPlaceholderColumnName(curr?.PhysicalName ?? string.Empty)) return;

            // Validate the LIVE type, not the snapshot field. A prior SCAPI writer in this same
            // gesture (e.g. ValidateDomain/ApplyDomainProperties applying a domain's datatype) can
            // change Physical_Data_Type WITHOUT refreshing the snapshot, leaving curr.PhysicalDataType
            // a stale pre-domain default (e.g. char(18)). Re-read so we never clobber a value a
            // domain/glossary just applied or cite a type the user never set. Sync curr so the dedup
            // key and the baseline reflect what is actually in the model.
            string attempted = curr?.PhysicalDataType ?? string.Empty;
            try
            {
                string liveNow = attr?.Properties("Physical_Data_Type").Value?.ToString();
                if (!string.IsNullOrEmpty(liveNow)) attempted = liveNow;
            }
            catch (Exception preEx) { Log($"AllowedDatatype: live type pre-read failed for {curr?.PhysicalName}: {preEx.Message}"); }
            if (curr != null) curr.PhysicalDataType = attempted;

            if (AllowedDatatypeService.Instance.IsAllowed(attempted)) return; // permitted

            // The NAME can be stale the same way the type was: erwin's delayed rename commits
            // (auto-uniquify '__NNNN' after a name collision) land between the snapshot capture
            // and this call. Re-read it so the glossary term lookup and every dialog below cite
            // the column the user actually sees. A sync alone would silently absorb the rename,
            // so renameCaught forces a naming re-validation before this method returns
            // (the 'Pre_Abc__1070' bug, 2026-07-09).
            bool renameCaught = RefreshNameAfterModal(attr, curr, "AllowedDatatype pre-enforcement");

            // Term-type changeability (2026-07-09). The column's canonical term type (glossary
            // TERM_TYPE_MAP) constrains which parts of the datatype may change: BUSINESS_TERM
            // locks base+length, AMORPH_DATA_TYPE locks the length, AMORPH_DATA_LENGTH locks the
            // base (TermTypeLocks = single source, mirrors EnforceTermTypePolicy). The picker was
            // term-type-BLIND, so after the policy correctly reverted a Business column's type,
            // this whitelist picker let the user override the lock (log 16:52: 'user picked
            // NUMBER(45)' on a BUSINESS_TERM column). The authoritative value = the pre-edit type
            // (prev snapshot); on new-column call sites (prev==null) the glossary cache supplies it.
            var (termLockBase, termLockLength) = TermTypeLocks.Get(curr?.TermTypeCanonical);
            string termAuthoritative = prev?.PhysicalDataType;
            if (string.IsNullOrEmpty(termAuthoritative) && (termLockBase || termLockLength))
                termAuthoritative = GetGlossaryAuthoritativeDatatype(curr?.PhysicalName);
            bool termHasLock = (termLockBase || termLockLength) && !string.IsNullOrEmpty(termAuthoritative);
            // The picker can only pin a locked base that exists in the whitelist combo; a locked
            // base MISSING from the whitelist is a glossary/whitelist admin conflict - no picker
            // can represent it, so fall through to the warn-only path and keep the authoritative
            // value (authority wins; the admin must reconcile the Datatype Library).
            string termAuthBase = termHasLock ? DataTypeParser.Parse(termAuthoritative).Base : null;
            bool termBasePinnable = termHasLock && termLockBase
                && AllowedDatatypeService.Instance.Allowed.Any(a =>
                       a != null && string.Equals(a.Datatype, termAuthBase, StringComparison.OrdinalIgnoreCase));
            bool termNoPicker = termHasLock
                && ((termLockBase && termLockLength) || (termLockBase && !termBasePinnable));

            // Target type to write. Prefer the previous value when it is ITSELF allowed (least
            // surprising - the column goes back to what it was). Otherwise force an allowed type
            // (user decision 2026-06-19 "izinli tipe zorla"): a brand-new column whose first pick
            // is disallowed has no allowed previous value, and the model must never keep a
            // disallowed type. GetFallbackDatatype gives a deterministic allowed token (the only
            // one if a single type is allowed, e.g. int; else the first by name).
            string restored = prev?.PhysicalDataType ?? string.Empty;
            bool canRestore = !string.IsNullOrEmpty(restored)
                              && AllowedDatatypeService.Instance.IsAllowed(restored);
            string target = canRestore ? restored : AllowedDatatypeService.Instance.GetFallbackDatatype();

            // Term lock overrides the automatic target: a locked column must go back to its
            // term-authoritative value, EVEN when that value is not whitelist-allowed (falling
            // back to e.g. 'varchar(1)' would silently change a locked base/length - the exact
            // override this fix removes). If glossary and whitelist disagree, that is admin data
            // to reconcile; the add-in must not "fix" it by mutating a term-locked type.
            if (termHasLock)
                target = termAuthoritative;

            // Dedup erwin's double combo-commit: the Column Editor commits the user pick twice (the
            // combo commit + a later sync that RE-APPLIES the disallowed value). Suppress the SECOND
            // popup but STILL re-enforce on the duplicate - exactly like EnforceTermTypePolicy - so
            // that second sync can never leave the disallowed type in the model. (Skipping the write
            // on the duplicate would let it persist.) The pathological non-round-trip loop is broken
            // separately, by advancing curr to the LIVE stored value after the write below, not by
            // skipping the write.
            bool suppressPopup = false;
            string objId = curr?.ObjectId ?? string.Empty;
            if (!string.IsNullOrEmpty(objId)
                && _allowedDatatypeRecentAttempts.TryGetValue(objId, out var last)
                && string.Equals(last.attempt, attempted, StringComparison.Ordinal)
                && (DateTime.UtcNow - last.when).TotalSeconds < TermTypeDedupSeconds)
            {
                suppressPopup = true;
            }
            if (!string.IsNullOrEmpty(objId))
                _allowedDatatypeRecentAttempts[objId] = (attempted, DateTime.UtcNow);

            // erwin's delayed second combo-commit re-applies the DISALLOWED value with
            // the popup suppressed. Re-enforce with what the user just PICKED in the
            // picker dialog (if any), not the automatic fallback - otherwise the
            // duplicate would silently replace their choice.
            if (suppressPopup
                && !string.IsNullOrEmpty(objId)
                && _allowedDatatypeUserPicks.TryGetValue(objId, out var remembered)
                && (DateTime.UtcNow - remembered.when).TotalSeconds < TermTypeDedupSeconds * 4
                && AllowedDatatypeService.Instance.IsAllowed(remembered.pick)
                // A remembered pick may only override a term-locked target when it respects the
                // locked parts (a pick made through the lock-aware picker always does; a stale
                // pick from before the locks were known must not resurrect the override).
                && (!termHasLock || TermTypeLocks.Honors(remembered.pick, termAuthoritative, termLockBase, termLockLength)))
            {
                target = remembered.pick;
            }

            if (string.IsNullOrEmpty(target))
            {
                // HasRestriction guarantees a non-empty list, so GetFallbackDatatype returns a
                // value; this is defensive only (e.g. a whitelist of only blank entries).
                Log($"AllowedDatatype: {curr?.TableName}.{curr?.PhysicalName} attempted '{attempted}' not in whitelist and no allowed type to fall back on - warned only.");
                if (!suppressPopup)
                {
                    ShowValidationModal(
                        $"Column '{curr?.TableName}.{curr?.PhysicalName}': datatype '{attempted}' is not in the allowed datatype list for this configuration.\n\n" +
                        $"Please pick an allowed datatype.",
                        "Datatype not allowed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    renameCaught |= RefreshNameAfterModal(attr, curr, "no-fallback warn");
                }
                // A rename caught before/during the warn must still get its Name rules.
                if (renameCaught && curr != null)
                {
                    bool discardedWarnOnly = ValidateColumnNamingStandard(attr, curr, isNew: isNew);
                    if (isNew && !discardedWarnOnly && _pendingResults.Count > 0) ShowConsolidatedPopup();
                }
                ScheduleAttributeRecheck(curr);
                return;
            }

            int trans = _session.BeginNamedTransaction("EnforceAllowedDatatype");
            try
            {
                attr.Properties("Physical_Data_Type").Value = target;
                _session.CommitTransaction(trans);

                // Advance the snapshot to the value erwin ACTUALLY stored (re-read), not the catalog
                // token we wrote - they can differ if erwin canonicalizes the spelling. Using the
                // real stored value as the next baseline stops the monitor re-firing on a phantom
                // diff AND breaks the pathological loop: if the stored value does not round-trip it
                // is still disallowed, but baseline==live so the change path never re-fires; the
                // name-commit paths fire once per rename, so neither re-writes in a loop.
                string liveAfterWrite = target;
                try { liveAfterWrite = attr.Properties("Physical_Data_Type").Value?.ToString() ?? target; }
                catch (Exception reEx) { Log($"AllowedDatatype: re-read after write failed for {curr?.PhysicalName}: {reEx.Message}"); }
                curr.PhysicalDataType = liveAfterWrite;

                bool storedDiffers = !string.Equals(liveAfterWrite, target, StringComparison.Ordinal);
                bool roundTripped = AllowedDatatypeService.Instance.IsAllowed(liveAfterWrite);
                Log($"AllowedDatatype: {curr?.TableName}.{curr?.PhysicalName} '{attempted}' not in whitelist - {(canRestore ? "reverted" : "forced")} to '{target}'" +
                    $"{(storedDiffers ? $" (erwin stored '{liveAfterWrite}')" : "")}{(roundTripped ? "" : " - WARNING: stored value still not allowed")}{(suppressPopup ? " (popup suppressed, duplicate within " + TermTypeDedupSeconds + "s)" : "")}");

                if (!suppressPopup && termNoPicker)
                {
                    // Term type locks everything the picker could offer (BUSINESS_TERM: base AND
                    // length fixed; or a locked base the whitelist cannot even represent). There is
                    // nothing for the user to choose - the authoritative value is already restored
                    // above - so no picker: one warning explains WHY the type snapped back, plus an
                    // admin note when the glossary type itself conflicts with the whitelist.
                    Log($"AllowedDatatype: {curr?.TableName}.{curr?.PhysicalName} is term-locked ('{curr?.TermTypeCanonical}') - picker skipped, kept '{liveAfterWrite}'{(roundTripped ? "" : " (glossary type not in whitelist - admin conflict)")}");
                    ShowValidationModal(
                        $"Column '{curr?.TableName}.{curr?.PhysicalName}' is mapped to a glossary term (term type '{curr?.TermTypeCanonical}').\n\n" +
                        $"Its datatype is fixed to '{liveAfterWrite}' by the term mapping and cannot be changed here." +
                        (roundTripped ? "" :
                            $"\n\nNOTE: '{liveAfterWrite}' is not in the allowed datatype list for this configuration - " +
                            "ask your administrator to align the Datatype Library with the glossary."),
                        "Datatype fixed by term mapping",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    // Remember the authoritative value so erwin's delayed duplicate combo-commit
                    // re-enforces it silently, and restamp the dedup window (same as the pick path).
                    if (!string.IsNullOrEmpty(objId))
                    {
                        _allowedDatatypeUserPicks[objId] = (liveAfterWrite, DateTime.UtcNow);
                        _allowedDatatypeRecentAttempts[objId] = (attempted, DateTime.UtcNow);
                    }

                    // The warn modal pumps like the picker does - catch a rename that landed
                    // during it and re-run the naming pass on the LIVE name (this branch has no
                    // replay of its own).
                    renameCaught |= RefreshNameAfterModal(attr, curr, "term-mapping warn");
                    if (renameCaught)
                    {
                        bool discardedTermWarn = ValidateColumnNamingStandard(attr, curr, isNew: isNew);
                        if (isNew && !discardedTermWarn && _pendingResults.Count > 0) ShowConsolidatedPopup();
                    }
                }
                else if (!suppressPopup)
                {
                    // Let the user pick the allowed replacement (2026-07-02 request):
                    // combo of the whitelist, parameter input for parameterized types.
                    // The safe automatic value is ALREADY in the model (written above),
                    // so the invariant "never hold a disallowed type" holds while the
                    // modal is up; Cancel simply keeps it.
                    string pickMessage = roundTripped
                        ? $"Column '{curr?.TableName}.{curr?.PhysicalName}': datatype '{attempted}' is not in the allowed datatype list for this configuration.\n\n" +
                          $"Choose the allowed datatype to use (Cancel keeps '{liveAfterWrite}')."
                        : $"Column '{curr?.TableName}.{curr?.PhysicalName}': datatype '{attempted}' is not allowed, and the configured allowed type '{target}' could not be applied (the model now holds '{liveAfterWrite}').\n\n" +
                          $"Choose an allowed datatype, and ask your administrator to check the Datatype Library entry for this DBMS.";

                    string pickPreselect = AllowedDatatypePickerFormPreselect(liveAfterWrite, target);

                    // Term-type partial locks (2026-07-09): pin the locked half to the
                    // term-authoritative value and tell the picker to disable that control -
                    // the combo when the base may not change (AMORPH_DATA_LENGTH), the
                    // parameter field when the length may not change (AMORPH_DATA_TYPE).
                    bool pickerLockBase = termHasLock && termLockBase;   // base pinnable, else termNoPicker took over
                    bool pickerLockParam = termHasLock && termLockLength;
                    string pickPrefill = Forms.AllowedDatatypePickerForm.ExtractParameter(attempted);
                    if (pickerLockBase) pickPreselect = termAuthBase;
                    if (pickerLockParam) pickPrefill = Forms.AllowedDatatypePickerForm.ExtractParameter(termAuthoritative);
                    if (pickerLockBase || pickerLockParam)
                        pickMessage += $"\n\nTerm mapping ('{curr?.TermTypeCanonical}'): " +
                            (pickerLockBase ? "the base type is fixed and cannot be changed." : "the length/precision is fixed and cannot be changed.");

                    // treatAsNew mirrors ValidateColumnNamingStandardCore (isNew || pending-new) so
                    // the picker's inline rule validator - and the post-enforcement re-check below -
                    // evaluate exactly the Create/Update/Both rules the editor-close naming pass would.
                    bool treatAsNew = isNew || (!string.IsNullOrEmpty(objId) && IsAttributePendingNew(objId));

                    // validate: run the admin Physical_Data_Type rules against the COMPOSED pick
                    // before the picker commits it. A violation (e.g. length > 4000) keeps the user
                    // in the dialog with the admin's own message, so a rule-breaking datatype can
                    // never leave the picker - this is what closes the Model Explorer gap where the
                    // picked value was never regex-validated (no editor-close pass to catch it).
                    // Guard the whole modal so BOTH timers bail while it is up (no naming popup
                    // stacked on top - they must come sequentially). Set immediately before Show,
                    // cleared in finally so an exception can never leave it stuck (which would
                    // wedge all validation).
                    string userPick;
                    DialogResult pickRc;
                    _validationModalShowing = true;
                    try
                    {
                        pickRc = Forms.AllowedDatatypePickerForm.Show(
                            "Datatype not allowed",
                            pickMessage,
                            AllowedDatatypeService.Instance.Allowed,
                            pickPreselect,
                            pickPrefill,
                            out userPick,
                            validate: candidate => ValidateDatatypeCandidate(attr, candidate, treatAsNew),
                            lockType: pickerLockBase,
                            lockParam: pickerLockParam);
                    }
                    finally { _validationModalShowing = false; }

                    // A rename can land while the picker pumps (57 s dwell in the 2026-07-09
                    // repro: 'Pre_Abc' -> 'Pre_Abc__1070' committed mid-modal and, with both
                    // timers gated, no detector ever saw it). Catch it here so the log lines and
                    // the naming replay below run against the LIVE name.
                    renameCaught |= RefreshNameAfterModal(attr, curr, "datatype picker");

                    if (pickRc == DialogResult.OK
                        && !string.IsNullOrEmpty(userPick)
                        && !string.Equals(userPick, liveAfterWrite, StringComparison.Ordinal))
                    {
                        int pickTrans = _session.BeginNamedTransaction("EnforceAllowedDatatypeUserPick");
                        try
                        {
                            attr.Properties("Physical_Data_Type").Value = userPick;
                            _session.CommitTransaction(pickTrans);

                            string liveAfterPick = userPick;
                            try { liveAfterPick = attr.Properties("Physical_Data_Type").Value?.ToString() ?? userPick; }
                            catch (Exception reEx) { Log($"AllowedDatatype: re-read after user pick failed for {curr?.PhysicalName}: {reEx.Message}"); }
                            curr.PhysicalDataType = liveAfterPick;
                            liveAfterWrite = liveAfterPick;

                            bool pickRoundTripped = AllowedDatatypeService.Instance.IsAllowed(liveAfterPick);
                            Log($"AllowedDatatype: {curr?.TableName}.{curr?.PhysicalName} user picked '{userPick}'" +
                                $"{(string.Equals(liveAfterPick, userPick, StringComparison.Ordinal) ? "" : $" (erwin stored '{liveAfterPick}')")}" +
                                $"{(pickRoundTripped ? "" : " - WARNING: stored value still not allowed")}");
                            if (!pickRoundTripped)
                            {
                                ShowValidationModal(
                                    $"Column '{curr?.TableName}.{curr?.PhysicalName}': the picked datatype '{userPick}' could not be applied cleanly (the model now holds '{liveAfterPick}').\n\n" +
                                    $"Ask your administrator to check the Datatype Library entry for this DBMS.",
                                    "Datatype not allowed",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                                renameCaught |= RefreshNameAfterModal(attr, curr, "pick-apply warn");
                            }
                        }
                        catch (Exception pickEx)
                        {
                            try { _session.RollbackTransaction(pickTrans); } catch { /* already closed */ }
                            Log($"AllowedDatatype: user-pick write failed for {curr?.TableName}.{curr?.PhysicalName}: {pickEx.Message} - automatic value '{liveAfterWrite}' kept");
                        }
                    }

                    // Remember the value now in force (user pick or the automatic one the
                    // user accepted via Cancel) so the dedup duplicate re-enforces IT.
                    if (!string.IsNullOrEmpty(objId))
                        _allowedDatatypeUserPicks[objId] = (liveAfterWrite, DateTime.UtcNow);

                    // Re-stamp the dedup window AFTER the (blocking) modal closes so erwin's delayed
                    // 2nd combo-commit - processed only once this modal returns (the _isProcessingChange
                    // guard holds re-entrant ticks until then) - still lands inside the window even if
                    // the user dwelled on the dialog longer than TermTypeDedupSeconds.
                    if (!string.IsNullOrEmpty(objId))
                        _allowedDatatypeRecentAttempts[objId] = (attempted, DateTime.UtcNow);

                    // Post-enforcement naming replay (2026-07-07): the type-change branch of
                    // ProcessAttributeChanges already re-runs the column naming validator right
                    // after enforcement (the C3 polymorphic replay) so rules CONDITIONED on the
                    // datatype - e.g. "Date-typed columns get suffix 'Date'" (rule#1032) - react
                    // to the FINAL type. The three new-column call sites had NOTHING after
                    // Enforce, so a type settled here (picker pick, or the forced fallback the
                    // user kept via Cancel) never re-evaluated those rules: in the Column Editor
                    // the suffix only appeared on the editor-CLOSE pass (late), and via Model
                    // Explorer never (no close edge exists). Re-run the full column pass against
                    // the final type NOW - the suffix/prefix applies while the editor is still
                    // open, and the Model Explorer path no longer needs a close edge. Step 3b of
                    // the pass also re-checks Physical_Data_Type rules, so this subsumes a
                    // datatype-only warning (an earlier warning-only safety net was replaced by
                    // this replay to avoid double dialogs). Gated on isNew: the type-change site
                    // (isNew:false) already replays itself right after Enforce returns. No naming
                    // frame is active on the new-column sites (their ValidateColumnNamingStandard
                    // ran and returned BEFORE Enforce), so the reentrancy guard will not no-op us.
                    // 2026-07-09: also runs when a rename landed before/during one of the modals
                    // above (renameCaught) - curr now holds the LIVE name and the Name rules
                    // (e.g. a no-digits Regexp vs erwin's '__NNNN' uniquify) must see it; for the
                    // isNew:false type-change site the Core's IsAutoUniquifyRename baseline
                    // bridge decides Create-vs-Update semantics.
                    if (isNew || renameCaught)
                    {
                        bool discardedAfterType = ValidateColumnNamingStandard(attr, curr, isNew: isNew);
                        if (!discardedAfterType && _pendingResults.Count > 0)
                        {
                            ShowConsolidatedPopup();
                        }
                    }
                }
                else if (renameCaught)
                {
                    // Silent duplicate path (suppressPopup): no modal was shown, but the entry
                    // re-read caught a rename nothing else observed - validate it now.
                    bool discardedSilent = ValidateColumnNamingStandard(attr, curr, isNew: isNew);
                    if (isNew && !discardedSilent && _pendingResults.Count > 0) ShowConsolidatedPopup();
                }

                // erwin's auto-uniquify can land even LATER - after this method returns, with the
                // gesture drained and nothing else watching the attribute. Schedule a targeted
                // live-vs-snapshot recheck (drained by MonitorTimer) as the safety net.
                ScheduleAttributeRecheck(curr);
            }
            catch (Exception ex)
            {
                try { _session.RollbackTransaction(trans); }
                catch (Exception rbEx) { Log($"EnforceAllowedDatatypeWhitelist rollback error: {rbEx.Message}"); }
                Log($"EnforceAllowedDatatypeWhitelist write failed for {curr?.PhysicalName}: {ex.Message}");
            }
        }

        /// <summary>
        /// UI-less evaluation of the admin naming/regex rules for a column's
        /// <c>Physical_Data_Type</c> against a CANDIDATE value that is NOT (yet) written to
        /// the model. Returns the first violation's message, or <c>null</c> when the candidate
        /// passes (or no rules are loaded). Pure/read-only:
        /// <see cref="NamingValidationEngine.ValidateObjectName"/> only reads rules + the column's
        /// UDPs, so this is safe to call as the datatype picker's inline validator with no
        /// transaction. Never throws - fails OPEN (returns null) and logs, so an internal
        /// validation error can never trap the user inside the picker.
        /// </summary>
        /// <summary>
        /// The glossary-authoritative Physical_Data_Type for a column (the DATA_TYPE the
        /// glossary row maps to PROPERTY_CODE 'PHYSICAL_DATA_TYPE'), or null when the column
        /// has no glossary mapping / no datatype field. In-memory cache lookup, no DB. Used by
        /// the term-lock path when there is no prev snapshot (new-column Enforce call sites).
        /// </summary>
        private static string GetGlossaryAuthoritativeDatatype(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return null;
            try
            {
                var vals = GlossaryService.Instance.GetUdpValues(columnName);
                if (vals == null) return null;
                foreach (var kv in vals)
                {
                    if (string.Equals(kv.Key, "PHYSICAL_DATA_TYPE", StringComparison.OrdinalIgnoreCase))
                        return string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value.Trim();
                }
            }
            catch (Exception ex)
            {
                AddinLogger.Log($"GetGlossaryAuthoritativeDatatype('{columnName}') failed: {ex.Message}");
            }
            return null;
        }

        private string ValidateDatatypeCandidate(dynamic attr, string candidate, bool treatAsNew)
        {
            if (!NamingStandardService.Instance.IsLoaded) return null;
            try
            {
                object attrBoxed = attr;
                var results = NamingValidationEngine.ValidateObjectName(
                    "Column", candidate ?? string.Empty, attrBoxed, "Physical_Data_Type", isNew: treatAsNew);
                var failure = results.FirstOrDefault(r => r != null && !r.IsValid);
                return failure?.ErrorMessage;
            }
            catch (Exception ex)
            {
                Log($"ValidateDatatypeCandidate error for '{candidate}': {ex.Message}");
                return null;
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

            // If the glossary could not load because its stored connection credentials are
            // undecryptable (DPAPI is per-Windows-user, or the ciphertext is corrupt/legacy),
            // warn the user ONCE. GlossaryService latches the failure so the LoadGlossary above
            // stops hammering the DB on every column; here - on the erwin STA thread, a safe
            // place for a modal - we surface the reason a single time (TryConsumeCredentialWarning
            // clears the pending message) instead of silently skipping validation forever.
            if (glossary.TryConsumeCredentialWarning(out string glossaryCredWarning))
            {
                AddinMessageDialog.Show(
                    glossaryCredWarning,
                    "Glossary not loaded",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            if (!glossary.IsLoaded)
            {
                // Can't validate - skip
                return;
            }

            if (!glossary.HasEntry(state.PhysicalName))
            {
                // 2026-06-04 GLOSSARY_REQUIRED_OPTION enforcement (effective value cached on
                // GlossaryService; the whole feature is already gated by USE_EXTERNAL_GLOSSARY
                // - the glossary would not be IsLoaded otherwise):
                //   OPTIONAL_SILENT (default) -> accept silently: do NOT queue a result
                //                                (no popup, no rename/delete);
                //   OPTIONAL_WARNING / REQUIRED -> queue so the consolidated popup warns.
                //                                  ShowConsolidatedPopup then renames/deletes
                //                                  ONLY for REQUIRED.
                if (glossary.RequiredOption == GlossaryRequiredOption.OPTIONAL_SILENT)
                {
                    Log($"Glossary no-match accepted (OPTIONAL_SILENT): {state.TableName}.{state.PhysicalName}");
                }
                else
                {
                    Log($"Glossary validation FAILED ({glossary.RequiredOption}): {state.TableName}.{state.PhysicalName}");
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
        /// <returns>True when the attribute was DISCARDED (deleted) by a Required-field
        /// Cancel, so the caller must stop touching the now-dead COM object.</returns>
        private bool ValidateColumnNamingStandard(dynamic attr, AttributeValidationSnapshot state, bool isNew = false)
        {
            if (_validationSuspended) return false;
            if (!NamingStandardService.Instance.IsLoaded) return false;
            // Reentrancy guard (2026-06-06): the Required-field popup in the core
            // method is MODAL and pumps the message loop, re-firing the monitor
            // timers. A reentrant tick that re-validates the same column before the
            // outer call advanced the attribute snapshot stacks another modal popup,
            // then a third, ad infinitum - the exact hazard _scopedCheckInProgress
            // guards on the Table path. (Exposed by the COLUMN.Definition Step-3b,
            // which made this path raise modals where Physical_Name validation
            // rarely did, so the latent loop became visible spam.)
            if (_columnNamingCheckInProgress) return false;
            _columnNamingCheckInProgress = true;
            try { return ValidateColumnNamingStandardCore(attr, state, isNew); }
            finally { _columnNamingCheckInProgress = false; }
        }

        /// <returns>True when the attribute was DISCARDED (deleted) by a Required-field Cancel.</returns>
        private bool ValidateColumnNamingStandardCore(dynamic attr, AttributeValidationSnapshot state, bool isNew = false)
        {
            if (string.IsNullOrEmpty(state.PhysicalName) ||
                state.PhysicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                state.PhysicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Locked predefined columns are admin-controlled: their exact name is
            // part of the admin definition and "Locked" means the column must not
            // be renamed - by the user OR by a naming rule. Skip naming-standard
            // application entirely for them. Otherwise a suffix rule (e.g. _DATE on
            // DateTime columns) rewrites "CreateDate" -> "CreateDate_DATE", which
            // breaks the name-based locked-order match and gets the column wrongly
            // shoved to the end of the table (user-confirmed behaviour 2026-06-09:
            // "Muaf tut"). The check is by current name, which still equals the
            // admin name here because we run BEFORE any rename would happen.
            if (PredefinedColumnService.Instance.IsLockedColumnName(state.PhysicalName))
            {
                Log($"Column naming skipped: '{state.TableName}.{state.PhysicalName}' is a locked predefined column (admin-owned name).");
                return false;
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

            // A column added via Model Explorer is first seen with a placeholder name
            // and parked in _pendingNamedAttrs, so by the time its real name commits it
            // is no longer "new" by snapshot identity (isNew=false) even though it is a
            // brand-new column. It must count as new for ALL new-vs-existing decisions
            // here: (1) the Required-field Cancel should DISCARD it, not revert; and
            // (2) - the reason a Model-Explorer column got NO validation while the same
            // column warns in the Column Editor and new TABLES warn fine - apply=Create
            // naming rules (forbidden-word, name regex, required Definition/Datatype)
            // must be evaluated. They are gated on isNew, so at isNew=false every Create
            // rule was silently skipped. New tables keep isNew=true through their own
            // pending-entity path; columns did not, which is this whole bug. (apply=Both
            // rules fire either way, so configs using Both are unaffected.)
            // Also treat an erwin AUTO-UNIQUIFY rename as a fresh create: when the name the add-in
            // applied collides with an existing column, erwin appends "__NNNN" (e.g. 'Pre_Abc' ->
            // 'Pre_Abc__1069') AFTER our naming ran. That erwin-assigned name must be re-validated
            // as isNew so apply=Create rules (name regex, forbidden words) re-fire on it - otherwise
            // the '__NNNN' name (digits defeat a PascalCase rule) slips through, because by now the
            // pending-new signal was already consumed at the placeholder->real-name commit.
            bool treatAsNew = isNew
                || (!string.IsNullOrEmpty(snapshotId) && IsAttributePendingNew(snapshotId))
                || NamingValidationEngine.IsAutoUniquifyRename(baselinePhysicalName, state.PhysicalName);

            // 2026-07-10: split the flag. `treatAsNew` above is the IDENTITY flag - "is this a
            // genuinely new column" - and MUST stay driving the Required-popup Cancel contract
            // (Create=discard the column, Update=revert the property). `revalidateAsNew` below is
            // the VALIDATION-SCOPE flag - "should apply=Create naming rules (name regex, forbidden
            // words, prefix/suffix apply) run on this name". The user's rule (stated repeatedly:
            // "isim degisiyorsa kural kontrolleri bastan yapilmali") is that ANY real rename must
            // re-run the naming chain on the new name - not just erwin's auto-uniquify. So a manual
            // rename of an EXISTING column (Model Explorer F2 / Properties pane / Column Editor)
            // gets revalidateAsNew=true (rule#1127 no-digits fires) while treatAsNew stays false
            // (Cancel REVERTS to the old name, it does NOT delete the pre-existing column - the
            // trap the two flags being one would spring). Not retroactive: it fires only because
            // the user actively changed the name; an unchanged name keeps revalidateAsNew=false.
            bool nameChangedFromBaseline =
                !treatAsNew
                && NamingValidationEngine.RenameRequiresRevalidation(baselinePhysicalName, state.PhysicalName, IsPlaceholderColumnName);
            bool revalidateAsNew = treatAsNew || nameChangedFromBaseline;
            if (nameChangedFromBaseline)
                Log($"Column rename re-validation: '{state.TableName}.{baselinePhysicalName}' -> '{state.PhysicalName}' - re-running apply=Create naming rules (Cancel still reverts, not deletes)");
            bool attributeDeleted = false;

            // Step 1: silently apply AUTO_APPLY=true rules
            if (attrBoxed != null)
            {
                string afterAuto = NamingValidationEngine.ApplyNamingStandards("Column", state.PhysicalName, attrBoxed, autoOnly: true, isNew: revalidateAsNew);
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
                        // 2026-07-09: the displayed target is re-read LIVE, because erwin may
                        // already have auto-uniquified our write ('Pre_Abc' -> 'Pre_Abc__1073'),
                        // and the dialog must not cite a name that no longer exists.
                        string afterLive = ReadLivePhysicalName(attr, afterAuto);
                        AddinMessageDialog.Show(
                            $"Column '{state.TableName}.{state.PhysicalName}' -> '{afterLive}'",
                            "Naming standard applied",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        // The uniquify can also land WHILE the modal pumps - continue Steps 2/3
                        // with whatever erwin holds NOW, so the Name rules (no-digits Regexp etc.)
                        // fire on the real '__NNNN' name in this same gesture instead of never.
                        string afterModal = ReadLivePhysicalName(attr, afterLive);
                        if (!string.Equals(afterModal, afterAuto, StringComparison.Ordinal))
                        {
                            Log($"Column naming auto-apply: erwin adjusted '{afterAuto}' to '{afterModal}' after our write - continuing validation with the live name");
                            // Re-fire apply=Create rules on erwin's uniquified name, but DO NOT flip
                            // the identity flag: for a manual rename that collided, treatAsNew must
                            // stay false so a Required-popup Cancel reverts the name instead of
                            // deleting the pre-existing column (2026-07-10 split).
                            if (NamingValidationEngine.IsAutoUniquifyRename(afterAuto, afterModal))
                                revalidateAsNew = true;
                        }
                        state.PhysicalName = afterModal;
                        // And it can land even later, after this whole pass returned - schedule
                        // the targeted live-vs-snapshot recheck as the safety net.
                        ScheduleAttributeRecheck(state);
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
                string afterAll = NamingValidationEngine.ApplyNamingStandards("Column", state.PhysicalName, attrBoxed, autoOnly: false, isNew: revalidateAsNew);
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
                            // 2026-07-09: erwin may auto-uniquify our write; keep the snapshot on
                            // whatever it actually holds and schedule the targeted recheck so a
                            // late '__NNNN' rename still gets its Name rules.
                            state.PhysicalName = ReadLivePhysicalName(attr, afterAll);
                            ScheduleAttributeRecheck(state);
                            return false;
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
            var results = NamingValidationEngine.ValidateObjectName("Column", state.PhysicalName, attrBoxed, isNew: revalidateAsNew);
            var failures = results.Where(r => !r.IsValid).ToList();

            // Step 3b (2026-06-05): the admin can author Column rules on any
            // PROPERTY_DEF, not just Physical_Name - e.g. COLUMN.Definition with
            // Length > 0 + IS_REQUIRED ("column comment is mandatory"). Until now
            // ValidateColumnNamingStandard only ran the Physical_Name rules above,
            // so those non-name rules never fired on a column add/rename (the
            // matching Table path already does this loop - see
            // TableTypeMonitorService "Step 3b"). Mirror it here: iterate every
            // non-Physical_Name property code that has Column rules, read the live
            // value via direct SCAPI access, and accumulate violations into the
            // SAME failures pipeline so Required rules get the input dialog and the
            // rest go to the consolidated warning.
            if (attrBoxed != null)
            {
                foreach (var propertyCode in NamingStandardService.Instance.GetPropertyCodes("Column"))
                {
                    if (string.Equals(propertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                        continue; // already covered by the Physical_Name run above

                    string propValue;
                    try
                    {
                        propValue = attr?.Properties(propertyCode)?.Value?.ToString() ?? "";
                    }
                    catch (Exception ex)
                    {
                        // SCAPI may not surface the property on a freshly-added
                        // column (e.g. an unset Definition raises "does not use a
                        // property of <X> type"). That is exactly the empty state a
                        // Length > 0 / Required rule is meant to catch, so treat the
                        // unset property as an empty string and validate.
                        propValue = "";
                        Log($"Naming standard: SCAPI did not surface 'Column.{propertyCode}' on this column (treating as empty): {ex.Message}");
                    }

                    Log($"NamingValidate: 'Column.{propertyCode}' on '{state.TableName}.{state.PhysicalName}' liveValue='{propValue}' isNew={revalidateAsNew}");
                    var extraResults = NamingValidationEngine.ValidateObjectName(
                        "Column", propValue, attrBoxed, propertyCode, isNew: revalidateAsNew);
                    failures.AddRange(extraResults.Where(r => !r.IsValid));
                }
            }

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
                    // Show the OBJECT NAME (table.column) + a friendly property label,
                    // e.g. "VpE_LOG.ID (Comment)" - the raw "Column.Definition" was
                    // meaningless to the user (2026-06-06).
                    // 2026-07-09: rebuilt before EVERY dialog pass with the LIVE name - erwin's
                    // delayed auto-uniquify can rename the column while a prior dialog pumped,
                    // and the prompt must not cite a name that no longer exists.
                    string friendlyProp = NamingValidationEngine.FriendlyPropertyLabel(rf.Rule.PropertyCode);
                    string fieldLabel = $"{state.TableName}.{ReadLivePhysicalName(attr, state.PhysicalName)} ({friendlyProp})";
                    var cancelMode = treatAsNew ? Forms.RequiredOperationMode.Create : Forms.RequiredOperationMode.Update;

                    // Pre-fill the dialog with the column's current value
                    // (same rationale as the Table path).
                    string seedValue = "";
                    try { seedValue = attr?.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                    catch { seedValue = ""; }

                    // Dialog + cancel-revert re-prompt loop. On an EXISTING column
                    // the user cannot escape a Required violation by clicking Revert
                    // when the baseline is itself invalid: the reverted value is
                    // re-validated and the dialog re-opens until it satisfies every
                    // rule (the 2026-05-24 Table-path rule, now applied to columns).
                    // New columns still escape via Discard.
                    string promptMessage = rf.ErrorMessage;
                    string promptSeed = seedValue;
                    DialogResult rc = DialogResult.Cancel;
                    string typed = "";
                    while (true)
                    {
                        // Refresh the label each pass (see the 2026-07-09 note above): a prior
                        // pass's dialog pump may have let erwin's auto-uniquify rename land.
                        fieldLabel = $"{state.TableName}.{ReadLivePhysicalName(attr, state.PhysicalName)} ({friendlyProp})";
                        rc = EliteSoft.Erwin.AddIn.Forms.RequiredFieldDialog.Show(
                            title: "Required field",
                            message: promptMessage,
                            fieldLabel: fieldLabel,
                            out typed,
                            owner: null,
                            initialValue: promptSeed,
                            mode: cancelMode,
                            objectKind: "Column");

                        if (rc == DialogResult.OK && !string.IsNullOrEmpty(typed))
                            break; // user typed a value - fall through to the write logic

                        Log($"Required field dialog cancelled: {state.TableName}.{state.PhysicalName} field={fieldLabel} (mode={cancelMode})");
                        if (treatAsNew)
                        {
                            requiredCancelHandled = TryDeleteNewAttribute(attr, state.TableName, state.PhysicalName);
                            attributeDeleted = requiredCancelHandled;
                            break; // new column discarded (or delete failed) - exit loop
                        }

                        string baseline = string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase)
                            ? baselinePhysicalName
                            : (baselineWatched.TryGetValue(rf.Rule.PropertyCode, out var bv) ? bv : "");
                        requiredCancelHandled = TryRevertAttributeProperty(attr, snapshotId, state.TableName, state.PhysicalName, rf.Rule.PropertyCode, baseline);
                        if (!requiredCancelHandled)
                            break; // revert failed - drop through so other Required failures still get a chance

                        // Keep the in-flight currentState aligned with the revert so
                        // code that runs after this method does not capture the
                        // rolled-back invalid value.
                        if (string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                            state.PhysicalName = baseline ?? "";
                        state.WatchedProperties[rf.Rule.PropertyCode] = baseline ?? "";

                        // 2026-05-24 rule: re-validate the reverted value. If it still
                        // violates, re-open the dialog (NO session dismissal, so the
                        // user must supply a valid value). Only when the reverted
                        // value is valid do we dismiss + stop the chain.
                        string afterRevert;
                        try { afterRevert = attr?.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                        catch { afterRevert = baseline ?? ""; }
                        List<NamingValidationResult> revertFresh;
                        // Mirror RevalidatePropertyAfterRevert's safeguard: on an
                        // internal error treat the value as valid (revertFresh=null)
                        // so a re-validation fault can never trap the user in the loop.
                        try { revertFresh = NamingValidationEngine.ValidateObjectName("Column", afterRevert, attrBoxed, rf.Rule.PropertyCode, isNew: treatAsNew); }
                        catch (Exception rvEx) { Log($"Post-revert re-validation error for {fieldLabel}, treating as valid: {rvEx.Message}"); revertFresh = null; }
                        var revertStillFail = revertFresh?.FirstOrDefault(r => !r.IsValid);
                        if (revertStillFail != null)
                        {
                            Log($"Required field re-prompt after Cancel (post-revert still invalid): {fieldLabel}");
                            promptMessage = revertStillFail.ErrorMessage;
                            promptSeed = afterRevert;
                            continue;
                        }

                        // Reverted value passes every rule - dismiss for the session
                        // and stop. (Same dismissal the next drift tick checks against.)
                        if (!string.IsNullOrEmpty(snapshotId))
                        {
                            _dismissedRequiredColumnKeys.Add($"{snapshotId}|{rf.Rule.PropertyCode}");
                            Log($"Required field dismissed for session: {snapshotId}|{rf.Rule.PropertyCode}");
                        }
                        break;
                    }

                    if (rc != DialogResult.OK || string.IsNullOrEmpty(typed))
                    {
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
                                "Column", liveValue, attrBoxed, rf.Rule.PropertyCode, isNew: revalidateAsNew);
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
                                if (treatAsNew)
                                {
                                    requiredCancelHandled = TryDeleteNewAttribute(attr, state.TableName, state.PhysicalName);
                                    attributeDeleted = requiredCancelHandled;
                                    failures.RemoveAll(f => f.Rule != null
                                        && string.Equals(f.Rule.PropertyCode, rf.Rule.PropertyCode, StringComparison.OrdinalIgnoreCase));
                                    break; // new column discarded - escape
                                }

                                string baseline = baselineWatched.TryGetValue(rf.Rule.PropertyCode, out var bv) ? bv : "";
                                requiredCancelHandled = TryRevertAttributeProperty(attr, snapshotId, state.TableName, state.PhysicalName, rf.Rule.PropertyCode, baseline);
                                if (!requiredCancelHandled)
                                {
                                    failures.RemoveAll(f => f.Rule != null
                                        && string.Equals(f.Rule.PropertyCode, rf.Rule.PropertyCode, StringComparison.OrdinalIgnoreCase));
                                    break; // revert failed - give up this property
                                }

                                if (string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                                    state.PhysicalName = baseline ?? "";
                                state.WatchedProperties[rf.Rule.PropertyCode] = baseline ?? "";
                                currentTyped = baseline ?? "";

                                // 2026-05-24 rule (mirror of SITE 1): re-validate the
                                // reverted value. Still invalid -> re-prompt via the loop
                                // top (no dismissal). Valid -> record the session
                                // dismissal (parity with SITE 1 / the Table path) + drop
                                // the failure + stop. On a re-validation fault treat as
                                // valid so a fault cannot trap the user.
                                string afterRevert2;
                                try { afterRevert2 = attr?.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                                catch { afterRevert2 = baseline ?? ""; }
                                List<NamingValidationResult> revertFresh2;
                                try { revertFresh2 = NamingValidationEngine.ValidateObjectName("Column", afterRevert2, attrBoxed, rf.Rule.PropertyCode, isNew: treatAsNew); }
                                catch (Exception rvEx2) { Log($"Post-revert re-validation error for {fieldLabel}, treating as valid: {rvEx2.Message}"); revertFresh2 = null; }
                                if (revertFresh2?.Any(r => !r.IsValid) == true)
                                    continue; // still invalid -> loop top re-prompts

                                if (!string.IsNullOrEmpty(snapshotId))
                                {
                                    _dismissedRequiredColumnKeys.Add($"{snapshotId}|{rf.Rule.PropertyCode}");
                                    Log($"Required field dismissed for session: {snapshotId}|{rf.Rule.PropertyCode}");
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
                return attributeDeleted;
            }

            foreach (var r in failures)
            {
                Log($"Naming standard violation ({r.RuleName}): {state.TableName}.{state.PhysicalName} — {r.ErrorMessage}");
                _pendingResults.Add(new CollectedValidationResult
                {
                    ValidationType = CollectedValidationResultType.NamingStandard,
                    TableName = state.TableName,
                    ColumnName = state.PhysicalName,
                    Message = r.ErrorMessage,
                    // 2026-07-09: carry the live handle so the consolidated popup can re-resolve
                    // the CURRENT name at display time (erwin's auto-uniquify may rename the
                    // column between queueing and the flush at editor close).
                    Attribute = attr,
                    ObjectId = state.ObjectId
                });
            }

            return false;
        }

        /// <summary>
        /// True when the column is a brand-new one parked in <see cref="_pendingNamedAttrs"/>
        /// - added via Model Explorer with a placeholder name, awaiting its real name.
        /// By snapshot identity such a column is no longer "new" once its name commits
        /// (it was recorded in _attributeSnapshots during the placeholder phase), but for
        /// the Required-field Cancel it must still count as new: Cancel should DISCARD the
        /// just-created column, not revert a property on a column the user never meant to
        /// keep. This is exactly the "Revert Change vs Discard New Column" split.
        /// </summary>
        private bool IsAttributePendingNew(string objectId)
        {
            if (string.IsNullOrEmpty(objectId)) return false;
            foreach (var pendSet in _pendingNamedAttrs.Values)
                if (pendSet.Contains(objectId)) return true;
            return false;
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
                        if (string.IsNullOrEmpty(kvp.Value))
                        {
                            // Locked field with no glossary value for this term (miss): do not
                            // fabricate an empty value and do not fail the apply - just flag it
                            // (never swallow silently).
                            if (glossary.GetIsLocked(kvp.Key))
                                Log($"Glossary: locked field '{kvp.Key}' has no value for '{columnName}' - left unset (not fabricated).");
                            continue;
                        }

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
                    Message = message,
                    // 2026-07-09: live handle for display-time name re-resolution (see the
                    // NamingStandard queue note).
                    Attribute = attr,
                    ObjectId = state.ObjectId
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

        /// <summary>
        /// Column name to DISPLAY for a queued result: prefers a live re-read via the stored
        /// COM handle, because erwin's delayed auto-uniquify can rename the column between
        /// queueing and the popup flush (editor close). Falls back to the frozen string when no
        /// handle was stored (PK/Index results) or the object is gone.
        /// </summary>
        private string LiveColumnNameFor(CollectedValidationResult result)
        {
            if (result?.Attribute == null) return result?.ColumnName ?? string.Empty;
            return ReadLivePhysicalName(result.Attribute, result.ColumnName ?? string.Empty);
        }

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
                        sb.AppendLine($"Column: {LiveColumnNameFor(result)}");
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
                        sb.AppendLine($"Column: {LiveColumnNameFor(result)}");
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
                        sb.AppendLine($"Column: {LiveColumnNameFor(result)}");
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
                // 2026-06-04 GLOSSARY_REQUIRED_OPTION: the destructive rename-to-
                // "PLEASE CHANGE IT" / delete is the REQUIRED enforcement ONLY.
                // OPTIONAL_WARNING shows the warning popup above but ACCEPTS the value
                // (no rename/delete); OPTIONAL_SILENT never queued a glossary result so
                // it never reaches here. Domain/naming results are unaffected.
                var glossaryMode = GlossaryService.Instance.RequiredOption;
                if (glossaryResults.Count > 0 && glossaryMode == GlossaryRequiredOption.REQUIRED)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool editorOpen = !string.IsNullOrEmpty(_activeColumnEditorTable);
                    Log($"[POPUP] OK clicked - REQUIRED: dispatching {(editorOpen ? "rename" : "delete")} for {glossaryResults.Count} column(s)");
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
                else if (glossaryResults.Count > 0)
                {
                    Log($"[POPUP] OK clicked - glossary mode {glossaryMode}: value ACCEPTED, no rename/delete for {glossaryResults.Count} column(s)");
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

                // ENTITY-SCOPED: only the predefined columns that actually apply to THIS entity
                // (unconditional + conditional rows whose gating UDP matches, e.g. TableClass='Log').
                // The old code used GetAll() across ALL table classes, so a column predefined for
                // 'Parametre' (e.g. "OID") was wrongly treated as predefined on a 'Log' table and
                // skipped from the glossary even though the user added it as a real column.
                var names = PredefinedColumnService.Instance.GetApplicableNames(entity);

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

        // Verbose, per-column-change diagnostic logging ([PK-WALK], [TEMPLATE-COND]).
        // The [Conditional] attribute makes the C# compiler omit every call to this
        // method - and the evaluation of its interpolated arguments - from builds
        // where DEV_DIAGNOSTICS is undefined, i.e. packaged (production) builds. So
        // these traces aid development without ever reaching the shipped log file.
        [System.Diagnostics.Conditional("DEV_DIAGNOSTICS")]
        private void LogDebug(string message) => Log(message);

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
