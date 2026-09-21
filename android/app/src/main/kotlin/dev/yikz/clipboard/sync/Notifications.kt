package dev.yikz.clipboard.sync

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import dev.yikz.clipboard.R
import dev.yikz.clipboard.core.BlockReason
import dev.yikz.clipboard.core.CachedItem
import dev.yikz.clipboard.core.ConnectionState
import dev.yikz.clipboard.core.Kind
import dev.yikz.clipboard.service.SyncService
import dev.yikz.clipboard.ui.ClipboardReadActivity
import dev.yikz.clipboard.ui.MainActivity
import dev.yikz.clipboard.util.Format

class Notifications(private val context: Context) {
    private val manager = NotificationManagerCompat.from(context)

    init {
        val system = context.getSystemService(NotificationManager::class.java)
        system.createNotificationChannels(
            listOf(
                NotificationChannel(CHANNEL_STATUS, "Connection status", NotificationManager.IMPORTANCE_LOW).apply {
                    description = "Persistent sync status with quick actions"
                    setShowBadge(false)
                },
                NotificationChannel(CHANNEL_TRANSFERS, "Large items", NotificationManager.IMPORTANCE_DEFAULT).apply {
                    description = "Items above the auto-download limit and download progress"
                },
                NotificationChannel(CHANNEL_ALERTS, "Alerts", NotificationManager.IMPORTANCE_DEFAULT).apply {
                    description = "Sign-in problems and storage warnings"
                },
                NotificationChannel(CHANNEL_UPDATES, "App updates", NotificationManager.IMPORTANCE_HIGH).apply {
                    description = "New versions of Yikz Clipboard ready to install"
                },
            ),
        )
    }

