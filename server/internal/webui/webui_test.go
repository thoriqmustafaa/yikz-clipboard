package webui

import (
	"bytes"
	"compress/gzip"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"testing/fstest"
)

func TestHandler(t *testing.T) {
	js := strings.Repeat("console.log('hello');\n", 100)
	h := NewFS(fstest.MapFS{
		"index.html":           {Data: []byte("<!doctype html><title>x</title>" + strings.Repeat(" ", 600))},
		"assets/app-abc123.js": {Data: []byte(js)},
		"favicon.svg":          {Data: []byte("<svg/>")},
	})
	get := func(path string, hdr map[string]string) *http.Response {
		req := httptest.NewRequest(http.MethodGet, path, nil)
		for k, v := range hdr {
			req.Header.Set(k, v)
		}
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		return rec.Result()
	}

	resp := get("/", nil)
	if resp.StatusCode != 200 || resp.Header.Get("Cache-Control") != "no-cache" || !strings.HasPrefix(resp.Header.Get("Content-Type"), "text/html") {
		t.Fatalf("index: %d %v", resp.StatusCode, resp.Header)
	}

	resp = get("/history/some/route", nil)
	if resp.StatusCode != 200 || resp.Header.Get("Cache-Control") != "no-cache" {
		t.Fatalf("spa fallback: %d %v", resp.StatusCode, resp.Header)
	}

	resp = get("/assets/app-abc123.js", map[string]string{"Accept-Encoding": "gzip, br"})
	if resp.StatusCode != 200 || resp.Header.Get("Content-Encoding") != "gzip" || !strings.Contains(resp.Header.Get("Cache-Control"), "immutable") {
		t.Fatalf("asset: %d %v", resp.StatusCode, resp.Header)
	}
	zr, err := gzip.NewReader(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(zr)
	if !bytes.Equal(body, []byte(js)) {
		t.Fatal("gzip body mismatch")
	}

	etag := resp.Header.Get("ETag")
	resp = get("/assets/app-abc123.js", map[string]string{"If-None-Match": etag})
	if resp.StatusCode != http.StatusNotModified {
		t.Fatalf("etag: %d", resp.StatusCode)
	}

	resp = get("/assets/app-abc123.js", nil)
	if resp.Header.Get("Content-Encoding") != "" {
		t.Fatal("gzip without accept-encoding")
	}

	resp = get("/assets/missing.js", nil)
	if resp.StatusCode != 404 {
		t.Fatalf("missing asset: %d", resp.StatusCode)
	}

	resp = get("/favicon.svg", nil)
	if resp.StatusCode != 200 || resp.Header.Get("Cache-Control") != "public, max-age=3600" {
		t.Fatalf("favicon: %d %v", resp.StatusCode, resp.Header)
	}
}

func TestEmbeddedPlaceholder(t *testing.T) {
	h, err := New()
	if err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/", nil))
	if rec.Code != 200 || !strings.Contains(rec.Body.String(), "<html") {
		t.Fatalf("embedded index: %d", rec.Code)
	}
}
