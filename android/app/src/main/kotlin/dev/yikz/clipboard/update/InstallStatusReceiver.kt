package dev.yikz.clipboard.update

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import dev.yikz.clipboard.graph

class InstallStatusReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        context.graph.updates.onInstallStatus(intent)
    }

    companion object {
        const val ACTION = "dev.yikz.clipboard.action.INSTALL_STATUS"
        const val EXTRA_VERSION = "version"
    }
}
