package app

import (
	"bytes"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"testing"
)

const testReleaseToken = "release-upload-token-for-tests"

type releaseKit struct {
	h     *harness
	priv  ed25519.PrivateKey
	token string
	dev   string
}

func newReleaseKit(t *testing.T, keep int) *releaseKit {
	t.Helper()
	seed := sha256.Sum256([]byte("yikz-clipboard release test key"))
	priv := ed25519.NewKeyFromSeed(seed[:])
	h := newHarness(t, harnessOpts{ReleaseToken: testReleaseToken, ReleaseKey: priv.Public().(ed25519.PublicKey), ReleasesKeep: keep})
	dev, tok := h.login("Mac", "macos", "")
	return &releaseKit{h: h, priv: priv, token: tok, dev: dev}
}

func (k *releaseKit) upload(version, file string, data []byte) response {
	k.h.t.Helper()
	return k.h.do("PUT", "/api/admin/releases/"+version+"/assets/"+file, testReleaseToken, bytes.NewReader(data), map[string]string{"Content-Type": "application/octet-stream"})
}

func (k *releaseKit) asset(platform, file string, data []byte) map[string]any {
	sum := sha256.Sum256(data)
	return map[string]any{
		"platform":  platform,
		"file":      file,
		"size":      len(data),
		"sha256":    hex.EncodeToString(sum[:]),
		"signature": base64.StdEncoding.EncodeToString(ed25519.Sign(k.priv, sum[:])),
	}
}

func (k *releaseKit) publish(version string, assets ...map[string]any) response {
	k.h.t.Helper()
	body := map[string]any{
		"version":      version,
		"published_at": "2026-09-22T10:00:00.000Z",
		"notes_md":     "### All\n- Release " + version + "\n",
		"assets":       assets,
	}
	req, _ := jsonReader(body)
	return k.h.do("PUT", "/api/admin/releases/"+version, testReleaseToken, req, map[string]string{"Content-Type": "application/json"})
}

func (k *releaseKit) release(version string, platforms ...string) {
	k.h.t.Helper()
	var assets []map[string]any
	for _, p := range platforms {
		file := "app-" + version + "-" + p + ".bin"
		data := testBytes(file, 3000)
		k.h.mustStatus(k.upload(version, file, data), http.StatusNoContent)
		assets = append(assets, k.asset(p, file, data))
	}
	k.h.mustStatus(k.publish(version, assets...), http.StatusCreated)
}

func jsonReader(v any) (*bytes.Reader, error) {
	data, err := json.Marshal(v)
	return bytes.NewReader(data), err
}

func TestReleaseAdminDisabledWithoutToken(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	_, tok := h.login("Mac", "macos", "")
	for _, tk := range []string{"", tok, "anything"} {
		r := h.do("PUT", "/api/admin/releases/1.0.0/assets/a.zip", tk, bytes.NewReader([]byte("x")), nil)
		h.mustStatus(r, http.StatusNotFound)
		r = h.doJSON("PUT", "/api/admin/releases/1.0.0", tk, map[string]any{"version": "1.0.0"})
		h.mustStatus(r, http.StatusNotFound)
	}
	m := h.mustStatus(h.do("GET", "/api/releases", tok, nil, nil), http.StatusOK).json(t)
	if list, ok := m["releases"].([]any); !ok || len(list) != 0 {
		t.Fatalf("expected empty releases, got %v", m)
	}
}

func TestReleaseAuthRequired(t *testing.T) {
	k := newReleaseKit(t, 0)
	h := k.h
	for _, path := range []string{"/api/releases", "/api/releases/latest?platform=macos", "/api/releases/1.0.0/assets/a.zip"} {
		h.mustStatus(h.do("GET", path, "", nil, nil), http.StatusUnauthorized)
		h.mustStatus(h.do("GET", path, "not-a-token", nil, nil), http.StatusUnauthorized)
	}
	for _, tk := range []string{"", "wrong-token", k.token, testReleaseToken + "x"} {
		r := h.do("PUT", "/api/admin/releases/1.0.0/assets/a.zip", tk, bytes.NewReader([]byte("x")), nil)
		if r.Status != http.StatusUnauthorized {
			t.Fatalf("token %q: status %d", tk, r.Status)
		}
	}
	r := h.do("PUT", "/api/admin/releases/1.0.0/assets/a.zip?token="+testReleaseToken, "", bytes.NewReader([]byte("x")), nil)
	h.mustStatus(r, http.StatusUnauthorized)
}

