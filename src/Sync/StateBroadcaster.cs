using System.Collections;
using LiteNetLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// MonoBehaviour attached to the Plugin GameObject.
    /// Periodic host-side sync loops that ride Unity coroutines:
    /// damage-state corrections and waypoint drag flushing.
    /// </summary>
    public class StateBroadcaster : MonoBehaviour
    {
        private void Start()
        {
            StartCoroutine(DamageCorrectionLoop());
            StartCoroutine(WaypointFlushLoop());
        }

        // ── Waypoint drag flush (catches throttled final positions) ────────
        private IEnumerator WaypointFlushLoop()
        {
            var wait = new WaitForSeconds(0.15f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsConnected) continue;

                foreach (var kvp in Patch_UserRootNode_UpdateSimulation._pending)
                {
                    var (unit, index) = kvp.Value;
                    var root = unit._userRoot;
                    if (root == null || index >= root.TaskViewModels.Count) continue;
                    if (root.TaskViewModels[index].Task is GoToWaypointTask wp)
                        Patch_UserRootNode_UpdateSimulation.SendEditWaypoint(unit, index, wp);
                }
                Patch_UserRootNode_UpdateSimulation._pending.Clear();
            }
        }

        // ── Periodic damage correction (catches drift / packet loss) ────────
        private IEnumerator DamageCorrectionLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(Plugin.Instance.CfgDamageSyncInterval.Value);
                if (!NetworkManager.Instance.IsConnected) continue;

                // v2: damage is host-authoritative in both modes
                if (!Plugin.Instance.CfgIsHost.Value) continue;

                BroadcastDamageCorrections();
            }
        }

        private static void BroadcastDamageCorrections()
        {
            SendCorrections(UnitRegistry.Vessels);
            SendCorrections(UnitRegistry.Submarines);
        }

        private static void SendCorrections<T>(System.Collections.Generic.IReadOnlyList<T> units) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.IsDestroyed) continue;
                var comps = unit.Compartments;
                if (comps == null) continue;

                // Only send for units that have taken damage or are sinking
                if (!comps._isSinking && comps.IntegrityPercentage > 99f) continue;

                var msg = DamageStateSerializer.Capture(unit);
                if (msg != null)
                    NetworkManager.Instance.SendToOther(msg, DeliveryMethod.Unreliable);
            }
        }
    }
}
