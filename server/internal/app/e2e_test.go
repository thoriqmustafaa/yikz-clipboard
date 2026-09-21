package app

import (
	"bytes"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"strconv"
	"testing"
	"time"

	"github.com/coder/websocket"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
)

type client struct {
	h        *harness
	key      []byte
	deviceID string
	token    string
	ws       *wsClient
}

var idCounter int64

func newItemID(now time.Time) string {
	idCounter++
	rnd := make([]byte, 10)
	rand.Read(rnd)
	return proto.FormatUUIDv7(now.UnixMilli()+idCounter, rnd)
}

func (c *client) contentHash(content []byte) string {
	chk := hmacRaw(c.key, []byte("yikz-clipboard/v1/content-hash"))
	return hmacHex(chk, content)
}

func hmacRaw(key, msg []byte) []byte {
	b, _ := hex.DecodeString(hmacHex(key, msg))
	return b
}

func (c *client) meta(id, kind string, content []byte) string {
	sum := sha256.Sum256(content)
	m, _ := json.Marshal(map[string]any{"v": 1, "mime": "text/plain; charset=utf-8", "preview": "", "sha256": hex.EncodeToString(sum[:])})
	return base64.StdEncoding.EncodeToString(randomSeal(c.key, []byte("yc1|meta|"+id), m))
}

func (c *client) createInline(content []byte) map[string]any {
	c.h.t.Helper()
	id := newItemID(c.h.clock.Now())
	body := map[string]any{
		"id": id, "kind": "text", "size": len(content), "chunk_count": 0,
		"content_hash": c.contentHash(content),
		"meta":         c.meta(id, "text", content),
		"payload":      base64.StdEncoding.EncodeToString(randomSeal(c.key, []byte("yc1|payload|"+id), content)),
	}
	return c.h.mustStatus(c.h.doJSON("POST", "/api/items", c.token, body), 201).json(c.h.t)
}

func (c *client) uploadChunked(content []byte, thumb []byte) (string, map[string]any) {
	c.h.t.Helper()
	id := newItemID(c.h.clock.Now())
	count := proto.ChunkCount(int64(len(content)))
	if thumb != nil {
		sealed := randomSeal(c.key, []byte("yc1|thumb|"+id), thumb)
		c.h.mustStatus(c.h.do("PUT", "/api/items/"+id+"/thumb", c.token, bytes.NewReader(sealed), nil), 204)
	}
	for i := count - 1; i >= 0; i-- {
		start := i * proto.ChunkSizeBytes
		end := min(start+proto.ChunkSizeBytes, len(content))
		aad := fmt.Sprintf("yc1|chunk|%s|%d|%d", id, i, count)
		sealed := randomSeal(c.key, []byte(aad), content[start:end])
		c.h.mustStatus(c.h.do("PUT", fmt.Sprintf("/api/items/%s/chunks/%d", id, i), c.token, bytes.NewReader(sealed), nil), 204)
	}
	body := map[string]any{
		"kind": "files", "size": len(content), "chunk_count": count,
		"content_hash": c.contentHash(content),
		"meta":         c.meta(id, "files", content),
	}
	return id, c.h.mustStatus(c.h.doJSON("POST", "/api/items/"+id+"/commit", c.token, body), 201).json(c.h.t)
}

func (c *client) download(item map[string]any) []byte {
	c.h.t.Helper()
	id := item["id"].(string)
	count := int(num(item["chunk_count"]))
	var out bytes.Buffer
	for i := 0; i < count; i++ {
		r := c.h.mustStatus(c.h.do("GET", fmt.Sprintf("/api/items/%s/chunks/%d", id, i), c.token, nil, nil), 200)
		if r.Header.Get("Content-Length") != strconv.Itoa(len(r.Body)) {
			c.h.t.Fatalf("content-length %s for %d bytes", r.Header.Get("Content-Length"), len(r.Body))
		}
		plain, err := open(c.key, r.Body, []byte(fmt.Sprintf("yc1|chunk|%s|%d|%d", id, i, count)))
		if err != nil {
			c.h.t.Fatalf("open chunk %d: %v", i, err)
		}
		out.Write(plain)
	}
	return out.Bytes()
}

