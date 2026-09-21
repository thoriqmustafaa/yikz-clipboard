package dev.yikz.clipboard.service

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.SystemClock
import dev.yikz.clipboard.core.Logger
import dev.yikz.clipboard.sync.ClipboardBridge

class LogcatWatcher(
    private val context: Context,
    private val log: Logger,
    private val clipboard: ClipboardBridge,
    private val onClipboardChanged: () -> Unit,
) {
    @Volatile
    private var running = false
    private var thread: Thread? = null
    private var process: Process? = null
    private var lastTrigger = 0L

    fun start() {
        if (running) return
        running = true
        thread = Thread({ loop() }, "logcat-watcher").apply {
            isDaemon = true
            start()
        }
        log.i("auto", "automatic mode started")
    }

    fun stop() {
        running = false
        process?.destroy()
        thread?.interrupt()
        thread = null
        log.i("auto", "automatic mode stopped")
    }

    private fun loop() {
        val marker = "Denying clipboard access to ${context.packageName}"
        var failures = 0
        while (running) {
            try {
                val p = ProcessBuilder("logcat", "-T", "1", "-v", "brief", "ClipboardService:E", "*:S")
                    .redirectErrorStream(true)
                    .start()
                process = p
                p.inputStream.bufferedReader().useLines { lines ->
                    for (line in lines) {
                        if (!running) break
                        if (line.contains(marker)) trigger()
                    }
                }
                p.destroy()
            } catch (e: Exception) {
                if (!running) break
                log.w("auto", "logcat reader failed", e)
            }
            if (!running) break
            failures++
            try {
                Thread.sleep(minOf(60_000L, 2_000L * failures))
            } catch (_: InterruptedException) {
                break
            }
        }
    }

    private fun trigger() {
        val now = SystemClock.elapsedRealtime()
        if (now - lastTrigger < 700) return
        if (clipboard.recentlyWrote()) return
        lastTrigger = now
        onClipboardChanged()
    }

    companion object {
        fun hasReadLogs(context: Context): Boolean =
            context.checkSelfPermission(Manifest.permission.READ_LOGS) == PackageManager.PERMISSION_GRANTED

        fun adbCommand(context: Context): String = "adb shell pm grant ${context.packageName} android.permission.READ_LOGS"
    }
}
