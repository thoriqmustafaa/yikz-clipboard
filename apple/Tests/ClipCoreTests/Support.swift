import Foundation
import Testing
@testable import ClipCore

enum Vectors {
    static let directory: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appendingPathComponent("protocol/vectors", isDirectory: true)

    static func data(_ name: String) throws -> Data {
        try Data(contentsOf: directory.appendingPathComponent(name))
    }

    static func object(_ name: String) throws -> [String: Any] {
        let any = try JSONSerialization.jsonObject(with: try data(name))
        guard let o = any as? [String: Any] else { throw VectorError.shape(name) }
        return o
    }

    enum VectorError: Error {
        case shape(String)
    }
}

extension Dictionary where Key == String, Value == Any {
    func str(_ k: String) -> String { self[k] as? String ?? "" }
    func int(_ k: String) -> Int { (self[k] as? NSNumber)?.intValue ?? 0 }
    func int64(_ k: String) -> Int64 { (self[k] as? NSNumber)?.int64Value ?? 0 }
    func bool(_ k: String) -> Bool { (self[k] as? NSNumber)?.boolValue ?? false }
    func dict(_ k: String) -> [String: Any] { self[k] as? [String: Any] ?? [:] }
    func list(_ k: String) -> [[String: Any]] { self[k] as? [[String: Any]] ?? [] }
    func hex(_ k: String) -> Data { Data(hex: str(k)) ?? Data() }
}

func jsonEqual(_ a: Data, _ b: Any) -> Bool {
    guard let x = try? JSONSerialization.jsonObject(with: a, options: [.fragmentsAllowed]) else { return false }
    return NSDictionary(dictionary: ["v": x]).isEqual(to: ["v": b])
}

func jsonData(_ any: Any) throws -> Data {
    try JSONSerialization.data(withJSONObject: any, options: [.fragmentsAllowed])
}

let fastKeyHex = "1812e835782ff141e7c31d36543e43c4f392d24b20d1e5cdbacb32a9b1d709a0"
let primaryKeyHex = "445b4a046c63fd1e1aa196088b1b75665ecba3d831addb5bf5a9908236767294"

func key(_ hex: String) -> MasterKey {
    MasterKey(data: Data(hex: hex)!)!
}
