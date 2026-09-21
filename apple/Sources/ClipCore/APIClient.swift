import Foundation

public protocol HTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

public struct URLSessionTransport: HTTPTransport {
    public let session: URLSession

    public init(session: URLSession = URLSessionTransport.makeSession()) {
        self.session = session
    }

    public static func makeSession() -> URLSession {
        let cfg = URLSessionConfiguration.ephemeral
        cfg.waitsForConnectivity = false
        cfg.timeoutIntervalForRequest = 30
        cfg.timeoutIntervalForResource = 600
        cfg.requestCachePolicy = .reloadIgnoringLocalCacheData
        cfg.urlCache = nil
        cfg.httpMaximumConnectionsPerHost = 6
        return URLSession(configuration: cfg)
    }

    public func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw APIError.invalidResponse }
        return (data, http)
    }
}

public enum APIError: Error, Sendable, Equatable, CustomStringConvertible {
    case http(status: Int, code: String, message: String, details: JSONValue?, retryAfter: TimeInterval?)
    case network(String)
    case decoding(String)
    case invalidResponse
    case notSignedIn

    public var status: Int? {
        if case .http(let s, _, _, _, _) = self { return s }
        return nil
    }

    public var code: String? {
        if case .http(_, let c, _, _, _) = self { return c }
        return nil
    }

    public var isUnauthorized: Bool { status == 401 && code != "invalid_credentials" }

    public var isRetryable: Bool {
        switch self {
        case .network: return true
        case .http(let s, _, _, _, _): return [500, 502, 503, 504].contains(s)
        default: return false
        }
    }

    public var description: String {
        switch self {
        case .http(let s, let c, let m, _, _): return "HTTP \(s) \(c): \(m)"
        case .network(let m): return "network error: \(m)"
        case .decoding(let m): return "invalid response: \(m)"
        case .invalidResponse: return "invalid response"
        case .notSignedIn: return "not signed in"
        }
    }

    public var userMessage: String {
        switch self {
        case .http(_, let c, let m, _, let retry):
            switch c {
            case "invalid_credentials": return "Wrong username or password."
            case "rate_limited":
                if let retry { return "Too many attempts. Try again in \(Int(retry.rounded(.up))) s." }
                return "Too many attempts. Try again later."
            case "item_too_large": return "The item does not fit in server storage."
            case "disk_low": return "The server disk is almost full."
            case "pinned_limit": return "Pinned storage limit reached."
            case "unauthorized": return "This device was signed out."
            default: return m.isEmpty ? c : m
            }
        case .network(let m): return m
        case .decoding: return "The server sent an unexpected response."
        case .invalidResponse: return "The server sent an unexpected response."
        case .notSignedIn: return "Not signed in."
        }
    }
}

public struct APIClient: Sendable {
    public var baseURL: URL
    public var token: String?
    public let transport: any HTTPTransport

    public init(baseURL: URL, token: String?, transport: any HTTPTransport) {
        self.baseURL = baseURL
        self.token = token
        self.transport = transport
    }

    func url(_ path: String, query: [URLQueryItem] = []) -> URL {
        var comps = URLComponents(url: baseURL, resolvingAgainstBaseURL: false)!
        let basePath = comps.path.hasSuffix("/") ? String(comps.path.dropLast()) : comps.path
        comps.path = basePath + path
        comps.queryItems = query.isEmpty ? nil : query
        return comps.url!
    }

    func request(_ method: String, _ path: String, query: [URLQueryItem] = [], body: Data? = nil, contentType: String? = nil, timeout: TimeInterval? = nil, auth: Bool = true) -> URLRequest {
        var r = URLRequest(url: url(path, query: query))
        r.httpMethod = method
        r.setValue("application/json", forHTTPHeaderField: "Accept")
        if let body {
            r.httpBody = body
            r.setValue(contentType ?? "application/json", forHTTPHeaderField: "Content-Type")
        }
        if auth, let token {
            r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        if let timeout { r.timeoutInterval = timeout }
        return r
    }

    func perform(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let data: Data
        let response: HTTPURLResponse
        do {
            (data, response) = try await transport.send(request)
        } catch let e as APIError {
            throw e
        } catch is CancellationError {
            throw CancellationError()
        } catch let e as URLError where e.code == .cancelled {
            throw CancellationError()
        } catch {
            throw APIError.network(error.localizedDescription)
        }
        guard (200..<300).contains(response.statusCode) else {
            let body = try? JSONCoding.decoder().decode(APIErrorBody.self, from: data)
            let retry = (response.value(forHTTPHeaderField: "Retry-After")).flatMap(TimeInterval.init)
            throw APIError.http(
                status: response.statusCode,
                code: body?.code ?? "http_\(response.statusCode)",
                message: body?.message ?? HTTPURLResponse.localizedString(forStatusCode: response.statusCode),
                details: body?.details,
                retryAfter: retry
            )
        }
        return (data, response)
    }

    func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
        do {
            return try JSONCoding.decoder().decode(type, from: data)
        } catch {
            throw APIError.decoding(String(describing: error))
        }
    }

    func json<T: Encodable>(_ value: T) throws -> Data {
        try JSONCoding.encoder().encode(value)
    }

    public func login(_ body: LoginRequest) async throws -> LoginResponse {
        let (data, _) = try await perform(request("POST", "/api/login", body: try json(body), auth: false))
        return try decode(LoginResponse.self, data)
    }

