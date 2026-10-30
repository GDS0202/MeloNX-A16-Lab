using Ryujinx.Graphics.Vulkan;
using System.Collections.Concurrent;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static int _passed;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Await(Task task) => Check(task.Wait(Timeout), "Worker timed out");
    private static void Wait(ManualResetEventSlim signal) => Check(signal.Wait(Timeout), "Signal timed out");
    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine($"PASS {name}"); _passed++; }
        catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error}"); throw; }
    }

    private static void CompletionBeforeMaintenance()
    {
        using ManualResetEventSlim entered = new(), release = new();
        PipelineCacheManager manager = new() { Maintenance = (_, _) => { entered.Set(); Wait(release); } };
        using BackgroundCompilationScheduler scheduler = new(manager);
        Task task = scheduler.SchedulePipelineCompile(_ => { }, demanded: true);
        try { Wait(entered); Check(task.IsCompletedSuccessfully, "Ready pipeline is still blocked by maintenance"); }
        finally { release.Set(); }
    }

    private static void MaintenanceFailureDoesNotFailPipeline()
    {
        using ManualResetEventSlim attempted = new();
        PipelineCacheManager manager = new() { Maintenance = (_, _) => { attempted.Set(); throw new IOException("Injected cache I/O failure"); } };
        using BackgroundCompilationScheduler scheduler = new(manager);
        Task task = scheduler.SchedulePipelineCompile(_ => { }, demanded: true);
        Wait(attempted);
        Check(task.IsCompletedSuccessfully, "Cache I/O failed an already compiled pipeline");
        Await(scheduler.ScheduleShaderCompile(() => { }));
    }

    private static void DemandAndPromotionPrecedeSpeculation()
    {
        int workers = BackgroundCompilationScheduler.PipelineCacheCount;
        ManualResetEventSlim[] gates = Enumerable.Range(0, workers).Select(_ => new ManualResetEventSlim()).ToArray();
        using CountdownEvent entered = new(workers);
        ConcurrentQueue<string> order = new();
        ConcurrentQueue<bool> permissions = new();
        PipelineCacheManager manager = new() { Maintenance = (_, allowed) => permissions.Enqueue(allowed) };
        using BackgroundCompilationScheduler scheduler = new(manager);
        List<Task> blockers = [];
        for (int i = 0; i < workers; i++)
            blockers.Add(scheduler.SchedulePipelineCompile(cache => { entered.Signal(); Wait(gates[(int)cache.Handle - 1]); }, demanded: true));
        try
        {
            Check(entered.Wait(Timeout), "Could not occupy workers");
            Task low = scheduler.SchedulePipelineCompile(_ => order.Enqueue("speculative"));
            Task promoted = scheduler.SchedulePipelineCompile(_ => order.Enqueue("promoted"));
            Task demand = scheduler.SchedulePipelineCompile(_ => order.Enqueue("demanded"), demanded: true);
            scheduler.PromotePipelineCompile(promoted);
            gates[0].Set();
            Await(demand); Await(promoted); Await(low);
            Check(order.ToArray().SequenceEqual(["demanded", "promoted", "speculative"]), "Demand priority was lost");
            Check(permissions.Contains(false), "Optional I/O was not deferred");
        }
        finally { foreach (var gate in gates) gate.Set(); Await(Task.WhenAll(blockers)); }
    }

    private static void CompileErrorsAndLaterWork()
    {
        using BackgroundCompilationScheduler scheduler = new(new PipelineCacheManager());
        Task bad = scheduler.ScheduleShaderCompile(() => throw new InvalidOperationException("Injected compilation failure"));
        try { Await(bad); throw new Exception("Failure was swallowed"); }
        catch (AggregateException) { Check(bad.IsFaulted, "Failure was swallowed"); }
        Task[] work = Enumerable.Range(0, 250).Select(i => scheduler.SchedulePipelineCompile(_ => { }, demanded: i % 3 == 0)).ToArray();
        Await(Task.WhenAll(work));
        Check(work.All(x => x.IsCompletedSuccessfully), "Later pipeline failed");
    }

    private static void ShutdownWaitsForMaintenance()
    {
        using ManualResetEventSlim entered = new(), release = new();
        PipelineCacheManager manager = new() { Maintenance = (_, _) => { entered.Set(); Wait(release); } };
        BackgroundCompilationScheduler scheduler = new(manager);
        Task task = scheduler.SchedulePipelineCompile(_ => { }, demanded: true);
        Wait(entered); Check(task.IsCompletedSuccessfully, "Pipeline is not ready");
        Task shutdown = Task.Run(scheduler.Dispose);
        try { Check(!shutdown.IsCompleted, "Worker still owns cache after shutdown"); }
        finally { release.Set(); }
        Await(shutdown);
        Task rejected = scheduler.SchedulePipelineCompile(_ => throw new Exception("Must not run"));
        Check(rejected.IsFaulted, "Post-shutdown work accepted"); _ = rejected.Exception;
    }

    private static void Metrics()
    {
        FrameTimeMetrics m = new();
        for (int i = 0; i < 95; i++) m.Add(33.333);
        m.Add(50); m.Add(100); m.Add(250); m.Add(251); m.Add(2500);
        m.Add(double.NaN); m.Add(double.PositiveInfinity); m.Add(-1);
        Check(m.Count == 100 && m.PercentileUpperBound(.95) == 34 && m.PercentileUpperBound(.99) == 251, "Quantile or validation error");
        Check(m.Over50Ms == 4 && m.Over100Ms == 3 && m.Over250Ms == 2 && m.MaximumMilliseconds == 2500, "Stall thresholds incorrect");
        m.Reset(); Check(m.Count == 0 && m.AverageMilliseconds == 0 && m.PercentileUpperBound(.99) == 0, "Reset failed");
    }

    private static void NoPerFrameAllocation()
    {
        FrameTimeMetrics m = new();
        for (int i = 0; i < 10_000; i++) m.Add(33.333);
        m.Reset(); long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000_000; i++) m.Add(33.333);
        Check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "Histogram accumulation allocated");
    }

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect-descriptors") return DescriptorChecks.Inspect(args[1]);
        Test("pipeline completion precedes maintenance", CompletionBeforeMaintenance);
        Test("maintenance failure does not fail pipeline", MaintenanceFailureDoesNotFailPipeline);
        Test("demand, promotion and checkpoint deferral", DemandAndPromotionPrecedeSpeculation);
        Test("compile errors and later concurrent work", CompileErrorsAndLaterWork);
        Test("shutdown and rejection of later work", ShutdownWaitsForMaintenance);
        Test("quantiles, thresholds, validation and reset", Metrics);
        Test("no per-frame metrics allocation", NoPerFrameAllocation);
        Test("JIT exception cleanup and concurrent interval operations", HostChecks.JitExceptionAndConcurrency);
        Test("critical pressure bypasses warning cooldown", HostChecks.MemoryPressureEscalation);
        Test("A16 thermal/power policy and recovery", HostChecks.AdaptiveWorkload);
        Test("pipeline cache identity and truncation", HostChecks.CacheIdentity);
        Test("BC1/2/3 exact color and alpha reference", TextureChecks.PaletteExpansion);
        Test("parallel transcode boundaries and trailing bytes", TextureChecks.ParallelTextureTail);
        Test("streamed BC2/3/7 mips match full-image reference", TextureChecks.RgbaMipReference);
        Test("versioned statistics ABI and sanitization", GameplayChecks.StatisticsAbi);
        Test("converted texture cache identity, ownership and corruption", GameplayChecks.CacheRoundTrip);
        Test("converted texture disk budget and I/O fallback", GameplayChecks.CacheBudgetAndFailure);
        Test("native callback retirement and replacement", GameplayChecks.CallbackLifetime);
        Test("runtime settings validation", GameplayChecks.LiveSettingsValidation);
        Test("texture recompression OFF and native format preservation", GameplayChecks.RecompressionPolicy);
        Console.WriteLine($"{_passed} checks passed. No iPad or GPU performance claim is made.");
        return 0;
    }
}
