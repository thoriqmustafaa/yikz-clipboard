import Foundation
@testable import ClipCore

final class FakeServer: HTTPTransport, @unchecked Sendable {
    let lock = NSLock()
    var serverId = "01a05bfb-7000-7691-98e4-301030971d0c"
    var salt = Data(hex: "5c1e8f2a9b3d47e6a0c4f81d2e6b9a73")!
    var keyCheck: String?
    var items: [ItemHeader] = []
    var stateRev: Int64 = 0
    var currentSeq: Int64 = 0
    var pageCap = 500
    var requests: [String] = []
    var beforeHistoryAfter: (@Sendable () async -> Void)?
    var limits: Limits?
    var chunks: [String: [Int: Data]] = [:]
    var thumbs: [String: Data] = [:]
    var dropChunkOnce: Int?
    let key: MasterKey
    let ownDeviceId: String

    init(key: MasterKey, ownDeviceId: String) {
        self.key = key
        self.ownDeviceId = ownDeviceId
        self.keyCheck = key.keyCheck
    }

    func sync<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    func log() -> [String] { sync { requests } }

    @discardableResult
    func add(text: String, device: String, createdAt: Date, pinned: Bool = false) -> ItemHeader {
        sync {
            currentSeq += 1
            let h = FakeServer.makeItem(key: key, text: text, seq: currentSeq, device: device, createdAt: createdAt, pinned: pinned)
            items.append(h)
            return h
        }
    }

    func remove(id: String) {
        sync {
            items.removeAll { $0.id == id }
            stateRev += 1
        }
    }

    func setPinned(id: String, _ pinned: Bool) {
        sync {
            if let i = items.firstIndex(where: { $0.id == id }) { items[i].pinned = pinned }
            stateRev += 1
        }
    }

    static func makeItem(key: MasterKey, text: String, seq: Int64, device: String, createdAt: Date, pinned: Bool = false, id: String = UUIDv7.generate()) -> ItemHeader {
        let content = Data(text.utf8)
        let meta = ItemMeta(mime: Proto.textMime, preview: Preview.make(text), sha256: AEAD.sha256Hex(content))
        let metaSealed = try! AEAD.seal(try! meta.encoded(), key: key, aad: AAD.meta(id))
        let payload = try! AEAD.seal(content, key: key, aad: AAD.payload(id))
        return ItemHeader(
            id: id, seq: seq, deviceId: device, kind: .text, size: Int64(content.count), chunkCount: 0,
            createdAt: createdAt, pinned: pinned, contentHash: key.contentHash(content), hasThumb: false,
            storedBytes: Int64(metaSealed.count + payload.count), meta: metaSealed.base64EncodedString(),
            payload: payload.base64EncodedString()
        )
    }

    func welcome(at date: Date) -> WelcomeMessage {
        sync {
            WelcomeMessage(serverId: serverId, serverTime: date, deviceId: ownDeviceId, currentSeq: currentSeq, stateRev: stateRev)
        }
    }

