using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client handshake verdict. On acceptance carries the session parameters
    /// the client needs before any gameplay traffic (mode, UID band, stream rate).
    /// </summary>
    public class WelcomeMessage : INetMessage
    {
        public bool   Accepted;
        public string RefusalReason = "";
        public bool   IsPvP;
        public byte   AssignedTaskforce;       // reserved (used from P1)
        public int    ClientUidBase;           // client-local UID band start (used from P2)
        public byte   StateRateHz;

        public MessageType Type => MessageType.Welcome;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Accepted);
            writer.Put(RefusalReason);
            writer.Put(IsPvP);
            writer.Put(AssignedTaskforce);
            writer.Put(ClientUidBase);
            writer.Put(StateRateHz);
        }

        public static WelcomeMessage Deserialize(NetDataReader reader) => new()
        {
            Accepted          = reader.GetBool(),
            RefusalReason     = reader.GetString(),
            IsPvP             = reader.GetBool(),
            AssignedTaskforce = reader.GetByte(),
            ClientUidBase     = reader.GetInt(),
            StateRateHz       = reader.GetByte(),
        };
    }
}
