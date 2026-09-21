import AppKit
import Observation
import ServiceManagement
import UniformTypeIdentifiers
import ClipCore

enum AccountState: Equatable {
    case signedOut
    case locked(username: String)
    case signedIn(username: String)

    var isSignedIn: Bool {
        if case .signedIn = self { return true }
        return false
    }

    var hasToken: Bool {
        if case .signedOut = self { return false }
        return true
    }
}

enum AppError: LocalizedError {
    case message(String)

    var errorDescription: String? {
        switch self {
        case .message(let m): return m
        }
    }
}

@MainActor
@Observable
final class AppModel {
    static let shared = AppModel()

    let settings = AppSettings()
    let paths = AppPaths.standard()
    let transport = URLSessionTransport()
    @ObservationIgnored let monitor = PasteboardMonitor()
    @ObservationIgnored let writer = PasteboardWriter()
    @ObservationIgnored let systemEvents = SystemEvents()
    @ObservationIgnored let activity = ActivityGuard()
    @ObservationIgnored let notifications = NotificationBridge()
    @ObservationIgnored private(set) var engine: SyncEngine!
    @ObservationIgnored private(set) var connection: ConnectionManager!
    @ObservationIgnored private var started = false
    @ObservationIgnored let thumbnails = ThumbnailCache()

    var status: ConnectionStatus = .idle
    var syncing = false
    var account: AccountState = .signedOut
    var items: [HistoryEntry] = []
    var devices: [String: DeviceInfo] = [:]
    var deviceList: [DeviceInfo] = []
    var online: [OnlineDevice] = []
    var transfers: [TransferProgress] = []
    var storageWarning: StorageWarningMessage?
    var notice: String?
    var lastActivity: Date?
    var historyOpenCount = 0
    var launchAtLogin = false
    var accessibilityTrusted = false

