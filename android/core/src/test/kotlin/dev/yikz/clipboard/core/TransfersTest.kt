package dev.yikz.clipboard.core

import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.nio.file.Files

class TransfersTest {
    private fun harness(): Triple<FakeApi, EchoGuard, Transfers> {
        val api = FakeApi().apply { keyCheck = Fixtures.key.keyCheck }
        val echo = EchoGuard()
        val store = MemoryStore()
        val transfers = Transfers(api, Fixtures.codec, echo, { store.newestContentHash() })
        return Triple(api, echo, transfers)
    }

    @Test
    fun inlineTextUploadIsDecryptableAndDeduped() = runTest {
        val (api, echo, transfers) = harness()
        val result = transfers.upload(UploadSource.text("hello there"))
        assertTrue(result is UploadResult.Sent)
        val header = (result as UploadResult.Sent).header
        assertTrue(Uuid7.isValid(header.id))
        assertEquals(0, header.chunkCount)
        val item = Fixtures.codec.decode(api.committed.single())
        assertEquals("hello there", item.meta!!.preview)
        assertEquals(Protocol.MIME_TEXT, item.meta!!.mime)
        assertArrayEquals("hello there".toByteArray(), transfers.inlineContent(item, api.committed.single().payload))
        assertArrayEquals("hello there".toByteArray(), transfers.inlineContent(item, null))
        assertTrue(header.contentHash in echo)
        assertEquals(UploadResult.Skipped("recently synced"), transfers.upload(UploadSource.text("hello there")))
        assertTrue(transfers.upload(UploadSource.text("hello there"), force = true) is UploadResult.Sent)
    }

    @Test
    fun inlineRetriesTransientErrors() = runTest {
        val (api, _, transfers) = harness()
        api.transientFailures["createItem"] = 2
        assertTrue(transfers.upload(UploadSource.text("retry me")) is UploadResult.Sent)
        assertEquals(3, api.calls.count { it.startsWith("create ") })
    }

    @Test
    fun keyCheckMissingSurfaces() = runTest {
        val (api, _, transfers) = harness()
        api.keyCheck = null
        try {
            transfers.upload(UploadSource.text("x"))
            error("expected failure")
        } catch (e: ApiException) {
            assertEquals("key_check_missing", e.code)
        }
    }

    @Test
    fun chunkedFilesUploadRoundTrips() = runTest {
        val (api, _, transfers) = harness()
        val big = ByteArray(9_000_000) { (it * 31).toByte() }
        val small = "notes".toByteArray()
        val source = UploadSource.files(
            listOf(
                Ycf1.Entry("big.bin", big.size.toLong()) { ByteArrayInputStream(big) },
                Ycf1.Entry("notes.txt", small.size.toLong()) { ByteArrayInputStream(small) },
                Ycf1.Entry("notes.txt", small.size.toLong()) { ByteArrayInputStream(small) },
            ),
        )
        api.transientFailures["uploadChunk"] = 2
        api.dropChunksOnce += 1
        var progress = 0L
        val result = transfers.upload(source) { done, _ -> progress = done } as UploadResult.Sent
        assertEquals(source.size, progress)
        assertEquals(3, result.header.chunkCount)
        assertTrue(api.calls.count { it.startsWith("commit ") } == 2)
        val item = Fixtures.codec.decode(result.header)
        assertEquals(listOf("big.bin", "notes.txt", "notes (2).txt"), item.meta!!.files!!.map { it.name })
        assertEquals("big.bin\nnotes.txt\nnotes (2).txt", item.meta!!.preview)
        val dir = Files.createTempDirectory("yikz-up").toFile()
        val target = File(dir, "download")
        transfers.download(item, target)
        val files = Ycf1.unpack(target.readBytes())
        assertArrayEquals(big, files[0].second)
        assertArrayEquals(small, files[2].second)
        dir.deleteRecursively()
    }

    @Test
    fun imageUploadIncludesThumbnail() = runTest {
        val (api, _, transfers) = harness()
        val png = ByteArray(300_000) { it.toByte() }
        val thumb = ByteArray(2_000) { 7 }
        val result = transfers.upload(UploadSource.image(png, 800, 600, thumb)) as UploadResult.Sent
        assertEquals(1, result.header.chunkCount)
        val sealedThumb = api.thumbs.getValue(result.header.id)
        assertArrayEquals(thumb, Fixtures.codec.decryptThumb(result.header.id, sealedThumb))
        val meta = Fixtures.codec.decode(result.header).meta!!
        assertEquals(ImageDims(800, 600), meta.image)
        assertEquals("", meta.preview)
    }
}
