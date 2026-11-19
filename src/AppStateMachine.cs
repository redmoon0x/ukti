using System;
using System.Collections.Generic;

namespace Ukti
{
    enum AppState { Loading, Idle, Recording, Working, Unloading, Sleeping, Waking, Faulted }

    // Single authority for legal state transitions. Every transition is checked
    // here instead of scattered `_state != X` guards across the controller.
    sealed class AppStateMachine
    {
        static readonly Dictionary<AppState, AppState[]> Allowed = new Dictionary<AppState, AppState[]>
        {
            { AppState.Loading,   new[] { AppState.Idle, AppState.Faulted, AppState.Unloading } },
            { AppState.Idle,      new[] { AppState.Recording, AppState.Working, AppState.Unloading, AppState.Loading, AppState.Faulted } },
            { AppState.Recording, new[] { AppState.Idle, AppState.Working, AppState.Faulted } },
            { AppState.Working,   new[] { AppState.Idle, AppState.Waking, AppState.Faulted } },
            { AppState.Unloading, new[] { AppState.Sleeping, AppState.Idle, AppState.Faulted } },
            { AppState.Sleeping,  new[] { AppState.Waking, AppState.Idle, AppState.Faulted, AppState.Loading } },
            { AppState.Waking,    new[] { AppState.Recording, AppState.Idle, AppState.Working, AppState.Faulted } },
            { AppState.Faulted,   new[] { AppState.Idle, AppState.Loading, AppState.Sleeping, AppState.Unloading } },
        };

        public AppState Current { get; private set; }
        public event Action<AppState, AppState> Changed;
        public AppStateMachine() { Current = AppState.Loading; }
        public bool CanTransition(AppState next)
        {
            if (next == Current) return true;
            AppState[] allowed;
            return Allowed.TryGetValue(Current, out allowed) && Array.IndexOf(allowed, next) >= 0;
        }

        public bool TryTransition(AppState next)
        {
            if (!CanTransition(next)) return false;
            AppState previous = Current;
            Current = next;
            if (Changed != null) Changed(previous, next);
            return true;
        }

        // Test seam: state is set directly for setup, not a runtime transition.
        internal void Reset(AppState state) { Current = state; }
    }
}