func (c *client) metaSHA(item map[string]any) string {
	c.h.t.Helper()
	sealed, _ := base64.StdEncoding.DecodeString(item["meta"].(string))
	plain, err := open(c.key, sealed, []byte("yc1|meta|"+item["id"].(string)))
	if err != nil {
		c.h.t.Fatalf("open meta: %v", err)
	}
	var m map[string]any
	json.Unmarshal(plain, &m)
	return m["sha256"].(string)
}

func setupClients(t *testing.T, h *harness, names ...string) []*client {
	t.Helper()
	key := make([]byte, 32)
	rand.Read(key)
	var out []*client
	for i, n := range names {
		id, tok := h.login(n, []string{"macos", "android", "windows", "web"}[i%4], "")
		out = append(out, &client{h: h, key: key, deviceID: id, token: tok})
	}
	kc := hmacHex(key, []byte("yikz-clipboard/v1/key-check"))
	h.mustStatus(h.doJSON("PUT", "/api/account/key-check", out[0].token, map[string]any{"key_check": kc}), 204)
	return out
}

func (c *client) connect() map[string]any {
	c.ws = c.h.dial(c.token)
	return c.ws.hello(c.deviceID)
}

func TestLoginAndKeyCheckFlow(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	macID, macTok := h.login("Mac", "macos", "")
	_, webTok := h.login("  Browser  ", "web", "")
	me := h.mustStatus(h.do("GET", "/api/me", macTok, nil, nil), 200).json(t)
	if me["key_check"] != nil || me["device"].(map[string]any)["id"] != macID {
		t.Fatalf("me: %v", me)
	}
	h.mustStatus(h.do("GET", "/api/me?token="+webTok, "", nil, nil), 200)
	h.mustStatus(h.do("GET", "/api/me?token="+webTok, macTok, nil, nil), 200)
	h.mustStatus(h.do("GET", "/api/me?token="+webTok, "yc_invalidtokeninvalidtokeninvalidtokeninval", nil, nil), 401)
	devs := h.mustStatus(h.do("GET", "/api/devices", webTok, nil, nil), 200).json(t)["devices"].([]any)
	if len(devs) != 2 || devs[1].(map[string]any)["name"] != "Browser" {
		t.Fatalf("devices: %v", devs)
	}

	kc1 := hex.EncodeToString(bytes.Repeat([]byte{1}, 32))
	kc2 := hex.EncodeToString(bytes.Repeat([]byte{2}, 32))
	h.mustStatus(h.doJSON("PUT", "/api/account/key-check", macTok, map[string]any{"key_check": "ABC"}), 400)
	h.mustStatus(h.doJSON("PUT", "/api/account/key-check", macTok, map[string]any{"key_check": kc1}), 204)
	h.mustStatus(h.doJSON("PUT", "/api/account/key-check", webTok, map[string]any{"key_check": kc1}), 204)
	r := h.mustStatus(h.doJSON("PUT", "/api/account/key-check", webTok, map[string]any{"key_check": kc2}), 409)
	if r.json(t)["code"] != "key_check_exists" {
		t.Fatal(string(r.Body))
	}
	if h.mustStatus(h.do("GET", "/api/me", webTok, nil, nil), 200).json(t)["key_check"] != kc1 {
		t.Fatal("key check not stored")
	}
	h.mustStatus(h.do("DELETE", "/api/account/key-check", macTok, nil, nil), 204)
	h.mustStatus(h.doJSON("PUT", "/api/account/key-check", webTok, map[string]any{"key_check": kc2}), 204)

	h.mustStatus(h.doJSON("POST", "/api/login", "", map[string]any{"username": "thoriq", "password": "login-password-example", "device_name": "x\x01", "platform": "web"}), 400)
	h.mustStatus(h.doJSON("POST", "/api/login", "", map[string]any{"username": "thoriq", "password": "login-password-example", "platform": "web"}), 400)
	h.mustStatus(h.do("POST", "/api/login", "", bytes.NewReader([]byte("[1]")), nil), 400)
	h.mustStatus(h.do("POST", "/api/login", "", bytes.NewReader(make([]byte, proto.MaxJSONBodyBytes+10)), nil), 413)

	id2, tok2 := h.login("Mac renamed", "macos", macID)
	if id2 != macID || tok2 == macTok {
		t.Fatal("login with existing device id did not reuse it")
	}
	h.mustStatus(h.do("GET", "/api/me", macTok, nil, nil), 401)
	h.mustStatus(h.do("POST", "/api/logout", tok2, nil, nil), 204)
	h.mustStatus(h.do("GET", "/api/me", tok2, nil, nil), 401)
	id3, tok3 := h.login("Mac again", "macos", macID)
	if id3 != macID {
		t.Fatal("revoked device id not reused")
	}
	h.mustStatus(h.do("GET", "/api/me", tok3, nil, nil), 200)

	h.mustStatus(h.do("GET", "/", "", nil, nil), 200)
	h.mustStatus(h.do("GET", "/history", "", nil, nil), 200)
	h.mustStatus(h.do("POST", "/something", "", nil, nil), 404)
	h.mustStatus(h.do("GET", "/api", "", nil, nil), 404)
}

