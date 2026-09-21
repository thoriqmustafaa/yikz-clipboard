import Foundation

public enum ArchiveError: Error, Sendable, Equatable, CustomStringConvertible {
    case badMagic
    case truncated
    case trailingBytes
    case badFileCount(UInt32)
    case invalidName(String)
    case duplicateName(String)
    case io(String)
    case nothingToSend

    public var description: String {
        switch self {
        case .badMagic: return "not a YCF1 archive"
        case .truncated: return "archive is truncated"
        case .trailingBytes: return "archive has trailing bytes"
        case .badFileCount(let n): return "invalid file count \(n)"
        case .invalidName(let r): return "invalid file name: \(r)"
        case .duplicateName(let n): return "duplicate file name \(n)"
        case .io(let m): return m
        case .nothingToSend: return "no regular files to send"
        }
    }
}

public struct ArchiveFileInfo: Codable, Sendable, Equatable, Hashable {
    public var name: String
    public var size: Int64

    public init(name: String, size: Int64) {
        self.name = name
        self.size = size
    }
}

public enum YCF1 {
    public static let magic = Data([0x59, 0x43, 0x46, 0x31])
    static let blockSize = 1 << 20

    public struct Entry: Sendable, Equatable {
        public var name: String
        public var data: Data

        public init(name: String, data: Data) {
            self.name = name
            self.data = data
        }
    }

    public static func validateName(_ name: String) throws {
        let bytes = Array(name.utf8)
        if bytes.isEmpty { throw ArchiveError.invalidName("empty") }
        if bytes.count > 255 { throw ArchiveError.invalidName("longer than 255 bytes") }
        if name == "." || name == ".." { throw ArchiveError.invalidName("dot name") }
        for scalar in name.unicodeScalars {
            let v = scalar.value
            if v == 0x2f || v == 0x5c { throw ArchiveError.invalidName("path separator") }
            if v < 0x20 || v == 0x7f { throw ArchiveError.invalidName("control character") }
        }
        if Array(name.precomposedStringWithCanonicalMapping.utf8) != bytes {
            throw ArchiveError.invalidName("not NFC")
        }
    }

    public static func sanitizedName(_ raw: String) -> String {
        var scalars = String.UnicodeScalarView()
        for s in raw.precomposedStringWithCanonicalMapping.unicodeScalars {
            let v = s.value
            if v == 0x2f || v == 0x5c || v < 0x20 || v == 0x7f {
                scalars.append("_")
            } else {
                scalars.append(s)
            }
        }
        var name = String(scalars)
        if name.isEmpty || name == "." || name == ".." { name = "file" }
        while name.utf8.count > 255 {
            name.removeFirst()
        }
        return name
    }

    public static func uniqueNames(_ names: [String]) -> [String] {
        var used = Set<String>()
        var out: [String] = []
        for original in names {
            var candidate = sanitizedName(original)
            if used.contains(candidate) {
                let ext = (candidate as NSString).pathExtension
                let stem = (candidate as NSString).deletingPathExtension
                var n = 2
                repeat {
                    candidate = ext.isEmpty ? "\(stem) (\(n))" : "\(stem) (\(n)).\(ext)"
                    n += 1
                } while used.contains(candidate)
            }
            used.insert(candidate)
            out.append(candidate)
        }
        return out
    }

    static func be32(_ v: UInt32) -> Data {
        Data([UInt8(v >> 24 & 0xff), UInt8(v >> 16 & 0xff), UInt8(v >> 8 & 0xff), UInt8(v & 0xff)])
    }

    static func be64(_ v: UInt64) -> Data {
        var d = Data(capacity: 8)
        for i in 0..<8 {
            d.append(UInt8(truncatingIfNeeded: v >> UInt64(8 * (7 - i))))
        }
        return d
    }

