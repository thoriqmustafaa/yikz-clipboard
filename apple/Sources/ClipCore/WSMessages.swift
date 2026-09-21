import Foundation

public struct OnlineDevice: Codable, Sendable, Equatable, Hashable, Identifiable {
    public var deviceId: String
    public var name: String
    public var platform: String

    public var id: String { deviceId }

    public init(deviceId: String, name: String, platform: String) {
        self.deviceId = deviceId
        self.name = name
        self.platform = platform
    }
}

public struct WelcomeMessage: Codable, Sendable, Equatable {
    public var protocolVersion: Int
    public var serverVersion: String
    public var serverId: String
    public var serverTime: Date
    public var deviceId: String
    public var currentSeq: Int64
    public var stateRev: Int64
    public var onlineDevices: [OnlineDevice]

    public init(protocolVersion: Int = 1, serverVersion: String = "test", serverId: String, serverTime: Date, deviceId: String, currentSeq: Int64, stateRev: Int64, onlineDevices: [OnlineDevice] = []) {
        self.protocolVersion = protocolVersion
        self.serverVersion = serverVersion
        self.serverId = serverId
        self.serverTime = serverTime
        self.deviceId = deviceId
        self.currentSeq = currentSeq
        self.stateRev = stateRev
        self.onlineDevices = onlineDevices
    }
}

public struct PresenceMessage: Codable, Sendable, Equatable {
    public var deviceId: String
    public var name: String
    public var platform: String
    public var online: Bool
}

public struct ClipMessage: Codable, Sendable, Equatable {
    public var item: ItemHeader
}

public struct ClipDeletedMessage: Codable, Sendable, Equatable {
    public var ids: [String]
    public var reason: String
    public var stateRev: Int64

    public init(ids: [String], reason: String, stateRev: Int64) {
        self.ids = ids
        self.reason = reason
        self.stateRev = stateRev
    }
}

public struct ClipPinnedMessage: Codable, Sendable, Equatable {
    public var id: String
    public var pinned: Bool
    public var stateRev: Int64

    public init(id: String, pinned: Bool, stateRev: Int64) {
        self.id = id
        self.pinned = pinned
        self.stateRev = stateRev
    }
}

public struct StorageWarningMessage: Codable, Sendable, Equatable {
    public var active: Bool
    public var reason: String
    public var freeDiskBytes: Int64
    public var minFreeDiskBytes: Int64
}

public struct WSErrorMessage: Codable, Sendable, Equatable {
    public var code: String
    public var message: String
    public var details: JSONValue?
}

public enum ServerMessage: Sendable, Equatable {
    case welcome(WelcomeMessage)
    case presence(PresenceMessage)
    case devicesChanged
    case clip(ItemHeader)
    case clipDeleted(ClipDeletedMessage)
    case clipPinned(ClipPinnedMessage)
    case storageWarning(StorageWarningMessage)
    case ping(Int64)
    case pong(Int64)
    case error(WSErrorMessage)
    case unknown(String)

    private struct TypeProbe: Decodable {
        var type: String
    }

    private struct TsBody: Decodable {
        var ts: Int64
    }

    public static func decode(_ data: Data) throws -> ServerMessage {
        let d = JSONCoding.decoder()
        let type = try d.decode(TypeProbe.self, from: data).type
        switch type {
        case "welcome": return .welcome(try d.decode(WelcomeMessage.self, from: data))
        case "presence": return .presence(try d.decode(PresenceMessage.self, from: data))
        case "devices_changed": return .devicesChanged
        case "clip": return .clip(try d.decode(ClipMessage.self, from: data).item)
        case "clip_deleted": return .clipDeleted(try d.decode(ClipDeletedMessage.self, from: data))
        case "clip_pinned": return .clipPinned(try d.decode(ClipPinnedMessage.self, from: data))
        case "storage_warning": return .storageWarning(try d.decode(StorageWarningMessage.self, from: data))
        case "ping": return .ping(try d.decode(TsBody.self, from: data).ts)
        case "pong": return .pong(try d.decode(TsBody.self, from: data).ts)
        case "error": return .error(try d.decode(WSErrorMessage.self, from: data))
        default: return .unknown(type)
        }
    }

    public static func decode(_ text: String) throws -> ServerMessage {
        try decode(Data(text.utf8))
    }
}

public enum ClientMessage: Sendable, Equatable {
    case hello(deviceId: String, lastSeq: Int64, appVersion: String)
    case ping(Int64)
    case pong(Int64)

    private struct Hello: Encodable {
        var type = "hello"
        var protocolVersion: Int
        var deviceId: String
        var lastSeq: Int64
        var appVersion: String
        var platform: String
    }

    private struct Ts: Encodable {
        var type: String
        var ts: Int64
    }

    public func encoded() throws -> Data {
        let e = JSONCoding.encoder()
        switch self {
        case .hello(let deviceId, let lastSeq, let appVersion):
            return try e.encode(Hello(protocolVersion: Proto.version, deviceId: deviceId, lastSeq: lastSeq, appVersion: appVersion, platform: Proto.platform))
        case .ping(let ts):
            return try e.encode(Ts(type: "ping", ts: ts))
        case .pong(let ts):
            return try e.encode(Ts(type: "pong", ts: ts))
        }
    }

    public func text() throws -> String {
        String(decoding: try encoded(), as: UTF8.self)
    }
}
