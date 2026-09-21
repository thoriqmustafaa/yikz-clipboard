package dev.yikz.clipboard.ui.screens

import android.content.ClipData
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.provider.MediaStore
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.OpenInNew
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.automirrored.outlined.InsertDriveFile
import androidx.compose.material.icons.outlined.PushPin
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.yikz.clipboard.core.CachedItem
import dev.yikz.clipboard.core.Kind
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.sync.Materialized
import dev.yikz.clipboard.sync.SyncController
import dev.yikz.clipboard.ui.components.ItemType
import dev.yikz.clipboard.ui.components.title
import dev.yikz.clipboard.ui.components.type
import dev.yikz.clipboard.ui.theme.MonoStyle
import dev.yikz.clipboard.util.Format
import dev.yikz.clipboard.util.Images
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ItemDetailSheet(
    item: CachedItem,
    deviceName: String,
    onDismiss: () -> Unit,
    onDelete: () -> Unit,
    onMessage: (String) -> Unit,
) {
    val context = LocalContext.current
    val g = context.graph
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val scope = rememberCoroutineScope()
    var busy by remember { mutableStateOf<String?>(null) }
    var progress by remember { mutableStateOf<Pair<Long, Long>?>(null) }
    var fullText by remember(item.id) { mutableStateOf<String?>(null) }
    var image by remember(item.id) { mutableStateOf<Bitmap?>(null) }

    LaunchedEffect(item.id) {
        try {
            if (item.kind == Kind.TEXT && item.size <= 1_048_576) {
                val content = g.sync.materialize(item)
                if (content is Materialized.Text) fullText = content.text
            } else if (item.kind == Kind.IMAGE && item.size <= 20_971_520) {
                val content = g.sync.materialize(item) { done, total -> progress = done to total }
                progress = null
                if (content is Materialized.Image) image = withContext(Dispatchers.IO) { Images.decodeSampled(content.file, 1600) }
            }
        } catch (_: Exception) {
            progress = null
        }
    }

    fun run(label: String, action: suspend () -> Unit) {
        if (busy != null) return
        busy = label
        scope.launch {
            try {
                action()
            } catch (e: Exception) {
                onMessage(SyncController.errorMessage(e))
            } finally {
                busy = null
                progress = null
            }
        }
    }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surfaceContainerLow) {
        Column(
            Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp)
                .navigationBarsPadding(),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Thumbnail(item, 48.dp)
                Spacer(Modifier.width(14.dp))
                Column(Modifier.weight(1f)) {
                    Text(item.title(), style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Text(
                        "${item.type().label.removeSuffix("s")}  ·  ${Format.bytes(item.size)}",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            Spacer(Modifier.height(16.dp))
            Preview(item, fullText, image, progress)
            AnimatedVisibility(visible = busy != null) {
                val p = progress
                Column(Modifier.padding(top = 12.dp)) {
                    if (p != null && p.second > 0) {
                        LinearProgressIndicator(progress = { p.first.toFloat() / p.second }, modifier = Modifier.fillMaxWidth())
                        Spacer(Modifier.height(6.dp))
                        Text("${Format.bytes(p.first)} of ${Format.bytes(p.second)}", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    } else {
                        LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                    }
                }
            }
            Spacer(Modifier.height(20.dp))
            Button(
                onClick = {
                    run("copy") {
                        g.sync.copyToClipboard(item) { done, total -> progress = done to total }
                        onMessage("Copied to clipboard")
                    }
                },
                enabled = busy == null && item.meta != null,
                modifier = Modifier.fillMaxWidth().height(52.dp),
            ) {
                if (busy == "copy") {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                } else {
                    Icon(Icons.Outlined.ContentCopy, contentDescription = null, modifier = Modifier.size(20.dp))
                    Spacer(Modifier.width(10.dp))
                    Text("Copy to clipboard")
                }
            }
            Spacer(Modifier.height(16.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly) {
                ActionButton(Icons.Outlined.Share, "Share", busy == null && item.meta != null) {
                    run("share") {
                        val content = g.sync.materialize(item) { done, total -> progress = done to total }
                        share(context, content)
                    }
                }
                ActionButton(Icons.Outlined.Download, "Save", busy == null && item.meta != null) {
                    run("save") {
                        val content = g.sync.materialize(item) { done, total -> progress = done to total }
                        val count = withContext(Dispatchers.IO) { saveToDownloads(context, item, content) }
                        onMessage(if (count == 1) "Saved to Downloads" else "Saved $count files to Downloads")
                    }
                }
                if (item.type() == ItemType.LINK) {
                    ActionButton(Icons.AutoMirrored.Outlined.OpenInNew, "Open", true) {
                        val url = item.preview.trim().let { if (it.startsWith("www.", true)) "https://$it" else it }
                        try {
                            context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
                        } catch (_: Exception) {
                            onMessage("No app can open this link")
                        }
                    }
                }
                ActionButton(if (item.pinned) Icons.Filled.PushPin else Icons.Outlined.PushPin, if (item.pinned) "Unpin" else "Pin", busy == null) {
                    run("pin") { g.sync.setPinned(item, !item.pinned) }
                }
                ActionButton(Icons.Outlined.Delete, "Delete", busy == null, destructive = true) {
                    onDelete()
                }
            }
            Spacer(Modifier.height(24.dp))
            Text("Information", style = MaterialTheme.typography.titleSmall)
            Spacer(Modifier.height(8.dp))
            Surface(color = MaterialTheme.colorScheme.surfaceContainerHigh, shape = MaterialTheme.shapes.medium) {
                Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) {
                    InfoRow("Source", deviceName)
                    item.meta?.sourceApp?.let { InfoRow("Application", it) }
                    InfoRow("Type", item.type().label.removeSuffix("s"))
                    InfoRow("Size", Format.bytes(item.size))
                    item.meta?.image?.let { InfoRow("Dimensions", "${it.width} × ${it.height}") }
                    item.meta?.files?.let { InfoRow("Files", it.size.toString()) }
                    InfoRow("Created", Format.dateTime(item.createdAtMs ?: 0))
                    InfoRow("Pinned", if (item.pinned) "Yes" else "No", last = true)
                }
            }
            Spacer(Modifier.height(24.dp))
        }
    }
}

@Composable
private fun Preview(item: CachedItem, fullText: String?, image: Bitmap?, progress: Pair<Long, Long>?) {
    val meta = item.meta
    if (meta == null) {
        Surface(color = MaterialTheme.colorScheme.errorContainer, shape = MaterialTheme.shapes.medium, modifier = Modifier.fillMaxWidth()) {
            Text(
                "This item could not be decrypted. It may come from a newer app version or a different encryption password.",
                modifier = Modifier.padding(16.dp),
                color = MaterialTheme.colorScheme.onErrorContainer,
            )
        }
        return
    }
    when (item.kind) {
        Kind.IMAGE -> Box(
            Modifier
                .fillMaxWidth()
                .clip(MaterialTheme.shapes.medium)
                .background(MaterialTheme.colorScheme.surfaceContainerHighest)
                .aspectRatio(meta.image?.let { (it.width.toFloat() / it.height.coerceAtLeast(1)).coerceIn(0.5f, 2.5f) } ?: 1.5f),
            contentAlignment = Alignment.Center,
        ) {
            if (image != null) {
                Image(image.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Fit, modifier = Modifier.fillMaxWidth())
            } else if (item.size > 20_971_520 && progress == null) {
                Text("Large image. Tap Copy or Save to download.", color = MaterialTheme.colorScheme.onSurfaceVariant)
            } else {
                CircularProgressIndicator()
            }
        }
        Kind.FILES -> Surface(color = MaterialTheme.colorScheme.surfaceContainerHigh, shape = MaterialTheme.shapes.medium) {
            Column(Modifier.padding(vertical = 4.dp)) {
                val files = meta.files.orEmpty()
                files.take(50).forEachIndexed { index, file ->
                    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.AutoMirrored.Outlined.InsertDriveFile, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(20.dp))
                        Spacer(Modifier.width(12.dp))
                        Text(file.name, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(Format.bytes(file.size), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    if (index < files.size - 1 && index < 49) HorizontalDivider(Modifier.padding(start = 48.dp), color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
                }
                if (files.size > 50) {
                    Text("and ${files.size - 50} more", modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        else -> Surface(color = MaterialTheme.colorScheme.surfaceContainerHigh, shape = MaterialTheme.shapes.medium, modifier = Modifier.fillMaxWidth()) {
            Box(Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState()).padding(16.dp)) {
                SelectionContainer {
                    Text(
                        fullText ?: meta.preview,
                        style = if (item.type() == ItemType.LINK) MaterialTheme.typography.bodyLarge.copy(color = MaterialTheme.colorScheme.primary) else MonoStyle.copy(color = MaterialTheme.colorScheme.onSurface, fontSize = MaterialTheme.typography.bodyMedium.fontSize, lineHeight = MaterialTheme.typography.bodyMedium.lineHeight),
                    )
                }
            }
        }
    }
}

@Composable
private fun ActionButton(icon: ImageVector, label: String, enabled: Boolean, destructive: Boolean = false, onClick: () -> Unit) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        FilledTonalIconButton(
            onClick = onClick,
            enabled = enabled,
            modifier = Modifier.size(52.dp),
            colors = if (destructive) {
                IconButtonDefaults.filledTonalIconButtonColors(
                    containerColor = MaterialTheme.colorScheme.errorContainer,
                    contentColor = MaterialTheme.colorScheme.onErrorContainer,
                )
            } else {
                IconButtonDefaults.filledTonalIconButtonColors()
            },
        ) {
            Icon(icon, contentDescription = null)
        }
        Spacer(Modifier.height(6.dp))
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun InfoRow(label: String, value: String, last: Boolean = false) {
    Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.width(110.dp))
        Text(value, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f), maxLines = 2, overflow = TextOverflow.Ellipsis)
    }
    if (!last) HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
}

private fun share(context: Context, content: Materialized) {
    val g = context.graph
    val intent = when (content) {
        is Materialized.Text -> Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, content.text)
        is Materialized.Image -> {
            val uri = g.clipboard.uriFor(content.file)
            Intent(Intent.ACTION_SEND).setType("image/png").putExtra(Intent.EXTRA_STREAM, uri).apply {
                clipData = ClipData.newRawUri("", uri)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
        }
        is Materialized.Files -> {
            val uris = ArrayList(content.files.map { g.clipboard.uriFor(it) })
            val base = if (uris.size == 1) {
                Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0])
            } else {
                Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
            }
            base.setType(if (uris.size == 1) mimeFor(content.files[0].name) else "*/*").apply {
                val clip = ClipData.newRawUri("", uris[0])
                uris.drop(1).forEach { clip.addItem(ClipData.Item(it)) }
                clipData = clip
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
        }
    }
    context.startActivity(Intent.createChooser(intent, null).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
}

private fun mimeFor(name: String): String =
    android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"

private fun saveToDownloads(context: Context, item: CachedItem, content: Materialized): Int {
    val stamp = java.text.SimpleDateFormat("yyyyMMdd-HHmmss", java.util.Locale.US).format(java.util.Date(item.createdAtMs ?: System.currentTimeMillis()))
    return when (content) {
        is Materialized.Text -> {
            writeDownload(context, "clipboard-$stamp.txt", "text/plain") { it.write(content.text.toByteArray()) }
            1
        }
        is Materialized.Image -> {
            writeDownload(context, "image-$stamp.png", "image/png") { out -> content.file.inputStream().use { it.copyTo(out) } }
            1
        }
        is Materialized.Files -> {
            content.files.forEach { file -> writeDownload(context, file.name, mimeFor(file.name)) { out -> file.inputStream().use { it.copyTo(out) } } }
            content.files.size
        }
    }
}

private fun writeDownload(context: Context, name: String, mime: String, write: (java.io.OutputStream) -> Unit) {
    val resolver = context.contentResolver
    val values = ContentValues().apply {
        put(MediaStore.MediaColumns.DISPLAY_NAME, name)
        put(MediaStore.MediaColumns.MIME_TYPE, mime)
        put(MediaStore.MediaColumns.IS_PENDING, 1)
    }
    val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw java.io.IOException("Cannot create $name")
    try {
        resolver.openOutputStream(uri)?.use(write) ?: throw java.io.IOException("Cannot write $name")
        resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
    } catch (e: Exception) {
        resolver.delete(uri, null, null)
        throw e
    }
}

