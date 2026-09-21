package release

import (
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/apierr"
)

const PublicKeyBase64 = "2Ve8Uwt53+AwbuAiK08Xf2gB5EU7pguqXL7I9yeqGGk="

const MaxAssetBytes int64 = 1 << 30

const DefaultKeep = 5

const (
	CodeMissingAsset  = "missing_asset"
	CodeAssetMismatch = "asset_mismatch"
	CodeBadSignature  = "bad_signature"
)

var Platforms = []string{"macos", "android", "windows-x64", "windows-arm64"}

var (
	fileRe    = regexp.MustCompile(`^[A-Za-z0-9._-]{1,128}$`)
	versionRe = regexp.MustCompile(`^(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})$`)
	hexRe     = regexp.MustCompile(`^[0-9a-f]{64}$`)
)

func EmbeddedPublicKey() ed25519.PublicKey {
	raw, err := base64.StdEncoding.DecodeString(PublicKeyBase64)
	if err != nil || len(raw) != ed25519.PublicKeySize {
		panic("invalid embedded release public key")
	}
	return ed25519.PublicKey(raw)
}

type Asset struct {
	Platform  string `json:"platform"`
	File      string `json:"file"`
	Size      int64  `json:"size"`
	SHA256    string `json:"sha256"`
	Signature string `json:"signature"`
}

type Manifest struct {
	Version     string  `json:"version"`
	PublishedAt string  `json:"published_at"`
	NotesMD     string  `json:"notes_md"`
	Assets      []Asset `json:"assets"`
}

type LatestAsset struct {
	Asset
	URL string `json:"url"`
}

type Latest struct {
	Version     string      `json:"version"`
	PublishedAt string      `json:"published_at"`
	NotesMD     string      `json:"notes_md"`
	Asset       LatestAsset `json:"asset"`
}

type Options struct {
	DataDir   string
	Keep      int
	PublicKey ed25519.PublicKey
	DiskLow   func() bool
	Now       func() time.Time
}

type Store struct {
	dir     string
	tmp     string
	keep    int
	pub     ed25519.PublicKey
	diskLow func() bool
	now     func() time.Time
	mu      sync.Mutex
}

func Open(o Options) (*Store, error) {
	if o.Keep <= 0 {
		o.Keep = DefaultKeep
	}
	if o.PublicKey == nil {
		o.PublicKey = EmbeddedPublicKey()
	}
	if len(o.PublicKey) != ed25519.PublicKeySize {
		return nil, errors.New("release public key must be 32 bytes")
	}
	if o.DiskLow == nil {
		o.DiskLow = func() bool { return false }
	}
	if o.Now == nil {
		o.Now = time.Now
	}
	s := &Store{
		dir:     filepath.Join(o.DataDir, "releases"),
		keep:    o.Keep,
		pub:     o.PublicKey,
		diskLow: o.DiskLow,
		now:     o.Now,
	}
	s.tmp = filepath.Join(s.dir, ".tmp")
	if err := os.RemoveAll(s.tmp); err != nil {
		return nil, fmt.Errorf("clean release temp dir: %w", err)
	}
	for _, d := range []string{s.dir, s.tmp} {
		if err := os.MkdirAll(d, 0o700); err != nil {
			return nil, fmt.Errorf("create %s: %w", d, err)
		}
	}
	return s, nil
}

func ValidVersion(v string) bool { return versionRe.MatchString(v) }

func ValidFile(f string) bool {
	return fileRe.MatchString(f) && f != "." && f != ".." && f != "manifest.json"
}

func ValidPlatform(p string) bool {
	for _, x := range Platforms {
		if x == p {
			return true
		}
	}
	return false
}

func parseVersion(v string) [3]int {
	var out [3]int
	for i, p := range strings.SplitN(v, ".", 3) {
		n, _ := strconv.Atoi(p)
		out[i] = n
	}
	return out
}

func Compare(a, b string) int {
	pa, pb := parseVersion(a), parseVersion(b)
	for i := 0; i < 3; i++ {
		if pa[i] != pb[i] {
			if pa[i] < pb[i] {
				return -1
			}
			return 1
		}
	}
	return 0
}

func notFound() *apierr.Error { return apierr.NotFound("release not found") }

