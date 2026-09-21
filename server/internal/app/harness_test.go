package app

import (
	"bytes"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/coder/websocket"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/clock"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/config"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/hub"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

const vectorsDir = "../../../protocol/vectors"

const gib = int64(1) << 30

var fixtureServerTime = time.Date(2026, 9, 21, 10, 10, 0, 0, time.UTC)

func loadVector(t testing.TB, name string, v any) {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(vectorsDir, name))
	if err != nil {
		t.Fatalf("read vector %s: %v", name, err)
	}
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	if err := dec.Decode(v); err != nil {
		t.Fatalf("parse vector %s: %v", name, err)
	}
}

func testBytes(label string, n int) []byte {
	var out []byte
	for counter := uint32(0); len(out) < n; counter++ {
		var c [4]byte
		binary.BigEndian.PutUint32(c[:], counter)
		sum := sha256.Sum256(append([]byte("yikz-clipboard test bytes|"+label+"|"), c[:]...))
		out = append(out, sum[:]...)
	}
	return out[:n]
}

func mustHex(t testing.TB, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func mustB64(t testing.TB, s string) []byte {
	t.Helper()
	b, err := base64.StdEncoding.DecodeString(s)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func seal(key, nonce, aad, plaintext []byte) []byte {
	block, _ := aes.NewCipher(key)
	gcm, _ := cipher.NewGCM(block)
	return gcm.Seal(append([]byte{}, nonce...), nonce, plaintext, aad)
}

func open(key, sealed, aad []byte) ([]byte, error) {
	if len(sealed) < 28 {
		return nil, errors.New("short")
	}
	block, _ := aes.NewCipher(key)
	gcm, _ := cipher.NewGCM(block)
	return gcm.Open(nil, sealed[:12], sealed[12:], aad)
}

func randomSeal(key, aad, plaintext []byte) []byte {
	nonce := make([]byte, 12)
	rand.Read(nonce)
	return seal(key, nonce, aad, plaintext)
}

func hmacHex(key, msg []byte) string {
	m := hmac.New(sha256.New, key)
	m.Write(msg)
	return hex.EncodeToString(m.Sum(nil))
}

type fixDevice struct {
	ID         string
	Name       string
	Platform   string
	CreatedAt  time.Time
	LastSeenAt time.Time
	Revoked    bool
	Token      string
}

type fixItem struct {
	Name        string
	ID          string
	Seq         int64
	DeviceID    string
	Kind        string
	Size        int64
	ChunkCount  int
	CreatedAt   time.Time
	Pinned      bool
	ContentHash string
	Meta        []byte
	Payload     []byte
	Thumb       []byte
	Chunks      [][]byte
	StoredBytes int64
	Content     []byte
	SHA256      string
}

type fixture struct {
	Key      []byte
	Salt     []byte
	ServerID string
	KeyCheck string
	Mac      fixDevice
	Android  fixDevice
	Windows  fixDevice
	Web      fixDevice
	Items    map[string]*fixItem
	Order    []string
}

var (
	fixtureOnce sync.Once
	fixtureVal  *fixture
	fixtureErr  error
)

func loadFixture(t testing.TB) *fixture {
	t.Helper()
	fixtureOnce.Do(func() {
		fixtureVal, fixtureErr = buildFixture(t)
	})
	if fixtureErr != nil {
		t.Fatal(fixtureErr)
	}
	return fixtureVal
}

func parseTime(t testing.TB, s string) time.Time {
	tm, err := time.Parse(time.RFC3339Nano, s)
	if err != nil {
		t.Fatal(err)
	}
	return tm.UTC()
}

func buildFixture(t testing.TB) (*fixture, error) {
	var keys struct {
		Keys []struct {
			Name     string `json:"name"`
			KeyHex   string `json:"key_hex"`
			KeyCheck string `json:"key_check"`
		} `json:"keys"`
	}
	loadVector(t, "keys.json", &keys)
	f := &fixture{Items: map[string]*fixItem{}}
	for _, k := range keys.Keys {
		if k.Name == "primary" {
			f.Key = mustHex(t, k.KeyHex)
			f.KeyCheck = k.KeyCheck
		}
	}
	var httpv httpVectors
	loadVector(t, "http.json", &httpv)
	login := httpv.find(t, "login_new_device")
	lb := login.ResponseBody.(map[string]any)
	f.Salt = mustB64(t, lb["salt"].(string))
	f.ServerID = lb["server_id"].(string)
	macToken := lb["token"].(string)

	devs := httpv.find(t, "devices_list").ResponseBody.(map[string]any)["devices"].([]any)
	toDev := func(m map[string]any) fixDevice {
		return fixDevice{
			ID:         m["id"].(string),
			Name:       m["name"].(string),
			Platform:   m["platform"].(string),
			CreatedAt:  parseTime(t, m["created_at"].(string)),
			LastSeenAt: parseTime(t, m["last_seen_at"].(string)),
			Revoked:    m["revoked"].(bool),
		}
	}
	f.Mac = toDev(devs[0].(map[string]any))
	f.Mac.Token = macToken
	f.Android = toDev(devs[1].(map[string]any))
	f.Android.Token = "yc_" + base64.RawURLEncoding.EncodeToString(testBytes("token|android-previous", 32))
	f.Windows = toDev(devs[2].(map[string]any))
	f.Windows.Token = "yc_" + base64.RawURLEncoding.EncodeToString(testBytes("token|windows", 32))
	f.Web = toDev(devs[3].(map[string]any))

	var items struct {
		Items []map[string]any `json:"items"`
	}
	loadVector(t, "items.json", &items)
	var archive struct {
		Cases []map[string]any `json:"cases"`
	}
	loadVector(t, "files_archive.json", &archive)
	for _, raw := range items.Items {
		header := raw["header"].(map[string]any)
		it := &fixItem{
			Name:        raw["name"].(string),
			ID:          raw["id"].(string),
			Kind:        raw["kind"].(string),
			ContentHash: raw["content_hash"].(string),
			Meta:        mustB64(t, raw["meta_sealed_b64"].(string)),
			SHA256:      raw["content_sha256"].(string),
			DeviceID:    header["device_id"].(string),
			CreatedAt:   parseTime(t, header["created_at"].(string)),
			Pinned:      header["pinned"].(bool),
		}
		it.Seq, _ = header["seq"].(json.Number).Int64()
		it.Size, _ = raw["size"].(json.Number).Int64()
		cc, _ := raw["chunk_count"].(json.Number).Int64()
		it.ChunkCount = int(cc)
		it.StoredBytes, _ = raw["stored_bytes"].(json.Number).Int64()
		if p, ok := raw["payload_sealed_b64"].(string); ok {
			it.Payload = mustB64(t, p)
			it.Content = mustHex(t, raw["content_hex"].(string))
		}
		if th, ok := raw["thumb_sealed_b64"].(string); ok {
			it.Thumb = mustB64(t, th)
		}
		if chunks, ok := raw["chunks"].([]any); ok {
			var name string
			var size int
			for _, c := range archive.Cases {
				if c["name"] == "single_large_file_header" {
					file := c["files"].([]any)[0].(map[string]any)
					name = file["name"].(string)
					n, _ := file["size"].(json.Number).Int64()
					size = int(n)
				}
			}
			var buf bytes.Buffer
			buf.WriteString("YCF1")
			binary.Write(&buf, binary.BigEndian, uint32(1))
			binary.Write(&buf, binary.BigEndian, uint32(len(name)))
			buf.WriteString(name)
			binary.Write(&buf, binary.BigEndian, uint64(size))
			data := make([]byte, size)
			for i := range data {
				data[i] = byte(i % 251)
			}
			buf.Write(data)
			it.Content = buf.Bytes()
			if int64(len(it.Content)) != it.Size {
				return nil, fmt.Errorf("large content size %d, want %d", len(it.Content), it.Size)
			}
			for i, c := range chunks {
				cm := c.(map[string]any)
				start := int64(i) * proto.ChunkSizeBytes
				end := min(start+proto.ChunkSizeBytes, it.Size)
				sealed := seal(f.Key, mustHex(t, cm["nonce_hex"].(string)), []byte(cm["aad_utf8"].(string)), it.Content[start:end])
				sum := sha256.Sum256(sealed)
				if hex.EncodeToString(sum[:]) != cm["sealed_sha256"].(string) {
					return nil, fmt.Errorf("chunk %d sealed sha256 mismatch", i)
				}
				it.Chunks = append(it.Chunks, sealed)
			}
		}
		f.Items[it.Name] = it
		f.Order = append(f.Order, it.Name)
	}
	return f, nil
}

type httpExample struct {
	Name            string            `json:"name"`
	Method          string            `json:"method"`
	Path            string            `json:"path"`
	RequestHeaders  map[string]string `json:"request_headers"`
	RequestBody     any               `json:"request_body"`
	Status          int               `json:"status"`
	ResponseHeaders map[string]string `json:"response_headers"`
	ResponseBody    any               `json:"response_body"`
}

type httpVectors struct {
	Examples []httpExample `json:"examples"`
}

func (v httpVectors) find(t testing.TB, name string) httpExample {
	t.Helper()
	for _, e := range v.Examples {
		if e.Name == name {
			return e
		}
	}
	t.Fatalf("vector %s not found", name)
	return httpExample{}
}

type scriptRand struct {
	mu     sync.Mutex
	script []byte
}

func (r *scriptRand) Push(b []byte) {
	r.mu.Lock()
	r.script = append(r.script, b...)
	r.mu.Unlock()
}

func (r *scriptRand) Read(p []byte) (int, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.script) > 0 {
		n := copy(p, r.script)
		r.script = r.script[n:]
		if n == len(p) {
			return n, nil
		}
		m, err := rand.Read(p[n:])
		return n + m, err
	}
	return rand.Read(p)
}

type harnessOpts struct {
	StorageMax    int64
	PinnedMax     int64
	MinFree       int64
	RetentionDays int
	Hub           hub.Config
	UploadTTL     time.Duration
	Start         time.Time
}

type harness struct {
	t     testing.TB
	app   *App
	srv   *httptest.Server
	clock *clock.Fake
	rand  *scriptRand
	free  atomic.Int64
}

func newHarness(t testing.TB, o harnessOpts) *harness {
	t.Helper()
	if o.StorageMax == 0 {
		o.StorageMax = 5 * gib
	}
	if o.PinnedMax == 0 {
		o.PinnedMax = gib
	}
	if o.MinFree == 0 {
		o.MinFree = 2 * gib
	}
	if o.RetentionDays == 0 {
		o.RetentionDays = 7
	}
	if o.Start.IsZero() {
		o.Start = fixtureServerTime
	}
	proxies, _ := config.ParsePrefixes("127.0.0.0/8,::1/128")
	cfg := config.Config{
		Username:         "thoriq",
		Password:         "login-password-example",
		Listen:           "127.0.0.1:0",
		DataDir:          t.TempDir(),
		RetentionDays:    o.RetentionDays,
		StorageMaxBytes:  o.StorageMax,
		PinnedMaxBytes:   o.PinnedMax,
		MinFreeDiskBytes: o.MinFree,
		TrustedProxies:   proxies,
		ShutdownTimeout:  5 * time.Second,
	}
	h := &harness{t: t, clock: clock.NewFake(o.Start), rand: &scriptRand{}}
	h.free.Store(11 * gib)
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	if os.Getenv("YC_TEST_LOG") != "" {
		logger = slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelDebug}))
	}
	a, err := New(context.Background(), Options{
		Config:    cfg,
		Version:   "1.0.0",
		Clock:     h.clock,
		Rand:      h.rand,
		Logger:    logger,
		DiskFree:  func(string) (int64, error) { return h.free.Load(), nil },
		Hub:       o.Hub,
		UploadTTL: o.UploadTTL,
	})
	if err != nil {
		t.Fatal(err)
	}
	h.app = a
	h.srv = httptest.NewServer(a.Handler)
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		a.Hub.Shutdown(ctx)
		h.srv.Close()
		a.Close()
	})
	return h
}

