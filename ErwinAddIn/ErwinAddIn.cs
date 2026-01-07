using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn
{
    /// <summary>
    /// Elite Soft Erwin Add-In - Model Configuration
    /// Register with: regasm EliteSoft.Erwin.AddIn.dll /codebase
    /// </summary>
    [ComVisible(true)]
    [Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    [ProgId("EliteSoft.Erwin.AddIn")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class ErwinAddIn
    {
        public ErwinAddIn()
        {
        }

        private static ModelConfigForm _activeForm = null;

        /// <summary>
        /// Called by erwin - parameterless version
        /// </summary>
        public void Execute()
        {
            try
            {
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

                // Show the form as non-modal (doesn't block other windows)
                _activeForm = new ModelConfigForm(scapi);
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
