using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Steers replica aircraft/helicopters between snapshots by feeding the native
    /// flight model a chase point derived from the host stream, using the game's
    /// own external-steering mechanism: with <c>commandPositionOverride = true</c>,
    /// FixedWingFlightPhysics/VTOLFlightPhysics skip their waypoint steering and fly
    /// toward our <c>commandPosition</c> (the same hook carrier Approach and
    /// VTOLTakeOff use). Attitude/banking stay native - no robotic transform lerps.
    /// Altitude is NOT overridden: DesiredAltitude is synced by UnitReplicaDriver
    /// and the native controller tracks it.
    ///
    /// The injection happens in a prefix on the concrete OnFixedUpdate overrides,
    /// which run inside Aircraft.OnFixedUpdate right after the aircraft wrote its
    /// own CommandVelocity - so our values win for the integration step.
    /// Deck phases use other MotionController subclasses (taxi/catapult/takeoff),
    /// which we deliberately leave untouched.
    /// </summary>
    public static class AircraftReplicaDriver
    {
        private struct AirTarget
        {
            public Vector3 PosUnity;
            public float   SpeedKts;
            public float   HeadingDeg;
            public float   RecvRealtime;
        }

        // Unity units per (knot · game-second) - the game's own conversion
        // (see Missile: _velocityInUnity = _velocityInKnots * 0.0076554087f).
        private const float UnityPerKnotSecond = 0.0076554087f;

        // Chase point this many game-seconds ahead along the streamed track.
        private const float ChaseHorizonSec = 2.5f;

        // Forget targets that stop receiving updates (unit despawned/landed).
        private const float StaleAfterSec = 5f;

        private static readonly Dictionary<int, AirTarget> _targets = new();
        private static readonly Dictionary<MotionController, ObjectBase?> _ownerCache = new();

        /// <summary>Called by UnitReplicaDriver for every applied air-unit entry.</summary>
        public static void Report(ObjectBase unit, Vector3 streamPosUnity, float speedKts, float headingDeg)
        {
            _targets[unit.UniqueID] = new AirTarget
            {
                PosUnity     = streamPosUnity,
                SpeedKts     = speedKts,
                HeadingDeg   = headingDeg,
                RecvRealtime = Time.realtimeSinceStartup,
            };
        }

        public static void Forget(int unitId) => _targets.Remove(unitId);

        public static void Reset()
        {
            _targets.Clear();
            _ownerCache.Clear();
        }

        public static int ActiveTargets => _targets.Count;

        internal static void Steer(MotionController mc)
        {
            // Client-only, post-handshake; host aircraft fly natively
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsEstablished) return;

            if (!_ownerCache.TryGetValue(mc, out var owner))
            {
                owner = mc.GetComponentInParent<ObjectBase>();
                _ownerCache[mc] = owner;
                if (_ownerCache.Count > 256) _ownerCache.Clear(); // controllers are recreated; avoid leak
            }
            if (owner == null) return;

            if (!_targets.TryGetValue(owner.UniqueID, out var t))
            {
                if (mc.commandPositionOverride) mc.commandPositionOverride = false;
                return;
            }

            if (Time.realtimeSinceStartup - t.RecvRealtime > StaleAfterSec)
            {
                _targets.Remove(owner.UniqueID);
                if (mc.commandPositionOverride) mc.commandPositionOverride = false;
                return;
            }

            Vector3 dir = Quaternion.Euler(0f, t.HeadingDeg, 0f) * Vector3.forward;
            Vector3 chase = t.PosUnity + dir * (t.SpeedKts * UnityPerKnotSecond * ChaseHorizonSec);

            mc.commandPositionOverride = true;
            mc.commandPosition = chase;
            mc.CommandVelocity = t.SpeedKts * 0.514444f; // knots → m/s (overwrites SpeedCommand feed)
        }
    }

    [HarmonyPatch(typeof(FixedWingFlightPhysics), nameof(FixedWingFlightPhysics.OnFixedUpdate))]
    public static class Patch_FixedWingFlightPhysics_OnFixedUpdate
    {
        static void Prefix(FixedWingFlightPhysics __instance) => AircraftReplicaDriver.Steer(__instance);
    }

    [HarmonyPatch(typeof(VTOLFlightPhysics), nameof(VTOLFlightPhysics.OnFixedUpdate))]
    public static class Patch_VTOLFlightPhysics_OnFixedUpdate
    {
        static void Prefix(VTOLFlightPhysics __instance) => AircraftReplicaDriver.Steer(__instance);
    }
}