func (h *harness) setFree(n int64) {
	h.free.Store(n)
	h.app.Disk.Refresh()
}

type seedOpts struct {
	Without    []string
	NoItems    bool
	NoDevices  bool
	NoKeyCheck bool
	CurrentSeq int64
	StateRev   int64
}

func (h *harness) seed(f *fixture, o seedOpts) {
	t := h.t
	t.Helper()
	ctx := context.Background()
	db := h.app.Store.DB()
	if o.CurrentSeq == 0 {
		o.CurrentSeq = 44
	}
	if o.StateRev == 0 {
		o.StateRev = 17
	}
	var kc any = f.KeyCheck
	if o.NoKeyCheck {
		kc = nil
	}
	if _, err := db.ExecContext(ctx, "UPDATE account SET salt = ?, server_id = ?, key_check = ?, current_seq = ?, state_rev = ? WHERE id = 1",
		f.Salt, f.ServerID, kc, o.CurrentSeq, o.StateRev); err != nil {
		t.Fatal(err)
	}
	if !o.NoDevices {
		for _, d := range []fixDevice{f.Mac, f.Android, f.Windows, f.Web} {
			hash := ""
			if d.Token != "" && !d.Revoked {
				hash = proto.HashToken(d.Token)
			}
			if err := h.app.Store.CreateDevice(ctx, store.Device{ID: d.ID, Name: d.Name, Platform: d.Platform, CreatedAt: d.CreatedAt, LastSeenAt: d.LastSeenAt, Revoked: d.Revoked}, hash); err != nil {
				t.Fatal(err)
			}
		}
	}
	if o.NoItems {
		return
	}
	skip := map[string]bool{}
	for _, n := range o.Without {
		skip[n] = true
	}
	for _, name := range f.Order {
		if !skip[name] {
			h.seedItem(f, name)
		}
	}
}

