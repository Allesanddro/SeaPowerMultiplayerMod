using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client every few seconds: the complete manifest of replicated
    /// entity ids (units + live weapons). The client diffs it against its local
    /// registry - missing ids get a spawn-replay request, entities absent from
    /// two consecutive censuses get quietly removed. This is the self-healing
    /// backbone that turns any missed event into a few-second blip instead of
    /// a permanent ghost.
    /// </summary>
    public class EntityCensusMessage : INetMessage
    {
        public ushort CensusSeq;
        public readonly List<(int id, byte kind)> Entries = new(128);

        public MessageType Type => MessageType.EntityCensus;

        public void Reset()
        {
            CensusSeq = 0;
            Entries.Clear();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(CensusSeq);
            writer.Put((ushort)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                writer.Put(Entries[i].id);
                writer.Put(Entries[i].kind);
            }
        }

        public static EntityCensusMessage Deserialize(NetDataReader reader)
        {
            var msg = new EntityCensusMessage { CensusSeq = reader.GetUShort() };
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
                msg.Entries.Add((reader.GetInt(), reader.GetByte()));
            return msg;
        }
    }

    /// <summary>Client → host: replay the EntitySpawn for these ids (I'm missing them).</summary>
    public class CensusDiffRequestMessage : INetMessage
    {
        public readonly List<int> Ids = new(16);

        public MessageType Type => MessageType.CensusDiffRequest;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((ushort)Ids.Count);
            for (int i = 0; i < Ids.Count; i++)
                writer.Put(Ids[i]);
        }

        public static CensusDiffRequestMessage Deserialize(NetDataReader reader)
        {
            var msg = new CensusDiffRequestMessage();
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
                msg.Ids.Add(reader.GetInt());
            return msg;
        }
    }
}
