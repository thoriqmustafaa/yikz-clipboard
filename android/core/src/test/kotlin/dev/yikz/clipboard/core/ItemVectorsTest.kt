package dev.yikz.clipboard.core

import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.fail
import org.junit.Test
import java.io.File
import java.nio.file.Files

class ItemVectorsTest {
    private val items = Vectors.load("items.json").arr("items").map { it.o() }
    private fun item(name: String) = items.first { it.str("name") == name }

    @Test
    fun everyItemDecryptsAndVerifies() {
        for (v in items) {
            val name = v.str("name")
            val key = MasterKey(hex(v.str("key_hex")))
            val codec = ItemCodec(key)
            val id = v.str("id")
            assertEquals(v.str("meta_aad_utf8"), String(Aad.meta(id)))
            val header = ProtocolJson.decodeFromJsonElement(ItemHeader.serializer(), v.obj("header"))
            assertEquals(id, header.id)
            assertEquals(v.str("kind"), header.kind)
            assertEquals(v.num("size"), header.size)
            assertEquals(v.num("chunk_count").toInt(), header.chunkCount)
            assertEquals(v.str("content_hash"), header.contentHash)
            assertEquals(v.num("stored_bytes"), header.storedBytes)
            assertEquals(v.str("meta_sealed_b64"), header.meta)
            val cached = codec.decode(header)
            assertNull(name, cached.metaError)
            val expectedMeta = ProtocolJson.decodeFromJsonElement(ItemMeta.serializer(), v.obj("meta"))
            assertEquals(name, expectedMeta, cached.meta)
            assertEquals(name, v.str("meta_plaintext_utf8"), String(key.open(Aad.meta(id), StrictBase64.decode(header.meta)), Charsets.UTF_8))
            assertEquals(name, v.str("meta_plaintext_utf8"), String(codec.encodeMeta(expectedMeta), Charsets.UTF_8))
            assertEquals(name, v.str("meta_sealed_b64"), codec.sealMeta(id, expectedMeta, hex(v.str("meta_nonce_hex"))))
            assertEquals(Timestamps.parse(header.createdAt), cached.createdAtMs)

            if (header.chunkCount == 0) {
                val content = hex(v.str("content_hex"))
                assertEquals(v.str("payload_aad_utf8"), String(Aad.payload(id)))
                assertEquals(v.str("payload_sealed_b64"), header.payload)
                assertArrayEquals(name, content, codec.decryptPayload(id, header.payload!!))
                assertEquals(v.str("payload_sealed_b64"), codec.sealPayload(id, content, hex(v.str("payload_nonce_hex"))))
                assertEquals(v.str("content_sha256"), Hashing.sha256Hex(content))
                assertEquals(v.str("content_hash"), key.contentHash(content))
                codec.verify(cached, content)
                v.strOrNull("content_utf8")?.let { assertEquals(it, String(content, Charsets.UTF_8)) }
                val payloadLength = StrictBase64.decode(header.payload).size
                assertEquals(content.size + 28, payloadLength)
            }
            if (v.containsKey("thumb_sealed_b64")) {
                assertEquals(v.str("thumb_aad_utf8"), String(Aad.thumb(id)))
                val thumb = codec.decryptThumb(id, StrictBase64.decode(v.str("thumb_sealed_b64")))
                assertEquals(v.str("thumb_plaintext_hex"), Hex.encode(thumb))
                assertEquals(v.str("thumb_sealed_b64"), StrictBase64.encode(codec.sealThumb(id, thumb, hex(v.str("thumb_nonce_hex")))))
            }
        }
    }

    @Test
    fun filesItemArchiveMatchesMeta() {
        val v = item("files")
        val archive = hex(v.str("content_hex"))
        val meta = ProtocolJson.decodeFromJsonElement(ItemMeta.serializer(), v.obj("meta"))
        assertEquals(meta.files!!.map { it.name to it.size }, Ycf1.listNames(archive))
        assertEquals(Preview.of(meta.files!!.joinToString("\n") { it.name }), meta.preview)
        assertEquals(meta, filesMeta(meta.files!!, meta.sha256, meta.sourceApp))
    }

