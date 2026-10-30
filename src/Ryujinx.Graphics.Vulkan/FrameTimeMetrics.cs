using System;

namespace Ryujinx.Graphics.Vulkan
{
    // CPU presentation intervals, not GPU execution or panel display timings.
    internal sealed class FrameTimeMetrics
    {
        private const int LastBucket = 2000;
        private readonly int[] _histogram = new int[LastBucket + 1];
        private double _totalMilliseconds;
        public int Count { get; private set; }
        public int Over50Ms { get; private set; }
        public int Over100Ms { get; private set; }
        public int Over250Ms { get; private set; }
        public double MaximumMilliseconds { get; private set; }
        public double AverageMilliseconds => Count == 0 ? 0 : _totalMilliseconds / Count;

        public void Add(double milliseconds)
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
            _histogram[(int)Math.Min(LastBucket, Math.Ceiling(milliseconds))]++;
            _totalMilliseconds += milliseconds;
            MaximumMilliseconds = Math.Max(MaximumMilliseconds, milliseconds);
            Count++;
            Over50Ms += milliseconds > 50 ? 1 : 0;
            Over100Ms += milliseconds > 100 ? 1 : 0;
            Over250Ms += milliseconds > 250 ? 1 : 0;
        }

        // Upper edge of a 1 ms bucket; bucket 2000 includes intervals >=1999 ms.
        public int PercentileUpperBound(double percentile)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(percentile);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1);
            int rank = (int)Math.Ceiling(Count * percentile);
            int cumulative = 0;
            for (int index = 0; index < _histogram.Length; index++)
            {
                cumulative += _histogram[index];
                if (cumulative >= rank) return index;
            }
            return 0;
        }

        public void Reset()
        {
            Array.Clear(_histogram);
            _totalMilliseconds = 0;
            Count = Over50Ms = Over100Ms = Over250Ms = 0;
            MaximumMilliseconds = 0;
        }
    }
}
