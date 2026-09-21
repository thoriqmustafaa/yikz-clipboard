import Foundation
import os

public protocol WebSocketChannel: AnyObject, Sendable {
    func open() async throws
    func send(_ text: String) async throws
    func receive() async throws -> String
    func close(code: Int)
    var closeCode: Int? { get }
    var upgradeStatus: Int? { get }
}

public protocol WebSocketConnecting: Sendable {
    func makeChannel(url: URL, token: String) -> any WebSocketChannel
}

public struct URLSessionWebSocketConnector: WebSocketConnecting {
    public init() {}

    public func makeChannel(url: URL, token: String) -> any WebSocketChannel {
        URLSessionWebSocketChannel(url: url, token: token)
    }
}

public enum ChannelError: Error, Sendable, CustomStringConvertible {
    case closed(Int?)
    case failed(String)
    case timeout

    public var description: String {
        switch self {
        case .closed(let c): return c.map { "closed with code \($0)" } ?? "closed"
        case .failed(let m): return m
        case .timeout: return "timed out"
        }
    }
}

public final class URLSessionWebSocketChannel: NSObject, WebSocketChannel, URLSessionWebSocketDelegate, @unchecked Sendable {
    private struct State {
        var openContinuation: CheckedContinuation<Void, Error>?
        var opened = false
        var failure: String?
        var closeCode: Int?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private var session: URLSession!
    private var task: URLSessionWebSocketTask!

    public init(url: URL, token: String) {
        super.init()
        let cfg = URLSessionConfiguration.ephemeral
        cfg.waitsForConnectivity = false
        cfg.timeoutIntervalForRequest = 90
        cfg.timeoutIntervalForResource = 7 * 86400
        cfg.urlCache = nil
        session = URLSession(configuration: cfg, delegate: self, delegateQueue: nil)
        var req = URLRequest(url: url)
        req.timeoutInterval = 15
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        task = session.webSocketTask(with: req)
        task.maximumMessageSize = 4 * 1024 * 1024
    }

    public func open() async throws {
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            let immediate: Result<Void, Error>? = state.withLock { s in
                if s.opened { return .success(()) }
                if let f = s.failure { return .failure(ChannelError.failed(f)) }
                s.openContinuation = c
                return nil
            }
            if let immediate {
                c.resume(with: immediate)
            } else {
                task.resume()
            }
        }
    }

    public func send(_ text: String) async throws {
        try await task.send(.string(text))
    }

    public func receive() async throws -> String {
        let msg = try await task.receive()
        switch msg {
        case .string(let s): return s
        case .data(let d): return String(decoding: d, as: UTF8.self)
        @unknown default: return ""
        }
    }

    public func close(code: Int) {
        let c = URLSessionWebSocketTask.CloseCode(rawValue: code) ?? .goingAway
        task.cancel(with: c, reason: nil)
        failOpen("closed locally")
        session.finishTasksAndInvalidate()
    }

    public var closeCode: Int? {
        if let c = state.withLock({ $0.closeCode }) { return c }
        let raw = task.closeCode.rawValue
        return raw == 0 ? nil : raw
    }

    public var upgradeStatus: Int? {
        (task.response as? HTTPURLResponse)?.statusCode
    }

    private func failOpen(_ message: String) {
        let c: CheckedContinuation<Void, Error>? = state.withLock { s in
            if s.failure == nil { s.failure = message }
            let c = s.openContinuation
            s.openContinuation = nil
            return c
        }
        c?.resume(throwing: ChannelError.failed(message))
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        let c: CheckedContinuation<Void, Error>? = state.withLock { s in
            s.opened = true
            let c = s.openContinuation
            s.openContinuation = nil
            return c
        }
        c?.resume()
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        state.withLock { $0.closeCode = closeCode.rawValue }
        failOpen("closed with code \(closeCode.rawValue)")
    }

    public func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        failOpen(error?.localizedDescription ?? "connection ended")
        session.finishTasksAndInvalidate()
    }
}

public enum StopReason: String, Sendable, Equatable {
    case unauthorized
    case protocolUnsupported
    case replaced
    case keyRejected
}

public enum ConnectionStatus: Sendable, Equatable {
    case idle
    case connecting(attempt: Int)
    case connected(since: Date)
    case waiting(retryAt: Date, attempt: Int, reason: String)
    case offline
    case sleeping
    case stopped(StopReason)

    public var isConnected: Bool {
        if case .connected = self { return true }
        return false
    }
}

