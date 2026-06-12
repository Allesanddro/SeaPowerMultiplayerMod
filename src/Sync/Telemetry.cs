using System.Collections.Generic;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Lightweight counters behind the v2 overlay page: per-message-type traffic,
    /// named event counters, and a per-frame send-bytes ring that proves the
    /// staggered streamer isn't bursting. All callers are on the Unity main thread
    /// (transport events fire inside NetworkManager.Tick's Poll), so no locking.
    /// </summary>
    public static class Telemetry
    {
        private const int TypeSlots = 64;

        private static readonly long[] _bytesIn  = new long[TypeSlots];
        private static readonly long[] _bytesOut = new long[TypeSlots];
        private static readonly long[] _msgsIn   = new long[TypeSlots];
        private static readonly long[] _msgsOut  = new long[TypeSlots];

        private static readonly Dictionary<string, long> _counters = new();

        // Per-frame send bytes ring (~5 s at 60 fps) - stagger flatness evidence
        private const int FrameRingSize = 300;
        private static readonly int[] _frameSendBytes = new int[FrameRingSize];
        private static int _frameIndex;
        private static int _framesSeen;

        public static long TotalBytesIn  { get; private set; }
        public static long TotalBytesOut { get; private set; }

        /// <summary>Live view for the overlay. Main thread only; do not mutate.</summary>
        public static IReadOnlyDictionary<string, long> Counters => _counters;

        public static void OnReceive(byte type, int bytes)
        {
            if (type < TypeSlots) { _bytesIn[type] += bytes; _msgsIn[type]++; }
            TotalBytesIn += bytes;
        }

        public static void OnSend(byte type, int bytes)
        {
            if (type < TypeSlots) { _bytesOut[type] += bytes; _msgsOut[type]++; }
            TotalBytesOut += bytes;
            _frameSendBytes[_frameIndex] += bytes;
        }

        public static void Count(string name, long delta = 1)
        {
            _counters.TryGetValue(name, out long v);
            _counters[name] = v + delta;
        }

        /// <summary>Advance the per-frame ring. Called once per frame from Plugin.Update.</summary>
        public static void FrameTick()
        {
            _frameIndex = (_frameIndex + 1) % FrameRingSize;
            _frameSendBytes[_frameIndex] = 0;
            if (_framesSeen < FrameRingSize) _framesSeen++;
        }

        public static (int min, float avg, int max) FrameSendStats()
        {
            int n = _framesSeen;
            if (n == 0) return (0, 0f, 0);
            int min = int.MaxValue, max = 0;
            long sum = 0;
            for (int i = 0; i < n; i++)
            {
                int v = _frameSendBytes[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            return (min, (float)sum / n, max);
        }

        public static long BytesIn(byte type)  => type < TypeSlots ? _bytesIn[type]  : 0;
        public static long BytesOut(byte type) => type < TypeSlots ? _bytesOut[type] : 0;
        public static long MsgsIn(byte type)   => type < TypeSlots ? _msgsIn[type]   : 0;
        public static long MsgsOut(byte type)  => type < TypeSlots ? _msgsOut[type]  : 0;

        public static void Reset()
        {
            for (int i = 0; i < TypeSlots; i++)
            {
                _bytesIn[i] = _bytesOut[i] = 0;
                _msgsIn[i]  = _msgsOut[i]  = 0;
            }
            _counters.Clear();
            for (int i = 0; i < FrameRingSize; i++) _frameSendBytes[i] = 0;
            _frameIndex = 0;
            _framesSeen = 0;
            TotalBytesIn = 0;
            TotalBytesOut = 0;
        }
    }
}
