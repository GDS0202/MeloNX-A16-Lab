import Foundation

struct EmulationStatistics: Sendable {
    var fps: Double = 0
    var frameTime: Double = 0
    var started = false
    var fifo: Double = 0
    var p95: Double = 0
    var p99: Double = 0
    var maximum: Double = 0
    var slowFrames: UInt32 = 0
    var samples: UInt32 = 0
    var textureCacheHits: UInt32 = 0
    var thermalState: UInt8 = 0
    var lowPower = false
    var receivedAt = Date.distantPast

    static func decode(_ pointer: UnsafeRawPointer, length: Int) -> Self? {
        guard length >= 25 else { return nil }
        func number(_ offset: Int) -> Double {
            let value = pointer.loadUnaligned(fromByteOffset: offset, as: Double.self)
            return value.isFinite && value >= 0 ? value : 0
        }
        var result = Self(fps: number(0), frameTime: number(8),
            started: pointer.load(fromByteOffset: 16, as: UInt8.self) != 0, fifo: number(17))
        if length >= 64 && pointer.load(fromByteOffset: 63, as: UInt8.self) == 3 {
            result.p95 = number(25)
            result.p99 = number(33)
            result.maximum = number(41)
            result.slowFrames = pointer.loadUnaligned(fromByteOffset: 49, as: UInt32.self)
            result.samples = pointer.loadUnaligned(fromByteOffset: 53, as: UInt32.self)
            result.textureCacheHits = pointer.loadUnaligned(fromByteOffset: 57, as: UInt32.self)
            result.thermalState = min(pointer.load(fromByteOffset: 61, as: UInt8.self), 3)
            result.lowPower = pointer.load(fromByteOffset: 62, as: UInt8.self) != 0
        }
        result.receivedAt = Date()
        return result
    }
}
