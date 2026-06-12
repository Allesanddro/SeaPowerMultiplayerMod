using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum FlightOpsAnimKind : byte
    {
        // Aircraft/helicopter code anims (gear, wings, canopy, hook, nozzles...)
        AircraftPlayAnim  = 0,  // PlayCodeAnim(AnimKey)
        AircraftSetOpen   = 1,  // SetCodeAnim(AnimKey, setOpen: true)
        AircraftSetClosed = 2,  // SetCodeAnim(AnimKey, setOpen: false)

        // Carrier flight-deck machinery (Index = elevator index)
        ElevatorDown  = 10,
        ElevatorUp    = 11,
        HangarOpen    = 12,
        HangarClose   = 13,
        HangarRetract = 14,
        HangarExtend  = 15,

        // Catapult hardware (Index = launch point index)
        DeflectorRaise = 20,
        DeflectorLower = 21,
        CatapultSteam  = 22,
    }

    /// <summary>
    /// Host → client, ReliableOrdered: a flight-ops animation event. UnitId is the
    /// aircraft for code anims, the carrier for deck machinery. Pure cosmetics -
    /// the client plays the same named/ini-defined animation locally.
    /// </summary>
    public class FlightOpsAnimMessage : INetMessage
    {
        public FlightOpsAnimKind Kind;
        public int    UnitId;
        public byte   Index;        // elevator / launch point index
        public string AnimKey = ""; // aircraft code-anim key only

        public MessageType Type => MessageType.FlightOpsAnim;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)Kind);
            writer.Put(UnitId);
            writer.Put(Index);
            writer.Put(AnimKey);
        }

        public static FlightOpsAnimMessage Deserialize(NetDataReader reader) => new()
        {
            Kind    = (FlightOpsAnimKind)reader.GetByte(),
            UnitId  = reader.GetInt(),
            Index   = reader.GetByte(),
            AnimKey = reader.GetString(),
        };
    }
}
