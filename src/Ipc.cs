using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ukti
{
    // One-line inter-process signal.
    //
    // A second launch cannot show the first instance's window directly, so it
    // posts a registered window message that the running app listens for. This
    // keeps "open the exe again" behaving like a normal desktop application
    // without adding a socket or a service.
    static class Ipc
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string windowName);

        const string MessageName = "Ukti.ShowWindow";

        public static uint ShowMessage { get { return RegisterWindowMessage(MessageName); } }

        // Waits briefly for the running instance to consume the message.
        public static bool RequestShowWindow()
        {
            try
            {
                uint msg = ShowMessage;
                if (msg == 0) return false;

                // The running app keeps a hidden window under this title; finding
                // it means the instance is alive and able to respond.
                IntPtr target = FindWindow(null, WindowTitle);
                if (target == IntPtr.Zero)
                {
                    // Give a just-started instance a moment to create its window.
                    for (int i = 0; i < 20 && target == IntPtr.Zero; i++)
                    {
                        Thread.Sleep(100);
                        target = FindWindow(null, WindowTitle);
                    }
                }

                if (target == IntPtr.Zero) return false;
                return PostMessage(target, msg, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        public const string WindowTitle = "Ukti.MessageWindow";
    }
}
