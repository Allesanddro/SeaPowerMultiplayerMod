using System;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Client-side playback for host firing cosmetics: replays gun bursts through
    /// the mount's own native fire path (muzzle flash, dust, recoil, tracer
    /// projectile - whose impacts are damage-free because Blastzone is suppressed)
    /// and drives CIWS tracer state at the real target replica. Also applies
    /// authoritative magazine counts so the weapon panel reads honestly.
    /// </summary>
    public static class CosmeticEventHandler
    {
        // StopEngage is private on WeaponSystemCIWS - cached open delegate
        private static readonly Action<WeaponSystemCIWS>? _ciwsStopEngage = BuildStopEngage();

        private static Action<WeaponSystemCIWS>? BuildStopEngage()
        {
            var m = AccessTools.Method(typeof(WeaponSystemCIWS), "StopEngage");
            if (m == null) return null;
            return (Action<WeaponSystemCIWS>)Delegate.CreateDelegate(typeof(Action<WeaponSystemCIWS>), m);
        }

        public static void HandleGunBurst(GunBurstEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var unit = ReplicaRegistry.Find(msg.ShooterId) ?? StateSerializer.FindById(msg.ShooterId);
            if (unit == null || unit._obp?._weaponSystems == null) return;
            if (msg.MountIndex < 0 || msg.MountIndex >= unit._obp._weaponSystems.Count) return;
            var ws = unit._obp._weaponSystems[msg.MountIndex];

            switch (msg.Kind)
            {
                case GunBurstKind.GunBurst:
                {
                    if (!(ws is WeaponSystemGun gun)) return;

                    var ammo = gun._vwp?._associatedMagazine?.getAmmunitionByName(msg.AmmoName)
                               ?? gun.getOnWeaponAmmunition();
                    if (ammo == null) { Telemetry.Count("v2.gunBurstNoAmmo"); return; }

                    float heading = GeoCodec.UnpackHeading(msg.SolutionHeadingQ);
                    float pitch   = GeoCodec.UnpackAngleCdeg(msg.SolutionPitchQ);
                    gun._solutionVector = Quaternion.Euler(pitch, heading, 0f) * Vector3.forward;
                    gun._ammoForEngage = ammo;
                    gun._targetObject = ReplicaRegistry.Find(msg.TargetId) ?? StateSerializer.FindById(msg.TargetId);
                    // Projectile.MoveProjectile lerps start→aim over _toTargetTime;
                    // without the host's solve the time sits at float.MaxValue and
                    // shells climb vertically then hang frozen at apex.
                    gun._projectileToTargetTime  = msg.ToTargetTime;
                    gun._projectileAimGeoPosition = new GeoPosition(msg.AimLatDeg, msg.AimLonDeg, msg.AimHeightM);
                    gun._solution = 1f; // host validated the solution - bypass the naval spot-shot gate

                    using (Authority.Allowed())
                        gun.fire();
                    Telemetry.Count("v2.playedGunBurst");
                    break;
                }

                case GunBurstKind.CiwsStart:
                {
                    if (!(ws is WeaponSystemCIWS ciws)) return;
                    // Weapon-replica targets live in ReplicaRegistry - without this
                    // the CIWS gets a null target and fires straight ahead.
                    ciws._currentClosestTarget = ReplicaRegistry.Find(msg.TargetId) ?? StateSerializer.FindById(msg.TargetId);
                    using (Authority.Allowed())
                        ciws.StartFire();
                    Telemetry.Count("v2.playedCiwsStart");
                    break;
                }

                case GunBurstKind.CiwsStop:
                {
                    if (!(ws is WeaponSystemCIWS ciws)) return;
                    using (Authority.Allowed())
                        _ciwsStopEngage?.Invoke(ciws);
                    break;
                }
            }
        }

        public static void HandleAmmoState(AmmoStateEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var unit = ReplicaRegistry.Find(msg.UnitId) ?? StateSerializer.FindById(msg.UnitId);
            if (unit?._obp?._weaponSystems == null) return;

            foreach (var ws in unit._obp._weaponSystems)
            {
                var mag = ws._vwp?._associatedMagazine;
                if (mag == null || mag.getAmmunitionByName(msg.AmmoName) == null) continue;

                int current = mag.getAmmunitionCount(msg.AmmoName);
                int delta = msg.MagazineCount - current;
                if (delta > 0)
                    mag.increaseAmmunitionCount(msg.AmmoName, delta);
                else if (delta < 0)
                    mag.decreaseAmmunitionCount(msg.AmmoName, -delta);

                unit.UpdateAmmoCount();
                Telemetry.Count("v2.appliedAmmoState");
                break;
            }
        }
    }
}
