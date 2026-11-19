using System;
using System.Windows.Forms;

namespace Ukti
{
    // Thin seams around each native/OS dependency. The WinForms TrayApp is the
    // composition root; a controller can be unit-tested with fakes.
    interface IAsrEngine : IDisposable
    {
        string Version { get; }
        string Transcribe(float[] samples, int sampleRate);
    }

    interface IAudioCapture : IDisposable
    {
        int SampleRate { get; }
        float Level { get; }
        bool IsOpen { get; }
        bool IsCapturing { get; }
        int DeviceIndex { get; set; }
        void Open();
        void BeginCapture();
        float[] EndCapture();
    }

    interface IHotkeySource : IDisposable
    {
        bool Enabled { get; set; }
        bool IsHooked { get; }
        event MethodInvoker Pressed;
        event MethodInvoker Released;
        event MethodInvoker Cancelled;
        void Configure(Shortcut shortcut);
        bool Install(IntPtr windowHandle);
    }

    interface ITextInserter
    {
        string Paste(string text, TargetIdentity target);
        bool CopyToClipboard(string text);
        bool ModifiersReleased();
        IntPtr CurrentForegroundWindow();
    }

    interface ISettingsStore
    {
        Settings Load();
        void Save(Settings settings);
    }

    interface IAppLogger
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }
}