public struct ConnectionConfig: Sendable, Equatable {
    public var serverURL: URL
    public var token: String
    public var deviceId: String
    public var appVersion: String

    public init(serverURL: URL, token: String, deviceId: String, appVersion: String) {
        self.serverURL = serverURL
        self.token = token
        self.deviceId = deviceId
        self.appVersion = appVersion
    }
}

public actor ConnectionManager {
    public nonisolated let statusUpdates: AsyncStream<ConnectionStatus>
    private let statusContinuation: AsyncStream<ConnectionStatus>.Continuation

    private let engine: SyncEngine
    private let connector: any WebSocketConnecting
    private let backoff: Backoff
    private var config: ConnectionConfig?
    private var status: ConnectionStatus = .idle
    private var attempt = 0
    private var generation = 0
    private var loopTask: Task<Void, Never>?
    private var channel: (any WebSocketChannel)?
    private var lastReceived = ContinuousClock.now
    private var welcomed = false
    private var probeTask: Task<Void, Never>?

    public init(engine: SyncEngine, connector: any WebSocketConnecting = URLSessionWebSocketConnector(), backoff: Backoff = .reconnect) {
        self.engine = engine
        self.connector = connector
        self.backoff = backoff
        var cont: AsyncStream<ConnectionStatus>.Continuation!
        statusUpdates = AsyncStream(bufferingPolicy: .bufferingNewest(16)) { cont = $0 }
        statusContinuation = cont
    }

    private func setStatus(_ s: ConnectionStatus) {
        guard s != status else { return }
        status = s
        statusContinuation.yield(s)
    }

    public func currentStatus() -> ConnectionStatus { status }

    public func start(_ config: ConnectionConfig) async {
        self.config = config
        await restart(reason: "start")
    }

    public func stop() async {
        config = nil
        tearDown()
        setStatus(.idle)
        await engine.connectionDropped()
    }

    public func kick(reason: String) async {
        guard config != nil else { return }
        if case .stopped(let r) = status, r != .replaced { return }
        Log.info("Reconnecting now (\(reason))", "ws")
        await restart(reason: reason)
    }

    public func probe(reason: String) async {
        guard config != nil else { return }
        switch status {
        case .connected:
            guard let ch = channel else { return }
            let sentAt = ContinuousClock.now
            let gen = generation
            probeTask?.cancel()
            probeTask = Task {
                try? await ch.send(ClientMessage.ping(Int64(Date().timeIntervalSince1970 * 1000)).text())
                try? await Task.sleep(for: .seconds(5))
                guard !Task.isCancelled else { return }
                await self.finishProbe(gen: gen, sentAt: sentAt, reason: reason)
            }
        case .stopped(let r) where r != .replaced:
            return
        case .idle, .connecting:
            return
        default:
            await kick(reason: reason)
        }
    }

    private func finishProbe(gen: Int, sentAt: ContinuousClock.Instant, reason: String) async {
        guard gen == generation, status.isConnected else { return }
        if lastReceived < sentAt {
            Log.warning("No reply within 5 s after \(reason); reconnecting", "ws")
            await restart(reason: "probe timeout")
        }
    }

    public func suspend(_ s: ConnectionStatus, reason: String) async {
        guard config != nil else { return }
        Log.info("Closing connection (\(reason))", "ws")
        tearDown()
        setStatus(s)
        await engine.connectionDropped()
    }

    private func tearDown() {
        generation += 1
        loopTask?.cancel()
        loopTask = nil
        probeTask?.cancel()
        probeTask = nil
        if let ch = channel {
            ch.close(code: 1001)
        }
        channel = nil
        welcomed = false
    }

    private func restart(reason: String) async {
        tearDown()
        let gen = generation
        await engine.connectionDropped()
        guard gen == generation, config != nil else { return }
        attempt = 0
        loopTask = Task { await self.runLoop(gen: gen) }
    }

    enum Outcome {
        case retry(String)
        case stop(StopReason)
    }

    private func runLoop(gen: Int) async {
        while gen == generation && !Task.isCancelled {
            guard let config else { return }
            setStatus(.connecting(attempt: attempt))
            let outcome = await connectOnce(config: config, gen: gen)
            guard gen == generation, !Task.isCancelled else { return }
            channel = nil
            welcomed = false
            await engine.connectionDropped()
            switch outcome {
            case .stop(let reason):
                Log.error("Connection stopped: \(reason.rawValue)", "ws")
                setStatus(.stopped(reason))
                return
            case .retry(let why):
                let d = backoff.delay(attempt: attempt)
                attempt += 1
                Log.warning("Disconnected (\(why)); retry in \(String(format: "%.1f", d)) s", "ws")
                setStatus(.waiting(retryAt: Date().addingTimeInterval(d), attempt: attempt, reason: why))
                do {
                    try await Task.sleep(for: .seconds(d))
                } catch {
                    return
                }
            }
        }
    }

    private func connectOnce(config: ConnectionConfig, gen: Int) async -> Outcome {
        let url = APIClient.webSocketURL(for: config.serverURL)
        let ch = connector.makeChannel(url: url, token: config.token)
        channel = ch
        Log.info("Connecting to \(url.host ?? url.absoluteString)", "ws")
        let watchdog = Task {
            try await Task.sleep(for: .seconds(20))
            ch.close(code: 1001)
        }
        do {
            try await ch.open()
            watchdog.cancel()
        } catch {
            watchdog.cancel()
            ch.close(code: 1001)
            if ch.upgradeStatus == 401 {
                return .stop(.unauthorized)
            }
            if let s = ch.upgradeStatus, s >= 400 {
                return .retry("server responded \(s)")
            }
            return .retry("cannot reach server: \(error)")
        }
        guard gen == generation else {
            ch.close(code: 1001)
            return .retry("superseded")
        }
        lastReceived = .now
        welcomed = false
        do {
            let lastSeq = await engine.lastSeq()
            try await ch.send(ClientMessage.hello(deviceId: config.deviceId, lastSeq: lastSeq, appVersion: config.appVersion).text())
        } catch {
            ch.close(code: 1001)
            return .retry("hello failed: \(error)")
        }
        let heartbeat = Task { await self.heartbeat(channel: ch, gen: gen) }
        defer { heartbeat.cancel() }
        var lastError: String?
        do {
            while true {
                let text = try await ch.receive()
                lastReceived = .now
                guard gen == generation else { return .retry("superseded") }
                let message: ServerMessage
                do {
                    message = try ServerMessage.decode(text)
                } catch {
                    Log.warning("Ignoring invalid message: \(error)", "ws")
                    continue
                }
                switch message {
                case .ping(let ts):
                    try? await ch.send(ClientMessage.pong(ts).text())
                case .pong:
                    break
                case .welcome(let w):
                    guard w.protocolVersion == Proto.version else {
                        ch.close(code: 1000)
                        return .stop(.protocolUnsupported)
                    }
                    welcomed = true
                    attempt = 0
                    Log.info("Connected as \(w.deviceId), \(w.onlineDevices.count) device(s) online", "ws")
                    setStatus(.connected(since: Date()))
                    await engine.handle(message)
                case .error(let e):
                    lastError = e.code
                    Log.error("Server error \(e.code): \(e.message)", "ws")
                default:
                    await engine.handle(message)
                }
            }
        } catch {
            let code = ch.closeCode
            ch.close(code: 1001)
            switch code {
            case 4001:
                return .stop(.unauthorized)
            case 4002:
                return .stop(.replaced)
            case 4003:
                return .stop(.protocolUnsupported)
            default:
                if lastError == "protocol_version_unsupported" { return .stop(.protocolUnsupported) }
                if lastError == "device_mismatch" { return .stop(.unauthorized) }
                if let code { return .retry("closed with code \(code)") }
                return .retry(String(describing: error))
            }
        }
    }

    private func heartbeat(channel ch: any WebSocketChannel, gen: Int) async {
        var sinceLastPing: Double = 0
        let tick: Double = 2
        while !Task.isCancelled && gen == generation {
            do {
                try await Task.sleep(for: .seconds(tick))
            } catch {
                return
            }
            guard gen == generation else { return }
            sinceLastPing += tick
            let silence = Double((ContinuousClock.now - lastReceived).components.seconds)
            if silence >= Proto.deadConnectionTimeout {
                Log.warning("No traffic for \(Int(silence)) s; closing dead connection", "ws")
                ch.close(code: 4005)
                return
            }
            if !welcomed && silence >= 15 {
                Log.warning("No welcome within 15 s; closing", "ws")
                ch.close(code: 4004)
                return
            }
            if sinceLastPing >= Proto.heartbeatInterval {
                sinceLastPing = 0
                let ts = Int64(Date().timeIntervalSince1970 * 1000)
                do {
                    try await ch.send(ClientMessage.ping(ts).text())
                } catch {
                    Log.warning("Ping failed: \(error)", "ws")
                    ch.close(code: 4005)
                    return
                }
            }
        }
    }
}
