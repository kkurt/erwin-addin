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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MartConnectionForm());
        }
    }
}