    var appVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
    }

    var ownDeviceId: String? { settings.deviceId }

    func start() {
        guard !started else { return }
        started = true
        Log.shared.enableFileLogging()
        Log.info("Yikz Clipboard \(appVersion) starting on macOS \(ProcessInfo.processInfo.operatingSystemVersionString)", "app")
        paths.prepare()
        let paths = self.paths
        Task.detached(priority: .background) {
            while !Task.isCancelled {
                paths.pruneReceived()
                try? await Task.sleep(for: .seconds(6 * 3600))
            }
        }

        let store: HistoryStore
        do {
            store = try HistoryStore(path: paths.historyDB.path)
        } catch {
            Log.error("History cache unusable (\(error)); recreating", "store")
            for suffix in ["", "-wal", "-shm"] {
                try? FileManager.default.removeItem(atPath: paths.historyDB.path + suffix)
            }
            store = (try? HistoryStore(path: paths.historyDB.path)) ?? (try! HistoryStore(path: ":memory:"))
        }
        let writer = self.writer
        let sink = ClipboardSink { content, id in
            await MainActor.run { writer.write(content, itemId: id) }
        }
        engine = SyncEngine(
            store: store,
            stateStore: SyncStateStore(url: paths.syncState),
            transport: transport,
            sink: sink,
            receivedDir: paths.received,
            tempDir: paths.temporary
        )
        connection = ConnectionManager(engine: engine)
        writer.monitor = monitor

        let engineEvents = engine.events
        Task { @MainActor [weak self] in
            for await e in engineEvents { self?.handle(e) }
        }
        let statusUpdates = connection.statusUpdates
        Task { @MainActor [weak self] in
            for await s in statusUpdates { self?.handleStatus(s) }
        }

        monitor.onCapture = { [weak self] captured in self?.submit(captured, force: false) }
        monitor.start()

        systemEvents.onWake = { [weak self] in
            guard let self else { return }
            Task { await self.connection.kick(reason: "wake") }
        }
        systemEvents.onSleep = { [weak self] in
            guard let self else { return }
            Task { await self.connection.suspend(.sleeping, reason: "system sleep") }
        }
        systemEvents.onProbe = { [weak self] reason in
            guard let self else { return }
            Task { await self.connection.probe(reason: reason) }
        }
        systemEvents.onNetworkChange = { [weak self] available in
            guard let self else { return }
            Task {
                if available {
                    await self.connection.kick(reason: "network changed")
                } else {
                    await self.connection.suspend(.offline, reason: "network unavailable")
                }
            }
        }
        systemEvents.start()

        notifications.onDownload = { id in
            Task { @MainActor in AppModel.shared.downloadAndApply(id: id) }
        }
        notifications.setUp()

        HotKeyCenter.shared.action = { WindowManager.shared.toggleHistory() }
        HotKeyCenter.shared.register(settings.historyShortcut)

        refreshSystemState()
        Task { await engine.updateSettings(settings.engineSettings) }
        restoreSession()
    }

    func refreshSystemState() {
        launchAtLogin = SMAppService.mainApp.status == .enabled
        accessibilityTrusted = Accessibility.isTrusted
    }

    private func restoreSession() {
        guard let creds = Keychain.loadCredentials() else {
            account = .signedOut
            Task { await engine.configure(identity: nil) }
            return
        }
        if creds.key == nil {
            account = .locked(username: settings.username)
            Task { await engine.configure(identity: nil) }
            return
        }
        startSync(credentials: creds)
    }

    private func startSync(credentials: StoredCredentials) {
        guard let keyData = credentials.key, let key = MasterKey(data: keyData),
              let url = APIClient.normalizedServerURL(settings.serverURL),
              let deviceId = settings.deviceId,
              let salt = settings.saltB64.flatMap({ Data(strictBase64: $0) }) else {
            Log.error("Stored session is incomplete; please sign in again", "account")
            account = .signedOut
            return
        }
        account = .signedIn(username: settings.username)
        let identity = EngineIdentity(serverURL: url, deviceId: deviceId, token: credentials.token, key: key, salt: salt)
        let config = ConnectionConfig(serverURL: url, token: credentials.token, deviceId: deviceId, appVersion: appVersion)
        let engine = self.engine!
        let connection = self.connection!
        let es = settings.engineSettings
        Task {
            await engine.updateSettings(es)
            await engine.configure(identity: identity)
            await connection.start(config)
        }
        Log.info("Signed in as \(settings.username) on \(url.host ?? url.absoluteString)", "account")
    }

    func pushSettings() {
        let es = settings.engineSettings
        Task { await engine.updateSettings(es) }
    }

    private func handleStatus(_ s: ConnectionStatus) {
        status = s
        switch s {
        case .connecting, .connected, .waiting:
            activity.hold(true)
        case .idle, .offline, .sleeping, .stopped:
            activity.hold(false)
        }
        if case .stopped(let reason) = s {
            switch reason {
            case .unauthorized:
                handleUnauthorized()
            case .protocolUnsupported:
                notice = "The server needs a newer version of this app."
            case .replaced:
                notice = "Too many connections from this Mac. Click Reconnect."
            case .keyRejected:
                break
            }
        }
    }

    private func handle(_ e: EngineEvent) {
        switch e {
        case .history(let list):
            items = list
        case .devices(let list):
            deviceList = list
            devices = Dictionary(list.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        case .online(let list):
            online = list
        case .transfer(let t):
            if let i = transfers.firstIndex(where: { $0.id == t.id }) {
                transfers[i] = t
            } else if !t.finished {
                transfers.append(t)
            }
            if t.finished {
                let id = t.id
                Task { @MainActor in
                    try? await Task.sleep(for: .seconds(t.failure == nil ? 1.5 : 6))
                    self.transfers.removeAll { $0.id == id && $0.finished }
                }
            }
        case .storageWarning(let w):
            storageWarning = w.active ? w : nil
        case .largeItemAvailable(let entry):
            let device = deviceName(for: entry.deviceId)
            notifications.postLargeItem(id: entry.id, title: entry.title, size: Format.bytes(entry.size), device: device)
            notice = "\(entry.title) (\(Format.bytes(entry.size))) from \(device) is ready to download."
        case .applied:
            lastActivity = Date()
        case .uploaded:
            lastActivity = Date()
        case .uploadFailed(let m):
            notice = "Sending failed: \(m)"
        case .unauthorized:
            handleUnauthorized()
        case .keyRejected:
            handleKeyRejected()
        case .protocolUnsupported:
            notice = "The server needs a newer version of this app."
            Task { await connection.stop() }
        case .syncing(let on):
            syncing = on
        }
    }

    private func handleUnauthorized() {
        guard account.hasToken else { return }
        Log.error("The server rejected this device's token; signing out", "account")
        notice = "This Mac was signed out by the server. Sign in again."
        Task { await localSignOut() }
    }

    private func handleKeyRejected() {
        guard case .signedIn(let user) = account else { return }
        Log.error("Encryption key rejected; asking for the encryption password", "account")
        if var creds = Keychain.loadCredentials() {
            creds.key = nil
            try? Keychain.saveCredentials(creds)
        }
        account = .locked(username: user)
        notice = "Enter your encryption password again."
        Task {
            await connection.stop()
            await engine.configure(identity: nil)
        }
    }

    func deviceName(for id: String) -> String {
        if id == settings.deviceId { return "This Mac" }
        if let d = devices[id] { return d.name }
        if let o = online.first(where: { $0.deviceId == id }) { return o.name }
        return "Unknown device"
    }

    func platform(for id: String) -> String? {
        devices[id]?.platform ?? online.first(where: { $0.deviceId == id })?.platform
    }

    var otherOnlineDevices: [OnlineDevice] {
        online.filter { $0.deviceId != settings.deviceId }
    }

    func submit(_ captured: CapturedClipboard, force: Bool) {
        guard account.isSignedIn else { return }
        if settings.paused && !force { return }
        let outgoing = paths.outgoing
        let engine = self.engine!
        Task.detached(priority: .userInitiated) {
            do {
                guard let content = try ContentBuilder.build(captured, outgoingDir: outgoing) else { return }
                let outcome = await engine.submit(content, force: force)
                if outcome == .queued {
                    Log.debug("Queued \(content.kind.rawValue) for sending", "clipboard")
                }
            } catch {
                Log.error("Cannot read the clipboard: \(error)", "clipboard")
            }
        }
    }

    func sendClipboardNow() {
        guard account.isSignedIn else {
            notice = "Sign in to send the clipboard."
            return
        }
        guard let captured = monitor.capture(force: true) else {
            notice = "The clipboard is empty or holds private content."
            return
        }
        Log.info("Sending the current clipboard on request", "clipboard")
        submit(captured, force: true)
    }

    func togglePause() {
        settings.paused.toggle()
        Log.info(settings.paused ? "Sync paused" : "Sync resumed", "app")
        pushSettings()
    }

    func reconnect() {
        notice = nil
        Task { await connection.kick(reason: "user request") }
    }

    func copy(_ entry: HistoryEntry) async -> Bool {
        do {
            try await engine.copyToClipboard(id: entry.id)
            return true
        } catch {
            Log.error("Copy failed: \(error)", "history")
            notice = "Copy failed: \(error)"
            return false
        }
    }

    func downloadAndApply(id: String) {
        Task { await engine.applyItem(id: id, userInitiated: true) }
    }

    func togglePin(_ entry: HistoryEntry) async {
        do {
            try await engine.setPinned(id: entry.id, pinned: !entry.pinned)
        } catch {
            notice = (error as? APIError)?.userMessage ?? "\(error)"
        }
    }

    func delete(_ entry: HistoryEntry) async {
        do {
            try await engine.delete(id: entry.id)
            thumbnails.remove(entry.id)
        } catch {
            notice = (error as? APIError)?.userMessage ?? "\(error)"
        }
    }

    func content(for entry: HistoryEntry) async throws -> ApplyContent {
        try await engine.content(for: entry.id)
    }

    func cachedContent(for entry: HistoryEntry) async -> ApplyContent? {
        await engine.cachedContent(for: entry.id)
    }

    func thumbnail(for entry: HistoryEntry) async -> NSImage? {
        if let img = thumbnails.get(entry.id) { return img }
        guard let data = await engine.thumbnail(for: entry.id) else { return nil }
        let image = await Task.detached { ThumbnailCache.downsample(data, maxSide: 96) }.value
        if let image { thumbnails.set(entry.id, image) }
        return image
    }

    func saveAs(_ entry: HistoryEntry) async {
        do {
            let content = try await engine.content(for: entry.id)
            NSApp.activate()
            switch content {
            case .text(let s):
                let panel = NSSavePanel()
                panel.nameFieldStringValue = "Clipboard.txt"
                panel.allowedContentTypes = [.plainText]
                if panel.runModal() == .OK, let url = panel.url {
                    try s.write(to: url, atomically: true, encoding: .utf8)
                }
            case .image(let png):
                let panel = NSSavePanel()
                panel.nameFieldStringValue = "Image.png"
                panel.allowedContentTypes = [.png]
                if panel.runModal() == .OK, let url = panel.url {
                    try png.write(to: url, options: .atomic)
                }
            case .files(let urls):
                if urls.count == 1 {
                    let panel = NSSavePanel()
                    panel.nameFieldStringValue = urls[0].lastPathComponent
                    if panel.runModal() == .OK, let url = panel.url {
                        try? FileManager.default.removeItem(at: url)
                        try FileManager.default.copyItem(at: urls[0], to: url)
                    }
                } else {
                    let panel = NSOpenPanel()
                    panel.canChooseDirectories = true
                    panel.canChooseFiles = false
                    panel.canCreateDirectories = true
                    panel.prompt = "Save Here"
                    panel.message = "Choose a folder for \(urls.count) files"
                    if panel.runModal() == .OK, let dir = panel.url {
                        for u in urls {
                            var dest = dir.appendingPathComponent(u.lastPathComponent)
                            var n = 2
                            while FileManager.default.fileExists(atPath: dest.path) {
                                let stem = u.deletingPathExtension().lastPathComponent
                                let ext = u.pathExtension
                                dest = dir.appendingPathComponent(ext.isEmpty ? "\(stem) (\(n))" : "\(stem) (\(n)).\(ext)")
                                n += 1
                            }
                            try FileManager.default.copyItem(at: u, to: dest)
                        }
                    }
                }
            }
        } catch {
            Log.error("Save failed: \(error)", "history")
            notice = "Save failed: \(error.localizedDescription)"
        }
    }

    func signIn(server: String, username: String, password: String, encryptionPassword: String, deviceName: String) async throws {
        guard let url = APIClient.normalizedServerURL(server) else {
            throw AppError.message("Enter a valid server address.")
        }
        let name = deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !username.isEmpty, !password.isEmpty else { throw AppError.message("Enter your username and password.") }
        guard !encryptionPassword.isEmpty else { throw AppError.message("Enter your encryption password.") }
        guard !name.isEmpty, name.unicodeScalars.count <= 64 else { throw AppError.message("Device name must be 1 to 64 characters.") }
        let api = APIClient(baseURL: url, token: nil, transport: transport)
        let resp: LoginResponse
        do {
            resp = try await api.login(LoginRequest(username: username, password: password, deviceName: name, deviceId: settings.deviceId))
        } catch let e as APIError {
            throw AppError.message(e.userMessage)
        }
        guard resp.protocolVersion == Proto.version else {
            throw AppError.message("The server uses protocol \(resp.protocolVersion). Update the app.")
        }
        guard resp.kdf.isSupported else { throw AppError.message("The server uses an unsupported key derivation.") }
        guard let salt = Data(strictBase64: resp.salt), salt.count == Proto.saltLength else {
            throw AppError.message("The server sent an invalid salt.")
        }
        await connection.stop()
        if let previous = settings.lastServerId, previous != resp.serverId {
            Log.warning("Signed in to a different server; clearing local history", "account")
            await engine.clearLocalData()
        }
        settings.serverURL = url.absoluteString
        settings.username = resp.username
        settings.deviceName = name
        settings.deviceId = resp.deviceId
        settings.saltB64 = resp.salt
        settings.lastServerId = resp.serverId
        try Keychain.saveCredentials(StoredCredentials(token: resp.token, key: nil))
        account = .locked(username: resp.username)
        Log.info("Logged in as \(resp.username); device id \(resp.deviceId)", "account")
        try await unlock(encryptionPassword: encryptionPassword, knownKeyCheck: .some(resp.keyCheck), salt: salt, iterations: resp.kdf.iterations)
    }

    func unlock(encryptionPassword: String) async throws {
        guard let creds = Keychain.loadCredentials(), let url = APIClient.normalizedServerURL(settings.serverURL) else {
            throw AppError.message("Sign in again.")
        }
        let api = APIClient(baseURL: url, token: creds.token, transport: transport)
        let me: MeResponse
        do {
            me = try await api.me()
        } catch let e as APIError {
            if e.isUnauthorized { await localSignOut() }
            throw AppError.message(e.userMessage)
        }
        guard let salt = Data(strictBase64: me.salt), salt.count == Proto.saltLength else {
            throw AppError.message("The server sent an invalid salt.")
        }
        if settings.lastServerId != me.serverId {
            await engine.clearLocalData()
            settings.lastServerId = me.serverId
        }
        settings.saltB64 = me.salt
        try await unlock(encryptionPassword: encryptionPassword, knownKeyCheck: .some(me.keyCheck), salt: salt, iterations: me.kdf.iterations)
    }

    private func unlock(encryptionPassword: String, knownKeyCheck: String??, salt: Data, iterations: Int) async throws {
        guard var creds = Keychain.loadCredentials(), let url = APIClient.normalizedServerURL(settings.serverURL) else {
            throw AppError.message("Sign in again.")
        }
        let key = try await Task.detached(priority: .userInitiated) {
            try MasterKey.derive(password: encryptionPassword, salt: salt, iterations: UInt32(iterations))
        }.value
        let api = APIClient(baseURL: url, token: creds.token, transport: transport)
        var serverCheck: String?
        if let known = knownKeyCheck {
            serverCheck = known
        } else {
            serverCheck = try await api.me().keyCheck
        }
        if serverCheck == nil {
            do {
                try await api.putKeyCheck(key.keyCheck)
                Log.info("Stored the key check on the server", "account")
                serverCheck = key.keyCheck
            } catch let e as APIError where e.code == "key_check_exists" {
                serverCheck = try await api.me().keyCheck
            } catch let e as APIError {
                throw AppError.message(e.userMessage)
            }
        }
        guard serverCheck == key.keyCheck else {
            Log.warning("Encryption password does not match the key check", "account")
            throw AppError.message("Wrong encryption password. It must match your other devices.")
        }
        creds.key = key.data
        try Keychain.saveCredentials(creds)
        notice = nil
        startSync(credentials: creds)
    }

    func signOut() async {
        if let creds = Keychain.loadCredentials(), let url = APIClient.normalizedServerURL(settings.serverURL) {
            let api = APIClient(baseURL: url, token: creds.token, transport: transport)
            do {
                try await api.logout()
            } catch {
                Log.warning("Logout request failed: \(error)", "account")
            }
        }
        await localSignOut()
        notice = nil
        Log.info("Signed out", "account")
    }

    private func localSignOut() async {
        await connection.stop()
        Keychain.deleteCredentials()
        account = .signedOut
        await engine.configure(identity: nil)
        await engine.clearLocalData()
        thumbnails.removeAll()
        transfers = []
        storageWarning = nil
    }

    func renameDevice(_ name: String) async throws {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.unicodeScalars.count <= 64 else { throw AppError.message("Device name must be 1 to 64 characters.") }
        guard let creds = Keychain.loadCredentials(), let url = APIClient.normalizedServerURL(settings.serverURL), let id = settings.deviceId else { return }
        let api = APIClient(baseURL: url, token: creds.token, transport: transport)
        do {
            _ = try await api.renameDevice(id: id, name: trimmed)
            settings.deviceName = trimmed
            await engine.refreshDevices()
        } catch let e as APIError {
            throw AppError.message(e.userMessage)
        }
    }

    func storageInfo() async -> StorageInfo? {
        guard let creds = Keychain.loadCredentials(), let url = APIClient.normalizedServerURL(settings.serverURL) else { return nil }
        return try? await APIClient(baseURL: url, token: creds.token, transport: transport).storage()
    }

    func setLaunchAtLogin(_ on: Bool) {
        do {
            if on {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            Log.info("Launch at login \(on ? "enabled" : "disabled")", "app")
        } catch {
            Log.error("Launch at login change failed: \(error.localizedDescription)", "app")
            notice = "Launch at login could not be changed: \(error.localizedDescription)"
        }
        refreshSystemState()
    }

    func updateShortcut(_ s: Shortcut) {
        settings.historyShortcut = s
        if !HotKeyCenter.shared.register(s) {
            notice = "\(s.display) is already in use. Choose another shortcut."
        }
    }
}

@MainActor
final class ThumbnailCache {
    private let cache = NSCache<NSString, NSImage>()

    init() {
        cache.countLimit = 300
    }

    func get(_ id: String) -> NSImage? { cache.object(forKey: id as NSString) }
    func set(_ id: String, _ image: NSImage) { cache.setObject(image, forKey: id as NSString) }
    func remove(_ id: String) { cache.removeObject(forKey: id as NSString) }
    func removeAll() { cache.removeAllObjects() }

    nonisolated static func downsample(_ data: Data, maxSide: Int) -> NSImage? {
        guard let src = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        let opts: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: maxSide * 2,
            kCGImageSourceCreateThumbnailWithTransform: true
        ]
        guard let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: cg.width / 2, height: cg.height / 2))
    }
}
