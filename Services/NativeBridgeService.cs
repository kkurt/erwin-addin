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

        // D1-spike: CC state inspection
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CCInspGetPtrFn();   // shared signature for all 3 getters

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspCallApplyCCSilentFn(IntPtr leftMs, IntPtr rightMs, int level);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspArmAsWriteWatchFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CCInspGetTrasactionSummaryFn(uint flags, IntPtr ms);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspSetGlobalPxAsFn(IntPtr asPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspCallOnFeFn(IntPtr ms, int boolFlag, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CCInspGenerateMartMartDdlViaOnFEFn(IntPtr ms);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspInstallOnFeHookFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CCInspGetLastOnFeMsFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CCInspGetLastEdrMsFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspGetEdrTxCountFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspCallApplyDiffRightFn(IntPtr leftMs, IntPtr rightMs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspHookEccApplyFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspGetEccApplyArgsFn(out IntPtr a1, out IntPtr a2, out IntPtr a3, out IntPtr a4);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long CCInspReplayEccApplyFn(IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspHookCmpApplyFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspGetCmpApplyArgsFn(out IntPtr a1, out IntPtr a2, out IntPtr a3, out IntPtr a4);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long CCInspReplayCmpApplyFn(IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspHookMsgMapFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspGetMsgMapArgsFn(out IntPtr a1, out IntPtr a2, out IntPtr a3, out IntPtr a4);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long CCInspReplayMsgMapFn(IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspCallShowERwinCCWizFn(IntPtr ms1, IntPtr ms2, int b1, int b2);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CCInspGetSeenMsFn(int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CCInspGetSeenMsCountFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CCInspSetEdrStackTraceFn(int enable);

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

        // D1-spike: CC state inspection delegates
        private static CCInspGetPtrFn _ccInspGetFEDataAs;
        private static CCInspGetPtrFn _ccInspGetFEDataMs;
        private static CCInspGetPtrFn _ccInspGetELC2As;
        private static CCInspCallApplyCCSilentFn _ccInspCallApplyCCSilent;
        private static CCInspArmAsWriteWatchFn _ccInspArmAsWriteWatch;
        private static CCInspGetTrasactionSummaryFn _ccInspGetTrasactionSummary;
        private static CCInspSetGlobalPxAsFn _ccInspSetGlobalPxAs;
        private static CCInspCallOnFeFn _ccInspCallOnFe;
        private static CCInspGenerateMartMartDdlViaOnFEFn _ccInspGenerateMartMartDdlViaOnFE;
        private static CCInspInstallOnFeHookFn _ccInspInstallOnFeHook;
        private static CCInspGetLastOnFeMsFn _ccInspGetLastOnFeMs;
        private static CCInspGetLastEdrMsFn _ccInspGetLastEdrMs;
        private static CCInspGetEdrTxCountFn _ccInspGetEdrTxCount;
        private static CCInspCallApplyDiffRightFn _ccInspCallApplyDiffRight;
        private static CCInspHookEccApplyFn _ccInspHookEccApply;
        private static CCInspGetEccApplyArgsFn _ccInspGetEccApplyArgs;
        private static CCInspReplayEccApplyFn _ccInspReplayEccApply;
        private static CCInspHookCmpApplyFn _ccInspHookCmpApply;
        private static CCInspGetCmpApplyArgsFn _ccInspGetCmpApplyArgs;
        private static CCInspReplayCmpApplyFn _ccInspReplayCmpApply;
        private static CCInspHookMsgMapFn _ccInspHookMsgMap;
        private static CCInspGetMsgMapArgsFn _ccInspGetMsgMapArgs;
        private static CCInspReplayMsgMapFn _ccInspReplayMsgMap;
        private static CCInspCallShowERwinCCWizFn _ccInspCallShowERwinCCWiz;
        private static CCInspGetSeenMsFn _ccInspGetSeenMs;
        private static CCInspGetSeenMsCountFn _ccInspGetSeenMsCount;
        private static CCInspSetEdrStackTraceFn _ccInspSetEdrStackTrace;

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

                    // D1-spike: CC state inspection
                    IntPtr ccAs  = GetProcAddress(_bridgeModule, "CCInsp_GetFEDataActionSummary");
                    IntPtr ccMs  = GetProcAddress(_bridgeModule, "CCInsp_GetFEDataModelSet");
                    IntPtr ccE2  = GetProcAddress(_bridgeModule, "CCInsp_GetELC2GlobalAs");
                    if (ccAs != IntPtr.Zero) _ccInspGetFEDataAs = Marshal.GetDelegateForFunctionPointer<CCInspGetPtrFn>(ccAs);
                    if (ccMs != IntPtr.Zero) _ccInspGetFEDataMs = Marshal.GetDelegateForFunctionPointer<CCInspGetPtrFn>(ccMs);
                    if (ccE2 != IntPtr.Zero) _ccInspGetELC2As  = Marshal.GetDelegateForFunctionPointer<CCInspGetPtrFn>(ccE2);

                    IntPtr ccSilent = GetProcAddress(_bridgeModule, "CCInsp_CallApplyCCSilent");
                    if (ccSilent != IntPtr.Zero)
                        _ccInspCallApplyCCSilent = Marshal.GetDelegateForFunctionPointer<CCInspCallApplyCCSilentFn>(ccSilent);

                    IntPtr ccArm = GetProcAddress(_bridgeModule, "CCInsp_ArmAsWriteWatch");
                    if (ccArm != IntPtr.Zero)
                        _ccInspArmAsWriteWatch = Marshal.GetDelegateForFunctionPointer<CCInspArmAsWriteWatchFn>(ccArm);

                    IntPtr ccGTS = GetProcAddress(_bridgeModule, "CCInsp_GetTrasactionSummary");
                    IntPtr ccSAs = GetProcAddress(_bridgeModule, "CCInsp_SetGlobalPxAs");
                    if (ccGTS != IntPtr.Zero) _ccInspGetTrasactionSummary = Marshal.GetDelegateForFunctionPointer<CCInspGetTrasactionSummaryFn>(ccGTS);
                    if (ccSAs != IntPtr.Zero) _ccInspSetGlobalPxAs = Marshal.GetDelegateForFunctionPointer<CCInspSetGlobalPxAsFn>(ccSAs);

                    IntPtr ccOnFe = GetProcAddress(_bridgeModule, "CCInsp_CallOnFE");
                    if (ccOnFe != IntPtr.Zero)
                        _ccInspCallOnFe = Marshal.GetDelegateForFunctionPointer<CCInspCallOnFeFn>(ccOnFe);

                    IntPtr ccMMOrch = GetProcAddress(_bridgeModule, "CCInsp_GenerateMartMartDdlViaOnFE");
                    if (ccMMOrch != IntPtr.Zero)
                        _ccInspGenerateMartMartDdlViaOnFE = Marshal.GetDelegateForFunctionPointer<CCInspGenerateMartMartDdlViaOnFEFn>(ccMMOrch);

                    IntPtr ccOnFeHk = GetProcAddress(_bridgeModule, "CCInsp_InstallOnFeHook");
                    if (ccOnFeHk != IntPtr.Zero)
                        _ccInspInstallOnFeHook = Marshal.GetDelegateForFunctionPointer<CCInspInstallOnFeHookFn>(ccOnFeHk);

                    IntPtr ccGetLastOnFe = GetProcAddress(_bridgeModule, "CCInsp_GetLastOnFeMs");
                    if (ccGetLastOnFe != IntPtr.Zero)
                        _ccInspGetLastOnFeMs = Marshal.GetDelegateForFunctionPointer<CCInspGetLastOnFeMsFn>(ccGetLastOnFe);

                    IntPtr ccGetLastEdr = GetProcAddress(_bridgeModule, "CCInsp_GetLastEdrMs");
                    if (ccGetLastEdr != IntPtr.Zero)
                        _ccInspGetLastEdrMs = Marshal.GetDelegateForFunctionPointer<CCInspGetLastEdrMsFn>(ccGetLastEdr);

                    IntPtr ccGetEdrTxCount = GetProcAddress(_bridgeModule, "CCInsp_GetEdrTxCount");
                    if (ccGetEdrTxCount != IntPtr.Zero)
                        _ccInspGetEdrTxCount = Marshal.GetDelegateForFunctionPointer<CCInspGetEdrTxCountFn>(ccGetEdrTxCount);

                    IntPtr ccCallAdr = GetProcAddress(_bridgeModule, "CCInsp_CallApplyDifferencesToRight");
                    if (ccCallAdr != IntPtr.Zero)
                        _ccInspCallApplyDiffRight = Marshal.GetDelegateForFunctionPointer<CCInspCallApplyDiffRightFn>(ccCallAdr);

                    IntPtr ccHookEcc = GetProcAddress(_bridgeModule, "CCInsp_HookEccApply");
                    if (ccHookEcc != IntPtr.Zero)
                        _ccInspHookEccApply = Marshal.GetDelegateForFunctionPointer<CCInspHookEccApplyFn>(ccHookEcc);

                    IntPtr ccGetEccArgs = GetProcAddress(_bridgeModule, "CCInsp_GetEccApplyArgs");
                    if (ccGetEccArgs != IntPtr.Zero)
                        _ccInspGetEccApplyArgs = Marshal.GetDelegateForFunctionPointer<CCInspGetEccApplyArgsFn>(ccGetEccArgs);

                    IntPtr ccReplayEcc = GetProcAddress(_bridgeModule, "CCInsp_ReplayEccApply");
                    if (ccReplayEcc != IntPtr.Zero)
                        _ccInspReplayEccApply = Marshal.GetDelegateForFunctionPointer<CCInspReplayEccApplyFn>(ccReplayEcc);

                    IntPtr ccHookCmp = GetProcAddress(_bridgeModule, "CCInsp_HookCmpApply");
                    if (ccHookCmp != IntPtr.Zero)
                        _ccInspHookCmpApply = Marshal.GetDelegateForFunctionPointer<CCInspHookCmpApplyFn>(ccHookCmp);

                    IntPtr ccGetCmpArgs = GetProcAddress(_bridgeModule, "CCInsp_GetCmpApplyArgs");
                    if (ccGetCmpArgs != IntPtr.Zero)
                        _ccInspGetCmpApplyArgs = Marshal.GetDelegateForFunctionPointer<CCInspGetCmpApplyArgsFn>(ccGetCmpArgs);

                    IntPtr ccReplayCmp = GetProcAddress(_bridgeModule, "CCInsp_ReplayCmpApply");
                    if (ccReplayCmp != IntPtr.Zero)
                        _ccInspReplayCmpApply = Marshal.GetDelegateForFunctionPointer<CCInspReplayCmpApplyFn>(ccReplayCmp);

                    IntPtr ccHookMsgMap = GetProcAddress(_bridgeModule, "CCInsp_HookMsgMap");
                    if (ccHookMsgMap != IntPtr.Zero)
                        _ccInspHookMsgMap = Marshal.GetDelegateForFunctionPointer<CCInspHookMsgMapFn>(ccHookMsgMap);

                    IntPtr ccGetMsgMapArgs = GetProcAddress(_bridgeModule, "CCInsp_GetMsgMapArgs");
                    if (ccGetMsgMapArgs != IntPtr.Zero)
                        _ccInspGetMsgMapArgs = Marshal.GetDelegateForFunctionPointer<CCInspGetMsgMapArgsFn>(ccGetMsgMapArgs);

                    IntPtr ccReplayMsgMap = GetProcAddress(_bridgeModule, "CCInsp_ReplayMsgMap");
                    if (ccReplayMsgMap != IntPtr.Zero)
                        _ccInspReplayMsgMap = Marshal.GetDelegateForFunctionPointer<CCInspReplayMsgMapFn>(ccReplayMsgMap);

                    IntPtr ccCCWiz = GetProcAddress(_bridgeModule, "CCInsp_CallShowERwinCCWiz");
                    if (ccCCWiz != IntPtr.Zero)
                        _ccInspCallShowERwinCCWiz = Marshal.GetDelegateForFunctionPointer<CCInspCallShowERwinCCWizFn>(ccCCWiz);

                    IntPtr ccSeenMs = GetProcAddress(_bridgeModule, "CCInsp_GetSeenMs");
                    IntPtr ccSeenMsCount = GetProcAddress(_bridgeModule, "CCInsp_GetSeenMsCount");
                    if (ccSeenMs != IntPtr.Zero) _ccInspGetSeenMs = Marshal.GetDelegateForFunctionPointer<CCInspGetSeenMsFn>(ccSeenMs);
                    if (ccSeenMsCount != IntPtr.Zero) _ccInspGetSeenMsCount = Marshal.GetDelegateForFunctionPointer<CCInspGetSeenMsCountFn>(ccSeenMsCount);

                    IntPtr ccEdrST = GetProcAddress(_bridgeModule, "CCInsp_SetEdrStackTrace");
                    if (ccEdrST != IntPtr.Zero) _ccInspSetEdrStackTrace = Marshal.GetDelegateForFunctionPointer<CCInspSetEdrStackTraceFn>(ccEdrST);

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
        /// D1-spike: calls EM_ECC!CWizInterface::ApplyCCSilentMode. Returns the
        /// int from the engine (or a negative sentinel if the call couldn't
        /// happen). Caller is expected to DumpCCState() before and after to
        /// see whether gbl_pxActionSummary gets populated as a side effect.
        /// </summary>
        public static int CallApplyCCSilent(IntPtr leftMs, IntPtr rightMs, int level)
        {
            if (_ccInspCallApplyCCSilent == null) return -10000;
            return _ccInspCallApplyCCSilent(leftMs, rightMs, level);
        }

        /// <summary>
        /// D1-spike: arm a one-shot write-watch on ELC2's gbl_pxActionSummary.
        /// Call this right before triggering the action expected to populate
        /// the global (e.g., clicking 'Right Alter Script' in Resolve Diff).
        /// Returns 0 on success. Captured writer RIP + stack goes to the
        /// native bridge log under [AS-WATCH]* tags.
        /// </summary>
        public static int ArmAsWriteWatch()
        {
            return _ccInspArmAsWriteWatch?.Invoke() ?? -10000;
        }

        /// <summary>
        /// D1-spike: call EDRAlterNameCaching::GetTrasactionSummary(flags, ms).
        /// Returns the AS representing recorded transactions on `ms`. Expected
        /// to be non-null after user has done a manual CC + Apply-to-Right.
        /// </summary>
        public static IntPtr GetTrasactionSummary(uint flags, IntPtr ms)
        {
            return _ccInspGetTrasactionSummary?.Invoke(flags, ms) ?? IntPtr.Zero;
        }

        /// <summary>D1-spike: directly write ELC2!gbl_pxActionSummary.</summary>
        public static int SetGlobalPxAs(IntPtr asPtr)
        {
            return _ccInspSetGlobalPxAs?.Invoke(asPtr) ?? -10000;
        }

        /// <summary>D4-spike: returns the MS captured by the EDR hook (right
        /// side in Mart-Mart). Zero if no EDR call has been observed yet.</summary>
        public static IntPtr GetLastEdrMs()
        {
            return _ccInspGetLastEdrMs?.Invoke() ?? IntPtr.Zero;
        }

        /// <summary>
        /// Returns the cumulative number of <c>RegsiterStartTransactionId</c>
        /// calls observed by the native bridge since startup. Used by managed
        /// code to wait for Apply-to-Right's deferred XTP cascade to finish
        /// firing transactions before invoking OnFE.
        /// </summary>
        public static int GetEdrTxCount()
        {
            return _ccInspGetEdrTxCount?.Invoke() ?? -1;
        }

        /// <summary>
        /// Invokes <c>CWizInterface::ApplyDifferencesToRight(leftMs, rightMs)</c>
        /// directly through the native bridge trampoline, bypassing the
        /// XTP listview click UI path entirely. Returns the int result from
        /// the exported EM_ECC function, <c>-9999</c> if the trampoline is not
        /// bound, or <c>-8888</c> on SEH.
        /// </summary>
        public static int CallApplyDifferencesToRight(IntPtr leftMs, IntPtr rightMs)
        {
            return _ccInspCallApplyDiffRight?.Invoke(leftMs, rightMs) ?? -9999;
        }

        /// <summary>
        /// Installs an inline hook on the function containing
        /// <c>EM_ECC.dll + 0x42F4A</c> (the Apply-to-Right dispatcher that the
        /// XTP listview arrow click invokes). First call stashes its 4 args
        /// for later <see cref="ReplayEccApply"/>. Idempotent.
        /// </summary>
        public static int HookEccApply()
        {
            return _ccInspHookEccApply?.Invoke() ?? -1;
        }

        /// <summary>
        /// Returns the 4 args latched by the first invocation of the
        /// ECC-Apply hook. Returns 1 if args are valid (hook fired at least
        /// once), 0 if no capture has happened yet.
        /// </summary>
        public static bool GetEccApplyArgs(out IntPtr a1, out IntPtr a2, out IntPtr a3, out IntPtr a4)
        {
            a1 = a2 = a3 = a4 = IntPtr.Zero;
            if (_ccInspGetEccApplyArgs == null) return false;
            int rc = _ccInspGetEccApplyArgs(out a1, out a2, out a3, out a4);
            return rc == 1;
        }

        /// <summary>
        /// Replays the captured ECC-Apply call with either latched args (pass
        /// <see cref="IntPtr.Zero"/> for each) or caller-supplied overrides.
        /// Returns the function's raw 64-bit result.
        /// </summary>
        public static long ReplayEccApply(IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4)
        {
            return _ccInspReplayEccApply?.Invoke(a1, a2, a3, a4) ?? -9999;
        }

        /// <summary>
        /// Installs an inline hook at the XTP listview click handler
        /// <c>EM_CMP.dll + 0x13920</c>'s enclosing function entry. One frame
        /// above the ECC dispatcher - args are expected to be more stable
        /// (MFC-style this+notif pointers, heap-resident).
        /// </summary>
        public static int HookCmpApply()
        {
            return _ccInspHookCmpApply?.Invoke() ?? -1;
        }

        public static bool GetCmpApplyArgs(out IntPtr a1, out IntPtr a2, out IntPtr a3, out IntPtr a4)
        {
            a1 = a2 = a3 = a4 = IntPtr.Zero;
            if (_ccInspGetCmpApplyArgs == null) return false;
            return _ccInspGetCmpApplyArgs(out a1, out a2, out a3, out a4) == 1;
        }

        public static long ReplayCmpApply(IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4)
        {
            return _ccInspReplayCmpApply?.Invoke(a1, a2, a3, a4) ?? -9999;
        }

        /// <summary>
        /// Installs inline hook at <c>EM_CMP.dll + 0x1EA11</c>'s enclosing
        /// function - the highest erwin frame called directly by mfc140's
        /// message map dispatcher. First call's 4 args are latched.
        /// </summary>
        public static int HookMsgMap()
        {
            return _ccInspHookMsgMap?.Invoke() ?? -1;
        }

        public static bool GetMsgMapArgs(out IntPtr a1, out IntPtr a2, out IntPtr a3, out IntPtr a4)
        {
            a1 = a2 = a3 = a4 = IntPtr.Zero;
            if (_ccInspGetMsgMapArgs == null) return false;
            return _ccInspGetMsgMapArgs(out a1, out a2, out a3, out a4) == 1;
        }

        public static long ReplayMsgMap(IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4)
        {
            return _ccInspReplayMsgMap?.Invoke(a1, a2, a3, a4) ?? -9999;
        }

        /// <summary>D4-spike: list of distinct modelSets seen by any hook.
        /// Order is chronological: index 0 is the first MS seen, index 1 the
        /// second, etc. Max 16 entries.</summary>
        /// <summary>Toggle stack-trace logging on EDR RegsiterStartTransactionId
        /// so we can identify the ELC2 internal function that triggers Apply-to-Right
        /// transactions.</summary>
        public static void SetEdrStackTrace(bool enable)
        {
            _ccInspSetEdrStackTrace?.Invoke(enable ? 1 : 0);
        }

        public static IntPtr[] GetSeenModelSets()
        {
            int count = _ccInspGetSeenMsCount?.Invoke() ?? 0;
            var result = new IntPtr[count];
            for (int i = 0; i < count; ++i)
                result[i] = _ccInspGetSeenMs?.Invoke(i) ?? IntPtr.Zero;
            return result;
        }

        /// <summary>
        /// Returns the i'th model-set pointer captured by the bridge's EDR
        /// hook, or <see cref="IntPtr.Zero"/> if <paramref name="index"/> is
        /// out of range.
        /// </summary>
        public static IntPtr GetSeenModelSet(int index)
        {
            int count = _ccInspGetSeenMsCount?.Invoke() ?? 0;
            if (index < 0 || index >= count) return IntPtr.Zero;
            return _ccInspGetSeenMs?.Invoke(index) ?? IntPtr.Zero;
        }

        /// <summary>D4-spike: call EM_ECC!CWizInterface::ShowERwinCCWiz
        /// with pre-populated ms2 (right model) and both bools. If erwin
        /// interprets this as a silent / auto-apply path we get full
        /// automation; otherwise it just opens the CC wizard UI.</summary>
        public static int CallShowERwinCCWiz(IntPtr ms1, IntPtr ms2, bool b1, bool b2)
        {
            return _ccInspCallShowERwinCCWiz?.Invoke(ms1, ms2, b1 ? 1 : 0, b2 ? 1 : 0) ?? -10000;
        }

        /// <summary>
        /// D1-spike: calls ELA::OnFE(ms, false, flags). This is the exact handler
        /// fired by the 'Right Alter Script' toolbar button in Resolve Differences.
        /// Internally computes the CC-context AS, writes gbl_pxActionSummary,
        /// and opens the alter wizard modally.
        /// </summary>
        public static int CallOnFE(IntPtr ms, bool flag, uint flags)
        {
            return _ccInspCallOnFe?.Invoke(ms, flag ? 1 : 0, flags) ?? -10000;
        }

        /// <summary>D1-spike: install inline detour on ELA::OnFE to log args
        /// every time it fires. Lets us discover which flags value the
        /// 'Right Alter Script' button actually passes.</summary>
        public static int InstallOnFeHook()
        {
            return _ccInspInstallOnFeHook?.Invoke() ?? -10000;
        }

        /// <summary>
        /// D1-spike variant: Mart-Mart DDL via direct ELA::OnFE call. No
        /// keystrokes. OnFE itself opens the alter wizard and writes gbl_pxAs;
        /// our FEW-CTOR hook + auto-hide + InvokePreview chain then captures
        /// the DDL and closes the wizard. Prereq: user has done manual
        /// CC + Apply-to-Right in Resolve Differences.
        /// </summary>
        public static string GenerateMartMartDdlViaOnFE(Action<string> log = null)
        {
            if (_ccInspGenerateMartMartDdlViaOnFE == null || _freeDdlBuffer == null)
            {
                log?.Invoke("GenerateMartMartDdlViaOnFE: native orchestrator export missing.");
                return null;
            }
            // MS selection fallback chain:
            //   1. EDR hook capture (fires during CC + Apply-to-Right on the RIGHT side)
            //   2. OnFE hook capture (fires when user clicks 'Right Alter Script')
            //   3. GA-detour active-PU MS (LEFT side - likely wrong for Mart-Mart)
            IntPtr edrMs   = _ccInspGetLastEdrMs?.Invoke() ?? IntPtr.Zero;
            IntPtr onFeMs  = _ccInspGetLastOnFeMs?.Invoke() ?? IntPtr.Zero;
            IntPtr activeMs = GetLastCapturedModelSet();

            IntPtr ms;
            string source;
            if (edrMs != IntPtr.Zero)        { ms = edrMs;    source = "EDR hook (right-side transaction target)"; }
            else if (onFeMs != IntPtr.Zero)  { ms = onFeMs;   source = "OnFE hook (right-side from manual click)"; }
            else                              { ms = activeMs; source = "active-PU fallback (LEFT side - result may be empty)"; }

            if (ms == IntPtr.Zero)
            {
                log?.Invoke("GenerateMartMartDdlViaOnFE: no MS available.");
                log?.Invoke("  Do: Complete Compare + Apply-to-Right in Resolve Differences, then retry.");
                return null;
            }
            log?.Invoke($"Using MS = 0x{ms.ToInt64():X} (source: {source})");
            log?.Invoke("Native orchestrator spawns worker + calls OnFE; bg thread blocks until worker closes wizard.");

            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = _ccInspGenerateMartMartDdlViaOnFE(ms);
                if (buf == IntPtr.Zero)
                {
                    log?.Invoke("Native orchestrator returned null. Check [ONFE-ORCH] / [ONFE-WORKER] / [GA] lines in bridge log.");
                    return null;
                }
                string ddl = Marshal.PtrToStringAnsi(buf);
                log?.Invoke($"Native orchestrator returned {ddl?.Length ?? 0} chars.");
                return ddl;
            }
            catch (Exception ex)
            {
                log?.Invoke($"GenerateMartMartDdlViaOnFE threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                if (buf != IntPtr.Zero) { try { _freeDdlBuffer(buf); } catch { } }
            }
        }

        /// <summary>
        /// D1-spike end-to-end: after user has done a manual CC + Apply-to-Right
        /// in the Resolve Differences dialog (but NOT yet clicked Right Alter
        /// Script), this wires the last mile: pull AS via GetTrasactionSummary,
        /// write it to gbl_pxAs, open the alter wizard hidden, invoke preview,
        /// capture DDL, close wizard. Returns the DDL string or null on
        /// failure.
        /// </summary>
        public static string GenerateMartMartDdl(Action<string> log = null)
        {
            IntPtr ms = GetLastCapturedModelSet();
            if (ms == IntPtr.Zero)
            {
                log?.Invoke("GenerateMartMartDdl: no captured modelSet. Click 'Generate DDL' once first.");
                return null;
            }
            log?.Invoke($"GenerateMartMartDdl: using captured MS = 0x{ms.ToInt64():X}");

            IntPtr asPtr = GetTrasactionSummary(0, ms);
            if (asPtr == IntPtr.Zero)
            {
                log?.Invoke("GenerateMartMartDdl: GetTrasactionSummary returned null. Did you do CC + Apply-to-Right first?");
                return null;
            }
            log?.Invoke($"GenerateMartMartDdl: AS = 0x{asPtr.ToInt64():X}");

            int sw = SetGlobalPxAs(asPtr);
            if (sw != 0) { log?.Invoke($"GenerateMartMartDdl: SetGlobalPxAs rc={sw}"); return null; }

            // Now open the alter wizard hidden. WARNING: the Ctrl+Alt+T path
            // triggers the NORMAL alter script handler which may overwrite
            // gbl_pxAs with a dirty-vs-save AS. If so, this spike fails and
            // we need a different invocation method.
            return GenerateAlterDdl(log);
        }

        /// <summary>
        /// D1-spike: snapshot of CC-related global state. Returns a formatted
        /// multi-line report so the caller can log it directly.
        /// </summary>
        public static string DumpCCState()
        {
            IntPtr fedAs = _ccInspGetFEDataAs?.Invoke() ?? IntPtr.Zero;
            IntPtr fedMs = _ccInspGetFEDataMs?.Invoke() ?? IntPtr.Zero;
            IntPtr elc2As = _ccInspGetELC2As?.Invoke() ?? IntPtr.Zero;
            IntPtr capMs = GetLastCapturedModelSet();
            return
                $"CC state snapshot:\n" +
                $"  CERwinFEData.ActionSummary = 0x{fedAs.ToInt64():X16}\n" +
                $"  CERwinFEData.ModelSet      = 0x{fedMs.ToInt64():X16}\n" +
                $"  EM_ELC2.gbl_pxActionSummary = 0x{elc2As.ToInt64():X16}\n" +
                $"  GA-detour captured MS      = 0x{capMs.ToInt64():X16}";
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
