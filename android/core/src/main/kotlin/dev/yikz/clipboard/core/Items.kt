package dev.yikz.clipboard.core

data class CachedItem(
    val id: String,
    val seq: Long,
    val deviceId: String,
    val kind: String,
    val size: Long,
    val chunkCount: Int,
    val createdAt: String,
    val createdAtMs: Long?,
    val pinned: Boolean,
    val contentHash: String,
    val hasThumb: Boolean,
    val storedBytes: Long,
    val sealedMeta: String,
    val meta: ItemMeta?,
    val metaError: String?,
    val payload: String? = null,
) {
    val isInline: Boolean get() = chunkCount == 0
    val preview: String get() = meta?.preview.orEmpty()
}

class IntegrityException(message: String) : Exception(message)

class ItemCodec(val key: MasterKey) {
    fun decode(header: ItemHeader): CachedItem {
        var meta: ItemMeta? = null
        var error: String? = null
        try {
            meta = decryptMeta(header.id, header.meta)
        } catch (e: Exception) {
            error = e.message ?: "undecryptable"
        }
        return CachedItem(
            id = header.id,
            seq = header.seq,
            deviceId = header.deviceId,
            kind = header.kind,
            size = header.size,
            chunkCount = header.chunkCount,
            createdAt = header.createdAt,
            createdAtMs = Timestamps.parseOrNull(header.createdAt),
            pinned = header.pinned,
            contentHash = header.contentHash,
            hasThumb = header.hasThumb,
            storedBytes = header.storedBytes,
            sealedMeta = header.meta,
            meta = meta,
            metaError = error,
            payload = header.payload,
        )
    }

    fun decryptMeta(itemId: String, sealedB64: String): ItemMeta {
        val plain = key.open(Aad.meta(itemId), StrictBase64.decode(sealedB64))
        val meta = ProtocolJson.decodeFromString(ItemMeta.serializer(), String(plain, Charsets.UTF_8))
        if (meta.v != 1) throw IntegrityException("unsupported meta version ${meta.v}")
        return meta
    }

    fun sealMeta(itemId: String, meta: ItemMeta, nonce: ByteArray = Aead.randomNonce()): String =
        StrictBase64.encode(key.seal(Aad.meta(itemId), encodeMeta(meta), nonce))

    fun encodeMeta(meta: ItemMeta): ByteArray =
        ProtocolJson.encodeToString(ItemMeta.serializer(), meta).toByteArray(Charsets.UTF_8)

    fun decryptPayload(itemId: String, sealedB64: String): ByteArray =
        key.open(Aad.payload(itemId), StrictBase64.decode(sealedB64))

    fun sealPayload(itemId: String, content: ByteArray, nonce: ByteArray = Aead.randomNonce()): String =
        StrictBase64.encode(key.seal(Aad.payload(itemId), content, nonce))

    fun decryptThumb(itemId: String, sealed: ByteArray): ByteArray = key.open(Aad.thumb(itemId), sealed)

    fun sealThumb(itemId: String, jpeg: ByteArray, nonce: ByteArray = Aead.randomNonce()): ByteArray =
        key.seal(Aad.thumb(itemId), jpeg, nonce)

    fun decryptChunk(itemId: String, index: Int, chunkCount: Int, sealed: ByteArray): ByteArray =
        key.open(Aad.chunk(itemId, index, chunkCount), sealed)

    fun verify(item: CachedItem, digests: Digests) {
        val meta = item.meta ?: throw IntegrityException("meta unavailable")
        if (digests.length != item.size) throw IntegrityException("size mismatch")
        if (digests.sha256 != meta.sha256) throw IntegrityException("sha256 mismatch")
        if (digests.contentHash != item.contentHash) throw IntegrityException("content_hash mismatch")
    }

    fun verify(item: CachedItem, content: ByteArray) {
        val digest = key.digest()
        digest.update(content)
        verify(item, digest.finish())
    }
}

fun textMeta(text: String, sha256: String, sourceApp: String? = null) = ItemMeta(
    v = 1,
    mime = Protocol.MIME_TEXT,
    preview = Preview.of(text),
    sha256 = sha256,
    sourceApp = sourceApp,
)

fun imageMeta(width: Int, height: Int, sha256: String, sourceApp: String? = null) = ItemMeta(
    v = 1,
    mime = Protocol.MIME_PNG,
    preview = "",
    sha256 = sha256,
    image = ImageDims(width, height),
    sourceApp = sourceApp,
)

fun filesMeta(files: List<FileEntry>, sha256: String, sourceApp: String? = null) = ItemMeta(
    v = 1,
    mime = Protocol.MIME_FILES,
    preview = Preview.of(files.joinToString("\n") { it.name }),
    sha256 = sha256,
    files = files,
    sourceApp = sourceApp,
)
