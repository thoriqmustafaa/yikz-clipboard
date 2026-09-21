package disk

import (
	"sync"
	"testing"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/clock"
)

func TestGuard(t *testing.T) {
	c := clock.NewFake(time.Date(2026, 9, 21, 10, 0, 0, 0, time.UTC))
	var mu sync.Mutex
	free := int64(10 << 30)
	calls := 0
	g := NewGuard(Options{Path: "/x", MinFreeBytes: 2 << 30, Clock: c, Free: func(string) (int64, error) {
		mu.Lock()
		defer mu.Unlock()
		calls++
		return free, nil
	}})
	var events []Status
	g.OnChange(func(s Status) { events = append(events, s) })
	if s := g.Status(); s.Low || s.FreeBytes != 10<<30 {
		t.Fatalf("status = %+v", s)
	}
	mu.Lock()
	free = 1 << 30
	mu.Unlock()
	if s := g.Status(); s.Low {
		t.Fatal("cached value should be used within 10 s")
	}
	c.Advance(10 * time.Second)
	if s := g.Status(); !s.Low {
		t.Fatal("expected low after cache expiry")
	}
	mu.Lock()
	free = 3 << 30
	mu.Unlock()
	g.Refresh()
	if len(events) != 2 || !events[0].Low || events[1].Low {
		t.Fatalf("events = %+v", events)
	}
	if calls != 3 {
		t.Fatalf("calls = %d", calls)
	}
}

func TestStatfs(t *testing.T) {
	n, err := StatfsFree(t.TempDir())
	if err != nil || n <= 0 {
		t.Fatalf("StatfsFree = %d, %v", n, err)
	}
}
