using System;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Marks code regions that are applying NETWORK-authoritative actions, so
    /// client-side suppression prefixes (Destruction, destroyObject, spawn canary)
    /// can distinguish "the host told us to do this" from "local sim tried to act
    /// on its own". Main thread only; reentrancy-safe via depth counting.
    /// </summary>
    public static class Authority
    {
        private static int _depth;

        public static bool IsAllowed => _depth > 0;

        private sealed class Scope : IDisposable
        {
            public void Dispose() => _depth--;
        }

        private static readonly Scope _scope = new();

        /// <summary>using (Authority.Allowed()) { ... network-applied game calls ... }</summary>
        public static IDisposable Allowed()
        {
            _depth++;
            return _scope;
        }
    }
}
