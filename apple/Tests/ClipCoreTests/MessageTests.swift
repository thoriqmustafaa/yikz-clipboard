import Foundation
import Testing
@testable import ClipCore

@Suite("Wire messages")
struct MessageTests {
    func examples() throws -> [String: [String: Any]] {
        let v = try Vectors.object("http.json")
        var out: [String: [String: Any]] = [:]
        for e in v.list("examples") { out[e.str("name")] = e }
        return out
    }

    func decode<T: Decodable>(_ type: T.Type, _ any: Any?) throws -> T {
        try JSONCoding.decoder().decode(type, from: try jsonData(any ?? NSNull()))
    }

    func encodeMatches<T: Encodable>(_ value: T, _ expected: Any?) throws -> Bool {
        jsonEqual(try JSONCoding.encoder().encode(value), expected ?? NSNull())
    }

    @Test func everyHTTPExampleParses() throws {
        let ex = try examples()
        #expect(ex.count >= 40)
        for (name, e) in ex {
            let status = e.int("status")
            guard let body = e["response_body"] else { continue }
            if status >= 400 {
                let err = try decode(APIErrorBody.self, body)
                #expect(!err.code.isEmpty, "\(name)")
                continue
            }
            let path = e.str("path")
            let method = e.str("method")
            switch (method, path) {
            case ("POST", "/api/login"): _ = try decode(LoginResponse.self, body)
            case ("GET", "/api/me"): _ = try decode(MeResponse.self, body)
            case ("GET", "/api/devices"): _ = try decode(DevicesResponse.self, body)
            case ("GET", "/api/storage"): _ = try decode(StorageInfo.self, body)
            case ("GET", "/healthz"): _ = try decode(HealthResponse.self, body)
            case ("GET", "/api/history/index"): _ = try decode(HistoryIndex.self, body)
            default:
                if path.hasPrefix("/api/history") {
                    _ = try decode(HistoryPage.self, body)
                } else if path.hasPrefix("/api/devices/") {
                    _ = try decode(DeviceInfo.self, body)
                } else if path.hasSuffix("/thumb") {
                    #expect(Data(strictBase64: body as? String ?? "") != nil, "\(name)")
                } else if path.hasPrefix("/api/items") {
                    _ = try decode(ItemHeader.self, body)
                } else {
                    Issue.record("unhandled example \(name)")
                }
            }
        }
    }

    @Test func loginExamples() throws {
        let ex = try examples()
        let newDevice = try #require(ex["login_new_device"])
        let req = LoginRequest(username: "thoriq", password: "login-password-example", deviceName: "Thoriq MacBook Pro")
        #expect(try encodeMatches(req, newDevice["request_body"]))
        let resp = try decode(LoginResponse.self, newDevice["response_body"])
        #expect(resp.keyCheck == nil)
        #expect(resp.kdf.isSupported)
        #expect(resp.kdf.iterations == 600_000)
        #expect(resp.protocolVersion == 1)
        #expect(DeviceToken.isValid(resp.token))
        #expect(Data(strictBase64: resp.salt)?.count == Proto.saltLength)

        let existing = try #require(ex["login_existing_device"])
        let req2 = LoginRequest(username: "thoriq", password: "login-password-example", deviceName: "Pixel 9", platform: "android", deviceId: "01a05c04-97c0-7c1c-bdb7-1933ae6945c4")
        #expect(try encodeMatches(req2, existing["request_body"]))
        let resp2 = try decode(LoginResponse.self, existing["response_body"])
        #expect(resp2.keyCheck == key(primaryKeyHex).keyCheck)

        let limited = try #require(ex["login_rate_limited"])
        #expect(limited.dict("response_headers").str("Retry-After") == "600")
    }

    @Test func meAndLimits() throws {
        let me = try decode(MeResponse.self, try examples()["me"]?["response_body"])
        #expect(me.device.current)
        #expect(me.limits == Limits.defaults)
        #expect(me.keyCheck == key(primaryKeyHex).keyCheck)
        #expect(me.device.createdAt == Timestamp.parse("2026-09-01T08:05:00.000Z"))
    }

