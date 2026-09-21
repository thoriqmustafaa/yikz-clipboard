package dev.yikz.clipboard.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream

class FormatVectorsTest {
    @Test
    fun uuidv7Generation() {
        val v = Vectors.load("uuidv7.json")
        assertEquals(Uuid7.REGEX.pattern, v.str("regex"))
        for (case in v.arr("generate").map { it.o() }) {
            assertEquals(case.str("expected"), Uuid7.generate(case.num("unix_ms"), hex(case.str("random_hex"))))
        }
        for (valid in v.arr("valid")) assertTrue(Uuid7.isValid(valid.jsonPrimitiveContent()))
        for (invalid in v.arr("invalid").map { it.o() }) assertFalse(invalid.str("reason"), Uuid7.isValid(invalid.str("value")))
        val generated = Uuid7.generate()
        assertTrue(Uuid7.isValid(generated))
        val millis = java.lang.Long.parseLong(generated.replace("-", "").substring(0, 12), 16)
        assertTrue(kotlin.math.abs(millis - System.currentTimeMillis()) < 5_000)
    }

    @Test
    fun tokens() {
        val v = Vectors.load("token.json")
        assertEquals(Tokens.REGEX.pattern, v.str("regex"))
        for (case in v.arr("cases").map { it.o() }) {
            val token = case.str("token")
            assertTrue(Tokens.isValid(token))
            assertEquals(46, token.length)
            assertEquals("yc_" + java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(hex(case.str("random_hex"))), token)
            assertEquals(case.str("token_hash"), Hashing.sha256Hex(token.toByteArray(Charsets.US_ASCII)))
        }
        assertFalse(Tokens.isValid("yc_invalidtokeninvalidtokeninvalidtokeninval"))
    }

    @Test
    fun chunking() {
        val v = Vectors.load("chunking.json")
        assertEquals(Protocol.INLINE_MAX_BYTES, v.num("inline_max_bytes"))
        assertEquals(Protocol.CHUNK_SIZE_BYTES, v.num("chunk_size_bytes"))
        assertEquals(Protocol.SEAL_OVERHEAD.toLong(), v.num("seal_overhead_bytes"))
        for (case in v.arr("cases").map { it.o() }) {
            val size = case.num("size")
            assertEquals("inline $size", case.bool("inline"), Chunking.isInline(size))
            assertEquals("count $size", case.num("chunk_count"), Chunking.chunkCount(size).toLong())
            if (case.bool("inline")) {
                assertEquals(case.num("payload_sealed_length"), size + Protocol.SEAL_OVERHEAD)
            } else {
                val last = case.num("last_chunk_index").toInt()
                assertEquals(last, Chunking.chunkCount(size) - 1)
                assertEquals(Protocol.CHUNK_SIZE_BYTES, case.num("full_chunk_plaintext_size"))
                assertEquals(Protocol.MAX_CHUNK_BODY_BYTES, case.num("full_chunk_sealed_length"))
                if (last > 0) assertEquals(case.num("full_chunk_sealed_length"), Chunking.chunkSealedSize(size, 0).toLong())
                assertEquals(case.num("last_chunk_plaintext_size"), Chunking.chunkPlainSize(size, last).toLong())
                assertEquals(case.num("last_chunk_sealed_length"), Chunking.chunkSealedSize(size, last).toLong())
                assertEquals(case.num("total_chunk_sealed_bytes"), Chunking.totalSealedChunkBytes(size))
            }
        }
    }

    @Test
    fun preview() {
        val v = Vectors.load("preview.json")
        assertEquals(500L, v.num("max_code_points"))
        for (case in v.arr("cases").map { it.o() }) {
            val input = case.str("input")
            assertEquals(case.num("input_code_points"), Preview.codePointCount(input).toLong())
            assertEquals(case.num("input_utf16_units"), input.length.toLong())
            val preview = Preview.of(input)
            assertEquals(case.str("name"), case.str("expected"), preview)
            assertEquals(case.num("expected_code_points"), Preview.codePointCount(preview).toLong())
        }
    }

    @Test
    fun ycf1ThreeFiles() {
        val case = Vectors.load("files_archive.json").arr("cases").map { it.o() }.first { it.str("name") == "three_files" }
        val files = case.arr("files").map { it.o() }.map { f ->
            assertEquals(f.str("name_utf8_hex"), Hex.encode(f.str("name").toByteArray(Charsets.UTF_8)))
            f.str("name") to hex(f.str("data_hex"))
        }
        val archive = Ycf1.pack(files)
        assertEquals(case.str("archive_hex"), Hex.encode(archive))
        assertEquals(case.num("archive_size"), archive.size.toLong())
        assertEquals(case.num("archive_size"), Ycf1.archiveSize(files.map { it.first to it.second.size.toLong() }))
        assertEquals(case.str("archive_sha256"), Hashing.sha256Hex(archive))
        val unpacked = Ycf1.unpack(archive)
        assertEquals(files.map { it.first }, unpacked.map { it.first })
        for (i in files.indices) assertArrayEquals(files[i].second, unpacked[i].second)
    }

