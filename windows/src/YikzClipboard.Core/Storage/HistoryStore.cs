using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Storage;

public sealed class SyncState
{
    public string? ServerId { get; set; }
    public long LastSeq { get; set; }
    public long? StateRev { get; set; }
    public long AppliedSeq { get; set; }
}

public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public HistoryStore(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        };
        _db = new SqliteConnection(builder.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS items(
                id TEXT PRIMARY KEY,
                seq INTEGER NOT NULL,
                device_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                size INTEGER NOT NULL,
                chunk_count INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                pinned INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                has_thumb INTEGER NOT NULL,
                stored_bytes INTEGER NOT NULL,
                meta TEXT NOT NULL,
                meta_json TEXT,
                meta_state INTEGER NOT NULL,
                search TEXT NOT NULL,
                payload BLOB,
                thumb BLOB
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS items_seq ON items(seq DESC);");
        Execute("CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT);");
    }

    public static HistoryStore InMemory() => new(":memory:");

    public event Action? Changed;

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
        }
    }

    public void Upsert(HistoryEntry entry, byte[]? payloadSealed = null)
    {
        lock (_gate)
        {
            UpsertCore(entry, payloadSealed, null);
        }
        RaiseChanged();
    }

    public void UpsertMany(IEnumerable<HistoryEntry> entries)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var e in entries)
            {
                UpsertCore(e, null, tx);
            }
            tx.Commit();
        }
        RaiseChanged();
    }

    private void UpsertCore(HistoryEntry entry, byte[]? payloadSealed, SqliteTransaction? tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO items(id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta, meta_json, meta_state, search, payload)
            VALUES($id, $seq, $device, $kind, $size, $chunks, $created, $pinned, $hash, $thumb, $stored, $meta, $metaJson, $metaState, $search, $payload)
            ON CONFLICT(id) DO UPDATE SET
                seq = excluded.seq,
                device_id = excluded.device_id,
                kind = excluded.kind,
                size = excluded.size,
                chunk_count = excluded.chunk_count,
                created_at = excluded.created_at,
                pinned = excluded.pinned,
                content_hash = excluded.content_hash,
                has_thumb = excluded.has_thumb,
                stored_bytes = excluded.stored_bytes,
                meta = excluded.meta,
                meta_json = excluded.meta_json,
                meta_state = excluded.meta_state,
                search = excluded.search,
                payload = COALESCE(excluded.payload, items.payload);
            """;
        var h = entry.Header;
        cmd.Parameters.AddWithValue("$id", h.Id);
        cmd.Parameters.AddWithValue("$seq", h.Seq);
        cmd.Parameters.AddWithValue("$device", h.DeviceId);
        cmd.Parameters.AddWithValue("$kind", h.Kind);
        cmd.Parameters.AddWithValue("$size", h.Size);
        cmd.Parameters.AddWithValue("$chunks", h.ChunkCount);
        cmd.Parameters.AddWithValue("$created", h.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$pinned", h.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$hash", h.ContentHash);
        cmd.Parameters.AddWithValue("$thumb", h.HasThumb ? 1 : 0);
        cmd.Parameters.AddWithValue("$stored", h.StoredBytes);
        cmd.Parameters.AddWithValue("$meta", h.Meta);
        cmd.Parameters.AddWithValue("$metaJson", entry.Meta != null ? Encoding.UTF8.GetString(MetaCodec.Serialize(entry.Meta)) : DBNull.Value);
        cmd.Parameters.AddWithValue("$metaState", (int)entry.MetaState);
        cmd.Parameters.AddWithValue("$search", entry.SearchText);
        cmd.Parameters.Add("$payload", SqliteType.Blob).Value = payloadSealed != null ? payloadSealed : DBNull.Value;
        cmd.ExecuteNonQuery();
    }

    public int Delete(IEnumerable<string> ids)
    {
        var count = 0;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM items WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                count += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (count > 0)
        {
            RaiseChanged();
        }
        return count;
    }

    public bool SetPinned(string id, bool pinned)
    {
        int n;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE items SET pinned = $p WHERE id = $id AND pinned <> $p;";
            cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            n = cmd.ExecuteNonQuery();
        }
        if (n > 0)
        {
            RaiseChanged();
        }
        return n > 0;
    }

    public void SetPinnedMany(IReadOnlyDictionary<string, bool> pins)
    {
        var changed = 0;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var (id, pinned) in pins)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE items SET pinned = $p WHERE id = $id AND pinned <> $p;";
                cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
                changed += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (changed > 0)
        {
            RaiseChanged();
        }
    }

    private const string SelectColumns = "id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta, meta_json, meta_state";

    private static HistoryEntry Read(SqliteDataReader r)
    {
        var header = new ItemHeader
        {
            Id = r.GetString(0),
            Seq = r.GetInt64(1),
            DeviceId = r.GetString(2),
            Kind = r.GetString(3),
            Size = r.GetInt64(4),
            ChunkCount = r.GetInt32(5),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
            Pinned = r.GetInt64(7) != 0,
            ContentHash = r.GetString(8),
            HasThumb = r.GetInt64(9) != 0,
            StoredBytes = r.GetInt64(10),
            Meta = r.GetString(11),
        };
        ItemMeta? meta = null;
        if (!r.IsDBNull(12))
        {
            try
            {
                meta = MetaCodec.Deserialize(Encoding.UTF8.GetBytes(r.GetString(12)));
            }
            catch
            {
                meta = null;
            }
        }
        var state = (MetaState)r.GetInt32(13);
        if (state == MetaState.Ok && meta == null)
        {
            state = MetaState.Corrupt;
        }
        return new HistoryEntry(header, meta, state);
    }

    public HistoryEntry? Get(string id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {SelectColumns} FROM items WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public HistoryEntry? Newest()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {SelectColumns} FROM items ORDER BY seq DESC LIMIT 1;";
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public List<HistoryEntry> List(int limit = 5000, string? search = null, string? kind = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
            {
                where.Add("search LIKE $q ESCAPE '\\'");
                var escaped = search.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                cmd.Parameters.AddWithValue("$q", "%" + escaped + "%");
            }
            if (!string.IsNullOrEmpty(kind))
            {
                where.Add("kind = $kind");
                cmd.Parameters.AddWithValue("$kind", kind);
            }
            cmd.CommandText = $"SELECT {SelectColumns} FROM items" +
                (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                " ORDER BY seq DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<HistoryEntry>();
            while (r.Read())
            {
                list.Add(Read(r));
            }
            return list;
        }
    }

    public Dictionary<string, (long Seq, bool Pinned)> Index()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, seq, pinned FROM items;";
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, (long, bool)>(StringComparer.Ordinal);
            while (r.Read())
            {
                map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2) != 0);
            }
            return map;
        }
    }

    public long? MinSeq()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MIN(seq) FROM items;";
            var v = cmd.ExecuteScalar();
            return v is long l ? l : null;
        }
    }

    public int Count()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM items;";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public byte[]? GetPayload(string id) => GetBlob(id, "payload");

    public void SetPayload(string id, byte[] sealedPayload) => SetBlob(id, "payload", sealedPayload);

    public byte[]? GetThumb(string id) => GetBlob(id, "thumb");

    public void SetThumb(string id, byte[] thumb) => SetBlob(id, "thumb", thumb);

    private byte[]? GetBlob(string id, string column)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {column} FROM items WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            var v = cmd.ExecuteScalar();
            return v as byte[];
        }
    }

    private void SetBlob(string id, string column, byte[] value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"UPDATE items SET {column} = $v WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.Add("$v", SqliteType.Blob).Value = value;
            cmd.ExecuteNonQuery();
        }
    }

    public void TrimTo(int maxItems)
    {
        int n;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE pinned = 0 AND id NOT IN (SELECT id FROM items ORDER BY seq DESC LIMIT $max);";
            cmd.Parameters.AddWithValue("$max", maxItems);
            n = cmd.ExecuteNonQuery();
        }
        if (n > 0)
        {
            RaiseChanged();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Execute("DELETE FROM items;");
        }
        RaiseChanged();
    }

    public string? GetValue(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetValue(string key, string? value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            if (value == null)
            {
                cmd.CommandText = "DELETE FROM kv WHERE key = $k;";
                cmd.Parameters.AddWithValue("$k", key);
            }
            else
            {
                cmd.CommandText = "INSERT INTO kv(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
            }
            cmd.ExecuteNonQuery();
        }
    }

    public SyncState LoadSyncState()
    {
        return new SyncState
        {
            ServerId = GetValue("server_id"),
            LastSeq = ParseLong(GetValue("last_seq")) ?? 0,
            StateRev = ParseLong(GetValue("state_rev")),
            AppliedSeq = ParseLong(GetValue("applied_seq")) ?? 0,
        };
    }

    public void SaveSyncState(SyncState state)
    {
        SetValue("server_id", state.ServerId);
        SetValue("last_seq", state.LastSeq.ToString(CultureInfo.InvariantCulture));
        SetValue("state_rev", state.StateRev?.ToString(CultureInfo.InvariantCulture));
        SetValue("applied_seq", state.AppliedSeq.ToString(CultureInfo.InvariantCulture));
    }

    private static long? ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }
}