func TestInlineBroadcast(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	wa := a.connect()
	if num(wa["current_seq"]) != 0 || len(wa["online_devices"].([]any)) != 1 {
		t.Fatalf("welcome: %v", wa)
	}
	wb := b.connect()
	if len(wb["online_devices"].([]any)) != 2 {
		t.Fatalf("welcome b: %v", wb)
	}
	p := a.ws.readType("presence")
	if p["device_id"] != b.deviceID || p["online"] != true {
		t.Fatalf("presence: %v", p)
	}

	content := []byte("hello from the other side \U0001f44b")
	hdr := a.createInline(content)
	if _, ok := hdr["payload"]; ok || num(hdr["seq"]) != 1 {
		t.Fatalf("create response: %v", hdr)
	}
	for _, c := range []*client{a, b} {
		clip := c.ws.readType("clip")["item"].(map[string]any)
		if clip["id"] != hdr["id"] || clip["device_id"] != a.deviceID {
			t.Fatalf("clip: %v", clip)
		}
		sealed, _ := base64.StdEncoding.DecodeString(clip["payload"].(string))
		plain, err := open(c.key, sealed, []byte("yc1|payload|"+clip["id"].(string)))
		if err != nil || !bytes.Equal(plain, content) {
			t.Fatalf("payload: %v", err)
		}
		sum := sha256.Sum256(plain)
		if c.metaSHA(clip) != hex.EncodeToString(sum[:]) {
			t.Fatal("meta sha mismatch")
		}
	}
	got := h.mustStatus(h.do("GET", "/api/items/"+hdr["id"].(string), b.token, nil, nil), 200).json(t)
	if got["payload"] == nil {
		t.Fatal("GET item without payload")
	}
	hist := h.mustStatus(h.do("GET", "/api/history", b.token, nil, nil), 200).json(t)
	if _, ok := hist["items"].([]any)[0].(map[string]any)["payload"]; ok {
		t.Fatal("history includes payload")
	}
	b.ws.c.Close(websocket.StatusNormalClosure, "")
	p = a.ws.readType("presence")
	if p["online"] != false {
		t.Fatalf("presence offline: %v", p)
	}
}

