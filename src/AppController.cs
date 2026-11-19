using System;

namespace Ukti
{
    // Owns the transition rules and idle-unload policy independently of the
    // WinForms view. All decisions are pure functions of the current state and
    // the supplied inputs, so they can be unit-tested without a microphone,
    // GPU, or message loop.
    sealed class AppController
    {
        readonly AppStateMachine _states = new AppStateMachine();
        readonly IAppLogger _logger;
        readonly ISettingsStore _settings;

        public AppState State { get { return _states.Current; } }
        public event Action<AppState, AppState> StateChanged;

        public AppController(IAppLogger logger, ISettingsStore settings)
        {
            _logger = logger;
            _settings = settings;
            _states.Changed += delegate (AppState from, AppState to)
            {
                if (_logger != null) _logger.Info("state " + from + " -> " + to);
                if (StateChanged != null) StateChanged(from, to);
            };
        }

        // Test seam: set state directly without logging a transition.
        internal void Reset(AppState state) { _states.Reset(state); }

        public bool TryTransition(AppState next) { return _states.TryTransition(next); }

        public bool CanStartRecording(bool exiting, bool enabled, bool settingsOpen, bool engineReady, bool micReady)
        {
            return !exiting && enabled && !settingsOpen && engineReady && micReady && State == AppState.Idle;
        }

        public bool CanWake(bool exiting, bool enabled, bool settingsOpen)
        {
            return !exiting && enabled && !settingsOpen && State == AppState.Sleeping;
        }

        public bool CanUnloadIdle(bool exiting, bool engineReady, bool settingsOpen, bool pastePending,
            bool modelLoaded, int idleMinutes, TimeSpan idle)
        {
            return !exiting && (State == AppState.Idle || State == AppState.Faulted) && engineReady && !pastePending &&
                modelLoaded && IdleUnloadPolicy.IsDue(idleMinutes, idle);
        }

        public bool CanPaste(bool exiting, bool enabled, int version, int captureVersion)
        {
            return !exiting && enabled && version == captureVersion;
        }

        public bool ShouldLoadModel(AppState state)
        {
            return state != AppState.Recording && state != AppState.Working &&
                   state != AppState.Waking && state != AppState.Unloading;
        }
    }
}
