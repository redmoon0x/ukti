using System;
using System.Reflection;
using System.Windows.Forms;

namespace Ukti
{
    // Regression for the ContextMenuStrip ObjectDisposedException: quitting
    // while the tray menu is open used to dispose the strip inside its own
    // modal menu loop, crashing WinForms' ModalMenuFilter.
    static class QuitMenuTests
    {
        [STAThread]
        internal static int RunQuitTest()
        {
            try
            {
                TrayApp app = new TrayApp(true, false);
                MethodInfo quit = typeof(TrayApp).GetMethod("Quit", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo trayField = typeof(TrayApp).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic);
                NotifyIcon tray = (NotifyIcon)trayField.GetValue(app);
                ContextMenuStrip menu = tray.ContextMenuStrip;
                IntPtr handle = menu.Handle; // force handle creation
                menu.Show(new System.Drawing.Point(100, 100));
                Application.DoEvents();
                quit.Invoke(app, null); // exactly the tray-Quit path
                Application.DoEvents();
                Console.WriteLine("PASS: quit with open tray menu did not throw.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
