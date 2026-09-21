import Foundation

public struct Backoff: Sendable, Equatable {
    public let base: Double
    public let cap: Double

    public static let reconnect = Backoff(base: 0.5, cap: 30)
    public static let http = Backoff(base: 1, cap: 60)

    public init(base: Double, cap: Double) {
        self.base = base
        self.cap = cap
    }

    public func ceiling(attempt: Int) -> Double {
        let exp = min(max(attempt, 0), 30)
        return min(cap, base * pow(2, Double(exp)))
    }

    public func delay(attempt: Int, unit: Double) -> Double {
        let u = min(max(unit, 0), 1)
        return u * ceiling(attempt: attempt)
    }

    public func delay(attempt: Int) -> Double {
        delay(attempt: attempt, unit: Double.random(in: 0..<1))
    }
}

public enum AutoApply {
    public static func serverNow(localNow: Date, offset: TimeInterval) -> Date {
        localNow.addingTimeInterval(offset)
    }

    public static func age(of item: ItemHeader, localNow: Date, offset: TimeInterval) -> TimeInterval {
        serverNow(localNow: localNow, offset: offset).timeIntervalSince(item.createdAt)
    }

    public static func isEligible(_ item: ItemHeader, ownDeviceId: String, appliedSeq: Int64, localNow: Date, offset: TimeInterval) -> Bool {
        item.deviceId != ownDeviceId
            && item.seq > appliedSeq
            && age(of: item, localNow: localNow, offset: offset) <= Proto.autoApplyMaxAge
    }

    public struct BurstResult: Sendable, Equatable {
        public var apply: ItemHeader?
        public var appliedSeq: Int64
    }

    public static func afterCatchUp(_ items: [ItemHeader], ownDeviceId: String, appliedSeq: Int64, localNow: Date, offset: TimeInterval) -> BurstResult {
        let eligible = items.filter {
            isEligible($0, ownDeviceId: ownDeviceId, appliedSeq: appliedSeq, localNow: localNow, offset: offset)
        }
        let pick = eligible.max { $0.seq < $1.seq }
        let highest = items.map(\.seq).max() ?? appliedSeq
        return BurstResult(apply: pick, appliedSeq: max(appliedSeq, highest))
    }
}

public struct RecentHashes: Sendable, Equatable {
    public let capacity: Int
    public private(set) var hashes: [String] = []

    public init(capacity: Int = Proto.recentHashCapacity) {
        self.capacity = capacity
    }

    public mutating func insert(_ hash: String) {
        if let i = hashes.firstIndex(of: hash) { hashes.remove(at: i) }
        hashes.append(hash)
        if hashes.count > capacity { hashes.removeFirst(hashes.count - capacity) }
    }

    public func contains(_ hash: String) -> Bool {
        hashes.contains(hash)
    }
}

public enum EchoDecision: Sendable, Equatable {
    case upload
    case skipRecent
    case skipNewest
}

public enum EchoGuard {
    public static func normalizedCRLF(_ text: String) -> String {
        guard text.contains("\r\n") else { return text }
        return text.replacingOccurrences(of: "\r\n", with: "\n")
    }

    public static func decide(contentHash: String, normalizedTextHash: String?, recent: RecentHashes, newestCachedHash: String?) -> EchoDecision {
        if recent.contains(contentHash) { return .skipRecent }
        if let n = normalizedTextHash, recent.contains(n) { return .skipRecent }
        if let newest = newestCachedHash, newest == contentHash { return .skipNewest }
        return .upload
    }
}
