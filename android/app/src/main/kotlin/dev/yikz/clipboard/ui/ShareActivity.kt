package dev.yikz.clipboard.ui

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.yikz.clipboard.core.Kind
import dev.yikz.clipboard.core.UploadResult
import dev.yikz.clipboard.core.UploadSource
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.sync.AuthState
import dev.yikz.clipboard.sync.ClipRead
import dev.yikz.clipboard.sync.SyncController
import dev.yikz.clipboard.ui.components.ItemType
import dev.yikz.clipboard.ui.components.KindBadge
import dev.yikz.clipboard.ui.theme.YikzTheme
import dev.yikz.clipboard.util.Format
import dev.yikz.clipboard.util.Images
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

sealed interface ShareState {
    data object Preparing : ShareState
    data class Uploading(val label: String, val type: ItemType, val size: Long, val done: Long) : ShareState
    data class Done(val label: String, val type: ItemType, val size: Long, val skipped: Boolean) : ShareState
    data class Failed(val message: String) : ShareState
}

class ShareActivity : ComponentActivity() {
    private val state = MutableStateFlow<ShareState>(ShareState.Preparing)
    private var job: Job? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        if (graph.sync.auth.value != AuthState.READY) {
            state.value = ShareState.Failed("Sign in to Yikz Clipboard first")
        } else if (savedInstanceState == null) {
            start(intent)
        }
        setContent {
            YikzTheme {
                val current by state.collectAsStateWithLifecycle()
                ShareSheet(
                    state = current,
                    onClose = { closeSheet(current) },
                    onCancel = {
                        job?.cancel()
                        finish()
                    },
                )
            }
        }
    }

    private fun closeSheet(current: ShareState) {
        if (current is ShareState.Uploading || current is ShareState.Preparing) moveTaskToBack(true) else finish()
    }

    private fun start(intent: Intent) {
        val g = graph
        job = g.scope.launch {
            try {
                val prepared = withContext(Dispatchers.IO) { prepare(intent) }
                if (prepared == null) {
                    state.value = ShareState.Failed("Nothing to send")
                    return@launch
                }
                val (source, label) = prepared
                val type = when (source.kind) {
                    Kind.IMAGE -> ItemType.IMAGE
                    Kind.FILES -> ItemType.FILES
                    else -> if (source.size < 2048 && Format.isLink(source.open().use { String(it.readBytes()) })) ItemType.LINK else ItemType.TEXT
                }
                state.value = ShareState.Uploading(label, type, source.size, 0)
                val result = g.sync.send(source, force = true) { done, total ->
                    state.value = ShareState.Uploading(label, type, total, done)
                }
                state.value = ShareState.Done(label, type, source.size, result is UploadResult.Skipped)
                delay(1_200)
                withContext(Dispatchers.Main) { finish() }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                g.log.e("share", "share upload failed", e)
                state.value = ShareState.Failed(SyncController.errorMessage(e))
            }
        }
    }

    private fun prepare(intent: Intent): Pair<UploadSource, String>? {
        val g = graph
        val streams: List<Uri> = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(streamExtra(intent))
            Intent.ACTION_SEND_MULTIPLE -> streamListExtra(intent)
            else -> emptyList()
        }
        if (streams.isNotEmpty()) {
            if (streams.size == 1) {
                val mime = contentResolver.getType(streams[0]) ?: intent.type
                if (mime != null && mime.startsWith("image/")) {
                    val png = Images.toPng(contentResolver, streams[0], mime)
                    return UploadSource.image(png.bytes, png.width, png.height, Images.thumbnail(png.bytes)) to "Image"
                }
            }
            return when (val read = g.clipboard.filesSource(streams)) {
                is ClipRead.Ready -> read.source to read.label
                is ClipRead.Skip -> null
            }
        }
        val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
        if (!text.isNullOrEmpty()) return UploadSource.text(text) to (text.lineSequence().first().take(80))
        return null
    }

    private fun streamExtra(intent: Intent): Uri? = if (Build.VERSION.SDK_INT >= 33) {
        intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
    } else {
        @Suppress("DEPRECATION")
        intent.getParcelableExtra(Intent.EXTRA_STREAM)
    }

    private fun streamListExtra(intent: Intent): List<Uri> = if (Build.VERSION.SDK_INT >= 33) {
        intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
    } else {
        @Suppress("DEPRECATION")
        intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty()
    }
}

