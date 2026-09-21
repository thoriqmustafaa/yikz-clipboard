import Foundation
import Testing
@testable import ClipCore

@Suite("Sync engine", .serialized)
struct EngineTests {
    @Test func initialSyncDoesNotAutoApply() async throws {
        let h = try await EngineHarness()
        h.server.pageCap = 2
        for i in 0..<5 {
            h.server.add(text: "item \(i)", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-10))
        }
        await h.welcome()
        let state = await h.engine.persistedState()
        #expect(state.lastSeq == 5)
        #expect(state.appliedSeq == 5)
        #expect(state.stateRev == 0)
        #expect(state.serverId == h.server.serverId)
        #expect(h.store.count() == 5)
        #expect(h.store.allEntries().first?.meta?.preview == "item 4")
        await h.settle()
        #expect(h.sink.texts.isEmpty)
        let log = h.server.log()
        #expect(log.contains("GET /api/history?before=6&limit=500"))
        #expect(log.contains("GET /api/history?before=4&limit=500"))
        #expect(log.contains("GET /api/history/index"))
        #expect(await h.engine.isCaughtUp())
    }

    @Test func catchUpPagesAfterAndAppliesNewestEligible() async throws {
        let h = try await EngineHarness()
        h.server.add(text: "seen", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-3600))
        await h.welcome()
        #expect(await h.engine.lastSeq() == 1)
        await h.engine.connectionDropped()

        h.server.pageCap = 2
        h.server.add(text: "old", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-900))
        h.server.add(text: "fresh from phone", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-60))
        h.server.add(text: "newest but mine", device: h.ownDevice, createdAt: h.now.addingTimeInterval(-5))
        h.server.add(text: "too old", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-400))
        await h.welcome()
        await h.settle()

        let log = h.server.log()
        #expect(log.contains("GET /api/history?after=1&limit=500"))
        #expect(log.contains("GET /api/history?after=3&limit=500"))
        #expect(!log.contains("GET /api/history/index") || log.filter { $0 == "GET /api/history/index" }.count == 1)
        let state = await h.engine.persistedState()
        #expect(state.lastSeq == 5)
        #expect(state.appliedSeq == 5)
        #expect(h.store.count() == 5)
        #expect(h.sink.texts == ["fresh from phone"])
    }

    @Test func liveClipDuringCatchUpIsIncluded() async throws {
        let h = try await EngineHarness()
        h.server.add(text: "base", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-3600))
        await h.welcome()
        await h.engine.connectionDropped()
        h.server.add(text: "missed", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-30))
        let welcome = h.server.welcome(at: h.now)
        let live = FakeServer.makeItem(key: key(fastKeyHex), text: "live during catch-up", seq: 3, device: h.otherDevice, createdAt: h.now.addingTimeInterval(-1))
        let engine = h.engine
        h.server.sync { h.server.beforeHistoryAfter = { await engine.handle(.clip(live)) } }
        await engine.handle(.welcome(welcome))
        await engine.awaitCatchUp()
        await h.settle()
        let state = await engine.persistedState()
        #expect(state.lastSeq == 3)
        #expect(state.appliedSeq == 3)
        #expect(h.store.contains(id: live.id))
        #expect(h.sink.texts == ["live during catch-up"])
    }

    @Test func interruptedCatchUpDoesNotAdvanceLastSeq() async throws {
        let h = try await EngineHarness()
        h.server.add(text: "base", device: h.otherDevice, createdAt: h.now)
        await h.welcome()
        await h.engine.connectionDropped()
        h.server.add(text: "missed", device: h.otherDevice, createdAt: h.now)
        let engine = h.engine
        h.server.sync { h.server.beforeHistoryAfter = { await engine.connectionDropped() } }
        await engine.handle(.welcome(h.server.welcome(at: h.now)))
        await engine.awaitCatchUp()
        #expect(await engine.lastSeq() == 1)
        #expect(!(await engine.isCaughtUp()))
        await h.welcome()
        #expect(await engine.lastSeq() == 2)
    }

    @Test func liveClipsAfterCatchUp() async throws {
        let h = try await EngineHarness()
        await h.welcome()
        let k = key(fastKeyHex)
        let mine = FakeServer.makeItem(key: k, text: "mine", seq: 1, device: h.ownDevice, createdAt: h.now)
        await h.engine.handle(.clip(mine))
        let theirs = FakeServer.makeItem(key: k, text: "theirs", seq: 2, device: h.otherDevice, createdAt: h.now)
        await h.engine.handle(.clip(theirs))
        let stale = FakeServer.makeItem(key: k, text: "stale", seq: 3, device: h.otherDevice, createdAt: h.now.addingTimeInterval(-600))
        await h.engine.handle(.clip(stale))
        await h.settle()
        #expect(h.sink.texts == ["theirs"])
        let state = await h.engine.persistedState()
        #expect(state.lastSeq == 3)
        #expect(state.appliedSeq == 3)
        await h.engine.updateSettings(EngineSettings(paused: true))
        await h.engine.handle(.clip(FakeServer.makeItem(key: k, text: "while paused", seq: 4, device: h.otherDevice, createdAt: h.now)))
        await h.settle()
        #expect(h.sink.texts == ["theirs"])
        #expect(await h.engine.persistedState().appliedSeq == 4)
    }

    @Test func stateRevGapTriggersReconcile() async throws {
        let h = try await EngineHarness()
        let a = h.server.add(text: "a", device: h.otherDevice, createdAt: h.now)
        let b = h.server.add(text: "b", device: h.otherDevice, createdAt: h.now)
        let c = h.server.add(text: "c", device: h.otherDevice, createdAt: h.now)
        h.server.sync { h.server.stateRev = 5 }
        await h.welcome()
        #expect(await h.engine.persistedState().stateRev == 5)
        let indexCalls = { h.server.log().filter { $0 == "GET /api/history/index" }.count }
        #expect(indexCalls() == 1)

        h.server.setPinned(id: a.id, true)
        await h.engine.handle(.clipPinned(ClipPinnedMessage(id: a.id, pinned: true, stateRev: 6)))
        await h.settle()
        #expect(await h.engine.persistedState().stateRev == 6)
        #expect(indexCalls() == 1)
        #expect(h.store.entry(id: a.id)?.pinned == true)

        h.server.remove(id: b.id)
        h.server.remove(id: c.id)
        h.server.setPinned(id: a.id, false)
        await h.engine.handle(.clipDeleted(ClipDeletedMessage(ids: [c.id], reason: "user", stateRev: 9)))
        await h.settle()
        #expect(indexCalls() == 2)
        #expect(await h.engine.persistedState().stateRev == 9)
        #expect(h.store.allIds() == [a.id])
        #expect(h.store.entry(id: a.id)?.pinned == false)

        await h.engine.handle(.clipDeleted(ClipDeletedMessage(ids: [a.id], reason: "user", stateRev: 8)))
        await h.settle()
        #expect(indexCalls() == 2)
        #expect(h.store.count() == 0)
        #expect(await h.engine.persistedState().stateRev == 9)
    }

    @Test func reconnectWithChangedStateRevReconciles() async throws {
        let h = try await EngineHarness()
        let a = h.server.add(text: "a", device: h.otherDevice, createdAt: h.now)
        let b = h.server.add(text: "b", device: h.otherDevice, createdAt: h.now)
        await h.welcome()
        await h.engine.connectionDropped()
        h.server.remove(id: a.id)
        h.server.setPinned(id: b.id, true)
        await h.welcome()
        #expect(h.store.allIds() == [b.id])
        #expect(h.store.entry(id: b.id)?.pinned == true)
        #expect(await h.engine.persistedState().stateRev == 2)
    }

    @Test func serverIdChangeClearsCache() async throws {
        let h = try await EngineHarness()
        h.server.add(text: "old server 1", device: h.otherDevice, createdAt: h.now)
        h.server.add(text: "old server 2", device: h.otherDevice, createdAt: h.now)
        await h.welcome()
        #expect(h.store.count() == 2)
        await h.engine.connectionDropped()
        h.server.sync {
            h.server.serverId = "01a0ffff-7000-7691-98e4-301030971d0c"
            h.server.items = []
            h.server.currentSeq = 0
            h.server.stateRev = 0
        }
        h.server.add(text: "new server", device: h.otherDevice, createdAt: h.now)
        await h.welcome()
        let state = await h.engine.persistedState()
        #expect(state.serverId == "01a0ffff-7000-7691-98e4-301030971d0c")
        #expect(state.lastSeq == 1)
        #expect(state.appliedSeq == 1)
        #expect(h.store.allEntries().map(\.preview) == ["new server"])
        await h.settle()
        #expect(h.sink.texts.isEmpty)
    }

    @Test func serverChangeWithNewSaltRejectsKey() async throws {
        let h = try await EngineHarness()
        await h.welcome()
        await h.engine.connectionDropped()
        h.server.sync {
            h.server.serverId = "01a0ffff-7000-7691-98e4-301030971d0c"
            h.server.salt = Data(repeating: 7, count: 16)
            h.server.keyCheck = nil
        }
        var events = h.engine.events.makeAsyncIterator()
        await h.welcome()
        var rejected = false
        for _ in 0..<50 {
            guard let e = await events.next() else { break }
            if case .keyRejected = e { rejected = true; break }
        }
        #expect(rejected)
        #expect(h.server.sync { h.server.keyCheck } == nil)
    }

    @Test func keyCheckIsStoredWhenMissing() async throws {
        let h = try await EngineHarness()
        h.server.sync { h.server.keyCheck = nil }
        await h.welcome()
        #expect(h.server.sync { h.server.keyCheck } == key(fastKeyHex).keyCheck)
        #expect(h.server.log().contains("PUT /api/account/key-check"))
    }

    @Test func echoLoopPrevention() async throws {
        let h = try await EngineHarness()
        await h.welcome()
        let k = key(fastKeyHex)
        let received = FakeServer.makeItem(key: k, text: "line one\nline two", seq: 1, device: h.otherDevice, createdAt: h.now)
        await h.engine.handle(.clip(received))
        await h.settle()
        #expect(h.sink.texts == ["line one\nline two"])

        #expect(await h.engine.submit(OutgoingContent.text("line one\nline two", sourceApp: nil)!) == .skippedEcho)
        #expect(await h.engine.submit(OutgoingContent.text("line one\r\nline two", sourceApp: nil)!) == .skippedEcho)

        #expect(await h.engine.submit(OutgoingContent.text("brand new", sourceApp: "Notes")!) == .queued)
        await h.engine.awaitUploads()
        #expect(h.server.log().contains("POST /api/items"))
        let sent = h.store.allEntries().first
        #expect(sent?.preview == "brand new")
        #expect(sent?.meta?.sourceApp == "Notes")
        #expect(sent?.hasCachedPayload == true)
        #expect(await h.engine.submit(OutgoingContent.text("brand new", sourceApp: nil)!) == .skippedEcho)

        let posts = { h.server.log().filter { $0 == "POST /api/items" }.count }
        #expect(await h.engine.submit(OutgoingContent.text("brand new", sourceApp: nil)!, force: true) == .queued)
        await h.engine.awaitUploads()
        #expect(posts() == 2)

        await h.engine.updateSettings(EngineSettings(syncText: false))
        #expect(await h.engine.submit(OutgoingContent.text("disabled kind", sourceApp: nil)!) == .disabled)
        await h.engine.updateSettings(EngineSettings(paused: true))
        #expect(await h.engine.submit(OutgoingContent.text("paused", sourceApp: nil)!) == .paused)
    }

    @Test func newestCachedItemIsNotResent() async throws {
        let h = try await EngineHarness()
        h.server.add(text: "already newest", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-3600))
        await h.welcome()
        #expect(await h.engine.submit(OutgoingContent.text("already newest", sourceApp: nil)!) == .skippedNewest)
    }

    @Test func copyFromHistoryUsesCachedPayloadAndVerifies() async throws {
        let h = try await EngineHarness()
        let item = h.server.add(text: "copy me", device: h.otherDevice, createdAt: h.now.addingTimeInterval(-3600))
        await h.welcome()
        #expect(h.store.entry(id: item.id)?.hasCachedPayload == false)
        try await h.engine.copyToClipboard(id: item.id)
        #expect(h.sink.texts == ["copy me"])
        #expect(h.store.entry(id: item.id)?.hasCachedPayload == true)
        #expect(await h.engine.recentHashes().contains(item.contentHash))
        #expect(await h.engine.cachedContent(for: item.id) == .text("copy me"))
    }

    @Test func chunkedFilesRoundTrip() async throws {
        let h = try await EngineHarness()
        h.server.sync {
            var l = Limits.defaults
            l.inlineMaxBytes = 64
            l.chunkSizeBytes = 100
            h.server.limits = l
            h.server.dropChunkOnce = 2
        }
        await h.welcome()
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("yikz-chunk-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let a = dir.appendingPathComponent("a.bin")
        let b = dir.appendingPathComponent("b.txt")
        try largePattern(250).write(to: a)
        try Data("hello chunks".utf8).write(to: b)
        let archive = dir.appendingPathComponent("out.ycf1")
        let infos = try YCF1.write(files: [YCF1.SourceFile(name: "a.bin", url: a), YCF1.SourceFile(name: "b.txt", url: b)], to: archive)
        let size = Int64(try Data(contentsOf: archive).count)
        let content = OutgoingContent.files(archive: .file(archive, deleteAfter: false), files: infos, sourceApp: "Finder")
        #expect(await h.engine.submit(content) == .queued)
        await h.engine.awaitUploads()

        let header = try #require(h.server.sync { h.server.items.last })
        #expect(header.chunkCount == Int((size + 99) / 100))
        #expect(header.kind == .files)
        let log = h.server.log()
        #expect(log.filter { $0.hasSuffix("/commit") }.count == 2)
        #expect(log.filter { $0.hasSuffix("/chunks/2") }.count == 2)
        let entry = try #require(h.store.entry(id: header.id))
        #expect(entry.meta?.files?.map(\.name) == ["a.bin", "b.txt"])

        try FileManager.default.removeItem(at: dir.appendingPathComponent("a.bin"))
        let fetched = try await h.engine.content(for: header.id)
        guard case .files(let urls) = fetched else {
            Issue.record("expected files, got \(fetched)")
            return
        }
        #expect(urls.map(\.lastPathComponent) == ["a.bin", "b.txt"])
        #expect(try Data(contentsOf: urls[0]) == largePattern(250))
        #expect(try Data(contentsOf: urls[1]) == Data("hello chunks".utf8))
        #expect(h.server.log().filter { $0.contains("/chunks/") && $0.hasPrefix("GET") }.count == header.chunkCount)
        let again = try await h.engine.content(for: header.id)
        #expect(again == fetched)
        #expect(h.server.log().filter { $0.contains("/chunks/") && $0.hasPrefix("GET") }.count == header.chunkCount)
    }

    @Test func tamperedChunkIsRejected() async throws {
        let h = try await EngineHarness()
        h.server.sync {
            var l = Limits.defaults
            l.inlineMaxBytes = 16
            l.chunkSizeBytes = 32
            h.server.limits = l
        }
        await h.welcome()
        let text = String(repeating: "chunked text ", count: 10)
        #expect(await h.engine.submit(OutgoingContent.text(text, sourceApp: nil)!) == .queued)
        await h.engine.awaitUploads()
        let header = try #require(h.server.sync { h.server.items.last })
        #expect(header.chunkCount > 1)
        #expect(try await h.engine.content(for: header.id) == .text(text))
        h.server.sync {
            var c = h.server.chunks[header.id]![1]!
            c[c.count - 1] ^= 0x01
            h.server.chunks[header.id]![1] = c
        }
        await #expect(throws: CryptoError.self) {
            _ = try await h.engine.content(for: header.id)
        }
    }

    @Test func unauthorizedIsReported() async throws {
        let h = try await EngineHarness()
        await h.engine.configure(identity: EngineIdentity(serverURL: URL(string: "https://fake.test")!, deviceId: h.ownDevice, token: "yc_revoked", key: key(fastKeyHex), salt: h.server.salt))
        var events = h.engine.events.makeAsyncIterator()
        await h.engine.handle(.welcome(h.server.welcome(at: h.now)))
        var sawUnauthorized = false
        for _ in 0..<50 {
            guard let e = await events.next() else { break }
            if case .unauthorized = e { sawUnauthorized = true; break }
        }
        #expect(sawUnauthorized)
    }
}

