import Foundation

public struct EngineIdentity: Sendable, Equatable {
    public var serverURL: URL
    public var deviceId: String
    public var token: String
    public var key: MasterKey
    public var salt: Data

    public init(serverURL: URL, deviceId: String, token: String, key: MasterKey, salt: Data) {
        self.serverURL = serverURL
        self.deviceId = deviceId
        self.token = token
        self.key = key
        self.salt = salt
    }
}

public struct EngineSettings: Sendable, Equatable {
    public var autoDownloadLimit: Int64
    public var syncText: Bool
    public var syncImages: Bool
    public var syncFiles: Bool
    public var paused: Bool

    public init(autoDownloadLimit: Int64 = Proto.defaultAutoDownloadLimit, syncText: Bool = true, syncImages: Bool = true, syncFiles: Bool = true, paused: Bool = false) {
        self.autoDownloadLimit = autoDownloadLimit
        self.syncText = syncText
        self.syncImages = syncImages
        self.syncFiles = syncFiles
        self.paused = paused
    }

    public func allows(_ kind: ItemKind) -> Bool {
        switch kind {
        case .text: return syncText
        case .image: return syncImages
        case .files: return syncFiles
        }
    }
}

public enum ApplyContent: Sendable, Equatable {
    case text(String)
    case image(Data)
    case files([URL])
}

public struct ClipboardSink: Sendable {
    public var write: @Sendable (ApplyContent, String) async -> Bool

    public init(write: @escaping @Sendable (ApplyContent, String) async -> Bool) {
        self.write = write
    }
}

public struct OutgoingContent: Sendable {
    public enum Body: Sendable {
        case data(Data)
        case file(URL, deleteAfter: Bool)
    }

    public var kind: ItemKind
    public var body: Body
    public var preview: String
    public var image: ImageDimensions?
    public var files: [ArchiveFileInfo]?
    public var sourceApp: String?
    public var thumbnail: Data?
    public var text: String?
    public var title: String

    public static func text(_ string: String, sourceApp: String?) -> OutgoingContent? {
        guard !string.isEmpty else { return nil }
        return OutgoingContent(
            kind: .text, body: .data(Data(string.utf8)), preview: Preview.make(string),
            image: nil, files: nil, sourceApp: sourceApp, thumbnail: nil, text: string,
            title: "Text"
        )
    }

    public static func image(png: Data, dimensions: ImageDimensions, thumbnail: Data?, sourceApp: String?) -> OutgoingContent {
        OutgoingContent(
            kind: .image, body: .data(png), preview: "", image: dimensions, files: nil,
            sourceApp: sourceApp, thumbnail: thumbnail, text: nil, title: "Image"
        )
    }

    public static func files(archive: Body, files: [ArchiveFileInfo], sourceApp: String?) -> OutgoingContent {
        let title = files.count == 1 ? files[0].name : "\(files.count) files"
        return OutgoingContent(
            kind: .files, body: archive, preview: Preview.forFiles(files.map(\.name)), image: nil,
            files: files, sourceApp: sourceApp, thumbnail: nil, text: nil, title: title
        )
    }

    public var mime: String {
        switch kind {
        case .text: return Proto.textMime
        case .image: return Proto.imageMime
        case .files: return Proto.filesMime
        }
    }

    public func cleanup() {
        if case .file(let url, true) = body {
            try? FileManager.default.removeItem(at: url)
        }
    }

    public static func digest(_ body: Body, key: MasterKey) throws -> ContentDigest {
        var d = key.digester()
        switch body {
        case .data(let data):
            d.update(data)
        case .file(let url, _):
            let h = try FileHandle(forReadingFrom: url)
            defer { try? h.close() }
            while let block = try h.read(upToCount: 1 << 20), !block.isEmpty {
                d.update(block)
            }
        }
        return d.finalize()
    }

    public static func read(_ body: Body, range: Range<Int64>) throws -> Data {
        switch body {
        case .data(let data):
            let lo = data.startIndex + Int(range.lowerBound)
            let hi = data.startIndex + Int(range.upperBound)
            return data.subdata(in: lo..<hi)
        case .file(let url, _):
            let h = try FileHandle(forReadingFrom: url)
            defer { try? h.close() }
            try h.seek(toOffset: UInt64(range.lowerBound))
            var out = Data()
            let want = Int(range.upperBound - range.lowerBound)
            while out.count < want {
                guard let block = try h.read(upToCount: want - out.count), !block.isEmpty else { break }
                out.append(block)
            }
            guard out.count == want else { throw ArchiveError.io("file changed while uploading") }
            return out
        }
    }
}

public struct TransferProgress: Sendable, Identifiable, Equatable {
    public enum Direction: String, Sendable {
        case upload
        case download
    }

    public var id: String
    public var title: String
    public var direction: Direction
    public var completed: Int64
    public var total: Int64
    public var finished: Bool
    public var failure: String?

    public var fraction: Double {
        total > 0 ? min(1, Double(completed) / Double(total)) : 0
    }
}

public enum EngineEvent: Sendable {
    case history([HistoryEntry])
    case devices([DeviceInfo])
    case online([OnlineDevice])
    case transfer(TransferProgress)
    case storageWarning(StorageWarningMessage)
    case largeItemAvailable(HistoryEntry)
    case applied(HistoryEntry)
    case uploaded(HistoryEntry)
    case uploadFailed(String)
    case unauthorized
    case keyRejected
    case protocolUnsupported
    case syncing(Bool)
}

public enum SubmitOutcome: Sendable, Equatable {
    case queued
    case skippedEcho
    case skippedNewest
    case paused
    case disabled
    case notSignedIn
    case failed(String)
}

public enum EngineError: Error, Sendable, Equatable, CustomStringConvertible {
    case notSignedIn
    case keyRejected
    case protocolUnsupported
    case unsupportedItem
    case notFound
    case corrupt(String)
    case tooLarge

    public var description: String {
        switch self {
        case .notSignedIn: return "Not signed in."
        case .keyRejected: return "The encryption password does not match this server."
        case .protocolUnsupported: return "The server uses a newer protocol. Update the app."
        case .unsupportedItem: return "This item uses an unsupported format."
        case .notFound: return "The item no longer exists."
        case .corrupt(let m): return "The item failed verification: \(m)"
        case .tooLarge: return "The item is larger than the auto-download limit."
        }
    }
}
