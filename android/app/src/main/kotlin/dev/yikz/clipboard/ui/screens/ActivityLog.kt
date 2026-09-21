package dev.yikz.clipboard.ui.screens

import android.content.Intent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.DeleteSweep
import androidx.compose.material.icons.outlined.IosShare
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.automirrored.outlined.Subject
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.yikz.clipboard.core.LogLevel
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.ui.components.EmptyState
import dev.yikz.clipboard.ui.theme.MonoStyle
import dev.yikz.clipboard.ui.theme.StatusColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ActivityLogScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val g = context.graph
    val entries by g.log.entries.collectAsStateWithLifecycle()
    var minLevel by rememberSaveable { mutableStateOf(LogLevel.DEBUG) }
    var query by rememberSaveable { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    val format = remember { SimpleDateFormat("HH:mm:ss.SSS", Locale.US) }
    val visible = remember(entries, minLevel, query) {
        val q = query.trim().lowercase()
        entries.asReversed().filter { it.level >= minLevel && (q.isEmpty() || it.message.lowercase().contains(q) || it.tag.contains(q)) }
    }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("Activity") },
            navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "Back") } },
            actions = {
                IconButton(onClick = {
                    scope.launch {
                        val file = withContext(Dispatchers.IO) { g.log.exportTo(File(context.cacheDir, "logs/yikz-clipboard-log.txt")) }
                        val uri = g.clipboard.uriFor(file)
                        val intent = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_STREAM, uri)
                            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                        context.startActivity(Intent.createChooser(intent, "Export log").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
                    }
                }) { Icon(Icons.Outlined.IosShare, contentDescription = "Export") }
                IconButton(onClick = { g.log.clear() }) { Icon(Icons.Outlined.DeleteSweep, contentDescription = "Clear") }
            },
        )
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            placeholder = { Text("Filter messages") },
            leadingIcon = { Icon(Icons.Outlined.Search, contentDescription = null) },
            singleLine = true,
            shape = CircleShape,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
        )
        LazyRow(
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(LogLevel.entries) { level ->
                FilterChip(
                    selected = minLevel == level,
                    onClick = { minLevel = level },
                    label = { Text(if (level == LogLevel.DEBUG) "All" else level.name.lowercase().replaceFirstChar { it.uppercase() } + "+") },
                    shape = CircleShape,
                )
            }
        }
        if (visible.isEmpty()) {
            EmptyState(Icons.AutoMirrored.Outlined.Subject, "No activity", "Connection events, syncs and errors show up here.")
        } else {
            LazyColumn(Modifier.fillMaxSize().navigationBarsPadding(), contentPadding = PaddingValues(bottom = 16.dp)) {
                items(visible, key = { it.id }) { entry ->
                    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
                        Text(
                            entry.level.name.first().toString(),
                            style = MonoStyle,
                            color = levelColor(entry.level),
                        )
                        Spacer(Modifier.width(10.dp))
                        Column(Modifier.weight(1f)) {
                            Text(
                                "${format.format(Date(entry.timeMs))}  ${entry.tag}",
                                style = MonoStyle,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Text(entry.message, style = MonoStyle, color = if (entry.level >= LogLevel.WARN) levelColor(entry.level) else MaterialTheme.colorScheme.onSurface)
                        }
                    }
                    HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f))
                }
            }
        }
    }
}

@Composable
private fun levelColor(level: LogLevel): Color = when (level) {
    LogLevel.DEBUG -> MaterialTheme.colorScheme.onSurfaceVariant
    LogLevel.INFO -> MaterialTheme.colorScheme.primary
    LogLevel.WARN -> StatusColors.warning
    LogLevel.ERROR -> MaterialTheme.colorScheme.error
}
