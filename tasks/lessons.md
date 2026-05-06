# Lessons Learned

A running log of corrections and non-obvious findings that future sessions
should not have to rediscover. Each entry is a short rule, the reason, and
how to apply it.

## 2026-05-05: PU.Locator is unreliable on r10.10 Mart-bound PUs

**Rule:** never read `pu.Locator?.ToString()` directly when the model lives
on a Mart server. Always go through `Services/PuLocatorReader.cs`.

**Why:** verified against `Mart://Mart/Kursat/MetaRepo` on r10.10 - the
direct property returned `""` even though the model was loaded and the title
bar showed the full mart path. Downstream services that scope on
`MART_PATH` (ConfigContext, Glossary, NamingStandard, PredefinedColumn,
UdpDefinition) silently misclassify the model as a local-file model and
the add-in surfaces "Active model is not on a Mart server" before any of
them can run.

**How to apply:** the helper performs four cascading reads (direct,
`PropertyBag()`, `PropertyBag(null,true)`, main-window title regex). It logs
the failed layers, so if the user reports an empty locator we can see which
fallback finally caught it. `VersionCompareService.ReadActiveLocator` is the
reference DRY consumer.

## 2026-05-05: Form.Shown can fire on a disposed form

**Rule:** every `Form.Shown` / `Form.Load` / `Form.Activated` handler that
runs as a side effect of `Show()` must guard with `if (!form.IsDisposed)`
before touching the form.

**Why:** when the form's synchronous init path (constructor, Load, or a
connect handler driven from `Show()`) hits a failure and calls `Close()` /
`ForceClose()`, the form is disposed before `Show()` returns. The Shown
event still fires post-dispose, raising `ObjectDisposedException` from
inside our handler. The exception bubbles to `ErwinAddIn.Execute()`'s catch
and is reported as "Add-In Error: Cannot access a disposed object", which
masks the real failure that triggered the dispose in the first place.

**How to apply:** existing audit point is
[ErwinAddIn.cs:157](../ErwinAddIn.cs#L157) where the TopMost-reset handler
already has the guard. Any new lifecycle-event subscription on a long-lived
form follows the same pattern.

## 2026-05-05: MART_PATH stem must match admin's parser exactly

**Rule:** when extracting the mart path from a locator, use the same regex
as `VersionCompareService.BuildMartLocatorForTarget`:
`Mart://Mart/(?<path>[^?&]+?)(?:[?&]|$)` with `Trim('/')`. The shared
implementation lives in `ConfigContextService.ParseMartPath`.

**Why:** admin's `ModelMappingService.GetByMartPath` does an exact-match
string compare against the value built by `BrowserPanel.BuildPath`
(e.g. `Kursat/MetaRepo`, no leading slash, no trailing slash). The first
draft of `ParseMartPath` only stopped at `?` and only `TrimEnd`'d the
trailing slash, missing two edge cases observed in the wild: locators that
use `&version=N` instead of `?VNO=N`, and a leading slash from
`Mart://Mart//<lib>/<model>`.

**How to apply:** unit test coverage in
[tests/ErwinAddIn.Tests/ConfigContextServiceTests.cs](../tests/ErwinAddIn.Tests/ConfigContextServiceTests.cs)
codifies the seven accepted shapes plus six rejection cases. Add a new
inline data row before changing the regex.

## 2026-05-06: Validation pipeline must be reactive, not periodic

**Rule:** never build the validation pipeline around a periodic full-model
scan. Tie validation work to actual user actions (editor open, model
change events) instead.

**Why:** the original ValidationCoordinator walked all 280 entities * 30
attrs = 8400 attribute properties every cycle, in 5-entity 500ms tick
batches. On the SQL_BUYUKMODEL big model this saturated the STA thread:
each tick was ~450ms of COM work in a 500ms slot, leaving ~10% breathing
room. The user's complaint that "tabloları select edemiyorum" (I cannot
select tables) was the periodic walk. Worst-case popup latency equals
total cycle time (~19s on a 30-entity batch) - no tick-interval tweak
fixes that, only structural change.

**How to apply:** the final design (Phase-2D) is purely reactive. Per-table
silent populate fires when the user opens a Column Editor for that table.
The MonitorTimer scoped path validates only that one entity. Editor
closed = MonitorTimer is idle. Any future "validate everything" feature
must run on user demand (button), not on a timer.

## 2026-05-06: Editor-close popup runs DURING WindowMonitor tick

