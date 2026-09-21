package dev.yikz.clipboard.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.io.ByteArrayInputStream
import java.io.File
import java.io.InputStream
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.coroutineContext
import kotlin.random.Random

fun interface Progress {
    fun update(done: Long, total: Long)
}

class UploadSource(
    val kind: String,
    val size: Long,
    val open: () -> InputStream,
    val buildMeta: (sha256: String) -> ItemMeta,
    val thumbnailJpeg: ByteArray? = null,
) {
    companion object {
        fun text(text: String, sourceApp: String? = null): UploadSource {
            val bytes = text.toByteArray(Charsets.UTF_8)
            return UploadSource(Kind.TEXT, bytes.size.toLong(), { ByteArrayInputStream(bytes) }, { textMeta(text, it, sourceApp) })
        }

        fun image(png: ByteArray, width: Int, height: Int, thumbnailJpeg: ByteArray?, sourceApp: String? = null) = UploadSource(
            Kind.IMAGE,
            png.size.toLong(),
            { ByteArrayInputStream(png) },
            { imageMeta(width, height, it, sourceApp) },
            thumbnailJpeg,
        )

        fun files(entries: List<Ycf1.Entry>, sourceApp: String? = null): UploadSource {
            val named = Ycf1.uniqueNames(entries.map { it.name }).zip(entries) { name, e -> Ycf1.Entry(name, e.size, e.open) }
            val size = Ycf1.archiveSize(named.map { it.name to it.size })
            return UploadSource(
                Kind.FILES,
                size,
                { archiveStream(named) },
                { filesMeta(named.map { e -> FileEntry(e.name, e.size) }, it, sourceApp) },
            )
        }

        private fun archiveStream(entries: List<Ycf1.Entry>): InputStream {
            val pipeIn = java.io.PipedInputStream(256 * 1024)
            val pipeOut = java.io.PipedOutputStream(pipeIn)
            val error = arrayOfNulls<Throwable>(1)
            val thread = Thread {
                try {
                    pipeOut.use { Ycf1.write(entries, it) }
                } catch (t: Throwable) {
                    error[0] = t
                    try {
                        pipeOut.close()
                    } catch (_: Exception) {
                    }
                }
            }
            thread.isDaemon = true
            thread.name = "ycf1-writer"
            thread.start()
            return object : java.io.FilterInputStream(pipeIn) {
                override fun read(): Int = super.read().also { if (it < 0) check() }
                override fun read(b: ByteArray, off: Int, len: Int): Int = super.read(b, off, len).also { if (it < 0) check() }
                private fun check() {
                    thread.join(5_000)
                    error[0]?.let { throw java.io.IOException("archive failed: ${it.message}", it) }
                }
            }
        }
    }
}

sealed interface UploadResult {
    data class Sent(val header: ItemHeader) : UploadResult
    data class Skipped(val reason: String) : UploadResult
}