    public static func encode(_ entries: [Entry]) throws -> Data {
        guard !entries.isEmpty, entries.count <= Proto.maxFilesPerItem else {
            throw ArchiveError.badFileCount(UInt32(entries.count))
        }
        var seen = Set<Data>()
        var out = magic
        out.append(be32(UInt32(entries.count)))
        for e in entries {
            try validateName(e.name)
            let nameBytes = Data(e.name.utf8)
            guard seen.insert(nameBytes).inserted else { throw ArchiveError.duplicateName(e.name) }
            out.append(be32(UInt32(nameBytes.count)))
            out.append(nameBytes)
            out.append(be64(UInt64(e.data.count)))
            out.append(e.data)
        }
        return out
    }

    public static func decode(_ data: Data) throws -> [Entry] {
        var reader = DataReader(data: data)
        let entries = try parse(reader: &reader) { name, length, r in
            Entry(name: name, data: try r.read(Int(length)))
        }
        guard reader.isAtEnd else { throw ArchiveError.trailingBytes }
        return entries
    }

    public static func listing(_ data: Data) throws -> [ArchiveFileInfo] {
        try decode(data).map { ArchiveFileInfo(name: $0.name, size: Int64($0.data.count)) }
    }

    static func parse<R: ByteReading, T>(reader: inout R, body: (String, UInt64, inout R) throws -> T) throws -> [T] {
        guard try reader.read(4) == magic else { throw ArchiveError.badMagic }
        let count = try reader.readBE32()
        guard count >= 1, count <= UInt32(Proto.maxFilesPerItem) else { throw ArchiveError.badFileCount(count) }
        var seen = Set<Data>()
        var out: [T] = []
        for _ in 0..<count {
            let nameLen = try reader.readBE32()
            guard nameLen >= 1, nameLen <= 255 else { throw ArchiveError.invalidName("bad name length") }
            let nameBytes = try reader.read(Int(nameLen))
            guard let name = String(data: nameBytes, encoding: .utf8) else { throw ArchiveError.invalidName("not UTF-8") }
            try validateName(name)
            guard seen.insert(nameBytes).inserted else { throw ArchiveError.duplicateName(name) }
            let length = try reader.readBE64()
            out.append(try body(name, length, &reader))
        }
        return out
    }

    public struct SourceFile: Sendable {
        public var name: String
        public var url: URL

        public init(name: String, url: URL) {
            self.name = name
            self.url = url
        }
    }

    public static func write(files: [SourceFile], to destination: URL) throws -> [ArchiveFileInfo] {
        guard !files.isEmpty, files.count <= Proto.maxFilesPerItem else {
            throw ArchiveError.badFileCount(UInt32(files.count))
        }
        FileManager.default.createFile(atPath: destination.path, contents: nil)
        guard let out = try? FileHandle(forWritingTo: destination) else {
            throw ArchiveError.io("cannot create \(destination.lastPathComponent)")
        }
        defer { try? out.close() }
        var infos: [ArchiveFileInfo] = []
        var seen = Set<String>()
        do {
            try out.write(contentsOf: magic + be32(UInt32(files.count)))
            for f in files {
                try validateName(f.name)
                guard seen.insert(f.name).inserted else { throw ArchiveError.duplicateName(f.name) }
                let attrs = try FileManager.default.attributesOfItem(atPath: f.url.path)
                let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
                let nameBytes = Data(f.name.utf8)
                try out.write(contentsOf: be32(UInt32(nameBytes.count)) + nameBytes + be64(UInt64(size)))
                let input = try FileHandle(forReadingFrom: f.url)
                var written: Int64 = 0
                while written < size {
                    let want = Int(min(Int64(blockSize), size - written))
                    guard let block = try input.read(upToCount: want), !block.isEmpty else { break }
                    try out.write(contentsOf: block)
                    written += Int64(block.count)
                }
                try? input.close()
                guard written == size else { throw ArchiveError.io("\(f.name) changed while reading") }
                infos.append(ArchiveFileInfo(name: f.name, size: size))
            }
        } catch let e as ArchiveError {
            throw e
        } catch {
            throw ArchiveError.io(error.localizedDescription)
        }
        return infos
    }

