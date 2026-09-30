package runner

import (
	"testing"
)

// TestRunInstancesOrderingPreserved is a unit-level sanity check on
// the worker pool's guarantee that summary.Instances is returned in
// the same order as opts.Instances, regardless of completion order.
//
// We can't easily unit-test runOneInstance without docker, so this
// test reaches into the package-private sort+indexedResult types by
// invoking the same sort step directly on a scrambled slice.
func TestRunInstancesOrderingPreserved(t *testing.T) {
	// Simulate results arriving out of order (the worker pool path
	// does this when concurrency > 1 and instances finish in different
	// wall-clock orders).
	results := []indexedResultForTest{
		{idx: 2, id: "c"},
		{idx: 0, id: "a"},
		{idx: 3, id: "d"},
		{idx: 1, id: "b"},
	}
	sortByIdx(results)
	want := []string{"a", "b", "c", "d"}
	for i, r := range results {
		if r.id != want[i] {
			t.Errorf("at %d: got %q want %q", i, r.id, want[i])
		}
	}
}

// TestConcurrencyDefaultsToOne — zero or negative concurrency maps to
// sequential (1 worker) behavior. Same net effect as the original
// Phase 1 runner.
func TestConcurrencyDefaultsToOne(t *testing.T) {
	cases := map[int]int{
		0:  1,
		-1: 1,
		1:  1,
		2:  2,
		5:  5,
	}
	for in, want := range cases {
		got := effectiveWorkers(in, 10)
		if got != want {
			t.Errorf("effectiveWorkers(%d, 10) = %d, want %d", in, got, want)
		}
	}
}

// TestConcurrencyClampsToInstanceCount — asking for more workers than
// instances should clamp to the instance count.
func TestConcurrencyClampsToInstanceCount(t *testing.T) {
	cases := []struct {
		conc, total, want int
	}{
		{10, 3, 3},
		{5, 5, 5},
		{2, 10, 2},
		{100, 1, 1},
	}
	for _, c := range cases {
		got := effectiveWorkers(c.conc, c.total)
		if got != c.want {
			t.Errorf("effectiveWorkers(%d, %d) = %d, want %d", c.conc, c.total, got, c.want)
		}
	}
}
