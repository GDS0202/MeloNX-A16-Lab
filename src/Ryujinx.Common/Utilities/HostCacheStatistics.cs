using System.Threading;

namespace Ryujinx.Common.Utilities
{
    // Shared telemetry without a renderer dependency on the texture codec assembly.
    public static class HostCacheStatistics
    {
        private static long _convertedTextureHits;
        public static long ConvertedTextureHits => Interlocked.Read(ref _convertedTextureHits);
        public static void RecordConvertedTextureHit() => Interlocked.Increment(ref _convertedTextureHits);
    }
}
