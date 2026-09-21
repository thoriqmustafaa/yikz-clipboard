package dev.yikz.clipboard.ui.screens

import android.graphics.Bitmap
import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.ContentPaste
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.SearchOff
import androidx.compose.material.icons.outlined.Warning
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Surface
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.yikz.clipboard.core.CachedItem
import dev.yikz.clipboard.core.Kind
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.sync.SyncController
import dev.yikz.clipboard.ui.components.EmptyState
import dev.yikz.clipboard.ui.components.ItemType
import dev.yikz.clipboard.ui.components.KindBadge
import dev.yikz.clipboard.ui.components.ShimmerRow
import dev.yikz.clipboard.ui.components.StatusPill
import dev.yikz.clipboard.ui.components.icon
import dev.yikz.clipboard.ui.components.statusInfo
import dev.yikz.clipboard.ui.components.title
import dev.yikz.clipboard.ui.components.type
import dev.yikz.clipboard.util.Format
import kotlinx.coroutines.launch

private data class Group(val label: String, val items: List<CachedItem>)

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun HistoryScreen() {
    val context = LocalContext.current
    val g = context.graph
    val items by g.db.items.collectAsStateWithLifecycle()
    val connection by g.sync.connection.collectAsStateWithLifecycle()
    val settings by g.sync.settings.collectAsStateWithLifecycle()
    val syncing by g.sync.syncing.collectAsStateWithLifecycle()
    val names by g.sync.deviceNames.collectAsStateWithLifecycle()
    val warning by g.sync.storageWarning.collectAsStateWithLifecycle()
    var query by rememberSaveable { mutableStateOf("") }
    var filter by rememberSaveable { mutableStateOf<ItemType?>(null) }
    var selectedId by rememberSaveable { mutableStateOf<String?>(null) }
    val pendingDeletes = remember { mutableStateListOf<String>() }
    val snackbar = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val listState = rememberLazyListState()

    val groups = remember(items, query, filter, pendingDeletes.toList(), names) {
        val q = query.trim().lowercase()
        val visible = items.filter { item ->
            item.id !in pendingDeletes &&
                (filter == null || item.type() == filter) &&
                (q.isEmpty() || searchText(item, names).contains(q))
        }
        val pinned = visible.filter { it.pinned }
        val rest = visible.filter { !it.pinned }
        buildList {
            if (pinned.isNotEmpty()) add(Group("Pinned", pinned))
            rest.groupBy { Format.dayLabel(it.createdAtMs ?: 0) }.forEach { (label, list) -> add(Group(label, list)) }
        }
    }

    fun requestDelete(item: CachedItem) {
        pendingDeletes += item.id
        if (selectedId == item.id) selectedId = null
        scope.launch {
            snackbar.currentSnackbarData?.dismiss()
            val result = snackbar.showSnackbar("Item deleted", actionLabel = "Undo", duration = SnackbarDuration.Short)
            if (result == SnackbarResult.ActionPerformed) {
                pendingDeletes -= item.id
            } else {
                try {
                    g.sync.delete(item.id)
                } catch (e: Exception) {
                    snackbar.showSnackbar("Could not delete: ${SyncController.errorMessage(e)}")
                } finally {
                    pendingDeletes -= item.id
                }
            }
        }
    }

    val nearEnd by remember {
        derivedStateOf {
            val info = listState.layoutInfo
            val last = info.visibleItemsInfo.lastOrNull()?.index ?: 0
            info.totalItemsCount > 0 && last >= info.totalItemsCount - 3
        }
    }
    val fabExpanded by remember { derivedStateOf { listState.firstVisibleItemIndex < 2 } }
    var canLoadMore by remember { mutableStateOf(true) }
    LaunchedEffect(nearEnd, items.size) {
        if (nearEnd && canLoadMore && items.size >= 100 && query.isEmpty() && filter == null) {
            canLoadMore = try {
                g.sync.loadOlder()
            } catch (_: Exception) {
                false
            }
        }
    }

    Scaffold(
        contentWindowInsets = WindowInsets(0),
        snackbarHost = { SnackbarHost(snackbar) },
        floatingActionButton = {
            ExtendedFloatingActionButton(
                onClick = { g.sync.sendClip(g.clipboard.currentClip(), manual = true) },
                icon = { Icon(Icons.Outlined.ContentPaste, contentDescription = null) },
                text = { Text("Send clipboard") },
                expanded = fabExpanded,
            )
        },
    ) { inner ->
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(bottom = inner.calculateBottomPadding() + 96.dp),
        ) {
            item(key = "header") {
                Column(Modifier.statusBarsPadding().padding(start = 20.dp, end = 20.dp, top = 20.dp, bottom = 12.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("Clipboard", style = MaterialTheme.typography.headlineLarge, modifier = Modifier.weight(1f))
                        StatusPill(
                            statusInfo(connection, settings.paused, settings.deviceId, syncing),
                            onClick = { if (settings.paused) g.sync.resume() else g.sync.reconnectNow() },
                        )
                    }
                    if (warning != null) {
                        Spacer(Modifier.height(12.dp))
                        Surface(color = MaterialTheme.colorScheme.errorContainer, shape = MaterialTheme.shapes.medium) {
                            Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Outlined.Warning, contentDescription = null, tint = MaterialTheme.colorScheme.onErrorContainer)
                                Spacer(Modifier.width(12.dp))
                                Text(
                                    "Server disk is almost full. Large uploads are paused.",
                                    color = MaterialTheme.colorScheme.onErrorContainer,
                                    style = MaterialTheme.typography.bodyMedium,
                                )
                            }
                        }
                    }
                    Spacer(Modifier.height(16.dp))
                    SearchField(query, onChange = { query = it })
                }
            }
            item(key = "filters") {
                LazyRow(
                    contentPadding = PaddingValues(horizontal = 20.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    modifier = Modifier.padding(bottom = 8.dp),
                ) {
                    item {
                        FilterChip(
                            selected = filter == null,
                            onClick = { filter = null },
                            label = { Text("All") },
                            shape = CircleShape,
                        )
                    }
                    items(ItemType.entries) { type ->
                        FilterChip(
                            selected = filter == type,
                            onClick = { filter = if (filter == type) null else type },
                            label = { Text(type.label) },
                            leadingIcon = { Icon(type.icon(), contentDescription = null, modifier = Modifier.size(FilterChipDefaults.IconSize)) },
                            shape = CircleShape,
                        )
                    }
                }
            }
            when {
                items.isEmpty() && syncing -> items(6, key = { "shimmer$it" }) { ShimmerRow() }
                items.isEmpty() -> item(key = "empty") {
                    EmptyState(
                        icon = Icons.Outlined.Inbox,
                        title = "Nothing here yet",
                        message = "Copy something on another device, or tap Send clipboard to share what is on this one.",
                    )
                }
                groups.isEmpty() -> item(key = "nomatch") {
                    EmptyState(
                        icon = Icons.Outlined.SearchOff,
                        title = "No matches",
                        message = "Try another search or filter.",
                    )
                }
                else -> groups.forEach { group ->
                    stickyHeader(key = "h-${group.label}") {
                        Text(
                            group.label,
                            style = MaterialTheme.typography.labelLarge,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier
                                .fillMaxWidth()
                                .background(MaterialTheme.colorScheme.surface)
                                .padding(start = 20.dp, end = 20.dp, top = 14.dp, bottom = 6.dp),
                        )
                    }
                    items(group.items, key = { it.id }) { item ->
                        SwipeRow(
                            item = item,
                            deviceName = names[item.deviceId] ?: if (item.deviceId == settings.deviceId) "This device" else "Unknown device",
                            onClick = { selectedId = item.id },
                            onDelete = { requestDelete(item) },
                            modifier = Modifier.animateItem(),
                        )
                    }
                }
            }
        }
    }

    val selected = selectedId?.let { id -> items.firstOrNull { it.id == id } }
    if (selected != null) {
        ItemDetailSheet(
            item = selected,
            deviceName = names[selected.deviceId] ?: if (selected.deviceId == settings.deviceId) "This device" else "Unknown device",
            onDismiss = { selectedId = null },
            onDelete = { requestDelete(selected) },
            onMessage = { message -> scope.launch { snackbar.showSnackbar(message) } },
        )
    }
}

