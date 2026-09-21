import Foundation

public enum UUIDv7 {
    public static let pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"

    public static func generate(unixMs: UInt64, random: [UInt8]) -> String {
        precondition(random.count == 10)
        var b = [UInt8](repeating: 0, count: 16)
        for i in 0..<6 {
            b[i] = UInt8(truncatingIfNeeded: unixMs >> UInt64(8 * (5 - i)))
        }
        for i in 0..<10 {
            b[6 + i] = random[i]
        }
        b[6] = 0x70 | (b[6] & 0x0f)
        b[8] = 0x80 | (b[8] & 0x3f)
        let h = Data(b).hex
        let c = Array(h)
        return String(c[0..<8]) + "-" + String(c[8..<12]) + "-" + String(c[12..<16]) + "-" + String(c[16..<20]) + "-" + String(c[20..<32])
    }

    public static func generate(now: Date = Date()) -> String {
        var rng = SystemRandomNumberGenerator()
        let random = (0..<10).map { _ in UInt8.random(in: 0...255, using: &rng) }
        let ms = UInt64(max(0, (now.timeIntervalSince1970 * 1000).rounded(.down)))
        return generate(unixMs: ms, random: random)
    }

    public static func isValid(_ s: String) -> Bool {
        let c = Array(s.utf8)
        guard c.count == 36 else { return false }
        for (i, ch) in c.enumerated() {
            switch i {
            case 8, 13, 18, 23:
                if ch != 45 { return false }
            case 14:
                if ch != 55 { return false }
            case 19:
                if !(ch == 56 || ch == 57 || ch == 97 || ch == 98) { return false }
            default:
                let hex = (ch >= 48 && ch <= 57) || (ch >= 97 && ch <= 102)
                if !hex { return false }
            }
        }
        return true
    }
}

public enum DeviceToken {
    public static func isValid(_ s: String) -> Bool {
        let c = Array(s.utf8)
        guard c.count == 46, s.hasPrefix("yc_") else { return false }
        for ch in c.dropFirst(3) {
            let ok = (ch >= 65 && ch <= 90) || (ch >= 97 && ch <= 122) || (ch >= 48 && ch <= 57) || ch == 45 || ch == 95
            if !ok { return false }
        }
        return true
    }
}

public enum Preview {
    public static func make(_ text: String, maxCodePoints: Int = Proto.previewMaxCodePoints) -> String {
        var view = String.UnicodeScalarView()
        view.append(contentsOf: text.unicodeScalars.prefix(maxCodePoints))
        return String(view)
    }

    public static func forFiles(_ names: [String]) -> String {
        make(names.joined(separator: "\n"))
    }
}

public struct ChunkPlan: Sendable, Equatable {
    public let size: Int64
    public let inlineMax: Int64
    public let chunkSize: Int64

    public init(size: Int64, inlineMax: Int64 = Proto.inlineMaxBytes, chunkSize: Int64 = Proto.chunkSizeBytes) {
        self.size = size
        self.inlineMax = inlineMax
        self.chunkSize = chunkSize
    }

    public var isInline: Bool { size <= inlineMax }

    public var chunkCount: Int {
        isInline ? 0 : Int((size + chunkSize - 1) / chunkSize)
    }

    public func range(of index: Int) -> Range<Int64> {
        let start = Int64(index) * chunkSize
        return start..<min(start + chunkSize, size)
    }

    public func plaintextSize(of index: Int) -> Int64 {
        let r = range(of: index)
        return r.upperBound - r.lowerBound
    }

    public func sealedSize(of index: Int) -> Int64 {
        plaintextSize(of: index) + Int64(Proto.sealOverhead)
    }

    public var inlineSealedSize: Int64 { size + Int64(Proto.sealOverhead) }

    public var totalSealedChunkBytes: Int64 {
        guard !isInline else { return 0 }
        return size + Int64(chunkCount * Proto.sealOverhead)
    }
}
