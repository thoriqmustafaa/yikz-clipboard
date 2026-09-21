package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"net/url"

	_ "modernc.org/sqlite"
)

var ErrNotFound = errors.New("not found")

const schema = `
CREATE TABLE IF NOT EXISTS account (
	id          INTEGER PRIMARY KEY CHECK (id = 1),
	username    TEXT    NOT NULL,
	salt        BLOB    NOT NULL,
	server_id   TEXT    NOT NULL,
	key_check   TEXT,
	current_seq INTEGER NOT NULL DEFAULT 0,
	state_rev   INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS devices (
	id           TEXT    PRIMARY KEY,
	name         TEXT    NOT NULL,
	platform     TEXT    NOT NULL,
	created_at   INTEGER NOT NULL,
	last_seen_at INTEGER NOT NULL,
	revoked      INTEGER NOT NULL DEFAULT 0,
	token_hash   TEXT UNIQUE
);
CREATE TABLE IF NOT EXISTS items (
	id           TEXT    PRIMARY KEY,
	seq          INTEGER NOT NULL UNIQUE,
	device_id    TEXT    NOT NULL,
	kind         TEXT    NOT NULL,
	size         INTEGER NOT NULL,
	chunk_count  INTEGER NOT NULL,
	created_at   INTEGER NOT NULL,
	pinned       INTEGER NOT NULL DEFAULT 0,
	content_hash TEXT    NOT NULL,
	has_thumb    INTEGER NOT NULL DEFAULT 0,
	stored_bytes INTEGER NOT NULL,
	meta         BLOB    NOT NULL,
	payload      BLOB
);
CREATE INDEX IF NOT EXISTS items_content_hash ON items (content_hash);
CREATE TABLE IF NOT EXISTS pending (
	id            TEXT    PRIMARY KEY,
	device_id     TEXT    NOT NULL,
	last_activity INTEGER NOT NULL,
	thumb_size    INTEGER
);
CREATE TABLE IF NOT EXISTS pending_chunks (
	id   TEXT    NOT NULL,
	idx  INTEGER NOT NULL,
	size INTEGER NOT NULL,
	PRIMARY KEY (id, idx)
) WITHOUT ROWID;
`

type Store struct {
	db *sql.DB
}

func Open(ctx context.Context, path string) (*Store, error) {
	q := url.Values{}
	for _, p := range []string{
		"busy_timeout(10000)",
		"journal_mode(WAL)",
		"synchronous(NORMAL)",
		"foreign_keys(ON)",
		"auto_vacuum(INCREMENTAL)",
		"cache_size(-2000)",
		"temp_store(MEMORY)",
	} {
		q.Add("_pragma", p)
	}
	q.Set("_txlock", "immediate")
	dsn := "file:" + path + "?" + q.Encode()
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	db.SetMaxOpenConns(4)
	db.SetMaxIdleConns(2)
	if err := db.PingContext(ctx); err != nil {
		db.Close()
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	var mode string
	if err := db.QueryRowContext(ctx, "PRAGMA journal_mode").Scan(&mode); err != nil {
		db.Close()
		return nil, fmt.Errorf("read journal mode: %w", err)
	}
	if mode != "wal" {
		db.Close()
		return nil, fmt.Errorf("sqlite journal mode is %q, expected wal", mode)
	}
	if _, err := db.ExecContext(ctx, schema); err != nil {
		db.Close()
		return nil, fmt.Errorf("migrate schema: %w", err)
	}
	return &Store{db: db}, nil
}

func (s *Store) Close() error { return s.db.Close() }

func (s *Store) DB() *sql.DB { return s.db }

func (s *Store) Vacuum(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, "PRAGMA incremental_vacuum")
	return err
}

func (s *Store) Checkpoint(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, "PRAGMA wal_checkpoint(TRUNCATE)")
	return err
}

func (s *Store) inTx(ctx context.Context, fn func(*sql.Tx) error) error {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	if err := fn(tx); err != nil {
		tx.Rollback()
		return err
	}
	return tx.Commit()
}

func boolInt(b bool) int {
	if b {
		return 1
	}
	return 0
}
