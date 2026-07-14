using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using EliteSoft.MetaAdmin.Shared.Data;
using EliteSoft.MetaAdmin.Shared.Data.Entities;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Best-effort user-session tracking + remote shutdown.
    ///
    /// One ADDIN_SESSION row per erwin process (a "run"): the add-in INSERTs a
    /// row on startup, refreshes LAST_SEEN on every heartbeat, reads the
    /// admin-issued SHUTDOWN_TYPE, and stamps END_TIME when the process exits.
    /// The admin tool's User Management screen lists these rows and issues a
    /// shutdown by writing SHUTDOWN_TYPE (GRACEFUL / FORCE); the add-in obeys it
    /// on its next poll. SHUTDOWN_TYPE / SHUTDOWN_REQUESTED_* are admin-owned -
    /// this service NEVER writes them. It only ever writes
    /// START_TIME / LAST_SEEN / END_TIME (plus the static identity columns on
    /// INSERT). EF change-tracking guarantees each SaveChanges UPDATEs only the
    /// column we touched (LAST_SEEN or END_TIME), so a concurrent admin
    /// SHUTDOWN_TYPE write is never clobbered.
    ///
    /// Design notes:
    /// - Process-level singleton (mirrors <see cref="DatabaseService.Instance"/>).
    ///   <see cref="Start"/> is idempotent: the session spans the whole erwin
    ///   process and survives model switches - it does NOT open a new row per
    ///   model.
    /// - All DB work uses EF (<see cref="RepoDbContext"/>), exactly like the
    ///   CORPORATE_PROPERTY reads in ConfigContextService. EF handles dialect +
    ///   identity, so there is no hand-written SQL here.
    /// - Everything runs OFF erwin's STA UI thread (Task.Run init/reload,
    ///   heartbeat on a <see cref="System.Timers.Timer"/> threadpool tick) so a
    ///   slow DB can never hang the host.
    /// - Best-effort: every DB call is wrapped; a failure is logged (never
    ///   swallowed) and tracking simply degrades - it must NEVER block modeling.
    /// - <see cref="ReloadSettings"/> re-reads USER_TRACKING_INTERVAL_MINUTES and
    ///   re-applies the interval at runtime (wired into the add-in's Reload
    ///   Config). The interval used to be read once at startup, so a mid-session
    ///   change was silently ignored.
    /// - END_TIME is stamped via <see cref="NotifyHostClosing"/>, called from the
    ///   add-in form's closing event - a managed hook that erwin's shutdown
    ///   raises reliably. AppDomain.ProcessExit is kept only as a fallback: it is
    ///   NOT raised dependably by erwin's native COM-host teardown (a manual
    ///   erwin close was observed leaving END_TIME NULL). FORCE additionally
    ///   stamps END_TIME explicitly before Environment.Exit.
    /// </summary>
    public sealed class SessionTrackingService
    {
        private const string KeyIntervalMinutes = "USER_TRACKING_INTERVAL_MINUTES";
        private const int DefaultIntervalMinutes = 5;

        private static readonly object _instanceLock = new object();
        private static SessionTrackingService _instance;

        public static SessionTrackingService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new SessionTrackingService();
                    }
                }
                return _instance;
            }
        }

        private SessionTrackingService() { }

        // 0/1 guards (Interlocked) so Start / tick are safe across threads.
        private int _started;
        private int _tickRunning;

        private readonly object _applyLock = new object(); // serialises startup vs reload
        private System.Timers.Timer _timer;
        private int? _sessionId;            // ADDIN_SESSION.ID of our row (set once after INSERT)
        private volatile bool _stopped;     // timer torn down
        private volatile bool _gracefulPosted; // graceful WM_CLOSE already requested for the current command

        private readonly object _endLock = new object();
        private bool _endWritten;           // END_TIME stamped exactly once

        /// <summary>The action implied by a SHUTDOWN_TYPE cell value.</summary>
        public enum ShutdownAction { None, Graceful, Force }

        /// <summary>
        /// Pure mapping from a SHUTDOWN_TYPE string to the action to take.
        /// NULL / blank / unknown -> None (admin may have cancelled the command;
        /// we only ever act on a recognised non-empty value). Case-insensitive.
        /// </summary>
        public static ShutdownAction DecideShutdownAction(string shutdownType)
        {
            if (string.IsNullOrWhiteSpace(shutdownType)) return ShutdownAction.None;
            string s = shutdownType.Trim();
            if (s.Equals(AddinSession.ShutdownTypes.Graceful, StringComparison.OrdinalIgnoreCase))
                return ShutdownAction.Graceful;
            if (s.Equals(AddinSession.ShutdownTypes.Force, StringComparison.OrdinalIgnoreCase))
                return ShutdownAction.Force;
            return ShutdownAction.None;
        }

        /// <summary>
        /// Pure resolution of the effective heartbeat interval (minutes) from the
        /// raw USER_TRACKING_INTERVAL_MINUTES value. Absent / blank / non-numeric /
        /// non-positive all fall back to <see cref="DefaultIntervalMinutes"/>: the
        /// interval only sets the polling period, it never disables tracking.
        /// User-session tracking runs by default (the former USER_TRACKING_ENABLED
        /// gate was removed), so this resolver always yields a usable period.
        /// </summary>
        public static int ResolveIntervalMinutes(string intervalRaw)
        {
            int minutes = ConfigContextService.ParseEffectiveInt(intervalRaw, DefaultIntervalMinutes);
            return minutes <= 0 ? DefaultIntervalMinutes : minutes;
        }

        /// <summary>
        /// Starts tracking once per process. Idempotent and non-blocking: the
        /// corporate resolve + INSERT all run on a background task so erwin
        /// startup is never delayed.
        /// </summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return; // already started this process

            try
            {
                // Fallback END_TIME hook (NotifyHostClosing is the primary one).
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                Task.Run(() => ApplySettings("startup"));
            }
            catch (Exception ex)
            {
                Log($"Start failed (best-effort, tracking off): {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-reads USER_TRACKING_INTERVAL_MINUTES and reconciles the running
        /// tracker (new interval). Wired into the add-in's Reload Config so an
        /// admin settings change takes effect without restarting erwin.
        /// Non-blocking (background task).
        /// </summary>
        public void ReloadSettings()
        {
            // If reload somehow precedes Start, behave like a start (hook exit).
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                try { AppDomain.CurrentDomain.ProcessExit += OnProcessExit; }
                catch (Exception ex) { Log($"ReloadSettings ProcessExit hook error: {ex.Message}"); }
            }

            try { Task.Run(() => ApplySettings("reload")); }
            catch (Exception ex) { Log($"ReloadSettings dispatch failed (best-effort): {ex.Message}"); }
        }

        /// <summary>
        /// Reads the interval, ensures the session row exists, and (re)starts the
        /// heartbeat timer at the current interval. Tracking runs by default (no
        /// enable gate). Serialised so a startup and a reload can never interleave.
        /// </summary>
        private void ApplySettings(string reason)
        {
            lock (_applyLock)
            {
                try
                {
                    var config = DatabaseService.Instance.GetConfig();
                    if (config == null || !config.IsConfigured)
                    {
                        Log($"{reason}: repo DB not configured - tracking inactive.");
                        return;
                    }

                    int? corporateId;
                    string intervalRaw;
                    using (var ctx = new RepoDbContext(config))
                    {
                        // Single MC_CORPORATE row (lowest ID if several, none -> stop).
                        corporateId = ctx.Corporates
                            .OrderBy(c => c.Id)
                            .Select(c => (int?)c.Id)
                            .FirstOrDefault();
                        if (corporateId == null)
                        {
                            Log($"{reason}: no MC_CORPORATE row - tracking inactive.");
                            return;
                        }

                        // User-session tracking runs by default. The former
                        // USER_TRACKING_ENABLED gate was removed - only the interval
                        // is read now, and it just sets the heartbeat period (it
                        // never disables tracking).
                        intervalRaw = ReadCorporateProperty(ctx, corporateId.Value, KeyIntervalMinutes);
                    }

                    int minutes = ResolveIntervalMinutes(intervalRaw);

                    if (_sessionId == null)
                        InsertSession(corporateId);

                    if (_sessionId != null)
                    {
                        RestartTimer(minutes);
                        Log($"{reason}: tracking active (session {_sessionId}, corporate {corporateId}, interval {minutes}min).");
                    }
                }
                catch (Exception ex)
                {
                    Log($"{reason}: ApplySettings failed (best-effort): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void InsertSession(int? corporateId)
        {
            var config = DatabaseService.Instance.GetConfig();
            if (config == null) return;

            int pid = CurrentProcessId();
            string version = GetAppVersion();
            string user = Environment.UserName ?? "";
            string machine = Environment.MachineName ?? "";

            using (var ctx = new RepoDbContext(config))
            {
                var session = new AddinSession
                {
                    CorporateId = corporateId,
                    WindowsUser = Truncate(user, 128),
                    MachineName = Truncate(machine, 128),
                    ProcessId = pid,
                    AppVersion = Truncate(version, 50),
                    StartTime = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                };
                ctx.AddinSessions.Add(session);
                ctx.SaveChanges();
                _sessionId = session.Id; // EF populates the IDENTITY id
            }

            Log($"session started: id={_sessionId}, corporate={corporateId}, user='{user}', machine='{machine}', pid={pid}, version='{version}'.");
        }

        private static string ReadCorporateProperty(RepoDbContext ctx, int corporateId, string key)
        {
            return ctx.CorporateProperties
                .Where(p => p.CorporateId == corporateId && p.Key == key)
                .Select(p => p.Value)
                .FirstOrDefault();
        }

        private void RestartTimer(int minutes)
        {
            StopTimer();
            _stopped = false;
            _gracefulPosted = false;
            var t = new System.Timers.Timer(minutes * 60_000.0) { AutoReset = true };
            t.Elapsed += (_, _) => OnHeartbeat();
            _timer = t;
            t.Start();
        }

        /// <summary>
        /// Heartbeat tick (threadpool thread). Refreshes LAST_SEEN, reads the
        /// admin SHUTDOWN_TYPE, and acts on it. Re-entrancy guarded and fully
        /// wrapped - a tick failure is logged and the next tick simply retries.
        /// </summary>
        private void OnHeartbeat()
        {
            if (_stopped) return;
            if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0)
                return; // a previous (slow) tick is still running - skip this one

            try
            {
                if (_sessionId == null) return;

                var config = DatabaseService.Instance.GetConfig();
                if (config == null) return;

                string shutdownType;
                using (var ctx = new RepoDbContext(config))
                {
                    var row = ctx.AddinSessions.FirstOrDefault(s => s.Id == _sessionId.Value);
                    if (row == null)
                    {
                        Log($"session row {_sessionId} no longer present - stopping heartbeat.");
                        StopTimer();
                        return;
                    }

                    shutdownType = row.ShutdownType;
                    row.LastSeen = DateTime.UtcNow;
                    ctx.SaveChanges();
                }

                switch (DecideShutdownAction(shutdownType))
                {
                    case ShutdownAction.None:
                        _gracefulPosted = false; // allow a freshly re-issued GRACEFUL to prompt again
                        break;

                    case ShutdownAction.Graceful:
                        // Post WM_CLOSE once per command (no nag). Keep heartbeating
                        // so a user-cancelled Save? prompt does not leave a stale
                        // session and a later FORCE is still obeyed.
                        if (!_gracefulPosted)
                        {
                            _gracefulPosted = true;
                            Log($"admin GRACEFUL shutdown for session {_sessionId} - requesting erwin close.");
                            GracefulClose();
                        }
                        break;

                    case ShutdownAction.Force:
                        Log($"admin FORCE shutdown for session {_sessionId} - exiting now.");
                        ForceClose();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"heartbeat failed (best-effort): {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        /// <summary>
        /// Graceful: post WM_CLOSE to erwin's main frame. erwin raises its own
        /// "Save changes?" prompt for any dirty model, so the user keeps control
        /// of their work. END_TIME is stamped by <see cref="NotifyHostClosing"/>
        /// when erwin actually closes.
        /// </summary>
        private void GracefulClose()
        {
            try
            {
                if (!Win32Helper.CloseErwinMainWindow())
                    Log("graceful close: erwin main window not found (will retry on next command).");
            }
            catch (Exception ex)
            {
                Log($"graceful close failed (best-effort): {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Force: stamp END_TIME, then exit the process immediately with no Save?
        /// prompt (unsaved work is lost - that is the admin's intent for FORCE).
        /// </summary>
        private void ForceClose()
        {
            WriteEndTime();
            StopTimer();
            try
            {
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log($"force exit failed (best-effort): {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Primary END_TIME hook: called from the add-in form's closing event
        /// when erwin (or Windows) shuts the add-in down. Bounded so an
        /// unreachable repo cannot hang the host's shutdown.
        /// </summary>
        public void NotifyHostClosing()
        {
            try
            {
                var t = Task.Run(() => { WriteEndTime(); StopTimer(); });
                if (!t.Wait(TimeSpan.FromSeconds(3)))
                    Log("NotifyHostClosing: END_TIME write did not finish within 3s (best-effort).");
            }
            catch (Exception ex)
            {
                Log($"NotifyHostClosing failed (best-effort): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            // Fallback only - erwin's native teardown does not raise this
            // dependably. Idempotent, so harmless after NotifyHostClosing / FORCE.
            WriteEndTime();
            StopTimer();
        }

        /// <summary>Stamps END_TIME exactly once. Never touches SHUTDOWN_TYPE (admin-owned).</summary>
        private void WriteEndTime()
        {
            lock (_endLock)
            {
                if (_endWritten) return;
                _endWritten = true;
            }

            try
            {
                if (_sessionId == null) return;

                var config = DatabaseService.Instance.GetConfig();
                if (config == null) return;

                using (var ctx = new RepoDbContext(config))
                {
                    var row = ctx.AddinSessions.FirstOrDefault(s => s.Id == _sessionId.Value);
                    if (row != null)
                    {
                        row.EndTime = DateTime.UtcNow;
                        ctx.SaveChanges();
                    }
                }
                Log($"session {_sessionId} END_TIME stamped.");
            }
            catch (Exception ex)
            {
                Log($"WriteEndTime failed (best-effort): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void StopTimer()
        {
            _stopped = true;
            try
            {
                var t = _timer;
                _timer = null;
                if (t != null)
                {
                    t.Stop();
                    t.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log($"StopTimer failed (best-effort): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static int CurrentProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        private static string GetAppVersion()
        {
            try
            {
                var asm = typeof(SessionTrackingService).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info;
                return asm.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                // Version is cosmetic metadata; an unreadable attribute must not
                // abort tracking. Fall back to a marker rather than throw.
                return "unknown";
            }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

        private static void Log(string message) => AddinLogger.Log($"SessionTracking: {message}");
    }
}
