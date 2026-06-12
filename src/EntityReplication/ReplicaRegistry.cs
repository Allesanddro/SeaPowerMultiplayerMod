using System.Collections.Generic;
using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>How the local machine treats a replicated entity.</summary>
    public enum ReplicaPolicy : byte
    {
        None = 0,           // not a replica (host-side native object)
        LocalMotionUnit,    // unit: local propulsion/AFCS integrates, stream corrects
        KinematicWeapon,    // weapon: inert (_isLaunched=false), driven from the stream
        LiveLocal,          // full local sim (sonobuoys: local sensing is wanted)
    }

    /// <summary>
    /// O(1) id→object map for every network-replicated entity, replacing
    /// SceneCreator.FindObjectById (linear scan) and FindGlobalObjectById
    /// (engine-wide Resources scan) on hot paths. Host registers everything it
    /// streams; client registers everything it mirrors. Main thread only.
    /// </summary>
    public static class ReplicaRegistry
    {
        private static readonly Dictionary<int, ObjectBase>    _byId   = new();
        private static readonly Dictionary<ObjectBase, int>    _idOf   = new();
        private static readonly Dictionary<int, ReplicaPolicy> _policy = new();

        public static int Count => _byId.Count;

        public static void Register(int entityId, ObjectBase obj, ReplicaPolicy policy)
        {
            if (obj == null) return;

            // Re-registration under a new id (e.g. SetUniqueId re-key) - drop stale entries
            if (_idOf.TryGetValue(obj, out int oldId) && oldId != entityId)
            {
                _byId.Remove(oldId);
                _policy.Remove(oldId);
            }

            _byId[entityId]   = obj;
            _idOf[obj]        = entityId;
            _policy[entityId] = policy;
        }

        public static void Unregister(int entityId)
        {
            if (_byId.TryGetValue(entityId, out var obj) && !ReferenceEquals(obj, null))
                _idOf.Remove(obj);
            _byId.Remove(entityId);
            _policy.Remove(entityId);
        }

        public static void UnregisterObject(ObjectBase obj)
        {
            if (ReferenceEquals(obj, null)) return;
            if (_idOf.TryGetValue(obj, out int id))
            {
                _idOf.Remove(obj);
                _byId.Remove(id);
                _policy.Remove(id);
            }
        }

        /// <summary>Find a live entity by network id. Unity-destroyed objects are purged lazily.</summary>
        public static ObjectBase? Find(int entityId)
        {
            if (!_byId.TryGetValue(entityId, out var obj)) return null;
            if (obj == null) // Unity lifetime check - object was destroyed
            {
                Unregister(entityId);
                return null;
            }
            return obj;
        }

        public static ReplicaPolicy PolicyOf(int entityId)
            => _policy.TryGetValue(entityId, out var p) ? p : ReplicaPolicy.None;

        public static ReplicaPolicy PolicyOf(ObjectBase obj)
            => !ReferenceEquals(obj, null) && _idOf.TryGetValue(obj, out int id)
                ? PolicyOf(id)
                : ReplicaPolicy.None;

        public static bool IsReplica(ObjectBase obj) => PolicyOf(obj) != ReplicaPolicy.None;

        /// <summary>Snapshot registered ids into a caller-owned list (census diffing).</summary>
        public static void CollectIds(List<int> into)
        {
            into.Clear();
            foreach (var id in _byId.Keys)
                into.Add(id);
        }

        public static void Clear()
        {
            _byId.Clear();
            _idOf.Clear();
            _policy.Clear();
        }
    }
}