func TestChunkedUploadDownload20MB(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	a.connect()
	b.connect()
	content := make([]byte, 20*1024*1024+12345)
	rand.Read(content)
	thumb := []byte("fake jpeg thumbnail bytes")
	id, hdr := a.uploadChunked(content, thumb)
	if num(hdr["chunk_count"]) != 6 || hdr["has_thumb"] != true {
		t.Fatalf("commit header: %v", hdr)
	}
	clip := b.ws.readType("clip")["item"].(map[string]any)
	if _, ok := clip["payload"]; ok || clip["id"] != id {
		t.Fatalf("clip: %v", clip)
	}
	got := b.download(clip)
	sum := sha256.Sum256(got)
	if !bytes.Equal(got, content) || b.metaSHA(clip) != hex.EncodeToString(sum[:]) {
		t.Fatal("downloaded content mismatch")
	}
	tr := h.mustStatus(h.do("GET", "/api/items/"+id+"/thumb", b.token, nil, nil), 200)
	plain, err := open(b.key, tr.Body, []byte("yc1|thumb|"+id))
	if err != nil || !bytes.Equal(plain, thumb) {
		t.Fatal("thumb mismatch")
	}
	var stored int64 = int64(len(tr.Body))
	meta, _ := base64.StdEncoding.DecodeString(clip["meta"].(string))
	stored += int64(len(meta))
	for i := 0; i < 6; i++ {
		stored += proto.ChunkSealedSize(int64(len(content)), i, 6)
	}
	if num(clip["stored_bytes"]) != stored {
		t.Fatalf("stored_bytes %d, want %d", num(clip["stored_bytes"]), stored)
	}
	st := h.mustStatus(h.do("GET", "/api/storage", a.token, nil, nil), 200).json(t)
	if num(st["used_bytes"]) != stored || num(st["item_count"]) != 1 {
		t.Fatalf("storage: %v", st)
	}
	h.mustStatus(h.do("GET", "/api/items/"+id+"/chunks/6", b.token, nil, nil), 404)
	h.mustStatus(h.do("GET", "/api/items/"+id+"/chunks/x", b.token, nil, nil), 404)
	h.mustStatus(h.do("PUT", "/api/items/"+id+"/chunks/99999", a.token, bytes.NewReader(make([]byte, 30)), nil), 400)
	h.mustStatus(h.do("PUT", "/api/items/"+id+"/thumb", a.token, bytes.NewReader(make([]byte, 30)), nil), 409)
	again := h.mustStatus(h.doJSON("POST", "/api/items/"+id+"/commit", a.token, map[string]any{
		"kind": "files", "size": len(content), "chunk_count": 6, "content_hash": a.contentHash(content), "meta": clip["meta"],
	}), 200).json(t)
	if again["seq"] != hdr["seq"] {
		t.Fatal("idempotent commit changed seq")
	}
	b.ws.expectNone(100 * time.Millisecond)
}

func TestPendingUploadCancelAndExpiry(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac")
	a := cs[0]
	id := newItemID(h.clock.Now())
	h.mustStatus(h.do("PUT", "/api/items/"+id+"/chunks/0", a.token, bytes.NewReader(make([]byte, 100)), nil), 204)
	h.mustStatus(h.do("GET", "/api/items/"+id, a.token, nil, nil), 404)
	h.mustStatus(h.do("DELETE", "/api/account/key-check", a.token, nil, nil), 409)
	h.mustStatus(h.do("DELETE", "/api/items/"+id, a.token, nil, nil), 204)
	h.mustStatus(h.do("DELETE", "/api/items/"+id, a.token, nil, nil), 404)

	id2 := newItemID(h.clock.Now())
	h.mustStatus(h.do("PUT", "/api/items/"+id2+"/chunks/0", a.token, bytes.NewReader(make([]byte, 100)), nil), 204)
	h.clock.Advance(59 * time.Minute)
	h.app.Service.RunRetention(t.Context(), "")
	h.mustStatus(h.do("PUT", "/api/items/"+id2+"/chunks/1", a.token, bytes.NewReader(make([]byte, 100)), nil), 204)
	h.clock.Advance(59 * time.Minute)
	h.app.Service.RunRetention(t.Context(), "")
	h.mustStatus(h.do("DELETE", "/api/items/"+id2, a.token, nil, nil), 204)

	id3 := newItemID(h.clock.Now())
	h.mustStatus(h.do("PUT", "/api/items/"+id3+"/chunks/0", a.token, bytes.NewReader(make([]byte, 100)), nil), 204)
	h.clock.Advance(61 * time.Minute)
	if err := h.app.Service.RunRetention(t.Context(), ""); err != nil {
		t.Fatal(err)
	}
	h.mustStatus(h.do("DELETE", "/api/items/"+id3, a.token, nil, nil), 404)
	ids, _ := h.app.Blobs.IDs()
	if len(ids) != 0 {
		t.Fatalf("blob dirs left: %v", ids)
	}
	h.mustStatus(h.do("DELETE", "/api/account/key-check", a.token, nil, nil), 204)
}

