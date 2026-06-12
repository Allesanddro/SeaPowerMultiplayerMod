using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side flight-ops animation playback. Replays the host's FlightOpsAnim
    /// events on the local twins of the same ini-defined animations:
    ///  - aircraft code anims go through the unit's own PlayCodeAnim/SetCodeAnim
    ///    (ObjectBase pumps those itself),
    ///  - carrier elevator / hangar / deflector clones aren't pumped by anything
    ///    client-side (the deck states that pump them only run on the host), so
    ///    they're driven from Tick() here until done,
    ///  - catapult steam plays the launch point's particle system for the
    ///    ini-defined duration.
    /// </summary>
    public static class CarrierOpsHandler
    {
        private static readonly List<ObjectCodeAnimation> _playing = new();
        private static readonly List<(ParticleSystem ps, float stopAt)> _steam = new();

        public static void HandleAnim(FlightOpsAnimMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var unit = ReplicaRegistry.Find(msg.UnitId) ?? StateSerializer.FindById(msg.UnitId);
            if (unit == null) return;

            switch (msg.Kind)
            {
                case FlightOpsAnimKind.AircraftPlayAnim:
                    unit.PlayCodeAnim(msg.AnimKey);
                    break;
                case FlightOpsAnimKind.AircraftSetOpen:
                    unit.SetCodeAnim(msg.AnimKey, false, true);
                    break;
                case FlightOpsAnimKind.AircraftSetClosed:
                    unit.SetCodeAnim(msg.AnimKey, false, false);
                    break;

                case FlightOpsAnimKind.ElevatorDown:
                case FlightOpsAnimKind.ElevatorUp:
                case FlightOpsAnimKind.HangarOpen:
                case FlightOpsAnimKind.HangarClose:
                case FlightOpsAnimKind.HangarRetract:
                case FlightOpsAnimKind.HangarExtend:
                {
                    var deck = unit._obp?._flightDeck;
                    if (deck == null || msg.Index >= deck._elevators.Count) return;
                    var el = deck._elevators[msg.Index];
                    ObjectCodeAnimation? anim = msg.Kind switch
                    {
                        FlightOpsAnimKind.ElevatorDown  => el._moveDownAnimation,
                        FlightOpsAnimKind.ElevatorUp    => el._moveUpAnimation,
                        FlightOpsAnimKind.HangarOpen    => el._hangarOpenAnimation,
                        FlightOpsAnimKind.HangarClose   => el._hangarCloseAnimation,
                        FlightOpsAnimKind.HangarRetract => el._hangarRetractAnimation,
                        _                               => el._hangarExtendAnimation,
                    };
                    // Mirror the host's deck bookkeeping so save/UI state agrees
                    if (msg.Kind == FlightOpsAnimKind.ElevatorDown) el._isLowered = true;
                    if (msg.Kind == FlightOpsAnimKind.ElevatorUp)   el._isLowered = false;
                    Play(anim);
                    break;
                }

                case FlightOpsAnimKind.DeflectorRaise:
                case FlightOpsAnimKind.DeflectorLower:
                {
                    var deck = unit._obp?._flightDeck;
                    if (deck == null || msg.Index >= deck._launchPoints.Count) return;
                    var lp = deck._launchPoints[msg.Index];
                    Play(msg.Kind == FlightOpsAnimKind.DeflectorRaise
                        ? lp._raiseDeflectorAnimation
                        : lp._lowerDeflectorAnimation);
                    break;
                }

                case FlightOpsAnimKind.CatapultSteam:
                {
                    var deck = unit._obp?._flightDeck;
                    if (deck == null || msg.Index >= deck._launchPoints.Count) return;
                    var lp = deck._launchPoints[msg.Index];
                    if (lp._catapultParticles == null) return;
                    lp._catapultParticles.Play();
                    _steam.Add((lp._catapultParticles, Time.unscaledTime + lp._catapultParticlesDuration));
                    break;
                }
            }
        }

        private static void Play(ObjectCodeAnimation? anim)
        {
            if (anim == null) return;
            anim.playAnim();
            if (!_playing.Contains(anim)) _playing.Add(anim);
        }

        /// <summary>Per-frame pump (Plugin.Update, client) - the deck states that
        /// normally pump these animations don't run client-side.</summary>
        public static void Tick()
        {
            for (int i = _playing.Count - 1; i >= 0; i--)
            {
                if (!_playing[i].update()) _playing.RemoveAt(i);
            }
            for (int i = _steam.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime >= _steam[i].stopAt)
                {
                    if (_steam[i].ps != null) _steam[i].ps.Stop();
                    _steam.RemoveAt(i);
                }
            }
        }

        public static void Reset()
        {
            _playing.Clear();
            foreach (var (ps, _) in _steam)
                if (ps != null) ps.Stop();
            _steam.Clear();
        }
    }
}
