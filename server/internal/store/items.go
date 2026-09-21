package store

import (
	"context"
	"database/sql"
	"errors"
	"strings"
	"time"
)

type Item struct {
	ID          string
	Seq         int64
	DeviceID    string
	Kind        string
	Size        int64
	ChunkCount  int
	CreatedAt   time.Time
	Pinned      bool
	ContentHash string
	HasThumb    bool
	StoredBytes int64
	Meta        []byte
	Payload     []byte
}

type ItemSummary struct {
	ID          string
	Seq         int64
	CreatedAt   time.Time
	Pinned      bool
	StoredBytes int64
}

type IndexEntry struct {
	ID     string
	Seq    int64
	Pinned bool
}

const headerColumns = "id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta"

func scanItem(r rowScanner, withPayload bool) (Item, error) {
	var it Item
	var created int64
	var pinned, thumb int
	dest := []any{&it.ID, &it.Seq, &it.DeviceID, &it.Kind, &it.Size, &it.ChunkCount, &created, &pinned, &it.ContentHash, &thumb, &it.StoredBytes, &it.Meta}
	if withPayload {
		dest = append(dest, &it.Payload)
	}
	if err := r.Scan(dest...); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Item{}, ErrNotFound
		}
		return Item{}, err
	}
	it.CreatedAt = time.UnixMilli(created).UTC()
	it.Pinned = pinned != 0
	it.HasThumb = thumb != 0
	return it, nil
}

func (s *Store) Item(ctx context.Context, id string, withPayload bool) (Item, error) {
	cols := headerColumns
	if withPayload {
		cols += ", payload"
	}
	return scanItem(s.db.QueryRowContext(ctx, "SELECT "+cols+" FROM items WHERE id = ?", id), withPayload)
}

func (s *Store) IsCommitted(ctx context.Context, id string) (bool, error) {
	var one int
	err := s.db.QueryRowContext(ctx, "SELECT 1 FROM items WHERE id = ?", id).Scan(&one)
	if errors.Is(err, sql.ErrNoRows) {
		return false, nil
	}
	return err == nil, err
}

func (s *Store) CommitItem(ctx context.Context, it Item) (Item, error) {
	err := s.inTx(ctx, func(tx *sql.Tx) error {
		var seq int64
		if err := tx.QueryRowContext(ctx, "UPDATE account SET current_seq = current_seq + 1 WHERE id = 1 RETURNING current_seq").Scan(&seq); err != nil {
			return err
		}
		it.Seq = seq
		var payload any
		if it.Payload != nil {
			payload = it.Payload
		}
		if _, err := tx.ExecContext(ctx,
			"INSERT INTO items (id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
			it.ID, it.Seq, it.DeviceID, it.Kind, it.Size, it.ChunkCount, it.CreatedAt.UnixMilli(), boolInt(it.Pinned), it.ContentHash, boolInt(it.HasThumb), it.StoredBytes, it.Meta, payload); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, "DELETE FROM pending_chunks WHERE id = ?", it.ID); err != nil {
			return err
		}
		_, err := tx.ExecContext(ctx, "DELETE FROM pending WHERE id = ?", it.ID)
		return err
	})
	return it, err
}

func (s *Store) InsertItemRaw(ctx context.Context, it Item) error {
	var payload any
	if it.Payload != nil {
		payload = it.Payload
	}
	_, err := s.db.ExecContext(ctx,
		"INSERT INTO items (id, seq, device_id, kind, size, chunk_count, created_at, pinned, content_hash, has_thumb, stored_bytes, meta, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
		it.ID, it.Seq, it.DeviceID, it.Kind, it.Size, it.ChunkCount, it.CreatedAt.UnixMilli(), boolInt(it.Pinned), it.ContentHash, boolInt(it.HasThumb), it.StoredBytes, it.Meta, payload)
	return err
}

func (s *Store) History(ctx context.Context, before, after *int64, limit int) ([]Item, bool, error) {
	var q string
	var args []any
	switch {
	case after != nil:
		q = "SELECT " + headerColumns + " FROM items WHERE seq > ? ORDER BY seq ASC LIMIT ?"
		args = []any{*after, limit + 1}
	case before != nil:
		q = "SELECT " + headerColumns + " FROM items WHERE seq < ? ORDER BY seq DESC LIMIT ?"
		args = []any{*before, limit + 1}
	default:
		q = "SELECT " + headerColumns + " FROM items ORDER BY seq DESC LIMIT ?"
		args = []any{limit + 1}
	}
	rows, err := s.db.QueryContext(ctx, q, args...)
	if err != nil {
		return nil, false, err
	}
	defer rows.Close()
	items := make([]Item, 0, min(limit, 64))
	for rows.Next() {
		it, err := scanItem(rows, false)
		if err != nil {
			return nil, false, err
		}
		items = append(items, it)
	}
	if err := rows.Err(); err != nil {
		return nil, false, err
	}
	more := len(items) > limit
	if more {
		items = items[:limit]
	}
	return items, more, nil
}

