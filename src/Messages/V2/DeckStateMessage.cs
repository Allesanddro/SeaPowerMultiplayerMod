using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client, Unreliable (rides the unit-tick cadence): carrier-relative
    /// transform of an aircraft in the flight-deck pipeline (launch taxi/catapult
    /// or landing rollout). World-space streaming can't represent motion on a
    /// moving deck - the client parents its puppet to the carrier root and lerps
    /// this local transform instead.
    /// </summary>
    public class DeckStateMessage : INetMessage
    {
        public int    AircraftId;
        public int    CarrierId;
        public float  LocalX, LocalY, LocalZ;  // carrier-root local position
        public ushort LocalYawQ;               // yaw relative to carrier heading

        public MessageType Type => MessageType.DeckState;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(AircraftId);
            writer.Put(CarrierId);
            writer.Put(LocalX);
            writer.Put(LocalY);
            writer.Put(LocalZ);
            writer.Put(LocalYawQ);
        }

        public static DeckStateMessage Deserialize(NetDataReader reader) => new()
        {
            AircraftId = reader.GetInt(),
            CarrierId  = reader.GetInt(),
            LocalX     = reader.GetFloat(),
            LocalY     = reader.GetFloat(),
            LocalZ     = reader.GetFloat(),
            LocalYawQ  = reader.GetUShort(),
        };
    }
}