func TestCatchUpViaHistoryAfter(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	b.connect()
	first := a.createInline([]byte("one"))
	lastSeq := num(b.ws.readType("clip")["item"].(map[string]any)["seq"])
	b.ws.c.Close(websocket.StatusNormalClosure, "")

	var created []string
	for i := 0; i < 5; i++ {
		created = append(created, a.createInline([]byte(fmt.Sprintf("offline %d", i)))["id"].(string))
	}
	w := b.connect()
	if num(w["current_seq"]) != lastSeq+5 {
		t.Fatalf("current_seq %d, want %d", num(w["current_seq"]), lastSeq+5)
	}
	cursor := lastSeq
	var got []string
	for {
		page := h.mustStatus(h.do("GET", fmt.Sprintf("/api/history?after=%d&limit=2", cursor), b.token, nil, nil), 200).json(t)
		for _, it := range page["items"].([]any) {
			m := it.(map[string]any)
			if num(m["seq"]) <= cursor {
				t.Fatal("history after not ascending")
			}
			cursor = num(m["seq"])
			got = append(got, m["id"].(string))
		}
		if page["has_more"] != true {
			break
		}
	}
	if fmt.Sprint(got) != fmt.Sprint(created) {
		t.Fatalf("catch-up ids %v, want %v", got, created)
	}
	desc := h.mustStatus(h.do("GET", fmt.Sprintf("/api/history?before=%d&limit=500", num(w["current_seq"])+1), b.token, nil, nil), 200).json(t)
	items := desc["items"].([]any)
	if len(items) != 6 || items[5].(map[string]any)["id"] != first["id"] || desc["has_more"] != false {
		t.Fatalf("history before: %v", desc)
	}
	for _, q := range []string{"limit=0", "limit=501", "before=-1", "after=abc", "before=1&before=2", "limit=1.5"} {
		h.mustStatus(h.do("GET", "/api/history?"+q, b.token, nil, nil), 400)
	}
	b.ws.expectNone(100*time.Millisecond, "presence")
}

func TestPinDeleteStateRev(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	w := b.connect()
	rev := num(w["state_rev"])
	x := a.createInline([]byte("pin me"))
	y := a.createInline([]byte("delete me"))
	b.ws.readType("clip")
	b.ws.readType("clip")

	h.mustStatus(h.doJSON("POST", "/api/items/"+x["id"].(string)+"/pin", a.token, map[string]any{"pinned": true}), 200)
	ev := b.ws.readType("clip_pinned")
	if ev["id"] != x["id"] || ev["pinned"] != true || num(ev["state_rev"]) != rev+1 {
		t.Fatalf("clip_pinned: %v", ev)
	}
	h.mustStatus(h.doJSON("POST", "/api/items/"+x["id"].(string)+"/pin", a.token, map[string]any{"pinned": true}), 200)
	h.mustStatus(h.doJSON("POST", "/api/items/"+x["id"].(string)+"/pin", a.token, map[string]any{}), 400)
	h.mustStatus(h.doJSON("POST", "/api/items/"+newItemID(h.clock.Now())+"/pin", a.token, map[string]any{"pinned": true}), 404)

	h.mustStatus(h.do("DELETE", "/api/items/"+y["id"].(string), a.token, nil, nil), 204)
	ev = b.ws.readType("clip_deleted")
	if ev["reason"] != "user" || num(ev["state_rev"]) != rev+2 || ev["ids"].([]any)[0] != y["id"] {
		t.Fatalf("clip_deleted: %v", ev)
	}
	idx := h.mustStatus(h.do("GET", "/api/history/index", b.token, nil, nil), 200).json(t)
	if num(idx["state_rev"]) != rev+2 || num(idx["current_seq"]) != 2 || len(idx["items"].([]any)) != 1 {
		t.Fatalf("index: %v", idx)
	}
	h.mustStatus(h.doJSON("POST", "/api/items/"+x["id"].(string)+"/pin", a.token, map[string]any{"pinned": false}), 200)
	if ev := b.ws.readType("clip_pinned"); ev["pinned"] != false || num(ev["state_rev"]) != rev+3 {
		t.Fatalf("unpin: %v", ev)
	}
	b.ws.expectNone(100 * time.Millisecond)
}