**Rule:** when a `MessageBox.Show` modal is up, other timers on the UI
thread continue to fire (the modal pumps messages internally). Code that
runs on close-transition cannot assume the popup's outcome has already
been applied.

**Why:** Phase-2C's `DeletePleaseChangeItColumns` ran from
`WindowMonitorTimer.Tick` on close detection. With a popup still showing
the validation FAILED message, the WindowMonitor tick fires, walks the
table for PLEASE CHANGE IT placeholders - finds zero (the rename happens
later when user clicks Yes on popup) - exits cleanly. The renamed
placeholder then survives forever.

**How to apply:** dispatch on observable state at the action site, not on
a follow-up timer. Phase-2D's fix routes the rename/delete decision
inside `ShowConsolidatedPopup` itself, using `_activeColumnEditorTable`
(captured at popup-OK time) to choose: editor-still-open -> rename to
PLEASE CHANGE IT placeholder, editor-closed -> delete directly. No race.

## 2026-05-07: License/anti-tamper on background thread = false positive

**Rule:** never run `LicensingService.Initialize` on a thread-pool worker.
License check must stay on the UI thread.

**Why:** Phase-3C tried to overlap the ~700ms license check with the
SCAPI activation + form constructor by wrapping it in `Task.Run`. The
first paying-user run reported a tampering-detected status and refused
to load. `LicensingService` runs `AntiTamper.CheckGroup1_Debugger` and
`CheckGroup2_Timing` - timing fingerprints and debugger-detection logic
that misfire on a non-UI thread context inside erwin's host process.
The ~250ms saving was not worth the false-positive risk on a security
check that gates all add-in loads.

**How to apply:** keep license validation sequential at the top of
`Execute()`. If startup parallelization is needed, only background
work that is provably context-independent (file I/O, DB queries that
don't touch process state) qualifies.

## 2026-05-07: Discovery loops with COM-property dynamic dispatch are taxes

**Rule:** never write a "try every name" probe loop over COM properties
to discover something at runtime. Each failed `attr.Properties("X").Value`
costs ~50ms (COM exception marshaling). Loops of 10-20 names compound
to 700-900ms of pure tax with no signal.

**Why:** `ReadModelPath` probed 9 PersistenceUnit + 7 root + 3 session
properties looking for a model path string that always fell through to
`root.Name`. Logs showed an 849ms gap between two adjacent log lines -
that gap was the failed-property loop. Removing the loop entirely
reclaimed the time.

**How to apply:** if you genuinely don't know which property holds the
value, run the probe ONCE, log the winning property name, then hard-code
the lookup. Don't ship the discovery loop to production.

## 2026-05-07: User intuition beats premature structural optimization

**Rule:** when the user says "this should be simpler / there must be an
easier way", stop and re-examine the problem from their angle before
adding more layers of indirection.

**Why:** Phase-1A through Phase-2C built a chunked silent populate
pipeline, fingerprint pass, scoped editor scan - all atop the assumption
that the validation pipeline needed a model-wide baseline. The user
asked: "objenin durumu snapshot edilse sadece?" (snapshot only the
selected object). That single sentence was the right architecture -
per-table lazy baseline, no global walk - and was hiding inside the
existing Phase-2C scoped scan code. Took ~5 lines to wire up. The earlier
elaborate work would have been unnecessary if I had understood that
framing earlier.

**How to apply:** before deepening a complex implementation, ask:
"what is the minimum set of objects this user actually cares about
right now?" If the answer is "the one they're editing", scope to that
and skip the global state.

## 2026-05-07: Performance variability hides real wins in single-run measurements

**Rule:** never claim a perf change is a regression based on a single
startup measurement. DB cold-start, COM lazy-init, network jitter
contribute ±1-2s of run-to-run variance on this codebase.

**Why:** Phase-3 optimizations measurably eliminated ~1900ms of work
(ValidationCoord 1470->27ms, MODEL_PATH probe gone, DB pre-warm
overlap), but a single run logged 6766ms vs 6228ms baseline - looking
worse on the surface. The hidden delta was a 1358ms LoadOpenModels
gap and a 1043ms LoadTablesComboBox spike that had nothing to do with
the changes - just cold COM. Without averaging across runs the wins
were invisible.

**How to apply:** when measuring startup, take 3-5 consecutive runs and
report the median (or report the min, since variability is one-sided
upward). Per-component scopes (`AddinLogger.BeginScope`) make the
component-level wins visible even when the total moves around.
