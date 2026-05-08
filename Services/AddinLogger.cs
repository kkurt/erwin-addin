using System;
using System.Diagnostics;
using System.IO;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Static, thread-safe file logger usable from the earliest entry point
    /// (before <c>ModelConfigForm</c> exists). Writes to the same path as
    /// <c>ModelConfigForm.Log</c> (<c>%TEMP%\erwin-addin-debug.log</c>) so the
    /// load timeline and runtime messages share one timestamp-ordered file.
    /// Each entry is annotated with elapsed milliseconds since the last
    /// <see cref="StartSession"/> call so phase durations are obvious without
    /// subtracting timestamps by hand.
    /// </summary>
    internal static class AddinLogger
    {
        /// <summary>Same path as <c>ModelConfigForm._addinLogPath</c>.</summary>
        public static readonly string FilePath =
            Path.Combine(Path.GetTempPath(), "erwin-addin-debug.log");

        private static readonly object _gate = new();
        private static readonly Stopwatch _swSession = new();
        private static int _sessionId;

        /// <summary>
        /// Marks the start of an add-in load. Restarts the elapsed counter
        /// and writes a banner so re-loads are visually separable in the
        /// shared log file. Safe to call multiple times - each call begins
        /// a new session block.
        /// </summary>
        public static void StartSession()
        {
            try
            {
                lock (_gate)
                {
                    var p = Process.GetCurrentProcess();
                    int sid = ++_sessionId;
                    var banner =
                        "\r\n" +
                        "================================================================\r\n" +
                        $"=== ADDIN LOAD START  {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  (session #{sid})\r\n" +
                        $"=== PID={p.Id}  Host={p.ProcessName}  ManagedThreadId={Environment.CurrentManagedThreadId}\r\n" +
                        "================================================================\r\n";
                    File.AppendAllText(FilePath, banner);
                    _swSession.Restart();
                }
            }
            catch { /* logging must never throw */ }
        }

        /// <summary>
        /// Append a single line. Adds wall-clock timestamp and elapsed
        /// milliseconds since <see cref="StartSession"/>; safe to call
        /// before <c>StartSession</c> (elapsed reads as 0).
        /// </summary>
        public static void Log(string message)
        {
            try
            {
                lock (_gate)
                {
                    long elapsed = _swSession.IsRunning ? _swSession.ElapsedMilliseconds : 0;
                    var line = $"[{DateTime.Now:HH:mm:ss.fff}] [+{elapsed,6}ms] {message}\r\n";
                    File.AppendAllText(FilePath, line);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"AddinLogger.Log failed: {ex.Message}"); }
        }

        /// <summary>
        /// Verbose / development-only line. Compiled away in PACKAGED
        /// builds (and any non-DEBUG configuration). Use this for traces
        /// that are useful while iterating but pollute the shipped log
        /// file: per-row glossary parse traces, per-refresh connection
        /// summaries, per-property COM probes. Real one-line events
        /// (load completed, validation failure, exception) stay on
        /// <see cref="Log(string)"/>.
        /// </summary>
        [Conditional("DEBUG")]
        public static void LogDebug(string message) => Log(message);

        /// <summary>
        /// Open a timed scope. In DEBUG builds logs <c>&gt;&gt;&gt; name</c>
        /// on entry and <c>&lt;&lt;&lt; name took Xms</c> on dispose so phase
        /// boundaries and durations are explicit in the log. In PACKAGED
        /// (production) builds returns a no-op disposable - the 40+ scopes
        /// across the connect cycle would otherwise dominate the shipped
        /// log file with noise that is only meaningful during development.
        /// Real one-line events (load completed, validation failure,
        /// exception) still go through <see cref="Log(string)"/>.
        /// </summary>
        public static IDisposable BeginScope(string name)
        {
#if PACKAGED
            return NoOpScope.Instance;
#else
            return new ScopedTimer(name);
#endif
        }

#if !PACKAGED
        private sealed class ScopedTimer : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            private bool _disposed;

            public ScopedTimer(string name)
            {
                _name = name;
                Log($">>> {_name}");
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _sw.Stop();
                Log($"<<< {_name} took {_sw.ElapsedMilliseconds}ms");
            }
        }
#else
        private sealed class NoOpScope : IDisposable
        {
            public static readonly NoOpScope Instance = new NoOpScope();
            private NoOpScope() { }
            public void Dispose() { }
        }
#endif
    }
}