    public static func extract(archive: URL, into directory: URL) throws -> [URL] {
        guard let handle = try? FileHandle(forReadingFrom: archive) else {
            throw ArchiveError.io("cannot open archive")
        }
        defer { try? handle.close() }
        var reader = FileReader(handle: handle)
        let urls = try extract(reader: &reader, into: directory)
        guard try reader.isAtEnd() else { throw ArchiveError.trailingBytes }
        return urls
    }

    public static func extract(data: Data, into directory: URL) throws -> [URL] {
        var reader = DataReader(data: data)
        let urls = try extract(reader: &reader, into: directory)
        guard reader.isAtEnd else { throw ArchiveError.trailingBytes }
        return urls
    }

    static func extract<R: ByteReading>(reader: inout R, into directory: URL) throws -> [URL] {
        let fm = FileManager.default
        try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
        var usedLower = Set<String>()
        return try parse(reader: &reader) { name, length, r in
            let finalName = uniqueCaseInsensitive(name, used: &usedLower)
            let url = directory.appendingPathComponent(finalName, isDirectory: false)
            fm.createFile(atPath: url.path, contents: nil)
            guard let out = try? FileHandle(forWritingTo: url) else { throw ArchiveError.io("cannot write \(finalName)") }
            defer { try? out.close() }
            var remaining = length
            while remaining > 0 {
                let n = Int(min(UInt64(blockSize), remaining))
                let block = try r.read(n)
                do { try out.write(contentsOf: block) } catch { throw ArchiveError.io(error.localizedDescription) }
                remaining -= UInt64(n)
            }
            return url
        }
    }

    static func uniqueCaseInsensitive(_ name: String, used: inout Set<String>) -> String {
        var candidate = name
        var n = 2
        let ext = (name as NSString).pathExtension
        let stem = (name as NSString).deletingPathExtension
        while used.contains(candidate.lowercased()) {
            candidate = ext.isEmpty ? "\(stem) (\(n))" : "\(stem) (\(n)).\(ext)"
            n += 1
        }
        used.insert(candidate.lowercased())
        return candidate
    }
}

protocol ByteReading {
    mutating func read(_ count: Int) throws -> Data
}

extension ByteReading {
    mutating func readBE32() throws -> UInt32 {
        let d = try read(4)
        return d.reduce(0) { $0 << 8 | UInt32($1) }
    }

    mutating func readBE64() throws -> UInt64 {
        let d = try read(8)
        return d.reduce(0) { $0 << 8 | UInt64($1) }
    }
}

struct DataReader: ByteReading {
    let data: Data
    var offset: Int

    init(data: Data) {
        self.data = data
        self.offset = data.startIndex
    }

    var isAtEnd: Bool { offset == data.endIndex }

    mutating func read(_ count: Int) throws -> Data {
        guard count >= 0, data.endIndex - offset >= count else { throw ArchiveError.truncated }
        let out = data.subdata(in: offset..<(offset + count))
        offset += count
        return out
    }
}

struct FileReader: ByteReading {
    let handle: FileHandle
    var buffer = Data()

    init(handle: FileHandle) {
        self.handle = handle
    }

    mutating func fill(_ count: Int) throws {
        while buffer.count < count {
            let want = max(count - buffer.count, 1 << 20)
            let chunk: Data?
            do { chunk = try handle.read(upToCount: want) } catch { throw ArchiveError.io(error.localizedDescription) }
            guard let chunk, !chunk.isEmpty else { throw ArchiveError.truncated }
            buffer.append(chunk)
        }
    }

    mutating func read(_ count: Int) throws -> Data {
        try fill(count)
        let out = buffer.prefix(count)
        buffer = Data(buffer.dropFirst(count))
        return Data(out)
    }

    mutating func isAtEnd() throws -> Bool {
        if !buffer.isEmpty { return false }
        let more = try? handle.read(upToCount: 1)
        return more == nil || more!.isEmpty
    }
}