@Composable
private fun ShareSheet(state: ShareState, onClose: () -> Unit, onCancel: () -> Unit) {
    BackHandler { onClose() }
    val visible = remember { MutableTransitionState(false).apply { targetState = true } }
    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.4f))
            .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) { onClose() },
        contentAlignment = Alignment.BottomCenter,
    ) {
        AnimatedVisibility(visibleState = visible, enter = slideInVertically { it } + fadeIn()) {
            Surface(
                shape = RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp),
                color = MaterialTheme.colorScheme.surfaceContainerLow,
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) {},
            ) {
                Column(Modifier.navigationBarsPadding().padding(24.dp)) {
                    Box(
                        Modifier
                            .align(Alignment.CenterHorizontally)
                            .size(width = 32.dp, height = 4.dp)
                            .clip(CircleShape)
                            .background(MaterialTheme.colorScheme.outlineVariant),
                    )
                    Spacer(Modifier.height(20.dp))
                    Text("Send to your devices", style = MaterialTheme.typography.titleLarge)
                    Spacer(Modifier.height(20.dp))
                    AnimatedContent(targetState = state, contentKey = { it::class }, transitionSpec = { fadeIn() togetherWith androidx.compose.animation.fadeOut() }, label = "share") { s ->
                        when (s) {
                            ShareState.Preparing -> Column {
                                Text("Preparing", color = MaterialTheme.colorScheme.onSurfaceVariant)
                                Spacer(Modifier.height(12.dp))
                                LinearProgressIndicator(Modifier.fillMaxWidth())
                            }
                            is ShareState.Uploading -> Column {
                                ItemSummary(s.type, s.label, s.size)
                                Spacer(Modifier.height(16.dp))
                                if (s.size > 0 && s.done > 0) {
                                    LinearProgressIndicator(progress = { s.done.toFloat() / s.size }, modifier = Modifier.fillMaxWidth())
                                } else {
                                    LinearProgressIndicator(Modifier.fillMaxWidth())
                                }
                                Spacer(Modifier.height(8.dp))
                                Text(
                                    if (s.done > 0) "Encrypting and uploading  ·  ${Format.bytes(s.done)} of ${Format.bytes(s.size)}" else "Encrypting and uploading",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            is ShareState.Done -> Column {
                                ItemSummary(s.type, s.label, s.size)
                                Spacer(Modifier.height(16.dp))
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Icon(Icons.Filled.CheckCircle, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                                    Spacer(Modifier.width(10.dp))
                                    Text(if (s.skipped) "Already synced" else "Sent", style = MaterialTheme.typography.titleMedium)
                                }
                            }
                            is ShareState.Failed -> Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Outlined.ErrorOutline, contentDescription = null, tint = MaterialTheme.colorScheme.error)
                                Spacer(Modifier.width(10.dp))
                                Text(s.message, style = MaterialTheme.typography.bodyLarge)
                            }
                        }
                    }
                    Spacer(Modifier.height(24.dp))
                    Row(Modifier.fillMaxWidth()) {
                        Spacer(Modifier.weight(1f))
                        when (state) {
                            is ShareState.Uploading, ShareState.Preparing -> {
                                TextButton(onClick = onCancel) { Text("Cancel") }
                                Spacer(Modifier.width(8.dp))
                                Button(onClick = onClose) { Text("Hide") }
                            }
                            else -> Button(onClick = onCancel) { Text("Done") }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ItemSummary(type: ItemType, label: String, size: Long) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        KindBadge(type, size = 48.dp)
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f)) {
            Text(label, style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(Format.bytes(size), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