func TestDedupe(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	b.connect()
	first := a.createInline([]byte("same"))
	pinned := a.createInline([]byte("other"))
	h.mustStatus(h.doJSON("POST", "/api/items/"+pinned["id"].(string)+"/pin", a.token, map[string]any{"pinned": true}), 200)
	b.ws.readType("clip")
	b.ws.readType("clip")
	b.ws.readType("clip_pinned")

	second := b.createInline([]byte("same"))
	if c := b.ws.readType("clip"); c["item"].(map[string]any)["id"] != second["id"] {
		t.Fatal("clip for second")
	}
	ev := b.ws.read()
	if ev["type"] != "clip_deleted" || ev["reason"] != "dedupe" || ev["ids"].([]any)[0] != first["id"] {
		t.Fatalf("dedupe event: %v", ev)
	}
	b.createInline([]byte("other"))
	b.ws.readType("clip")
	b.ws.expectNone(100 * time.Millisecond)
	idx := h.mustStatus(h.do("GET", "/api/history/index", a.token, nil, nil), 200).json(t)
	if len(idx["items"].([]any)) != 3 {
		t.Fatalf("index after dedupe: %v", idx)
	}
}

func TestRetentionByAgeAndSize(t *testing.T) {
	h := newHarness(t, harnessOpts{StorageMax: 2000, PinnedMax: 1000, RetentionDays: 2})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	b.connect()
	old := a.createInline(bytes.Repeat([]byte("o"), 100))
	keep := a.createInline(bytes.Repeat([]byte("k"), 100))
	h.mustStatus(h.doJSON("POST", "/api/items/"+keep["id"].(string)+"/pin", a.token, map[string]any{"pinned": true}), 200)
	b.ws.readType("clip")
	b.ws.readType("clip")
	b.ws.readType("clip_pinned")

	h.clock.Advance(49 * time.Hour)
	if err := h.app.Service.RunRetention(t.Context(), ""); err != nil {
		t.Fatal(err)
	}
	ev := b.ws.readType("clip_deleted")
	if ev["reason"] != "retention" || len(ev["ids"].([]any)) != 1 || ev["ids"].([]any)[0] != old["id"] {
		t.Fatalf("age retention: %v", ev)
	}

	var ids []string
	for i := 0; i < 6; i++ {
		ids = append(ids, a.createInline(bytes.Repeat([]byte{byte('a' + i)}, 300))["id"].(string))
	}
	var deleted []any
	for {
		m, ok := b.ws.next(300 * time.Millisecond)
		if !ok {
			break
		}
		if m.err != nil {
			t.Fatal(m.err)
		}
		if m.data["type"] == "clip_deleted" {
			if m.data["reason"] != "retention" {
				t.Fatalf("unexpected delete: %v", m.data)
			}
			deleted = append(deleted, m.data["ids"].([]any)...)
		}
	}
	if len(deleted) == 0 || deleted[0] != ids[0] {
		t.Fatalf("size retention deleted %v, want oldest %s first", deleted, ids[0])
	}
	for _, d := range deleted {
		if d == ids[len(ids)-1] || d == keep["id"] {
			t.Fatalf("retention removed %v", d)
		}
	}
	st := h.mustStatus(h.do("GET", "/api/storage", a.token, nil, nil), 200).json(t)
	if num(st["used_bytes"]) > 2000 {
		t.Fatalf("storage over limit: %v", st)
	}
	idx := h.mustStatus(h.do("GET", "/api/history/index", a.token, nil, nil), 200).json(t)
	found := false
	for _, it := range idx["items"].([]any) {
		if it.(map[string]any)["id"] == keep["id"] {
			found = true
		}
	}
	if !found {
		t.Fatal("pinned item removed by retention")
	}
	big := a.createInline(bytes.Repeat([]byte("z"), 900))
	h.mustStatus(h.doJSON("POST", "/api/items/"+big["id"].(string)+"/pin", a.token, map[string]any{"pinned": true}), 409)
}

