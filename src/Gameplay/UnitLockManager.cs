using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks which unit the remote player controls and which unit we've claimed
    /// control of in co-op. The Harmony patch on ObjectBase.IsControllable reads
    /// <see cref="IsLockedByRemote"/> to force the game's built-in ally handling
    /// for remote-controlled units on our side.
    ///
    /// "Controlling" = we broadcast a UnitSelected. "Spectating" = we selected a
    /// unit the remote already controls and stayed silent. Co-op only; ignored in PvP.
    /// </summary>
    public static class UnitLockManager
    {
        // Unit the remote player has claimed control of (0 = none).
        private static int _remoteLockedUnitId;

        // Unit we've claimed control of by broadcasting UnitSelected (0 = none).
        private static int _localControlledUnitId;

        public static int RemoteLockedUnitId => _remoteLockedUnitId;
        public static int LocalControlledUnitId => _localControlledUnitId;

        public static void SetLocalControlled(int unitId) => _localControlledUnitId = unitId;
        public static void ClearLocalControlled() => _localControlledUnitId = 0;

        /// <summary>Called when a UnitSelected event arrives from the remote player.</summary>
        public static void OnRemoteSelected(int unitId)
        {
            int previouslyLocked = _remoteLockedUnitId;
            _remoteLockedUnitId = unitId;
            Plugin.Log.LogDebug($"[UnitLock] Remote player selected unit {unitId} — marked uncontrollable locally.");

            if (previouslyLocked != 0 && previouslyLocked != unitId)
                MapUnitViewModelRegistry.NotifyLockChanged(previouslyLocked);
            if (unitId != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(unitId);

            // Remote switched between units - if we were spectating the released one, take over.
            TryAutoClaim(previouslyLocked, unitId);
        }

        /// <summary>Called when a UnitDeselected event arrives from the remote player.</summary>
        public static void OnRemoteDeselected()
        {
            int released = _remoteLockedUnitId;
            _remoteLockedUnitId = 0;
            Plugin.Log.LogDebug($"[UnitLock] Remote player deselected unit {released} — controllable restored.");

            if (released != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(released);

            TryAutoClaim(released, 0);
        }

        /// <summary>
        /// When the remote player releases a unit we're currently spectating locally,
        /// broadcast a UnitSelected so we become the controller without the user
        /// having to deselect-and-reselect.
        /// </summary>
        private static void TryAutoClaim(int releasedRemoteId, int newRemoteId)
        {
            if (releasedRemoteId == 0) return;
            if (releasedRemoteId == newRemoteId) return;
            if (_localControlledUnitId == releasedRemoteId) return;

            var rp = Singleton<RenderPosition>.Instance;
            var selected = rp?.SelectedObject;
            if (selected == null || selected.UniqueID != releasedRemoteId) return;

            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.UnitSelected,
                Param     = (float)releasedRemoteId,
            });
            _localControlledUnitId = releasedRemoteId;
            Plugin.Log.LogDebug($"[UnitLock] Auto-claimed control of unit {releasedRemoteId} after remote release.");
        }

        /// <summary>Returns true if the given unit is currently held by the remote player.</summary>
        public static bool IsLockedByRemote(int unitId)
        {
            return _remoteLockedUnitId != 0 && _remoteLockedUnitId == unitId;
        }

        /// <summary>
        /// The single ally-lock predicate: may this machine act on this unit at all?
        ///
        /// Every order patch used to re-implement this inline, and the ones that
        /// rolled their own send logic simply forgot - which is how orders the
        /// local player was refused still reached the other player. Anything that
        /// gates on the lock asks here.
        ///
        /// Orders arriving FROM the network are always allowed: that is the remote
        /// player commanding their own unit, which is the whole point of the lock.
        /// </summary>
        public static bool BlocksOrdersFor(ObjectBase? unit)
        {
            if (unit == null) return false;
            if (Plugin.Instance.CfgPvP.Value) return false;       // co-op concept only
            if (OrderHandler.ApplyingFromNetwork) return false;
            if (!NetworkManager.Instance.IsConnected) return false;
            return IsLockedByRemote(unit.UniqueID);
        }

        // ── Refusal feedback ─────────────────────────────────────────────────
        //
        // The lock is enforced by silently dropping the order in OrderSyncHelper.
        // On the client the game also renders the unit as uncontrollable
        // (Patch_ObjectBase_IsControllable), so the refusal at least looks
        // deliberate. On the HOST that override is deliberately not applied - it
        // would make the game drop queued fires mid-tick - so the unit still looks
        // controllable, the click is accepted, and nothing happens. That is how
        // queued waypoints on an ally-held P-3 looked like a lost order rather
        // than a lock. Record the refusal so the overlay can say so.

        /// <summary>Name of the unit whose order was last refused by the lock.</summary>
        public static string LastRefusedUnitName { get; private set; } = "";

        /// <summary>Unscaled time the message should stop being shown.</summary>
        public static float RefusalNoticeUntil { get; private set; }

        private const float NoticeSeconds = 3f;
        private static float _nextRefusalLog;

        /// <summary>Called when the ally lock refuses an order. Safe to call every
        /// frame - a waypoint drag fires continuously, so the notice just keeps
        /// refreshing its own expiry and the log line is throttled.</summary>
        public static void NoteOrderRefused(ObjectBase? unit)
        {
            LastRefusedUnitName = unit == null ? "" : unit.Name?.Value ?? unit.name;
            RefusalNoticeUntil  = UnityEngine.Time.unscaledTime + NoticeSeconds;

            if (UnityEngine.Time.unscaledTime < _nextRefusalLog) return;
            _nextRefusalLog = UnityEngine.Time.unscaledTime + NoticeSeconds;
            Plugin.Log.LogInfo($"[UnitLock] Order refused for {LastRefusedUnitName} - your ally is commanding it.");
        }

        /// <summary>Clear lock state on disconnect.</summary>
        public static void Reset()
        {
            int released = _remoteLockedUnitId;
            _remoteLockedUnitId = 0;
            _localControlledUnitId = 0;
            LastRefusedUnitName = "";
            RefusalNoticeUntil = 0f;
            if (released != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(released);
            Plugin.Log.LogDebug("[UnitLock] Reset.");
        }
    }
}
