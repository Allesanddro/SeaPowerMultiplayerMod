using System;
using LiteNetLib.Utils;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer.Net2
{
    /// <summary>
    /// Wire codec for positions and angles.
    /// Lat/lon travel as int32 fixed-point at 1e-7 degrees (~1.1 cm) - float32 degrees
    /// would quantize to ~1 m and visibly jitter fast weapons. Height stays float32.
    /// Unity-space conversions go through the game's own helpers so floating-origin
    /// (center tile) handling matches the game exactly.
    /// </summary>
    public static class GeoCodec
    {
        public const double LatLonScale = 1e7;

        public static int PackLatLon(double degrees) => (int)Math.Round(degrees * LatLonScale);
        public static double UnpackLatLon(int fixedPoint) => fixedPoint / LatLonScale;

        public static void PutGeo(NetDataWriter w, GeoPosition g)
        {
            w.Put((int)Math.Round(g._longitude * LatLonScale));
            w.Put((int)Math.Round(g._latitude * LatLonScale));
            w.Put((float)g._height);
        }

        public static GeoPosition GetGeo(NetDataReader r)
        {
            int lon  = r.GetInt();
            int lat  = r.GetInt();
            float h  = r.GetFloat();
            return new GeoPosition(lat / LatLonScale, lon / LatLonScale, h);
        }

        public static void PutGeoFromUnity(NetDataWriter w, Vector3 worldPos)
            => PutGeo(w, Utils.worldPositionFromUnityToLongLat(worldPos, Globals._currentCenterTile));

        public static Vector3 GetGeoAsUnity(NetDataReader r)
            => Utils.longLatToLocalV3(GetGeo(r), Globals._currentCenterTile);

        // ── Angle / speed quantizers ─────────────────────────────────────────

        /// <summary>Heading 0-360° → u16 (0.0055° steps). 360 wraps to 0.</summary>
        public static ushort PackHeading(float deg)
            => unchecked((ushort)Mathf.RoundToInt(Mathf.Repeat(deg, 360f) * (65536f / 360f)));

        public static float UnpackHeading(ushort v) => v * (360f / 65536f);

        /// <summary>Signed angle (pitch/roll) → i16 centidegrees. Input normalized to [-180, 180).</summary>
        public static short PackAngleCdeg(float deg)
        {
            deg = Mathf.Repeat(deg + 180f, 360f) - 180f;
            return (short)Mathf.RoundToInt(deg * 100f);
        }

        public static float UnpackAngleCdeg(short v) => v / 100f;

        /// <summary>Speed in knots → u16 at 0.1 kt resolution (max 6553.5 kts).</summary>
        public static ushort PackSpeedKts(float kts)
            => (ushort)Mathf.Clamp(Mathf.RoundToInt(kts * 10f), 0, ushort.MaxValue);

        public static float UnpackSpeedKts(ushort v) => v / 10f;
    }
}
