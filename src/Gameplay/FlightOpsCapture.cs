using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// HOST-side flight-ops animation capture. Pure cosmetics, replicated as
    /// FlightOpsAnim events:
    ///  - aircraft code anims (gear, wings, canopy, hook, nozzles) via the
    ///    ObjectBase.PlayCodeAnim / SetCodeAnim funnels,
    ///  - carrier elevator / hangar-door moves via the PlayCodeAnimations deck
    ///    state (LowerElevator/RaiseElevator/Open-/CloseHangarDoors call its
    ///    base.onEnter),
    ///  - blast deflectors + catapult steam via the two static helpers every
    ///    takeoff state funnels through (PlaneTakeOff.runAnimationAsync /
    ///    runParticleSystemAsync).
    /// </summary>
    public static class FlightOpsAnimCapture
    {
        internal static void Send(FlightOpsAnimKind kind, int unitId, int index = 0, string animKey = "")
        {
            NetworkManager.Instance.BroadcastToClients(new FlightOpsAnimMessage
            {
                Kind    = kind,
                UnitId  = unitId,
                Index   = (byte)Mathf.Clamp(index, 0, 255),
                AnimKey = animKey,
            });
            Telemetry.Count("v2.capturedFlightOpsAnim");
        }

        /// <summary>Find which carrier deck-hardware animation this instance is
        /// (elevator clones / launch point deflector anims are per-carrier copies
        /// built from the same ini on both machines, so (carrier, index, kind)
        /// resolves to the identical clone client-side).</summary>
        internal static bool TryClassifyCarrierAnim(ObjectCodeAnimation anim,
            out FlightOpsAnimKind kind, out ObjectBase? carrier, out int index)
        {
            kind = default; carrier = null; index = 0;
            var vessels = UnitRegistry.Vessels;
            for (int v = 0; v < vessels.Count; v++)
            {
                var deck = vessels[v]?._obp?._flightDeck;
                if (deck == null) continue;

                for (int i = 0; i < deck._elevators.Count; i++)
                {
                    var el = deck._elevators[i];
                    FlightOpsAnimKind? k = null;
                    if (ReferenceEquals(anim, el._moveDownAnimation))     k = FlightOpsAnimKind.ElevatorDown;
                    else if (ReferenceEquals(anim, el._moveUpAnimation))  k = FlightOpsAnimKind.ElevatorUp;
                    else if (ReferenceEquals(anim, el._hangarOpenAnimation))    k = FlightOpsAnimKind.HangarOpen;
                    else if (ReferenceEquals(anim, el._hangarCloseAnimation))   k = FlightOpsAnimKind.HangarClose;
                    else if (ReferenceEquals(anim, el._hangarRetractAnimation)) k = FlightOpsAnimKind.HangarRetract;
                    else if (ReferenceEquals(anim, el._hangarExtendAnimation))  k = FlightOpsAnimKind.HangarExtend;
                    if (k != null) { kind = k.Value; carrier = vessels[v]; index = i; return true; }
                }

                for (int i = 0; i < deck._launchPoints.Count; i++)
                {
                    var lp = deck._launchPoints[i];
                    FlightOpsAnimKind? k = null;
                    if (ReferenceEquals(anim, lp._raiseDeflectorAnimation))      k = FlightOpsAnimKind.DeflectorRaise;
                    else if (ReferenceEquals(anim, lp._lowerDeflectorAnimation)) k = FlightOpsAnimKind.DeflectorLower;
                    if (k != null) { kind = k.Value; carrier = vessels[v]; index = i; return true; }
                }
            }
            return false;
        }
    }

    /// <summary>Aircraft code anims played by deck states / approach / takeoff -
    /// gear extend/retract, wing fold, canopy, hook, STOL nozzles, takeoff flaps.</summary>
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.PlayCodeAnim))]
    public static class Patch_V2_PlayCodeAnim_Capture
    {
        static void Postfix(ObjectBase __instance, string animationKey)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (!(__instance is Aircraft) && !(__instance is Helicopter)) return;
            FlightOpsAnimCapture.Send(FlightOpsAnimKind.AircraftPlayAnim, __instance.UniqueID, 0, animationKey);
        }
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.SetCodeAnim))]
    public static class Patch_V2_SetCodeAnim_Capture
    {
        static void Postfix(ObjectBase __instance, string animationKey, bool setOpen)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (!(__instance is Aircraft) && !(__instance is Helicopter)) return;
            FlightOpsAnimCapture.Send(
                setOpen ? FlightOpsAnimKind.AircraftSetOpen : FlightOpsAnimKind.AircraftSetClosed,
                __instance.UniqueID, 0, animationKey);
        }
    }

    /// <summary>Elevator / hangar-door moves: the deck states (LowerElevator,
    /// RaiseElevator, Open-/CloseHangarDoors, Retract-/ExtendHangar) all run
    /// their animations through PlayCodeAnimations.base.onEnter.</summary>
    [HarmonyPatch(typeof(PlayCodeAnimations), nameof(PlayCodeAnimations.onEnter))]
    public static class Patch_V2_DeckMachinery_Capture
    {
        static void Postfix(PlayCodeAnimations __instance)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (__instance._interrupt) return; // state no-opped, nothing played

            foreach (var anim in __instance._objectCodeAnimations)
            {
                if (anim == null) continue;
                if (FlightOpsAnimCapture.TryClassifyCarrierAnim(anim, out var kind, out var carrier, out int idx)
                    && carrier != null)
                {
                    FlightOpsAnimCapture.Send(kind, carrier.UniqueID, idx);
                }
            }
        }
    }

    /// <summary>Blast deflector raise/lower: every takeoff state (PlaneTakeOff,
    /// STOLTakeOff) plays deflector anims through this one static helper.</summary>
    [HarmonyPatch(typeof(PlaneTakeOff), nameof(PlaneTakeOff.runAnimationAsync))]
    public static class Patch_V2_Deflector_Capture
    {
        static void Postfix(ObjectCodeAnimation objectCodeAnimation)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (objectCodeAnimation == null) return;
            if (FlightOpsAnimCapture.TryClassifyCarrierAnim(objectCodeAnimation, out var kind, out var carrier, out int idx)
                && carrier != null
                && (kind == FlightOpsAnimKind.DeflectorRaise || kind == FlightOpsAnimKind.DeflectorLower))
            {
                FlightOpsAnimCapture.Send(kind, carrier.UniqueID, idx);
            }
        }
    }

    /// <summary>Catapult steam: PlaneTakeOff triggers the launch point's particle
    /// system through this static helper once per stroke.</summary>
    [HarmonyPatch(typeof(PlaneTakeOff), nameof(PlaneTakeOff.runParticleSystemAsync))]
    public static class Patch_V2_CatapultSteam_Capture
    {
        static void Postfix(ParticleSystem particleSystem)
        {
            if (!CaptureState.HostCaptureActive) return;
            if (particleSystem == null) return;

            var vessels = UnitRegistry.Vessels;
            for (int v = 0; v < vessels.Count; v++)
            {
                var deck = vessels[v]?._obp?._flightDeck;
                if (deck == null) continue;
                for (int i = 0; i < deck._launchPoints.Count; i++)
                {
                    if (ReferenceEquals(deck._launchPoints[i]._catapultParticles, particleSystem))
                    {
                        FlightOpsAnimCapture.Send(FlightOpsAnimKind.CatapultSteam, vessels[v].UniqueID, i);
                        return;
                    }
                }
            }
        }
    }
}
