package retention

import (
	"reflect"
	"testing"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

func TestPlan(t *testing.T) {
	now := time.Date(2026, 9, 21, 10, 0, 0, 0, time.UTC)
	day := 24 * time.Hour
	items := []store.ItemSummary{
		{ID: "a", Seq: 1, CreatedAt: now.Add(-10 * day), StoredBytes: 10},
		{ID: "b", Seq: 2, CreatedAt: now.Add(-9 * day), Pinned: true, StoredBytes: 10},
		{ID: "c", Seq: 3, CreatedAt: now.Add(-1 * day), StoredBytes: 50},
		{ID: "d", Seq: 4, CreatedAt: now.Add(-1 * time.Hour), StoredBytes: 50},
		{ID: "e", Seq: 5, CreatedAt: now, StoredBytes: 50},
	}
	got := Plan(items, now, Policy{MaxAge: 7 * day, StorageMaxBytes: 1000}, "")
	if !reflect.DeepEqual(got, []string{"a"}) {
		t.Fatalf("age plan = %v", got)
	}
	got = Plan(items, now, Policy{MaxAge: 7 * day, StorageMaxBytes: 60}, "e")
	if !reflect.DeepEqual(got, []string{"a", "c", "d"}) {
		t.Fatalf("size plan = %v", got)
	}
	got = Plan(items, now, Policy{MaxAge: 7 * day, StorageMaxBytes: 0}, "e")
	if !reflect.DeepEqual(got, []string{"a", "c", "d"}) {
		t.Fatalf("keep plan = %v", got)
	}
}