    @Test
    fun ycf1LargeFileStreaming() {
        val case = Vectors.load("files_archive.json").arr("cases").map { it.o() }.first { it.str("name") == "single_large_file_header" }
        val file = case.arr("files")[0].o()
        assertEquals("byte i = i mod 251", file.str("data_rule"))
        val size = file.num("size")
        val out = ByteArrayOutputStream()
        val entry = Ycf1.Entry(file.str("name"), size) { PatternStream(size) }
        Ycf1.write(listOf(entry), out)
        val archive = out.toByteArray()
        assertEquals(case.num("archive_size"), archive.size.toLong())
        assertEquals(case.num("archive_size"), Ycf1.archiveSize(listOf(file.str("name") to size)))
        val prefix = hex(case.str("archive_prefix_hex"))
        assertArrayEquals(prefix, archive.copyOfRange(0, prefix.size))
        assertEquals(case.str("archive_sha256"), Hashing.sha256Hex(archive))
        var seen = 0L
        Ycf1.read(ByteArrayInputStream(archive)) { name, length, data ->
            assertEquals("pattern.bin", name)
            assertEquals(size, length)
            seen = data.readBytes().size.toLong()
        }
        assertEquals(size, seen)
        val streamed = UploadSource.files(listOf(entry)).open().use { it.readBytes() }
        assertEquals(case.str("archive_sha256"), Hashing.sha256Hex(streamed))
    }

    @Test
    fun ycf1RejectsInvalidNames() {
        for (case in Vectors.load("files_archive.json").arr("invalid_names").map { it.o() }) {
            assertFalse(case.str("reason"), Ycf1.isValidName(case.str("name")))
            try {
                Ycf1.pack(listOf(case.str("name") to byteArrayOf(1)))
                fail("packed invalid name ${case.str("reason")}")
            } catch (_: ArchiveException) {
            }
        }
        assertFalse(Ycf1.isValidName("é.txt"))
        assertTrue(Ycf1.isValidName("é.txt"))
        assertTrue(Ycf1.isValidName("x".repeat(255)))
    }

    @Test
    fun ycf1RejectsMalformedArchives() {
        val good = Ycf1.pack(listOf("a.txt" to byteArrayOf(1, 2, 3)))
        val bad = listOf(
            good + byteArrayOf(0),
            good.copyOf(good.size - 1),
            byteArrayOf(0x59, 0x43, 0x46, 0x32) + good.copyOfRange(4, good.size),
            good.copyOf(4) + byteArrayOf(0, 0, 0, 0),
            Ycf1.pack(listOf("a" to byteArrayOf(1))).let { it.copyOf(8) + byteArrayOf(0, 0, 0, 1, 0x2f) + it.copyOfRange(13, it.size) },
        )
        for ((i, archive) in bad.withIndex()) {
            try {
                Ycf1.unpack(archive)
                fail("accepted malformed archive $i")
            } catch (_: ArchiveException) {
            }
        }
        try {
            Ycf1.pack(listOf("a" to byteArrayOf(1), "a" to byteArrayOf(2)))
            fail("accepted duplicate")
        } catch (_: ArchiveException) {
        }
        assertEquals(listOf("a.txt", "a (2).txt", "b"), Ycf1.uniqueNames(listOf("a.txt", "a.txt", "b")))
    }

    @Test
    fun timestamps() {
        assertEquals("2026-09-21T10:00:01.250Z", Timestamps.format(Timestamps.parse("2026-09-21T10:00:01.250Z")))
        assertEquals(Timestamps.parse("2026-09-21T10:00:01.250Z"), Timestamps.parse("2026-09-21T17:00:01.25+07:00"))
        assertEquals(Timestamps.parse("2026-09-21T10:00:01Z"), Timestamps.parse("2026-09-21t10:00:01z"))
    }
}

private fun kotlinx.serialization.json.JsonElement.jsonPrimitiveContent(): String =
    (this as kotlinx.serialization.json.JsonPrimitive).content

class PatternStream(private val size: Long) : java.io.InputStream() {
    private var position = 0L

    override fun read(): Int {
        if (position >= size) return -1
        return ((position++ % 251).toInt())
    }

    override fun read(b: ByteArray, off: Int, len: Int): Int {
        if (position >= size) return -1
        val n = minOf(len.toLong(), size - position).toInt()
        for (i in 0 until n) b[off + i] = ((position + i) % 251).toByte()
        position += n
        return n
    }
}
