using Ryujinx.Common.Utilities;
using Ryujinx.Cpu.LightningJit;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Memory;
using System.Buffers.Binary;

internal static class HostChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void JitExceptionAndConcurrency()
    {
        TranslatorCache<string> cache = new();
        cache.TryAdd(0x1000, 16, "original");
        try
        {
            cache.AddOrUpdate(0x1000, 16, "replacement", (_, _) => throw new IOException("injected"));
            throw new Exception("Callback exception swallowed");
        }
        catch (IOException) { }
        // A leaked write lock blocks a different thread; use a bounded wait.
        Task continued = Task.Run(() =>
        {
            Check(cache.TryGetValue(0x1000, out string value) && value == "original", "Original lost");
            cache.GetOrAdd(0x2000, 16, "next");
            Check(cache.Count == 2, "Count corrupted");
        });
        Check(continued.Wait(TimeSpan.FromSeconds(5)), "JIT cache remained locked after callback failure");
        Parallel.For(0, 2000, i =>
        {
            ulong address = 0x10000 + (ulong)i * 32;
            cache.TryAdd(address, 16, i.ToString());
            Check(cache.ContainsKey(address), "Concurrent insert lost");
            ulong[] overlaps = [];
            Check(cache.GetOverlaps(address, 1, ref overlaps) == 1, "Concurrent overlap failed");
            cache.Remove(address);
        });
        Check(cache.AsList().Count == 2, "Concurrent cache operations corrupted tree");
        cache.Clear(); Check(cache.Count == 0, "Clear failed");
    }

    public static void MemoryPressureEscalation()
    {
        MemoryPressureTrimPolicy policy = new();
        Check(!policy.TryBeginTrim(MemoryPressureLevel.Normal, 0), "Normal pressure triggered trim");
        Check(policy.TryBeginTrim(MemoryPressureLevel.Warning, 0), "Initial warning ignored");
        Check(!policy.TryBeginTrim(MemoryPressureLevel.Warning, 1), "Warning flood not throttled");
        Check(policy.TryBeginTrim(MemoryPressureLevel.Critical, 2), "Critical escalation delayed");
        Check(!policy.TryBeginTrim(MemoryPressureLevel.Critical, 3), "Critical flood not throttled");
        Check(!policy.TryBeginTrim(MemoryPressureLevel.Warning, 4), "Weaker event bypassed throttle");
        Check(policy.TryBeginTrim(MemoryPressureLevel.Critical, 2002), "Later critical ignored");
    }

    public static void AdaptiveWorkload()
    {
        Check(HostPerformance.GetTextureWorkerLimit(true, 0, false, 5) == 2, "iPad A16 nominal budget");
        Check(HostPerformance.GetTextureWorkerLimit(true, 1, false, 5) == 2, "Fair state overreacted");
        Check(HostPerformance.GetTextureWorkerLimit(true, 2, false, 5) == 1, "Serious state not handled");
        Check(HostPerformance.GetTextureWorkerLimit(true, 3, false, 5) == 1, "Critical state not handled");
        Check(HostPerformance.GetTextureWorkerLimit(true, 0, true, 5) == 1, "Low Power Mode not handled");
        Check(HostPerformance.GetTextureWorkerLimit(true, 0, false, 1) == 1, "Single CPU starved");
        Check(HostPerformance.GetTextureWorkerLimit(false, 3, true, 16) == -1, "Other host changed");
        HostPerformance.ReportPowerState(2, true);
        Check(HostPerformance.ThermalState == 2 && HostPerformance.LowPowerMode, "Host bridge state lost");
        HostPerformance.ReportPowerState(0, false);
        Check(HostPerformance.ThermalState == 0 && !HostPerformance.LowPowerMode, "Cooling did not recover");
    }

    public static void CacheIdentity()
    {
        byte[] header = new byte[32];
        byte[] uuid = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x106b);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0x1234);
        uuid.CopyTo(header, 16);
        Check(PipelineCacheHeader.IsCompatible(header, 0x106b, 0x1234, uuid), "Valid cache rejected");
        for (int size = 0; size < 32; size++)
            Check(!PipelineCacheHeader.IsCompatible(header.AsSpan(0, size), 0x106b, 0x1234, uuid), "Truncated cache accepted");
        foreach (int index in new[] { 0, 4, 8, 12, 16, 31 })
        {
            header[index] ^= 1;
            Check(!PipelineCacheHeader.IsCompatible(header, 0x106b, 0x1234, uuid), "Wrong identity accepted");
            header[index] ^= 1;
        }
    }
}
