using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Common.Utilities
{
    /// <summary>Advisory host policy. Does not change guest clocks or GPU capabilities.</summary>
    public static partial class HostPerformance
    {
        // Avoid beforefieldinit: Swift configures the host environment before core startup.
        static HostPerformance() { }

        public static bool IsA16 { get; } = OperatingSystem.IsIOS() &&
            Environment.GetEnvironmentVariable("MELONX_A16_POLICY") == "1";

        private static int _powerState;
        public static int ThermalState => Volatile.Read(ref _powerState) & 3;
        public static bool LowPowerMode => (Volatile.Read(ref _powerState) & 4) != 0;

        [LibraryImport("libSystem.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
        private static partial int SetQos(uint qosClass, int relativePriority);

        [ThreadStatic] private static uint _lastQos;
        [ThreadStatic] private static bool _qosUnavailable;

        public static void ReportPowerState(int thermalState, bool lowPowerMode)
        {
            Volatile.Write(ref _powerState, Math.Clamp(thermalState, 0, 3) | (lowPowerMode ? 4 : 0));
        }

        public static int TextureWorkerLimit => GetTextureWorkerLimit(
            IsA16, ThermalState, LowPowerMode, Environment.ProcessorCount);

        public static int GetTextureWorkerLimit(bool isA16, int thermalState, bool lowPowerMode, int processorCount)
        {
            if (!isA16) return -1;
            if (thermalState >= 2 || lowPowerMode) return 1;
            return Math.Clamp((processorCount - 1) / 2, 1, 2);
        }

        /// <summary>Only call on owned compiler workers, never shared ThreadPool threads.</summary>
        public static void SetCompilerQos(bool demanded, bool highPriority = false)
        {
            if (!IsA16) return;
            if (_qosUnavailable) { SetFallbackPriority(demanded || highPriority); return; }

            // USER_INITIATED for a renderer dependency, UTILITY for normal precompilation,
            // BACKGROUND for speculative work while hot or in Low Power Mode.
            uint qos = demanded || highPriority ? 0x19u :
                ThermalState >= 2 || LowPowerMode ? 0x09u : 0x11u;
            if (_lastQos == qos) return;

            try
            {
                int result = SetQos(qos, 0);
                if (result != 0)
                {
                    _qosUnavailable = true;
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"A16 compiler QoS unavailable (errno {result}); using managed thread priority.");
                    SetFallbackPriority(demanded || highPriority);
                    return;
                }
                if (_lastQos == 0) Logger.Info?.PrintMsg(LogClass.Gpu, "A16 compiler worker using Apple QoS.");
                _lastQos = qos;
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                _qosUnavailable = true;
                Logger.Warning?.PrintMsg(LogClass.Gpu, "A16 compiler QoS API unavailable; using managed thread priority.");
                SetFallbackPriority(demanded || highPriority);
            }
        }

        private static void SetFallbackPriority(bool demanded)
        {
            ThreadPriority desired = demanded ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
            if (Thread.CurrentThread.Priority != desired) Thread.CurrentThread.Priority = desired;
        }
    }
}
