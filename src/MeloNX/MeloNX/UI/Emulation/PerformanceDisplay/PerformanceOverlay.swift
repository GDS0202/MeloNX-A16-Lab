import SwiftUI
import Foundation
import os

@MainActor
final class MemoryUsageMonitor: ObservableObject {
    @Published private(set) var memoryUsage: UInt64 = 0
    @Published private(set) var donatedMemory: UInt64 = 0
    @Published private(set) var availableMemory: UInt64 = 0
    private let formatter: ByteCountFormatter = {
        let value = ByteCountFormatter()
        value.allowedUnits = [.useMB, .useGB]
        value.countStyle = .memory
        return value
    }()

    func updateMemoryUsage() {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.stride / 4)
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        memoryUsage = result == KERN_SUCCESS ? info.phys_footprint : 0
        donatedMemory = MLXRamDonor.sharedIfExists?.donatedBytes() ?? 0
        availableMemory = UInt64(os_proc_available_memory())
    }

    func formatMemorySize(_ bytes: UInt64) -> String {
        formatter.string(fromByteCount: Int64(clamping: bytes))
    }
}

struct PerformanceOverlayView: View {
    @StateObject private var memorymonitor = MemoryUsageMonitor()
    @ObservedObject private var nativeSettings = NativeSettingsManager.shared
    @ObservedObject var statisticsHandler: StatisticsHandler
    @State private var batteryLevel = -1
    @State private var batteryWasEnabled = false
    @State private var now = Date()

    private var thermalText: String {
        ["Normal", "Warm", "Hot", "Critical"][Int(statisticsHandler.sample.thermalState)]
    }

    @ViewBuilder
    private var overlayText: some View {
        Text(statisticsHandler.formatFPS() + (nativeSettings.performanceFrameTime(true).bool
             ? String(format: " · avg %.1f ms", statisticsHandler.frameTime) : ""))
        if statisticsHandler.sample.samples > 0 {
            Text(String(format: "CPU p95/p99: %.0f/%.0f ms · max %.0f",
                        statisticsHandler.sample.p95, statisticsHandler.sample.p99, statisticsHandler.sample.maximum))
            Text("CPU gaps >100 ms: \(statisticsHandler.sample.slowFrames) · converted cache: \(statisticsHandler.sample.textureCacheHits) hits")
        }
        if nativeSettings.performanceRam(true).bool {
            Text("RAM: " + memorymonitor.formatMemorySize(memorymonitor.memoryUsage)
                 + " · available: " + memorymonitor.formatMemorySize(memorymonitor.availableMemory))
            if memorymonitor.donatedMemory > 0 {
                Text("Donated: " + memorymonitor.formatMemorySize(memorymonitor.donatedMemory))
            }
        }
        Text("Thermal: \(thermalText)" + (statisticsHandler.sample.lowPower ? " · Low Power Mode" : ""))
        if nativeSettings.performanceFIFO(false).bool {
            Text("FIFO: \(statisticsHandler.fifo, specifier: "%.1f")%")
        }
        if batteryLevel >= 0 { Text("Battery: \(batteryLevel)%") }
        if statisticsHandler.started && now.timeIntervalSince(statisticsHandler.sample.receivedAt) > 2 {
            Text("Waiting for fresh statistics…").foregroundStyle(.orange)
        }
    }

    var body: some View {
        Group {
            if nativeSettings.horizontalorvertical(false).bool {
                HStack(spacing: 8) { overlayText }
            } else {
                VStack(alignment: .leading, spacing: 3) { overlayText }
            }
        }
        .font(.caption2.monospacedDigit())
        .foregroundStyle(.white)
        .padding(8)
        .background(.black.opacity(0.7), in: RoundedRectangle(cornerRadius: 8))
        .onAppear { batteryWasEnabled = UIDevice.current.isBatteryMonitoringEnabled }
        .task {
            UIDevice.current.isBatteryMonitoringEnabled = true
            while !Task.isCancelled {
                memorymonitor.updateMemoryUsage()
                let battery = UIDevice.current.batteryLevel
                batteryLevel = battery < 0 ? -1 : Int(battery * 100)
                now = Date()
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
        }
        .onDisappear { UIDevice.current.isBatteryMonitoringEnabled = batteryWasEnabled }
    }
}
