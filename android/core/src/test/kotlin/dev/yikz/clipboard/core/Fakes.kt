package dev.yikz.clipboard.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

class FakeApi(var now: () -> Long = { 1_789_984_800_000L }) : SyncApi {
    val committed = ArrayList<ItemHeader>()
    val chunks = HashMap<String, MutableMap<Int, ByteArray>>()
    val thumbs = HashMap<String, ByteArray>()
    val calls = ArrayList<String>()
    var seq = 0L
    var stateRev = 0L
    var keyCheck: String? = null
    var salt = "XB6PKps9R+agxPgdLmuacw=="
    var serverId = "01a05bfb-7000-7691-98e4-301030971d0c"
    var deviceId = "01a05c04-97c0-7c1c-bdb7-1933ae6945c4"
    val transientFailures = HashMap<String, Int>()
    val dropChunksOnce = HashSet<Int>()

    private fun maybeFail(op: String) {
        val left = transientFailures[op] ?: 0
        if (left > 0) {
            transientFailures[op] = left - 1
            throw ApiException(503, "unavailable", "injected")
        }
    }

    fun add(header: ItemHeader): ItemHeader {
        val h = header.copy(seq = ++seq, createdAt = header.createdAt.ifEmpty { Timestamps.format(now()) })
        committed.removeAll { it.contentHash == h.contentHash && !it.pinned }
        committed += h
        return h
    }

    fun delete(ids: List<String>) {
        committed.removeAll { it.id in ids }
        stateRev++
    }

    fun setPin(id: String, pinned: Boolean) {
        val i = committed.indexOfFirst { it.id == id }
        committed[i] = committed[i].copy(pinned = pinned)
        stateRev++
    }

    override suspend fun me(): MeResponse {
        calls += "me"
        return MeResponse(
            username = "thoriq",
            device = Device(deviceId, "Pixel 9", "android", current = true),
            salt = salt,
            kdf = KdfParams("pbkdf2-sha256", 600000, 32),
            keyCheck = keyCheck,
            serverId = serverId,
            protocolVersion = 1,
        )
    }

    override suspend fun putKeyCheck(keyCheck: String) {
        calls += "putKeyCheck"
        val current = this.keyCheck
        if (current != null && current != keyCheck) throw ApiException(409, "key_check_exists", "exists")
        this.keyCheck = keyCheck
    }

    override suspend fun history(before: Long?, after: Long?, limit: Int?): HistoryResponse {
        calls += "history before=$before after=$after limit=$limit"
        maybeFail("history")
        val n = limit ?: 100
        val sorted = committed.sortedBy { it.seq }
        return if (after != null) {
            val matching = sorted.filter { it.seq > after }
            HistoryResponse(matching.take(n).map { it.copy(payload = null) }, matching.size > n)
        } else {
            val matching = sorted.filter { before == null || it.seq < before }.reversed()
            HistoryResponse(matching.take(n).map { it.copy(payload = null) }, matching.size > n)
        }
    }

    override suspend fun historyIndex(): HistoryIndex {
        calls += "index"
        return HistoryIndex(seq, stateRev, committed.sortedBy { it.seq }.map { IndexEntry(it.id, it.seq, it.pinned) })
    }

    override suspend fun getItem(id: String): ItemHeader {
        calls += "get $id"
        return committed.firstOrNull { it.id == id } ?: throw ApiException(404, "not_found", "item not found")
    }

    override suspend fun downloadChunk(id: String, index: Int): ByteArray {
        maybeFail("downloadChunk")
        return chunks[id]?.get(index) ?: throw ApiException(404, "not_found", "no chunk")
    }

    override suspend fun downloadThumb(id: String): ByteArray = thumbs[id] ?: throw ApiException(404, "not_found", "no thumb")

    override suspend fun createItem(request: CreateItemRequest): ItemHeader {
        calls += "create ${request.id}"
        maybeFail("createItem")
        if (keyCheck == null) throw ApiException(409, "key_check_missing", "missing")
        require(Uuid7.isValid(request.id))
        require(request.size <= Protocol.INLINE_MAX_BYTES)
        if (StrictBase64.decode(request.payload).size.toLong() != request.size + 28) throw ApiException(400, "size_mismatch", "payload")
        val stored = StrictBase64.decode(request.meta).size + StrictBase64.decode(request.payload).size + (thumbs[request.id]?.size ?: 0)
        return add(
            ItemHeader(
                id = request.id, deviceId = deviceId, kind = request.kind, size = request.size, chunkCount = 0,
                contentHash = request.contentHash, hasThumb = thumbs.containsKey(request.id), storedBytes = stored.toLong(),
                meta = request.meta, payload = request.payload,
            ),
        )
    }

    override suspend fun uploadChunk(id: String, index: Int, body: ByteArray) {
        calls += "chunk $id $index"
        maybeFail("uploadChunk")
        if (index in dropChunksOnce) {
            dropChunksOnce.remove(index)
            return
        }
        chunks.getOrPut(id) { HashMap() }[index] = body
    }

