using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client, ReliableOrdered: a launcher's weapon-container hatch (VLS lid,
    /// torpedo-tube door) started opening or closing. Pure cosmetics - the client
    /// plays the same ini-defined open/close animation on its twin container. Mount
    /// and container indices are stable across machines (both build them from the
    /// same vessel ini), so they resolve to the identical container client-side.
    /// </summary>
    public class WeaponHatchEventMessage : INetMessage
    {
        public int   UnitId;
        public short  MountIndex;    // index into unit._obp._weaponSystems
        public byte   ContainerId;   // index into launcher._containers
        public bool   Open;          // true = open hatches, false = close

        public MessageType Type => MessageType.WeaponHatchEvent;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(UnitId);
            writer.Put(MountIndex);
            writer.Put(ContainerId);
            writer.Put(Open);
        }

        public static WeaponHatchEventMessage Deserialize(NetDataReader reader) => new()
        {
            UnitId      = reader.GetInt(),
            MountIndex  = reader.GetShort(),
            ContainerId = reader.GetByte(),
            Open        = reader.GetBool(),
        };
    }
}