func TestDiskGuard(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	b.connect()
	h.setFree(gib)
	ev := b.ws.readType("storage_warning")
	if ev["active"] != true || num(ev["free_disk_bytes"]) != gib {
		t.Fatalf("warning: %v", ev)
	}
	c := h.dial(a.token)
	c.hello(a.deviceID)
	if m := c.read(); m["type"] != "storage_warning" || m["active"] != true {
		t.Fatalf("expected storage_warning after welcome, got %v", m)
	}

	id := newItemID(h.clock.Now())
	r := h.mustStatus(h.do("PUT", "/api/items/"+id+"/chunks/1", a.token, bytes.NewReader(make([]byte, 100)), nil), 507)
	if r.json(t)["code"] != "disk_low" {
		t.Fatal(string(r.Body))
	}
	h.mustStatus(h.do("PUT", "/api/items/"+id+"/chunks/0", a.token, bytes.NewReader(make([]byte, proto.DiskLowMaxChunkBody+1)), nil), 507)
	h.mustStatus(h.do("PUT", "/api/items/"+id+"/chunks/0", a.token, bytes.NewReader(make([]byte, proto.DiskLowMaxChunkBody)), nil), 204)
	h.mustStatus(h.do("PUT", "/api/items/"+id+"/thumb", a.token, bytes.NewReader(make([]byte, 1000)), nil), 204)
	a.createInline([]byte("inline still works"))
	h.mustStatus(h.doJSON("POST", "/api/items/"+id+"/commit", a.token, map[string]any{
		"kind": "files", "size": proto.DiskLowMaxUploadBytes + 1, "chunk_count": 1, "content_hash": a.contentHash([]byte("x")), "meta": a.meta(id, "files", []byte("x")),
	}), 400)

	small := newItemID(h.clock.Now())
	content := make([]byte, 300000)
	sealed := randomSeal(a.key, []byte(fmt.Sprintf("yc1|chunk|%s|0|1", small)), content)
	h.mustStatus(h.do("PUT", "/api/items/"+small+"/chunks/0", a.token, bytes.NewReader(sealed), nil), 204)
	h.mustStatus(h.doJSON("POST", "/api/items/"+small+"/commit", a.token, map[string]any{
		"kind": "files", "size": len(content), "chunk_count": 1, "content_hash": a.contentHash(content), "meta": a.meta(small, "files", content),
	}), 201)

	bigID := newItemID(h.clock.Now())
	h.setFree(10 * gib)
	if ev := b.ws.readType("storage_warning"); ev["active"] != false {
		t.Fatalf("cleared: %v", ev)
	}
	bigContent := make([]byte, proto.DiskLowMaxUploadBytes+10)
	bigSealed := randomSeal(a.key, []byte(fmt.Sprintf("yc1|chunk|%s|0|1", bigID)), bigContent)
	h.mustStatus(h.do("PUT", "/api/items/"+bigID+"/chunks/0", a.token, bytes.NewReader(bigSealed), nil), 204)
	h.setFree(gib)
	r = h.mustStatus(h.doJSON("POST", "/api/items/"+bigID+"/commit", a.token, map[string]any{
		"kind": "files", "size": len(bigContent), "chunk_count": 1, "content_hash": a.contentHash(bigContent), "meta": a.meta(bigID, "files", bigContent),
	}), 507)
	st := h.mustStatus(h.do("GET", "/api/storage", a.token, nil, nil), 200).json(t)
	if st["disk_low"] != true || num(st["free_disk_bytes"]) != gib {
		t.Fatalf("storage: %v", st)
	}
}

