package app

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"net"
	"net/http"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/coder/websocket"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
)

type patternReader struct {
	remaining int64
	offset    int64
}

func (p *patternReader) Read(b []byte) (int, error) {
	if p.remaining == 0 {
		return 0, io.EOF
	}
	n := int64(len(b))
	if n > p.remaining {
		n = p.remaining
	}
	for i := int64(0); i < n; i++ {
		b[i] = byte((p.offset + i) % 251)
	}
	p.offset += n
	p.remaining -= n
	return int(n), nil
}

type heapSampler struct {
	peak atomic.Uint64
	stop chan struct{}
	wg   sync.WaitGroup
}

func startSampler() *heapSampler {
	s := &heapSampler{stop: make(chan struct{})}
	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		var ms runtime.MemStats
		t := time.NewTicker(5 * time.Millisecond)
		defer t.Stop()
		for {
			runtime.ReadMemStats(&ms)
			if ms.HeapInuse > s.peak.Load() {
				s.peak.Store(ms.HeapInuse)
			}
			select {
			case <-s.stop:
				return
			case <-t.C:
			}
		}
	}()
	return s
}

func (s *heapSampler) Stop() uint64 {
	close(s.stop)
	s.wg.Wait()
	return s.peak.Load()
}

func baseline() runtime.MemStats {
	runtime.GC()
	runtime.GC()
	var ms runtime.MemStats
	runtime.ReadMemStats(&ms)
	return ms
}

func TestLargeUploadMemoryIsBounded(t *testing.T) {
	if testing.Short() {
		t.Skip("skipping 200 MiB upload in short mode")
	}
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac")
	a := cs[0]
	const size = int64(200 << 20)
	count := proto.ChunkCount(size)
	id := newItemID(h.clock.Now())

	before := baseline()
	sampler := startSampler()
	for i := 0; i < count; i++ {
		body := &patternReader{remaining: proto.ChunkSealedSize(size, i, count), offset: int64(i) * proto.MaxChunkBodyBytes}
		req, err := http.NewRequest("PUT", fmt.Sprintf("%s/api/items/%s/chunks/%d", h.srv.URL, id, i), body)
		if err != nil {
			t.Fatal(err)
		}
		req.ContentLength = body.remaining
		req.Header.Set("Authorization", "Bearer "+a.token)
		resp, err := h.srv.Client().Do(req)
		if err != nil {
			t.Fatal(err)
		}
		io.Copy(io.Discard, resp.Body)
		resp.Body.Close()
		if resp.StatusCode != 204 {
			t.Fatalf("chunk %d: status %d", i, resp.StatusCode)
		}
	}
	peakUpload := sampler.Stop()
	after := baseline()

	h.mustStatus(h.doJSON("POST", "/api/items/"+id+"/commit", a.token, map[string]any{
		"kind": "files", "size": size, "chunk_count": count,
		"content_hash": strings.Repeat("ab", 32), "meta": a.meta(id, "files", []byte("x")),
	}), 201)

	sampler = startSampler()
	hasher := sha256.New()
	want := sha256.New()
	for i := 0; i < count; i++ {
		req, _ := http.NewRequest("GET", fmt.Sprintf("%s/api/items/%s/chunks/%d", h.srv.URL, id, i), nil)
		req.Header.Set("Authorization", "Bearer "+a.token)
		resp, err := h.srv.Client().Do(req)
		if err != nil {
			t.Fatal(err)
		}
		n, _ := io.Copy(hasher, resp.Body)
		resp.Body.Close()
		expected := proto.ChunkSealedSize(size, i, count)
		if resp.StatusCode != 200 || n != expected {
			t.Fatalf("download chunk %d: status %d, %d bytes", i, resp.StatusCode, n)
		}
		io.Copy(want, &patternReader{remaining: expected, offset: int64(i) * proto.MaxChunkBodyBytes})
	}
	peakDownload := sampler.Stop()
	if hex.EncodeToString(hasher.Sum(nil)) != hex.EncodeToString(want.Sum(nil)) {
		t.Fatal("downloaded bytes differ from uploaded bytes")
	}

	uploaded := uint64(size) + uint64(count)*proto.SealOverhead
	growth := int64(peakUpload) - int64(before.HeapInuse)
	retained := int64(after.HeapInuse) - int64(before.HeapInuse)
	allocated := after.TotalAlloc - before.TotalAlloc
	dlGrowth := int64(peakDownload) - int64(before.HeapInuse)
	t.Logf("uploaded %d MiB: heap in use before %.1f MiB, peak %.1f MiB (growth %.1f MiB), retained %.1f MiB, total allocated %.1f MiB; download peak growth %.1f MiB",
		uploaded>>20, mib(before.HeapInuse), mib(peakUpload), float64(growth)/(1<<20), float64(retained)/(1<<20), mib(allocated), float64(dlGrowth)/(1<<20))
	if growth > 32<<20 {
		t.Fatalf("heap grew by %.1f MiB during a %d MiB upload", float64(growth)/(1<<20), uploaded>>20)
	}
	if dlGrowth > 32<<20 {
		t.Fatalf("heap grew by %.1f MiB during a %d MiB download", float64(dlGrowth)/(1<<20), uploaded>>20)
	}
	if allocated > uploaded/4 {
		t.Fatalf("allocated %.1f MiB for a %d MiB upload; bodies are being buffered", mib(allocated), uploaded>>20)
	}
}

func mib(n uint64) float64 { return float64(n) / (1 << 20) }

func TestGracefulShutdownClosesSockets(t *testing.T) {
	h := newHarness(t, harnessOpts{})
	cs := setupClients(t, h, "Mac")
	a := cs[0]
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- h.app.Serve(ctx, ln) }()

	u := "ws://" + ln.Addr().String() + "/ws"
	c, _, err := websocket.Dial(t.Context(), u, &websocket.DialOptions{HTTPHeader: http.Header{"Authorization": {"Bearer " + a.token}}})
	if err != nil {
		t.Fatal(err)
	}
	defer c.CloseNow()
	c.Write(t.Context(), websocket.MessageText, []byte(fmt.Sprintf(`{"type":"hello","protocol_version":1,"device_id":%q,"last_seq":0,"app_version":"x","platform":"macos"}`, a.deviceID)))
	if _, msg, err := c.Read(t.Context()); err != nil || !strings.Contains(string(msg), "welcome") {
		t.Fatalf("welcome: %s %v", msg, err)
	}
	resp, err := http.Get("http://" + ln.Addr().String() + "/healthz")
	if err != nil || resp.StatusCode != 200 {
		t.Fatalf("healthz: %v", err)
	}
	resp.Body.Close()

	readErr := make(chan error, 1)
	go func() {
		for {
			if _, _, err := c.Read(context.Background()); err != nil {
				readErr <- err
				return
			}
		}
	}()
	cancel()
	select {
	case err := <-readErr:
		if websocket.CloseStatus(err) != websocket.StatusGoingAway {
			t.Fatalf("close status %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("socket not closed on shutdown")
	}
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("Serve did not return")
	}
}
