package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"
	"time"
)

func openTest(t *testing.T) *Store {
	t.Helper()
	s, err := Open(context.Background(), filepath.Join(t.TempDir(), "test.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	return s
}

func TestWALAndAccount(t *testing.T) {
	ctx := context.Background()
	s := openTest(t)
	var mode string
	if err := s.DB().QueryRowContext(ctx, "PRAGMA journal_mode").Scan(&mode); err != nil || mode != "wal" {
		t.Fatalf("journal mode %q %v", mode, err)
	}
	a, err := s.InitAccount(ctx, "u", func() ([]byte, error) { return make([]byte, 16), nil }, func() (string, error) { return "sid", nil })
	if err != nil || a.ServerID != "sid" || a.KeyCheck != nil {
		t.Fatalf("init: %+v %v", a, err)
	}
	a2, err := s.InitAccount(ctx, "v", func() ([]byte, error) { return nil, errors.New("must not be called") }, func() (string, error) { return "other", nil })
	if err != nil || a2.ServerID != "sid" || a2.Username != "v" {
		t.Fatalf("reinit: %+v %v", a2, err)
	}
}

func TestCommitAssignsSeqAndDeleteBumpsRev(t *testing.T) {
	ctx := context.Background()
	s := openTest(t)
	s.InitAccount(ctx, "u", func() ([]byte, error) { return make([]byte, 16), nil }, func() (string, error) { return "sid", nil })
	now := time.Now()
	s.TouchPending(ctx, "a", "d", now)
	s.PutPendingChunk(ctx, "a", "d", 0, 100, now)
	it, err := s.CommitItem(ctx, Item{ID: "a", DeviceID: "d", Kind: "text", Size: 1, CreatedAt: now, ContentHash: "h", StoredBytes: 10, Meta: []byte("m")})
	if err != nil || it.Seq != 1 {
		t.Fatalf("commit: %+v %v", it, err)
	}
	if _, err := s.Pending(ctx, "a"); !errors.Is(err, ErrNotFound) {
		t.Fatal("pending not cleared on commit")
	}
	it2, _ := s.CommitItem(ctx, Item{ID: "b", DeviceID: "d", Kind: "text", Size: 1, CreatedAt: now, ContentHash: "h", StoredBytes: 10, Meta: []byte("m")})
	if it2.Seq != 2 {
		t.Fatalf("seq %d", it2.Seq)
	}
	deleted, rev, err := s.DeleteItems(ctx, []string{"b", "missing", "a"})
	if err != nil || len(deleted) != 2 || deleted[0] != "b" || rev != 1 {
		t.Fatalf("delete: %v %d %v", deleted, rev, err)
	}
	_, rev, _ = s.DeleteItems(ctx, []string{"missing"})
	if rev != 0 {
		t.Fatal("state_rev bumped without deletions")
	}
	it3, _ := s.CommitItem(ctx, Item{ID: "c", DeviceID: "d", Kind: "text", Size: 1, CreatedAt: now, ContentHash: "h", StoredBytes: 10, Meta: []byte("m")})
	if it3.Seq != 3 {
		t.Fatalf("seq reused: %d", it3.Seq)
	}
	cur, sr, entries, err := s.Index(ctx)
	if err != nil || cur != 3 || sr != 1 || len(entries) != 1 {
		t.Fatalf("index: %d %d %v %v", cur, sr, entries, err)
	}
}
