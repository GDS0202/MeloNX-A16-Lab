using System;
using System.Buffers.Binary;

namespace Ryujinx.Common.Utilities
{
    public static class StatisticsPacket
    {
        // Keep the first 25 bytes compatible with the old Swift callback. No implicit struct packing.
        public const int Size = 64;
        public const byte Version = 3;

        public static void Write(Span<byte> bytes, double fps, double frameTime, bool started, double fifo,
            PresentationStatistics pacing, long textureCacheHits, int thermalState, bool lowPowerMode)
        {
            if (bytes.Length < Size) throw new ArgumentException("Statistics packet buffer is too small.", nameof(bytes));
            static double Finite(double value) => double.IsFinite(value) && value >= 0 ? value : 0;
            BinaryPrimitives.WriteDoubleLittleEndian(bytes, Finite(fps));
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[8..], Finite(frameTime));
            bytes[16] = started ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[17..], Finite(fifo));
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[25..], Finite(pacing.P95Ms));
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[33..], Finite(pacing.P99Ms));
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[41..], Finite(pacing.MaximumMs));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[49..], (uint)Math.Max(0, pacing.Over100Ms));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[53..], (uint)Math.Max(0, pacing.Samples));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[57..], (uint)Math.Clamp(textureCacheHits, 0, uint.MaxValue));
            bytes[61] = (byte)Math.Clamp(thermalState, 0, 3);
            bytes[62] = lowPowerMode ? (byte)1 : (byte)0;
            bytes[63] = Version;
        }
    }
}
