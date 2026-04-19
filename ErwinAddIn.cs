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
    [ProgId("EliteSoft.Erwin.AddIn")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class ErwinAddIn
    {
        private static ModelConfigForm _activeForm = null;
        private static bool _exceptionHandlerInstalled = false;

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
        /// Called by erwin - parameterless version
        /// </summary>
        public void Execute()
        {
            try
            {
                EnsureExceptionHandler();

                // Phase A spike: install native SyncModelCallback hook.
                // Safe no-op if the bridge DLL is missing or EM_ECX can't be found.
                Services.NativeBridgeService.Install(msg =>
                    System.Diagnostics.Debug.WriteLine(msg));

                // License check
                if (!CheckLicense())
                    return;

                // If form is already open, bring it to front
                if (_activeForm != null && !_activeForm.IsDisposed)
                {
                    _activeForm.TopMost = true;
                    _activeForm.BringToFront();
                    _activeForm.Activate();
                    _activeForm.TopMost = false;
                    return;
                }

                // Create SCAPI connection
                Type scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                if (scapiType == null)
                {
                    ShowTopMostMessage("Could not find erwin SCAPI!", "Error");
                    return;
                }

                dynamic scapi = Activator.CreateInstance(scapiType);

                _activeForm = new ModelConfigForm(scapi);
                _activeForm.StartPosition = FormStartPosition.CenterScreen;
                _activeForm.TopMost = true;
                _activeForm.Shown += (s, e) => { ((Form)s).TopMost = false; };
                _activeForm.Show();
            }
            catch (Exception ex)
            {
                ShowTopMostMessage("Add-In Error: " + ex.Message, "Error");
            }
        }

        /// <summary>
        /// Validates hardware license. Returns true if valid, false otherwise.
        /// </summary>
        private bool CheckLicense()
        {
            // Skip if already validated and cached
            if (LicensingService.IsValid)
                return true;

            // License file is in the same directory as the DLL
            var assemblyLocation = typeof(ErwinAddIn).Assembly.Location;
            if (string.IsNullOrEmpty(assemblyLocation))
                assemblyLocation = AppContext.BaseDirectory;
            var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
            var licensePath = Path.Combine(assemblyDir, "license.lic");

            var status = LicensingService.Initialize(licensePath);

            if (status == LicenseStatus.Valid)
                return true;

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
                _ =>
                    "License validation failed.\n\n" +
                    "Please contact Elite Soft for assistance."
            };

            ShowTopMostMessage(message, "Elite Soft - License Error", isError: false);

            return false;
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
