import Foundation

/// Metadata is ignored by older Options decoders, preserving the existing JSON file format.
enum SettingsOverlay {
    static let overrideKey = "__a16_override_fields"

    static func resolve(global: Data, game: Data) throws -> Data {
        guard let base = try JSONSerialization.jsonObject(with: global) as? [String: Any],
              let saved = try JSONSerialization.jsonObject(with: game) as? [String: Any] else {
            throw CocoaError(.propertyListReadCorrupt)
        }
        guard let metadata = saved[overrideKey] else { return game } // Honor explicit legacy snapshots.
        guard let fields = metadata as? [String] else { throw CocoaError(.propertyListReadCorrupt) }
        var result = base
        for field in fields where field != overrideKey {
            guard let value = saved[field], base[field] != nil else { continue }
            result[field] = value
        }
        return try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
    }

    static func encode(global: Data, effective: Data) throws -> Data {
        guard let base = try JSONSerialization.jsonObject(with: global) as? [String: Any],
              var saved = try JSONSerialization.jsonObject(with: effective) as? [String: Any] else {
            throw CocoaError(.propertyListReadCorrupt)
        }
        let fields = saved.keys.filter { key in
            key != overrideKey && !((saved[key] as? NSObject)?.isEqual(base[key]) ?? false)
        }.sorted()
        saved[overrideKey] = fields
        return try JSONSerialization.data(withJSONObject: saved, options: [.prettyPrinted, .sortedKeys])
    }
}
