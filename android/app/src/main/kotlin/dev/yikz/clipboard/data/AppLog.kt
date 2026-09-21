package dev.yikz.clipboard.data

import android.util.Log
import dev.yikz.clipboard.core.LogLevel
import dev.yikz.clipboard.core.Logger
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

data class LogEntry(val id: Long, val timeMs: Long, val level: LogLevel, val tag: String, val message: String)

class AppLog(private val dir: File) : Logger {
    private val capacity = 1000
    private val buffer = ArrayDeque<LogEntry>()
    private var nextId = 0L
    private val _entries = MutableStateFlow<List<LogEntry>>(emptyList())
    val entries: StateFlow<List<LogEntry>> = _entries.asStateFlow()
    private val writer = Executors.newSingleThreadExecutor { r -> Thread(r, "log-writer").apply { isDaemon = true } }
    private val fileFormat = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US)

    init {
        dir.mkdirs()
    }

    val currentFile: File get() = File(dir, "app.log")

    override fun log(level: LogLevel, tag: String, message: String, error: Throwable?) {
        val text = if (error != null) "$message: ${error.javaClass.simpleName}: ${error.message}" else message
        when (level) {
            LogLevel.DEBUG -> Log.d("yikz/$tag", text)
            LogLevel.INFO -> Log.i("yikz/$tag", text)
            LogLevel.WARN -> Log.w("yikz/$tag", text)
            LogLevel.ERROR -> Log.e("yikz/$tag", text, error)
        }
        val entry: LogEntry
        synchronized(buffer) {
            entry = LogEntry(nextId++, System.currentTimeMillis(), level, tag, text)
            buffer.addLast(entry)
            while (buffer.size > capacity) buffer.removeFirst()
            _entries.value = buffer.toList()
        }
        writer.execute { append(entry) }
    }

    fun clear() {
        synchronized(buffer) {
            buffer.clear()
            _entries.value = emptyList()
        }
    }

    private fun append(entry: LogEntry) {
        try {
            val file = currentFile
            if (file.length() > MAX_FILE_BYTES) rotate()
            file.appendText("${fileFormat.format(Date(entry.timeMs))} ${entry.level.name.first()} ${entry.tag}: ${entry.message}\n")
        } catch (_: Exception) {
        }
    }

    private fun rotate() {
        for (i in MAX_FILES - 1 downTo 1) {
            val from = if (i == 1) currentFile else File(dir, "app.${i - 1}.log")
            val to = File(dir, "app.$i.log")
            if (from.exists()) {
                to.delete()
                from.renameTo(to)
            }
        }
    }

    fun exportTo(target: File): File {
        writer.submit {}.get()
        target.parentFile?.mkdirs()
        target.outputStream().bufferedWriter().use { out ->
            for (i in MAX_FILES - 1 downTo 1) {
                val f = File(dir, "app.$i.log")
                if (f.exists()) out.write(f.readText())
            }
            if (currentFile.exists()) out.write(currentFile.readText())
        }
        return target
    }

    companion object {
        private const val MAX_FILE_BYTES = 512 * 1024L
        private const val MAX_FILES = 4
    }
}