func TestHeartbeatTimeout(t *testing.T) {
	h := newHarness(t, harnessOpts{Hub: hubCfg(50*time.Millisecond, 400*time.Millisecond)})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	a.connect()
	b.connect()
	a.ws.readType("presence")

	pings := 0
	offline := false
	began := time.Now()
	deadline := time.After(5 * time.Second)
	for pings < 10 || !offline || time.Since(began) < 900*time.Millisecond {
		select {
		case m := <-b.ws.msgs:
			if m.err != nil {
				t.Fatalf("responsive client closed: %v", m.err)
			}
			switch m.data["type"] {
			case "ping":
				pings++
				b.ws.send(map[string]any{"type": "pong", "ts": m.data["ts"]})
			case "presence":
				if m.data["online"] == false && m.data["device_id"] == a.deviceID {
					offline = true
				}
			}
		case <-deadline:
			t.Fatalf("timeout: pings=%d offline=%v", pings, offline)
		}
	}
	a.ws.expectClose(4005)
}

func TestDeviceRevocationClosesSocket(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac", "Pixel")
	a, b := cs[0], cs[1]
	a.connect()
	b.connect()
	a.ws.readType("presence")
	h.mustStatus(h.do("DELETE", "/api/devices/"+a.deviceID, b.token, nil, nil), 204)
	a.ws.expectClose(4001)
	b.ws.readType("devices_changed")
	if p := b.ws.readType("presence"); p["online"] != false || p["device_id"] != a.deviceID {
		t.Fatalf("presence: %v", p)
	}
	h.mustStatus(h.do("GET", "/api/me", a.token, nil, nil), 401)
	devs := h.mustStatus(h.do("GET", "/api/devices", b.token, nil, nil), 200).json(t)["devices"].([]any)
	if devs[0].(map[string]any)["revoked"] != true {
		t.Fatalf("devices: %v", devs)
	}
	h.mustStatus(h.do("DELETE", "/api/devices/"+a.deviceID, b.token, nil, nil), 204)
	b.ws.readType("devices_changed")
	devs = h.mustStatus(h.do("GET", "/api/devices", b.token, nil, nil), 200).json(t)["devices"].([]any)
	if len(devs) != 1 {
		t.Fatalf("device not removed: %v", devs)
	}
	h.mustStatus(h.do("DELETE", "/api/devices/"+a.deviceID, b.token, nil, nil), 404)
	u := "ws" + h.srv.URL[len("http"):] + "/ws?token=" + a.token
	_, resp, err := websocket.Dial(t.Context(), u, nil)
	if err == nil || resp == nil || resp.StatusCode != 401 {
		t.Fatalf("revoked upgrade: %v %v", err, resp)
	}
}

func TestMaxConnectionsPerDevice(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Browser")
	a := cs[0]
	var conns []*wsClient
	for i := 0; i < proto.MaxConnectionsPerDev; i++ {
		c := h.dial(a.token)
		c.hello(a.deviceID)
		conns = append(conns, c)
	}
	extra := h.dial(a.token)
	extra.hello(a.deviceID)
	conns[0].expectClose(4002)
	for _, c := range conns[1:] {
		c.expectNone(50 * time.Millisecond)
	}
	a.createInline([]byte("x"))
	extra.readType("clip")
}

func TestQueryTokenOnWebSocket(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Browser")
	u := "ws" + h.srv.URL[len("http"):] + "/ws?token=" + cs[0].token
	c, _, err := websocket.Dial(t.Context(), u, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer c.CloseNow()
	data, _ := json.Marshal(map[string]any{"type": "hello", "protocol_version": 1, "device_id": cs[0].deviceID, "last_seq": 0, "app_version": "x", "platform": "web"})
	c.Write(t.Context(), websocket.MessageText, data)
	_, msg, err := c.Read(t.Context())
	if err != nil || !bytes.Contains(msg, []byte(`"welcome"`)) {
		t.Fatalf("welcome via query token: %s %v", msg, err)
	}
}