    @Test func requestBodies() throws {
        let ex = try examples()
        let create = try #require(ex["item_create_inline"])
        let body = try decode(CreateItemRequest.self, create["request_body"])
        #expect(try encodeMatches(body, create["request_body"]))
        let commit = try #require(ex["commit"])
        let cbody = try decode(CommitRequest.self, commit["request_body"])
        #expect(cbody.chunkCount == 3)
        #expect(try encodeMatches(cbody, commit["request_body"]))
        #expect(try encodeMatches(PinRequest(pinned: true), ex["pin"]?["request_body"]))
        #expect(try encodeMatches(PinRequest(pinned: false), ex["unpin"]?["request_body"]))
        #expect(try encodeMatches(KeyCheckRequest(keyCheck: key(primaryKeyHex).keyCheck), ex["key_check_set"]?["request_body"]))
        #expect(try encodeMatches(RenameDeviceRequest(name: "Gaming PC"), ex["device_rename"]?["request_body"]))
    }

    @Test func errorDetails() throws {
        let ex = try examples()
        let missing = try decode(APIErrorBody.self, ex["commit_missing_chunks"]?["response_body"])
        #expect(missing.code == "missing_chunks")
        #expect(missing.details?["missing"]?.intArray == [1, 2])
        let e = APIError.http(status: 409, code: missing.code, message: missing.message, details: missing.details, retryAfter: nil)
        #expect(e.detailsMissing == [1, 2])
        #expect(!e.isRetryable)
        let mismatch = try decode(APIErrorBody.self, ex["commit_chunk_size_mismatch"]?["response_body"])
        #expect(mismatch.details?["expected"]?.intValue == 1059)
        #expect(APIError.http(status: 503, code: "x", message: "", details: nil, retryAfter: nil).isRetryable)
        #expect(APIError.network("offline").isRetryable)
        #expect(APIError.http(status: 401, code: "unauthorized", message: "", details: nil, retryAfter: nil).isUnauthorized)
        #expect(!APIError.http(status: 401, code: "invalid_credentials", message: "", details: nil, retryAfter: nil).isUnauthorized)
    }

    @Test func historyExamples() throws {
        let ex = try examples()
        let newest = try decode(HistoryPage.self, ex["history_newest"]?["response_body"])
        #expect(newest.items.map(\.seq) == [44, 43, 42])
        #expect(newest.items.allSatisfy { $0.payload == nil })
        let after = try decode(HistoryPage.self, ex["history_after_catch_up"]?["response_body"])
        #expect(after.items.map(\.seq) == after.items.map(\.seq).sorted())
        let index = try decode(HistoryIndex.self, ex["history_index"]?["response_body"])
        #expect(index.currentSeq == 44)
        #expect(index.stateRev == 17)
        let k = key(primaryKeyHex)
        for h in newest.items {
            let meta = try AEAD.open(Data(strictBase64: h.meta)!, key: k, aad: AAD.meta(h.id))
            #expect(try ItemMeta.decode(meta).v == 1)
        }
        let inline = try decode(ItemHeader.self, ex["item_get_inline"]?["response_body"])
        let plain = try AEAD.open(Data(strictBase64: inline.payload!)!, key: k, aad: AAD.payload(inline.id))
        #expect(k.contentHash(plain) == inline.contentHash)
    }

