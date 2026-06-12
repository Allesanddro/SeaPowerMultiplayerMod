using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum UnitType : byte
    {
        Vessel     = 0,
        Submarine  = 1,
        Aircraft   = 2,
        Helicopter = 3,
        Biologic   = 4,
        LandUnit   = 5,
        // v2 entity stream kinds (weapons ride the same EntityStateBatch)
        Missile    = 6,
        Torpedo    = 7,
        Bomb       = 8,
    }

    /// <summary>Unit snapshot used by the post-load ID alignment pass.</summary>
    public struct UnitState
    {
        public int      EntityId;
        public UnitType Kind;
        public float    X, Y, Z;        // world position (GeoPosition: lon, height, lat)
        public float    Heading;        // degrees (Y euler angle)
        public float    Speed;          // knots
        public bool     IsDestroyed;
        public bool     IsSinking;       // gradual sinking in progress
        public float    RudderAngle;     // commanded rudder angle (-25 to +25)
        public int      Telegraph;       // speed telegraph (-1 to 5)
        public float    DesiredAltitude; // aircraft/helicopter altitude command
        public float    Pitch;           // visual pitch (X euler angle)
        public float    Roll;            // visual roll (Z euler angle)
        public float    IntegrityPercent; // hull integrity for damage visuals
    }
}