class Transfers(
    private val api: SyncApi,
    private val codec: ItemCodec,
    private val echo: EchoGuard,
    private val newestHash: () -> String?,
    private val log: Logger = NoLog,
    private val random: Random = Random.Default,
    private val maxAttempts: Int = 8,
    private val parallelChunks: Int = 3,
) {
    suspend fun upload(source: UploadSource, force: Boolean = false, progress: Progress = Progress { _, _ -> }): UploadResult {
        require(Kind.isValid(source.kind)) { "invalid kind" }
        if (source.size <= 0) return UploadResult.Skipped("empty")
        val key = codec.key
        val digests = source.open().use { key.digest().update(it) }.finish()
        if (digests.length != source.size) throw IntegrityException("source size changed")
        if (!force) {
            val normalized = if (source.kind == Kind.TEXT && source.size <= Protocol.INLINE_MAX_BYTES * 64) {
                EchoRules.normalizedTextHash(key, source.open().use { it.readBytes() }, source.kind)
            } else {
                null
            }
            val decision = EchoRules.decide(digests.contentHash, normalized, echo, newestHash())
            if (decision is UploadDecision.Skip) return UploadResult.Skipped(decision.reason)
        }
        val meta = source.buildMeta(digests.sha256)
        var lastError: Exception? = null
        repeat(2) {
            val id = Uuid7.generate()
            try {
                val header = if (Chunking.isInline(source.size)) {
                    uploadInline(id, source, meta, digests, progress)
                } else {
                    uploadChunked(id, source, meta, digests, progress)
                }
                echo.add(header.contentHash)
                return UploadResult.Sent(header)
            } catch (e: ApiException) {
                if (e.code != "id_conflict") throw e
                lastError = e
                log.w(TAG, "id conflict for $id, retrying with a new id")
            }
        }
        throw lastError ?: IllegalStateException("upload failed")
    }

    private suspend fun uploadThumb(id: String, source: UploadSource) {
        val jpeg = source.thumbnailJpeg ?: return
        val sealed = codec.sealThumb(id, jpeg)
        if (sealed.size > Protocol.THUMB_MAX_BYTES) return
        retrying("thumb $id") { api.uploadThumb(id, sealed) }
    }

    private suspend fun uploadInline(id: String, source: UploadSource, meta: ItemMeta, digests: Digests, progress: Progress): ItemHeader {
        val content = source.open().use { it.readBytes() }
        if (content.size.toLong() != source.size) throw IntegrityException("source size changed")
        uploadThumb(id, source)
        val request = CreateItemRequest(
            id = id,
            kind = source.kind,
            size = source.size,
            chunkCount = 0,
            contentHash = digests.contentHash,
            meta = codec.sealMeta(id, meta),
            payload = codec.sealPayload(id, content),
        )
        val header = retrying("create $id") { api.createItem(request) }
        progress.update(source.size, source.size)
        return header
    }

    private suspend fun uploadChunked(id: String, source: UploadSource, meta: ItemMeta, digests: Digests, progress: Progress): ItemHeader {
        val count = Chunking.chunkCount(source.size)
        val sent = AtomicLong(0)
        try {
            uploadThumb(id, source)
            val secondPass = streamChunks(id, source, count, null, sent, progress)
            if (secondPass.contentHash != digests.contentHash) throw IntegrityException("content changed during upload")
            val request = CommitRequest(
                kind = source.kind,
                size = source.size,
                chunkCount = count,
                contentHash = digests.contentHash,
                meta = codec.sealMeta(id, meta),
            )
            var rounds = 0
            while (true) {
                try {
                    val header = retrying("commit $id") { api.commit(id, request) }
                    progress.update(source.size, source.size)
                    return header
                } catch (e: ApiException) {
                    if (e.code != "missing_chunks" || rounds >= 3) throw e
                    rounds++
                    val missing = e.missingChunks().filter { it in 0 until count }.toSet()
                    log.w(TAG, "server is missing chunks $missing of $id")
                    streamChunks(id, source, count, missing.ifEmpty { (0 until count).toSet() }, sent, progress)
                }
            }
        } catch (e: CancellationException) {
            cancelPending(id)
            throw e
        } catch (e: Exception) {
            if (!(e is ApiException && e.code == "already_committed")) cancelPending(id)
            throw e
        }
    }

    private suspend fun cancelPending(id: String) {
        try {
            api.deleteItem(id)
        } catch (_: Exception) {
        }
    }

    private suspend fun streamChunks(
        id: String,
        source: UploadSource,
        count: Int,
        only: Set<Int>?,
        sent: AtomicLong,
        progress: Progress,
    ): Digests {
        val digest = codec.key.digest()
        val semaphore = Semaphore(parallelChunks)
        coroutineScope {
            val jobs = ArrayList<kotlinx.coroutines.Deferred<Unit>>()
            source.open().use { input ->
                for (index in 0 until count) {
                    coroutineContext.ensureActive()
                    val plainSize = Chunking.chunkPlainSize(source.size, index)
                    semaphore.acquire()
                    val buffer = ByteArray(plainSize)
                    val read = input.readFully(buffer, plainSize)
                    if (read != plainSize) {
                        semaphore.release()
                        throw IntegrityException("source ended early")
                    }
                    digest.update(buffer, 0, plainSize)
                    if (only != null && index !in only) {
                        semaphore.release()
                        continue
                    }
                    jobs += async {
                        try {
                            val sealed = codec.key.seal(Aad.chunk(id, index, count), buffer, 0, plainSize)
                            retrying("chunk $index of $id") { api.uploadChunk(id, index, sealed) }
                            progress.update(sent.addAndGet(plainSize.toLong()).coerceAtMost(source.size), source.size)
                        } finally {
                            semaphore.release()
                        }
                    }
                }
                if (input.read() >= 0) throw IntegrityException("source is longer than declared")
            }
            jobs.awaitAll()
        }
        return digest.finish()
    }

    suspend fun inlineContent(item: CachedItem, cachedPayload: String?): ByteArray {
        require(item.isInline) { "item is chunked" }
        val payload = cachedPayload ?: retrying("get ${item.id}") { api.getItem(item.id) }.payload
            ?: throw IntegrityException("inline item without payload")
        val content = codec.decryptPayload(item.id, payload)
        codec.verify(item, content)
        return content
    }

    suspend fun download(item: CachedItem, target: File, progress: Progress = Progress { _, _ -> }) {
        require(!item.isInline) { "item is inline" }
        val digest = codec.key.digest()
        val partial = File(target.parentFile, target.name + ".part")
        try {
            partial.outputStream().buffered(256 * 1024).use { out ->
                var done = 0L
                for (index in 0 until item.chunkCount) {
                    coroutineContext.ensureActive()
                    val expected = Chunking.chunkSealedSize(item.size, index)
                    val sealed = retrying("chunk $index of ${item.id}") { api.downloadChunk(item.id, index) }
                    if (sealed.size != expected) throw IntegrityException("chunk $index has ${sealed.size} bytes, expected $expected")
                    val plain = codec.decryptChunk(item.id, index, item.chunkCount, sealed)
                    digest.update(plain)
                    out.write(plain)
                    done += plain.size
                    progress.update(done, item.size)
                }
            }
            codec.verify(item, digest.finish())
            if (target.exists()) target.delete()
            if (!partial.renameTo(target)) throw java.io.IOException("cannot move download into place")
        } catch (e: Throwable) {
            partial.delete()
            throw e
        }
    }

    suspend fun <T> retrying(what: String, block: suspend () -> T): T {
        var attempt = 0
        while (true) {
            try {
                return block()
            } catch (e: CancellationException) {
                throw e
            } catch (e: ApiException) {
                if (!e.isRetryable || attempt + 1 >= maxAttempts) throw e
                val wait = Backoff.httpRetryDelayMs(attempt, random)
                log.w(TAG, "$what failed (${e.code}), retry ${attempt + 1} in ${wait}ms")
                attempt++
                delay(wait)
            }
        }
    }

    companion object {
        private const val TAG = "transfer"
    }
}
