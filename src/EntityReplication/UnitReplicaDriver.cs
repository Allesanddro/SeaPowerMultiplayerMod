using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// v2 client-side unit state application (replaces StateApplier's unit path).
    /// The host streams ALL units (unified host authority); the client applies
    /// every entry - including its own taskforce - using the v1-proven hybrid
    /// model: local propulsion/flight physics integrate between snapshots, the
    /// stream feeds command-state (telegraph/rudder/desired altitude) so local
    /// sim targets what the host commanded, and position/heading/speed
    /// corrections snap or lerp by drift tier.
    /// </summary>
    public static class UnitReplicaDriver
    {
        // ── Correction tiers (ported from v1 StateApplier - proven in live play) ──
        private const float ShipSnapThreshold = 75f;
        private const float ShipPosLerp       = 0.7f;
        private const float ShipHeadingLerp   = 0.8f;
        private const float ShipSpeedLerp     = 0.7f;
        private const float AirSnapThreshold  = 150f;
        private const float AirPosLerp        = 0.75f;
        private const float AirHeadingLerp    = 0.85f;
        private const float AirSpeedLerp      = 0.7f;

        // Compiled setter for Vessel._setRudderAngle (autopilot steering target).
        // SetRudderToHeading writes this field directly, so a method patch can't
        // feed it - we mirror the host's value so local propulsion turns the
        // same way between corrections.
        private static readonly Action<Vessel, float>? _setRudderAngle;

        static UnitReplicaDriver()
        {
            var field = AccessTools.Field(typeof(Vessel), "_setRudderAngle");
            if (field != null)
            {
                var vParam = Expression.Parameter(typeof(Vessel));
                var fParam = Expression.Parameter(typeof(float));
                var assign = Expression.Assign(Expression.Field(vParam, field), fParam);
                _setRudderAngle = Expression.Lambda<Action<Vessel, float>>(assign, vParam, fParam).Compile();
            }
        }

        // ── Alignment (first batch after scene load re-keys local IDs) ───────
        private static bool _pendingAlignment;
        public static void SetPendingAlignment() => _pendingAlignment = true;

        // Last applied server tick (drop stale/reordered unreliable packets per entity batch)
        private static uint _lastServerTick;

        public static void Apply(EntityStateBatchMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            // Unreliable channel: tolerate reorder within a small window, drop very stale
            if (msg.ServerTick + 10 < _lastServerTick) { Telemetry.Count("v2.staleBatchDropped"); return; }
            if (msg.ServerTick > _lastServerTick) _lastServerTick = msg.ServerTick;

            if (_pendingAlignment && msg.Entries.Count > 0)
            {
                _pendingAlignment = false;
                RunAlignment(msg);
                return; // apply starts next batch, on aligned IDs
            }

            float shipDriftSum = 0f, shipDriftMax = 0f; int shipCount = 0;
            float airDriftSum  = 0f, airDriftMax  = 0f; int airCount  = 0;

            // Keep the client's auto-defence switch asserted (cheap re-check)
            Suppression.EnforceDefenseFlag();

            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];

                // Weapon kinds route to the kinematic replica driver
                if (e.Kind == UnitType.Missile || e.Kind == UnitType.Torpedo || e.Kind == UnitType.Bomb)
                {
                    WeaponReplicaDriver.OnSample(in e);
                    continue;
                }

                var unit = ReplicaRegistry.Find(e.EntityId);
                if (unit == null)
                {
                    unit = StateSerializer.FindById(e.EntityId);
                    if (unit != null)
                        ReplicaRegistry.Register(e.EntityId, unit, ReplicaPolicy.LocalMotionUnit);
                }
                if (unit == null)
                {
                    Telemetry.Count("v2.unknownUnitId"); // census self-heals missed spawns
                    continue;
                }
                if (unit is WeaponBase) continue; // ID collision safety

                // Deck puppets: a world-space sample means the host flew this unit
                // off (or it's a stale pre-touchdown packet) - the driver decides
                // and either flips it airborne or swallows the sample.
                if ((e.Kind == UnitType.Aircraft || e.Kind == UnitType.Helicopter)
                    && DeckPuppetDriver.HandleWorldSample(unit, in e)) continue;

                // ── Destruction / sinking (host-decided) ──────────────────────
                var comps = unit.Compartments;
                bool sinking   = (e.Flags & EntityState.FlagSinking)   != 0;
                bool destroyed = (e.Flags & EntityState.FlagDestroyed) != 0;

                if (sinking && comps != null && !comps._isSinking)
                {
                    comps.Sink(Compartments.SinkFocus.All, false);
                    continue;
                }
                if (comps != null && comps._isSinking) continue; // let the animation play
                if (destroyed && !unit.IsDestroyed)
                {
                    CombatEventHandler.DestroyFromNetwork(unit);
                    continue;
                }

                // ── Decode (only for entries that survived the filters above) ─
                var geo = new GeoPosition(e.LatDeg, e.LonDeg, e.HeightM);
                Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                Vector3 hostPos = new Vector3(local.x, e.HeightM, local.y);

                float heading = GeoCodec.UnpackHeading(e.HeadingQ);
                float pitch   = GeoCodec.UnpackAngleCdeg(e.PitchQ);
                float roll    = GeoCodec.UnpackAngleCdeg(e.RollQ);
                float speed   = GeoCodec.UnpackSpeedKts(e.SpeedQ);

                // ── Command-state feed (local sim targets host's commands) ───
                ApplyCommandState(unit, in e);

                bool isAir = e.Kind == UnitType.Aircraft || e.Kind == UnitType.Helicopter;

                // Submarines: local depth physics drives y toward the streamed
                // DesiredAltitude; don't fight it with vertical position snaps.
                if (e.Kind == UnitType.Submarine)
                    hostPos.y = unit.transform.position.y;

                if (isAir)
                {
                    AircraftReplicaDriver.Report(unit, hostPos, speed, heading);

                    bool isOnDeck = e.HeightM < 2.0f;
                    if (isOnDeck)
                    {
                        hostPos.y = e.HeightM;
                    }
                    else
                    {
                        float yDrift  = Mathf.Abs(unit.transform.position.y - e.HeightM);
                        float xzDrift = Vector2.Distance(
                            new Vector2(unit.transform.position.x, unit.transform.position.z),
                            new Vector2(hostPos.x, hostPos.z));

                        if (yDrift < 50f && xzDrift < 50f)
                        {
                            hostPos = unit.transform.position; // accept zone - AFCS chases
                        }
                        else if (yDrift < 500f && xzDrift < 500f)
                        {
                            hostPos = Vector3.Lerp(unit.transform.position, hostPos, 0.15f);
                            hostPos.y = Mathf.Lerp(unit.transform.position.y, e.HeightM, 0.15f);
                        }
                        else
                        {
                            hostPos.y = e.HeightM;
                            Plugin.Log.LogWarning($"[UnitReplica] Aircraft {unit.name} drift " +
                                $"Y={yDrift:F0} XZ={xzDrift:F0} exceeded 500, force-snapped");
                        }
                    }
                }

                float snapThresh = isAir ? AirSnapThreshold : ShipSnapThreshold;
                float posLerp    = isAir ? AirPosLerp       : ShipPosLerp;
                float hdgLerp    = isAir ? AirHeadingLerp   : ShipHeadingLerp;
                float spdLerp    = isAir ? AirSpeedLerp     : ShipSpeedLerp;

                float drift = Vector3.Distance(unit.transform.position, hostPos);
                if (isAir)
                {
                    airDriftSum += drift;
                    if (drift > airDriftMax) airDriftMax = drift;
                    airCount++;
                }
                else
                {
                    shipDriftSum += drift;
                    if (drift > shipDriftMax) shipDriftMax = drift;
                    shipCount++;
                }

                if (drift > snapThresh)
                {
                    unit.transform.position = hostPos;
                    unit.transform.eulerAngles = new Vector3(pitch, heading, roll);
                    unit._velocityInKnots = speed;
                }
                else
                {
                    unit.transform.position = Vector3.Lerp(unit.transform.position, hostPos, posLerp);
                    float newHeading = Mathf.LerpAngle(unit.transform.eulerAngles.y, heading, hdgLerp);
                    float newPitch = isAir
                        ? Mathf.LerpAngle(unit.transform.eulerAngles.x, pitch, hdgLerp)
                        : unit.transform.eulerAngles.x;
                    float newRoll = isAir
                        ? Mathf.LerpAngle(unit.transform.eulerAngles.z, roll, hdgLerp)
                        : unit.transform.eulerAngles.z;
                    unit.transform.eulerAngles = new Vector3(newPitch, newHeading, newRoll);
                    unit._velocityInKnots = Mathf.Lerp(unit._velocityInKnots, speed, spdLerp);
                }
            }

            StateApplier.ReportDrift(
                shipCount > 0 ? shipDriftSum / shipCount : 0f, shipDriftMax,
                airCount  > 0 ? airDriftSum  / airCount  : 0f, airDriftMax);
        }

        /// <summary>Mirror the host's command-state so local sim targets it between corrections.</summary>
        private static void ApplyCommandState(ObjectBase unit, in EntityState e)
        {
            // Telegraph (vessels + subs) - only when changed; suppress patch re-send
            if ((e.Kind == UnitType.Vessel || e.Kind == UnitType.Submarine)
                && unit.getTelegraph() != e.Telegraph)
            {
                bool prev = OrderHandler.ApplyingFromNetwork;
                OrderHandler.ApplyingFromNetwork = true;
                try { unit.setTelegraph(e.Telegraph); }
                finally { OrderHandler.ApplyingFromNetwork = prev; }
            }

            // Rudder steering target (vessels) - direct field write, no patches fire
            if (unit is Vessel vessel && _setRudderAngle != null)
                _setRudderAngle(vessel, e.RudderQ / 2f);

            // Desired altitude / depth (aircraft, helicopters, submarines)
            if (e.Kind == UnitType.Aircraft || e.Kind == UnitType.Helicopter)
            {
                if (e.DesiredAlt > 0f)
                    unit.DesiredAltitude.Value = e.DesiredAlt;
            }
            else if (e.Kind == UnitType.Submarine)
            {
                if (e.DesiredAlt != 0f)
                    unit.DesiredAltitude.Value = e.DesiredAlt;
            }
        }

        private static void RunAlignment(EntityStateBatchMessage msg)
        {
            // Reuse the v1 alignment core (position-match → SetUniqueId two-pass)
            var units = new List<UnitState>(msg.Entries.Count);
            for (int i = 0; i < msg.Entries.Count; i++)
            {
                var e = msg.Entries[i];
                units.Add(new UnitState
                {
                    EntityId = e.EntityId,
                    Kind     = e.Kind,
                    X        = (float)e.LonDeg,
                    Y        = e.HeightM,
                    Z        = (float)e.LatDeg,
                });
            }
            StateApplier.RunAlignmentFromUnitStates(units);
            ReplicaRegistry.Clear(); // re-resolve under the aligned IDs
        }

        public static void Reset()
        {
            _pendingAlignment = false;
            _lastServerTick = 0;
        }
    }
}
