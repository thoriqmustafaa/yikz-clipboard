package app

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"
)

type vectorCase struct {
	opts   harnessOpts
	seed   *seedOpts
	clock  time.Time
	before func(t *testing.T, h *harness, f *fixture)
	token  func(f *fixture) string
	body   func(f *fixture) []byte
	after  func(t *testing.T, h *harness, f *fixture, r response)
}

func uploadChunks(t *testing.T, h *harness, f *fixture, idx ...int) {
	large := f.Items["large"]
	for _, i := range idx {
		h.mustStatus(h.do("PUT", fmt.Sprintf("/api/items/%s/chunks/%d", large.ID, i), f.Mac.Token, bytes.NewReader(large.Chunks[i]), nil), 204)
	}
}

func vectorCases() map[string]vectorCase {
	full := &seedOpts{}
	withoutLarge := &seedOpts{Without: []string{"large"}, CurrentSeq: 43}
	return map[string]vectorCase{
		"login_new_device": {
			seed:  &seedOpts{NoDevices: true, NoItems: true, NoKeyCheck: true},
			clock: time.Date(2026, 9, 1, 8, 5, 0, 0, time.UTC),
			before: func(t *testing.T, h *harness, f *fixture) {
				h.rand.Push(testBytes("uuid|mac", 10))
				h.rand.Push(testBytes("token|mac", 32))
			},
			after: func(t *testing.T, h *harness, f *fixture, r response) {
				h.mustStatus(h.do("GET", "/api/me", f.Mac.Token, nil, nil), 200)
			},
		},
		"login_existing_device": {
			seed: full,
			before: func(t *testing.T, h *harness, f *fixture) {
				h.rand.Push(testBytes("token|android", 32))
			},
			after: func(t *testing.T, h *harness, f *fixture, r response) {
				h.mustStatus(h.do("GET", "/api/me", f.Android.Token, nil, nil), 401)
			},
		},
		"login_invalid_credentials": {seed: full},
		"login_invalid_platform":    {seed: full},
		"login_rate_limited": {
			seed: full,
			before: func(t *testing.T, h *harness, f *fixture) {
				body := map[string]any{"username": "thoriq", "password": "wrong", "device_name": "Pixel 9", "platform": "android"}
				for i := 0; i < 10; i++ {
					h.mustStatus(h.doJSON("POST", "/api/login", "", body), 401)
				}
			},
		},
		"me": {
			seed: full,
			before: func(t *testing.T, h *harness, f *fixture) {
				h.dial(f.Android.Token).hello(f.Android.ID)
				h.dial(f.Mac.Token).hello(f.Mac.ID)
			},
		},
		"me_unauthorized": {seed: full},
		"logout": {
			seed: full,
			after: func(t *testing.T, h *harness, f *fixture, r response) {
				h.mustStatus(h.do("GET", "/api/me", f.Mac.Token, nil, nil), 401)
			},
		},
		"key_check_set":           {seed: &seedOpts{NoItems: true, NoKeyCheck: true}},
		"key_check_conflict":      {seed: full},
		"key_check_reset_refused": {seed: full},
		"devices_list": {
			seed: full,
			before: func(t *testing.T, h *harness, f *fixture) {
				h.dial(f.Android.Token).hello(f.Android.ID)
				h.dial(f.Mac.Token).hello(f.Mac.ID)
			},
		},
		"device_rename": {seed: full},
		"device_revoke": {
			seed: full,
			after: func(t *testing.T, h *harness, f *fixture, r response) {
				h.mustStatus(h.do("GET", "/api/me", f.Windows.Token, nil, nil), 401)
			},
		},
		"device_not_found": {seed: full},
		"item_create_inline": {
			seed:  &seedOpts{NoItems: true, CurrentSeq: 40},
			clock: time.Date(2026, 9, 21, 10, 0, 1, 250_000_000, time.UTC),
		},
		"item_create_inline_with_thumb_uploaded_first": {
			seed:  &seedOpts{Without: []string{"image", "files", "large"}, CurrentSeq: 41},
			clock: time.Date(2026, 9, 21, 10, 2, 0, 0, time.UTC),
			token: func(f *fixture) string { return f.Android.Token },
			before: func(t *testing.T, h *harness, f *fixture) {
				img := f.Items["image"]
				h.mustStatus(h.do("PUT", "/api/items/"+img.ID+"/thumb", f.Android.Token, bytes.NewReader(img.Thumb), nil), 204)
			},
		},
		"item_create_repeat_is_idempotent":  {seed: full},
		"item_create_id_conflict":           {seed: full},
		"item_create_payload_size_mismatch": {seed: full},
		"item_create_key_check_missing":     {seed: &seedOpts{NoItems: true, NoKeyCheck: true, CurrentSeq: 40}},
		"thumb_upload": {
			seed: &seedOpts{Without: []string{"image"}},
		},
		"chunk_upload": {
			seed: withoutLarge,
			body: func(f *fixture) []byte { return f.Items["large"].Chunks[0] },
		},
		"chunk_upload_too_large": {
			seed: withoutLarge,
			body: func(f *fixture) []byte { return make([]byte, 4194333) },
		},
		"chunk_upload_disk_low": {
			seed: withoutLarge,
			before: func(t *testing.T, h *harness, f *fixture) {
				h.setFree(1717986918)
			},
			body: func(f *fixture) []byte { return make([]byte, 4194332) },
		},
		"chunk_upload_after_commit": {
			seed: full,
			body: func(f *fixture) []byte { return make([]byte, 100) },
		},
		"commit": {
			seed:  withoutLarge,
			clock: time.Date(2026, 9, 21, 10, 5, 0, 0, time.UTC),
			before: func(t *testing.T, h *harness, f *fixture) {
				uploadChunks(t, h, f, 2, 0, 1)
			},
			after: func(t *testing.T, h *harness, f *fixture, r response) {
				large := f.Items["large"]
				for i, c := range large.Chunks {
					got := h.mustStatus(h.do("GET", fmt.Sprintf("/api/items/%s/chunks/%d", large.ID, i), f.Mac.Token, nil, nil), 200)
					if !bytes.Equal(got.Body, c) {
						t.Fatalf("chunk %d mismatch after commit", i)
					}
				}
			},
		},
		"commit_missing_chunks": {
			seed: withoutLarge,
			before: func(t *testing.T, h *harness, f *fixture) {
				uploadChunks(t, h, f, 0)
			},
		},
		"commit_chunk_size_mismatch": {
			seed: withoutLarge,
			before: func(t *testing.T, h *harness, f *fixture) {
				uploadChunks(t, h, f, 0, 1)
				large := f.Items["large"]
				short := large.Chunks[2][:len(large.Chunks[2])-3]
				h.mustStatus(h.do("PUT", "/api/items/"+large.ID+"/chunks/2", f.Mac.Token, bytes.NewReader(short), nil), 204)
			},
		},
		"commit_item_too_large": {
			opts: harnessOpts{StorageMax: 8390000, PinnedMax: 1 << 20},
			seed: withoutLarge,
			before: func(t *testing.T, h *harness, f *fixture) {
				uploadChunks(t, h, f, 0, 1, 2)
			},
		},
		"item_get_inline":     {seed: full},
		"item_get_chunked":    {seed: full},
		"item_get_not_found":  {seed: full},
		"item_get_invalid_id": {seed: full},
		"chunk_download": {
			seed: full,
			after: func(t *testing.T, h *harness, f *fixture, r response) {
				if !bytes.Equal(r.Body, f.Items["large"].Chunks[2]) {
					t.Fatal("chunk body mismatch")
				}
			},
		},
		"thumb_download":         {seed: full},
		"history_newest":         {seed: full},
		"history_before":         {seed: full},
		"history_after_catch_up": {seed: full},
		"history_bad_params":     {seed: full},
		"history_index":          {seed: full},
		"pin":                    {seed: full},
		"unpin":                  {seed: full},
		"pin_limit": {
			opts: harnessOpts{PinnedMax: 1 << 20},
			seed: full,
		},
		"item_delete": {
			seed: full,
			after: func(t *testing.T, h *harness, f *fixture, r response) {
				h.mustStatus(h.do("GET", "/api/items/"+f.Items["image"].ID, f.Mac.Token, nil, nil), 404)
				h.mustStatus(h.do("GET", "/api/items/"+f.Items["image"].ID+"/thumb", f.Mac.Token, nil, nil), 404)
			},
		},
		"storage":                 {seed: full},
		"healthz":                 {},
		"ws_upgrade_unauthorized": {seed: full},
		"unknown_route":           {seed: full},
		"method_not_allowed":      {seed: full},
	}
}

