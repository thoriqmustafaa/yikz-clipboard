package retention

import (
	"context"
	"log/slog"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

type Policy struct {
	MaxAge          time.Duration
	StorageMaxBytes int64
}

func Plan(items []store.ItemSummary, now time.Time, p Policy, keepID string) []string {
	cutoff := now.Add(-p.MaxAge)
	var used int64
	for _, it := range items {
		used += it.StoredBytes
	}
	var out []string
	removed := make(map[string]bool)
	for _, it := range items {
		if it.Pinned || it.ID == keepID {
			continue
		}
		if it.CreatedAt.Before(cutoff) {
			out = append(out, it.ID)
			removed[it.ID] = true
			used -= it.StoredBytes
		}
	}
	for _, it := range items {
		if used <= p.StorageMaxBytes {
			break
		}
		if it.Pinned || it.ID == keepID || removed[it.ID] {
			continue
		}
		out = append(out, it.ID)
		removed[it.ID] = true
		used -= it.StoredBytes
	}
	return out
}

type Runner interface {
	RunRetention(ctx context.Context, keepID string) error
}

func Schedule(ctx context.Context, r Runner, interval time.Duration, log *slog.Logger) {
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			if err := r.RunRetention(ctx, ""); err != nil && ctx.Err() == nil {
				log.Error("retention run failed", "err", err)
			}
		}
	}
}
