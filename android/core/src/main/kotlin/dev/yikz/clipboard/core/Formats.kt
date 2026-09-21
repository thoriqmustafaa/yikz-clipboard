package dev.yikz.clipboard.core

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.text.Normalizer

object Uuid7 {
    val REGEX = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")

    fun isValid(value: String): Boolean = REGEX.matches(value)

    fun generate(unixMillis: Long = System.currentTimeMillis(), random: ByteArray = randomBytes(10)): String {
        require(random.size == 10) { "random part must be 10 bytes" }
        val b = ByteArray(16)
        for (i in 0..5) b[i] = (unixMillis ushr (8 * (5 - i))).toByte()
        random.copyInto(b, 6)
        b[6] = (0x70 or (b[6].toInt() and 0x0f)).toByte()
        b[8] = (0x80 or (b[8].toInt() and 0x3f)).toByte()
        val hex = Hex.encode(b)
        return "${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}"
    }
}

object Tokens {
    val REGEX = Regex("^yc_[A-Za-z0-9_-]{43}$")

    fun isValid(token: String): Boolean = REGEX.matches(token)
}

object Chunking {
    fun isInline(size: Long): Boolean = size <= Protocol.INLINE_MAX_BYTES

    fun chunkCount(size: Long): Int {
        require(size >= 1) { "size must be at least 1" }
        if (isInline(size)) return 0
        return ((size + Protocol.CHUNK_SIZE_BYTES - 1) / Protocol.CHUNK_SIZE_BYTES).toInt()
    }

    fun chunkPlainSize(size: Long, index: Int): Int {
        val count = chunkCount(size)
        require(index in 0 until count) { "chunk index out of range" }
        val start = index * Protocol.CHUNK_SIZE_BYTES
        return minOf(Protocol.CHUNK_SIZE_BYTES, size - start).toInt()
    }

    fun chunkSealedSize(size: Long, index: Int): Int = chunkPlainSize(size, index) + Protocol.SEAL_OVERHEAD

    fun totalSealedChunkBytes(size: Long): Long = size + chunkCount(size).toLong() * Protocol.SEAL_OVERHEAD
}

object Preview {
    fun of(text: String, maxCodePoints: Int = Protocol.PREVIEW_MAX_CODE_POINTS): String {
        var index = 0
        var count = 0
        while (index < text.length && count < maxCodePoints) {
            index += Character.charCount(text.codePointAt(index))
            count++
        }
        return if (index >= text.length) text else text.substring(0, index)
    }

    fun codePointCount(text: String): Int = text.codePointCount(0, text.length)
}

class ArchiveException(message: String) : Exception(message)

object Ycf1 {
    private val MAGIC = byteArrayOf(0x59, 0x43, 0x46, 0x31)

    class Entry(val name: String, val size: Long, val open: () -> InputStream)

    fun normalizeName(name: String): String = Normalizer.normalize(name, Normalizer.Form.NFC)

    fun nameError(name: String): String? {
        if (name.isEmpty()) return "empty"
        if (!Normalizer.isNormalized(name, Normalizer.Form.NFC)) return "not NFC"
        if (name == "." || name == "..") return "dot"
        for (c in name) {
            if (c == '/' || c == '\\') return "separator"
            if (c.code < 0x20 || c.code == 0x7f) return "control character"
        }
        val bytes = name.toByteArray(Charsets.UTF_8)
        if (bytes.size > 255) return "longer than 255 bytes"
        if (String(bytes, Charsets.UTF_8) != name) return "invalid UTF-16"
        return null
    }

    fun isValidName(name: String): Boolean = nameError(name) == null

    fun archiveSize(entries: List<Pair<String, Long>>): Long =
        8L + entries.sumOf { (name, size) -> 4L + normalizeName(name).toByteArray(Charsets.UTF_8).size + 8L + size }

    fun uniqueNames(names: List<String>): List<String> {
        val used = HashSet<String>()
        return names.map { raw ->
            val base = normalizeName(raw)
            var candidate = base
            var n = 2
            while (!used.add(candidate)) {
                val dot = base.lastIndexOf('.')
                candidate = if (dot > 0) "${base.substring(0, dot)} ($n)${base.substring(dot)}" else "$base ($n)"
                n++
            }
            candidate
        }
    }

    fun write(entries: List<Entry>, out: OutputStream) {
        if (entries.isEmpty() || entries.size > Protocol.MAX_FILES_PER_ITEM) throw ArchiveException("invalid file count")
        val seen = HashSet<String>()
        out.write(MAGIC)
        out.write(u32(entries.size.toLong()))
        val buffer = ByteArray(64 * 1024)
        for (entry in entries) {
            val name = normalizeName(entry.name)
            nameError(name)?.let { throw ArchiveException("invalid name: $it") }
            if (!seen.add(name)) throw ArchiveException("duplicate name")
            val nameBytes = name.toByteArray(Charsets.UTF_8)
            out.write(u32(nameBytes.size.toLong()))
            out.write(nameBytes)
            out.write(u64(entry.size))
            var remaining = entry.size
            entry.open().use { input ->
                while (remaining > 0) {
                    val n = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                    if (n < 0) throw ArchiveException("file shorter than declared size")
                    out.write(buffer, 0, n)
                    remaining -= n
                }
                if (input.read() >= 0) throw ArchiveException("file longer than declared size")
            }
        }
    }

