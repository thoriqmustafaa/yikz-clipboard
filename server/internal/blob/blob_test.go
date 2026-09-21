package blob

import (
	"bytes"
	"errors"
	"io"
	"os"
	"path/filepath"
	"testing"
)

func TestWriteInstallOpen(t *testing.T) {
	dir := t.TempDir()
	os.MkdirAll(filepath.Join(dir, "tmp"), 0o700)
	os.WriteFile(filepath.Join(dir, "tmp", "stale"), []byte("x"), 0o600)
	s, err := Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(dir, "tmp", "stale")); !os.IsNotExist(err) {
		t.Fatal("stale temp file not removed")
	}
	tmp, err := s.WriteTemp(bytes.NewReader([]byte("hello")), 5)
	if err != nil || tmp.Size() != 5 {
		t.Fatalf("write: %v", err)
	}
	if err := s.Install(tmp, "item", ChunkName(0)); err != nil {
		t.Fatal(err)
	}
	f, size, err := s.Open("item", "0")
	if err != nil || size != 5 {
		t.Fatalf("open: %v", err)
	}
	data, _ := io.ReadAll(f)
	f.Close()
	if string(data) != "hello" {
		t.Fatal("content mismatch")
	}
	if _, err := s.WriteTemp(bytes.NewReader([]byte("toolong")), 5); !errors.Is(err, ErrTooLarge) {
		t.Fatalf("expected ErrTooLarge, got %v", err)
	}
	entries, _ := os.ReadDir(filepath.Join(dir, "tmp"))
	if len(entries) != 0 {
		t.Fatal("temp file left behind")
	}
	ids, _ := s.IDs()
	if len(ids) != 1 || ids[0] != "item" {
		t.Fatalf("ids: %v", ids)
	}
	if err := s.RemoveItem("../x"); err == nil {
		t.Fatal("path traversal accepted")
	}
	if err := s.RemoveItem("item"); err != nil {
		t.Fatal(err)
	}
}
