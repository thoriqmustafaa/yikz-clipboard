package dev.yikz.clipboard.sync

import android.content.Context
import dev.yikz.clipboard.core.CachedItem
import dev.yikz.clipboard.core.Kind
import dev.yikz.clipboard.core.Progress
import dev.yikz.clipboard.core.Transfers
import dev.yikz.clipboard.core.Ycf1
import dev.yikz.clipboard.data.ClipDatabase
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.ByteArrayInputStream
import java.io.File

class ContentStore(private val context: Context, private val db: ClipDatabase) {
    private val locks = HashMap<String, Mutex>()

    val root: File get() = File(context.cacheDir, "clips")

    private fun lockFor(id: String): Mutex = synchronized(locks) { locks.getOrPut(id) { Mutex() } }

    suspend fun materialize(item: CachedItem, transfers: Transfers, progress: Progress = Progress { _, _ -> }): Materialized =
        lockFor(item.id).withLock {
            withContext(Dispatchers.IO) { materializeLocked(item, transfers, progress) }
        }

    private suspend fun materializeLocked(item: CachedItem, transfers: Transfers, progress: Progress): Materialized {
        val dir = File(root, item.id)
        val ready = File(dir, ".ready")
        if (ready.exists()) cached(item, dir)?.let { return it }
        dir.deleteRecursively()
        dir.mkdirs()
        val result: Materialized = if (item.isInline) {
            val content = transfers.inlineContent(item, db.payload(item.id))
            progress.update(item.size, item.size)
            when (item.kind) {
                Kind.TEXT -> Materialized.Text(String(content, Charsets.UTF_8))
                Kind.IMAGE -> {
                    val file = File(dir, "image.png")
                    file.writeBytes(content)
                    Materialized.Image(file)
                }
                else -> Materialized.Files(unpack(ByteArrayInputStream(content), dir))
            }
        } else {
            val blob = File(dir, ".content")
            transfers.download(item, blob, progress)
            when (item.kind) {
                Kind.TEXT -> Materialized.Text(blob.readText(Charsets.UTF_8)).also { blob.delete() }
                Kind.IMAGE -> {
                    val file = File(dir, "image.png")
                    blob.renameTo(file)
                    Materialized.Image(file)
                }
                else -> {
                    val files = blob.inputStream().buffered(256 * 1024).use { unpack(it, dir) }
                    blob.delete()
                    Materialized.Files(files)
                }
            }
        }
        if (result !is Materialized.Text) ready.createNewFile()
        return result
    }

    private fun cached(item: CachedItem, dir: File): Materialized? = when (item.kind) {
        Kind.IMAGE -> File(dir, "image.png").takeIf { it.exists() }?.let { Materialized.Image(it) }
        Kind.FILES -> {
            val names = item.meta?.files?.map { it.name }.orEmpty()
            val files = names.indices.map { File(File(dir, "files"), localName(names, it)) }
            if (files.isNotEmpty() && files.all { it.exists() }) Materialized.Files(files) else null
        }
        else -> null
    }

    private fun localName(names: List<String>, index: Int): String {
        val used = HashSet<String>()
        var result = ""
        for (i in 0..index) {
            val base = names[i]
            var candidate = base
            var n = 2
            while (!used.add(candidate.lowercase())) {
                val dot = base.lastIndexOf('.')
                candidate = if (dot > 0) "${base.substring(0, dot)} ($n)${base.substring(dot)}" else "$base ($n)"
                n++
            }
            result = candidate
        }
        return result
    }

    private fun unpack(input: java.io.InputStream, dir: File): List<File> {
        val filesDir = File(dir, "files").apply { mkdirs() }
        val names = ArrayList<String>()
        val out = ArrayList<File>()
        Ycf1.read(input) { name, _, data ->
            names += name
            val file = File(filesDir, localName(names, names.size - 1))
            file.outputStream().buffered(256 * 1024).use { data.copyTo(it) }
            out += file
        }
        return out
    }

    fun prune(keep: Int = 30, maxAgeMs: Long = 24 * 3600_000L) {
        val dirs = root.listFiles()?.filter { it.isDirectory }?.sortedByDescending { it.lastModified() } ?: return
        val now = System.currentTimeMillis()
        dirs.forEachIndexed { index, dir ->
            if (index >= keep || now - dir.lastModified() > maxAgeMs) dir.deleteRecursively()
        }
        File(context.cacheDir, "shared").listFiles()?.forEach { if (now - it.lastModified() > maxAgeMs) it.deleteRecursively() }
    }

    fun clear() {
        root.deleteRecursively()
        File(context.cacheDir, "shared").deleteRecursively()
        File(context.cacheDir, "thumbs").deleteRecursively()
    }
}
