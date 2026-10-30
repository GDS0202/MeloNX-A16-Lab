using Ryujinx.Memory;

namespace Ryujinx.Graphics.Gpu
{
    /// <summary>Rate limits eviction bursts without delaying a more severe pressure event.</summary>
    internal sealed class MemoryPressureTrimPolicy
    {
        private const long MinimumIntervalMs = 2000;
        private bool _hasTrimmed;
        private long _lastTimestamp;
        private MemoryPressureLevel _lastLevel;

        public bool TryBeginTrim(MemoryPressureLevel level, long timestamp)
        {
            if (level == MemoryPressureLevel.Normal ||
                (_hasTrimmed && level <= _lastLevel && timestamp - _lastTimestamp < MinimumIntervalMs))
            {
                return false;
            }

            _hasTrimmed = true;
            _lastTimestamp = timestamp;
            _lastLevel = level;
            return true;
        }
    }
}
