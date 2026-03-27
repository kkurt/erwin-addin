using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using xHardwareLicensing;
using xHardwareLicensing.Core;

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

                // License check
                if (!CheckLicense())
                    return;

                // If form is already open, bring it to front
                if (_activeForm != null && !_activeForm.IsDisposed)
                {
                    _activeForm.BringToFront();
                    _activeForm.Activate();
                    return;
                }

                // Create SCAPI connection
                Type scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                if (scapiType == null)
                {
                    MessageBox.Show("Could not find erwin SCAPI!", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                dynamic scapi = Activator.CreateInstance(scapiType);

                // Show the form centered on screen
                _activeForm = new ModelConfigForm(scapi);
                _activeForm.StartPosition = FormStartPosition.CenterScreen;
                _activeForm.FormClosed += (s, e) => { _activeForm = null; };
                _activeForm.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Add-In Error: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
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

            MessageBox.Show(message, "Elite Soft - License Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

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