func (s *Store) PutAsset(version, file string, body io.Reader, contentLength int64) error {
	if !ValidVersion(version) {
		return apierr.InvalidRequest("version must be MAJOR.MINOR.PATCH")
	}
	if !ValidFile(file) {
		return apierr.InvalidRequest("file name must match ^[A-Za-z0-9._-]{1,128}$")
	}
	if contentLength > MaxAssetBytes {
		return apierr.BodyTooLarge("release assets must be at most 1 GiB")
	}
	if s.diskLow() {
		return apierr.DiskLow()
	}
	f, err := os.CreateTemp(s.tmp, "asset-*")
	if err != nil {
		return fmt.Errorf("create temp file: %w", err)
	}
	tmp := f.Name()
	defer os.Remove(tmp)
	n, err := io.Copy(f, io.LimitReader(body, MaxAssetBytes+1))
	if err != nil {
		f.Close()
		return apierr.InvalidRequest("failed to read request body")
	}
	if n > MaxAssetBytes {
		f.Close()
		return apierr.BodyTooLarge("release assets must be at most 1 GiB")
	}
	if contentLength >= 0 && n != contentLength {
		f.Close()
		return apierr.InvalidRequest("body shorter than Content-Length")
	}
	if err := f.Sync(); err != nil {
		f.Close()
		return fmt.Errorf("sync temp file: %w", err)
	}
	if err := f.Close(); err != nil {
		return fmt.Errorf("close temp file: %w", err)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	dir := filepath.Join(s.dir, version)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return fmt.Errorf("create release dir: %w", err)
	}
	if err := os.Rename(tmp, filepath.Join(dir, file)); err != nil {
		return fmt.Errorf("install release asset: %w", err)
	}
	return nil
}

func (s *Store) validateManifest(version string, m *Manifest) error {
	if !ValidVersion(version) {
		return apierr.InvalidRequest("version must be MAJOR.MINOR.PATCH")
	}
	if m.Version == "" {
		m.Version = version
	}
	if m.Version != version {
		return apierr.InvalidRequest("manifest version does not match the URL")
	}
	if m.PublishedAt == "" {
		m.PublishedAt = s.now().UTC().Format("2006-01-02T15:04:05.000Z")
	} else if _, err := time.Parse(time.RFC3339Nano, m.PublishedAt); err != nil {
		return apierr.InvalidRequest("published_at must be an RFC 3339 timestamp")
	}
	if len(m.NotesMD) > 256<<10 {
		return apierr.InvalidRequest("notes_md is too long")
	}
	if len(m.Assets) == 0 {
		return apierr.InvalidRequest("assets must not be empty")
	}
	seenPlatform := map[string]bool{}
	seenFile := map[string]bool{}
	for i, a := range m.Assets {
		if !ValidPlatform(a.Platform) {
			return apierr.InvalidRequest(fmt.Sprintf("assets[%d].platform is not a known platform", i))
		}
		if !ValidFile(a.File) {
			return apierr.InvalidRequest(fmt.Sprintf("assets[%d].file is invalid", i))
		}
		if seenPlatform[a.Platform] || seenFile[a.File] {
			return apierr.InvalidRequest(fmt.Sprintf("assets[%d] duplicates a platform or file", i))
		}
		seenPlatform[a.Platform] = true
		seenFile[a.File] = true
		if a.Size <= 0 || a.Size > MaxAssetBytes {
			return apierr.InvalidRequest(fmt.Sprintf("assets[%d].size is out of range", i))
		}
		if !hexRe.MatchString(a.SHA256) {
			return apierr.InvalidRequest(fmt.Sprintf("assets[%d].sha256 must be 64 lowercase hex characters", i))
		}
		sig, err := base64.StdEncoding.DecodeString(a.Signature)
		if err != nil || len(sig) != ed25519.SignatureSize {
			return apierr.InvalidRequest(fmt.Sprintf("assets[%d].signature must be 64 bytes of standard base64", i))
		}
	}
	return nil
}

func hashFile(path string) (int64, []byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return 0, nil, err
	}
	defer f.Close()
	h := sha256.New()
	n, err := io.Copy(h, f)
	if err != nil {
		return 0, nil, err
	}
	return n, h.Sum(nil), nil
}

