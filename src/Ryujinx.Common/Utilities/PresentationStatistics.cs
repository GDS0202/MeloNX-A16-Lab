using System;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    // CPU intervals between presentations. Never describe these as GPU execution times.
    public sealed record PresentationStatistics(int Samples, double P95Ms, double P99Ms, double MaximumMs, int Over100Ms)
    {
        private static PresentationStatistics _latest = new(0, 0, 0, 0, 0);
        public static PresentationStatistics Latest => Volatile.Read(ref _latest);
        public static void Publish(PresentationStatistics value) => Volatile.Write(ref _latest, value);
        public static void Reset() => Publish(new(0, 0, 0, 0, 0));
    }
}