func TestReleasePublishFlow(t *testing.T) {
	k := newReleaseKit(t, 0)
	h := k.h
	ws := h.dial(k.token)
	ws.hello(k.dev)

	mac := testBytes("mac", 70000)
	win := testBytes("win", 50000)
	h.mustStatus(k.upload("1.1.0", "YikzClipboard-1.1.0-macos.zip", []byte("stale")), http.StatusNoContent)
	h.mustStatus(k.upload("1.1.0", "YikzClipboard-1.1.0-macos.zip", mac), http.StatusNoContent)
	h.mustStatus(k.upload("1.1.0", "YikzClipboard-1.1.0-win-x64.zip", win), http.StatusNoContent)

	h.mustStatus(h.do("GET", "/api/releases/latest?platform=macos", k.token, nil, nil), http.StatusNoContent)
	h.mustStatus(h.do("GET", "/api/releases/1.1.0/assets/YikzClipboard-1.1.0-macos.zip", k.token, nil, nil), http.StatusNotFound)

	r := h.mustStatus(k.publish("1.1.0", k.asset("macos", "YikzClipboard-1.1.0-macos.zip", mac), k.asset("windows-x64", "YikzClipboard-1.1.0-win-x64.zip", win)), http.StatusCreated)
	if m := r.json(t); m["version"] != "1.1.0" || len(m["assets"].([]any)) != 2 {
		t.Fatalf("unexpected publish response: %s", r.Body)
	}
	msg := ws.readType("release_available")
	if msg["version"] != "1.1.0" {
		t.Fatalf("unexpected broadcast: %v", msg)
	}

	list := h.mustStatus(h.do("GET", "/api/releases", k.token, nil, nil), http.StatusOK).json(t)
	rels := list["releases"].([]any)
	if len(rels) != 1 || rels[0].(map[string]any)["notes_md"] != "### All\n- Release 1.1.0\n" {
		t.Fatalf("unexpected list: %v", list)
	}

	latest := h.mustStatus(h.do("GET", "/api/releases/latest?platform=macos", k.token, nil, nil), http.StatusOK).json(t)
	asset := latest["asset"].(map[string]any)
	if latest["version"] != "1.1.0" || asset["url"] != "/api/releases/1.1.0/assets/YikzClipboard-1.1.0-macos.zip" || num(asset["size"]) != int64(len(mac)) {
		t.Fatalf("unexpected latest: %v", latest)
	}
	h.mustStatus(h.do("GET", "/api/releases/latest?platform=android", k.token, nil, nil), http.StatusNoContent)
	h.mustStatus(h.do("GET", "/api/releases/latest?platform=linux", k.token, nil, nil), http.StatusBadRequest)
	h.mustStatus(h.do("GET", "/api/releases/latest", k.token, nil, nil), http.StatusBadRequest)

	dl := h.mustStatus(h.do("GET", asset["url"].(string), k.token, nil, nil), http.StatusOK)
	if !bytes.Equal(dl.Body, mac) || dl.Header.Get("Content-Type") != "application/octet-stream" || dl.Header.Get("Content-Length") != "70000" {
		t.Fatalf("download mismatch: %d bytes, headers %v", len(dl.Body), dl.Header)
	}
	part := h.mustStatus(h.do("GET", asset["url"].(string), k.token, nil, map[string]string{"Range": "bytes=100-199"}), http.StatusPartialContent)
	if !bytes.Equal(part.Body, mac[100:200]) || part.Header.Get("Content-Range") != "bytes 100-199/70000" {
		t.Fatalf("range mismatch: %v", part.Header)
	}
	tail := h.mustStatus(h.do("GET", asset["url"].(string), k.token, nil, map[string]string{"Range": "bytes=69990-"}), http.StatusPartialContent)
	if !bytes.Equal(tail.Body, mac[69990:]) {
		t.Fatal("open-ended range mismatch")
	}
	h.mustStatus(h.do("GET", "/api/releases/1.1.0/assets/missing.zip", k.token, nil, nil), http.StatusNotFound)
	h.mustStatus(h.do("GET", "/api/releases/9.9.9/assets/YikzClipboard-1.1.0-macos.zip", k.token, nil, nil), http.StatusNotFound)
	h.mustStatus(h.do("GET", "/api/releases/1.1.0/assets/manifest.json", k.token, nil, nil), http.StatusNotFound)

	storage := h.mustStatus(h.do("GET", "/api/storage", k.token, nil, nil), http.StatusOK).json(t)
	if num(storage["used_bytes"]) != 0 {
		t.Fatalf("releases counted toward quota: %v", storage)
	}
}

