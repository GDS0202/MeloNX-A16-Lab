using System;
using System.Buffers.Binary;

namespace Ryujinx.Graphics.Vulkan
{
    internal static class PipelineCacheHeader
    {
        // VkPipelineCacheHeaderVersionOne has a tightly packed little-endian header.
        public const int Size = 32;

        public static bool IsCompatible(ReadOnlySpan<byte> data, uint vendor, uint device, ReadOnlySpan<byte> uuid)
        {
            return data.Length >= Size && uuid.Length == 16 &&
                BinaryPrimitives.ReadUInt32LittleEndian(data) == Size &&
                BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == 1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) == vendor &&
                BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) == device &&
                data.Slice(16, 16).SequenceEqual(uuid);
        }
    }
}
