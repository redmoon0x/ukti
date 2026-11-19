using System;

namespace Ukti
{
    // Unified entry point for the smoke/UI test suites. Each suite has its own
    // Main; this runner dispatches to it so one project can compile both.
    static class TestRunner
    {
        [STAThread]
        static int Main(string[] args)
        {

            if (AsrWorker.IsWorker(args)) return AsrWorker.Run(args);
            if (args.Length > 0 && args[0] == "--worker-owner-probe") return RuntimeSmoke.OwnerProbe(args);
            if (args.Length > 0 && args[0] == "--interaction") return InteractionTests.Run(args);
            if (args.Length > 0 && args[0] == "--audio-probe") return AudioCaptureTests.Probe(args);
            if (args.Length > 0 && args[0] == "--hotkey") return HotkeyInputTests.Run();
            bool ui = args.Length > 0 && args[0] == "--ui";
            if (ui)
            {
                string[] rest = new string[args.Length - 1];
                Array.Copy(args, 1, rest, 0, rest.Length);
                return UiTests.Entry(rest);
            }
            return RuntimeSmoke.Entry(args);
        }
    }
}
