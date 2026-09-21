package blob

import (
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strconv"
)

const ThumbName = "thumb"

var ErrTooLarge = errors.New("body exceeds limit")

type Store struct {
	blobs string
	tmp   string
}

func Open(dataDir string) (*Store, error) {
	s := &Store{
		blobs: filepath.Join(dataDir, "blobs"),
		tmp:   filepath.Join(dataDir, "tmp"),
	}
	if err := os.RemoveAll(s.tmp); err != nil {
		return nil, fmt.Errorf("clean temp dir: %w", err)
	}
	for _, d := range []string{s.blobs, s.tmp} {
		if err := os.MkdirAll(d, 0o700); err != nil {
			return nil, fmt.Errorf("create %s: %w", d, err)
		}
	}
	return s, nil
}

func ChunkName(n int) string { return strconv.Itoa(n) }

type Temp struct {
	path string
	size int64
}

func (t *Temp) Size() int64 { return t.size }

func (s *Store) Discard(t *Temp) {
	if t != nil {
		os.Remove(t.path)
	}
}

func (s *Store) WriteTemp(r io.Reader, limit int64) (*Temp, error) {
	f, err := os.CreateTemp(s.tmp, "upload-*")
	if err != nil {
		return nil, fmt.Errorf("create temp file: %w", err)
	}
	t := &Temp{path: f.Name()}
	n, err := io.Copy(f, io.LimitReader(r, limit+1))
	t.size = n
	if err != nil {
		f.Close()
		s.Discard(t)
		return nil, err
	}
	if n > limit {
		f.Close()
		s.Discard(t)
		return nil, ErrTooLarge
	}
	if err := f.Sync(); err != nil {
		f.Close()
		s.Discard(t)
		return nil, fmt.Errorf("sync temp file: %w", err)
	}
	if err := f.Close(); err != nil {
		s.Discard(t)
		return nil, fmt.Errorf("close temp file: %w", err)
	}
	return t, nil
}

func (s *Store) Install(t *Temp, id, name string) error {
	dir := filepath.Join(s.blobs, id)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return fmt.Errorf("create blob dir: %w", err)
	}
	if err := os.Rename(t.path, filepath.Join(dir, name)); err != nil {
		return fmt.Errorf("install blob: %w", err)
	}
	return nil
}

func (s *Store) Open(id, name string) (*os.File, int64, error) {
	f, err := os.Open(filepath.Join(s.blobs, id, name))
	if err != nil {
		return nil, 0, err
	}
	st, err := f.Stat()
	if err != nil {
		f.Close()
		return nil, 0, err
	}
	return f, st.Size(), nil
}

func (s *Store) Remove(id, name string) error {
	err := os.Remove(filepath.Join(s.blobs, id, name))
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

func (s *Store) RemoveItem(id string) error {
	if id == "" || id != filepath.Base(id) {
		return fmt.Errorf("invalid blob id %q", id)
	}
	return os.RemoveAll(filepath.Join(s.blobs, id))
}

func (s *Store) IDs() ([]string, error) {
	entries, err := os.ReadDir(s.blobs)
	if err != nil {
		return nil, err
	}
	ids := make([]string, 0, len(entries))
	for _, e := range entries {
		ids = append(ids, e.Name())
	}
	return ids, nil
}
