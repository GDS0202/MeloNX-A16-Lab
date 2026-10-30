using Ryujinx.Graphics.Texture;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal static class TextureChecks
{
    // Independent scalar BC1/BC2/BC3 specification reference, deliberately unoptimized.
    private static byte[] Reference(ReadOnlySpan<byte> block, int format)
    {
        ReadOnlySpan<byte> rgb = format == 1 ? block : block[8..];
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(rgb);
        int c1 = BinaryPrimitives.ReadUInt16LittleEndian(rgb[2..]);
        int[][] palette = [new int[4], new int[4], new int[4], new int[4]];
        for (int endpoint = 0; endpoint < 2; endpoint++)
        {
            int color = endpoint == 0 ? c0 : c1;
            int r = color >> 11, g = (color >> 5) & 63, b = color & 31;
            palette[endpoint] = [(r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2), 255];
        }
        bool threeColor = format == 1 && c0 <= c1;
        for (int channel = 0; channel < 3; channel++)
        {
            int a = palette[0][channel], b = palette[1][channel];
            palette[2][channel] = threeColor ? (a + b) / 2 : (2 * a + b) / 3;
            palette[3][channel] = threeColor ? 0 : (a + 2 * b) / 3;
        }
        palette[2][3] = 255; palette[3][3] = threeColor ? 0 : 255;
        byte[] alpha = new byte[8];
        if (format == 3)
        {
            int a = block[0], b = block[1]; alpha[0] = (byte)a; alpha[1] = (byte)b;
            int divisor = a > b ? 7 : 5;
            for (int i = 1; i < divisor; i++) alpha[i + 1] = (byte)(((divisor - i) * a + i * b) / divisor);
            if (a <= b) { alpha[6] = 0; alpha[7] = 255; }
        }
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(rgb[4..]);
        ulong alphaBits = format == 1 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(block);
        byte[] output = new byte[64];
        for (int pixel = 0; pixel < 16; pixel++)
        {
            int[] color = palette[(indices >> (pixel * 2)) & 3];
            for (int channel = 0; channel < 4; channel++) output[pixel * 4 + channel] = (byte)color[channel];
            if (format == 2) output[pixel * 4 + 3] = (byte)(((alphaBits >> (pixel * 4)) & 15) * 17);
            if (format == 3) output[pixel * 4 + 3] = alpha[(alphaBits >> (16 + pixel * 3)) & 7];
        }
        return output;
    }

    public static void PaletteExpansion()
    {
        Console.WriteLine($"Texture backend: AVX2={Avx2.IsSupported}, Vector128={Vector128.IsHardwareAccelerated}");
        Random random = new(0xA16);
        for (int format = 1; format <= 3; format++)
        {
            byte[] block = new byte[format == 1 ? 8 : 16];
            for (int sample = 0; sample < 4096; sample++)
            {
                random.NextBytes(block);
                if (sample == 0) Array.Clear(block);
                if (sample == 1) Array.Fill(block, (byte)255);
                using var actual = format switch
                {
                    1 => BCnDecoder.DecodeBC1(block, 4, 4, 1, 1, 1),
                    2 => BCnDecoder.DecodeBC2(block, 4, 4, 1, 1, 1),
                    _ => BCnDecoder.DecodeBC3(block, 4, 4, 1, 1, 1),
                };
                if (!actual.Span.SequenceEqual(Reference(block, format)))
                    throw new Exception($"BC{format} color/alpha mismatch, sample {sample}");
            }
        }
    }

    public static void ParallelTextureTail()
    {
        byte[] input = new byte[8 * 1027 + 3];
        new Random(16).NextBytes(input);
        using var whole = ETC2Transcoder.TranscodeBC1ToEtc2Pta(input);
        for (int i = 0; i < 1027; i++)
        {
            using var block = ETC2Transcoder.TranscodeBC1ToEtc2Pta(input.AsMemory(i * 8, 8));
            if (!block.Span.SequenceEqual(whole.Span.Slice(i * 8, 8))) throw new Exception("Parallel transcode lost a block");
        }
        if (!input.AsSpan(input.Length - 3).SequenceEqual(whole.Span[^3..])) throw new Exception("Texture tail corrupted");
    }
}
