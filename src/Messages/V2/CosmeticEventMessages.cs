using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum GunBurstKind : byte
    {
        GunBurst  = 0,
        CiwsStart = 1,
        CiwsStop  = 2,
    }

    /// <summary>
    /// Host → client, unreliable: a gun mount fired / a CIWS started or stopped
    /// firing. Pure cosmetics - the client replays the mount's own native fire
    /// path (muzzle flash, tracers, audio); damage never happens client-side.
    /// </summary>
    public class GunBurstEventMessage : INetMessage
    {
        public int    ShooterId;
        public short  MountIndex;         // index into unit._obp._weaponSystems
        public GunBurstKind Kind;
        public int    TargetId;           // CIWS aim target (usually a weapon replica)
        public ushort SolutionHeadingQ;   // gun fire solution direction
        public short  SolutionPitchQ;
        public float  ToTargetTime;       // host ballistic solve - Projectile lerps to aim over this
        public double AimLatDeg, AimLonDeg;
        public float  AimHeightM;
        public string AmmoName = "";

        public MessageType Type => MessageType.GunBurstEvent;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ShooterId);
            writer.Put(MountIndex);
            writer.Put((byte)Kind);
            writer.Put(TargetId);
            writer.Put(SolutionHeadingQ);
            writer.Put(SolutionPitchQ);
            writer.Put(ToTargetTime);
            writer.Put(AimLatDeg);
            writer.Put(AimLonDeg);
            writer.Put(AimHeightM);
            writer.Put(AmmoName);
        }

        public static GunBurstEventMessage Deserialize(NetDataReader reader) => new()
        {
            ShooterId        = reader.GetInt(),
            MountIndex       = reader.GetShort(),
            Kind             = (GunBurstKind)reader.GetByte(),
            TargetId         = reader.GetInt(),
            SolutionHeadingQ = reader.GetUShort(),
            SolutionPitchQ   = reader.GetShort(),
            ToTargetTime     = reader.GetFloat(),
            AimLatDeg        = reader.GetDouble(),
            AimLonDeg        = reader.GetDouble(),
            AimHeightM       = reader.GetFloat(),
            AmmoName         = reader.GetString(),
        };
    }

    /// <summary>
    /// Host → client, reliable, throttled: authoritative magazine count for one
    /// ammo type on one unit. Keeps the client's weapon-panel numbers honest
    /// (the client's containers never fire, so they'd otherwise read full).
    /// </summary>
    public class AmmoStateEventMessage : INetMessage
    {
        public int    UnitId;
        public string AmmoName = "";
        public int    MagazineCount;

        public MessageType Type => MessageType.AmmoStateEvent;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(UnitId);
            writer.Put(AmmoName);
            writer.Put(MagazineCount);
        }

        public static AmmoStateEventMessage Deserialize(NetDataReader reader) => new()
        {
            UnitId        = reader.GetInt(),
            AmmoName      = reader.GetString(),
            MagazineCount = reader.GetInt(),
        };
    }
}