func (h *harness) seedItem(f *fixture, name string) {
	h.t.Helper()
	it := f.Items[name]
	if err := h.app.Store.InsertItemRaw(context.Background(), store.Item{
		ID: it.ID, Seq: it.Seq, DeviceID: it.DeviceID, Kind: it.Kind, Size: it.Size, ChunkCount: it.ChunkCount,
		CreatedAt: it.CreatedAt, Pinned: it.Pinned, ContentHash: it.ContentHash, HasThumb: it.Thumb != nil,
		StoredBytes: it.StoredBytes, Meta: it.Meta, Payload: it.Payload,
	}); err != nil {
		h.t.Fatal(err)
	}
	if it.Thumb != nil {
		h.writeBlob(it.ID, "thumb", it.Thumb)
	}
	for i, c := range it.Chunks {
		h.writeBlob(it.ID, strconv.Itoa(i), c)
	}
}

func (h *harness) writeBlob(id, name string, data []byte) {
	tmp, err := h.app.Blobs.WriteTemp(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		h.t.Fatal(err)
	}
	if err := h.app.Blobs.Install(tmp, id, name); err != nil {
		h.t.Fatal(err)
	}
}

type response struct {
	Status int
	Header http.Header
	Body   []byte
}

func (r response) json(t testing.TB) map[string]any {
	t.Helper()
	var m map[string]any
	dec := json.NewDecoder(bytes.NewReader(r.Body))
	dec.UseNumber()
	if err := dec.Decode(&m); err != nil {
		t.Fatalf("decode response %q: %v", r.Body, err)
	}
	return m
}