func normalize(t *testing.T, v any) any {
	t.Helper()
	data, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	var out any
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	if err := dec.Decode(&out); err != nil {
		t.Fatal(err)
	}
	return out
}

func TestHTTPVectors(t *testing.T) {
	f := loadFixture(t)
	var v httpVectors
	loadVector(t, "http.json", &v)
	cases := vectorCases()
	seen := map[string]bool{}
	for _, ex := range v.Examples {
		ex := ex
		c, ok := cases[ex.Name]
		if !ok {
			t.Errorf("no server test case for http vector %q", ex.Name)
			continue
		}
		seen[ex.Name] = true
		t.Run(ex.Name, func(t *testing.T) {
			h := newHarness(t, c.opts)
			if c.seed != nil {
				h.seed(f, *c.seed)
			}
			if c.before != nil {
				c.before(t, h, f)
			}
			if !c.clock.IsZero() {
				h.clock.Set(c.clock)
			}
			var body io.Reader
			switch rb := ex.RequestBody.(type) {
			case nil:
			case string:
				raw, err := base64.StdEncoding.DecodeString(rb)
				if err != nil {
					t.Fatal(err)
				}
				body = bytes.NewReader(raw)
			default:
				data, err := json.Marshal(rb)
				if err != nil {
					t.Fatal(err)
				}
				body = bytes.NewReader(data)
			}
			if c.body != nil {
				body = bytes.NewReader(c.body(f))
			}
			req, err := http.NewRequest(ex.Method, h.srv.URL+ex.Path, body)
			if err != nil {
				t.Fatal(err)
			}
			for k, val := range ex.RequestHeaders {
				req.Header.Set(k, val)
			}
			if c.token != nil {
				req.Header.Set("Authorization", "Bearer "+c.token(f))
			}
			resp, err := h.srv.Client().Do(req)
			if err != nil {
				t.Fatal(err)
			}
			data, _ := io.ReadAll(resp.Body)
			resp.Body.Close()
			r := response{Status: resp.StatusCode, Header: resp.Header, Body: data}

			if r.Status != ex.Status {
				t.Fatalf("status %d, want %d; body %s", r.Status, ex.Status, data)
			}
			for k, want := range ex.ResponseHeaders {
				if got := resp.Header.Get(k); got != want {
					t.Errorf("header %s = %q, want %q", k, got, want)
				}
			}
			switch want := ex.ResponseBody.(type) {
			case nil:
				if r.Status == http.StatusNoContent && len(data) != 0 {
					t.Errorf("204 with body %q", data)
				}
			case string:
				raw, _ := base64.StdEncoding.DecodeString(want)
				if !bytes.Equal(raw, data) {
					t.Errorf("binary body mismatch: %d bytes, want %d", len(data), len(raw))
				}
			default:
				if ct := resp.Header.Get("Content-Type"); ct != "application/json; charset=utf-8" {
					t.Errorf("content type %q", ct)
				}
				var got any
				dec := json.NewDecoder(bytes.NewReader(data))
				dec.UseNumber()
				if err := dec.Decode(&got); err != nil {
					t.Fatalf("decode body %q: %v", data, err)
				}
				exp := normalize(t, want)
				if !reflect.DeepEqual(got, exp) {
					gj, _ := json.MarshalIndent(got, "", "  ")
					ej, _ := json.MarshalIndent(exp, "", "  ")
					t.Errorf("body mismatch\n got: %s\nwant: %s", gj, ej)
				}
			}
			if c.after != nil {
				c.after(t, h, f, r)
			}
		})
	}
	for name := range cases {
		if !seen[name] {
			t.Errorf("test case %q has no matching vector", name)
		}
	}
}

