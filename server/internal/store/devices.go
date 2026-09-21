package store

import (
	"context"
	"database/sql"
	"errors"
	"time"
)

type Device struct {
	ID         string
	Name       string
	Platform   string
	CreatedAt  time.Time
	LastSeenAt time.Time
	Revoked    bool
}

const deviceColumns = "id, name, platform, created_at, last_seen_at, revoked"

type rowScanner interface {
	Scan(dest ...any) error
}

func scanDevice(r rowScanner) (Device, error) {
	var d Device
	var created, seen int64
	var revoked int
	if err := r.Scan(&d.ID, &d.Name, &d.Platform, &created, &seen, &revoked); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Device{}, ErrNotFound
		}
		return Device{}, err
	}
	d.CreatedAt = time.UnixMilli(created).UTC()
	d.LastSeenAt = time.UnixMilli(seen).UTC()
	d.Revoked = revoked != 0
	return d, nil
}

func (s *Store) CreateDevice(ctx context.Context, d Device, tokenHash string) error {
	var th any
	if tokenHash != "" {
		th = tokenHash
	}
	_, err := s.db.ExecContext(ctx,
		"INSERT INTO devices (id, name, platform, created_at, last_seen_at, revoked, token_hash) VALUES (?, ?, ?, ?, ?, ?, ?)",
		d.ID, d.Name, d.Platform, d.CreatedAt.UnixMilli(), d.LastSeenAt.UnixMilli(), boolInt(d.Revoked), th)
	return err
}

func (s *Store) Device(ctx context.Context, id string) (Device, error) {
	return scanDevice(s.db.QueryRowContext(ctx, "SELECT "+deviceColumns+" FROM devices WHERE id = ?", id))
}

func (s *Store) DeviceByTokenHash(ctx context.Context, hash string) (Device, error) {
	return scanDevice(s.db.QueryRowContext(ctx, "SELECT "+deviceColumns+" FROM devices WHERE token_hash = ? AND revoked = 0", hash))
}

func (s *Store) Devices(ctx context.Context) ([]Device, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT "+deviceColumns+" FROM devices ORDER BY created_at ASC, rowid ASC")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Device
	for rows.Next() {
		d, err := scanDevice(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, d)
	}
	return out, rows.Err()
}

func (s *Store) ReissueDevice(ctx context.Context, id, name, platform, tokenHash string, now time.Time) error {
	res, err := s.db.ExecContext(ctx,
		"UPDATE devices SET name = ?, platform = ?, revoked = 0, token_hash = ?, last_seen_at = ? WHERE id = ?",
		name, platform, tokenHash, now.UnixMilli(), id)
	if err != nil {
		return err
	}
	return affected(res)
}

func (s *Store) RenameDevice(ctx context.Context, id, name string) error {
	res, err := s.db.ExecContext(ctx, "UPDATE devices SET name = ? WHERE id = ?", name, id)
	if err != nil {
		return err
	}
	return affected(res)
}

func (s *Store) RevokeDevice(ctx context.Context, id string) error {
	res, err := s.db.ExecContext(ctx, "UPDATE devices SET revoked = 1, token_hash = NULL WHERE id = ?", id)
	if err != nil {
		return err
	}
	return affected(res)
}

func (s *Store) DeleteDevice(ctx context.Context, id string) error {
	res, err := s.db.ExecContext(ctx, "DELETE FROM devices WHERE id = ?", id)
	if err != nil {
		return err
	}
	return affected(res)
}

func (s *Store) TouchDevice(ctx context.Context, id string, now time.Time) error {
	_, err := s.db.ExecContext(ctx, "UPDATE devices SET last_seen_at = ? WHERE id = ? AND last_seen_at < ?", now.UnixMilli(), id, now.UnixMilli())
	return err
}

func affected(res sql.Result) error {
	n, err := res.RowsAffected()
	if err != nil {
		return err
	}
	if n == 0 {
		return ErrNotFound
	}
	return nil
}
