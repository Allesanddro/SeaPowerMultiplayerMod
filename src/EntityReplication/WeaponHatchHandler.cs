using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side weapon-hatch playback. The host launcher's engage state machine
    /// (which opens VLS lids / torpedo-tube doors before a launch) doesn't run on
    /// client puppets, so hatch animations never play locally. Replays the host's
    /// WeaponHatchEvent on the twin container's ini-defined open/close animation and
    /// pumps it each frame from Tick() - the same ObjectCodeAnimation pattern the deck
    /// machinery uses in CarrierOpsHandler (these clones aren't pumped by anything
    /// client-side).
    /// </summary>
    public static class WeaponHatchHandler
    {
        private static readonly List<ObjectCodeAnimation> _playing = new();

        public static void Handle(WeaponHatchEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var unit = ReplicaRegistry.Find(msg.UnitId) ?? StateSerializer.FindById(msg.UnitId);
            if (unit == null) return;

            var systems = unit._obp?._weaponSystems;
            if (systems == null || msg.MountIndex < 0 || msg.MountIndex >= systems.Count) return;
            if (!(systems[msg.MountIndex] is WeaponSystemLauncher launcher)) return;
            if (msg.ContainerId >= launcher._containers.Count) return;

            var container = launcher._containers[msg.ContainerId];
            if (container == null) return;

            // Mirror the host's bookkeeping so save/UI hatch state agrees.
            container._areHatchesOpen = msg.Open;

            var anim = msg.Open ? container._openAnimation : container._closeAnimation;
            if (anim == null) return;

            anim.playAnim();
            if (!_playing.Contains(anim)) _playing.Add(anim);

            if (container._hatchSoundClip != null && container._audioSource != null)
            {
                container._audioSource.clip = container._hatchSoundClip;
                container._audioSource.Stop();
                container._audioSource.Play();
            }
        }

        /// <summary>Per-frame pump (Plugin.Update, client) - the launcher states that
        /// normally advance these animations don't run on client puppets.</summary>
        public static void Tick()
        {
            for (int i = _playing.Count - 1; i >= 0; i--)
            {
                if (!_playing[i].update()) _playing.RemoveAt(i);
            }
        }

        public static void Reset() => _playing.Clear();
    }
}
