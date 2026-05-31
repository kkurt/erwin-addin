using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using xLicense;
using xLicense.Core;

namespace EliteSoft.Erwin.AddIn
{
    /// <summary>
    /// Elite Soft Erwin Add-In - Model Configuration
    /// Register with: regsvr32 EliteSoft.Erwin.AddIn.comhost.dll
    /// </summary>
    [ComVisible(true)]
    [Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    // ProgID renamed from "EliteSoft.Erwin.AddIn" to "EliteSoft.Meta.AddIn"
    // 2026-05-25. The C# namespace and assembly name remain
    // EliteSoft.Erwin.AddIn (rename would touch every file with no
    // user-visible benefit). Install scripts clean up the old ProgID
    // and old menu key ("Elite Soft Erwin Addin") on install.
    [ProgId("EliteSoft.Meta.AddIn")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class ErwinAddIn
    {
        private static ModelConfigForm _activeForm = null;
        private static bool _exceptionHandlerInstalled = false;

        /// <summary>
        /// Public read-only accessor for the currently displayed addin form.
        /// Used by <c>AddinMessageDialog</c> to position popups on the same
        /// monitor as the addin (a popup that lands on a different display
        /// from the host window is a UX bug - verified 2026-05-15). Returns
        /// null when no form is active (between add-in unload/load cycles).
        /// </summary>
        public static System.Windows.Forms.Form ActiveForm =>
            (_activeForm != null && !_activeForm.IsDisposed) ? _activeForm : null;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        // MB flags
        private const uint MB_OK = 0x0;
        private const uint MB_ICONERROR = 0x10;
        private const uint MB_ICONWARNING = 0x30;
        private const uint MB_TOPMOST = 0x40000;
        private const uint MB_SETFOREGROUND = 0x10000;

        /// <summary>
        /// Shows a MessageBox that is always on top, even when called from a background thread.
        /// </summary>
        internal static void ShowTopMostMessage(string text, string caption, bool isError = true)
        {
            uint flags = MB_OK | MB_TOPMOST | MB_SETFOREGROUND | (isError ? MB_ICONERROR : MB_ICONWARNING);
            MessageBoxW(IntPtr.Zero, text, caption, flags);
        }

        public ErwinAddIn()
        {
        }

        /// <summary>
        /// Install global exception handler to prevent erwin crash from COM exceptions.
        /// </summary>
        private static void EnsureExceptionHandler()
        {
            if (_exceptionHandlerInstalled) return;
            _exceptionHandlerInstalled = true;

            try
            {
                Application.ThreadException += Application_ThreadException;
            }
            catch { }
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            // Swallow COM exceptions to prevent erwin crash
            System.Diagnostics.Debug.WriteLine($"ErwinAddIn ThreadException caught: {e.Exception.GetType().Name}: {e.Exception.Message}");

            // If it's a COM-related exception and form is active, trigger session lost cleanup
            if (_activeForm != null && !_activeForm.IsDisposed)
            {
                if (e.Exception is COMException ||
                    e.Exception is InvalidComObjectException ||
                    e.Exception is AccessViolationException ||
                    e.Exception.Message.Contains("COM") ||
                    e.Exception.Message.Contains("RPC") ||
                    e.Exception.Message.Contains("0x800"))
                {
                    // Form will handle cleanup via HandleSessionLost
                    System.Diagnostics.Debug.WriteLine("ErwinAddIn: COM exception caught - session may be lost");
                }
            }
        }

        /// <summary>
        /// Atomic re-entry guard for <see cref="Execute"/>. erwin's COM
        /// dispatch is on the UI thread, but the body pumps the message
        /// queue at multiple points (early splash dialog, MessageBoxW
        /// during license fail, SCAPI Activator.CreateInstance, and
        /// ModelConfigForm constructor). If a second WM_COMMAND for the
        /// same cmd id is queued during one of those pumps the message
        /// pump dispatches it inline, re-entering Execute before the
        /// _activeForm field is set at line 238. The form-active guard
        /// at line 208 then misses (still null), the body runs again,
        /// and we end up with TWO ModelConfigForm windows open at once
        /// (verified 2026-05-26 23:18 with duplicate watchers in flight).
        /// Setting this flag atomically at body entry, clearing in the
        /// finally block, makes nested invocations no-op out immediately
        /// regardless of how many PostMessage WM_COMMAND 1181s end up in
        /// the queue. 0 = idle, 1 = currently executing.
        /// </summary>
        private static int _executeRunning;

        /// <summary>
        /// Called by erwin - parameterless version
        /// </summary>
        public void Execute()
        {
            // Top-of-body re-entry guard. See _executeRunning XML doc for
            // the duplicate-form scenario this prevents. Runs BEFORE
            // logger start so we don't even pollute the log with the
            // re-entered call's session marker.
            if (System.Threading.Interlocked.Exchange(ref _executeRunning, 1) != 0)
            {
                try { Services.AddinLogger.Log("Execute re-entered while previous invocation is still running - returning no-op."); }
                catch { /* never throw out of the guard */ }
                return;
            }

            try
            {
                ExecuteBody();
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _executeRunning, 0);
            }
        }

        private void ExecuteBody()
        {
            Services.AddinLogger.StartSession();
            using var _scope = Services.AddinLogger.BeginScope("ErwinAddIn.Execute");

            // DIAGNOSTIC (auto-load discovery): subclass erwin's main window
            // WindowProc and log every WM_COMMAND wParam so we can identify
            // the dynamically-assigned cmd id erwin uses to dispatch our
            // Tools&gt;Add-Ins entry. With that id, the watcher can
            // PostMessage(erwinMain, WM_COMMAND, id, 0) from outside and
            // auto-load the addin WITHOUT DLL injection (SEP-clean). The
            // MarkExecuteEntry line gives us a timestamp anchor to
            // correlate WM_COMMAND wParams with actual addin invocations.
            // Idempotent / safe / always-on; log goes to
            // %LOCALAPPDATA%\EliteSoft\ErwinAddIn\wmcmd.log.
            try
            {
                var erwinMain = Services.Win32Helper.GetErwinMainWindow();
                Services.WmCommandLogger.Install(erwinMain);
                Services.WmCommandLogger.MarkExecuteEntry();
            }
            catch (Exception logEx)
            {
                Services.AddinLogger.Log($"WmCommandLogger install failed (non-fatal): {logEx.GetType().Name}: {logEx.Message}");
            }
            // Splash shown BEFORE every heavy step so the user gets immediate
            // feedback. Without this the user saw a 1.5-7 sec dead-time between
            // erwin's model-load completing and the add-in's first paint
            // (CheckLicense ~1s + ctor ~500ms + Show ~200ms + LoadOpenModels
            // model-dependent wait). Form is disposed by ConnectToModel after
            // it consumes it via AttachEarlySplash; if anything aborts before
            // then, the catch / early-return branches dispose it explicitly.
            Form earlySplash = null;
            try { earlySplash = ModelConfigForm.ShowLoadingDialog("Initializing add-in..."); }
            catch (Exception ex) { Services.AddinLogger.Log($"Early splash creation failed (non-fatal): {ex.GetType().Name}: {ex.Message}"); }
            try
            {
                // Suppress WinForms UIA event raise (Button.OnClick,
                // TextBox.SetFocus, etc) BEFORE any control is created.
                // erwin r10.10 vendor bug: EM_PSF/OLEACC NULL-deref on UIA
                // events when active diagram has ~280 entities (verified
                // 2026-05-05/06/07, three crashes at coreclr.dll+0x36852a).
                // .NET 10 default raises these events; legacy switch makes
                // WinForms skip them so the broadcast never reaches erwin's
                // broken UIA proxy. Idempotent / safe to re-call. Wrapped
                // in catch because addin must never fail to load over an
                // accessibility-switch problem.
                using (Services.AddinLogger.BeginScope("SuppressLegacyUiaEventRaise"))
                {
                    try
                    {
                        AppContext.SetSwitch("Switch.UseLegacyAccessibility", true);
                        AppContext.SetSwitch("Switch.System.Windows.Forms.AccessibilityImprovements.UseLegacyAccessibilityFeatures", true);
                        AppContext.SetSwitch("Switch.System.Windows.Forms.AccessibilityImprovements.UseLegacyAccessibilityFeatures.2", true);
                        AppContext.SetSwitch("Switch.System.Windows.Forms.AccessibilityImprovements.UseLegacyAccessibilityFeatures.3", true);
                        AppContext.SetSwitch("Switch.System.Windows.Forms.UseLegacyToolTipDisplay", true);
                    }
                    catch (Exception ex)
                    {
                        Services.AddinLogger.Log($"AppContext UIA-suppress switch failed (continuing): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                using (Services.AddinLogger.BeginScope("EnsureExceptionHandler"))
                    EnsureExceptionHandler();

                // Phase A spike: install native SyncModelCallback hook.
                // Safe no-op if the bridge DLL is missing or EM_ECX can't be found.
                using (Services.AddinLogger.BeginScope("NativeBridgeService.Install"))
                    Services.NativeBridgeService.Install(msg =>
                        System.Diagnostics.Debug.WriteLine(msg));

                ModelConfigForm.UpdateLoadingMessage(earlySplash, "Verifying license...");

                // License check on UI thread. Phase-3C parallel-on-thread-pool variant
                // was tried 2026-05-07 but reverted: LicensingService's AntiTamper
                // checks (debugger / timing fingerprints) misfire on a non-UI thread
                // context inside erwin's host process and the addin reported a
                // tampering-detected status to a paying user. The ~250 ms saving was
                // not worth the false-positive risk; license stays sequential.
                bool licenseOk;
                using (Services.AddinLogger.BeginScope("CheckLicense"))
                    licenseOk = CheckLicense();
                if (!licenseOk)
                {
                    Services.AddinLogger.Log("CheckLicense returned false - aborting load");
                    DisposeEarlySplash(ref earlySplash);
                    return;
                }

                // If form is already open, bring it to front
                if (_activeForm != null && !_activeForm.IsDisposed)
                {
                    Services.AddinLogger.Log("Active form already open - bringing to front (skipping init)");
                    _activeForm.TopMost = true;
                    _activeForm.BringToFront();
                    _activeForm.Activate();
                    _activeForm.TopMost = false;
                    DisposeEarlySplash(ref earlySplash);
                    return;
                }

                ModelConfigForm.UpdateLoadingMessage(earlySplash, "Loading model configuration...");

                // Create SCAPI connection
                Type scapiType;
                using (Services.AddinLogger.BeginScope("Type.GetTypeFromProgID(erwin9.SCAPI)"))
                    scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                if (scapiType == null)
                {
                    Services.AddinLogger.Log("erwin9.SCAPI ProgID not registered - aborting");
                    DisposeEarlySplash(ref earlySplash);
                    ShowTopMostMessage("Could not find erwin SCAPI!", "Error");
                    return;
                }

                dynamic scapi;
                using (Services.AddinLogger.BeginScope("Activator.CreateInstance(SCAPI)"))
                    scapi = Activator.CreateInstance(scapiType);

                // Drop the in-process bootstrap cache on every FRESH addin
                // invocation (form not already open). The .NET CLR AppDomain
                // hosting this COM addin stays alive across Tools > Add-Ins
                // "unload" + reload cycles, so DatabaseService.Instance's
                // BootstrapConfig (captured at first Execute) would otherwise
                // pin the OLD HKLM/HKCU registry values forever - admins
                // editing the registry had to restart erwin or hit the dev-
                // only Reload Config button (user-reported 2026-05-30). The
                // ClearCache is cheap (~ms registry stat) and only runs on
                // fresh form creation, not on bring-to-front re-clicks.
                try
                {
                    Services.DatabaseService.Instance.ClearCache();
                    Services.AddinLogger.Log("Execute: bootstrap cache cleared - registry will be re-read on form init.");
                }
                catch (Exception ex)
                {
                    Services.AddinLogger.Log($"Execute: ClearCache err: {ex.GetType().Name}: {ex.Message}");
                }

                using (Services.AddinLogger.BeginScope("new ModelConfigForm(scapi)"))
                    _activeForm = new ModelConfigForm(scapi);
                _activeForm.StartPosition = FormStartPosition.CenterScreen;
                _activeForm.TopMost = true;
                // ForceClose during init can dispose the form before this
                // event fires; guard against the resulting ObjectDisposedException.
                _activeForm.Shown += (s, e) =>
                {
                    var form = (Form)s;
                    if (!form.IsDisposed) form.TopMost = false;
                };
                // Hand the splash to the form so its first ConnectToModel reuses
                // it (one continuous splash from add-in start to model open).
                // After this call ownership transfers to ModelConfigForm.
                _activeForm.AttachEarlySplash(earlySplash);
                earlySplash = null;
                using (Services.AddinLogger.BeginScope("ModelConfigForm.Show"))
                    _activeForm.Show();
            }
            catch (Exception ex)
            {
                DisposeEarlySplash(ref earlySplash);
                Services.AddinLogger.Log($"Execute() FAILED: {ex.GetType().Name}: {ex.Message}");
                ShowTopMostMessage("Add-In Error: " + ex.Message, "Error");
            }
        }

        // Defensive cleanup for the early splash on Execute() error / early-return
        // paths. After AttachEarlySplash hands it to ModelConfigForm, ownership
        // transfers (caller sets local to null) and this is a no-op.
        private static void DisposeEarlySplash(ref Form splash)
        {
            try
            {
                if (splash != null && !splash.IsDisposed)
                {
                    splash.Close();
                    splash.Dispose();
                }
            }
            catch (Exception ex) { Services.AddinLogger.Log($"DisposeEarlySplash err (non-fatal): {ex.GetType().Name}: {ex.Message}"); }
            finally { splash = null; }
        }

        /// <summary>
        /// Latched once the failure popup has been shown in this AppDomain.
        /// Even after the watcher's retry-storm fix, a user-driven re-click
        /// + a failing-license combo could still cause a second Execute()
        /// invocation; this guard suppresses the duplicate popup in that
        /// edge case. Gated at popup-show time (not at check-start) so a
        /// mid-session license fix + re-click still re-validates and can
        /// succeed. Cleared on AppDomain teardown (next erwin launch).
        /// </summary>
        private static bool _licenseFailurePopupShown;

        /// <summary>
        /// Validates hardware license. Returns true if valid, false otherwise.
        /// Wraps the UI-free check + shows the MessageBox on failure. Used by the
        /// startup path; the failure message is displayed on the calling thread,
        /// so this must be invoked from the UI thread (which Execute() is on).
        ///
        /// Retry policy: on LicenseStatus.ValidationTransient, silently retry once
        /// after a 1.5 s pause. xLicense returns ValidationTransient when its
        /// timing window check (AntiTamper.CheckGroup2_Timing) trips without a
        /// real debugger present — caused by AV first-scan, .NET tiered JIT,
        /// COM/SCAPI cold-init contention, or GC compaction. xLicense's
        /// HwidGenerator caches WMI results for the AppDomain, so the retry
        /// validates in &lt; 1 s. TamperingDetected (real debugger via
        /// CheckGroup1/CheckGroup3) gets no retry; the popup is correct there.
        /// </summary>
        private bool CheckLicense()
        {
            var sw = Stopwatch.StartNew();
            var (status, failureMessage) = CheckLicenseStatus();
            sw.Stop();

            if (status == LicenseStatus.Valid) return true;

            Services.AddinLogger.Log($"License check failed: status={status}, elapsed={sw.ElapsedMilliseconds}ms");

            // ValidationTransient = xLicense timing-window check fired but no real
            // debugger was detected (AntiTamper.CheckGroup2_Timing). Caused by AV
            // first-scan, GC compaction, COM/SCAPI cold-init contention. With the
            // HwidGenerator WMI cache the second Validate hits zero WMI calls, so
            // retry resolves in well under a second. TamperingDetected (real debugger
            // detection from CheckGroup1/CheckGroup3) gets no retry — the popup is
            // the correct outcome there.
            if (status == LicenseStatus.ValidationTransient)
            {
                Services.AddinLogger.Log("ValidationTransient - retrying once after 1500 ms");
                Thread.Sleep(1500);

                var retrySw = Stopwatch.StartNew();
                var (retryStatus, retryMessage) = CheckLicenseStatus();
                retrySw.Stop();

                Services.AddinLogger.Log($"License retry: status={retryStatus}, elapsed={retrySw.ElapsedMilliseconds}ms");

                if (retryStatus == LicenseStatus.Valid) return true;

                // Retry produced a different failure (or persistent transient).
                // Prefer the retry's message; it reflects the steady-state cause.
                failureMessage = retryMessage;
            }

            // De-dupe the popup but always run the underlying check. A
            // mid-session license fix + manual re-click should be able to
            // re-validate; latching CheckLicenseStatus itself would block
            // that recovery path.
            if (_licenseFailurePopupShown)
            {
                Services.AddinLogger.Log("CheckLicense: re-failed but popup already shown this session - suppressing duplicate dialog");
                return false;
            }
            _licenseFailurePopupShown = true;
            ShowTopMostMessage(failureMessage, "Elite Soft - License Error", isError: false);
            return false;
        }

        /// <summary>
        /// Phase-3C parallel-friendly variant: pure managed work (file read + decrypt),
        /// no UI calls. Runs safely on a thread-pool worker so it can overlap with the
        /// SCAPI activation + form constructor (~250 ms total) on the startup critical
        /// path. Returns (LicenseStatus.Valid, null) on success or (status, userFacingMessage)
        /// on failure; the caller renders the message on the UI thread.
        /// </summary>
        private static (LicenseStatus status, string failureMessage) CheckLicenseStatus()
        {
            if (LicensingService.IsValid)
                return (LicenseStatus.Valid, null);

            var assemblyLocation = typeof(ErwinAddIn).Assembly.Location;
            if (string.IsNullOrEmpty(assemblyLocation))
                assemblyLocation = AppContext.BaseDirectory;
            var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
            var licensePath = Path.Combine(assemblyDir, "license.lic");

            var status = LicensingService.Initialize(licensePath);

            if (status == LicenseStatus.Valid)
                return (LicenseStatus.Valid, null);

            string message = status switch
            {
                LicenseStatus.FileNotFound =>
                    "License file not found.\n\n" +
                    "Please contact Elite Soft to obtain a license file for this machine.\n\n" +
                    $"Machine ID: {LicensingService.GetCurrentHwid()}",
                LicenseStatus.Expired =>
                    "Your license has expired.\n\n" +
                    "Please contact Elite Soft to renew your license.",
                LicenseStatus.HwidMismatch =>
                    "This license is not valid for this machine.\n\n" +
                    "Please contact Elite Soft with your Machine ID to obtain a new license.\n\n" +
                    $"Machine ID: {LicensingService.GetCurrentHwid()}",
                LicenseStatus.DecryptionFailed =>
                    "License file is corrupted or not valid for this machine.\n\n" +
                    "Please contact Elite Soft to obtain a new license file.\n\n" +
                    $"Machine ID: {LicensingService.GetCurrentHwid()}",
                LicenseStatus.SignatureInvalid =>
                    "License file has been tampered with.\n\n" +
                    "Please contact Elite Soft to obtain a valid license file.",
                LicenseStatus.TamperingDetected =>
                    "License validation failed due to security check.\n\n" +
                    "Please close any debugging tools and try again.",
                LicenseStatus.ValidationTransient =>
                    "License validation could not complete due to system load.\n\n" +
                    "Please close other heavy applications and try opening the add-in again.",
                _ =>
                    "License validation failed.\n\n" +
                    "Please contact Elite Soft for assistance."
            };

            return (status, message);
        }

        /// <summary>
        /// Called by erwin - with application parameter
        /// </summary>
        public void Execute(object application)
        {
            Execute();
        }


        /// <summary>
        /// Add-in display name for erwin menu
        /// </summary>
        public string Name => "Elite Soft Erwin Add-In";

        /// <summary>
        /// Add-in description
        /// </summary>
        public string Description => "Elite Soft Erwin Model Configuration Add-In";
    }
}
