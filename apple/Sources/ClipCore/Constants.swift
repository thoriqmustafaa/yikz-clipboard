import Foundation

public enum Proto {
    public static let version = 1
    public static let platform = "macos"
    public static let kdfAlgorithm = "pbkdf2-sha256"
    public static let kdfIterations: UInt32 = 600_000
    public static let keyLength = 32
    public static let saltLength = 16
    public static let nonceLength = 12
    public static let tagLength = 16
    public static let sealOverhead = 28
    public static let inlineMaxBytes: Int64 = 262_144
    public static let chunkSizeBytes: Int64 = 4_194_304
    public static let maxChunkBodyBytes: Int64 = 4_194_332
    public static let metaMaxBytes = 65_536
    public static let thumbMaxBytes = 65_536
    public static let maxFilesPerItem = 1000
    public static let previewMaxCodePoints = 500
    public static let thumbnailMaxSide = 320
    public static let historyDefaultLimit = 100
    public static let historyMaxLimit = 500
    public static let heartbeatInterval: TimeInterval = 20
    public static let deadConnectionTimeout: TimeInterval = 45
    public static let autoApplyMaxAge: TimeInterval = 300
    public static let defaultAutoDownloadLimit: Int64 = 52_428_800
    public static let originPasteboardType = "dev.yikz.clipboard.origin"
    public static let recentHashCapacity = 32
    public static let contentHashLabel = "yikz-clipboard/v1/content-hash"
    public static let keyCheckLabel = "yikz-clipboard/v1/key-check"
    public static let textMime = "text/plain; charset=utf-8"
    public static let imageMime = "image/png"
    public static let filesMime = "application/x-yikz-files"
}

public struct Limits: Codable, Sendable, Equatable {
    public var inlineMaxBytes: Int64
    public var chunkSizeBytes: Int64
    public var maxChunkBodyBytes: Int64
    public var thumbMaxBytes: Int
    public var metaMaxBytes: Int
    public var maxJsonBodyBytes: Int
    public var maxFilesPerItem: Int
    public var historyDefaultLimit: Int
    public var historyMaxLimit: Int
    public var maxWsClientFrameBytes: Int
    public var maxWsServerFrameBytes: Int
    public var maxConnectionsPerDevice: Int
    public var uploadTtlSeconds: Int
    public var diskLowMaxUploadBytes: Int64

    public static let defaults = Limits(
        inlineMaxBytes: Proto.inlineMaxBytes,
        chunkSizeBytes: Proto.chunkSizeBytes,
        maxChunkBodyBytes: Proto.maxChunkBodyBytes,
        thumbMaxBytes: Proto.thumbMaxBytes,
        metaMaxBytes: Proto.metaMaxBytes,
        maxJsonBodyBytes: 1_048_576,
        maxFilesPerItem: Proto.maxFilesPerItem,
        historyDefaultLimit: Proto.historyDefaultLimit,
        historyMaxLimit: Proto.historyMaxLimit,
        maxWsClientFrameBytes: 65_536,
        maxWsServerFrameBytes: 1_048_576,
        maxConnectionsPerDevice: 8,
        uploadTtlSeconds: 3600,
        diskLowMaxUploadBytes: 1_048_576
    )

    public init(
        inlineMaxBytes: Int64, chunkSizeBytes: Int64, maxChunkBodyBytes: Int64, thumbMaxBytes: Int,
        metaMaxBytes: Int, maxJsonBodyBytes: Int, maxFilesPerItem: Int, historyDefaultLimit: Int,
        historyMaxLimit: Int, maxWsClientFrameBytes: Int, maxWsServerFrameBytes: Int,
        maxConnectionsPerDevice: Int, uploadTtlSeconds: Int, diskLowMaxUploadBytes: Int64
    ) {
        self.inlineMaxBytes = inlineMaxBytes
        self.chunkSizeBytes = chunkSizeBytes
        self.maxChunkBodyBytes = maxChunkBodyBytes
        self.thumbMaxBytes = thumbMaxBytes
        self.metaMaxBytes = metaMaxBytes
        self.maxJsonBodyBytes = maxJsonBodyBytes
        self.maxFilesPerItem = maxFilesPerItem
        self.historyDefaultLimit = historyDefaultLimit
        self.historyMaxLimit = historyMaxLimit
        self.maxWsClientFrameBytes = maxWsClientFrameBytes
        self.maxWsServerFrameBytes = maxWsServerFrameBytes
        self.maxConnectionsPerDevice = maxConnectionsPerDevice
        self.uploadTtlSeconds = uploadTtlSeconds
        self.diskLowMaxUploadBytes = diskLowMaxUploadBytes
    }

    public init(from decoder: Decoder) throws {
        let d = Limits.defaults
        let c = try decoder.container(keyedBy: CodingKeys.self)
        inlineMaxBytes = try c.decodeIfPresent(Int64.self, forKey: .inlineMaxBytes) ?? d.inlineMaxBytes
        chunkSizeBytes = try c.decodeIfPresent(Int64.self, forKey: .chunkSizeBytes) ?? d.chunkSizeBytes
        maxChunkBodyBytes = try c.decodeIfPresent(Int64.self, forKey: .maxChunkBodyBytes) ?? d.maxChunkBodyBytes
        thumbMaxBytes = try c.decodeIfPresent(Int.self, forKey: .thumbMaxBytes) ?? d.thumbMaxBytes
        metaMaxBytes = try c.decodeIfPresent(Int.self, forKey: .metaMaxBytes) ?? d.metaMaxBytes
        maxJsonBodyBytes = try c.decodeIfPresent(Int.self, forKey: .maxJsonBodyBytes) ?? d.maxJsonBodyBytes
        maxFilesPerItem = try c.decodeIfPresent(Int.self, forKey: .maxFilesPerItem) ?? d.maxFilesPerItem
        historyDefaultLimit = try c.decodeIfPresent(Int.self, forKey: .historyDefaultLimit) ?? d.historyDefaultLimit
        historyMaxLimit = try c.decodeIfPresent(Int.self, forKey: .historyMaxLimit) ?? d.historyMaxLimit
        maxWsClientFrameBytes = try c.decodeIfPresent(Int.self, forKey: .maxWsClientFrameBytes) ?? d.maxWsClientFrameBytes
        maxWsServerFrameBytes = try c.decodeIfPresent(Int.self, forKey: .maxWsServerFrameBytes) ?? d.maxWsServerFrameBytes
        maxConnectionsPerDevice = try c.decodeIfPresent(Int.self, forKey: .maxConnectionsPerDevice) ?? d.maxConnectionsPerDevice
        uploadTtlSeconds = try c.decodeIfPresent(Int.self, forKey: .uploadTtlSeconds) ?? d.uploadTtlSeconds
        diskLowMaxUploadBytes = try c.decodeIfPresent(Int64.self, forKey: .diskLowMaxUploadBytes) ?? d.diskLowMaxUploadBytes
    }
}
