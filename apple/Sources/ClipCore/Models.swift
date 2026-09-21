import Foundation

public enum ItemKind: String, Codable, Sendable, CaseIterable, Hashable {
    case text
    case image
    case files
}

public struct KDFParams: Codable, Sendable, Equatable {
    public var algorithm: String
    public var iterations: Int
    public var keyLength: Int

    public init(algorithm: String, iterations: Int, keyLength: Int) {
        self.algorithm = algorithm
        self.iterations = iterations
        self.keyLength = keyLength
    }

    public var isSupported: Bool {
        algorithm == Proto.kdfAlgorithm && keyLength == Proto.keyLength && iterations > 0
    }
}

public struct LoginRequest: Codable, Sendable, Equatable {
    public var username: String
    public var password: String
    public var deviceName: String
    public var platform: String
    public var deviceId: String?

    public init(username: String, password: String, deviceName: String, platform: String = Proto.platform, deviceId: String? = nil) {
        self.username = username
        self.password = password
        self.deviceName = deviceName
        self.platform = platform
        self.deviceId = deviceId
    }
}

public struct LoginResponse: Codable, Sendable, Equatable {
    public var deviceId: String
    public var token: String
    public var username: String
    public var salt: String
    public var kdf: KDFParams
    public var keyCheck: String?
    public var serverId: String
    public var serverVersion: String
    public var protocolVersion: Int
}

public struct DeviceInfo: Codable, Sendable, Equatable, Identifiable, Hashable {
    public var id: String
    public var name: String
    public var platform: String
    public var createdAt: Date
    public var lastSeenAt: Date
    public var online: Bool
    public var revoked: Bool
    public var current: Bool

    public init(id: String, name: String, platform: String, createdAt: Date, lastSeenAt: Date, online: Bool, revoked: Bool, current: Bool) {
        self.id = id
        self.name = name
        self.platform = platform
        self.createdAt = createdAt
        self.lastSeenAt = lastSeenAt
        self.online = online
        self.revoked = revoked
        self.current = current
    }
}

public struct DevicesResponse: Codable, Sendable, Equatable {
    public var devices: [DeviceInfo]
}

public struct RenameDeviceRequest: Codable, Sendable, Equatable {
    public var name: String
}

public struct MeResponse: Codable, Sendable, Equatable {
    public var username: String
    public var device: DeviceInfo
    public var salt: String
    public var kdf: KDFParams
    public var keyCheck: String?
    public var serverId: String
    public var serverVersion: String
    public var protocolVersion: Int
    public var limits: Limits?
}

public struct KeyCheckRequest: Codable, Sendable, Equatable {
    public var keyCheck: String
}

public struct ItemHeader: Codable, Sendable, Equatable, Identifiable, Hashable {
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
    public var meta: String
    public var payload: String?

    public init(
        id: String, seq: Int64, deviceId: String, kind: ItemKind, size: Int64, chunkCount: Int,
        createdAt: Date, pinned: Bool, contentHash: String, hasThumb: Bool, storedBytes: Int64,
        meta: String, payload: String? = nil
    ) {
        self.id = id
        self.seq = seq
        self.deviceId = deviceId
        self.kind = kind
        self.size = size
        self.chunkCount = chunkCount
        self.createdAt = createdAt
        self.pinned = pinned
        self.contentHash = contentHash
        self.hasThumb = hasThumb
        self.storedBytes = storedBytes
        self.meta = meta
        self.payload = payload
    }

    public var isInline: Bool { chunkCount == 0 }
}

public struct ImageDimensions: Codable, Sendable, Equatable, Hashable {
    public var width: Int
    public var height: Int

    public init(width: Int, height: Int) {
        self.width = width
        self.height = height
    }
}

public struct ItemMeta: Codable, Sendable, Equatable, Hashable {
    public var v: Int
    public var mime: String
    public var preview: String
    public var sha256: String
    public var image: ImageDimensions?
    public var files: [ArchiveFileInfo]?
    public var sourceApp: String?

    public init(v: Int = 1, mime: String, preview: String, sha256: String, image: ImageDimensions? = nil, files: [ArchiveFileInfo]? = nil, sourceApp: String? = nil) {
        self.v = v
        self.mime = mime
        self.preview = preview
        self.sha256 = sha256
        self.image = image
        self.files = files
        self.sourceApp = sourceApp
    }

    public func encoded() throws -> Data {
        try JSONCoding.encoder().encode(self)
    }

    public static func decode(_ data: Data) throws -> ItemMeta {
        try JSONCoding.decoder().decode(ItemMeta.self, from: data)
    }
}

public struct CreateItemRequest: Codable, Sendable, Equatable {
    public var id: String
    public var kind: ItemKind
    public var size: Int64
    public var chunkCount: Int
    public var contentHash: String
    public var meta: String
    public var payload: String
}

public struct CommitRequest: Codable, Sendable, Equatable {
    public var kind: ItemKind
    public var size: Int64
    public var chunkCount: Int
    public var contentHash: String
    public var meta: String
}

public struct HistoryPage: Codable, Sendable, Equatable {
    public var items: [ItemHeader]
    public var hasMore: Bool
}

public struct HistoryIndexEntry: Codable, Sendable, Equatable {
    public var id: String
    public var seq: Int64
    public var pinned: Bool
}

public struct HistoryIndex: Codable, Sendable, Equatable {
    public var currentSeq: Int64
    public var stateRev: Int64
    public var items: [HistoryIndexEntry]
}

public struct PinRequest: Codable, Sendable, Equatable {
    public var pinned: Bool
}

public struct StorageInfo: Codable, Sendable, Equatable {
    public var usedBytes: Int64
    public var limitBytes: Int64
    public var pinnedBytes: Int64
    public var pinnedLimitBytes: Int64
    public var itemCount: Int
    public var freeDiskBytes: Int64
    public var minFreeDiskBytes: Int64
    public var retentionDays: Int
    public var diskLow: Bool
}

public struct HealthResponse: Codable, Sendable, Equatable {
    public var status: String
    public var serverVersion: String
    public var protocolVersion: Int
}

public struct APIErrorBody: Codable, Sendable, Equatable {
    public var code: String
    public var message: String
    public var details: JSONValue?
}