final class FakeChannel: WebSocketChannel, @unchecked Sendable {
    let lock = NSLock()
    var sent: [String] = []
    var inbox: [String] = []
    var waiters: [CheckedContinuation<String, Error>] = []
    var closedWith: Int?
    var serverCloseCode: Int?
    var failUpgrade: Int?

    func open() async throws {
        if let s = failUpgrade { throw ChannelError.failed("upgrade \(s)") }
    }

    func send(_ text: String) async throws {
        lock.withLock { sent.append(text) }
    }

    func receive() async throws -> String {
        try await withCheckedThrowingContinuation { c in
            lock.lock()
            if closedWith != nil || serverCloseCode != nil {
                lock.unlock()
                c.resume(throwing: ChannelError.closed(serverCloseCode))
                return
            }
            if !inbox.isEmpty {
                let m = inbox.removeFirst()
                lock.unlock()
                c.resume(returning: m)
                return
            }
            waiters.append(c)
            lock.unlock()
        }
    }

    func push(_ text: String) {
        lock.lock()
        if !waiters.isEmpty {
            let w = waiters.removeFirst()
            lock.unlock()
            w.resume(returning: text)
            return
        }
        inbox.append(text)
        lock.unlock()
    }

    func serverClose(_ code: Int) {
        lock.lock()
        serverCloseCode = code
        let ws = waiters
        waiters = []
        lock.unlock()
        for w in ws { w.resume(throwing: ChannelError.closed(code)) }
    }

