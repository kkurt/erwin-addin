using System;
using System.Windows.Forms;

namespace EliteSoft.Erwin.Admin
{
    static class Program
    {
        /// <summary>
        /// Elite Soft Erwin Admin - Standalone application for administrative tasks
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Global exception handlers to prevent application crashes
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new MartConnectionForm());
            }
            catch (Exception ex)
            {
                ShowErrorDialog("Application Error", ex);
            }
        }

        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            ShowErrorDialog("Unhandled Thread Exception", e.Exception);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                ShowErrorDialog("Unhandled Domain Exception", ex);
            }
        }

        private static void ShowErrorDialog(string title, Exception ex)
        {
            string errorMessage = $"An unexpected error occurred:\n\n{ex.Message}\n\nDetails:\n{ex.GetType().Name}\n\nStack Trace:\n{ex.StackTrace}";

            MessageBox.Show(
                errorMessage,
                $"Elite Soft Erwin Admin - {title}",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }
}
