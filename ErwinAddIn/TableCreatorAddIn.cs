using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ErwinAddIn
{
    /// <summary>
    /// erwin Data Modeler Add-In for creating tables
    /// Register with: regasm ErwinAddIn.dll /codebase
    /// </summary>
    [ComVisible(true)]
    [Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    [ProgId("ErwinAddIn.TableCreator")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class TableCreatorAddIn
    {
        public TableCreatorAddIn()
        {
        }

        private static TableCreatorForm _activeForm = null;

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
                _activeForm = new TableCreatorForm(scapi);
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
        public string Name => "Table Creator";

        /// <summary>
        /// Add-in description
        /// </summary>
        public string Description => "Creates new entities/tables in the model";
    }
}
