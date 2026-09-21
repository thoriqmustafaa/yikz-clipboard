package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

type Pending struct {
	ID           string
	DeviceID     string
	LastActivity time.Time
	ThumbSize    int64
	HasThumb     bool
	Chunks       map[int]int64
}

func (p Pending) ChunkBytes() int64 {
	var total int64
	for _, n := range p.Chunks {
		total += n
	}
	return total
}

func (s *Store) Pending(ctx context.Context, id string) (Pending, error) {
	var p Pending
	var last int64
	var thumb sql.NullInt64
	err := s.db.QueryRowContext(ctx, "SELECT id, device_id, last_activity, thumb_size FROM pending WHERE id = ?", id).Scan(&p.ID, &p.DeviceID, &last, &thumb)
	if errors.Is(err, sql.ErrNoRows) {
		return Pending{}, ErrNotFound
	}
	if err != nil {
		return Pending{}, err
	}
	p.LastActivity = time.UnixMilli(last).UTC()
	p.HasThumb = thumb.Valid
	p.ThumbSize = thumb.Int64
	p.Chunks = map[int]int64{}
	rows, err := s.db.QueryContext(ctx, "SELECT idx, size FROM pending_chunks WHERE id = ?", id)
	if err != nil {
		return Pending{}, err
	}
	defer rows.Close()
	for rows.Next() {
		var idx int
		var size int64
		if err := rows.Scan(&idx, &size); err != nil {
			return Pending{}, err
		}
		p.Chunks[idx] = size
	}
	return p, rows.Err()
}

func (s *Store) TouchPending(ctx context.Context, id, deviceID string, now time.Time) error {
	_, err := s.db.ExecContext(ctx,
		"INSERT INTO pending (id, device_id, last_activity) VALUES (?, ?, ?) ON CONFLICT (id) DO UPDATE SET last_activity = excluded.last_activity",
		id, deviceID, now.UnixMilli())
	return err
}

func (s *Store) PutPendingChunk(ctx context.Context, id, deviceID string, idx int, size int64, now time.Time) error {
	return s.inTx(ctx, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx,
			"INSERT INTO pending (id, device_id, last_activity) VALUES (?, ?, ?) ON CONFLICT (id) DO UPDATE SET last_activity = excluded.last_activity",
			id, deviceID, now.UnixMilli()); err != nil {
			return err
		}
		_, err := tx.ExecContext(ctx,
			"INSERT INTO pending_chunks (id, idx, size) VALUES (?, ?, ?) ON CONFLICT (id, idx) DO UPDATE SET size = excluded.size",
			id, idx, size)
		return err
	})
}

func (s *Store) PutPendingThumb(ctx context.Context, id, deviceID string, size int64, now time.Time) error {
	_, err := s.db.ExecContext(ctx,
		"INSERT INTO pending (id, device_id, last_activity, thumb_size) VALUES (?, ?, ?, ?) ON CONFLICT (id) DO UPDATE SET last_activity = excluded.last_activity, thumb_size = excluded.thumb_size",
		id, deviceID, now.UnixMilli(), size)
	return err
}

func (s *Store) DeletePendingChunksFrom(ctx context.Context, id string, fromIdx int) ([]int, error) {
	rows, err := s.db.QueryContext(ctx, "DELETE FROM pending_chunks WHERE id = ? AND idx >= ? RETURNING idx", id, fromIdx)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []int
	for rows.Next() {
		var idx int
		if err := rows.Scan(&idx); err != nil {
			return nil, err
		}
		out = append(out, idx)
	}
	return out, rows.Err()
}

func (s *Store) DeletePending(ctx context.Context, id string) (bool, error) {
	var found bool
	err := s.inTx(ctx, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, "DELETE FROM pending_chunks WHERE id = ?", id); err != nil {
			return err
		}
		res, err := tx.ExecContext(ctx, "DELETE FROM pending WHERE id = ?", id)
		if err != nil {
			return err
		}
		n, err := res.RowsAffected()
		found = n > 0
		return err
	})
	return found, err
}

func (s *Store) ExpiredPending(ctx context.Context, before time.Time) ([]string, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT id FROM pending WHERE last_activity < ?", before.UnixMilli())
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

func (s *Store) KnownIDs(ctx context.Context) (map[string]bool, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT id FROM items UNION SELECT id FROM pending")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]bool{}
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		out[id] = true
	}
	return out, rows.Err()
}

func (s *Store) DropPendingForCommitted(ctx context.Context) error {
	return s.inTx(ctx, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, "DELETE FROM pending_chunks WHERE id IN (SELECT id FROM items)"); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, "DELETE FROM pending_chunks WHERE id NOT IN (SELECT id FROM pending)"); err != nil {
			return err
		}
		_, err := tx.ExecContext(ctx, "DELETE FROM pending WHERE id IN (SELECT id FROM items)")
		return err
	})
}
