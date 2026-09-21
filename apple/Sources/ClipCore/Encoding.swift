import Foundation

extension Data {
    public init?(hex: String) {
        let chars = Array(hex.utf8)
        guard chars.count % 2 == 0 else { return nil }
        var out = Data(capacity: chars.count / 2)
        var i = 0
        while i < chars.count {
            guard let hi = Data.hexValue(chars[i]), let lo = Data.hexValue(chars[i + 1]) else { return nil }
            out.append(hi << 4 | lo)
            i += 2
        }
        self = out
    }

    public var hex: String {
        let digits = Array("0123456789abcdef".utf8)
        var bytes = [UInt8]()
        bytes.reserveCapacity(count * 2)
        for b in self {
            bytes.append(digits[Int(b >> 4)])
            bytes.append(digits[Int(b & 0x0f)])
        }
        return String(decoding: bytes, as: UTF8.self)
    }

    public init?(strictBase64 string: String) {
        guard string.utf8.count % 4 == 0 else { return nil }
        for c in string.utf8 {
            let ok = (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || (c >= 48 && c <= 57) || c == 43 || c == 47 || c == 61
            if !ok { return nil }
        }
        guard let d = Data(base64Encoded: string) else { return nil }
        self = d
    }

    private static func hexValue(_ c: UInt8) -> UInt8? {
        switch c {
        case 48...57: return c - 48
        case 97...102: return c - 87
        case 65...70: return c - 55
        default: return nil
        }
    }
}

extension String {
    public var isLowerHex64: Bool {
        utf8.count == 64 && utf8.allSatisfy { ($0 >= 48 && $0 <= 57) || ($0 >= 97 && $0 <= 102) }
    }
}

public enum Timestamp {
    public static func format(_ date: Date) -> String {
        let totalMs = Int64((date.timeIntervalSince1970 * 1000).rounded(.down))
        let secs = Int64((Double(totalMs) / 1000).rounded(.down))
        let ms = Int(totalMs - secs * 1000)
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let c = cal.dateComponents([.year, .month, .day, .hour, .minute, .second], from: Date(timeIntervalSince1970: TimeInterval(secs)))
        return String(
            format: "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ",
            c.year ?? 0, c.month ?? 0, c.day ?? 0, c.hour ?? 0, c.minute ?? 0, c.second ?? 0, ms
        )
    }

    public static func parse(_ string: String) -> Date? {
        let s = Array(string.utf8)
        guard s.count >= 20 else { return nil }
        func num(_ from: Int, _ len: Int) -> Int? {
            guard from + len <= s.count else { return nil }
            var v = 0
            for i in from..<(from + len) {
                let c = s[i]
                guard c >= 48 && c <= 57 else { return nil }
                v = v * 10 + Int(c - 48)
            }
            return v
        }
        guard let year = num(0, 4), s[4] == 45, let month = num(5, 2), s[7] == 45, let day = num(8, 2),
              s[10] == 84 || s[10] == 116 || s[10] == 32,
              let hour = num(11, 2), s[13] == 58, let minute = num(14, 2), s[16] == 58, let second = num(17, 2)
        else { return nil }
        var idx = 19
        var fraction = 0.0
        if idx < s.count && s[idx] == 46 {
            idx += 1
            var scale = 0.1
            let start = idx
            while idx < s.count, s[idx] >= 48, s[idx] <= 57 {
                fraction += Double(s[idx] - 48) * scale
                scale /= 10
                idx += 1
            }
            if idx == start { return nil }
        }
        guard idx < s.count else { return nil }
        var offsetSeconds = 0
        if s[idx] == 90 || s[idx] == 122 {
            idx += 1
        } else if s[idx] == 43 || s[idx] == 45 {
            let sign = s[idx] == 43 ? 1 : -1
            guard let oh = num(idx + 1, 2), idx + 3 < s.count, s[idx + 3] == 58, let om = num(idx + 4, 2) else { return nil }
            offsetSeconds = sign * (oh * 3600 + om * 60)
            idx += 6
        } else {
            return nil
        }
        guard idx == s.count else { return nil }
        guard (1...12).contains(month), (1...31).contains(day), hour < 24, minute < 60, second <= 60 else { return nil }
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        var comps = DateComponents()
        comps.year = year
        comps.month = month
        comps.day = day
        comps.hour = hour
        comps.minute = minute
        comps.second = min(second, 59)
        guard let base = cal.date(from: comps) else { return nil }
        return base.addingTimeInterval(fraction - TimeInterval(offsetSeconds))
    }
}

public enum JSONCoding {
    public static func decoder() -> JSONDecoder {
        let d = JSONDecoder()
        d.keyDecodingStrategy = .convertFromSnakeCase
        d.dateDecodingStrategy = .custom { decoder in
            let c = try decoder.singleValueContainer()
            let s = try c.decode(String.self)
            guard let date = Timestamp.parse(s) else {
                throw DecodingError.dataCorruptedError(in: c, debugDescription: "invalid timestamp \(s)")
            }
            return date
        }
        return d
    }

    public static func encoder() -> JSONEncoder {
        let e = JSONEncoder()
        e.keyEncodingStrategy = .convertToSnakeCase
        e.outputFormatting = [.withoutEscapingSlashes]
        e.dateEncodingStrategy = .custom { date, encoder in
            var c = encoder.singleValueContainer()
            try c.encode(Timestamp.format(date))
        }
        return e
    }
}

public enum JSONValue: Codable, Sendable, Equatable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() {
            self = .null
        } else if let b = try? c.decode(Bool.self) {
            self = .bool(b)
        } else if let n = try? c.decode(Double.self) {
            self = .number(n)
        } else if let s = try? c.decode(String.self) {
            self = .string(s)
        } else if let a = try? c.decode([JSONValue].self) {
            self = .array(a)
        } else {
            self = .object(try c.decode([String: JSONValue].self))
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let b): try c.encode(b)
        case .number(let n): try c.encode(n)
        case .string(let s): try c.encode(s)
        case .array(let a): try c.encode(a)
        case .object(let o): try c.encode(o)
        }
    }

    public subscript(key: String) -> JSONValue? {
        if case .object(let o) = self { return o[key] }
        return nil
    }

    public var intArray: [Int]? {
        guard case .array(let a) = self else { return nil }
        return a.compactMap { if case .number(let n) = $0 { return Int(n) } else { return nil } }
    }

    public var intValue: Int? {
        if case .number(let n) = self { return Int(n) }
        return nil
    }
}
