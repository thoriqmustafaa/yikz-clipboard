import Foundation
import SQLite3

public enum MetaState: String, Sendable, Codable, Hashable {
    case ok
    case unsupported
    case corrupt
}

public struct HistoryEntry: Sendable, Identifiable, Equatable, Hashable {
    public var id: String
    public var seq: Int64
    public var deviceId: String
    public var kind: ItemKind
    public var size: Int64
    public var chunkCount: Int
    public var createdAt: Date
    public var pinned: Bool
    public var contentHash: String
    public var hasThumb: Bool
    public var storedBytes: Int64
    public var meta: ItemMeta?
    public var metaState: MetaState
    public var hasCachedPayload: Bool

    public var preview: String { meta?.preview ?? "" }
    public var isInline: Bool { chunkCount == 0 }

    public var isLink: Bool {
        guard kind == .text, let p = meta?.preview else { return false }
        return HistoryEntry.linkURL(in: p) != nil
    }

    public static func linkURL(in text: String) -> URL? {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty, !t.contains(" "), !t.contains("\n"), t.count < 2048 else { return nil }
        guard let u = URL(string: t), let scheme = u.scheme?.lowercased() else { return nil }
        if scheme == "http" || scheme == "https" { return u.host != nil ? u : nil }
        if scheme == "mailto" || scheme == "ftp" { return u }
        return nil
    }
}

public final class HistoryStore: @unchecked Sendable {
    private var db: OpaquePointer?
    private let lock = NSLock()
    public let path: String

    public init(path: String) throws {
        self.path = path
        var handle: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
        guard sqlite3_open_v2(path, &handle, flags, nil) == SQLITE_OK else {
            let msg = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "unknown"
            sqlite3_close(handle)
            throw StoreError.open(msg)
        }
        db = handle
        if path != ":memory:" {
            chmod(path, 0o600)
        }
        try exec("PRAGMA journal_mode=WAL")
        try exec("PRAGMA synchronous=NORMAL")
        try exec("""
            CREATE TABLE IF NOT EXISTS items (
              id TEXT PRIMARY KEY,
              seq INTEGER NOT NULL,
              device_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              size INTEGER NOT NULL,
              chunk_count INTEGER NOT NULL,
              created_at REAL NOT NULL,
              pinned INTEGER NOT NULL,
              content_hash TEXT NOT NULL,
              has_thumb INTEGER NOT NULL,
              stored_bytes INTEGER NOT NULL,
              meta_sealed TEXT NOT NULL,
              meta_json TEXT,
              meta_state TEXT NOT NULL,
              payload_sealed BLOB
            )
            """)
        try exec("CREATE INDEX IF NOT EXISTS items_seq ON items(seq)")
        try exec("CREATE TABLE IF NOT EXISTS thumbs (id TEXT PRIMARY KEY, sealed BLOB NOT NULL)")
    }

    deinit {
        sqlite3_close(db)
    }

    public enum StoreError: Error, CustomStringConvertible {
        case open(String)
        case sql(String)

        public var description: String {
            switch self {
            case .open(let m): return "cannot open history cache: \(m)"
            case .sql(let m): return "history cache error: \(m)"
            }
        }
    }

    private func exec(_ sql: String) throws {
        var err: UnsafeMutablePointer<CChar>?
        if sqlite3_exec(db, sql, nil, nil, &err) != SQLITE_OK {
            let msg = err.map { String(cString: $0) } ?? "unknown"
            sqlite3_free(err)
            throw StoreError.sql(msg)
        }
    }