    fun pack(files: List<Pair<String, ByteArray>>): ByteArray {
        val out = ByteArrayOutputStream()
        write(files.map { (name, data) -> Entry(name, data.size.toLong()) { ByteArrayInputStream(data) } }, out)
        return out.toByteArray()
    }

    fun read(input: InputStream, onFile: (name: String, size: Long, data: InputStream) -> Unit): Int {
        val header = readExact(input, 8)
        if (!header.copyOfRange(0, 4).contentEquals(MAGIC)) throw ArchiveException("bad magic")
        val count = readU32(header, 4)
        if (count == 0L || count > Protocol.MAX_FILES_PER_ITEM) throw ArchiveException("invalid file count")
        val seen = HashSet<String>()
        for (i in 0 until count.toInt()) {
            val nameLength = readU32(readExact(input, 4), 0)
            if (nameLength < 1 || nameLength > 255) throw ArchiveException("invalid name length")
            val name = decodeUtf8(readExact(input, nameLength.toInt()))
            nameError(name)?.let { throw ArchiveException("invalid name: $it") }
            if (!seen.add(name)) throw ArchiveException("duplicate name")
            val dataLength = ByteBuffer.wrap(readExact(input, 8)).long
            if (dataLength < 0) throw ArchiveException("invalid data length")
            val bounded = BoundedInputStream(input, dataLength)
            onFile(name, dataLength, bounded)
            bounded.drain()
        }
        if (input.read() >= 0) throw ArchiveException("trailing bytes")
        return count.toInt()
    }

    fun unpack(archive: ByteArray): List<Pair<String, ByteArray>> {
        val out = ArrayList<Pair<String, ByteArray>>()
        read(ByteArrayInputStream(archive)) { name, _, data -> out += name to data.readBytes() }
        return out
    }

    fun listNames(archive: ByteArray): List<Pair<String, Long>> {
        val out = ArrayList<Pair<String, Long>>()
        read(ByteArrayInputStream(archive)) { name, size, _ -> out += name to size }
        return out
    }

    private fun decodeUtf8(bytes: ByteArray): String = try {
        Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes))
            .toString()
    } catch (_: CharacterCodingException) {
        throw ArchiveException("name is not valid UTF-8")
    }

    private fun readExact(input: InputStream, count: Int): ByteArray {
        val out = ByteArray(count)
        var read = 0
        while (read < count) {
            val n = input.read(out, read, count - read)
            if (n < 0) throw ArchiveException("truncated archive")
            read += n
        }
        return out
    }

    private fun readU32(bytes: ByteArray, offset: Int): Long =
        ((bytes[offset].toLong() and 0xff) shl 24) or ((bytes[offset + 1].toLong() and 0xff) shl 16) or
            ((bytes[offset + 2].toLong() and 0xff) shl 8) or (bytes[offset + 3].toLong() and 0xff)

    private fun u32(value: Long): ByteArray =
        byteArrayOf((value ushr 24).toByte(), (value ushr 16).toByte(), (value ushr 8).toByte(), value.toByte())

    private fun u64(value: Long): ByteArray = ByteBuffer.allocate(8).putLong(value).array()

    private class BoundedInputStream(private val source: InputStream, private var remaining: Long) : InputStream() {
        override fun read(): Int {
            if (remaining <= 0) return -1
            val b = source.read()
            if (b < 0) throw ArchiveException("truncated archive")
            remaining--
            return b
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (remaining <= 0) return -1
            val n = source.read(b, off, minOf(len.toLong(), remaining).toInt())
            if (n < 0) throw ArchiveException("truncated archive")
            remaining -= n
            return n
        }

        override fun available(): Int = minOf(remaining, source.available().toLong()).toInt()

        fun drain() {
            val buffer = ByteArray(16 * 1024)
            while (remaining > 0) {
                if (read(buffer) < 0) break
            }
        }

        override fun close() {}
    }
}

class CountingOutputStream(private val target: OutputStream) : OutputStream() {
    var count: Long = 0
        private set

    override fun write(b: Int) {
        target.write(b)
        count++
    }

    override fun write(b: ByteArray, off: Int, len: Int) {
        target.write(b, off, len)
        count += len
    }

    override fun flush() = target.flush()
    override fun close() = target.close()
}

fun InputStream.readFully(buffer: ByteArray, length: Int): Int {
    var read = 0
    while (read < length) {
        val n = read(buffer, read, length - read)
        if (n < 0) break
        read += n
    }
    return read
}

fun InputStream.skipFully(count: Long) {
    var remaining = count
    val buffer = ByteArray(64 * 1024)
    while (remaining > 0) {
        val skipped = skip(remaining)
        if (skipped > 0) {
            remaining -= skipped
            continue
        }
        val n = read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
        if (n < 0) throw EOFException("stream ended early")
        remaining -= n
    }
}
