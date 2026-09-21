package dev.yikz.clipboard.data

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import dev.yikz.clipboard.core.CachedItem
import dev.yikz.clipboard.core.ItemCodec
import dev.yikz.clipboard.core.SyncState
import dev.yikz.clipboard.core.SyncStore
import dev.yikz.clipboard.core.Timestamps
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

class ClipDatabase(context: Context) : SQLiteOpenHelper(context, "clips.db", null, 1), SyncStore {
    private val lock = Any()
    private var codec: ItemCodec? = null
    private val memory = LinkedHashMap<String, CachedItem>()
    private var loaded = false
    private val _items = MutableStateFlow<List<CachedItem>>(emptyList())
    val items: StateFlow<List<CachedItem>> = _items.asStateFlow()

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE items (
                id TEXT PRIMARY KEY,
                seq INTEGER NOT NULL,
                device_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                size INTEGER NOT NULL,
                chunk_count INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                pinned INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                has_thumb INTEGER NOT NULL,
                stored_bytes INTEGER NOT NULL,
                meta TEXT NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL("CREATE INDEX items_seq ON items(seq)")
        db.execSQL("CREATE TABLE payloads (id TEXT PRIMARY KEY, sealed TEXT NOT NULL)")
        db.execSQL("CREATE TABLE kv (k TEXT PRIMARY KEY, v TEXT)")
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        db.execSQL("DROP TABLE IF EXISTS items")
        db.execSQL("DROP TABLE IF EXISTS payloads")
        db.execSQL("DROP TABLE IF EXISTS kv")
        onCreate(db)
    }

    fun attach(codec: ItemCodec?) = synchronized(lock) {
        this.codec = codec
        memory.clear()
        loaded = false
        if (codec != null) load()
        publish()
    }

    private fun load() {
        val c = codec ?: return
        readableDatabase.rawQuery(
            "SELECT id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta FROM items ORDER BY seq DESC",
            null,
        ).use { cursor ->
            while (cursor.moveToNext()) {
                val decoded = c.decode(
                    dev.yikz.clipboard.core.ItemHeader(
                        id = cursor.getString(0),
                        seq = cursor.getLong(1),
                        deviceId = cursor.getString(2),
                        kind = cursor.getString(3),
                        size = cursor.getLong(4),
                        chunkCount = cursor.getInt(5),
                        createdAt = cursor.getString(6),
                        pinned = cursor.getInt(7) != 0,
                        contentHash = cursor.getString(8),
                        hasThumb = cursor.getInt(9) != 0,
                        storedBytes = cursor.getLong(10),
                        meta = cursor.getString(11),
                    ),
                )
                memory[decoded.id] = decoded
            }
        }
        loaded = true
    }

    private fun publish() {
        _items.value = memory.values.sortedByDescending { it.seq }
    }

    private fun kvGet(key: String): String? =
        readableDatabase.rawQuery("SELECT v FROM kv WHERE k = ?", arrayOf(key)).use { if (it.moveToFirst()) it.getString(0) else null }

    private fun kvPut(db: SQLiteDatabase, key: String, value: String?) {
        if (value == null) {
            db.delete("kv", "k = ?", arrayOf(key))
        } else {
            db.insertWithOnConflict("kv", null, ContentValues().apply {
                put("k", key)
                put("v", value)
            }, SQLiteDatabase.CONFLICT_REPLACE)
        }
    }

    override fun state(): SyncState = synchronized(lock) {
        SyncState(
            serverId = kvGet("server_id"),
            lastSeq = kvGet("last_seq")?.toLongOrNull() ?: 0,
            stateRev = kvGet("state_rev")?.toLongOrNull(),
            appliedSeq = kvGet("applied_seq")?.toLongOrNull() ?: 0,
        )
    }

    override fun saveState(state: SyncState) = synchronized(lock) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            kvPut(db, "server_id", state.serverId)
            kvPut(db, "last_seq", state.lastSeq.toString())
            kvPut(db, "state_rev", state.stateRev?.toString())
            kvPut(db, "applied_seq", state.appliedSeq.toString())
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    override fun upsert(items: List<CachedItem>) = synchronized(lock) {
        if (items.isEmpty()) return@synchronized
        val db = writableDatabase
        db.beginTransaction()
        try {
            for (item in items) {
                db.insertWithOnConflict("items", null, ContentValues().apply {
                    put("id", item.id)
                    put("seq", item.seq)
                    put("device_id", item.deviceId)
                    put("kind", item.kind)
                    put("size", item.size)
                    put("chunk_count", item.chunkCount)
                    put("created_at", item.createdAt)
                    put("pinned", if (item.pinned) 1 else 0)
                    put("content_hash", item.contentHash)
                    put("has_thumb", if (item.hasThumb) 1 else 0)
                    put("stored_bytes", item.storedBytes)
                    put("meta", item.sealedMeta)
                }, SQLiteDatabase.CONFLICT_REPLACE)
                item.payload?.let { payload ->
                    db.insertWithOnConflict("payloads", null, ContentValues().apply {
                        put("id", item.id)
                        put("sealed", payload)
                    }, SQLiteDatabase.CONFLICT_REPLACE)
                }
                memory[item.id] = item.copy(payload = null)
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
        publish()
    }

    override fun delete(ids: Collection<String>) = synchronized(lock) {
        if (ids.isEmpty()) return@synchronized
        val db = writableDatabase
        db.beginTransaction()
        try {
            for (id in ids) {
                db.delete("items", "id = ?", arrayOf(id))
                db.delete("payloads", "id = ?", arrayOf(id))
                memory.remove(id)
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
        publish()
    }

    override fun setPinned(id: String, pinned: Boolean) = synchronized(lock) {
        writableDatabase.update("items", ContentValues().apply { put("pinned", if (pinned) 1 else 0) }, "id = ?", arrayOf(id))
        memory[id]?.let { memory[id] = it.copy(pinned = pinned) }
        publish()
    }

    override fun reconcile(index: Map<String, Boolean>, upToSeq: Long) = synchronized(lock) {
        val stale = memory.values.filter { it.seq <= upToSeq && it.id !in index }.map { it.id }
        val db = writableDatabase
        db.beginTransaction()
        try {
            for (id in stale) {
                db.delete("items", "id = ?", arrayOf(id))
                db.delete("payloads", "id = ?", arrayOf(id))
                memory.remove(id)
            }
            for ((id, pinned) in index) {
                val current = memory[id] ?: continue
                if (current.pinned != pinned) {
                    db.update("items", ContentValues().apply { put("pinned", if (pinned) 1 else 0) }, "id = ?", arrayOf(id))
                    memory[id] = current.copy(pinned = pinned)
                }
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
        publish()
    }

    override fun clearAll() = synchronized(lock) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            db.delete("items", null, null)
            db.delete("payloads", null, null)
            db.delete("kv", null, null)
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
        memory.clear()
        publish()
    }

    override fun newestContentHash(): String? = synchronized(lock) { memory.values.maxByOrNull { it.seq }?.contentHash }

    override fun get(id: String): CachedItem? = synchronized(lock) { memory[id] }

    override fun payload(id: String): String? = synchronized(lock) {
        readableDatabase.rawQuery("SELECT sealed FROM payloads WHERE id = ?", arrayOf(id)).use { if (it.moveToFirst()) it.getString(0) else null }
    }

    override fun oldestSeq(): Long? = synchronized(lock) { memory.values.minOfOrNull { it.seq } }

    fun createdAtMs(item: CachedItem): Long = item.createdAtMs ?: Timestamps.parseOrNull(item.createdAt) ?: 0L
}
