using Ryujinx.Common.Callbacks;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Utilities;
using Ryujinx.Graphics.Texture;
using Ryujinx.Library;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Reflection;

internal static class GameplayChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void StatisticsAbi()
    {
        byte[] bytes = Enumerable.Repeat((byte)0xCD, 80).ToArray();
        StatisticsPacket.Write(bytes, 25, 40, true, 13.5, new(25, 42, 90, 190, 2), long.MaxValue, 90, true);
        Check(bytes[63] == 3 && bytes[16] == 1, "ABI version/started incorrect");
        Check(BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(17)) == 13.5, "Legacy unaligned FIFO changed");
        Check(BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(25)) == 42 &&
              BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(33)) == 90 &&
              BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(41)) == 190, "Tail timings incorrect");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(49)) == 2 &&
              BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(53)) == 25 &&
              BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(57)) == uint.MaxValue && bytes[61] == 3 && bytes[62] == 1, "Tail counts incorrect");
        Check(bytes.AsSpan(64).ToArray().All(x => x == 0xCD), "Packet overrun");
        StatisticsPacket.Write(bytes, double.NaN, double.PositiveInfinity, false, -1, new(0, 0, 0, 0, 0), -1, -1, false);
        Check(bytes.AsSpan(0, 63).ToArray().All(x => x == 0), "Invalid values or unused packet bytes leaked");
        try { StatisticsPacket.Write(new byte[63], 0, 0, false, 0, new(0,0,0,0,0),0,0,false); throw new Exception("Short buffer accepted"); }
        catch (ArgumentException) { }
    }

    public static void CacheRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), "a16-texture-" + Guid.NewGuid());
        byte[] source = new byte[8192], converted = new byte[8192];
        new Random(16).NextBytes(source); new Random(17).NextBytes(converted);
        string key = TranscodeDiskCache.CreateKey(source, 3, 128, 64, 1, 1, 1);
        Check(key != TranscodeDiskCache.CreateKey(source, 2, 128, 64, 1, 1, 1) &&
              key != TranscodeDiskCache.CreateKey(source, 3, 64, 128, 1, 1, 1), "Format/dimensions alias cache keys");
        try
        {
            using var cache = new TranscodeDiskCache(path);
            Check(!cache.TryRead(key, converted.Length, out _), "Missing entry hit");
            Check(cache.TryStore(key, converted), "Write rejected");
            byte[] reference = converted.ToArray(); Array.Clear(converted);
            Check(cache.WaitForIdle(TimeSpan.FromSeconds(10)), "Writer hung");
            Check(cache.TryRead(key, reference.Length, out var result), "Roundtrip missed");
            using (result) { Check(result.Span.SequenceEqual(reference), "Queued write borrowed caller memory"); result.Span.Clear(); }
            Check(cache.TryRead(key, reference.Length, out var independent), "Second read failed");
            using (independent) Check(independent.Span.SequenceEqual(reference), "Read shared mutable memory");
            string entry = Path.Combine(path, key + ".bin");
            byte[] damaged = File.ReadAllBytes(entry); damaged[^1] ^= 1; File.WriteAllBytes(entry, damaged);
            Check(!cache.TryRead(key, reference.Length, out _), "Corrupt payload accepted");
            File.WriteAllBytes(entry, damaged[..20]);
            Check(!cache.TryRead(key, reference.Length, out _) && !cache.TryStore(key, new byte[16]), "Truncated/small entry accepted");
        }
        finally { Directory.Delete(path, true); }
    }

    public static void CacheBudgetAndFailure()
    {
        string path = Path.Combine(Path.GetTempPath(), "a16-texture-" + Guid.NewGuid());
        byte[] data = new byte[4096];
        try
        {
            using var cache = new TranscodeDiskCache(path, 12 * 1024);
            for (int i = 0; i < 8; i++)
            {
                data[0] = (byte)i;
                Check(cache.TryStore(TranscodeDiskCache.CreateKey(data, 1), data), "Sequential queue rejected");
                Check(cache.WaitForIdle(TimeSpan.FromSeconds(10)), "Writer hung");
            }
            Check(new DirectoryInfo(path).EnumerateFiles("*.bin").Sum(x => x.Length) <= 12 * 1024, "Disk budget unbounded");
            Directory.Delete(path, true); File.WriteAllText(path, "unavailable directory");
            Check(!cache.TryRead("missing", data.Length, out _), "I/O failure hit");
            cache.TryStore("failed-write", data);
            Check(cache.WaitForIdle(TimeSpan.FromSeconds(10)), "I/O failure killed cache worker");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); }
    }

    public static void CallbackLifetime()
    {
        nint name = Marshal.StringToCoTaskMemUTF8("a16-lifetime-test");
        using ManualResetEventSlim entered = new(), release = new(), unregistering = new();
        int oldCalls = 0, newCalls = 0;
        SwiftDataCallback oldCallback = (ref CallbackData _, nint user) => { Check(user == 16, "Bad userData"); Interlocked.Increment(ref oldCalls); entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "Callback release hung"); };
        SwiftDataCallback replacement = (ref CallbackData _, nint user) => Interlocked.Increment(ref newCalls);
        try
        {
            CallbackRegistry.RegisterCallback(name, Marshal.GetFunctionPointerForDelegate(oldCallback), 16);
            Task invoke = Task.Run(() => CallbackRegistry.Invoke("a16-lifetime-test"));
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "Callback did not enter");
            Task retire = Task.Run(() => { unregistering.Set(); CallbackRegistry.UnregisterCallback(name); });
            try { Check(unregistering.Wait(TimeSpan.FromSeconds(10)) && !retire.Wait(100), "Native unregister returned with live userData"); }
            finally { release.Set(); }
            Check(Task.WaitAll([invoke, retire], TimeSpan.FromSeconds(10)), "Retirement deadlocked");
            Check(!CallbackRegistry.Invoke("a16-lifetime-test"), "Removed callback invoked");
            CallbackRegistry.RegisterCallback(name, Marshal.GetFunctionPointerForDelegate(oldCallback), 16);
            CallbackRegistry.RegisterCallback(name, Marshal.GetFunctionPointerForDelegate(replacement), 17);
            CallbackRegistry.Invoke("a16-lifetime-test");
            Check(oldCalls == 1 && newCalls == 1, "Replacement retained stale handler");
        }
        finally { release.Set(); CallbackRegistry.UnregisterCallback(name); Marshal.FreeCoTaskMem(name); GC.KeepAlive(oldCallback); GC.KeepAlive(replacement); }
    }

    public static void LiveSettingsValidation()
    {
        var valid = LiveGraphicsSettings.Create(1, -1, (int)AntiAliasing.None, (int)ScalingFilter.Bilinear, 200, 2, (int)AspectRatio.Fixed16x9, .5f, true);
        Check(valid != null && valid.FilterLevel == 100 && valid.Scale == 1, "Valid 1x settings rejected");
        Check(LiveGraphicsSettings.Create(float.NaN, -1, 0, 0, 0, 2, 0, 1, true) == null &&
              LiveGraphicsSettings.Create(1, -1, int.MaxValue, 0, 0, 2, 0, 1, true) == null &&
              LiveGraphicsSettings.Create(1, -1, 0, 0, 0, 2, 0, 4, true) == null, "Unsafe runtime settings accepted");
    }

    public static void RecompressionPolicy()
    {
        // Exercise production format selection without expanding the GPU assembly's public API.
        var assembly = typeof(Ryujinx.Graphics.Gpu.GraphicsConfig).Assembly;
        Type infoType = assembly.GetType("Ryujinx.Graphics.Gpu.Image.TextureInfo", true)!;
        var ctor = infoType.GetConstructors().Single();
        Type formatInfoType = assembly.GetType("Ryujinx.Graphics.Gpu.Image.FormatInfo", true)!;
        var capsCtor = typeof(Ryujinx.Graphics.GAL.Capabilities).GetConstructors().Single();
        object Default(Type type) => type.IsValueType ? Activator.CreateInstance(type)! : null!;
        object Caps(bool nativeBc) => capsCtor.Invoke(capsCtor.GetParameters().Select(p => p.Name switch
        {
            "supportsEtc2Compression" => (object)true,
            "supportsBc123Compression" => nativeBc,
            _ => Default(p.ParameterType)
        }).ToArray());
        object Info(Ryujinx.Graphics.GAL.Format format) => ctor.Invoke(ctor.GetParameters().Select(p => p.Name switch
        {
            "formatInfo" => Activator.CreateInstance(formatInfoType, [format, (byte)4, (byte)4, (byte)16, (byte)4])!,
            "target" => Ryujinx.Graphics.GAL.Target.Texture2D,
            _ => Default(p.ParameterType)
        }).ToArray());
        MethodInfo method = assembly.GetType("Ryujinx.Graphics.Gpu.Image.TextureCompatibility", true)!.GetMethod("ToHostCompatibleFormat")!;
        Ryujinx.Graphics.GAL.Format Select(Ryujinx.Graphics.GAL.Format format, bool nativeBc = false) =>
            (Ryujinx.Graphics.GAL.Format)formatInfoType.GetProperty("Format")!.GetValue(method.Invoke(null, [Info(format), Caps(nativeBc)]))!;
        bool original = Ryujinx.Graphics.Gpu.GraphicsConfig.EnableTextureRecompression;
        try
        {
            Ryujinx.Graphics.Gpu.GraphicsConfig.EnableTextureRecompression = true;
            Check(Select(Ryujinx.Graphics.GAL.Format.Bc3Unorm) == Ryujinx.Graphics.GAL.Format.Etc2RgbaUnorm, "Compressed mode lost ETC2");
            Ryujinx.Graphics.Gpu.GraphicsConfig.EnableTextureRecompression = false;
            Check(Select(Ryujinx.Graphics.GAL.Format.Bc3Unorm) == Ryujinx.Graphics.GAL.Format.R8G8B8A8Unorm &&
                  Select(Ryujinx.Graphics.GAL.Format.Bc5Unorm) == Ryujinx.Graphics.GAL.Format.R8G8Unorm, "OFF still transcodes");
            Check(Select(Ryujinx.Graphics.GAL.Format.Bc3Unorm, true) == Ryujinx.Graphics.GAL.Format.Bc3Unorm, "Native BC support regressed");
        }
        finally { Ryujinx.Graphics.Gpu.GraphicsConfig.EnableTextureRecompression = original; }
    }
}