private fun searchText(item: CachedItem, names: Map<String, String>): String = buildString {
    append(item.title().lowercase())
    append('\n')
    append(item.preview.lowercase())
    item.meta?.files?.forEach { append('\n').append(it.name.lowercase()) }
    item.meta?.sourceApp?.let { append('\n').append(it.lowercase()) }
    names[item.deviceId]?.let { append('\n').append(it.lowercase()) }
}

@Composable
private fun SearchField(value: String, onChange: (String) -> Unit) {
    Surface(shape = CircleShape, color = MaterialTheme.colorScheme.surfaceContainerHigh, modifier = Modifier.fillMaxWidth().height(52.dp)) {
        Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Outlined.Search, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.width(12.dp))
            Box(Modifier.weight(1f)) {
                if (value.isEmpty()) {
                    Text("Search clipboard history", color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyLarge)
                }
                BasicTextField(
                    value = value,
                    onValueChange = onChange,
                    singleLine = true,
                    textStyle = MaterialTheme.typography.bodyLarge.copy(color = MaterialTheme.colorScheme.onSurface),
                    cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            if (value.isNotEmpty()) {
                IconButton(onClick = { onChange("") }) { Icon(Icons.Outlined.Close, contentDescription = "Clear search") }
            }
        }
    }
}

@Composable
private fun SwipeRow(item: CachedItem, deviceName: String, onClick: () -> Unit, onDelete: () -> Unit, modifier: Modifier = Modifier) {
    val state = rememberSwipeToDismissBoxState()
    SwipeToDismissBox(
        state = state,
        modifier = modifier,
        enableDismissFromStartToEnd = false,
        onDismiss = { value -> if (value == SwipeToDismissBoxValue.EndToStart) onDelete() },
        backgroundContent = {
            val color by animateColorAsState(
                if (state.targetValue == SwipeToDismissBoxValue.EndToStart) MaterialTheme.colorScheme.errorContainer else MaterialTheme.colorScheme.surface,
                label = "swipe",
            )
            Box(Modifier.fillMaxSize().background(color).padding(horizontal = 28.dp), contentAlignment = Alignment.CenterEnd) {
                Icon(Icons.Outlined.Delete, contentDescription = "Delete", tint = MaterialTheme.colorScheme.onErrorContainer)
            }
        },
    ) {
        ItemRow(item, deviceName, onClick)
    }
}

