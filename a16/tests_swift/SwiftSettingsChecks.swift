import Foundation

@main
struct SwiftSettingsChecks {
    static func check(_ value: Bool, _ message: String) { precondition(value, message) }
    @MainActor static func main() throws {
        let prefix = "a16-regression-" + UUID().uuidString
        let title = UUID().uuidString
        let defaults = UserDefaults.standard
        defer {
            for key in defaults.dictionaryRepresentation().keys where key.hasPrefix(prefix) || key == title {
                defaults.removeObject(forKey: key)
            }
        }
        let global = NativeSettingsManager()
        let game = NativeSettingsManager(title, true)
        let inherited = game.setting(forKey: prefix, default: true)
        check(inherited.bool, "Absent Bool ignored true default")
        global.setting(forKey: prefix, default: true).bool = false
        check(!inherited.bool, "Game did not inherit global false")
        inherited.bool = true
        check(inherited.bool && !global.setting(forKey: prefix, default: true).bool, "Game override contaminated globals")
        game.reset()
        check(!inherited.bool, "Reset did not restore global fallback")
        enum Position: String { case left, right }
        enum Mode: Int32 { case first = 1, second = 2 }
        let position = global.setting(forKey: prefix + "position", default: Position.right)
        position.value = .left
        check(position.value == .left, "String raw enum was written but could not be read")
        let mode = global.setting(forKey: prefix + "mode", default: Mode.first)
        mode.value = .second
        check(mode.value == .second, "Int32 raw enum was written but could not be read")
        let fallbackPosition = game.setting(forKey: prefix + "position", default: Position.right)
        check(fallbackPosition.value == .left, "Enum global inheritance failed")
        fallbackPosition.value = .right
        check(fallbackPosition.value == .right && position.value == .left, "Per-game enum override failed")
        print("PASS native defaults, global inheritance, enum persistence, override and reset")

        func json(_ object: [String: Any]) throws -> Data { try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]) }
        func object(_ data: Data) throws -> [String: Any] { try JSONSerialization.jsonObject(with: data) as! [String: Any] }
        let base = try json(["scale": 1, "async": true, "volume": 0.5])
        let custom = try json(["scale": 1, "async": false, "volume": 0.5])
        let saved = try SettingsOverlay.encode(global: base, effective: custom)
        check((try object(saved)[SettingsOverlay.overrideKey] as! [String]) == ["async"], "Untouched fields became overrides")
        let changed = try json(["scale": 1, "async": true, "volume": 0.8])
        let resolved = try object(SettingsOverlay.resolve(global: changed, game: saved))
        check(resolved["async"] as? Bool == false && resolved["volume"] as? Double == 0.8, "Global change failed to propagate around explicit override")
        let legacy = try object(SettingsOverlay.resolve(global: changed, game: custom))
        check(legacy["volume"] as? Double == 0.5, "Legacy full snapshot changed")
        do {
            _ = try SettingsOverlay.resolve(global: base, game: json([SettingsOverlay.overrideKey: 7]))
            preconditionFailure("Invalid metadata accepted")
        } catch { }
        print("PASS sparse override persistence, global changes, legacy and malformed metadata")

        // Deliberately unaligned base pointer and unaligned Double fields reproduce ARM64 input.
        let memory = UnsafeMutableRawPointer.allocate(byteCount: 66, alignment: 8)
        defer { memory.deallocate() }
        memory.initializeMemory(as: UInt8.self, repeating: 0, count: 66)
        let packet = memory.advanced(by: 1)
        func write<T>(_ value: T, _ offset: Int) {
            var copy = value
            withUnsafeBytes(of: &copy) { bytes in packet.advanced(by: offset).copyMemory(from: bytes.baseAddress!, byteCount: bytes.count) }
        }
        write(25.0, 0); write(40.0, 8); write(UInt8(1), 16); write(13.5, 17)
        write(42.0, 25); write(90.0, 33); write(190.0, 41)
        write(UInt32(2), 49); write(UInt32(25), 53); write(UInt32(19), 57)
        write(UInt8(2), 61); write(UInt8(1), 62); write(UInt8(3), 63)
        let sample = EmulationStatistics.decode(packet, length: 64)!
        check(sample.started && sample.fps == 25 && sample.fifo == 13.5 && sample.p95 == 42 && sample.p99 == 90 && sample.maximum == 190, "Statistics field layout mismatch")
        check(sample.samples == 25 && sample.slowFrames == 2 && sample.textureCacheHits == 19 && sample.thermalState == 2 && sample.lowPower, "Statistics tail mismatch")
        check(EmulationStatistics.decode(packet, length: 24) == nil, "Short packet accepted")
        check(EmulationStatistics.decode(packet, length: 25)!.fifo == 13.5 && EmulationStatistics.decode(packet, length: 33)!.samples == 0, "Legacy ABI failed")
        write(UInt8(4), 63)
        check(EmulationStatistics.decode(packet, length: 64)!.samples == 0, "Unknown extension interpreted")
        write(Double.nan, 0)
        check(EmulationStatistics.decode(packet, length: 64)!.fps == 0, "Non-finite FPS escaped")
        print("PASS statistics v3, legacy packets, bounds, unaligned loads and sanitization")
        print("3 Swift regression groups passed; no device performance claim.")
    }
}
