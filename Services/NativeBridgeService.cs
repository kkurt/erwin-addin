using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Phase A spike: loads ErwinNativeBridge.dll into erwin.exe's process and
    /// calls its InstallHook() export. The bridge installs
    /// ECX::SetSynchronizeModelCallback so erwin invokes our native callback
    /// whenever it runs a Complete Compare / Resolve Differences synchronize.
    ///
    /// Log output: %TEMP%\erwin-native-bridge.log
    ///
    /// This is a one-way install per process - once loaded the DLL stays
    /// resident. Call Install() once at add-in startup (safe to call twice,
    /// idempotent via HMODULE caching).
    /// </summary>
    internal static class NativeBridgeService
    {
        private const string BridgeDllName = "ErwinNativeBridge.dll";

        private static IntPtr _bridgeModule = IntPtr.Zero;
        private static bool _installed;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string procName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InstallHookFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetLastCapturedModelSetFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ResetCapturedModelSetFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GenerateAlterDdlFromCapturedFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeDdlBufferFn(IntPtr buf);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InstallObserverHookFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ConsumeLastCapturedDdlFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ClearCapturedDdlFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CallInvokePreviewFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GenerateAlterDdlStandaloneFn(IntPtr clientMs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr OpenAlterScriptWizardHiddenFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseHiddenWizardFn(IntPtr hwnd);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetCapturedFEWPageOptionsFn();

        // Cached delegates, populated after Install() succeeds.
        private static GetLastCapturedModelSetFn _getLastCapturedModelSet;
        private static ResetCapturedModelSetFn _resetCapturedModelSet;
        private static GenerateAlterDdlFromCapturedFn _generateAlterDdl;
        private static FreeDdlBufferFn _freeDdlBuffer;
        private static InstallObserverHookFn _installObserverHook;
        private static ConsumeLastCapturedDdlFn _consumeLastDdl;
        private static ClearCapturedDdlFn _clearCapturedDdl;
        private static CallInvokePreviewFn _callInvokePreview;
        private static GenerateAlterDdlStandaloneFn _generateAlterStandalone;
        private static OpenAlterScriptWizardHiddenFn _openHiddenWizard;
        private static CloseHiddenWizardFn _closeHiddenWizard;
        private static GetCapturedFEWPageOptionsFn _getCapturedFEWPO;
        private static IntPtr _hiddenWizardHwnd = IntPtr.Zero;

        /// <summary>
        /// Returns the absolute path where ErwinNativeBridge.dll is expected to live.
        /// Mirrors the existing "same folder as managed DLL" install layout.
        /// </summary>
        private static string ResolveBridgePath()
        {
            var asmLoc = typeof(NativeBridgeService).Assembly.Location;
            var baseDir = string.IsNullOrEmpty(asmLoc)
                ? AppContext.BaseDirectory
                : (Path.GetDirectoryName(asmLoc) ?? AppContext.BaseDirectory);
            return Path.Combine(baseDir, BridgeDllName);
        }

        /// <summary>
        /// Install the native SyncModelCallback hook. Returns true on success.
        /// Idempotent - repeat calls are no-ops.
        /// </summary>
        public static bool Install(Action<string> log = null)
        {
            if (_installed) return true;

            try
            {
                string path = ResolveBridgePath();
                if (!File.Exists(path))
                {
                    log?.Invoke($"NativeBridge: {BridgeDllName} not found at '{path}' - skipping hook install.");
                    return false;
                }

                _bridgeModule = LoadLibraryW(path);
                if (_bridgeModule == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    log?.Invoke($"NativeBridge: LoadLibraryW('{path}') failed (Win32 err 0x{err:X}).");
                    return false;
                }

                IntPtr proc = GetProcAddress(_bridgeModule, "InstallHook");
                if (proc == IntPtr.Zero)
                {
                    log?.Invoke($"NativeBridge: InstallHook export not found in {BridgeDllName}.");
                    return false;
                }

                var install = Marshal.GetDelegateForFunctionPointer<InstallHookFn>(proc);
                int rc = install();
                log?.Invoke($"NativeBridge: InstallHook returned {rc} (0=OK, 1=ECX missing, 2=symbol missing).");
                _installed = (rc == 0);

                // Bind the capture-accessor delegates. Safe if the exports are
                // missing - feature simply stays unavailable.
                if (_installed)
                {
                    IntPtr getProc = GetProcAddress(_bridgeModule, "GetLastCapturedModelSet");
                    IntPtr resetProc = GetProcAddress(_bridgeModule, "ResetCapturedModelSet");
                    if (getProc != IntPtr.Zero)
                        _getLastCapturedModelSet = Marshal.GetDelegateForFunctionPointer<GetLastCapturedModelSetFn>(getProc);
                    if (resetProc != IntPtr.Zero)
                        _resetCapturedModelSet = Marshal.GetDelegateForFunctionPointer<ResetCapturedModelSetFn>(resetProc);

                    IntPtr genProc = GetProcAddress(_bridgeModule, "GenerateAlterDdlFromCaptured");
                    IntPtr freeProc = GetProcAddress(_bridgeModule, "FreeDdlBuffer");
                    if (genProc != IntPtr.Zero)
                        _generateAlterDdl = Marshal.GetDelegateForFunctionPointer<GenerateAlterDdlFromCapturedFn>(genProc);
                    if (freeProc != IntPtr.Zero)
                        _freeDdlBuffer = Marshal.GetDelegateForFunctionPointer<FreeDdlBufferFn>(freeProc);

                    IntPtr obsProc = GetProcAddress(_bridgeModule, "InstallObserverHook");
                    if (obsProc != IntPtr.Zero)
                        _installObserverHook = Marshal.GetDelegateForFunctionPointer<InstallObserverHookFn>(obsProc);

                    IntPtr consumeProc = GetProcAddress(_bridgeModule, "ConsumeLastCapturedDdl");
                    IntPtr clearProc = GetProcAddress(_bridgeModule, "ClearCapturedDdl");
                    if (consumeProc != IntPtr.Zero)
                        _consumeLastDdl = Marshal.GetDelegateForFunctionPointer<ConsumeLastCapturedDdlFn>(consumeProc);
                    if (clearProc != IntPtr.Zero)
                        _clearCapturedDdl = Marshal.GetDelegateForFunctionPointer<ClearCapturedDdlFn>(clearProc);

                    IntPtr invokeProc = GetProcAddress(_bridgeModule, "CallInvokePreviewOnCaptured");
                    if (invokeProc != IntPtr.Zero)
                        _callInvokePreview = Marshal.GetDelegateForFunctionPointer<CallInvokePreviewFn>(invokeProc);

                    IntPtr standaloneProc = GetProcAddress(_bridgeModule, "GenerateAlterDdlStandalone");
                    if (standaloneProc != IntPtr.Zero)
                        _generateAlterStandalone = Marshal.GetDelegateForFunctionPointer<GenerateAlterDdlStandaloneFn>(standaloneProc);

                    IntPtr openProc = GetProcAddress(_bridgeModule, "OpenAlterScriptWizardHidden");
                    IntPtr closeProc = GetProcAddress(_bridgeModule, "CloseHiddenWizard");
                    if (openProc != IntPtr.Zero)
                        _openHiddenWizard = Marshal.GetDelegateForFunctionPointer<OpenAlterScriptWizardHiddenFn>(openProc);
                    if (closeProc != IntPtr.Zero)
                        _closeHiddenWizard = Marshal.GetDelegateForFunctionPointer<CloseHiddenWizardFn>(closeProc);
                    IntPtr getCapProc = GetProcAddress(_bridgeModule, "GetCapturedFEWPageOptions");
                    if (getCapProc != IntPtr.Zero)
                        _getCapturedFEWPO = Marshal.GetDelegateForFunctionPointer<GetCapturedFEWPageOptionsFn>(getCapProc);

                    log?.Invoke($"NativeBridge: capture API bound (get={getProc != IntPtr.Zero}, reset={resetProc != IntPtr.Zero}, gen={genProc != IntPtr.Zero}, free={freeProc != IntPtr.Zero}, obs={obsProc != IntPtr.Zero}, consume={consumeProc != IntPtr.Zero}, clear={clearProc != IntPtr.Zero}, invoke={invokeProc != IntPtr.Zero}).");
                }
                return _installed;
            }
            catch (Exception ex)
            {
                log?.Invoke($"NativeBridge: Install() threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns the last GDMModelSetI* pointer captured by the
        /// GenerateFEScript detour, or IntPtr.Zero if none captured yet.
        /// </summary>
        public static IntPtr GetLastCapturedModelSet()
        {
            return _getLastCapturedModelSet?.Invoke() ?? IntPtr.Zero;
        }

        /// <summary>Clears the captured-pointer cache so the next FE_DDL call is a clean capture.</summary>
        public static void ResetCapturedModelSet()
        {
            _resetCapturedModelSet?.Invoke();
        }

        /// <summary>
        /// Reads and clears the most-recent alter DDL captured by the
        /// GenerateAlterScript detour. Returns null if nothing captured.
        /// </summary>
        public static string ConsumeLastCapturedDdl()
        {
            if (_consumeLastDdl == null || _freeDdlBuffer == null) return null;
            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = _consumeLastDdl();
                if (ptr == IntPtr.Zero) return null;
                return Marshal.PtrToStringAnsi(ptr);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                {
                    try { _freeDdlBuffer(ptr); } catch { /* diag-only; swallow */ }
                }
            }
        }

        /// <summary>
        /// Clears the stashed DDL buffer so a subsequent ConsumeLastCapturedDdl
        /// returns only DDL produced AFTER this call. Use before triggering a
        /// new wizard run to avoid reading stale data.
        /// </summary>
        public static void ClearCapturedDdl() => _clearCapturedDdl?.Invoke();

        /// <summary>
        /// Directly invokes FEWPageOptions::InvokePreviewStringOnlyCommand on
        /// the most-recently-captured FEWPageOptions* pointer (populated when
        /// the user opens the Alter Script wizard). Returns the DDL string,
        /// or null if no wizard has been opened in this session or the call
        /// fails. Caller must free via the bridge's FreeDdlBuffer (handled
        /// here internally).
        /// </summary>
        /// <summary>
        /// Experimental: generate alter DDL by constructing FEWPageOptions
        /// standalone (no wizard UI). Requires that the user has opened the
        /// Alter Script wizard at least ONCE during this erwin session to
        /// seed the feParam + wsfBase template the native bridge clones.
        /// After that, any number of calls can be made without re-opening.
        /// Returns the DDL string or null if anything fails.
        /// </summary>
        public static string GenerateAlterDdlStandalone(dynamic currentPU, Action<string> log = null)
        {
            if (_generateAlterStandalone == null || _freeDdlBuffer == null)
            {
                log?.Invoke("NativeBridge: standalone export not bound.");
                return null;
            }
            // Need the modelSet pointer for the current dirty PU.
            IntPtr ms = EnsureActiveModelSetCaptured(currentPU, log);
            if (ms == IntPtr.Zero)
            {
                log?.Invoke("NativeBridge: could not capture modelSet for standalone alter.");
                return null;
            }

            IntPtr ptr = IntPtr.Zero;
            try
            {
                log?.Invoke($"NativeBridge: invoking GenerateAlterDdlStandalone(ms=0x{ms.ToInt64():X})...");
                ptr = _generateAlterStandalone(ms);
                if (ptr == IntPtr.Zero)
                {
                    log?.Invoke("NativeBridge: standalone returned null. See bridge log.");
                    return null;
                }
                string ddl = Marshal.PtrToStringAnsi(ptr);
                log?.Invoke($"NativeBridge: standalone returned {ddl?.Length ?? 0} chars.");
                return ddl;
            }
            catch (Exception ex)
            {
                log?.Invoke($"NativeBridge: standalone threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                {
                    try { _freeDdlBuffer(ptr); } catch { }
                }
            }
        }

        /// <summary>
        /// Unified programmatic alter-DDL entry point. Orchestrates:
        ///   1. If no wizard is currently hidden-alive, SendInput Ctrl+Alt+T
        ///      to erwin to open the Alter Script wizard; immediately hides
        ///      it off-screen. FEWPageOptions ctor hook captures `this`.
        ///   2. Calls CallInvokePreviewOnCaptured which triggers
        ///      FEProcessor::GenerateAlterScript internally. Our GA detour
        ///      captures the DDL string.
        ///   3. Returns the DDL. Hidden wizard stays alive for subsequent
        ///      calls - no re-open cost until user/erwin closes it.
        /// Returns null if anything fails.
        /// </summary>
        public static string GenerateAlterDdl(Action<string> log = null)
        {
            if (_callInvokePreview == null)
            {
                log?.Invoke("NativeBridge: InvokePreview export missing.");
                return null;
            }

            // Step 1: ensure a FEWPageOptions is captured (wizard open).
            if (GetCapturedFEWPageOptionsPtr() == IntPtr.Zero)
            {
                if (_openHiddenWizard == null)
                {
                    log?.Invoke("NativeBridge: no wizard open and OpenAlterScriptWizardHidden export missing.");
                    return null;
                }
                log?.Invoke("NativeBridge: no wizard open - triggering Ctrl+Alt+T silently...");
                _hiddenWizardHwnd = _openHiddenWizard();
                if (_hiddenWizardHwnd == IntPtr.Zero)
                {
                    log?.Invoke("NativeBridge: failed to auto-open wizard.");
                    return null;
                }
                log?.Invoke($"NativeBridge: hidden wizard opened at hwnd=0x{_hiddenWizardHwnd.ToInt64():X}");
                // The FEW-CTOR hook fires synchronously during wizard creation,
                // so g_capturedFEWPO should already be set by the time we get here.
            }

            // Step 2: call Invoke → GA detour captures DDL.
            string ddl = CallInvokePreviewDirect(log);

            // Step 3: always close the hidden wizard. It's a MODAL CPropertySheet,
            // and leaving it alive locks the erwin main window (title shows
            // "Read-Only"). Closing it has a small cost (next call re-opens
            // in ~700ms) but keeps erwin usable between calls.
            if (_hiddenWizardHwnd != IntPtr.Zero && _closeHiddenWizard != null)
            {
                log?.Invoke($"NativeBridge: closing hidden wizard hwnd=0x{_hiddenWizardHwnd.ToInt64():X}");
                try { _closeHiddenWizard(_hiddenWizardHwnd); } catch (Exception ex)
                { log?.Invoke($"NativeBridge: close wizard threw: {ex.Message}"); }
                _hiddenWizardHwnd = IntPtr.Zero;
            }
            return ddl;
        }

        /// <summary>Closes the hidden wizard (if one was auto-opened). Normally
        /// not needed during a session — the wizard is cheap to keep alive.</summary>
        public static void CloseHiddenWizardIfAny()
        {
            if (_hiddenWizardHwnd != IntPtr.Zero && _closeHiddenWizard != null)
            {
                try { _closeHiddenWizard(_hiddenWizardHwnd); } catch { }
                _hiddenWizardHwnd = IntPtr.Zero;
            }
        }

        // Helper: IntPtr wrapper for native g_capturedFEWPO (we don't have a
        // dedicated getter; piggy-back on the direct-invoke return path's
        // implicit check by calling a no-op path). Actually, simplest: expose
        // a dedicated getter. For now, rely on _callInvokePreview returning
        // null when nothing captured.
        private static IntPtr GetCapturedFEWPageOptionsPtr()
        {
            return _getCapturedFEWPO?.Invoke() ?? IntPtr.Zero;
        }

        public static string CallInvokePreviewDirect(Action<string> log = null)
        {
            if (_callInvokePreview == null || _freeDdlBuffer == null)
            {
                log?.Invoke("NativeBridge: CallInvokePreview export missing.");
                return null;
            }
            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = _callInvokePreview();
                if (ptr == IntPtr.Zero)
                {
                    log?.Invoke("NativeBridge: CallInvokePreviewOnCaptured returned null (no captured wizard / invoke failed).");
                    return null;
                }
                string ddl = Marshal.PtrToStringAnsi(ptr);
                log?.Invoke($"NativeBridge: direct-invoke returned {ddl?.Length ?? 0} chars.");
                return ddl;
            }
            catch (Exception ex)
            {
                log?.Invoke($"NativeBridge: CallInvokePreview threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                {
                    try { _freeDdlBuffer(ptr); } catch { }
                }
            }
        }

        /// <summary>
        /// Faz A-spike: install observer detours on MCX internal entry points
        /// (PrepareServerModelSet, InitializeClientActionSummary, MCX ctor,
        /// MCX Execute) so a subsequent user-triggered UI CC flow leaves
        /// diagnostic traces in the bridge log. Used once to learn what
        /// state erwin expects before those functions can succeed.
        /// </summary>
        public static bool InstallObserverHooks(Action<string> log = null)
        {
            if (_installObserverHook == null)
            {
                log?.Invoke("NativeBridge: InstallObserverHook export missing - rebuild bridge DLL.");
                return false;
            }
            try
            {
                int rc = _installObserverHook();
                log?.Invoke($"NativeBridge: InstallObserverHook returned {rc}.");
                return rc == 0;
            }
            catch (Exception ex)
            {
                log?.Invoke($"NativeBridge: InstallObserverHook threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Makes sure the GDMModelSetI* for the given active PU has been captured.
        /// Triggers a one-shot FEModel_DDL(tempPath, "") call so our detour fires
        /// and records the pointer. Caller is responsible for passing the active
        /// PU (the pointer we capture belongs to whichever PU FEModel_DDL runs on).
        /// </summary>
        /// <returns>Non-zero IntPtr on success; IntPtr.Zero if capture failed.</returns>
        public static IntPtr EnsureActiveModelSetCaptured(dynamic currentPU, Action<string> log = null)
        {
            if (currentPU == null)
            {
                log?.Invoke("NativeBridge: currentPU is null - cannot capture.");
                return IntPtr.Zero;
            }
            if (!_installed || _getLastCapturedModelSet == null)
            {
                log?.Invoke("NativeBridge: bridge not installed / capture export missing.");
                return IntPtr.Zero;
            }

            // Always reset before capturing - the cached pointer may belong to
            // a PU the user has since switched away from. The detour fires
            // cheaply (erwin may show a "No schema" dialog if the model has
            // no Target DB configured, but the detour records the pointer
            // BEFORE that error path, so capture still succeeds).
            _resetCapturedModelSet?.Invoke();

            string tempDir = Path.Combine(Path.GetTempPath(), "erwin-addin-capture");
            Directory.CreateDirectory(tempDir);
            string tempSql = Path.Combine(tempDir, $"capture-{Guid.NewGuid():N}.sql");

            try
            {
                log?.Invoke($"NativeBridge: triggering FEModel_DDL('{tempSql}') to capture modelSet...");
                bool ok = false;
                try { ok = (bool)currentPU.FEModel_DDL(tempSql, ""); }
                catch (Exception ex) { log?.Invoke($"NativeBridge: FEModel_DDL threw: {ex.GetType().Name}: {ex.Message}"); }
                log?.Invoke($"NativeBridge: FEModel_DDL returned {ok}.");
            }
            finally
            {
                try { if (File.Exists(tempSql)) File.Delete(tempSql); } catch { }
            }

            IntPtr captured = _getLastCapturedModelSet();
            log?.Invoke($"NativeBridge: capture result = 0x{captured.ToInt64():X}");
            return captured;
        }

        /// <summary>
        /// Faz 2: runs the fully silent alter-DDL pipeline using erwin's own natives:
        /// PrepareServerModelSet -> InitializeClientActionSummary ->
        /// MCXInvokeCompleteCompare::Execute -> FEProcessor::GenerateAlterScript ->
        /// GetScript -> concatenated UTF-8 DDL string.
        ///
        /// Returns null if any step fails. Check %TEMP%\erwin-native-bridge.log for details.
        /// </summary>
        public static string GenerateAlterDdl(dynamic currentPU, Action<string> log = null)
        {
            if (_generateAlterDdl == null || _freeDdlBuffer == null)
            {
                log?.Invoke("NativeBridge: Faz 2 exports not bound - is the latest bridge DLL installed?");
                return null;
            }

            // Make sure the active PU's ModelSet is captured and in the cache.
            IntPtr ms = EnsureActiveModelSetCaptured(currentPU, log);
            if (ms == IntPtr.Zero)
            {
                log?.Invoke("NativeBridge: could not capture modelSet; alter DDL pipeline skipped.");
                return null;
            }

            IntPtr ddlPtr = IntPtr.Zero;
            try
            {
                log?.Invoke("NativeBridge: invoking GenerateAlterDdlFromCaptured...");
                ddlPtr = _generateAlterDdl();
                if (ddlPtr == IntPtr.Zero)
                {
                    log?.Invoke("NativeBridge: alter DDL pipeline returned null. See bridge log.");
                    return null;
                }
                string ddl = Marshal.PtrToStringAnsi(ddlPtr);
                log?.Invoke($"NativeBridge: alter DDL = {ddl?.Length ?? 0} chars");
                return ddl;
            }
            catch (Exception ex)
            {
                log?.Invoke($"NativeBridge: GenerateAlterDdl threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                if (ddlPtr != IntPtr.Zero)
                {
                    try { _freeDdlBuffer(ddlPtr); }
                    catch (Exception ex) { log?.Invoke($"NativeBridge: FreeDdlBuffer threw: {ex.Message}"); }
                }
            }
        }
    }
}
