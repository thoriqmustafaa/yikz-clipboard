import Foundation
import Security
import os

public struct PersistedSyncState: Codable, Sendable, Equatable {
    public var serverId: String?
    public var lastSeq: Int64 = 0
    public var stateRev: Int64?
    public var appliedSeq: Int64 = 0

    public init(serverId: String? = nil, lastSeq: Int64 = 0, stateRev: Int64? = nil, appliedSeq: Int64 = 0) {
        self.serverId = serverId
        self.lastSeq = lastSeq
        self.stateRev = stateRev
        self.appliedSeq = appliedSeq
    }
}

public final class SyncStateStore: Sendable {
    private let url: URL?
    private let memory = OSAllocatedUnfairLock(initialState: PersistedSyncState())

    public init(url: URL?) {
        self.url = url
        if let url, let data = try? Data(contentsOf: url),
           let s = try? JSONDecoder().decode(PersistedSyncState.self, from: data) {
            memory.withLock { $0 = s }
        }
    }

    public func load() -> PersistedSyncState {
        memory.withLock { $0 }
    }

    public func save(_ state: PersistedSyncState) {
        memory.withLock { $0 = state }
        guard let url else { return }
        if let data = try? JSONEncoder().encode(state) {
            try? data.write(to: url, options: [.atomic])
            chmod(url.path, 0o600)
        }
    }
}

public struct StoredCredentials: Codable, Sendable, Equatable {
    public var token: String
    public var key: Data?

    public init(token: String, key: Data?) {
        self.token = token
        self.key = key
    }
}

public enum Keychain {
    public static let service = "dev.yikz.clipboard"
    public static let credentialsAccount = "credentials"

    public enum KeychainError: Error, CustomStringConvertible {
        case status(OSStatus)

        public var description: String {
            switch self {
            case .status(let s):
                let msg = SecCopyErrorMessageString(s, nil) as String? ?? "error"
                return "Keychain: \(msg) (\(s))"
            }
        }
    }

    public static func set(_ data: Data, account: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        let update: [String: Any] = [kSecValueData as String: data]
        let status = SecItemUpdate(query as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var add = query
            add[kSecValueData as String] = data
            add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            add[kSecAttrLabel as String] = "Yikz Clipboard"
            let s = SecItemAdd(add as CFDictionary, nil)
            guard s == errSecSuccess else { throw KeychainError.status(s) }
        } else if status != errSecSuccess {
            throw KeychainError.status(status)
        }
    }

    public static func get(account: String) throws -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var out: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &out)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw KeychainError.status(status) }
        return out as? Data
    }

    public static func delete(account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(query as CFDictionary)
    }

    public static func loadCredentials() -> StoredCredentials? {
        do {
            guard let data = try get(account: credentialsAccount) else { return nil }
            return try JSONDecoder().decode(StoredCredentials.self, from: data)
        } catch {
            Log.error("Cannot read credentials: \(error)", "keychain")
            return nil
        }
    }

    public static func saveCredentials(_ c: StoredCredentials) throws {
        try set(try JSONEncoder().encode(c), account: credentialsAccount)
    }

    public static func deleteCredentials() {
        delete(account: credentialsAccount)
    }
}

public struct AppPaths: Sendable {
    public let support: URL
    public let caches: URL

    public init(support: URL, caches: URL) {
        self.support = support
        self.caches = caches
    }

    public static func standard() -> AppPaths {
        let fm = FileManager.default
        let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("dev.yikz.clipboard", isDirectory: true)
        let caches = fm.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("dev.yikz.clipboard", isDirectory: true)
        return AppPaths(support: support, caches: caches)
    }

    public var historyDB: URL { support.appendingPathComponent("history.sqlite") }
    public var syncState: URL { support.appendingPathComponent("sync-state.json") }
    public var received: URL { caches.appendingPathComponent("received", isDirectory: true) }
    public var temporary: URL { caches.appendingPathComponent("tmp", isDirectory: true) }
    public var outgoing: URL { caches.appendingPathComponent("outgoing", isDirectory: true) }
    public var updates: URL { caches.appendingPathComponent("updates", isDirectory: true) }

    public func prepare() {
        let fm = FileManager.default
        for dir in [support, caches, received, temporary, outgoing, updates] {
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        }
        chmod(support.path, 0o700)
        try? (support as NSURL).setResourceValue(true, forKey: .isExcludedFromBackupKey)
    }

    public func pruneReceived(maxAge: TimeInterval = 7 * 86400, maxBytes: Int64 = 2 * 1024 * 1024 * 1024, now: Date = Date()) {
        let fm = FileManager.default
        for dir in [temporary, outgoing] {
            if let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.contentModificationDateKey]) {
                for u in items {
                    let m = (try? u.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
                    if now.timeIntervalSince(m) > 86400 { try? fm.removeItem(at: u) }
                }
            }
        }
        guard let dirs = try? fm.contentsOfDirectory(at: received, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        var entries: [(URL, Date, Int64)] = []
        for d in dirs {
            let m = (try? d.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
            if now.timeIntervalSince(m) > maxAge {
                try? fm.removeItem(at: d)
                continue
            }
            entries.append((d, m, AppPaths.directorySize(d)))
        }
        var total = entries.reduce(0) { $0 + $1.2 }
        guard total > maxBytes else { return }
        for e in entries.sorted(by: { $0.1 < $1.1 }) {
            try? fm.removeItem(at: e.0)
            total -= e.2
            if total <= maxBytes { break }
        }
    }

    public static func directorySize(_ url: URL) -> Int64 {
        var total: Int64 = 0
        if let en = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.fileSizeKey]) {
            for case let f as URL in en {
                total += Int64((try? f.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0)
            }
        }
        return total
    }
}