type wsVectors struct {
	CloseCodes []struct {
		Code int `json:"code"`
	} `json:"close_codes"`
	Messages []struct {
		Name      string         `json:"name"`
		Direction string         `json:"direction"`
		Message   map[string]any `json:"message"`
	} `json:"messages"`
}

func (v wsVectors) msg(t *testing.T, name string) any {
	t.Helper()
	for _, m := range v.Messages {
		if m.Name == name {
			return normalize(t, m.Message)
		}
	}
	t.Fatalf("ws vector %s not found", name)
	return nil
}

func assertMsg(t *testing.T, got map[string]any, want any) {
	t.Helper()
	g := normalize(t, got)
	if !reflect.DeepEqual(g, want) {
		gj, _ := json.MarshalIndent(g, "", "  ")
		wj, _ := json.MarshalIndent(want, "", "  ")
		t.Fatalf("message mismatch\n got: %s\nwant: %s", gj, wj)
	}
}

func TestWebSocketVectors(t *testing.T) {
	f := loadFixture(t)
	var v wsVectors
	loadVector(t, "ws.json", &v)

	for _, cc := range v.CloseCodes {
		switch websocket.StatusCode(cc.Code) {
		case 1000, 1001, 4001, 4002, 4003, 4004, 4005:
		default:
			t.Errorf("unexpected close code %d in vectors", cc.Code)
		}
	}

	for _, m := range v.Messages {
		if m.Direction != "client_to_server" {
			continue
		}
		if _, err := json.Marshal(m.Message); err != nil || m.Message["type"] == nil {
			t.Errorf("client message %s invalid", m.Name)
		}
	}

	t.Run("welcome_presence_devices_changed", func(t *testing.T) {
		h := newHarness(t, harnessOpts{})
		h.seed(f, seedOpts{})
		android := h.dial(f.Android.Token)
		android.hello(f.Android.ID)
		mac := h.dial(f.Mac.Token)
		hello := v.msg(t, "hello").(map[string]any)
		mac.send(hello)
		assertMsg(t, mac.read(), v.msg(t, "welcome"))
		android.readType("presence")

		win := h.dial(f.Windows.Token)
		win.hello(f.Windows.ID)
		assertMsg(t, mac.read(), v.msg(t, "presence_online"))
		win.c.Close(websocket.StatusNormalClosure, "")
		assertMsg(t, mac.read(), v.msg(t, "presence_offline"))

		h.mustStatus(h.doJSON("PATCH", "/api/devices/"+f.Windows.ID, f.Android.Token, map[string]any{"name": "Gaming PC"}), 200)
		assertMsg(t, mac.read(), v.msg(t, "devices_changed"))
	})

	t.Run("clip_inline_text", func(t *testing.T) {
		h := newHarness(t, harnessOpts{})
		h.seed(f, seedOpts{NoItems: true, CurrentSeq: 40})
		mac := h.dial(f.Mac.Token)
		mac.hello(f.Mac.ID)
		h.clock.Set(time.Date(2026, 9, 21, 10, 0, 1, 250_000_000, time.UTC))
		var hv httpVectors
		loadVector(t, "http.json", &hv)
		h.mustStatus(h.doJSON("POST", "/api/items", f.Mac.Token, hv.find(t, "item_create_inline").RequestBody), 201)
		assertMsg(t, mac.read(), v.msg(t, "clip_inline_text"))
	})

	t.Run("clip_inline_image_and_chunked", func(t *testing.T) {
		h := newHarness(t, harnessOpts{})
		h.seed(f, seedOpts{Without: []string{"image", "files", "large"}, CurrentSeq: 41})
		mac := h.dial(f.Mac.Token)
		mac.hello(f.Mac.ID)
		var hv httpVectors
		loadVector(t, "http.json", &hv)
		img := f.Items["image"]
		h.mustStatus(h.do("PUT", "/api/items/"+img.ID+"/thumb", f.Android.Token, bytes.NewReader(img.Thumb), nil), 204)
		h.clock.Set(img.CreatedAt)
		h.mustStatus(h.doJSON("POST", "/api/items", f.Android.Token, hv.find(t, "item_create_inline_with_thumb_uploaded_first").RequestBody), 201)
		assertMsg(t, mac.read(), v.msg(t, "clip_inline_image"))

		h.app.Store.DB().Exec("UPDATE account SET current_seq = 43")
		uploadChunks(t, h, f, 0, 1, 2)
		h.clock.Set(f.Items["large"].CreatedAt)
		h.mustStatus(h.doJSON("POST", "/api/items/"+f.Items["large"].ID+"/commit", f.Mac.Token, hv.find(t, "commit").RequestBody), 201)
		assertMsg(t, mac.read(), v.msg(t, "clip_chunked"))
	})

	t.Run("clip_deleted_user", func(t *testing.T) {
		h := newHarness(t, harnessOpts{})
		h.seed(f, seedOpts{})
		mac := h.dial(f.Mac.Token)
		mac.hello(f.Mac.ID)
		h.mustStatus(h.do("DELETE", "/api/items/"+f.Items["image"].ID, f.Mac.Token, nil, nil), 204)
		assertMsg(t, mac.read(), v.msg(t, "clip_deleted_user"))
		h.mustStatus(h.do("DELETE", "/api/items/"+f.Items["image"].ID, f.Mac.Token, nil, nil), 404)
		mac.expectNone(100 * time.Millisecond)
	})

	t.Run("clip_deleted_retention", func(t *testing.T) {
		h := newHarness(t, harnessOpts{StorageMax: 8391704 - 296 - 990})
		h.seed(f, seedOpts{StateRev: 18})
		mac := h.dial(f.Mac.Token)
		mac.hello(f.Mac.ID)
		if err := h.app.Service.RunRetention(t.Context(), ""); err != nil {
			t.Fatal(err)
		}
		assertMsg(t, mac.read(), v.msg(t, "clip_deleted_retention"))
	})

	t.Run("clip_deleted_dedupe_and_pinned", func(t *testing.T) {
		h := newHarness(t, harnessOpts{})
		h.seed(f, seedOpts{StateRev: 19})
		mac := h.dial(f.Mac.Token)
		mac.hello(f.Mac.ID)
		text := f.Items["text"]
		dupID := "01a0c368-81e2-7460-8f15-000000000001"
		dup := map[string]any{
			"id": dupID, "kind": "text", "size": text.Size, "chunk_count": 0,
			"content_hash": text.ContentHash,
			"meta":         base64.StdEncoding.EncodeToString(randomSeal(f.Key, []byte("yc1|meta|"+dupID), []byte(`{"v":1}`))),
			"payload":      base64.StdEncoding.EncodeToString(randomSeal(f.Key, []byte("yc1|payload|"+dupID), text.Content)),
		}
		h.mustStatus(h.doJSON("POST", "/api/items", f.Mac.Token, dup), 201)
		if got := mac.read(); got["type"] != "clip" {
			t.Fatalf("expected clip, got %v", got)
		}
		assertMsg(t, mac.read(), v.msg(t, "clip_deleted_dedupe"))

		h.seedItem(f, "text")
		h.mustStatus(h.doJSON("POST", "/api/items/"+text.ID+"/pin", f.Mac.Token, map[string]any{"pinned": true}), 200)
		assertMsg(t, mac.read(), v.msg(t, "clip_pinned"))
	})

	t.Run("storage_warning", func(t *testing.T) {
		h := newHarness(t, harnessOpts{})
		h.seed(f, seedOpts{})
		mac := h.dial(f.Mac.Token)
		mac.hello(f.Mac.ID)
		h.setFree(1717986918)
		assertMsg(t, mac.read(), v.msg(t, "storage_warning_active"))
		late := h.dial(f.Android.Token)
		late.hello(f.Android.ID)
		assertMsg(t, late.read(), v.msg(t, "storage_warning_active"))
		h.setFree(3221225472)
		assertMsg(t, mac.readType("storage_warning"), v.msg(t, "storage_warning_cleared"))
		assertMsg(t, late.readType("storage_warning"), v.msg(t, "storage_warning_cleared"))
	})

	t.Run("ping_pong", func(t *testing.T) {
		h := newHarness(t, harnessOpts{Hub: hubCfg(100*time.Millisecond, 10*time.Second)})
		h.seed(f, seedOpts{})
		mac := h.dial(f.Mac.Token)
		mac.hello(f.Mac.ID)
		mac.send(v.msg(t, "ping_from_client"))
		for {
			m, ok := mac.next(5 * time.Second)
			if !ok || m.err != nil {
				t.Fatal("no pong")
			}
			if m.data["type"] == "pong" {
				assertMsg(t, m.data, v.msg(t, "pong_from_server"))
				break
			}
		}
		h.clock.Set(time.UnixMilli(1789985420000))
		for {
			m, ok := mac.next(5 * time.Second)
			if !ok || m.err != nil {
				t.Fatal("no server ping")
			}
			if m.data["type"] == "ping" && num(m.data["ts"]) == 1789985420000 {
				assertMsg(t, m.data, v.msg(t, "ping_from_server"))
				break
			}
		}
		mac.send(v.msg(t, "pong_from_client"))
		mac.expectNone(300*time.Millisecond, "ping")
	})

	t.Run("errors", func(t *testing.T) {
		h := newHarness(t, harnessOpts{})
		h.seed(f, seedOpts{})

		c := h.dial(f.Mac.Token)
		c.send(map[string]any{"type": "hello", "protocol_version": 2, "device_id": f.Mac.ID, "last_seq": 0, "app_version": "x", "platform": "macos"})
		assertMsg(t, c.read(), v.msg(t, "error_protocol_version"))
		c.expectClose(4003)

		c = h.dial(f.Mac.Token)
		c.send(map[string]any{"type": "hello", "protocol_version": 1, "device_id": f.Android.ID, "last_seq": 0, "app_version": "x", "platform": "macos"})
		assertMsg(t, c.read(), v.msg(t, "error_device_mismatch"))
		c.expectClose(4001)

		c = h.dial(f.Mac.Token)
		c.send(map[string]any{"type": "foo"})
		assertMsg(t, c.read(), v.msg(t, "error_not_ready"))
		c.expectClose(4004)

		c = h.dial(f.Mac.Token)
		c.send(map[string]any{"type": "ping", "ts": 5})
		if m := c.read(); m["type"] != "pong" || num(m["ts"]) != 5 {
			t.Fatalf("pre-hello pong: %v", m)
		}
		c.hello(f.Mac.ID)
		c.send(map[string]any{"type": "foo"})
		assertMsg(t, c.read(), v.msg(t, "error_unknown_type"))
		c.sendRaw(websocket.MessageText, []byte("not json"))
		assertMsg(t, c.read(), v.msg(t, "error_invalid_message"))
		c.sendRaw(websocket.MessageText, []byte(`{"type": 5}`))
		assertMsg(t, c.read(), v.msg(t, "error_invalid_message"))
		c.sendRaw(websocket.MessageText, []byte(`[1,2]`))
		assertMsg(t, c.read(), v.msg(t, "error_invalid_message"))
		c.send(map[string]any{"type": "ping", "ts": 7})
		if m := c.read(); m["type"] != "pong" || num(m["ts"]) != 7 {
			t.Fatalf("pong: %v", m)
		}
		c.sendRaw(websocket.MessageText, []byte(`{"type":"ping","pad":"`+strings.Repeat("x", 70000)+`"}`))
		c.expectClose(websocket.StatusMessageTooBig)
	})

	t.Run("hello_timeout", func(t *testing.T) {
		cfg := hubCfg(time.Second, 10*time.Second)
		cfg.HelloTimeout = 200 * time.Millisecond
		h := newHarness(t, harnessOpts{Hub: cfg})
		h.seed(f, seedOpts{})
		c := h.dial(f.Mac.Token)
		c.expectClose(4004)
	})
}
