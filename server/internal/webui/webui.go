package webui

import (
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"errors"
	"io/fs"
	"mime"
	"net/http"
	"path"
	"strconv"
	"strings"
	"sync"
)

//go:embed all:dist
var embedded embed.FS

type asset struct {
	data  []byte
	gz    []byte
	etag  string
	ctype string
}

type Handler struct {
	fsys  fs.FS
	mu    sync.Mutex
	cache map[string]*asset
}

func New() (*Handler, error) {
	sub, err := fs.Sub(embedded, "dist")
	if err != nil {
		return nil, err
	}
	return NewFS(sub), nil
}

func NewFS(fsys fs.FS) *Handler {
	return &Handler{fsys: fsys, cache: map[string]*asset{}}
}

var compressible = map[string]bool{
	".html": true, ".htm": true, ".js": true, ".mjs": true, ".css": true, ".svg": true,
	".json": true, ".map": true, ".txt": true, ".xml": true, ".webmanifest": true, ".wasm": true,
}

func (h *Handler) load(name string) (*asset, error) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if a, ok := h.cache[name]; ok {
		return a, nil
	}
	st, err := fs.Stat(h.fsys, name)
	if err != nil {
		return nil, err
	}
	if st.IsDir() {
		return nil, fs.ErrNotExist
	}
	data, err := fs.ReadFile(h.fsys, name)
	if err != nil {
		return nil, err
	}
	ext := strings.ToLower(path.Ext(name))
	ctype := mime.TypeByExtension(ext)
	switch {
	case ext == ".webmanifest":
		ctype = "application/manifest+json"
	case ext == ".mjs":
		ctype = "text/javascript; charset=utf-8"
	case ctype == "":
		ctype = http.DetectContentType(data)
	}
	sum := sha256.Sum256(data)
	a := &asset{data: data, ctype: ctype, etag: `"` + hex.EncodeToString(sum[:12]) + `"`}
	if compressible[ext] && len(data) >= 512 {
		var buf bytes.Buffer
		zw, _ := gzip.NewWriterLevel(&buf, gzip.BestCompression)
		zw.Write(data)
		zw.Close()
		if buf.Len() < len(data) {
			a.gz = buf.Bytes()
		}
	}
	h.cache[name] = a
	return a, nil
}

func notFound(w http.ResponseWriter) {
	body := `{"code":"not_found","message":"route not found"}`
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(http.StatusNotFound)
	w.Write([]byte(body))
}

func (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	clean := path.Clean("/" + r.URL.Path)
	name := strings.TrimPrefix(clean, "/")
	if name == "" {
		name = "index.html"
	}
	cache := "public, max-age=3600"
	switch {
	case name == "index.html":
		cache = "no-cache"
	case strings.HasPrefix(name, "assets/"):
		cache = "public, max-age=31536000, immutable"
	}
	a, err := h.load(name)
	if err != nil {
		if !errors.Is(err, fs.ErrNotExist) || path.Ext(name) != "" {
			notFound(w)
			return
		}
		a, err = h.load("index.html")
		if err != nil {
			notFound(w)
			return
		}
		cache = "no-cache"
	}
	hdr := w.Header()
	hdr.Set("Content-Type", a.ctype)
	hdr.Set("Cache-Control", cache)
	hdr.Set("ETag", a.etag)
	hdr.Set("X-Content-Type-Options", "nosniff")
	if strings.HasPrefix(a.ctype, "text/html") {
		hdr.Set("X-Frame-Options", "DENY")
		hdr.Set("Referrer-Policy", "no-referrer")
	}
	if a.gz != nil {
		hdr.Add("Vary", "Accept-Encoding")
	}
	if match := r.Header.Get("If-None-Match"); match != "" && etagMatch(match, a.etag) {
		w.WriteHeader(http.StatusNotModified)
		return
	}
	body := a.data
	if a.gz != nil && acceptsGzip(r) {
		hdr.Set("Content-Encoding", "gzip")
		body = a.gz
	}
	hdr.Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(http.StatusOK)
	if r.Method != http.MethodHead {
		w.Write(body)
	}
}

func etagMatch(header, etag string) bool {
	for _, part := range strings.Split(header, ",") {
		p := strings.TrimSpace(part)
		p = strings.TrimPrefix(p, "W/")
		if p == etag || p == "*" {
			return true
		}
	}
	return false
}

func acceptsGzip(r *http.Request) bool {
	for _, part := range strings.Split(r.Header.Get("Accept-Encoding"), ",") {
		enc, params, _ := strings.Cut(strings.TrimSpace(part), ";")
		if !strings.EqualFold(strings.TrimSpace(enc), "gzip") {
			continue
		}
		params = strings.ReplaceAll(params, " ", "")
		if params == "q=0" || params == "q=0.0" || params == "q=0.00" || params == "q=0.000" {
			return false
		}
		return true
	}
	return false
}