@Composable
private fun ItemRow(item: CachedItem, deviceName: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface)
            .clickable(onClick = onClick)
            .padding(horizontal = 20.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Thumbnail(item)
        Spacer(Modifier.width(16.dp))
        Column(Modifier.weight(1f)) {
            Text(
                item.title(),
                style = MaterialTheme.typography.bodyLarge,
                maxLines = if (item.kind == Kind.TEXT) 2 else 1,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(2.dp))
            Text(
                listOf(deviceName, Format.time(item.createdAtMs ?: 0), Format.bytes(item.size)).joinToString("  ·  "),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        if (item.pinned) {
            Spacer(Modifier.width(8.dp))
            Icon(Icons.Filled.PushPin, contentDescription = "Pinned", tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
        }
    }
}

@Composable
fun Thumbnail(item: CachedItem, size: androidx.compose.ui.unit.Dp = 44.dp) {
    val g = LocalContext.current.graph
    val bitmap by produceState<Bitmap?>(initialValue = g.thumbnails.cached(item.id), item.id) {
        if (value == null && item.kind == Kind.IMAGE) {
            val session = g.sync.thumbnailSession() ?: return@produceState
            value = g.thumbnails.load(item, session.api, session.codec, g.db)
        }
    }
    val bmp = bitmap
    if (bmp != null) {
        Image(
            bitmap = bmp.asImageBitmap(),
            contentDescription = null,
            contentScale = ContentScale.Crop,
            modifier = Modifier.size(size).clip(RoundedCornerShape(size * 0.3f)).background(MaterialTheme.colorScheme.surfaceContainerHigh),
        )
    } else {
        KindBadge(item.type(), size = size)
    }
}