    func respond(_ status: Int, _ body: Data?) -> (Data, HTTPURLResponse) {
        let r = HTTPURLResponse(url: URL(string: "https://fake")!, statusCode: status, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!
        return (body ?? Data(), r)
    }

    func json<T: Encodable>(_ v: T) -> Data {
        try! JSONCoding.encoder().encode(v)
    }

    func error(_ status: Int, _ code: String) -> (Data, HTTPURLResponse) {
        respond(status, json(APIErrorBody(code: code, message: code, details: nil)))
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let url = request.url!
        let comps = URLComponents(url: url, resolvingAgainstBaseURL: false)!
        let path = comps.path
        let method = request.httpMethod ?? "GET"
        var query: [String: String] = [:]
        for q in comps.queryItems ?? [] { query[q.name] = q.value }
        sync { requests.append("\(method) \(path)\(comps.query.map { "?" + $0 } ?? "")") }
        guard request.value(forHTTPHeaderField: "Authorization") == "Bearer yc_test" else {
            return error(401, "unauthorized")
        }
        if method == "GET", path == "/api/history", query["after"] != nil, let hook = sync({ beforeHistoryAfter }) {
            sync { beforeHistoryAfter = nil }
            await hook()
        }
        return sync { route(method: method, path: path, query: query, body: request.httpBody) }
    }

    private func stripped(_ h: ItemHeader) -> ItemHeader {
        var c = h
        c.payload = nil
        return c
    }

    private func route(method: String, path: String, query: [String: String], body: Data?) -> (Data, HTTPURLResponse) {
        switch (method, path) {
        case ("GET", "/api/me"):
            let device = DeviceInfo(id: ownDeviceId, name: "Mac", platform: "macos", createdAt: Date(), lastSeenAt: Date(), online: true, revoked: false, current: true)
            let me = MeResponse(username: "thoriq", device: device, salt: salt.base64EncodedString(), kdf: KDFParams(algorithm: "pbkdf2-sha256", iterations: 600_000, keyLength: 32), keyCheck: keyCheck, serverId: serverId, serverVersion: "test", protocolVersion: 1, limits: limits)
            return respond(200, json(me))
        case ("PUT", "/api/account/key-check"):
            let req = try! JSONCoding.decoder().decode(KeyCheckRequest.self, from: body!)
            if let k = keyCheck, k != req.keyCheck { return error(409, "key_check_exists") }
            keyCheck = req.keyCheck
            return respond(204, nil)
        case ("GET", "/api/devices"):
            return respond(200, json(DevicesResponse(devices: [])))
        case ("GET", "/api/history/index"):
            let idx = HistoryIndex(currentSeq: currentSeq, stateRev: stateRev, items: items.sorted { $0.seq < $1.seq }.map { HistoryIndexEntry(id: $0.id, seq: $0.seq, pinned: $0.pinned) })
            return respond(200, json(idx))
        case ("GET", "/api/history"):
            let limit = min(Int(query["limit"] ?? "100") ?? 100, pageCap)
            var page: [ItemHeader]
            if let a = query["after"].flatMap(Int64.init) {
                page = items.filter { $0.seq > a }.sorted { $0.seq < $1.seq }
            } else {
                let b = query["before"].flatMap(Int64.init) ?? Int64.max
                page = items.filter { $0.seq < b }.sorted { $0.seq > $1.seq }
            }
            let hasMore = page.count > limit
            page = Array(page.prefix(limit)).map(stripped)
            return respond(200, json(HistoryPage(items: page, hasMore: hasMore)))
        case ("POST", "/api/items"):
            guard keyCheck != nil else { return error(409, "key_check_missing") }
            let req = try! JSONCoding.decoder().decode(CreateItemRequest.self, from: body!)
            currentSeq += 1
            let h = ItemHeader(id: req.id, seq: currentSeq, deviceId: ownDeviceId, kind: req.kind, size: req.size, chunkCount: 0, createdAt: Date(), pinned: false, contentHash: req.contentHash, hasThumb: false, storedBytes: 0, meta: req.meta, payload: req.payload)
            items.append(h)
            return respond(201, json(stripped(h)))
        default:
            let parts = path.split(separator: "/").map(String.init)
            if parts.count == 5, parts[0] == "api", parts[1] == "items", parts[3] == "chunks", let n = Int(parts[4]) {
                let id = parts[2]
                if method == "PUT" {
                    if dropChunkOnce == n {
                        dropChunkOnce = nil
                        return respond(204, nil)
                    }
                    chunks[id, default: [:]][n] = body ?? Data()
                    return respond(204, nil)
                }
                guard let d = chunks[id]?[n] else { return error(404, "not_found") }
                return respond(200, d)
            }
            if parts.count == 4, parts[3] == "thumb" {
                if method == "PUT" {
                    thumbs[parts[2]] = body
                    return respond(204, nil)
                }
                guard let d = thumbs[parts[2]] else { return error(404, "not_found") }
                return respond(200, d)
            }
            if parts.count == 4, parts[3] == "commit", method == "POST" {
                let id = parts[2]
                let req = try! JSONCoding.decoder().decode(CommitRequest.self, from: body!)
                let have = chunks[id] ?? [:]
                let missing = (0..<req.chunkCount).filter { have[$0] == nil }
                if !missing.isEmpty {
                    let details = JSONValue.object(["missing": .array(missing.map { .number(Double($0)) })])
                    return respond(409, json(APIErrorBody(code: "missing_chunks", message: "missing", details: details)))
                }
                currentSeq += 1
                let h = ItemHeader(id: id, seq: currentSeq, deviceId: ownDeviceId, kind: req.kind, size: req.size, chunkCount: req.chunkCount, createdAt: Date(), pinned: false, contentHash: req.contentHash, hasThumb: thumbs[id] != nil, storedBytes: 0, meta: req.meta)
                items.append(h)
                return respond(201, json(h))
            }
            if method == "GET", path.hasPrefix("/api/items/") {
                let id = String(path.dropFirst("/api/items/".count))
                guard let h = items.first(where: { $0.id == id }) else { return error(404, "not_found") }
                return respond(200, json(h))
            }
            return error(404, "not_found")
        }
    }
}

final class SinkRecorder: @unchecked Sendable {
    let lock = NSLock()
    var writes: [(ApplyContent, String)] = []

    var sink: ClipboardSink {
        ClipboardSink { [self] content, id in
            record(content, id)
            return true
        }
    }

    func record(_ content: ApplyContent, _ id: String) {
        lock.withLock { writes.append((content, id)) }
    }

    var texts: [String] {
        lock.lock()
        defer { lock.unlock() }
        return writes.compactMap { if case .text(let s) = $0.0 { return s } else { return nil } }
    }
}

struct EngineHarness {
    let engine: SyncEngine
    let server: FakeServer
    let sink: SinkRecorder
    let store: HistoryStore
    let stateStore: SyncStateStore
    let now: Date
    let ownDevice = "01a05c00-03e0-7c59-b16c-c1bb2928ae88"
    let otherDevice = "01a05c04-97c0-7c1c-bdb7-1933ae6945c4"

    init(now: Date = Date(timeIntervalSince1970: 1_790_000_000)) async throws {
        self.now = now
        let k = key(fastKeyHex)
        server = FakeServer(key: k, ownDeviceId: ownDevice)
        sink = SinkRecorder()
        store = try HistoryStore(path: ":memory:")
        stateStore = SyncStateStore(url: nil)
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("yikz-tests-\(UUID().uuidString)")
        let fixedNow = now
        engine = SyncEngine(
            store: store, stateStore: stateStore, transport: server, sink: sink.sink,
            receivedDir: tmp.appendingPathComponent("received"), tempDir: tmp.appendingPathComponent("tmp"),
            clock: { fixedNow }
        )
        await engine.configure(identity: EngineIdentity(serverURL: URL(string: "https://fake.test")!, deviceId: ownDevice, token: "yc_test", key: k, salt: server.salt))
    }

    func welcome() async {
        await engine.handle(.welcome(server.welcome(at: now)))
        await engine.awaitCatchUp()
    }

    func settle() async {
        for _ in 0..<20 { await Task.yield() }
        try? await Task.sleep(for: .milliseconds(50))
    }
}