    @Test
    fun imageAndTextMetaBuilders() {
        val text = item("text")
        val textMetaExpected = ProtocolJson.decodeFromJsonElement(ItemMeta.serializer(), text.obj("meta"))
        assertEquals(textMetaExpected, textMeta(text.str("content_utf8"), text.str("content_sha256"), "Safari"))
        val image = item("image")
        val imageMetaExpected = ProtocolJson.decodeFromJsonElement(ItemMeta.serializer(), image.obj("meta"))
        assertEquals(imageMetaExpected, imageMeta(4, 3, image.str("content_sha256")))
    }

    @Test
    fun chunkedItemSealsAndDownloads() = runTest {
        val v = item("large")
        val key = MasterKey(hex(v.str("key_hex")))
        val codec = ItemCodec(key)
        val id = v.str("id")
        val content = patternArchive()
        assertEquals(v.num("size"), content.size.toLong())
        assertEquals(v.str("content_sha256"), Hashing.sha256Hex(content))
        assertEquals(v.str("content_hash"), key.contentHash(content))
        val chunks = v.arr("chunks").map { it.o() }
        assertEquals(3, chunks.size)
        val sealedChunks = HashMap<Int, ByteArray>()
        for (c in chunks) {
            val index = c.num("index").toInt()
            assertEquals(c.str("aad_utf8"), String(Aad.chunk(id, index, 3)))
            val start = index * Protocol.CHUNK_SIZE_BYTES.toInt()
            val size = Chunking.chunkPlainSize(content.size.toLong(), index)
            assertEquals(c.num("plaintext_size"), size.toLong())
            val plain = content.copyOfRange(start, start + size)
            assertEquals(c.str("plaintext_sha256"), Hashing.sha256Hex(plain))
            val sealed = key.seal(Aad.chunk(id, index, 3), plain, hex(c.str("nonce_hex")))
            assertEquals(c.num("sealed_size"), sealed.size.toLong())
            assertEquals(c.str("sealed_sha256"), Hashing.sha256Hex(sealed))
            assertEquals(c.str("sealed_prefix_hex"), Hex.encode(sealed.copyOfRange(0, 44)))
            assertEquals(c.str("sealed_suffix_hex"), Hex.encode(sealed.copyOfRange(sealed.size - 32, sealed.size)))
            assertArrayEquals(plain, codec.decryptChunk(id, index, 3, sealed))
            sealedChunks[index] = sealed
        }
        val header = ProtocolJson.decodeFromJsonElement(ItemHeader.serializer(), v.obj("header"))
        assertNull(header.payload)
        val cached = codec.decode(header)
        val api = FakeApi().apply { this.chunks[id] = sealedChunks }
        val transfers = Transfers(api, codec, EchoGuard(), { null })
        val dir = Files.createTempDirectory("yikz").toFile()
        val target = File(dir, "out.bin")
        var lastProgress = 0L
        transfers.download(cached, target) { done, _ -> lastProgress = done }
        assertEquals(content.size.toLong(), lastProgress)
        assertEquals(v.str("content_sha256"), Hashing.sha256Hex(target.readBytes()))

        api.chunks[id] = HashMap(sealedChunks).apply { put(1, sealedChunks[2]!!) }
        val bad = File(dir, "bad.bin")
        try {
            transfers.download(cached, bad)
            fail("accepted swapped chunk")
        } catch (_: IntegrityException) {
        } catch (_: CryptoException) {
        }
        assertEquals(false, bad.exists())
        dir.deleteRecursively()
    }

    @Test
    fun tamperedInlinePayloadIsRejected() {
        val v = item("text")
        val codec = ItemCodec(primaryKey)
        val header = ProtocolJson.decodeFromJsonElement(ItemHeader.serializer(), v.obj("header"))
        val cached = codec.decode(header).copy(contentHash = "0".repeat(64))
        try {
            codec.verify(cached, codec.decryptPayload(header.id, header.payload!!))
            fail("accepted wrong content hash")
        } catch (_: IntegrityException) {
        }
        val moved = codec.decode(header.copy(id = item("image").str("id")))
        assertEquals(null, moved.meta)
    }
}
