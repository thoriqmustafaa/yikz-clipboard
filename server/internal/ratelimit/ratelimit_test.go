package ratelimit

import (
	"testing"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/clock"
)

func TestSlidingWindow(t *testing.T) {
	c := clock.NewFake(time.Date(2026, 9, 21, 10, 0, 0, 0, time.UTC))
	l := New(10, 10*time.Minute, c)
	for i := 0; i < 9; i++ {
		l.Fail("1.2.3.4")
		c.Advance(time.Second)
	}
	if _, limited := l.RetryAfter("1.2.3.4"); limited {
		t.Fatal("limited after 9 failures")
	}
	l.Fail("1.2.3.4")
	wait, limited := l.RetryAfter("1.2.3.4")
	if !limited {
		t.Fatal("not limited after 10 failures")
	}
	if Seconds(wait) != 591 {
		t.Fatalf("retry after = %d, want 591", Seconds(wait))
	}
	if _, limited := l.RetryAfter("5.6.7.8"); limited {
		t.Fatal("other ip limited")
	}
	c.Advance(wait)
	if _, limited := l.RetryAfter("1.2.3.4"); limited {
		t.Fatal("still limited after window freed")
	}
}
