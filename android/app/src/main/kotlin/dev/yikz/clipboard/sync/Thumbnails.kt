package dev.yikz.clipboard.sync

import android.content.Context
import android.graphics.Bitmap
import android.util.LruCache
import dev.yikz.clipboard.core.CachedItem
import dev.yikz.clipboard.core.ItemCodec
import dev.yikz.clipboard.core.Kind
import dev.yikz.clipboard.core.SyncApi
import dev.yikz.clipboard.data.ClipDatabase
import dev.yikz.clipboard.util.Images
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import java.io.File

class Thumbnails(private val context: Context) {
    private val memory = object : LruCache<String, Bitmap>(24 * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.allocationByteCount
    }
    private val permits = Semaphore(4)
    private val dir: File get() = File(context.cacheDir, "thumbs").apply { mkdirs() }

    fun cached(id: String): Bitmap? = memory.get(id)

    suspend fun load(item: CachedItem, api: SyncApi, codec: ItemCodec, db: ClipDatabase): Bitmap? {
        if (item.kind != Kind.IMAGE) return null
        memory.get(item.id)?.let { return it }
        return permits.withPermit {
            withContext(Dispatchers.IO) {
                memory.get(item.id) ?: runCatching { fetch(item, api, codec, db) }.getOrNull()?.also { memory.put(item.id, it) }
            }
        }
    }

    private suspend fun fetch(item: CachedItem, api: SyncApi, codec: ItemCodec, db: ClipDatabase): Bitmap? {
        if (item.hasThumb) {
            val file = File(dir, item.id)
            val sealed = if (file.exists()) file.readBytes() else api.downloadThumb(item.id).also { file.writeBytes(it) }
            val jpeg = try {
                codec.decryptThumb(item.id, sealed)
            } catch (e: Exception) {
                file.delete()
                throw e
            }
            return Images.decodeSampled(jpeg, 320)
        }
        if (item.isInline) {
            val payload = db.payload(item.id) ?: api.getItem(item.id).payload ?: return null
            return Images.decodeSampled(codec.decryptPayload(item.id, payload), 320)
        }
        return null
    }

    fun clear() {
        memory.evictAll()
        dir.deleteRecursively()
    }
}