    private fun canPost(): Boolean =
        Build.VERSION.SDK_INT < 33 ||
            ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    private fun openApp(): PendingIntent = PendingIntent.getActivity(
        context,
        1,
        Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun serviceAction(action: String, code: Int, extra: String? = null): PendingIntent = PendingIntent.getForegroundService(
        context,
        code,
        Intent(context, SyncService::class.java).setAction(action).apply { extra?.let { putExtra(SyncService.EXTRA_ID, it) } },
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun sendClipboard(): PendingIntent = PendingIntent.getActivity(
        context,
        2,
        ClipboardReadActivity.intent(context, manual = true),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    fun status(state: ConnectionState, paused: Boolean, ownDeviceId: String, syncing: Boolean): Notification {
        val builder = NotificationCompat.Builder(context, CHANNEL_STATUS)
            .setSmallIcon(R.drawable.ic_notification)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setSilent(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .setContentIntent(openApp())
        val title: String
        var text: String? = null
        when {
            paused || state == ConnectionState.Paused -> {
                title = "Paused"
                text = "Sync is paused. Nothing is sent or received."
            }
            state is ConnectionState.Connected -> {
                val others = state.onlineDevices.count { it.deviceId != ownDeviceId }
                title = when (others) {
                    0 -> "Connected"
                    1 -> "Connected to 1 device"
                    else -> "Connected to $others devices"
                }
                text = when {
                    syncing -> "Catching up on history"
                    others == 0 -> "No other devices online"
                    else -> state.onlineDevices.filter { it.deviceId != ownDeviceId }.joinToString(", ") { it.name }
                }
            }
            state is ConnectionState.Waiting -> {
                val seconds = ((state.retryAtMs - System.currentTimeMillis()) / 1000).coerceAtLeast(0)
                title = if (seconds <= 0) "Reconnecting" else "Reconnecting in ${seconds}s"
                text = state.lastError
                builder.setUsesChronometer(true).setChronometerCountDown(true).setWhen(state.retryAtMs).setShowWhen(true)
            }
            state is ConnectionState.Connecting -> title = "Connecting"
            state is ConnectionState.Blocked -> {
                title = when (state.reason) {
                    BlockReason.UNAUTHORIZED -> "Signed out"
                    BlockReason.UPDATE_REQUIRED -> "Update required"
                    BlockReason.TOO_MANY_CONNECTIONS -> "Disconnected"
                }
                text = when (state.reason) {
                    BlockReason.UNAUTHORIZED -> "Open the app to sign in again"
                    BlockReason.UPDATE_REQUIRED -> "The server needs a newer app version"
                    BlockReason.TOO_MANY_CONNECTIONS -> "Too many connections for this device. Open the app to reconnect."
                }
            }
            else -> title = "Not connected"
        }
        builder.setContentTitle(title)
        text?.let { builder.setContentText(it) }
        builder.addAction(0, "Send clipboard", sendClipboard())
        if (paused) {
            builder.addAction(0, "Resume", serviceAction(SyncService.ACTION_RESUME, 10))
        } else {
            builder.addAction(0, "Pause", serviceAction(SyncService.ACTION_PAUSE, 11))
        }
        return builder.build()
    }

    fun offerDownload(item: CachedItem, fromDevice: String) {
        if (!canPost()) return
        val name = describe(item)
        val notification = NotificationCompat.Builder(context, CHANNEL_TRANSFERS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("$name (${Format.bytes(item.size)})")
            .setContentText("From $fromDevice. Download to clipboard?")
            .setAutoCancel(true)
            .setContentIntent(openApp())
            .addAction(0, "Download", serviceAction(SyncService.ACTION_DOWNLOAD, item.id.hashCode(), item.id))
            .build()
        post(item.id.hashCode(), notification)
    }

    fun downloadProgress(item: CachedItem, done: Long, total: Long) {
        if (!canPost()) return
        val percent = if (total > 0) ((done * 100) / total).toInt() else 0
        val notification = NotificationCompat.Builder(context, CHANNEL_TRANSFERS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Downloading ${describe(item)}")
            .setContentText("${Format.bytes(done)} of ${Format.bytes(total)}")
            .setProgress(100, percent, total <= 0)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .build()
        post(item.id.hashCode(), notification)
    }

    fun downloadFinished(item: CachedItem, ok: Boolean) {
        if (!canPost()) return
        val notification = NotificationCompat.Builder(context, CHANNEL_TRANSFERS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(if (ok) "Copied to clipboard" else "Download failed")
            .setContentText(describe(item))
            .setAutoCancel(true)
            .setTimeoutAfter(if (ok) 5_000 else 60_000)
            .setContentIntent(openApp())
            .build()
        post(item.id.hashCode(), notification)
    }

    fun cancel(id: Int) = manager.cancel(id)

    private fun post(id: Int, notification: Notification) {
        if (!canPost()) return
        try {
            manager.notify(id, notification)
        } catch (_: SecurityException) {
        }
    }

    fun alert(id: Int, title: String, text: String) {
        if (!canPost()) return
        val notification = NotificationCompat.Builder(context, CHANNEL_ALERTS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setAutoCancel(true)
            .setContentIntent(openApp())
            .build()
        post(id, notification)
    }

    fun updateReady(id: Int, version: String, confirm: Intent) {
        val intent = PendingIntent.getActivity(context, 20, confirm, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(context, CHANNEL_UPDATES)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Update $version ready, tap to install")
            .setContentText("Downloaded and verified. Android asks you to confirm the install.")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
            .setAutoCancel(true)
            .setContentIntent(intent)
            .addAction(0, "Install", intent)
            .build()
        post(id, notification)
    }

    fun updateAvailable(id: Int, version: String) {
        val notification = NotificationCompat.Builder(context, CHANNEL_UPDATES)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Update $version available")
            .setContentText("Open Settings in Yikz Clipboard to install it.")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
            .setAutoCancel(true)
            .setContentIntent(openApp())
            .build()
        post(id, notification)
    }

    fun updatePermission(id: Int, version: String, settings: Intent) {
        val intent = PendingIntent.getActivity(
            context,
            21,
            settings.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val text = "Update $version is ready. Allow Yikz Clipboard to install apps once, then updates install by themselves."
        val notification = NotificationCompat.Builder(context, CHANNEL_UPDATES)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Allow installing updates")
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setContentIntent(intent)
            .build()
        post(id, notification)
    }

    private fun describe(item: CachedItem): String {
        val meta = item.meta
        return when (item.kind) {
            Kind.FILES -> meta?.files?.let { if (it.size == 1) it[0].name else "${it.size} files" } ?: "Files"
            Kind.IMAGE -> "Image"
            else -> "Text"
        }
    }

    companion object {
        const val CHANNEL_STATUS = "status"
        const val CHANNEL_TRANSFERS = "transfers"
        const val CHANNEL_ALERTS = "alerts"
        const val CHANNEL_UPDATES = "updates"
        const val STATUS_ID = 1
        const val ALERT_SIGNED_OUT = 2
        const val ALERT_STORAGE = 3
    }
}
