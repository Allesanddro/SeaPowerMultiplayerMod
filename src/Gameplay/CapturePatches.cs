using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// HOST-side v2 capture hooks: every weapon that comes into existence, every
    /// weapon detonation/removal, and every unit kill is replicated as an event.
    /// CommonLaunchSettings is the universal launch funnel - every weapon class
    /// (incl. ASROC/RBU submunitions and jettisons) passes through it.
    /// </summary>
    public static class CaptureState
    {
        // Despawn dedup: ids whose EntityDespawn already went out
        internal static readonly HashSet<int> DespawnSent = new();
        internal static readonly HashSet<int> ImpactSent = new();

        // Every EntitySpawn the host sent, by id - replayed verbatim when the
        // client's census diff reports a missing entity (self-heal).
        internal static readonly Dictionary<int, EntitySpawnMessage> SpawnLedger = new();

        // Synchronized gate: pre-sync world state travels inside the session-sync
        // save; events only flow once both sides run the same session. Without it,
        // spawns sent while the client is still in the menu / loading arrive with
        // unresolvable taskforces and are dropped (permanently missing entities).
        internal static bool HostCaptureActive =>
            Plugin.Instance.CfgIsHost.Value
            && NetworkManager.Instance.IsEstablished
            && SimSyncManager.CurrentState == SimState.Synchronized
            && !SessionManager.SceneLoading;

        internal static void RecordSpawn(EntitySpawnMessage msg) => SpawnLedger[msg.EntityId] = msg;

        // Aircraft currently in the flight-deck launch pipeline - their airborne
        // flip (giveControl) re-sends the spawn with real wheels-up state.
        internal static readonly HashSet<int> DeckPhase = new();

        internal static void ForgetSpawn(int id)
        {
            SpawnLedger.Remove(id);
            DeckPhase.Remove(id);
        }

        /// <summary>Index of a weapon system within its unit (stable across machines
        /// - both build the list from the same ini), or -1.</summary>
        internal static int MountIndexOf(ObjectBase? unit, WeaponSystem ws)
            => unit?._obp?._weaponSystems?.IndexOf(ws) ?? -1;

        /// <summary>Shared event-throttle: true = suppressed (sent too recently).</summary>
        internal static bool Throttled<TKey>(Dictionary<TKey, float> lastSent, TKey key, float intervalSec)
        {
            float now = Time.unscaledTime;
            if (lastSent.TryGetValue(key, out float last) && now - last < intervalSec) return true;
            lastSent[key] = now;
            return false;
        }

        internal static void Clear()
        {
            DespawnSent.Clear();
            ImpactSent.Clear();
            SpawnLedger.Clear();
            DeckPhase.Clear();
        }

        /// <summary>Replicated weapon classes: missiles, torpedoes, sonobuoy bombs
        /// (LiveLocal), and ordinary bombs (kinematic replicas).</summary>
        internal static byte? WeaponClassOf(WeaponBase wb)
        {
            if (wb is Missile) return 0;
            if (wb is Torpedo) return 1;
            if (wb is Bomb && wb._ap != null && wb._ap._subType == Ammunition.Type.Sonobuoy) return 2;
            if (wb is Bomb) return 3;
            return null;
        }
    }

    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.CommonLaunchSettings))]
    public static class Patch_V2_WeaponLaunch_Capture
    {
        static void Postfix(WeaponBase __instance, ObjectBase targetObject, Vector3 targetPosition,
                            WeaponSystem weaponSystem, bool isSubmunition)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (__instance._ap == null) return;

            var weaponClass = CaptureState.WeaponClassOf(__instance);
            bool isDecoy = __instance is ChaffCloud || __instance is Noisemaker;
            if (weaponClass == null && !isDecoy) return; // gun shells etc. - cosmetic events handle those

            var geo = Utils.worldPositionFromUnityToLongLat(
                __instance.transform.position, Globals._currentCenterTile);
            var aim = __instance.AimPointGeoPosition;
            var shooter = StateSerializer.GetLaunchPlatform(__instance);

            var msg = new EntitySpawnMessage
            {
                Kind        = isDecoy ? SpawnKind.Decoy : SpawnKind.Weapon,
                EntityId    = __instance.UniqueID,
                WeaponClass = weaponClass ?? 0,
                AmmoName    = __instance._ap._ammunitionFileName,
                ShooterId   = shooter != null ? shooter.UniqueID : 0,
                TargetId    = targetObject != null ? targetObject.UniqueID : 0,
                LonDeg      = geo._longitude,
                LatDeg      = geo._latitude,
                HeightM     = (float)geo._height,
                HeadingQ    = GeoCodec.PackHeading(__instance.transform.eulerAngles.y),
                PitchQ      = GeoCodec.PackAngleCdeg(__instance.transform.eulerAngles.x),
                SpeedQ      = GeoCodec.PackSpeedKts(__instance._velocityInKnots),
                AimLonDeg   = aim._longitude,
                AimLatDeg   = aim._latitude,
                AimHeightM  = (float)aim._height,
                Flags       = isSubmunition ? EntitySpawnMessage.FlagSubmunition : (byte)0,
            };
            NetworkManager.Instance.BroadcastToClients(msg);
            CaptureState.RecordSpawn(msg);
            Telemetry.Count(isDecoy ? "v2.capturedDecoy" : "v2.capturedSpawn");
        }
    }

    /// <summary>Ship/aircraft chaff launches bypass CommonLaunchSettings entirely
    /// (launchChaff → ChaffAttacher.launchChaffCloud → ChaffCloud.launchChaffEffect
    /// sets _isLaunched directly), so the universal launch capture above never sees
    /// them - auto- and manual chaff never replicated. Capture them here.</summary>
    [HarmonyPatch(typeof(ChaffCloud), nameof(ChaffCloud.launchChaffEffect))]
    public static class Patch_V2_ChaffLaunch_Capture
    {
        static void Postfix(ChaffCloud __instance)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (__instance._ap == null) return;

            var geo = Utils.worldPositionFromUnityToLongLat(
                __instance.transform.position, Globals._currentCenterTile);
            var shooter = StateSerializer.GetLaunchPlatform(__instance);

            var msg = new EntitySpawnMessage
            {
                Kind      = SpawnKind.Decoy,
                EntityId  = __instance.UniqueID,
                AmmoName  = __instance._ap._ammunitionFileName,
                ShooterId = shooter != null ? shooter.UniqueID : 0,
                LonDeg    = geo._longitude,
                LatDeg    = geo._latitude,
                HeightM   = (float)geo._height,
                HeadingQ  = GeoCodec.PackHeading(__instance.transform.eulerAngles.y),
            };
            NetworkManager.Instance.BroadcastToClients(msg);
            CaptureState.RecordSpawn(msg);
            Telemetry.Count("v2.capturedDecoy");
        }
    }

    /// <summary>Weapon detonation → ImpactEvent. Destruction is virtual; Missile
    /// overrides it, so both bodies are patched (ImpactSent dedupes base calls).</summary>
    public static class Patch_V2_WeaponDestruction_Capture
    {
        internal static void OnDestruction(WeaponBase wb, Vector3 position, Quaternion rotation, ObjectBase targetObject)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (CaptureState.WeaponClassOf(wb) == null) return;
            if (wb.IsDestroyed) return; // Destruction body no-ops in that case too
            if (!CaptureState.ImpactSent.Add(wb.UniqueID)) return;

            var geo = Utils.worldPositionFromUnityToLongLat(position, Globals._currentCenterTile);
            var euler = rotation.eulerAngles;
            NetworkManager.Instance.BroadcastToClients(new ImpactEventMessage
            {
                WeaponId  = wb.UniqueID,
                HitUnitId = targetObject != null ? targetObject.UniqueID : 0,
                LonDeg    = geo._longitude,
                LatDeg    = geo._latitude,
                HeightM   = (float)geo._height,
                HeadingQ  = GeoCodec.PackHeading(euler.y),
                PitchQ    = GeoCodec.PackAngleCdeg(euler.x),
            });
            Telemetry.Count("v2.capturedImpact");
        }
    }

    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.Destruction))]
    public static class Patch_V2_WeaponBaseDestruction_Capture
    {
        static void Prefix(WeaponBase __instance, Vector3 position, Quaternion rotation, ObjectBase targetObject)
            => Patch_V2_WeaponDestruction_Capture.OnDestruction(__instance, position, rotation, targetObject);
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.Destruction))]
    public static class Patch_V2_MissileDestruction_Capture
    {
        static void Prefix(Missile __instance, Vector3 position, Quaternion rotation, ObjectBase targetObject)
            => Patch_V2_WeaponDestruction_Capture.OnDestruction(__instance, position, rotation, targetObject);
    }

    /// <summary>Universal weapon removal → EntityDespawn (covers fuel-out, pool
    /// free, scripted removal - anything that didn't go through Destruction).</summary>
    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.destroyObject))]
    public static class Patch_V2_WeaponDestroy_Capture
    {
        static void Postfix(WeaponBase __instance)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (CaptureState.WeaponClassOf(__instance) == null
                && !(__instance is ChaffCloud) && !(__instance is Noisemaker)) return;
            int id = __instance.UniqueID;
            if (!CaptureState.DespawnSent.Add(id)) return;

            var geo = Utils.worldPositionFromUnityToLongLat(
                __instance.transform.position, Globals._currentCenterTile);
            NetworkManager.Instance.BroadcastToClients(new EntityDespawnMessage
            {
                EntityId = id,
                Cause    = CaptureState.ImpactSent.Contains(id) ? DespawnCause.Impact : DespawnCause.Removed,
                LonDeg   = geo._longitude,
                LatDeg   = geo._latitude,
                HeightM  = (float)geo._height,
            });
            CaptureState.ForgetSpawn(id);
            Telemetry.Count("v2.capturedDespawn");
        }
    }

    /// <summary>Unit kills → reliable DestroyEvent (the state-stream flags are
    /// unreliable; this guarantees the client sees every kill).</summary>
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.setDestroyedFlag))]
    public static class Patch_V2_UnitDestroyed_Capture
    {
        static void Postfix(ObjectBase __instance)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (__instance is WeaponBase) return; // weapons ride Impact/Despawn
            if (!CaptureState.DespawnSent.Add(__instance.UniqueID)) return;

            NetworkManager.Instance.BroadcastToClients(new DestroyEventMessage
            {
                UnitId = __instance.UniqueID,
                Mode   = DestroyEventMessage.ModeInstantKill,
            });
            CaptureState.ForgetSpawn(__instance.UniqueID);
            Telemetry.Count("v2.capturedUnitKill");
        }
    }

    // ── P3: gun / CIWS firing cosmetics + ammo state ─────────────────────────

    [HarmonyPatch]
    public static class Patch_V2_GunFire_Capture
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GunBarrel), "LaunchProjectile");

        private static readonly AccessTools.FieldRef<GunBarrel, WeaponSystemGun>? _vwsgRef =
            AccessTools.FieldRefAccess<GunBarrel, WeaponSystemGun>("_vwsg");

        // Per (unit, mount) throttle: visuals only need a refresh, not per-round events
        private static readonly Dictionary<long, float> _lastSent = new();
        private const float ThrottleSec = 0.4f;

        internal static void Clear() => _lastSent.Clear();

        static void Postfix(GunBarrel __instance, Ammunition ammo)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (_vwsgRef == null) return;
            var gun = _vwsgRef(__instance);
            var unit = gun?._baseObject;
            if (gun == null || unit == null) return;

            int mountIdx = CaptureState.MountIndexOf(unit, gun);
            if (mountIdx < 0) return;

            long key = ((long)unit.UniqueID << 16) | (uint)(mountIdx & 0xFFFF);
            if (CaptureState.Throttled(_lastSent, key, ThrottleSec)) return;

            var dir = gun._solutionVector.normalized;
            float heading = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;

            NetworkManager.Instance.BroadcastToClients(new GunBurstEventMessage
            {
                ShooterId        = unit.UniqueID,
                MountIndex       = (short)mountIdx,
                Kind             = GunBurstKind.GunBurst,
                TargetId         = gun._targetObject != null ? gun._targetObject.UniqueID : 0,
                SolutionHeadingQ = GeoCodec.PackHeading(heading),
                SolutionPitchQ   = GeoCodec.PackAngleCdeg(pitch),
                ToTargetTime     = gun._projectileToTargetTime,
                AimLatDeg        = gun._projectileAimGeoPosition._latitude,
                AimLonDeg        = gun._projectileAimGeoPosition._longitude,
                AimHeightM       = (float)gun._projectileAimGeoPosition._height,
                AmmoName         = ammo?._ap?._ammunitionFileName ?? "",
            }, LiteNetLib.DeliveryMethod.Unreliable);
            Telemetry.Count("v2.capturedGunBurst");
        }
    }

    [HarmonyPatch(typeof(WeaponSystemCIWS), nameof(WeaponSystemCIWS.StartFire))]
    public static class Patch_V2_CiwsStart_Capture
    {
        private static readonly Dictionary<long, float> _lastSent = new();
        internal static void Clear() => _lastSent.Clear();

        static void Postfix(WeaponSystemCIWS __instance)
        {
            if (!CaptureState.HostCaptureActive) return;
            var unit = __instance._baseObject;
            if (unit == null) return;
            int mountIdx = CaptureState.MountIndexOf(unit, __instance);
            if (mountIdx < 0) return;

            long key = ((long)unit.UniqueID << 16) | (uint)(mountIdx & 0xFFFF);
            if (CaptureState.Throttled(_lastSent, key, 1f)) return;

            NetworkManager.Instance.BroadcastToClients(new GunBurstEventMessage
            {
                ShooterId  = unit.UniqueID,
                MountIndex = (short)mountIdx,
                Kind       = GunBurstKind.CiwsStart,
                TargetId   = __instance._currentClosestTarget != null
                                ? __instance._currentClosestTarget.UniqueID : 0,
            }, LiteNetLib.DeliveryMethod.Unreliable);
            Telemetry.Count("v2.capturedCiwsStart");
        }
    }

    [HarmonyPatch]
    public static class Patch_V2_CiwsStop_Capture
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(WeaponSystemCIWS), "StopEngage");

        static void Postfix(WeaponSystemCIWS __instance)
        {
            if (!CaptureState.HostCaptureActive) return;
            var unit = __instance._baseObject;
            if (unit == null) return;
            int mountIdx = CaptureState.MountIndexOf(unit, __instance);
            if (mountIdx < 0) return;

            NetworkManager.Instance.BroadcastToClients(new GunBurstEventMessage
            {
                ShooterId  = unit.UniqueID,
                MountIndex = (short)mountIdx,
                Kind       = GunBurstKind.CiwsStop,
            }, LiteNetLib.DeliveryMethod.Unreliable);
        }
    }

    /// <summary>Magazine expenditure → throttled authoritative count sync.</summary>
    public static class AmmoStateCapture
    {
        private static readonly Dictionary<(int unit, string ammo), float> _lastSent = new();
        private const float ThrottleSec = 1f;

        internal static void Clear() => _lastSent.Clear();

        internal static void OnMagazineChanged(WeaponMagazineSystem mag, string ammoName)
        {
            if (!CaptureState.HostCaptureActive) return;
            var unit = mag._baseObject;
            if (unit == null || string.IsNullOrEmpty(ammoName)) return;

            if (CaptureState.Throttled(_lastSent, (unit.UniqueID, ammoName), ThrottleSec)) return;

            NetworkManager.Instance.BroadcastToClients(new AmmoStateEventMessage
            {
                UnitId        = unit.UniqueID,
                AmmoName      = ammoName,
                MagazineCount = mag.getAmmunitionCount(ammoName),
            });
            Telemetry.Count("v2.capturedAmmoState");
        }
    }

    [HarmonyPatch(typeof(WeaponMagazineSystem), nameof(WeaponMagazineSystem.decreaseAmmunitionCount),
        new[] { typeof(string), typeof(bool) })]
    public static class Patch_V2_MagazineDecrease1_Capture
    {
        static void Postfix(WeaponMagazineSystem __instance, string ammunitionName)
            => AmmoStateCapture.OnMagazineChanged(__instance, ammunitionName);
    }

    [HarmonyPatch(typeof(WeaponMagazineSystem), nameof(WeaponMagazineSystem.decreaseAmmunitionCount),
        new[] { typeof(string), typeof(int), typeof(bool) })]
    public static class Patch_V2_MagazineDecrease2_Capture
    {
        static void Postfix(WeaponMagazineSystem __instance, string ammunitionName)
            => AmmoStateCapture.OnMagazineChanged(__instance, ammunitionName);
    }

    // ── P4: aircraft / helicopter lifecycle ───────────────────────────────────

    public static class AircraftSpawnCapture
    {
        internal static void OnUnitCreated(ObjectBase? result, UnitType kind, string squadronReference,
            string loadoutVariant, ObjectBase? homeBase, GameObject? parent, string iniName, int number,
            GeoPosition geoPosition, Vector3 heading, Taskforce? taskForce, string nationOverride)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (result == null) return;

            // Deck launch (FlightDeck passes the carrier root as parent): the game
            // creates these at the GEO CENTER placeholder with zero heading - use
            // the carrier's transform instead and flag the deck phase. The spawn is
            // re-sent with real wheels-up state from the giveControl capture below.
            bool deckLaunch = parent != null && homeBase != null;
            if (deckLaunch)
            {
                geoPosition = Utils.worldPositionFromUnityToLongLat(
                    homeBase!.transform.position, Globals._currentCenterTile);
                heading = homeBase.transform.eulerAngles;
                CaptureState.DeckPhase.Add(result.UniqueID);
            }

            var msg = new EntitySpawnMessage
            {
                Kind           = SpawnKind.Unit,
                EntityId       = result.UniqueID,
                LonDeg         = geoPosition._longitude,
                LatDeg         = geoPosition._latitude,
                HeightM        = (float)geoPosition._height,
                HeadingQ       = GeoCodec.PackHeading(heading.y),
                UnitKind       = (byte)kind,
                UnitIniName    = iniName,
                SquadronRef    = squadronReference,
                LoadoutVariant = loadoutVariant,
                UnitNumber     = (byte)Mathf.Clamp(number, 0, 255),
                HomeBaseId     = homeBase != null ? homeBase.UniqueID : 0,
                TaskforceSide  = taskForce != null ? (byte)taskForce.Side : (byte)0,
                Nation         = nationOverride ?? "",
                UnitFlags      = deckLaunch ? EntitySpawnMessage.UnitFlagDeckPhase : (byte)0,
            };
            NetworkManager.Instance.BroadcastToClients(msg);
            CaptureState.RecordSpawn(msg);
            Telemetry.Count("v2.capturedUnitSpawn");
            Plugin.Log.LogInfo($"[Capture] Replicating {kind} spawn id={result.UniqueID} ini={iniName} " +
                $"home={homeBase?.UniqueID ?? 0} deck={deckLaunch}");
        }

        /// <summary>Wheels-up: every fixed-wing/VTOL takeoff funnels through
        /// Aircraft.giveControl, every helo lift-off through Helicopter.GiveControl.
        /// Re-send the (ledger-updated) spawn with the real airborne state - the
        /// client flips its deck puppet to flight on receipt. Airborne-created
        /// units never enter DeckPhase, so this no-ops for them.</summary>
        internal static void OnAirborne(ObjectBase unit)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (unit == null) return;
            if (!CaptureState.DeckPhase.Remove(unit.UniqueID)) return;
            if (!CaptureState.SpawnLedger.TryGetValue(unit.UniqueID, out var spawn)) return;

            var geo = Utils.worldPositionFromUnityToLongLat(
                unit.transform.position, Globals._currentCenterTile);
            spawn.LonDeg    = geo._longitude;
            spawn.LatDeg    = geo._latitude;
            spawn.HeightM   = (float)geo._height;
            spawn.HeadingQ  = GeoCodec.PackHeading(unit.transform.eulerAngles.y);
            spawn.PitchQ    = GeoCodec.PackAngleCdeg(unit.transform.eulerAngles.x);
            spawn.SpeedQ    = GeoCodec.PackSpeedKts(unit._velocityInKnots);
            spawn.UnitFlags = 0;

            // Ledger holds the same instance - census replays now yield a flyer
            NetworkManager.Instance.BroadcastToClients(spawn);
            Telemetry.Count("v2.capturedAirborne");
            Plugin.Log.LogInfo($"[Capture] Airborne flip id={unit.UniqueID} ({unit.name})");
        }
    }

    [HarmonyPatch(typeof(ObjectsManager), nameof(ObjectsManager.createAircraft))]
    public static class Patch_V2_CreateAircraft_Capture
    {
        static void Postfix(Aircraft __result, string squadronReference, string loadoutVariant,
            ObjectBase homeBase, GameObject parent, string aircraftIniName, int aircraftNumber,
            GeoPosition geoPosition, Vector3 heading, Taskforce taskForce, string nationOverride)
            => AircraftSpawnCapture.OnUnitCreated(__result, UnitType.Aircraft, squadronReference,
                loadoutVariant, homeBase, parent, aircraftIniName, aircraftNumber, geoPosition, heading,
                taskForce, nationOverride);
    }

    [HarmonyPatch(typeof(ObjectsManager), nameof(ObjectsManager.createHelicopter))]
    public static class Patch_V2_CreateHelicopter_Capture
    {
        static void Postfix(Helicopter __result, string squadronReference, string loadoutVariant,
            ObjectBase homeBase, GameObject parent, string helicopterIniName, int helicopterNumber,
            GeoPosition geoPosition, Vector3 heading, Taskforce taskForce, string nationOverride)
            => AircraftSpawnCapture.OnUnitCreated(__result, UnitType.Helicopter, squadronReference,
                loadoutVariant, homeBase, parent, helicopterIniName, helicopterNumber, geoPosition, heading,
                taskForce, nationOverride);
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.giveControl))]
    public static class Patch_V2_AircraftAirborne_Capture
    {
        static void Postfix(Aircraft __instance) => AircraftSpawnCapture.OnAirborne(__instance);
    }

    [HarmonyPatch(typeof(Helicopter), nameof(Helicopter.GiveControl))]
    public static class Patch_V2_HelicopterAirborne_Capture
    {
        static void Postfix(Helicopter __instance) => AircraftSpawnCapture.OnAirborne(__instance);
    }

    /// <summary>Mission end is host-decided (client trigger evaluation is gated).</summary>
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.CalculateEndMissionData))]
    public static class Patch_V2_MissionEnd_Capture
    {
        private static bool _sent;
        internal static void Reset() => _sent = false;

        static void Postfix()
        {
            if (!CaptureState.HostCaptureActive) return;
            if (_sent) return;
            _sent = true;
            NetworkManager.Instance.BroadcastToClients(new GameEventMessage
            {
                EventType = GameEventType.MissionEnd,
            });
            Plugin.Log.LogInfo("[Capture] Mission end replicated to client");
        }
    }

    /// <summary>Aircraft removal that is NOT a kill (recovery → stored, scripted
    /// removal) - the universal removal registry call.</summary>
    [HarmonyPatch(typeof(ObjectsManager), nameof(ObjectsManager.removeObject))]
    public static class Patch_V2_UnitRemove_Capture
    {
        static void Postfix(ObjectBase obj)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (obj == null) return;
            if (!(obj is Aircraft) && !(obj is Helicopter)) return;
            if (!CaptureState.DespawnSent.Add(obj.UniqueID)) return; // kills already sent DestroyEvent

            var geo = Utils.worldPositionFromUnityToLongLat(
                obj.transform.position, Globals._currentCenterTile);
            NetworkManager.Instance.BroadcastToClients(new EntityDespawnMessage
            {
                EntityId = obj.UniqueID,
                Cause    = DespawnCause.Landed,
                LonDeg   = geo._longitude,
                LatDeg   = geo._latitude,
                HeightM  = (float)geo._height,
            });
            CaptureState.ForgetSpawn(obj.UniqueID);
            Telemetry.Count("v2.capturedUnitDespawn");
        }
    }
}
