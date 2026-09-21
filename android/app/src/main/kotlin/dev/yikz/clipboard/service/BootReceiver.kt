package dev.yikz.clipboard.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import dev.yikz.clipboard.BuildConfig
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.sync.AuthState

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        val g = context.graph
        if (intent.action == Intent.ACTION_MY_PACKAGE_REPLACED) {
            g.log.i("update", "app replaced, now running ${BuildConfig.VERSION_NAME}")
            g.updates.cleanup()
        }
        g.sync.refreshAuth()
        if (g.sync.auth.value == AuthState.READY) {
            g.log.i("boot", "starting sync after ${intent.action?.substringAfterLast('.')}")
            SyncService.start(context)
        }
    }
}
