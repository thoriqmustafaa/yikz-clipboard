package ratelimit

import (
	"math"
	"sync"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/clock"
)

type Limiter struct {
	max        int
	window     time.Duration
	maxEntries int
	clock      clock.Clock

	mu       sync.Mutex
	failures map[string][]time.Time
}

func New(max int, window time.Duration, c clock.Clock) *Limiter {
	if c == nil {
		c = clock.Real{}
	}
	return &Limiter{max: max, window: window, maxEntries: 10000, clock: c, failures: map[string][]time.Time{}}
}

func (l *Limiter) prune(key string, now time.Time) []time.Time {
	list := l.failures[key]
	cut := 0
	for cut < len(list) && !list[cut].After(now.Add(-l.window)) {
		cut++
	}
	list = list[cut:]
	if len(list) == 0 {
		delete(l.failures, key)
		return nil
	}
	l.failures[key] = list
	return list
}

func (l *Limiter) RetryAfter(key string) (time.Duration, bool) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock.Now()
	list := l.prune(key, now)
	if len(list) < l.max {
		return 0, false
	}
	wait := list[len(list)-l.max].Add(l.window).Sub(now)
	if wait < time.Second {
		wait = time.Second
	}
	return wait, true
}

func (l *Limiter) Fail(key string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock.Now()
	l.prune(key, now)
	if _, ok := l.failures[key]; !ok && len(l.failures) >= l.maxEntries {
		for k := range l.failures {
			l.prune(k, now)
		}
		if len(l.failures) >= l.maxEntries {
			for k := range l.failures {
				delete(l.failures, k)
				break
			}
		}
	}
	l.failures[key] = append(l.failures[key], now)
}

func Seconds(d time.Duration) int {
	return int(math.Ceil(d.Seconds()))
}
