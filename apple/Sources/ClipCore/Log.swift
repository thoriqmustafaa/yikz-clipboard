import Foundation
import os

public enum LogLevel: Int, Sendable, Comparable, CaseIterable, Codable, Hashable {
    case debug = 0
    case info = 1
    case warning = 2
    case error = 3

    public static func < (lhs: LogLevel, rhs: LogLevel) -> Bool { lhs.rawValue < rhs.rawValue }

    public var label: String {
        switch self {
        case .debug: return "DEBUG"
        case .info: return "INFO"
        case .warning: return "WARN"
        case .error: return "ERROR"
        }
    }

    public var title: String {
        switch self {
        case .debug: return "Debug"
        case .info: return "Info"
        case .warning: return "Warning"
        case .error: return "Error"
        }
    }

    var osType: OSLogType {
        switch self {
        case .debug: return .debug
        case .info: return .info
        case .warning: return .default
        case .error: return .error
        }
    }
}

public struct LogEntry: Sendable, Identifiable, Hashable {
    public let id: UInt64
    public let date: Date
    public let level: LogLevel
    public let category: String
    public let message: String

    public var line: String {
        "\(LogEntry.stamp(date)) \(level.label.padding(toLength: 5, withPad: " ", startingAt: 0)) [\(category)] \(message)"
    }

    static func stamp(_ date: Date) -> String {
        Timestamp.format(date)
    }
}

final class LogFileWriter: @unchecked Sendable {
    let directory: URL
    let baseName = "YikzClipboard"
    let maxBytes: Int64 = 2 * 1024 * 1024
    let keep = 5
    private var handle: FileHandle?
    private var size: Int64 = 0

    init(directory: URL) {
        self.directory = directory
    }

    var currentURL: URL { directory.appendingPathComponent("\(baseName).log") }

    private func rotatedURL(_ n: Int) -> URL { directory.appendingPathComponent("\(baseName).\(n).log") }

    func write(_ line: String) {
        if handle == nil { open() }
        guard let handle else { return }
        let data = Data((line + "\n").utf8)
        do {
            try handle.write(contentsOf: data)
            size += Int64(data.count)
        } catch {
            try? handle.close()
            self.handle = nil
            return
        }
        if size >= maxBytes { rotate() }
    }

    private func open() {
        let fm = FileManager.default
        try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
        if !fm.fileExists(atPath: currentURL.path) {
            fm.createFile(atPath: currentURL.path, contents: nil, attributes: [.posixPermissions: 0o600])
        }
        handle = try? FileHandle(forWritingTo: currentURL)
        if let h = handle {
            size = Int64((try? h.seekToEnd()) ?? 0)
        }
    }

    private func rotate() {
        try? handle?.close()
        handle = nil
        let fm = FileManager.default
        try? fm.removeItem(at: rotatedURL(keep))
        for n in stride(from: keep - 1, through: 1, by: -1) {
            if fm.fileExists(atPath: rotatedURL(n).path) {
                try? fm.moveItem(at: rotatedURL(n), to: rotatedURL(n + 1))
            }
        }
        try? fm.moveItem(at: currentURL, to: rotatedURL(1))
        size = 0
        open()
    }

    func flush() {
        try? handle?.synchronize()
    }
}

public final class Log: Sendable {
    public typealias Observer = @Sendable (LogEntry) -> Void

    struct State: Sendable {
        var entries: [LogEntry] = []
        var start = 0
        var nextId: UInt64 = 1
        var observers: [UUID: Observer] = [:]
        var minimumFileLevel: LogLevel = .debug
    }

    public static let shared = Log()
    public static let subsystem = "dev.yikz.clipboard"
    public let capacity = 5000

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let queue = DispatchQueue(label: "dev.yikz.clipboard.log", qos: .utility)
    private let writerBox = OSAllocatedUnfairLock<LogFileWriter?>(initialState: nil)

    public static var defaultDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/YikzClipboard", isDirectory: true)
    }

    public var directory: URL? {
        writerBox.withLock { $0?.directory }
    }

    public func enableFileLogging(directory: URL = Log.defaultDirectory) {
        let writer = LogFileWriter(directory: directory)
        writerBox.withLock { $0 = writer }
    }

    public static func debug(_ message: String, _ category: String = "app") { shared.log(.debug, message, category) }
    public static func info(_ message: String, _ category: String = "app") { shared.log(.info, message, category) }
    public static func warning(_ message: String, _ category: String = "app") { shared.log(.warning, message, category) }
    public static func error(_ message: String, _ category: String = "app") { shared.log(.error, message, category) }

    public func log(_ level: LogLevel, _ message: String, _ category: String) {
        let now = Date()
        let (entry, observers): (LogEntry, [Observer]) = state.withLock { s in
            let e = LogEntry(id: s.nextId, date: now, level: level, category: category, message: message)
            s.nextId += 1
            if s.entries.count < capacity {
                s.entries.append(e)
            } else {
                s.entries[s.start] = e
                s.start = (s.start + 1) % capacity
            }
            return (e, Array(s.observers.values))
        }
        Logger(subsystem: Log.subsystem, category: category).log(level: level.osType, "\(message, privacy: .public)")
        let writer = writerBox.withLock { $0 }
        if let writer {
            let line = entry.line
            queue.async { writer.write(line) }
        }
        for o in observers { o(entry) }
    }

    public func entries() -> [LogEntry] {
        state.withLock { s in
            if s.entries.count < capacity { return s.entries }
            return Array(s.entries[s.start...] + s.entries[..<s.start])
        }
    }

    @discardableResult
    public func addObserver(_ observer: @escaping Observer) -> UUID {
        let id = UUID()
        state.withLock { $0.observers[id] = observer }
        return id
    }

    public func removeObserver(_ id: UUID) {
        state.withLock { _ = $0.observers.removeValue(forKey: id) }
    }

    public func flush() {
        let writer = writerBox.withLock { $0 }
        queue.sync { writer?.flush() }
    }
}