    override suspend fun uploadThumb(id: String, body: ByteArray) {
        calls += "thumb $id"
        thumbs[id] = body
    }

    override suspend fun commit(id: String, request: CommitRequest): ItemHeader {
        calls += "commit $id"
        val have = chunks[id].orEmpty()
        val missing = (0 until request.chunkCount).filter { it !in have }
        if (missing.isNotEmpty()) {
            throw ApiException(409, "missing_chunks", "missing", JsonObject(mapOf("missing" to JsonArray(missing.map { JsonPrimitive(it) }))))
        }
        for (i in 0 until request.chunkCount) {
            val expected = Chunking.chunkSealedSize(request.size, i)
            if (have.getValue(i).size != expected) throw ApiException(400, "size_mismatch", "chunk $i")
        }
        return add(
            ItemHeader(
                id = id, deviceId = deviceId, kind = request.kind, size = request.size, chunkCount = request.chunkCount,
                contentHash = request.contentHash, meta = request.meta,
                storedBytes = StrictBase64.decode(request.meta).size + have.values.sumOf { it.size }.toLong(),
            ),
        )
    }

    override suspend fun deleteItem(id: String) {
        calls += "delete $id"
        if (committed.any { it.id == id }) delete(listOf(id)) else chunks.remove(id)
    }

    override suspend fun pin(id: String, pinned: Boolean): ItemHeader {
        setPin(id, pinned)
        return committed.first { it.id == id }
    }

    override suspend fun devices(): List<Device> = emptyList()
    override suspend fun renameDevice(id: String, name: String): Device = Device(id, name, "android")
    override suspend fun deleteDevice(id: String) {}
    override suspend fun storage(): StorageInfo = StorageInfo()
    override suspend fun logout() {}
}

class MemoryStore : SyncStore {
    var syncState = SyncState()
    val items = LinkedHashMap<String, CachedItem>()

    override fun state(): SyncState = syncState
    override fun saveState(state: SyncState) {
        syncState = state
    }

    override fun upsert(items: List<CachedItem>) {
        for (item in items) {
            val existing = this.items[item.id]
            this.items[item.id] = if (item.payload == null && existing?.payload != null) item.copy(payload = existing.payload) else item
        }
    }

    override fun delete(ids: Collection<String>) {
        ids.forEach { items.remove(it) }
    }

    override fun setPinned(id: String, pinned: Boolean) {
        items[id]?.let { items[id] = it.copy(pinned = pinned) }
    }

    override fun reconcile(index: Map<String, Boolean>, upToSeq: Long) {
        val remove = items.values.filter { it.seq <= upToSeq && it.id !in index }.map { it.id }
        remove.forEach { items.remove(it) }
        for ((id, pinned) in index) setPinned(id, pinned)
    }

    override fun clearAll() {
        items.clear()
    }

    override fun newestContentHash(): String? = items.values.maxByOrNull { it.seq }?.contentHash
    override fun get(id: String): CachedItem? = items[id]
    override fun payload(id: String): String? = items[id]?.payload
    override fun oldestSeq(): Long? = items.values.minOfOrNull { it.seq }
}

class RecordingApplier(private val limit: Long = Protocol.DEFAULT_AUTO_DOWNLOAD_BYTES) : ItemApplier {
    val written = ArrayList<String>()
    val offered = ArrayList<String>()
    val disabledKinds = HashSet<String>()

    override fun autoDownloadLimit(): Long = limit
    override fun isKindEnabled(kind: String): Boolean = kind !in disabledKinds
    override suspend fun prepare(item: CachedItem): PreparedClip = object : PreparedClip {
        override suspend fun write() {
            written += item.id
        }
    }
    override fun offerDownload(item: CachedItem) {
        offered += item.id
    }
}

object Fixtures {
    val key = primaryKey
    val codec = ItemCodec(key)
    const val OWN = "01a05c04-97c0-7c1c-bdb7-1933ae6945c4"
    const val OTHER = "01a05c00-03e0-7c59-b16c-c1bb2928ae88"

    fun textHeader(text: String, device: String = OTHER, id: String = Uuid7.generate()): ItemHeader {
        val bytes = text.toByteArray()
        return ItemHeader(
            id = id,
            deviceId = device,
            kind = Kind.TEXT,
            size = bytes.size.toLong(),
            chunkCount = 0,
            contentHash = key.contentHash(bytes),
            meta = codec.sealMeta(id, textMeta(text, Hashing.sha256Hex(bytes))),
            payload = codec.sealPayload(id, bytes),
        )
    }

    fun chunkedHeader(size: Long, device: String = OTHER, id: String = Uuid7.generate()): ItemHeader = ItemHeader(
        id = id,
        deviceId = device,
        kind = Kind.FILES,
        size = size,
        chunkCount = Chunking.chunkCount(size),
        contentHash = "ab".repeat(32),
        meta = codec.sealMeta(id, filesMeta(listOf(FileEntry("big.bin", size - 30)), "cd".repeat(32))),
    )
}
