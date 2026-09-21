import Foundation

public actor SyncEngine {
    public nonisolated let events: AsyncStream<EngineEvent>
    private let continuation: AsyncStream<EngineEvent>.Continuation

    private let store: HistoryStore
    private let stateStore: SyncStateStore
    private let transport: any HTTPTransport
    private let sink: ClipboardSink
    private let receivedDir: URL
    private let tempDir: URL
    private let clock: @Sendable () -> Date
    private let initialSyncLimit: Int

    private var identity: EngineIdentity?
    private var settings = EngineSettings()
    private var state: PersistedSyncState
    private var limits = Limits.defaults
    private var clockOffset: TimeInterval = 0
    private var recent = RecentHashes()
    private var caughtUp = false
    private var catchUpGeneration = 0
    private var catchUpTask: Task<Void, Never>?
    private var liveDuringCatchUp: [ItemHeader] = []
    private var reconciling = false
    private var reconcilePending = false
    private var lastWrittenSeq: Int64 = 0
    private var uploadQueue: [UploadJob] = []
    private var uploadWorker: Task<Void, Never>?
    private var online: [OnlineDevice] = []
    private var thumbTasks: [String: Task<Data?, Never>] = [:]
    private var contentTasks: [String: Task<ApplyContent, Error>] = [:]
    private var uploadProgress: [String: TransferProgress] = [:]

    struct UploadJob: Sendable {
        var content: OutgoingContent
        var digest: ContentDigest
    }

    public init(
        store: HistoryStore,
        stateStore: SyncStateStore,
        transport: any HTTPTransport,
        sink: ClipboardSink,
        receivedDir: URL,
        tempDir: URL,
        initialSyncLimit: Int = 3000,
        clock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.store = store
        self.stateStore = stateStore
        self.transport = transport
        self.sink = sink
        self.receivedDir = receivedDir
        self.tempDir = tempDir
        self.clock = clock
        self.initialSyncLimit = initialSyncLimit
        self.state = stateStore.load()
        var cont: AsyncStream<EngineEvent>.Continuation!
        self.events = AsyncStream(bufferingPolicy: .unbounded) { cont = $0 }
        self.continuation = cont
    }

    private func emit(_ e: EngineEvent) {
        continuation.yield(e)
    }

    private func client(_ id: EngineIdentity) -> APIClient {
        APIClient(baseURL: id.serverURL, token: id.token, transport: transport)
    }

    private func saveState() {
        stateStore.save(state)
    }

    private func publish() {
        emit(.history(store.allEntries()))
    }

    public func configure(identity: EngineIdentity?) {
        let changed = self.identity?.deviceId != identity?.deviceId || self.identity?.key != identity?.key
        self.identity = identity
        if identity == nil || changed {
            catchUpGeneration += 1
            catchUpTask?.cancel()
            catchUpTask = nil
            caughtUp = false
            liveDuringCatchUp = []
        }
        if identity == nil {
            uploadWorker?.cancel()
            uploadWorker = nil
            for j in uploadQueue { j.content.cleanup() }
            uploadQueue = []
        }
        publish()
    }

    public func updateSettings(_ s: EngineSettings) {
        settings = s
    }

    public func currentSettings() -> EngineSettings { settings }

    public func lastSeq() -> Int64 { state.lastSeq }

    public func persistedState() -> PersistedSyncState { state }

    public func isCaughtUp() -> Bool { caughtUp }

    public func serverClockOffset() -> TimeInterval { clockOffset }

    public func recentHashes() -> RecentHashes { recent }

    public func snapshot() -> [HistoryEntry] { store.allEntries() }

    public func refreshSnapshot() { publish() }

    public func clearLocalData() {
        catchUpGeneration += 1
        catchUpTask?.cancel()
        catchUpTask = nil
        caughtUp = false
        liveDuringCatchUp = []
        store.clear()
        state = PersistedSyncState()
        saveState()
        recent = RecentHashes()
        lastWrittenSeq = 0
        online = []
        try? FileManager.default.removeItem(at: receivedDir)
        try? FileManager.default.createDirectory(at: receivedDir, withIntermediateDirectories: true)
        emit(.online([]))
        emit(.devices([]))
        publish()
    }

    public func connectionDropped() {
        catchUpGeneration += 1
        catchUpTask?.cancel()
        catchUpTask = nil
        caughtUp = false
        liveDuringCatchUp = []
        online = []
        emit(.online([]))
        emit(.syncing(false))
    }

    public func awaitCatchUp() async {
        while let t = catchUpTask {
            await t.value
            if catchUpTask == nil || catchUpTask == t { break }
        }
    }

    public func awaitUploads() async {
        while let w = uploadWorker {
            await w.value
            if uploadWorker == nil { break }
        }
    }

    public func handle(_ message: ServerMessage) async {
        switch message {
        case .welcome(let w):
            handleWelcome(w)
        case .clip(let h):
            handleClip(h)
        case .clipDeleted(let m):
            handleDeleted(m)
        case .clipPinned(let m):
            handlePinned(m)
        case .presence(let p):
            online.removeAll { $0.deviceId == p.deviceId }
            if p.online {
                online.append(OnlineDevice(deviceId: p.deviceId, name: p.name, platform: p.platform))
            }
            Log.info("\(p.name) is \(p.online ? "online" : "offline")", "sync")
            emit(.online(online))
        case .devicesChanged:
            Task { await self.refreshDevices() }
        case .storageWarning(let s):
            if s.active {
                Log.warning("Server disk is low: \(s.freeDiskBytes) bytes free", "sync")
            }
            emit(.storageWarning(s))
        case .error(let e):
            Log.error("Server error \(e.code): \(e.message)", "ws")
        case .ping, .pong, .unknown:
            break
        }
    }

    private func decodeMeta(_ h: ItemHeader, key: MasterKey) -> (ItemMeta?, MetaState) {
        guard let sealed = Data(strictBase64: h.meta) else {
            Log.error("Item \(h.id): meta is not valid base64", "crypto")
            return (nil, .corrupt)
        }
        let plain: Data
        do {
            plain = try AEAD.open(sealed, key: key, aad: AAD.meta(h.id))
        } catch {
            Log.error("Item \(h.id): cannot decrypt meta (\(error))", "crypto")
            return (nil, .corrupt)
        }
        struct Version: Decodable { var v: Int }
        if let v = try? JSONDecoder().decode(Version.self, from: plain), v.v != 1 {
            return (nil, .unsupported)
        }
        guard let meta = try? ItemMeta.decode(plain) else {
            Log.error("Item \(h.id): meta JSON is invalid", "crypto")
            return (nil, .corrupt)
        }
        return (meta, .ok)
    }

    @discardableResult
    private func insert(_ h: ItemHeader, key: MasterKey, payloadSealed: Data? = nil, meta knownMeta: ItemMeta? = nil) -> Bool {
        let (meta, metaState) = knownMeta.map { ($0, MetaState.ok) } ?? decodeMeta(h, key: key)
        var payload = payloadSealed
        if payload == nil, let p = h.payload {
            payload = Data(strictBase64: p)
        }
        do {
            try store.upsert(h, meta: meta, metaState: metaState, payloadSealed: payload)
            return true
        } catch {
            Log.error("Cannot store item \(h.id): \(error)", "store")
            return false
        }
    }

    private func handleWelcome(_ w: WelcomeMessage) {
        guard identity != nil else { return }
        catchUpGeneration += 1
        let gen = catchUpGeneration
        catchUpTask?.cancel()
        var serverChanged = false
        if let stored = state.serverId, stored != w.serverId {
            Log.warning("Server id changed (\(stored) to \(w.serverId)); clearing local history", "sync")
            store.clear()
            state = PersistedSyncState()
            lastWrittenSeq = 0
            serverChanged = true
        }
        state.serverId = w.serverId
        saveState()
        clockOffset = w.serverTime.timeIntervalSince(clock())
        online = w.onlineDevices
        emit(.online(online))
        caughtUp = false
        liveDuringCatchUp = []
        if serverChanged { publish() }
        Log.info("Welcome: current_seq \(w.currentSeq), state_rev \(w.stateRev), local last_seq \(state.lastSeq), clock offset \(String(format: "%.2f", clockOffset)) s", "sync")
        catchUpTask = Task { await self.runCatchUp(w, gen: gen, serverChanged: serverChanged) }
    }

    private func runCatchUp(_ w: WelcomeMessage, gen: Int, serverChanged: Bool) async {
        emit(.syncing(true))
        var attempt = 0
        while gen == catchUpGeneration && !Task.isCancelled {
            guard let identity else { break }
            do {
                try await catchUp(w, gen: gen, identity: identity, verifyKey: serverChanged || attempt == 0)
                break
            } catch is CancellationError {
                break
            } catch let e as APIError where e.isUnauthorized {
                Log.error("Catch-up: token rejected", "sync")
                emit(.unauthorized)
                break
            } catch let e as EngineError {
                Log.error("Catch-up stopped: \(e)", "sync")
                break
            } catch {
                guard gen == catchUpGeneration else { break }
                let d = Backoff.http.delay(attempt: attempt)
                Log.warning("Catch-up failed (\(error)); retry in \(String(format: "%.1f", d)) s", "sync")
                attempt += 1
                try? await Task.sleep(nanoseconds: UInt64(d * 1_000_000_000))
            }
        }
        if gen == catchUpGeneration {
            emit(.syncing(false))
        }
    }

    private func verifyAccount(_ api: APIClient, identity: EngineIdentity) async throws {
        let me = try await api.me()
        guard me.protocolVersion == Proto.version, me.kdf.isSupported else {
            emit(.protocolUnsupported)
            throw EngineError.protocolUnsupported
        }
        limits = me.limits ?? .defaults
        guard Data(strictBase64: me.salt) == identity.salt else {
            Log.error("Account salt changed; the encryption password must be entered again", "sync")
            emit(.keyRejected)
            throw EngineError.keyRejected
        }
        try await ensureKeyCheck(api, key: identity.key, known: .some(me.keyCheck))
    }

    private func ensureKeyCheck(_ api: APIClient, key: MasterKey, known: String?? = nil) async throws {
        let serverValue: String?
        if let known {
            serverValue = known
        } else {
            serverValue = try await api.me().keyCheck
        }
        if let serverValue {
            guard serverValue == key.keyCheck else {
                Log.error("Key check mismatch; the encryption password is wrong for this server", "sync")
                emit(.keyRejected)
                throw EngineError.keyRejected
            }
            return
        }
        do {
            try await api.putKeyCheck(key.keyCheck)
            Log.info("Stored key check on server", "sync")
        } catch let e as APIError where e.code == "key_check_exists" {
            let again = try await api.me().keyCheck
            guard again == key.keyCheck else {
                emit(.keyRejected)
                throw EngineError.keyRejected
            }
        }
    }

    private func catchUp(_ w: WelcomeMessage, gen: Int, identity: EngineIdentity, verifyKey: Bool) async throws {
        let api = client(identity)
        let key = identity.key
        if verifyKey {
            try await verifyAccount(api, identity: identity)
            guard gen == catchUpGeneration else { return }
            Task { await self.refreshDevices() }
        }
        let startLast = state.lastSeq
        var candidates: [ItemHeader] = []
        if startLast == 0 {
            var before = w.currentSeq + 1
            var fetched = 0
            while true {
                let page = try await api.history(before: before, limit: Proto.historyMaxLimit)
                guard gen == catchUpGeneration else { return }
                for h in page.items { insert(h, key: key) }
                fetched += page.items.count
                guard page.hasMore, let last = page.items.last, fetched < initialSyncLimit else { break }
                before = last.seq
            }
            Log.info("Initial sync loaded \(fetched) items", "sync")
            publish()
            let liveMax = liveDuringCatchUp.map(\.seq).max() ?? 0
            state.lastSeq = max(w.currentSeq, liveMax)
            state.appliedSeq = max(state.appliedSeq, w.currentSeq)
            candidates = []
        } else if w.currentSeq > startLast {
            var cursor = startLast
            var inserted: [ItemHeader] = []
            while true {
                let page = try await api.history(after: cursor, limit: Proto.historyMaxLimit)
                guard gen == catchUpGeneration else { return }
                for h in page.items { insert(h, key: key) }
                inserted += page.items
                if let last = page.items.last { cursor = last.seq }
                if !page.hasMore || page.items.isEmpty { break }
            }
            Log.info("Catch-up fetched \(inserted.count) items after seq \(startLast)", "sync")
            publish()
            let liveMax = liveDuringCatchUp.map(\.seq).max() ?? 0
            state.lastSeq = max(state.lastSeq, cursor, liveMax)
            candidates = inserted
        } else {
            let liveMax = liveDuringCatchUp.map(\.seq).max() ?? 0
            state.lastSeq = max(state.lastSeq, liveMax)
        }
        saveState()
        if state.stateRev != w.stateRev {
            try await reconcile(api, key: key)
            guard gen == catchUpGeneration else { return }
        }
        let live = liveDuringCatchUp.filter { startLast != 0 || $0.seq > w.currentSeq }
        for h in live where !candidates.contains(where: { $0.id == h.id }) {
            candidates.append(h)
        }
        let liveMax = liveDuringCatchUp.map(\.seq).max() ?? 0
        state.lastSeq = max(state.lastSeq, liveMax)
        let burst = AutoApply.afterCatchUp(candidates, ownDeviceId: identity.deviceId, appliedSeq: state.appliedSeq, localNow: clock(), offset: clockOffset)
        state.appliedSeq = burst.appliedSeq
        liveDuringCatchUp = []
        caughtUp = true
        saveState()
        publish()
        Log.info("Sync complete: last_seq \(state.lastSeq), applied_seq \(state.appliedSeq), state_rev \(state.stateRev.map(String.init) ?? "none")", "sync")
        if let item = burst.apply {
            scheduleApply(item)
        }
    }

    private func reconcile(_ api: APIClient, key: MasterKey) async throws {
        if reconciling {
            reconcilePending = true
            return
        }
        reconciling = true
        defer { reconciling = false }
        repeat {
            reconcilePending = false
            let index = try await api.historyIndex()
            let serverIds = Set(index.items.map(\.id))
            let entries = store.allEntries()
            let localIds = Set(entries.map(\.id))
            let removed = Array(localIds.subtracting(serverIds))
            store.delete(ids: removed)
            let pinnedLocal = Dictionary(entries.map { ($0.id, $0.pinned) }, uniquingKeysWith: { a, _ in a })
            var pinChanges = 0
            for e in index.items {
                if let p = pinnedLocal[e.id], p != e.pinned {
                    store.setPinned(id: e.id, pinned: e.pinned)
                    pinChanges += 1
                }
            }
            let floor = store.minSeq() ?? Int64.max
            let missing = index.items
                .filter { !localIds.contains($0.id) && $0.seq >= floor }
                .sorted { $0.seq > $1.seq }
                .prefix(100)
            var fetched = 0
            for m in missing {
                do {
                    let h = try await api.item(id: m.id)
                    insert(h, key: key)
                    fetched += 1
                } catch let e as APIError where e.status == 404 {
                    continue
                }
            }
            state.stateRev = index.stateRev
            saveState()
            publish()
            Log.info("Reconciled with server: removed \(removed.count), pin changes \(pinChanges), fetched \(fetched), state_rev \(index.stateRev)", "sync")
        } while reconcilePending
    }

    private func requestReconcile() {
        guard let identity else { return }
        let api = client(identity)
        let key = identity.key
        Task {
            do {
                try await self.reconcile(api, key: key)
            } catch let e as APIError where e.isUnauthorized {
                self.emit(.unauthorized)
            } catch {
                Log.warning("Reconcile failed: \(error)", "sync")
            }
        }
    }

    private func handleClip(_ h: ItemHeader) {
        guard let identity else { return }
        insert(h, key: identity.key)
        recent.insert(h.contentHash)
        if !caughtUp {
            liveDuringCatchUp.append(h)
            publish()
            return
        }
        state.lastSeq = max(state.lastSeq, h.seq)
        let eligible = AutoApply.isEligible(h, ownDeviceId: identity.deviceId, appliedSeq: state.appliedSeq, localNow: clock(), offset: clockOffset)
        state.appliedSeq = max(state.appliedSeq, h.seq)
        saveState()
        publish()
        if eligible {
            scheduleApply(h)
        }
    }

    private func trackStateRev(_ rev: Int64) {
        if let current = state.stateRev, rev == current + 1 {
            state.stateRev = rev
            saveState()
        } else if state.stateRev == nil || rev > (state.stateRev ?? 0) {
            Log.info("state_rev jumped to \(rev) (local \(state.stateRev.map(String.init) ?? "none")); reconciling", "sync")
            requestReconcile()
        }
    }

    private func handleDeleted(_ m: ClipDeletedMessage) {
        store.delete(ids: m.ids)
        for id in m.ids {
            try? FileManager.default.removeItem(at: receivedDir.appendingPathComponent(id, isDirectory: true))
        }
        Log.info("Deleted \(m.ids.count) item(s), reason \(m.reason)", "sync")
        trackStateRev(m.stateRev)
        publish()
    }

    private func handlePinned(_ m: ClipPinnedMessage) {
        store.setPinned(id: m.id, pinned: m.pinned)
        trackStateRev(m.stateRev)
        publish()
    }

    public func refreshDevices() async {
        guard let identity else { return }
        do {
            let list = try await client(identity).devices()
            emit(.devices(list))
        } catch let e as APIError where e.isUnauthorized {
            emit(.unauthorized)
        } catch {
            Log.warning("Cannot load devices: \(error)", "sync")
        }
    }

    private func scheduleApply(_ h: ItemHeader) {
        guard let entry = store.entry(id: h.id) else { return }
        if settings.paused {
            Log.info("Sync paused; not applying \(h.kind.rawValue) item", "sync")
            return
        }
        if !settings.allows(h.kind) {
            Log.info("Receiving \(h.kind.rawValue) is disabled; item kept in history", "sync")
            return
        }
        if !h.isInline && h.size > settings.autoDownloadLimit {
            Log.info("Item \(h.id) is \(h.size) bytes, above the auto-download limit", "sync")
            emit(.largeItemAvailable(entry))
            return
        }
        Task { await self.applyItem(id: h.id, userInitiated: false) }
    }

    public func applyItem(id: String, userInitiated: Bool) async {
        guard let header = store.header(id: id) else { return }
        do {
            let content = try await materialize(id: id)
            if !userInitiated && header.seq < lastWrittenSeq {
                Log.info("Skipped applying seq \(header.seq): a newer item was applied meanwhile", "sync")
                return
            }
            if !userInitiated {
                lastWrittenSeq = max(lastWrittenSeq, header.seq)
            }
            recent.insert(header.contentHash)
            let ok = await sink.write(content, header.id)
            if ok, let entry = store.entry(id: id) {
                Log.info("Placed \(header.kind.rawValue) item seq \(header.seq) on the clipboard", "sync")
                emit(.applied(entry))
            }
        } catch {
            Log.error("Cannot apply item \(id): \(error)", "sync")
        }
    }

    public func copyToClipboard(id: String) async throws {
        let content = try await materialize(id: id)
        if let h = store.header(id: id) {
            recent.insert(h.contentHash)
        }
        _ = await sink.write(content, id)
    }

    public func content(for id: String) async throws -> ApplyContent {
        try await materialize(id: id)
    }

    public func cachedContent(for id: String) -> ApplyContent? {
        guard let identity, let h = store.header(id: id), let entry = store.entry(id: id), let meta = entry.meta else { return nil }
        if h.isInline, let sealed = store.payloadSealed(id: id), let plain = try? AEAD.open(sealed, key: identity.key, aad: AAD.payload(id)) {
            guard AEAD.sha256Hex(plain) == meta.sha256 else { return nil }
            switch h.kind {
            case .text: return String(data: plain, encoding: .utf8).map { .text($0) }
            case .image: return .image(plain)
            case .files: return completedFiles(id: id).map { .files($0) }
            }
        }
        switch h.kind {
        case .files: return completedFiles(id: id).map { .files($0) }
        case .image:
            let u = receivedDir.appendingPathComponent(id, isDirectory: true).appendingPathComponent("image.png")
            return (try? Data(contentsOf: u)).map { .image($0) }
        case .text: return nil
        }
    }

    private func completedFiles(id: String) -> [URL]? {
        let dir = receivedDir.appendingPathComponent(id, isDirectory: true)
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: dir.path), !names.isEmpty else { return nil }
        let order = store.entry(id: id)?.meta?.files?.map(\.name) ?? []
        let sorted = names.sorted { a, b in
            let ia = order.firstIndex(of: a) ?? Int.max
            let ib = order.firstIndex(of: b) ?? Int.max
            return ia == ib ? a < b : ia < ib
        }
        return sorted.map { dir.appendingPathComponent($0) }
    }

    private func materialize(id: String) async throws -> ApplyContent {
        if let t = contentTasks[id] {
            return try await t.value
        }
        let task = Task { try await self.materializeUncached(id: id) }
        contentTasks[id] = task
        defer { contentTasks[id] = nil }
        do {
            return try await task.value
        } catch let e as APIError where e.isUnauthorized {
            emit(.unauthorized)
            throw e
        } catch let e as APIError where e.status == 404 {
            store.delete(ids: [id])
            publish()
            throw EngineError.notFound
        }
    }

    private func materializeUncached(id: String) async throws -> ApplyContent {
        guard let identity else { throw EngineError.notSignedIn }
        guard let header = store.header(id: id), let entry = store.entry(id: id) else { throw EngineError.notFound }
        guard let meta = entry.meta else { throw EngineError.unsupportedItem }
        let key = identity.key
        let api = client(identity)
        if header.isInline {
            var sealed = store.payloadSealed(id: id)
            if sealed == nil {
                let full = try await withRetries(maxAttempts: 4, label: "Fetch item") { try await api.item(id: id) }
                guard let p = full.payload, let d = Data(strictBase64: p) else { throw EngineError.corrupt("missing payload") }
                store.setPayloadSealed(id: id, d)
                sealed = d
            }
            let plain = try AEAD.open(sealed!, key: key, aad: AAD.payload(id))
            try verify(plain: plain, header: header, meta: meta, key: key)
            switch header.kind {
            case .text:
                guard let s = String(data: plain, encoding: .utf8) else { throw EngineError.corrupt("text is not UTF-8") }
                return .text(s)
            case .image:
                return .image(plain)
            case .files:
                if let done = completedFiles(id: id) { return .files(done) }
                let partial = receivedDir.appendingPathComponent(id + ".partial", isDirectory: true)
                try? FileManager.default.removeItem(at: partial)
                _ = try YCF1.extract(data: plain, into: partial)
                return .files(try finishExtraction(partial: partial, id: id))
            }
        }
        if header.kind == .files, let done = completedFiles(id: id) { return .files(done) }
        let imageURL = receivedDir.appendingPathComponent(id, isDirectory: true).appendingPathComponent("image.png")
        if header.kind == .image, let d = try? Data(contentsOf: imageURL) { return .image(d) }
        let tmp = try await downloadChunks(header: header, meta: meta, api: api, key: key, title: entry.title)
        defer { try? FileManager.default.removeItem(at: tmp) }
        switch header.kind {
        case .text:
            let d = try Data(contentsOf: tmp)
            guard let s = String(data: d, encoding: .utf8) else { throw EngineError.corrupt("text is not UTF-8") }
            return .text(s)
        case .image:
            let d = try Data(contentsOf: tmp)
            let dir = receivedDir.appendingPathComponent(id, isDirectory: true)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try? d.write(to: imageURL, options: .atomic)
            return .image(d)
        case .files:
            let partial = receivedDir.appendingPathComponent(id + ".partial", isDirectory: true)
            try? FileManager.default.removeItem(at: partial)
            _ = try YCF1.extract(archive: tmp, into: partial)
            return .files(try finishExtraction(partial: partial, id: id))
        }
    }

    private func finishExtraction(partial: URL, id: String) throws -> [URL] {
        let final = receivedDir.appendingPathComponent(id, isDirectory: true)
        try? FileManager.default.removeItem(at: final)
        try FileManager.default.moveItem(at: partial, to: final)
        guard let urls = completedFiles(id: id) else { throw EngineError.corrupt("no files extracted") }
        return urls
    }

    private func verify(plain: Data, header: ItemHeader, meta: ItemMeta, key: MasterKey) throws {
        guard Int64(plain.count) == header.size else { throw EngineError.corrupt("size mismatch") }
        guard AEAD.sha256Hex(plain) == meta.sha256 else { throw EngineError.corrupt("sha256 mismatch") }
        guard key.contentHash(plain) == header.contentHash else { throw EngineError.corrupt("content_hash mismatch") }
    }

    private func downloadChunks(header: ItemHeader, meta: ItemMeta, api: APIClient, key: MasterKey, title: String) async throws -> URL {
        let id = header.id
        let count = header.chunkCount
        let plan = ChunkPlan(size: header.size, inlineMax: min(limits.inlineMaxBytes, header.size - 1), chunkSize: limits.chunkSizeBytes)
        guard plan.chunkCount == count else { throw EngineError.corrupt("chunk count mismatch") }
        try? FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        let tmp = tempDir.appendingPathComponent("\(id).part")
        FileManager.default.createFile(atPath: tmp.path, contents: nil, attributes: [.posixPermissions: 0o600])
        let out = try FileHandle(forWritingTo: tmp)
        defer { try? out.close() }
        var progress = TransferProgress(id: id, title: title, direction: .download, completed: 0, total: header.size, finished: false, failure: nil)
        emit(.transfer(progress))
        do {
            let d: ContentDigest = try await withThrowingTaskGroup(of: (Int, Data).self, returning: ContentDigest.self) { group in
                var digester = key.digester()
                var launched = 0
                var next = 0
                var pending: [Int: Data] = [:]
                var done: Int64 = 0
                func launch(_ i: Int) {
                    group.addTask {
                        let sealed = try await withRetries(label: "Download chunk \(i)") {
                            try await api.downloadChunk(id: id, index: i)
                        }
                        let plain = try AEAD.open(sealed, key: key, aad: AAD.chunk(id, index: i, count: count))
                        guard Int64(plain.count) == plan.plaintextSize(of: i) else {
                            throw EngineError.corrupt("chunk \(i) has wrong size")
                        }
                        return (i, plain)
                    }
                }
                while launched < min(3, count) {
                    launch(launched)
                    launched += 1
                }
                while let (i, plain) = try await group.next() {
                    pending[i] = plain
                    while let p = pending.removeValue(forKey: next) {
                        try out.write(contentsOf: p)
                        digester.update(p)
                        next += 1
                        done += Int64(p.count)
                        emit(.transfer(TransferProgress(id: id, title: title, direction: .download, completed: done, total: header.size, finished: false, failure: nil)))
                    }
                    if launched < count {
                        launch(launched)
                        launched += 1
                    }
                }
                return digester.finalize()
            }
            guard d.size == header.size else { throw EngineError.corrupt("size mismatch") }
            guard d.sha256 == meta.sha256 else { throw EngineError.corrupt("sha256 mismatch") }
            guard d.contentHash == header.contentHash else { throw EngineError.corrupt("content_hash mismatch") }
            progress.completed = header.size
            progress.finished = true
            emit(.transfer(progress))
            return tmp
        } catch {
            progress.finished = true
            progress.failure = String(describing: error)
            emit(.transfer(progress))
            try? FileManager.default.removeItem(at: tmp)
            throw error
        }
    }

    public func thumbnail(for id: String) async -> Data? {
        if let t = thumbTasks[id] { return await t.value }
        let task = Task { await self.loadThumbnail(id: id) }
        thumbTasks[id] = task
        let result = await task.value
        thumbTasks[id] = nil
        return result
    }

    private func loadThumbnail(id: String) async -> Data? {
        guard let identity, let entry = store.entry(id: id), entry.kind == .image else { return nil }
        let key = identity.key
        if entry.hasThumb {
            var sealed = store.thumbSealed(id: id)
            if sealed == nil {
                sealed = try? await client(identity).downloadThumb(id: id)
                if let s = sealed { store.setThumbSealed(id: id, s) }
            }
            if let s = sealed, let plain = try? AEAD.open(s, key: key, aad: AAD.thumb(id)) {
                return plain
            }
        }
        if entry.isInline, let s = store.payloadSealed(id: id), let plain = try? AEAD.open(s, key: key, aad: AAD.payload(id)) {
            return plain
        }
        let cached = receivedDir.appendingPathComponent(id, isDirectory: true).appendingPathComponent("image.png")
        return try? Data(contentsOf: cached)
    }

    public func setPinned(id: String, pinned: Bool) async throws {
        guard let identity else { throw EngineError.notSignedIn }
        do {
            let h = try await client(identity).setPinned(id: id, pinned: pinned)
            store.setPinned(id: id, pinned: h.pinned)
            publish()
        } catch let e as APIError where e.isUnauthorized {
            emit(.unauthorized)
            throw e
        } catch let e as APIError where e.status == 404 {
            store.delete(ids: [id])
            publish()
            throw EngineError.notFound
        }
    }

    public func delete(id: String) async throws {
        guard let identity else { throw EngineError.notSignedIn }
        do {
            try await client(identity).deleteItem(id: id)
        } catch let e as APIError where e.isUnauthorized {
            emit(.unauthorized)
            throw e
        }
        store.delete(ids: [id])
        try? FileManager.default.removeItem(at: receivedDir.appendingPathComponent(id, isDirectory: true))
        publish()
    }

    public func submit(_ content: OutgoingContent, force: Bool = false) async -> SubmitOutcome {
        guard let identity else {
            content.cleanup()
            return .notSignedIn
        }
        if !force && settings.paused {
            content.cleanup()
            return .paused
        }
        if !force && !settings.allows(content.kind) {
            content.cleanup()
            return .disabled
        }
        let key = identity.key
        let body = content.body
        let digest: ContentDigest
        do {
            digest = try await Task.detached(priority: .userInitiated) {
                try OutgoingContent.digest(body, key: key)
            }.value
        } catch {
            content.cleanup()
            Log.error("Cannot read clipboard content: \(error)", "upload")
            return .failed(String(describing: error))
        }
        guard digest.size > 0 else {
            content.cleanup()
            return .failed("empty content")
        }
        if !force {
            var normalized: String?
            if let t = content.text {
                let n = EchoGuard.normalizedCRLF(t)
                if n != t { normalized = key.contentHash(Data(n.utf8)) }
            }
            let decision = EchoGuard.decide(
                contentHash: digest.contentHash,
                normalizedTextHash: normalized,
                recent: recent,
                newestCachedHash: store.newestContentHash()
            )
            switch decision {
            case .skipRecent:
                Log.debug("Clipboard change matches a recent item; not sending", "upload")
                content.cleanup()
                return .skippedEcho
            case .skipNewest:
                Log.debug("Clipboard change equals the newest item; not sending", "upload")
                content.cleanup()
                return .skippedNewest
            case .upload:
                break
            }
        }
        recent.insert(digest.contentHash)
        uploadQueue.append(UploadJob(content: content, digest: digest))
        while uploadQueue.count > 20 {
            let dropped = uploadQueue.removeFirst()
            dropped.content.cleanup()
            Log.warning("Upload queue full; dropped an older item", "upload")
        }
        if uploadWorker == nil {
            uploadWorker = Task { await self.runUploads() }
        }
        return .queued
    }

    private func runUploads() async {
        while !uploadQueue.isEmpty && !Task.isCancelled {
            let job = uploadQueue.removeFirst()
            await upload(job)
            job.content.cleanup()
        }
        uploadWorker = nil
    }

    private func upload(_ job: UploadJob) async {
        var id = UUIDv7.generate()
        var attempt = 0
        var conflictRetried = false
        var keyCheckRetried = false
        while !Task.isCancelled {
            guard let identity else { return }
            let api = client(identity)
            do {
                let result = try await performUpload(job, id: id, api: api, key: identity.key)
                insert(result.header, key: identity.key, payloadSealed: result.payloadSealed, meta: result.meta)
                if let t = result.thumbSealed { store.setThumbSealed(id: id, t) }
                publish()
                if let e = store.entry(id: id) { emit(.uploaded(e)) }
                Log.info("Sent \(job.content.kind.rawValue) item (\(job.digest.size) bytes) as seq \(result.header.seq)", "upload")
                return
            } catch let e as APIError where e.isUnauthorized {
                emit(.unauthorized)
                return
            } catch let e as APIError where e.code == "id_conflict" && !conflictRetried {
                conflictRetried = true
                id = UUIDv7.generate()
            } catch let e as APIError where e.code == "key_check_missing" && !keyCheckRetried {
                keyCheckRetried = true
                do {
                    try await ensureKeyCheck(api, key: identity.key)
                } catch {
                    emit(.uploadFailed("The encryption key could not be verified."))
                    return
                }
            } catch let e as APIError where e.isRetryable && attempt < 10 {
                let d = Backoff.http.delay(attempt: attempt)
                attempt += 1
                Log.warning("Upload failed (\(e)); retry \(attempt) in \(String(format: "%.1f", d)) s", "upload")
                try? await Task.sleep(nanoseconds: UInt64(d * 1_000_000_000))
            } catch let e as APIError {
                Log.error("Upload failed: \(e)", "upload")
                emit(.uploadFailed(e.userMessage))
                emit(.transfer(TransferProgress(id: id, title: job.content.title, direction: .upload, completed: 0, total: job.digest.size, finished: true, failure: e.userMessage)))
                if e.code == "item_too_large" || e.code == "disk_low" {
                    try? await api.deleteItem(id: id)
                }
                return
            } catch is CancellationError {
                return
            } catch {
                Log.error("Upload failed: \(error)", "upload")
                emit(.uploadFailed(String(describing: error)))
                return
            }
        }
    }

    struct UploadResult: Sendable {
        var header: ItemHeader
        var meta: ItemMeta
        var payloadSealed: Data?
        var thumbSealed: Data?
    }

    private func performUpload(_ job: UploadJob, id: String, api: APIClient, key: MasterKey) async throws -> UploadResult {
        let c = job.content
        let digest = job.digest
        let plan = ChunkPlan(size: digest.size, inlineMax: limits.inlineMaxBytes, chunkSize: limits.chunkSizeBytes)
        let meta = ItemMeta(mime: c.mime, preview: c.preview, sha256: digest.sha256, image: c.image, files: c.files, sourceApp: c.sourceApp)
        let metaSealed = try AEAD.seal(try meta.encoded(), key: key, aad: AAD.meta(id))
        guard metaSealed.count <= limits.metaMaxBytes else { throw EngineError.corrupt("meta too large") }
        var thumbSealed: Data?
        if c.kind == .image, let thumb = c.thumbnail {
            let sealed = try AEAD.seal(thumb, key: key, aad: AAD.thumb(id))
            if sealed.count <= limits.thumbMaxBytes {
                try await withRetries(label: "Upload thumbnail") { try await api.uploadThumb(id: id, sealed: sealed) }
                thumbSealed = sealed
            }
        }
        if plan.isInline {
            let data = try OutgoingContent.read(c.body, range: 0..<digest.size)
            let payloadSealed = try AEAD.seal(data, key: key, aad: AAD.payload(id))
            let req = CreateItemRequest(
                id: id, kind: c.kind, size: digest.size, chunkCount: 0, contentHash: digest.contentHash,
                meta: metaSealed.base64EncodedString(), payload: payloadSealed.base64EncodedString()
            )
            let header = try await withRetries(label: "Create item") { try await api.createItem(req) }
            return UploadResult(header: header, meta: meta, payloadSealed: payloadSealed, thumbSealed: thumbSealed)
        }
        let count = plan.chunkCount
        uploadProgress[id] = TransferProgress(id: id, title: c.title, direction: .upload, completed: 0, total: digest.size, finished: false, failure: nil)
        emit(.transfer(uploadProgress[id]!))
        let body = c.body
        func uploadChunks(_ indices: [Int]) async throws {
            try await withThrowingTaskGroup(of: Int64.self) { group in
                var iterator = indices.makeIterator()
                func launch(_ i: Int) {
                    group.addTask {
                        let plain = try OutgoingContent.read(body, range: plan.range(of: i))
                        let sealed = try AEAD.seal(plain, key: key, aad: AAD.chunk(id, index: i, count: count))
                        do {
                            try await withRetries(label: "Upload chunk \(i)") {
                                try await api.uploadChunk(id: id, index: i, sealed: sealed)
                            }
                        } catch let e as APIError where e.code == "already_committed" {
                            return Int64(plain.count)
                        }
                        return Int64(plain.count)
                    }
                }
                for _ in 0..<3 {
                    if let i = iterator.next() { launch(i) }
                }
                while let done = try await group.next() {
                    bumpUpload(id: id, by: done)
                    if let i = iterator.next() { launch(i) }
                }
            }
        }
        do {
            try await uploadChunks(Array(0..<count))
            let commit = CommitRequest(kind: c.kind, size: digest.size, chunkCount: count, contentHash: digest.contentHash, meta: metaSealed.base64EncodedString())
            var header: ItemHeader?
            var rounds = 0
            while header == nil {
                do {
                    header = try await withRetries(label: "Commit") { try await api.commit(id: id, body: commit) }
                } catch let e as APIError where e.code == "missing_chunks" && rounds < 3 {
                    rounds += 1
                    let missing = e.detailsMissing
                    Log.warning("Server is missing chunks \(missing); uploading again", "upload")
                    try await uploadChunks(missing.isEmpty ? Array(0..<count) : missing)
                }
            }
            finishUpload(id: id, failure: nil)
            return UploadResult(header: header!, meta: meta, payloadSealed: nil, thumbSealed: thumbSealed)
        } catch {
            finishUpload(id: id, failure: (error as? APIError)?.userMessage ?? String(describing: error))
            throw error
        }
    }

    private func bumpUpload(id: String, by n: Int64) {
        guard var p = uploadProgress[id] else { return }
        p.completed = min(p.total, p.completed + n)
        uploadProgress[id] = p
        emit(.transfer(p))
    }

    private func finishUpload(id: String, failure: String?) {
        guard var p = uploadProgress.removeValue(forKey: id) else { return }
        if failure == nil { p.completed = p.total }
        p.finished = true
        p.failure = failure
        emit(.transfer(p))
    }
}

extension APIError {
    var detailsMissing: [Int] {
        if case .http(_, _, _, let details, _) = self {
            return details?["missing"]?.intArray ?? []
        }
        return []
    }
}

extension HistoryEntry {
    public var title: String {
        switch kind {
        case .text:
            let line = preview.split(whereSeparator: \.isNewline).first.map(String.init) ?? preview
            let t = line.trimmingCharacters(in: .whitespaces)
            return t.isEmpty ? "Text" : String(t.prefix(80))
        case .image:
            if let d = meta?.image { return "Image \(d.width) x \(d.height)" }
            return "Image"
        case .files:
            let files = meta?.files ?? []
            if files.count == 1 { return files[0].name }
            if files.isEmpty { return "Files" }
            return "\(files.count) files"
        }
    }
}