func (s *Store) Publish(version string, m Manifest) (Manifest, error) {
	if err := s.validateManifest(version, &m); err != nil {
		return Manifest{}, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	dir := filepath.Join(s.dir, version)
	for _, a := range m.Assets {
		path := filepath.Join(dir, a.File)
		st, err := os.Stat(path)
		if err != nil || !st.Mode().IsRegular() {
			return Manifest{}, apierr.New(http.StatusConflict, CodeMissingAsset, "asset "+a.File+" has not been uploaded").
				WithDetails(map[string]any{"file": a.File})
		}
		if st.Size() != a.Size {
			return Manifest{}, apierr.New(http.StatusConflict, CodeAssetMismatch, "asset "+a.File+" size does not match").
				WithDetails(map[string]any{"file": a.File, "size": st.Size()})
		}
		n, sum, err := hashFile(path)
		if err != nil {
			return Manifest{}, fmt.Errorf("hash %s: %w", a.File, err)
		}
		if n != a.Size || hex.EncodeToString(sum) != a.SHA256 {
			return Manifest{}, apierr.New(http.StatusConflict, CodeAssetMismatch, "asset "+a.File+" sha256 does not match").
				WithDetails(map[string]any{"file": a.File})
		}
		sig, _ := base64.StdEncoding.DecodeString(a.Signature)
		if !ed25519.Verify(s.pub, sum, sig) {
			return Manifest{}, apierr.New(http.StatusUnprocessableEntity, CodeBadSignature, "asset "+a.File+" signature does not verify").
				WithDetails(map[string]any{"file": a.File})
		}
	}
	data, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return Manifest{}, err
	}
	tmp, err := os.CreateTemp(s.tmp, "manifest-*")
	if err != nil {
		return Manifest{}, fmt.Errorf("create temp manifest: %w", err)
	}
	defer os.Remove(tmp.Name())
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		return Manifest{}, fmt.Errorf("write manifest: %w", err)
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return Manifest{}, fmt.Errorf("sync manifest: %w", err)
	}
	if err := tmp.Close(); err != nil {
		return Manifest{}, fmt.Errorf("close manifest: %w", err)
	}
	if err := os.Rename(tmp.Name(), filepath.Join(dir, "manifest.json")); err != nil {
		return Manifest{}, fmt.Errorf("install manifest: %w", err)
	}
	s.pruneLocked()
	return m, nil
}

func (s *Store) versionsLocked() ([]string, error) {
	entries, err := os.ReadDir(s.dir)
	if err != nil {
		return nil, err
	}
	var out []string
	for _, e := range entries {
		if e.IsDir() && ValidVersion(e.Name()) {
			out = append(out, e.Name())
		}
	}
	sort.Slice(out, func(i, j int) bool { return Compare(out[i], out[j]) > 0 })
	return out, nil
}

func (s *Store) readManifest(version string) (Manifest, bool) {
	data, err := os.ReadFile(filepath.Join(s.dir, version, "manifest.json"))
	if err != nil {
		return Manifest{}, false
	}
	var m Manifest
	if err := json.Unmarshal(data, &m); err != nil || m.Version != version {
		return Manifest{}, false
	}
	if m.Assets == nil {
		m.Assets = []Asset{}
	}
	return m, true
}

func (s *Store) pruneLocked() {
	versions, err := s.versionsLocked()
	if err != nil {
		return
	}
	kept := 0
	oldestKept := ""
	for _, v := range versions {
		if _, ok := s.readManifest(v); !ok {
			continue
		}
		if kept < s.keep {
			kept++
			oldestKept = v
		}
	}
	if oldestKept == "" {
		return
	}
	for _, v := range versions {
		if Compare(v, oldestKept) < 0 {
			os.RemoveAll(filepath.Join(s.dir, v))
		}
	}
}

func (s *Store) List(limit int) ([]Manifest, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	versions, err := s.versionsLocked()
	if err != nil {
		return nil, err
	}
	out := []Manifest{}
	for _, v := range versions {
		if limit > 0 && len(out) >= limit {
			break
		}
		if m, ok := s.readManifest(v); ok {
			out = append(out, m)
		}
	}
	return out, nil
}

func AssetURL(version, file string) string {
	return "/api/releases/" + version + "/assets/" + file
}

func (s *Store) Latest(platform string) (Latest, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	versions, err := s.versionsLocked()
	if err != nil {
		return Latest{}, false, err
	}
	for _, v := range versions {
		m, ok := s.readManifest(v)
		if !ok {
			continue
		}
		for _, a := range m.Assets {
			if a.Platform == platform {
				return Latest{
					Version:     m.Version,
					PublishedAt: m.PublishedAt,
					NotesMD:     m.NotesMD,
					Asset:       LatestAsset{Asset: a, URL: AssetURL(m.Version, a.File)},
				}, true, nil
			}
		}
	}
	return Latest{}, false, nil
}

func (s *Store) OpenAsset(version, file string) (*os.File, os.FileInfo, error) {
	if !ValidVersion(version) || !ValidFile(file) {
		return nil, nil, notFound()
	}
	s.mu.Lock()
	m, ok := s.readManifest(version)
	s.mu.Unlock()
	if !ok {
		return nil, nil, notFound()
	}
	listed := false
	for _, a := range m.Assets {
		if a.File == file {
			listed = true
			break
		}
	}
	if !listed {
		return nil, nil, notFound()
	}
	f, err := os.Open(filepath.Join(s.dir, version, file))
	if err != nil {
		return nil, nil, notFound()
	}
	st, err := f.Stat()
	if err != nil || !st.Mode().IsRegular() {
		f.Close()
		return nil, nil, notFound()
	}
	return f, st, nil
}