    private func withStatement<T>(_ sql: String, _ body: (OpaquePointer) throws -> T) throws -> T {
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
            throw StoreError.sql(String(cString: sqlite3_errmsg(db)))
        }
        defer { sqlite3_finalize(stmt) }
        return try body(stmt)
    }

    private func bind(_ stmt: OpaquePointer, _ idx: Int32, _ value: String?) {
        if let value {
            sqlite3_bind_text(stmt, idx, value, -1, sqliteTransient)
        } else {
            sqlite3_bind_null(stmt, idx)
        }
    }

    private func bind(_ stmt: OpaquePointer, _ idx: Int32, _ value: Data?) {
        if let value {
            _ = value.withUnsafeBytes { buf in
                sqlite3_bind_blob(stmt, idx, buf.baseAddress, Int32(buf.count), sqliteTransient)
            }
        } else {
            sqlite3_bind_null(stmt, idx)
        }
    }

    private func text(_ stmt: OpaquePointer, _ col: Int32) -> String? {
        guard let p = sqlite3_column_text(stmt, col) else { return nil }
        return String(cString: p)
    }

    private func blob(_ stmt: OpaquePointer, _ col: Int32) -> Data? {
        guard sqlite3_column_type(stmt, col) != SQLITE_NULL else { return nil }
        let n = Int(sqlite3_column_bytes(stmt, col))
        guard let p = sqlite3_column_blob(stmt, col) else { return Data() }
        return Data(bytes: p, count: n)
    }

    public func upsert(_ header: ItemHeader, meta: ItemMeta?, metaState: MetaState, payloadSealed: Data?) throws {
        lock.lock()
        defer { lock.unlock() }
        let metaJSON = try meta.map { String(decoding: try JSONCoding.encoder().encode($0), as: UTF8.self) }
        let sql = """
            INSERT INTO items (id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta_sealed, meta_json, meta_state, payload_sealed)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(id) DO UPDATE SET
              seq=excluded.seq, device_id=excluded.device_id, kind=excluded.kind, size=excluded.size,
              chunk_count=excluded.chunk_count, created_at=excluded.created_at, pinned=excluded.pinned,
              content_hash=excluded.content_hash, has_thumb=excluded.has_thumb, stored_bytes=excluded.stored_bytes,
              meta_sealed=excluded.meta_sealed, meta_json=excluded.meta_json, meta_state=excluded.meta_state,
              payload_sealed=COALESCE(excluded.payload_sealed, items.payload_sealed)
            """
        try withStatement(sql) { s in
            bind(s, 1, header.id)
            sqlite3_bind_int64(s, 2, header.seq)
            bind(s, 3, header.deviceId)
            bind(s, 4, header.kind.rawValue)
            sqlite3_bind_int64(s, 5, header.size)
            sqlite3_bind_int64(s, 6, Int64(header.chunkCount))
            sqlite3_bind_double(s, 7, header.createdAt.timeIntervalSince1970)
            sqlite3_bind_int(s, 8, header.pinned ? 1 : 0)
            bind(s, 9, header.contentHash)
            sqlite3_bind_int(s, 10, header.hasThumb ? 1 : 0)
            sqlite3_bind_int64(s, 11, header.storedBytes)
            bind(s, 12, header.meta)
            bind(s, 13, metaJSON)
            bind(s, 14, metaState.rawValue)
            bind(s, 15, payloadSealed)
            guard sqlite3_step(s) == SQLITE_DONE else { throw StoreError.sql(String(cString: sqlite3_errmsg(db))) }
        }
    }

    private static let entryColumns = "id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta_json, meta_state, payload_sealed IS NOT NULL"

    private func readEntry(_ s: OpaquePointer) -> HistoryEntry? {
        guard let id = text(s, 0), let kindRaw = text(s, 3), let kind = ItemKind(rawValue: kindRaw) else { return nil }
        var meta: ItemMeta?
        if let json = text(s, 11) {
            meta = try? ItemMeta.decode(Data(json.utf8))
        }
        return HistoryEntry(
            id: id,
            seq: sqlite3_column_int64(s, 1),
            deviceId: text(s, 2) ?? "",
            kind: kind,
            size: sqlite3_column_int64(s, 4),
            chunkCount: Int(sqlite3_column_int64(s, 5)),
            createdAt: Date(timeIntervalSince1970: sqlite3_column_double(s, 6)),
            pinned: sqlite3_column_int(s, 7) != 0,
            contentHash: text(s, 8) ?? "",
            hasThumb: sqlite3_column_int(s, 9) != 0,
            storedBytes: sqlite3_column_int64(s, 10),
            meta: meta,
            metaState: MetaState(rawValue: text(s, 12) ?? "") ?? .corrupt,
            hasCachedPayload: sqlite3_column_int(s, 13) != 0
        )
    }

    public func allEntries() -> [HistoryEntry] {
        lock.lock()
        defer { lock.unlock() }
        return (try? withStatement("SELECT \(HistoryStore.entryColumns) FROM items ORDER BY seq DESC") { s in
            var out: [HistoryEntry] = []
            while sqlite3_step(s) == SQLITE_ROW {
                if let e = readEntry(s) { out.append(e) }
            }
            return out
        }) ?? []
    }

    public func entry(id: String) -> HistoryEntry? {
        lock.lock()
        defer { lock.unlock() }
        return try? withStatement("SELECT \(HistoryStore.entryColumns) FROM items WHERE id = ?") { s in
            bind(s, 1, id)
            return sqlite3_step(s) == SQLITE_ROW ? readEntry(s) : nil
        }
    }

    public func header(id: String) -> ItemHeader? {
        lock.lock()
        defer { lock.unlock() }
        return try? withStatement("SELECT id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta_sealed FROM items WHERE id = ?") { s in
            bind(s, 1, id)
            guard sqlite3_step(s) == SQLITE_ROW, let kind = ItemKind(rawValue: text(s, 3) ?? "") else { return nil }
            return ItemHeader(
                id: text(s, 0) ?? id,
                seq: sqlite3_column_int64(s, 1),
                deviceId: text(s, 2) ?? "",
                kind: kind,
                size: sqlite3_column_int64(s, 4),
                chunkCount: Int(sqlite3_column_int64(s, 5)),
                createdAt: Date(timeIntervalSince1970: sqlite3_column_double(s, 6)),
                pinned: sqlite3_column_int(s, 7) != 0,
                contentHash: text(s, 8) ?? "",
                hasThumb: sqlite3_column_int(s, 9) != 0,
                storedBytes: sqlite3_column_int64(s, 10),
                meta: text(s, 11) ?? ""
            )
        }
    }

    public func payloadSealed(id: String) -> Data? {
        lock.lock()
        defer { lock.unlock() }
        return try? withStatement("SELECT payload_sealed FROM items WHERE id = ?") { s in
            bind(s, 1, id)
            return sqlite3_step(s) == SQLITE_ROW ? blob(s, 0) : nil
        }
    }

    public func setPayloadSealed(id: String, _ data: Data) {
        lock.lock()
        defer { lock.unlock() }
        _ = try? withStatement("UPDATE items SET payload_sealed = ? WHERE id = ?") { s in
            bind(s, 1, data)
            bind(s, 2, id)
            sqlite3_step(s)
        }
    }

    public func thumbSealed(id: String) -> Data? {
        lock.lock()
        defer { lock.unlock() }
        return try? withStatement("SELECT sealed FROM thumbs WHERE id = ?") { s in
            bind(s, 1, id)
            return sqlite3_step(s) == SQLITE_ROW ? blob(s, 0) : nil
        }
    }

    public func setThumbSealed(id: String, _ data: Data) {
        lock.lock()
        defer { lock.unlock() }
        _ = try? withStatement("INSERT OR REPLACE INTO thumbs (id, sealed) VALUES (?, ?)") { s in
            bind(s, 1, id)
            bind(s, 2, data)
            sqlite3_step(s)
        }
    }

    public func contains(id: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return (try? withStatement("SELECT 1 FROM items WHERE id = ?") { s in
            bind(s, 1, id)
            return sqlite3_step(s) == SQLITE_ROW
        }) ?? false
    }

    public func allIds() -> Set<String> {
        lock.lock()
        defer { lock.unlock() }
        return (try? withStatement("SELECT id FROM items") { s in
            var out = Set<String>()
            while sqlite3_step(s) == SQLITE_ROW {
                if let id = text(s, 0) { out.insert(id) }
            }
            return out
        }) ?? []
    }

    public func newestContentHash() -> String? {
        lock.lock()
        defer { lock.unlock() }
        return try? withStatement("SELECT content_hash FROM items ORDER BY seq DESC LIMIT 1") { s in
            sqlite3_step(s) == SQLITE_ROW ? text(s, 0) : nil
        }
    }

    public func minSeq() -> Int64? {
        lock.lock()
        defer { lock.unlock() }
        return try? withStatement("SELECT MIN(seq) FROM items") { s in
            guard sqlite3_step(s) == SQLITE_ROW, sqlite3_column_type(s, 0) != SQLITE_NULL else { return nil }
            return sqlite3_column_int64(s, 0)
        }
    }

    public func count() -> Int {
        lock.lock()
        defer { lock.unlock() }
        return (try? withStatement("SELECT COUNT(*) FROM items") { s in
            sqlite3_step(s) == SQLITE_ROW ? Int(sqlite3_column_int64(s, 0)) : 0
        }) ?? 0
    }

    public func setPinned(id: String, pinned: Bool) {
        lock.lock()
        defer { lock.unlock() }
        _ = try? withStatement("UPDATE items SET pinned = ? WHERE id = ?") { s in
            sqlite3_bind_int(s, 1, pinned ? 1 : 0)
            bind(s, 2, id)
            sqlite3_step(s)
        }
    }

    public func delete(ids: [String]) {
        guard !ids.isEmpty else { return }
        lock.lock()
        defer { lock.unlock() }
        try? exec("BEGIN")
        for id in ids {
            _ = try? withStatement("DELETE FROM items WHERE id = ?") { s in
                bind(s, 1, id)
                sqlite3_step(s)
            }
            _ = try? withStatement("DELETE FROM thumbs WHERE id = ?") { s in
                bind(s, 1, id)
                sqlite3_step(s)
            }
        }
        try? exec("COMMIT")
    }

    public func clear() {
        lock.lock()
        defer { lock.unlock() }
        try? exec("DELETE FROM items")
        try? exec("DELETE FROM thumbs")
    }
}

private var sqliteTransient: sqlite3_destructor_type {
    unsafeBitCast(-1, to: sqlite3_destructor_type.self)
}