    func close(code: Int) {
        lock.lock()
        if closedWith == nil { closedWith = code }
        let ws = waiters
        waiters = []
        lock.unlock()
        for w in ws { w.resume(throwing: ChannelError.closed(nil)) }
    }

    var closeCode: Int? {
        lock.lock()
        defer { lock.unlock() }
        return serverCloseCode
    }

    var upgradeStatus: Int? { failUpgrade }

    var sentMessages: [String] {
        lock.lock()
        defer { lock.unlock() }
        return sent
    }
}

final class FakeConnector: WebSocketConnecting, @unchecked Sendable {
    let lock = NSLock()
    var channels: [FakeChannel] = []
    var nextFailUpgrade: Int?

    func makeChannel(url: URL, token: String) -> any WebSocketChannel {
        let c = FakeChannel()
        lock.lock()
        c.failUpgrade = nextFailUpgrade
        channels.append(c)
        lock.unlock()
        return c
    }

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return channels.count
    }

    var last: FakeChannel? {
        lock.lock()
        defer { lock.unlock() }
        return channels.last
    }
}

@Suite("Connection manager", .serialized)
struct ConnectionTests {
    func waitFor(_ condition: @Sendable () async -> Bool) async -> Bool {
        for _ in 0..<200 {
            if await condition() { return true }
            try? await Task.sleep(for: .milliseconds(10))
        }
        return false
    }

