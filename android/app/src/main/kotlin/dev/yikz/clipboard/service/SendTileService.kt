package dev.yikz.clipboard.service

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import dev.yikz.clipboard.core.ConnectionState
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.ui.ClipboardReadActivity

class SendTileService : TileService() {
    override fun onStartListening() {
        super.onStartListening()
        val tile = qsTile ?: return
        val g = graph
        tile.state = Tile.STATE_INACTIVE
        tile.label = "Send clipboard"
        tile.subtitle = when {
            g.sync.settings.value.paused -> "Paused"
            g.sync.connection.value is ConnectionState.Connected -> "Connected"
            else -> "Offline"
        }
        tile.updateTile()
    }

    @SuppressLint("StartActivityAndCollapseDeprecated")
    override fun onClick() {
        super.onClick()
        val intent = ClipboardReadActivity.intent(this, manual = true)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 3, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}
