package dev.yikz.clipboard.sync

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import android.os.PersistableBundle
import android.provider.OpenableColumns
import androidx.core.content.FileProvider
import dev.yikz.clipboard.core.Logger
import dev.yikz.clipboard.core.UploadSource
import dev.yikz.clipboard.core.Ycf1
import dev.yikz.clipboard.util.Images
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

sealed interface Materialized {
    data class Text(val text: String) : Materialized
    data class Image(val file: File) : Materialized
    data class Files(val files: List<File>) : Materialized
}

sealed interface ClipRead {
    data class Ready(val source: UploadSource, val label: String) : ClipRead
    data class Skip(val reason: String) : ClipRead
}

class ClipboardBridge(private val context: Context, private val log: Logger) {
    private val manager: ClipboardManager get() = context.getSystemService(ClipboardManager::class.java)

    @Volatile
    var lastOwnWriteAt: Long = 0
        private set

    @Volatile
    var ignoreNextChange: Boolean = false

    fun uriFor(file: File): Uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)

    suspend fun write(itemId: String, content: Materialized) = withContext(Dispatchers.Main) {
        val resolver = context.contentResolver
        val clip = when (content) {
            is Materialized.Text -> ClipData.newPlainText("Clipboard", content.text)
            is Materialized.Image -> ClipData.newUri(resolver, "Image", uriFor(content.file))
            is Materialized.Files -> {
                val uris = content.files.map { uriFor(it) }
                val data = ClipData.newUri(resolver, content.files.first().name, uris.first())
                uris.drop(1).forEach { data.addItem(resolver, ClipData.Item(it)) }
                data
            }
        }
        clip.description.extras = PersistableBundle().apply { putString(ORIGIN_EXTRA, itemId) }
        lastOwnWriteAt = android.os.SystemClock.elapsedRealtime()
        ignoreNextChange = true
        manager.setPrimaryClip(clip)
        log.i("clipboard", "wrote ${content.javaClass.simpleName.lowercase()} $itemId to clipboard")
    }

    fun recentlyWrote(windowMs: Long = 2_000): Boolean =
        android.os.SystemClock.elapsedRealtime() - lastOwnWriteAt < windowMs

    fun currentClip(): ClipData? = try {
        manager.primaryClip
    } catch (e: SecurityException) {
        null
    }

    fun read(clip: ClipData?): ClipRead {
        if (clip == null || clip.itemCount == 0) return ClipRead.Skip("Clipboard is empty")
        val extras = clip.description?.extras
        if (extras != null) {
            if (extras.getBoolean(SENSITIVE_EXTRA, false)) return ClipRead.Skip("Sensitive content is never synced")
            if (extras.getString(ORIGIN_EXTRA) != null) return ClipRead.Skip("Already synced")
        }
        val resolver = context.contentResolver
        val uris = (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri }.filter { it.scheme == "content" || it.scheme == "file" }
        if (uris.isNotEmpty()) {
            if (uris.size == 1) {
                val mime = resolver.getType(uris[0]) ?: clip.description?.getMimeType(0)
                if (mime != null && mime.startsWith("image/")) {
                    return try {
                        val png = Images.toPng(resolver, uris[0], mime)
                        ClipRead.Ready(UploadSource.image(png.bytes, png.width, png.height, Images.thumbnail(png.bytes)), "Image")
                    } catch (e: Exception) {
                        log.w("clipboard", "cannot read image from clipboard", e)
                        ClipRead.Skip("Cannot read image")
                    }
                }
            }
            return filesSource(uris)
        }
        val text = buildString {
            for (i in 0 until clip.itemCount) {
                val item = clip.getItemAt(i)
                val t = item.text?.toString() ?: item.htmlText?.let { android.text.Html.fromHtml(it, 0).toString() }
                if (t != null) {
                    if (isNotEmpty()) append('\n')
                    append(t)
                }
            }
        }
        if (text.isEmpty()) return ClipRead.Skip("Nothing to send")
        return ClipRead.Ready(UploadSource.text(text), "Text")
    }

    fun filesSource(uris: List<Uri>): ClipRead {
        val resolver = context.contentResolver
        val entries = ArrayList<Ycf1.Entry>()
        for ((index, uri) in uris.withIndex()) {
            var name: String? = null
            var size: Long? = null
            try {
                resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                    if (c.moveToFirst()) {
                        name = c.getString(0)
                        if (!c.isNull(1)) size = c.getLong(1)
                    }
                }
            } catch (_: Exception) {
            }
            val fileName = sanitize(name ?: uri.lastPathSegment ?: "file-${index + 1}")
            val known = size
            if (known != null && known >= 0) {
                entries += Ycf1.Entry(fileName, known) { resolver.openInputStream(uri) ?: throw java.io.IOException("cannot open $fileName") }
            } else {
                val copy = File(File(context.cacheDir, "shared").apply { mkdirs() }, "${System.nanoTime()}-$index")
                resolver.openInputStream(uri)?.use { input -> copy.outputStream().use { input.copyTo(it) } }
                    ?: return ClipRead.Skip("Cannot read $fileName")
                entries += Ycf1.Entry(fileName, copy.length()) { copy.inputStream() }
            }
        }
        if (entries.isEmpty()) return ClipRead.Skip("Nothing to send")
        val label = if (entries.size == 1) entries[0].name else "${entries.size} files"
        return ClipRead.Ready(UploadSource.files(entries), label)
    }

    private fun sanitize(name: String): String {
        val cleaned = Ycf1.normalizeName(name).map { c -> if (c == '/' || c == '\\' || c.code < 0x20 || c.code == 0x7f) '_' else c }.joinToString("")
        val trimmed = cleaned.trim().ifEmpty { "file" }.let { if (it == "." || it == "..") "file" else it }
        var result = trimmed
        while (result.toByteArray().size > 255) result = result.dropLast(1)
        return result
    }

    companion object {
        const val ORIGIN_EXTRA = "dev.yikz.clipboard.origin"
        const val SENSITIVE_EXTRA = "android.content.extra.IS_SENSITIVE"
    }
}
