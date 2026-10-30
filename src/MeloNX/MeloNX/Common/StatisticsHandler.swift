import SwiftUI
import Foundation
import Combine

@MainActor
final class StatisticsHandler: ObservableObject {
    @Published private(set) var sample = EmulationStatistics()
    private var registered = false
    var fps: Double { sample.fps }
    var frameTime: Double { sample.frameTime }
    var started: Bool { sample.started }
    var fifo: Double { sample.fifo }

    func registerPush() {
        guard !registered else { return }
        registered = true
        CallbackManager.register(name: "push_statistics") { [weak self] data in
            guard let pointer = data.ptr,
                  let decoded = EmulationStatistics.decode(UnsafeRawPointer(pointer), length: Int(data.len)) else { return }
            // Copy values while the native stack packet is alive, publish once on the main actor.
            DispatchQueue.main.async { [weak self] in self?.sample = decoded }
        }
    }

    func unregisterPush() {
        guard registered else { return }
        registered = false
        CallbackManager.unregister(name: "push_statistics")
    }

    func formatFPS() -> String { String(format: "FPS: %.1f", fps) }
}