func TestReleasePublishErrors(t *testing.T) {
	k := newReleaseKit(t, 0)
	h := k.h
	data := testBytes("asset", 4096)
	h.mustStatus(k.upload("1.2.0", "app.zip", data), http.StatusNoContent)

	other := ed25519.NewKeyFromSeed(bytes.Repeat([]byte{7}, 32))
	sum := sha256.Sum256(data)
	bad := k.asset("macos", "app.zip", data)
	bad["signature"] = base64.StdEncoding.EncodeToString(ed25519.Sign(other, sum[:]))
	r := h.mustStatus(k.publish("1.2.0", bad), http.StatusUnprocessableEntity)
	if r.json(t)["code"] != "bad_signature" {
		t.Fatalf("unexpected: %s", r.Body)
	}

	missing := k.asset("android", "app.apk", data)
	r = h.mustStatus(k.publish("1.2.0", k.asset("macos", "app.zip", data), missing), http.StatusConflict)
	if r.json(t)["code"] != "missing_asset" {
		t.Fatalf("unexpected: %s", r.Body)
	}

	wrongSize := k.asset("macos", "app.zip", data)
	wrongSize["size"] = len(data) + 1
	r = h.mustStatus(k.publish("1.2.0", wrongSize), http.StatusConflict)
	if r.json(t)["code"] != "asset_mismatch" {
		t.Fatalf("unexpected: %s", r.Body)
	}

	changed := append([]byte{}, data...)
	changed[5] ^= 1
	wrongHash := k.asset("macos", "app.zip", changed)
	r = h.mustStatus(k.publish("1.2.0", wrongHash), http.StatusConflict)
	if r.json(t)["code"] != "asset_mismatch" {
		t.Fatalf("unexpected: %s", r.Body)
	}

	h.mustStatus(k.publish("1.3.0", k.asset("macos", "app.zip", data)), http.StatusConflict)
	h.mustStatus(k.publish("1.2.0"), http.StatusBadRequest)
	h.mustStatus(k.publish("1.2", k.asset("macos", "app.zip", data)), http.StatusBadRequest)
	h.mustStatus(k.publish("1.2.0", k.asset("ios", "app.zip", data)), http.StatusBadRequest)
	h.mustStatus(k.publish("1.2.0", k.asset("macos", "app.zip", data), k.asset("macos", "app.zip", data)), http.StatusBadRequest)
	mismatch, _ := jsonReader(map[string]any{"version": "1.9.0", "assets": []any{k.asset("macos", "app.zip", data)}})
	h.mustStatus(h.do("PUT", "/api/admin/releases/1.2.0", testReleaseToken, mismatch, nil), http.StatusBadRequest)
	h.mustStatus(h.do("PUT", "/api/admin/releases/1.2.0", testReleaseToken, bytes.NewReader([]byte("nope")), nil), http.StatusBadRequest)

	h.mustStatus(k.upload("1.2.0", "bad%20name.zip", data), http.StatusBadRequest)
	h.mustStatus(k.upload("1.2.0", "manifest.json", data), http.StatusBadRequest)
	h.mustStatus(k.upload("v1.2.0", "app.zip", data), http.StatusBadRequest)
	h.mustStatus(h.do("GET", "/api/admin/releases/1.2.0/assets/app.zip", testReleaseToken, nil, nil), http.StatusMethodNotAllowed)

	list := h.mustStatus(h.do("GET", "/api/releases", k.token, nil, nil), http.StatusOK).json(t)
	if len(list["releases"].([]any)) != 0 {
		t.Fatalf("failed publishes must not create releases: %v", list)
	}
	h.mustStatus(k.publish("1.2.0", k.asset("macos", "app.zip", data)), http.StatusCreated)
}

