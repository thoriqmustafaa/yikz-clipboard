package dev.yikz.clipboard.ui

import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.sync.AuthState

class ClipboardReadActivity : ComponentActivity() {
    private var handled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (graph.sync.auth.value != AuthState.READY) {
            Toast.makeText(this, "Sign in to Yikz Clipboard first", Toast.LENGTH_SHORT).show()
            finishQuietly()
            return
        }
        window.decorView.postDelayed({ if (!handled) finishQuietly() }, 3_000)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || handled) return
        handled = true
        val manual = intent.getBooleanExtra(EXTRA_MANUAL, true)
        graph.sync.sendClip(graph.clipboard.currentClip(), manual)
        finishQuietly()
    }

    private fun finishQuietly() {
        finish()
        if (Build.VERSION.SDK_INT >= 34) {
            overrideActivityTransition(OVERRIDE_TRANSITION_CLOSE, 0, 0)
        } else {
            @Suppress("DEPRECATION")
            overridePendingTransition(0, 0)
        }
    }

    companion object {
        private const val EXTRA_MANUAL = "manual"

        fun intent(context: Context, manual: Boolean): Intent = Intent(context, ClipboardReadActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION or Intent.FLAG_ACTIVITY_EXCLUDE_FROM_RECENTS)
            .putExtra(EXTRA_MANUAL, manual)
    }
}