    public func me() async throws -> MeResponse {
        let (data, _) = try await perform(request("GET", "/api/me"))
        return try decode(MeResponse.self, data)
    }

    public func logout() async throws {
        _ = try await perform(request("POST", "/api/logout"))
    }

    public func health() async throws -> HealthResponse {
        let (data, _) = try await perform(request("GET", "/healthz", auth: false))
        return try decode(HealthResponse.self, data)
    }

    public func putKeyCheck(_ keyCheck: String) async throws {
        _ = try await perform(request("PUT", "/api/account/key-check", body: try json(KeyCheckRequest(keyCheck: keyCheck))))
    }

    public func devices() async throws -> [DeviceInfo] {
        let (data, _) = try await perform(request("GET", "/api/devices"))
        return try decode(DevicesResponse.self, data).devices
    }

    public func renameDevice(id: String, name: String) async throws -> DeviceInfo {
        let (data, _) = try await perform(request("PATCH", "/api/devices/\(id)", body: try json(RenameDeviceRequest(name: name))))
        return try decode(DeviceInfo.self, data)
    }

    public func history(before: Int64? = nil, after: Int64? = nil, limit: Int? = nil) async throws -> HistoryPage {
        var q: [URLQueryItem] = []
        if let before { q.append(URLQueryItem(name: "before", value: String(before))) }
        if let after { q.append(URLQueryItem(name: "after", value: String(after))) }
        if let limit { q.append(URLQueryItem(name: "limit", value: String(limit))) }
        let (data, _) = try await perform(request("GET", "/api/history", query: q))
        return try decode(HistoryPage.self, data)
    }

    public func historyIndex() async throws -> HistoryIndex {
        let (data, _) = try await perform(request("GET", "/api/history/index", timeout: 60))
        return try decode(HistoryIndex.self, data)
    }

    public func item(id: String) async throws -> ItemHeader {
        let (data, _) = try await perform(request("GET", "/api/items/\(id)"))
        return try decode(ItemHeader.self, data)
    }

    public func createItem(_ body: CreateItemRequest) async throws -> ItemHeader {
        let (data, _) = try await perform(request("POST", "/api/items", body: try json(body), timeout: 60))
        return try decode(ItemHeader.self, data)
    }

    public func uploadThumb(id: String, sealed: Data) async throws {
        _ = try await perform(request("PUT", "/api/items/\(id)/thumb", body: sealed, contentType: "application/octet-stream"))
    }

    public func uploadChunk(id: String, index: Int, sealed: Data) async throws {
        _ = try await perform(request("PUT", "/api/items/\(id)/chunks/\(index)", body: sealed, contentType: "application/octet-stream", timeout: 120))
    }

    public func commit(id: String, body: CommitRequest) async throws -> ItemHeader {
        let (data, _) = try await perform(request("POST", "/api/items/\(id)/commit", body: try json(body), timeout: 60))
        return try decode(ItemHeader.self, data)
    }

    public func downloadChunk(id: String, index: Int) async throws -> Data {
        var r = request("GET", "/api/items/\(id)/chunks/\(index)", timeout: 120)
        r.setValue("application/octet-stream", forHTTPHeaderField: "Accept")
        return try await perform(r).0
    }

    public func downloadThumb(id: String) async throws -> Data {
        var r = request("GET", "/api/items/\(id)/thumb")
        r.setValue("application/octet-stream", forHTTPHeaderField: "Accept")
        return try await perform(r).0
    }

    public func setPinned(id: String, pinned: Bool) async throws -> ItemHeader {
        let (data, _) = try await perform(request("POST", "/api/items/\(id)/pin", body: try json(PinRequest(pinned: pinned))))
        return try decode(ItemHeader.self, data)
    }

    public func deleteItem(id: String) async throws {
        do {
            _ = try await perform(request("DELETE", "/api/items/\(id)"))
        } catch let e as APIError where e.status == 404 {
            return
        }
    }

    public func storage() async throws -> StorageInfo {
        let (data, _) = try await perform(request("GET", "/api/storage"))
        return try decode(StorageInfo.self, data)
    }

    public static func webSocketURL(for base: URL) -> URL {
        var comps = URLComponents(url: base, resolvingAgainstBaseURL: false)!
        comps.scheme = comps.scheme == "http" ? "ws" : "wss"
        let basePath = comps.path.hasSuffix("/") ? String(comps.path.dropLast()) : comps.path
        comps.path = basePath + "/ws"
        comps.query = nil
        return comps.url!
    }

    public static func normalizedServerURL(_ raw: String) -> URL? {
        var s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.isEmpty { return nil }
        if !s.contains("://") { s = "https://" + s }
        while s.hasSuffix("/") { s.removeLast() }
        guard let u = URL(string: s), let scheme = u.scheme?.lowercased(), scheme == "https" || scheme == "http", u.host != nil else { return nil }
        return u
    }
}

public func withRetries<T: Sendable>(
    maxAttempts: Int = 8,
    backoff: Backoff = .http,
    label: String,
    _ operation: @Sendable () async throws -> T
) async throws -> T {
    var attempt = 0
    while true {
        do {
            return try await operation()
        } catch let e as APIError where e.isRetryable && attempt + 1 < maxAttempts {
            let d = backoff.delay(attempt: attempt)
            Log.warning("\(label) failed (\(e)), retry \(attempt + 1) in \(String(format: "%.1f", d)) s", "http")
            attempt += 1
            try await Task.sleep(nanoseconds: UInt64(d * 1_000_000_000))
        }
    }
}