func (h *harness) do(method, path, token string, body io.Reader, hdr map[string]string) response {
	h.t.Helper()
	req, err := http.NewRequest(method, h.srv.URL+path, body)
	if err != nil {
		h.t.Fatal(err)
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	for k, v := range hdr {
		req.Header.Set(k, v)
	}
	resp, err := h.srv.Client().Do(req)
	if err != nil {
		h.t.Fatal(err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		h.t.Fatal(err)
	}
	return response{Status: resp.StatusCode, Header: resp.Header, Body: data}
}

func (h *harness) doJSON(method, path, token string, body any) response {
	h.t.Helper()
	var r io.Reader
	if body != nil {
		data, err := json.Marshal(body)
		if err != nil {
			h.t.Fatal(err)
		}
		r = bytes.NewReader(data)
	}
	return h.do(method, path, token, r, map[string]string{"Content-Type": "application/json"})
}

func (h *harness) mustStatus(r response, status int) response {
	h.t.Helper()
	if r.Status != status {
		h.t.Fatalf("status %d, want %d: %s", r.Status, status, r.Body)
	}
	return r
}

func (h *harness) login(name, platform, deviceID string) (string, string) {
	h.t.Helper()
	body := map[string]any{"username": "thoriq", "password": "login-password-example", "device_name": name, "platform": platform}
	if deviceID != "" {
		body["device_id"] = deviceID
	}
	m := h.mustStatus(h.doJSON("POST", "/api/login", "", body), 200).json(h.t)
	return m["device_id"].(string), m["token"].(string)
}

type wsClient struct {
	t    testing.TB
	c    *websocket.Conn
	h    *harness
	msgs chan wsMsg
}

type wsMsg struct {
	data map[string]any
	raw  []byte
	err  error
}

func (h *harness) dial(token string) *wsClient {
	h.t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	u := "ws" + strings.TrimPrefix(h.srv.URL, "http") + "/ws"
	c, _, err := websocket.Dial(ctx, u, &websocket.DialOptions{HTTPHeader: http.Header{"Authorization": {"Bearer " + token}}})
	if err != nil {
		h.t.Fatalf("dial: %v", err)
	}
	c.SetReadLimit(proto.MaxWSServerFrameBytes)
	w := &wsClient{t: h.t, c: c, h: h, msgs: make(chan wsMsg, 256)}
	go func() {
		for {
			_, data, err := c.Read(context.Background())
			if err != nil {
				w.msgs <- wsMsg{err: err}
				close(w.msgs)
				return
			}
			var m map[string]any
			dec := json.NewDecoder(bytes.NewReader(data))
			dec.UseNumber()
			dec.Decode(&m)
			w.msgs <- wsMsg{data: m, raw: data}
		}
	}()
	h.t.Cleanup(func() { c.CloseNow() })
	return w
}

func (w *wsClient) send(v any) {
	w.t.Helper()
	data, err := json.Marshal(v)
	if err != nil {
		w.t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := w.c.Write(ctx, websocket.MessageText, data); err != nil {
		w.t.Fatalf("ws write: %v", err)
	}
}

func (w *wsClient) sendRaw(typ websocket.MessageType, data []byte) {
	w.t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := w.c.Write(ctx, typ, data); err != nil {
		w.t.Fatalf("ws write: %v", err)
	}
}

func (w *wsClient) next(timeout time.Duration) (wsMsg, bool) {
	select {
	case m, ok := <-w.msgs:
		if !ok {
			return wsMsg{err: errors.New("closed")}, true
		}
		return m, true
	case <-time.After(timeout):
		return wsMsg{}, false
	}
}

func (w *wsClient) read() map[string]any {
	w.t.Helper()
	for {
		m, ok := w.next(5 * time.Second)
		if !ok {
			w.t.Fatal("timeout waiting for websocket message")
		}
		if m.err != nil {
			w.t.Fatalf("websocket closed: %v", m.err)
		}
		if m.data["type"] == "ping" {
			continue
		}
		return m.data
	}
}

func (w *wsClient) readType(typ string) map[string]any {
	w.t.Helper()
	for {
		m := w.read()
		if m["type"] == typ {
			return m
		}
	}
}

func (w *wsClient) expectNone(d time.Duration, ignore ...string) {
	w.t.Helper()
	deadline := time.After(d)
	for {
		select {
		case m, ok := <-w.msgs:
			if !ok || m.err != nil {
				w.t.Fatalf("unexpected close: %v", m.err)
			}
			t, _ := m.data["type"].(string)
			skip := t == "ping"
			for _, ig := range ignore {
				if t == ig {
					skip = true
				}
			}
			if !skip {
				w.t.Fatalf("unexpected message: %s", m.raw)
			}
		case <-deadline:
			return
		}
	}
}

func (w *wsClient) expectClose(code websocket.StatusCode) {
	w.t.Helper()
	for {
		m, ok := w.next(10 * time.Second)
		if !ok {
			w.t.Fatalf("timeout waiting for close %d", code)
		}
		if m.err != nil {
			if got := websocket.CloseStatus(m.err); got != code {
				w.t.Fatalf("close status %d, want %d (%v)", got, code, m.err)
			}
			return
		}
	}
}

func (w *wsClient) hello(deviceID string) map[string]any {
	w.t.Helper()
	w.send(map[string]any{"type": "hello", "protocol_version": 1, "device_id": deviceID, "last_seq": 0, "app_version": "test", "platform": "macos"})
	m := w.read()
	if m["type"] != "welcome" {
		w.t.Fatalf("expected welcome, got %v", m)
	}
	return m
}

func num(v any) int64 {
	switch n := v.(type) {
	case json.Number:
		i, _ := n.Int64()
		return i
	case float64:
		return int64(n)
	}
	return -1
}

func hubCfg(ping, dead time.Duration) hub.Config {
	return hub.Config{PingInterval: ping, DeadTimeout: dead}
}
