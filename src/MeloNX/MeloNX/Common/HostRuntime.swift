import Foundation
import Metal

/// Host feedback for CPU work; never changes resolution, guest clocks or GPU capabilities.
enum HostRuntime {
    static func configureEnvironment(device: MTLDevice) {
        // Apple8 also includes M2: match the actual Metal device name as well.
        let isA16 = device.name.range(of: #"\bA16\b"#, options: .regularExpression) != nil
            && device.supportsFamily(.apple8) && !ProcessInfo.processInfo.isiOSAppOnMac
        let policyEnabled = UserDefaults.standard.object(forKey: "a16WorkloadPolicyEnabled") as? Bool ?? true
        setenv("MELONX_A16_POLICY", isA16 && policyEnabled ? "1" : "0", 1)
        let textureCacheEnabled = UserDefaults.standard.object(forKey: "a16TextureDiskCacheEnabled") as? Bool ?? true
        setenv("MELONX_TEXTURE_CACHE", isA16 && textureCacheEnabled ? "1" : "0", 1)
        print("[A16Host] GPU=\(device.name), cpu=\(ProcessInfo.processInfo.activeProcessorCount), ramMiB=\(ProcessInfo.processInfo.physicalMemory / 1048576), apple8=\(device.supportsFamily(.apple8)), argumentBuffersTier=\(device.argumentBuffersSupport.rawValue), a16Policy=\(isA16 && policyEnabled), textureDiskCache=\(isA16 && textureCacheEnabled)")
        if #available(iOS 16.4, *) {
            print("[A16Host] nativeBC=\(device.supportsBCTextureCompression)")
        }
    }

    static func observePowerState() -> [NSObjectProtocol] {
        // Read thermalState before subscribing, as required by Foundation.
        _ = ProcessInfo.processInfo.thermalState
        let report: @Sendable (Notification) -> Void = { _ in
            let state = ProcessInfo.processInfo
            report_host_state(UInt8(clamping: state.thermalState.rawValue), state.isLowPowerModeEnabled ? 1 : 0)
        }
        let observers = [ProcessInfo.thermalStateDidChangeNotification, .NSProcessInfoPowerStateDidChange].map { name in
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main, using: report)
        }
        report(Notification(name: ProcessInfo.thermalStateDidChangeNotification))
        return observers
    }
}
