using SeaPower;

namespace SeapowerMultiplayer
{
    public enum SimState
    {
        Idle,
        WaitingForClient,
        Synchronized,
    }

    /// <summary>
    /// Coordinates the synchronized simulation lifecycle.
    /// Tracks whether both sides have loaded and are ready to run.
    /// </summary>
    public static class SimSyncManager
    {
        private static SimState _currentState = SimState.Idle;
        public static SimState CurrentState
        {
            get => _currentState;
            set
            {
                if (_currentState != value)
                {
                    Plugin.Log.LogInfo($"[SimSync] State transition: {_currentState} → {value}");
                    _currentState = value;
                }
            }
        }

        public static bool BothSidesReady { get; set; }

        // ── Issue banner ──────────────────────────────────────────────────────
        // A failed session sync used to leave no trace outside the BepInEx log:
        // CurrentState fell back to Idle and the overlay simply stopped drawing a
        // sync line, so a broken sync looked identical to a healthy connection.
        // These fields survive Reset() so the overlay can keep showing what went
        // wrong until the next sync attempt starts.

        /// <summary>Short issue line for the overlay, or null when healthy.</summary>
        public static string? IssueMessage { get; private set; }

        /// <summary>Optional second line with detail or a suggested fix.</summary>
        public static string? IssueHint { get; private set; }

        /// <summary>True when the issue is transient (retry in progress) rather than fatal.</summary>
        public static bool IssueIsWarning { get; private set; }

        public static bool HasIssue => IssueMessage != null;

        public static void ReportIssue(string message, string hint = "", bool warning = false)
        {
            IssueMessage   = message;
            IssueHint      = string.IsNullOrEmpty(hint) ? null : hint;
            IssueIsWarning = warning;
            if (warning) Plugin.Log.LogWarning($"[SimSync] Issue: {message} {hint}");
            else         Plugin.Log.LogError($"[SimSync] Issue: {message} {hint}");
        }

        public static void ClearIssue()
        {
            if (IssueMessage == null) return;
            Plugin.Log.LogInfo("[SimSync] Issue cleared");
            IssueMessage   = null;
            IssueHint      = null;
            IssueIsWarning = false;
        }

        /// <summary>
        /// Resets the sync lifecycle. Deliberately leaves the issue banner alone -
        /// failure paths call Reset() right after reporting, and the player still
        /// needs to see why the sync failed.
        /// </summary>
        public static void Reset()
        {
            Plugin.Log.LogInfo("[SimSync] Reset()");
            CurrentState = SimState.Idle;
            BothSidesReady = false;
        }

        /// <summary>
        /// Called on host when a SessionReady message arrives from the client.
        /// </summary>
        public static void OnClientReady()
        {
            BothSidesReady = true;
            CurrentState = SimState.Synchronized;
            ClearIssue();
            Plugin.Log.LogInfo($"[SimSync] Client ready — paused={GameTime.IsPaused()}, TC={GameTime.TimeCompression}");
        }
    }
}
