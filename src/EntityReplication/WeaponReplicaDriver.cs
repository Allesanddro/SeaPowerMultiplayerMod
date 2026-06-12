using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Drives inert (KinematicWeapon) replicas on the client: dead-reckons each
    /// weapon from its latest host sample (position snapped per 10 Hz packet,
    /// extrapolated along heading/pitch between packets, in the game-time domain
    /// so pause and time compression behave), and pumps the visual side that the
    /// suppressed state machine would normally run - mesh switch (canister →
    /// main mesh + afterburner), booster/sustainer flight effects, engine audio.
    /// </summary>
    public static class WeaponReplicaDriver
    {
        // Unity units per (knot · game-second) - the game's own conversion
        private const float UnityPerKnotSecond = 0.0076554087f;
        private const float MaxExtrapolateSec  = 0.5f;
        private const float PosSmoothing       = 0.35f; // per-frame pull toward target
        private const float AngSmoothing       = 0.4f;

        private class Replica
        {
            public WeaponBase Weapon = null!;
            public bool IsMissile;
            // Latest host sample
            public double LonDeg, LatDeg;
            public float HeightM, HeadingDeg, PitchDeg, SpeedKts;
            public float GameTimeSinceSample;   // accumulated GameTime.deltaTime
            public float SampleRealtime;
            public bool HasSample;
        }

        private static readonly Dictionary<int, Replica> _replicas = new();
        private static readonly List<int> _toRemove = new();

        // State samples that arrived before their spawn message (unreliable vs reliable race)
        private static readonly Dictionary<int, (EntityState entry, float realtime)> _pendingSamples = new();
        private const float PendingSampleTtlSec = 2f;

        // ── Reflection for Missile's private effects pump ─────────────────────
        private static readonly Action<Missile, bool>? _scheduleFlightEffects = BuildScheduleFlightEffects();
        private static readonly Action<Missile>? _updateFlightEffects = BuildUpdateFlightEffects();
        private static readonly AccessTools.FieldRef<ObjectBase, ObjectSoundHandler>? _soundHandlerRef =
            AccessTools.FieldRefAccess<ObjectBase, ObjectSoundHandler>("_soundHandler");

        private static Action<Missile, bool>? BuildScheduleFlightEffects()
        {
            var m = AccessTools.Method(typeof(Missile), "ScheduleFlightEffects");
            if (m == null) return null;
            return (Action<Missile, bool>)Delegate.CreateDelegate(typeof(Action<Missile, bool>), m);
        }

        private static Action<Missile>? BuildUpdateFlightEffects()
        {
            var m = AccessTools.Method(typeof(Missile), "UpdateFlightEffects");
            if (m == null) return null;
            return (Action<Missile>)Delegate.CreateDelegate(typeof(Action<Missile>), m);
        }

        public static int ActiveReplicas => _replicas.Count;

        // ── Registration ──────────────────────────────────────────────────────

        public static void OnReplicaSpawned(WeaponBase wb, EntitySpawnMessage msg)
        {
            _replicas[msg.EntityId] = new Replica
            {
                Weapon     = wb,
                IsMissile  = wb is Missile,
                LonDeg     = msg.LonDeg,
                LatDeg     = msg.LatDeg,
                HeightM    = msg.HeightM,
                HeadingDeg = GeoCodec.UnpackHeading(msg.HeadingQ),
                PitchDeg   = GeoCodec.UnpackAngleCdeg(msg.PitchQ),
                SpeedKts   = GeoCodec.UnpackSpeedKts(msg.SpeedQ),
                SampleRealtime = Time.realtimeSinceStartup,
                HasSample  = true,
            };

            // Seed with a fresher pre-arrived sample if one raced the spawn
            if (_pendingSamples.TryGetValue(msg.EntityId, out var pending)
                && Time.realtimeSinceStartup - pending.realtime < PendingSampleTtlSec)
            {
                OnSample(in pending.entry);
            }
            _pendingSamples.Remove(msg.EntityId);
        }

        public static void OnReplicaDemoted(WeaponBase wb)
        {
            // Save-restored weapon demoted after a session sync: seed from its
            // current transform; the host stream takes over within a tick.
            var geo = Utils.worldPositionFromUnityToLongLat(wb.transform.position, Globals._currentCenterTile);
            _replicas[wb.UniqueID] = new Replica
            {
                Weapon     = wb,
                IsMissile  = wb is Missile,
                LonDeg     = geo._longitude,
                LatDeg     = geo._latitude,
                HeightM    = (float)geo._height,
                HeadingDeg = wb.transform.eulerAngles.y,
                PitchDeg   = Utils.WrapAngle(wb.transform.eulerAngles.x),
                SpeedKts   = wb._velocityInKnots,
                SampleRealtime = Time.realtimeSinceStartup,
                HasSample  = true,
            };
        }

        /// <summary>Called by UnitReplicaDriver for weapon-kind state entries.</summary>
        public static void OnSample(in EntityState e)
        {
            if (!_replicas.TryGetValue(e.EntityId, out var r))
            {
                if (!SpawnReplicator.IsTombstoned(e.EntityId))
                {
                    _pendingSamples[e.EntityId] = (e, Time.realtimeSinceStartup);
                    Telemetry.Count("v2.weaponSamplePending");
                }
                return;
            }

            r.LonDeg     = e.LonDeg;
            r.LatDeg     = e.LatDeg;
            r.HeightM    = e.HeightM;
            r.HeadingDeg = GeoCodec.UnpackHeading(e.HeadingQ);
            r.PitchDeg   = GeoCodec.UnpackAngleCdeg(e.PitchQ);
            r.SpeedKts   = GeoCodec.UnpackSpeedKts(e.SpeedQ);
            r.GameTimeSinceSample = 0f;
            r.SampleRealtime = Time.realtimeSinceStartup;
            r.HasSample = true;
        }

        public static void Forget(int entityId)
        {
            _replicas.Remove(entityId);
            _pendingSamples.Remove(entityId);
        }

        public static void Reset()
        {
            _replicas.Clear();
            _pendingSamples.Clear();
        }

        // ── Per-frame drive (called from Plugin.Update on the client) ─────────

        public static void Tick()
        {
            if (_replicas.Count == 0) return;
            if (Plugin.Instance.CfgIsHost.Value) return;

            float dt = GameTime.deltaTime; // 0 while paused; scales with compression

            foreach (var kv in _replicas)
            {
                var r = kv.Value;
                var wb = r.Weapon;
                if (wb == null || wb.IsDestroyed) { _toRemove.Add(kv.Key); continue; }
                if (!r.HasSample) continue;

                r.GameTimeSinceSample += dt;

                // Dead-reckon along the sampled track, capped
                float t = Mathf.Min(r.GameTimeSinceSample, MaxExtrapolateSec);
                Vector3 basePos = Utils.longLatToLocalV3(
                    new GeoPosition(r.LatDeg, r.LonDeg, r.HeightM), Globals._currentCenterTile);
                Vector3 dir = Quaternion.Euler(r.PitchDeg, r.HeadingDeg, 0f) * Vector3.forward;
                Vector3 target = basePos + dir * (r.SpeedKts * UnityPerKnotSecond * t);

                var tr = wb.transform;
                tr.position = Vector3.Lerp(tr.position, target, PosSmoothing);
                var e = tr.eulerAngles;
                tr.eulerAngles = new Vector3(
                    Mathf.LerpAngle(e.x, r.PitchDeg, AngSmoothing),
                    Mathf.LerpAngle(e.y, r.HeadingDeg, AngSmoothing),
                    Mathf.LerpAngle(e.z, 0f, AngSmoothing * 0.5f));

                wb._velocityInKnots = r.SpeedKts;
                wb._velocityInUnity = r.SpeedKts * UnityPerKnotSecond; // suppressed native update maintains this
                wb._velocityVecInUnity = dir * wb._velocityInUnity;   // map course leader reads this vector
                // The suppressed native update would maintain these: geo position
                // feeds the map/sensor/threat maths, and the reactive properties
                // feed the map UI (course/speed/altitude readouts - without them
                // the map shows the launch heading forever).
                wb._geoPosition = Utils.worldPositionFromUnityToLongLat(tr.position, Globals._currentCenterTile);
                wb.Heading.Value  = wb.getHeading();
                wb.Velocity.Value = wb.getVelocityInKnots();
                wb.Altitude.Value = wb.getHeightInFeet();
                wb._dt += dt; // generic time-since-launch - drives mesh-switch/effects timing

                // Kill the game's debug weapon trail if one exists (root-level
                // TrailRenderer, solid red line - created during the save-load
                // relaunch while DM._showWeaponTrails was on, before demotion).
                var dbgTrail = tr.GetComponent<TrailRenderer>();
                if (dbgTrail != null && dbgTrail.enabled) dbgTrail.enabled = false;

                if (r.IsMissile)
                    PumpMissileVisuals((Missile)wb);

                // Audio follows the weapon
                if (_soundHandlerRef != null)
                {
                    var sh = _soundHandlerRef(wb);
                    sh?.OnUpdate();
                }
            }

            if (_toRemove.Count > 0)
            {
                for (int i = 0; i < _toRemove.Count; i++) _replicas.Remove(_toRemove[i]);
                _toRemove.Clear();
            }

            // Prune stale pending samples
            if (_pendingSamples.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                _toRemove.Clear();
                foreach (var kv in _pendingSamples)
                    if (now - kv.Value.realtime > PendingSampleTtlSec) _toRemove.Add(kv.Key);
                for (int i = 0; i < _toRemove.Count; i++) _pendingSamples.Remove(_toRemove[i]);
                _toRemove.Clear();
            }
        }

        /// <summary>
        /// The visual progression Missile.Launch.takeAction would run: canister →
        /// main mesh (+ AFTERBURNER submodels) at the resource-defined switch time,
        /// booster/sustainer effect scheduling, per-frame effect/audio updates.
        /// </summary>
        private static void PumpMissileVisuals(Missile m)
        {
            var wi = m._weaponInstance;
            if (wi != null && !m._launchObjectSwitched && m._dt > wi._resourcesMeshSwitchTime)
            {
                m._launchObjectSwitched = true;
                if (wi._mainMesh != null) wi._mainMesh.SetActive(true);
                if (wi._mainMeshForLaunch != null) wi._mainMeshForLaunch.SetActive(false);
                if (wi._mainMeshCanister != null) wi._mainMeshCanister.SetActive(false);
                if (wi._subModels != null)
                {
                    foreach (var sub in wi._subModels)
                    {
                        if (sub._type == WeaponSubModel.Type.AFTERBURNER && sub._subModel != null)
                            sub._subModel.gameObject.SetActive(true);
                    }
                }
            }

            if (!m._isBoosterEffectStarted)
                _scheduleFlightEffects?.Invoke(m, false);

            _updateFlightEffects?.Invoke(m);

            // Booster burnout: vanilla cuts the booster in the flight-stage logic
            // we suppress, and UpdateFlightEffects never ends it (endDelay = -1) -
            // without this the booster flame/trail burns for the whole flight.
            // IsMotorBurning runs the real thrust curve, so the cut lands on
            // vanilla timing.
            if (m._isBoosterEffectStarted && m._boosterEffect != null && !m.IsMotorBurning)
            {
                // Components can sit on children of the effect instance - stop them all.
                foreach (var ps in m._boosterEffect.GetComponentsInChildren<ParticleSystem>())
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                foreach (var trail in m._boosterEffect.GetComponentsInChildren<TrailRenderer>())
                    trail.emitting = false;
                m._weaponInstance?._boosterAudioSource?.Stop();
                m._boosterEffect = null;
            }
        }
    }
}