    @Test func webSocketMessages() throws {
        let v = try Vectors.object("ws.json")
        let codes = v.list("close_codes").map { $0.int("code") }
        #expect(codes == [1000, 1001, 4001, 4002, 4003, 4004, 4005])
        var seenTypes = Set<String>()
        for m in v.list("messages") {
            let name = m.str("name")
            let message = m.dict("message")
            seenTypes.insert(message.str("type"))
            let data = try jsonData(message)
            if m.str("direction") == "client_to_server" {
                let client: ClientMessage
                switch message.str("type") {
                case "hello":
                    client = .hello(deviceId: message.str("device_id"), lastSeq: message.int64("last_seq"), appVersion: message.str("app_version"))
                case "ping":
                    client = .ping(message.int64("ts"))
                case "pong":
                    client = .pong(message.int64("ts"))
                default:
                    Issue.record("unexpected client message \(name)")
                    continue
                }
                #expect(jsonEqual(try client.encoded(), message), "\(name)")
                continue
            }
            let decoded = try ServerMessage.decode(data)
            switch (message.str("type"), decoded) {
            case ("welcome", .welcome(let w)):
                #expect(w.currentSeq == 44)
                #expect(w.stateRev == 17)
                #expect(w.onlineDevices.count == 2)
                #expect(w.serverTime == Timestamp.parse("2026-09-21T10:10:00.000Z"))
            case ("presence", .presence(let p)):
                #expect(p.online == message.bool("online"))
            case ("devices_changed", .devicesChanged):
                break
            case ("clip", .clip(let h)):
                #expect(h.id == message.dict("item").str("id"))
                #expect((h.payload != nil) == (h.chunkCount == 0))
            case ("clip_deleted", .clipDeleted(let d)):
                #expect(!d.ids.isEmpty)
                #expect(["user", "retention", "dedupe"].contains(d.reason))
            case ("clip_pinned", .clipPinned(let p)):
                #expect(p.stateRev == 21)
            case ("storage_warning", .storageWarning(let s)):
                #expect(s.active == message.bool("active"))
            case ("ping", .ping(let ts)), ("pong", .pong(let ts)):
                #expect(ts == message.int64("ts"))
            case ("error", .error(let e)):
                #expect(!e.code.isEmpty)
            default:
                Issue.record("message \(name) decoded as \(decoded)")
            }
        }
        #expect(seenTypes.isSuperset(of: ["hello", "welcome", "presence", "clip", "clip_deleted", "clip_pinned", "ping", "pong", "error"]))
        #expect(try ServerMessage.decode(#"{"type":"future_thing","x":1}"#) == .unknown("future_thing"))
        let welcomeWithExtra = #"{"type":"welcome","protocol_version":1,"server_version":"1","server_id":"a","server_time":"2026-09-21T10:10:00Z","device_id":"d","current_seq":1,"state_rev":0,"online_devices":[],"extra":true}"#
        if case .welcome(let w) = try ServerMessage.decode(welcomeWithExtra) {
            #expect(w.currentSeq == 1)
        } else {
            Issue.record("welcome with unknown field failed")
        }
    }

    @Test func timestamps() throws {
        let d = try #require(Timestamp.parse("2026-09-21T10:00:01.250Z"))
        #expect(Timestamp.format(d) == "2026-09-21T10:00:01.250Z")
        #expect(Timestamp.parse("2026-09-21T12:00:01.250+02:00") == d)
        #expect(Timestamp.parse("2026-09-21T10:00:01Z") == d.addingTimeInterval(-0.25))
        #expect(Timestamp.parse("2026-09-21T10:00:01.250") == nil)
        #expect(Timestamp.parse("garbage") == nil)
        #expect(Timestamp.format(Date(timeIntervalSince1970: 0)) == "1970-01-01T00:00:00.000Z")
    }

    @Test func urls() {
        let base = APIClient.normalizedServerURL("clip.yikz.dev/")!
        #expect(base.absoluteString == "https://clip.yikz.dev")
        #expect(APIClient.webSocketURL(for: base).absoluteString == "wss://clip.yikz.dev/ws")
        #expect(APIClient.webSocketURL(for: URL(string: "http://localhost:8080")!).absoluteString == "ws://localhost:8080/ws")
        let client = APIClient(baseURL: base, token: nil, transport: URLSessionTransport())
        #expect(client.url("/api/history", query: [URLQueryItem(name: "after", value: "40")]).absoluteString == "https://clip.yikz.dev/api/history?after=40")
        #expect(APIClient.normalizedServerURL("ftp://x") == nil)
    }
}
