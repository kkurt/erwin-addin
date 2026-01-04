using System;
using System.Windows.Forms;

namespace EliteSoft.Erwin.Admin
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MartConnectionForm());
        }
    }
}
