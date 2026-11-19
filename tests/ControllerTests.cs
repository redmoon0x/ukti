using System;

namespace Ukti
{
    // Real unit coverage for the extracted controller: transitions, idle unload,
    // cancellation/precedence, and hotkey eligibility - no private-field poking.
    static class ControllerUnitTests
    {
        sealed class Logger : IAppLogger
        {
            public int Infos, Warnings, Errors;
            public void Info(string m) { Infos++; }
            public void Warning(string m) { Warnings++; }
            public void Error(string m) { Errors++; }
        }

        sealed class Store : ISettingsStore
        {
            public int Loads, Saves;
            public Settings Load() { Loads++; return new Settings(); }
            public void Save(Settings s) { Saves++; }
        }

        internal static void Run(Action<bool, string> assert)
        {
            Logger logger = new Logger();
            AppController c = new AppController(logger, new Store());

            // Legal transitions; illegal ones are rejected without mutating state.
            assert(c.State == AppState.Loading, "Controller starts in Loading.");
            assert(c.TryTransition(AppState.Idle), "Loading -> Idle is legal.");
            assert(c.State == AppState.Idle, "State updates after a legal transition.");
            assert(!c.TryTransition(AppState.Sleeping), "Idle -> Sleeping must go through Unloading.");
            assert(c.State == AppState.Idle, "Illegal transition leaves state unchanged.");
            assert(c.TryTransition(AppState.Recording), "Idle -> Recording is legal.");
            assert(!c.TryTransition(AppState.Waking), "Recording -> Waking is illegal.");
            assert(c.TryTransition(AppState.Idle), "Recording can return to Idle.");
            assert(logger.Infos >= 3, "Transitions are logged through IAppLogger.");

            // Recording and wake eligibility.
            assert(c.CanStartRecording(false, true, false, true, true), "Ready app may start recording.");
            assert(!c.CanStartRecording(true, true, false, true, true), "Exiting app may not record.");
            assert(!c.CanStartRecording(false, false, false, true, true), "Disabled app may not record.");
            assert(!c.CanStartRecording(false, true, true, true, true), "Settings open blocks recording.");
            c.Reset(AppState.Sleeping);
            assert(c.CanWake(false, true, false), "Sleeping app may wake.");
            assert(!c.CanWake(false, false, false), "Disabled app may not wake.");

            // Idle unload policy gates.
            c.Reset(AppState.Idle);
            assert(c.CanUnloadIdle(false, true, false, false, true, 5, TimeSpan.FromMinutes(5)),
                "Due idle model should unload.");
            assert(!c.CanUnloadIdle(false, true, false, false, true, 5, TimeSpan.FromMinutes(4)),
                "Not due yet.");
            assert(c.CanUnloadIdle(false, true, true, false, true, 5, TimeSpan.FromHours(1)),
                "Open Settings must not pin an inactive model in memory.");
            c.Reset(AppState.Faulted);
            assert(c.CanUnloadIdle(false, true, false, false, true, 5, TimeSpan.FromHours(1)),
                "A microphone error must not pin model memory indefinitely.");
            assert(c.TryTransition(AppState.Unloading), "Faulted resident model may unload.");
            c.Reset(AppState.Idle);
            assert(!c.CanUnloadIdle(false, true, false, true, true, 5, TimeSpan.FromHours(1)),
                "Pending paste blocks unload.");
            assert(!c.CanUnloadIdle(false, false, false, false, true, 5, TimeSpan.FromHours(1)),
                "No engine blocks unload.");
            assert(!c.CanUnloadIdle(false, true, false, false, false, 5, TimeSpan.FromHours(1)),
                "No loaded model blocks unload.");

            // Result validity (Escape/disable/quit invalidate via version).
            assert(c.CanPaste(false, true, 7, 7), "Current result is eligible.");
            assert(!c.CanPaste(false, true, 6, 7), "Stale result is discarded.");
            assert(!c.CanPaste(false, false, 7, 7), "Disabled app discards results.");
            assert(!c.CanPaste(true, true, 7, 7), "Exiting app discards results.");

            assert(c.ShouldLoadModel(AppState.Idle) && !c.ShouldLoadModel(AppState.Recording) &&
                   !c.ShouldLoadModel(AppState.Working), "Model switching respects busy states.");
        }
    }
}