func TestReleaseDiskLowRejectsUpload(t *testing.T) {
	k := newReleaseKit(t, 0)
	k.h.setFree(1 << 20)
	r := k.h.mustStatus(k.upload("1.0.0", "a.zip", []byte("abc")), http.StatusInsufficientStorage)
	if r.json(t)["code"] != "disk_low" {
		t.Fatalf("unexpected: %s", r.Body)
	}
}

func TestReleasePruneAndLatestPerPlatform(t *testing.T) {
	k := newReleaseKit(t, 2)
	h := k.h
	k.release("1.0.0", "macos", "android")
	k.release("1.1.0", "macos", "windows-arm64")
	h.mustStatus(k.upload("1.3.0", "pending.zip", []byte("pending")), http.StatusNoContent)
	k.release("1.2.0", "macos")

	dir := filepath.Join(h.app.Config.DataDir, "releases")
	if _, err := os.Stat(filepath.Join(dir, "1.0.0")); !os.IsNotExist(err) {
		t.Fatalf("1.0.0 should be pruned, stat err %v", err)
	}
	for _, v := range []string{"1.1.0", "1.2.0", "1.3.0"} {
		if _, err := os.Stat(filepath.Join(dir, v)); err != nil {
			t.Fatalf("%s should be kept: %v", v, err)
		}
	}

	list := h.mustStatus(h.do("GET", "/api/releases", k.token, nil, nil), http.StatusOK).json(t)
	rels := list["releases"].([]any)
	if len(rels) != 2 || rels[0].(map[string]any)["version"] != "1.2.0" || rels[1].(map[string]any)["version"] != "1.1.0" {
		t.Fatalf("unexpected list: %v", list)
	}
	one := h.mustStatus(h.do("GET", "/api/releases?limit=1", k.token, nil, nil), http.StatusOK).json(t)
	if len(one["releases"].([]any)) != 1 {
		t.Fatalf("limit ignored: %v", one)
	}
	h.mustStatus(h.do("GET", "/api/releases?limit=0", k.token, nil, nil), http.StatusBadRequest)

	latest := func(p string) string {
		r := h.do("GET", "/api/releases/latest?platform="+p, k.token, nil, nil)
		if r.Status == http.StatusNoContent {
			return ""
		}
		h.mustStatus(r, http.StatusOK)
		return r.json(t)["version"].(string)
	}
	if v := latest("macos"); v != "1.2.0" {
		t.Fatalf("macos latest %q", v)
	}
	if v := latest("windows-arm64"); v != "1.1.0" {
		t.Fatalf("windows-arm64 latest %q", v)
	}
	if v := latest("android"); v != "" {
		t.Fatalf("android latest %q after pruning", v)
	}
	if v := latest("windows-x64"); v != "" {
		t.Fatalf("windows-x64 latest %q", v)
	}

	k.release("1.4.0", "android")
	if v := latest("android"); v != "1.4.0" {
		t.Fatalf("android latest %q", v)
	}
	if _, err := os.Stat(filepath.Join(dir, "1.1.0")); !os.IsNotExist(err) {
		t.Fatal("1.1.0 should be pruned after 1.4.0")
	}
	if _, err := os.Stat(filepath.Join(dir, "1.3.0")); err != nil {
		t.Fatal("unpublished 1.3.0 is newer than the oldest kept release and must stay")
	}
}
