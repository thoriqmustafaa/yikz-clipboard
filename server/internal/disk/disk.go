package disk

import (
	"context"
	"log/slog"
	"sync"
	"syscall"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/clock"
)

type FreeFunc func(path string) (int64, error)

func StatfsFree(path string) (int64, error) {
	var st syscall.Statfs_t
	if err := syscall.Statfs(path, &st); err != nil {
		return 0, err
	}
	return int64(uint64(st.Bavail) * uint64(st.Bsize)), nil
}

type Status struct {
	FreeBytes    int64
	MinFreeBytes int64
	Low          bool
}

type Guard struct {
	path     string
	min      int64
	free     FreeFunc
	clock    clock.Clock
	maxAge   time.Duration
	log      *slog.Logger
	onChange func(Status)

	refreshMu sync.Mutex
	mu        sync.Mutex
	measured  time.Time
	status    Status
	known     bool
}

type Options struct {
	Path         string
	MinFreeBytes int64
	Free         FreeFunc
	Clock        clock.Clock
	MaxAge       time.Duration
	Logger       *slog.Logger
}

func NewGuard(o Options) *Guard {
	if o.Free == nil {
		o.Free = StatfsFree
	}
	if o.Clock == nil {
		o.Clock = clock.Real{}
	}
	if o.MaxAge <= 0 {
		o.MaxAge = 10 * time.Second
	}
	if o.Logger == nil {
		o.Logger = slog.Default()
	}
	return &Guard{path: o.Path, min: o.MinFreeBytes, free: o.Free, clock: o.Clock, maxAge: o.MaxAge, log: o.Logger}
}

func (g *Guard) OnChange(fn func(Status)) {
	g.mu.Lock()
	g.onChange = fn
	g.mu.Unlock()
}

func (g *Guard) Status() Status {
	g.mu.Lock()
	if g.known && g.clock.Now().Sub(g.measured) < g.maxAge {
		s := g.status
		g.mu.Unlock()
		return s
	}
	g.mu.Unlock()
	return g.Refresh()
}

func (g *Guard) Current() Status {
	g.mu.Lock()
	defer g.mu.Unlock()
	if !g.known {
		return Status{MinFreeBytes: g.min}
	}
	return g.status
}

func (g *Guard) Refresh() Status {
	g.refreshMu.Lock()
	defer g.refreshMu.Unlock()
	free, err := g.free(g.path)
	g.mu.Lock()
	if err != nil {
		g.log.Error("measure free disk space", "path", g.path, "err", err)
		prev := g.status
		prev.MinFreeBytes = g.min
		g.mu.Unlock()
		return prev
	}
	s := Status{FreeBytes: free, MinFreeBytes: g.min, Low: free < g.min}
	changed := !g.known || s.Low != g.status.Low
	wasKnown := g.known
	g.status = s
	g.known = true
	g.measured = g.clock.Now()
	cb := g.onChange
	g.mu.Unlock()
	if changed && (wasKnown || s.Low) {
		if s.Low {
			g.log.Warn("disk space low", "free_bytes", s.FreeBytes, "min_free_bytes", s.MinFreeBytes)
		} else {
			g.log.Info("disk space recovered", "free_bytes", s.FreeBytes, "min_free_bytes", s.MinFreeBytes)
		}
		if cb != nil {
			cb(s)
		}
	}
	return s
}

func (g *Guard) Run(ctx context.Context, interval time.Duration) {
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			g.Refresh()
		}
	}
}