func (s *Store) Index(ctx context.Context) (currentSeq, stateRev int64, entries []IndexEntry, err error) {
	tx, err := s.db.BeginTx(ctx, &sql.TxOptions{ReadOnly: true})
	if err != nil {
		return 0, 0, nil, err
	}
	defer tx.Rollback()
	if err := tx.QueryRowContext(ctx, "SELECT current_seq, state_rev FROM account WHERE id = 1").Scan(&currentSeq, &stateRev); err != nil {
		return 0, 0, nil, err
	}
	rows, err := tx.QueryContext(ctx, "SELECT id, seq, pinned FROM items ORDER BY seq ASC")
	if err != nil {
		return 0, 0, nil, err
	}
	defer rows.Close()
	entries = []IndexEntry{}
	for rows.Next() {
		var e IndexEntry
		var pinned int
		if err := rows.Scan(&e.ID, &e.Seq, &pinned); err != nil {
			return 0, 0, nil, err
		}
		e.Pinned = pinned != 0
		entries = append(entries, e)
	}
	return currentSeq, stateRev, entries, rows.Err()
}

func (s *Store) Summaries(ctx context.Context) ([]ItemSummary, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT id, seq, created_at, pinned, stored_bytes FROM items ORDER BY seq ASC")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []ItemSummary
	for rows.Next() {
		var it ItemSummary
		var created int64
		var pinned int
		if err := rows.Scan(&it.ID, &it.Seq, &created, &pinned, &it.StoredBytes); err != nil {
			return nil, err
		}
		it.CreatedAt = time.UnixMilli(created).UTC()
		it.Pinned = pinned != 0
		out = append(out, it)
	}
	return out, rows.Err()
}

func (s *Store) DuplicateIDs(ctx context.Context, contentHash, exceptID string) ([]string, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT id FROM items WHERE content_hash = ? AND id != ? AND pinned = 0 ORDER BY seq ASC", contentHash, exceptID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var ids []string
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		ids = append(ids, id)
	}
	return ids, rows.Err()
}

type Usage struct {
	UsedBytes   int64
	PinnedBytes int64
	ItemCount   int64
}

func (s *Store) Usage(ctx context.Context) (Usage, error) {
	var u Usage
	err := s.db.QueryRowContext(ctx,
		"SELECT COALESCE(SUM(stored_bytes), 0), COALESCE(SUM(CASE WHEN pinned = 1 THEN stored_bytes ELSE 0 END), 0), COUNT(*) FROM items").
		Scan(&u.UsedBytes, &u.PinnedBytes, &u.ItemCount)
	return u, err
}

func (s *Store) SetPinned(ctx context.Context, id string, pinned bool) (int64, error) {
	var rev int64
	err := s.inTx(ctx, func(tx *sql.Tx) error {
		res, err := tx.ExecContext(ctx, "UPDATE items SET pinned = ? WHERE id = ?", boolInt(pinned), id)
		if err != nil {
			return err
		}
		if err := affected(res); err != nil {
			return err
		}
		return tx.QueryRowContext(ctx, "UPDATE account SET state_rev = state_rev + 1 WHERE id = 1 RETURNING state_rev").Scan(&rev)
	})
	return rev, err
}

func (s *Store) DeleteItems(ctx context.Context, ids []string) ([]string, int64, error) {
	var deleted []string
	var rev int64
	err := s.inTx(ctx, func(tx *sql.Tx) error {
		deleted = deleted[:0]
		if len(ids) == 0 {
			return nil
		}
		placeholders := strings.TrimSuffix(strings.Repeat("?,", len(ids)), ",")
		args := make([]any, len(ids))
		for i, id := range ids {
			args[i] = id
		}
		rows, err := tx.QueryContext(ctx, "DELETE FROM items WHERE id IN ("+placeholders+") RETURNING id, seq", args...)
		if err != nil {
			return err
		}
		type pair struct {
			id  string
			seq int64
		}
		var got []pair
		for rows.Next() {
			var p pair
			if err := rows.Scan(&p.id, &p.seq); err != nil {
				rows.Close()
				return err
			}
			got = append(got, p)
		}
		rows.Close()
		if err := rows.Err(); err != nil {
			return err
		}
		if len(got) == 0 {
			return nil
		}
		order := make(map[string]int64, len(got))
		for _, p := range got {
			order[p.id] = p.seq
		}
		for _, id := range ids {
			if _, ok := order[id]; ok {
				deleted = append(deleted, id)
				delete(order, id)
			}
		}
		return tx.QueryRowContext(ctx, "UPDATE account SET state_rev = state_rev + 1 WHERE id = 1 RETURNING state_rev").Scan(&rev)
	})
	if err != nil {
		return nil, 0, err
	}
	return deleted, rev, nil
}