    @Test func helloWelcomePingAndCloseCodes() async throws {
        let h = try await EngineHarness()
        let connector = FakeConnector()
        let manager = ConnectionManager(engine: h.engine, connector: connector, backoff: Backoff(base: 0.01, cap: 0.02))
        await manager.start(ConnectionConfig(serverURL: URL(string: "https://fake.test")!, token: "yc_test", deviceId: h.ownDevice, appVersion: "1.0.0"))
        #expect(await waitFor { connector.last?.sentMessages.isEmpty == false })
        let first = try #require(connector.last)
        let hello = try JSONSerialization.jsonObject(with: Data(first.sentMessages[0].utf8)) as! [String: Any]
        #expect(hello["type"] as? String == "hello")
        #expect(hello["platform"] as? String == "macos")
        #expect(hello["device_id"] as? String == h.ownDevice)

        let w = h.server.welcome(at: Date())
        first.push(String(decoding: try JSONCoding.encoder().encode(WelcomeWire(w)), as: UTF8.self))
        #expect(await waitFor { await manager.currentStatus().isConnected })

        first.push(#"{"type":"ping","ts":123}"#)
        #expect(await waitFor { first.sentMessages.contains { $0.contains("\"pong\"") && $0.contains("123") } })

        first.serverClose(1001)
        #expect(await waitFor { connector.count >= 2 })
        let second = try #require(connector.last)
        second.serverClose(4001)
        #expect(await waitFor { await manager.currentStatus() == .stopped(.unauthorized) })
        let count = connector.count
        await manager.kick(reason: "wake")
        try await Task.sleep(for: .milliseconds(100))
        #expect(connector.count == count)
        await manager.stop()
        #expect(await manager.currentStatus() == .idle)
    }

    @Test func replacedConnectionWaitsForUserAction() async throws {
        let h = try await EngineHarness()
        let connector = FakeConnector()
        let manager = ConnectionManager(engine: h.engine, connector: connector, backoff: Backoff(base: 0.01, cap: 0.02))
        await manager.start(ConnectionConfig(serverURL: URL(string: "https://fake.test")!, token: "yc_test", deviceId: h.ownDevice, appVersion: "1.0.0"))
        #expect(await waitFor { connector.last?.sentMessages.isEmpty == false })
        connector.last?.serverClose(4002)
        #expect(await waitFor { await manager.currentStatus() == .stopped(.replaced) })
        #expect(connector.count == 1)
        await manager.kick(reason: "user")
        #expect(await waitFor { connector.count == 2 })
        await manager.stop()
    }

    @Test func unauthorizedUpgradeStops() async throws {
        let h = try await EngineHarness()
        let connector = FakeConnector()
        connector.nextFailUpgrade = 401
        let manager = ConnectionManager(engine: h.engine, connector: connector, backoff: Backoff(base: 0.01, cap: 0.02))
        await manager.start(ConnectionConfig(serverURL: URL(string: "https://fake.test")!, token: "yc_test", deviceId: h.ownDevice, appVersion: "1.0.0"))
        #expect(await waitFor { await manager.currentStatus() == .stopped(.unauthorized) })
        await manager.stop()
    }
}

struct WelcomeWire: Encodable {
    var type = "welcome"
    var protocolVersion: Int
    var serverVersion: String
    var serverId: String
    var serverTime: Date
    var deviceId: String
    var currentSeq: Int64
    var stateRev: Int64
    var onlineDevices: [OnlineDevice]

    init(_ w: WelcomeMessage) {
        protocolVersion = w.protocolVersion
        serverVersion = w.serverVersion
        serverId = w.serverId
        serverTime = w.serverTime
        deviceId = w.deviceId
        currentSeq = w.currentSeq
        stateRev = w.stateRev
        onlineDevices = w.onlineDevices
    }
}
