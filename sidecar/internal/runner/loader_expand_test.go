package runner

import (
	"os"
	"path/filepath"
	"testing"
)

func TestExpandPathHome(t *testing.T) {
	home, _ := os.UserHomeDir()
	cases := []struct {
		in, want string
	}{
		{"", ""},
		{"/absolute/path", "/absolute/path"},
		{"relative/path", "relative/path"},
		{"~", home},
		{"~/foo/bar", filepath.Join(home, "foo/bar")},
		{"~/.vett/datasets/x.jsonl", filepath.Join(home, ".vett/datasets/x.jsonl")},
	}
	for _, c := range cases {
		got, err := expandPath(c.in)
		if err != nil {
			t.Errorf("expandPath(%q) error: %v", c.in, err)
			continue
		}
		// filepath.Join on Windows yields backslashes; compare via
		// filepath.ToSlash for portability.
		if filepath.ToSlash(got) != filepath.ToSlash(c.want) {
			t.Errorf("expandPath(%q) = %q, want %q", c.in, got, c.want)
		}
	}
}

func TestExpandPathLeavesTildePrefixesAlone(t *testing.T) {
	// "~user" style (not supported) should pass through unchanged.
	got, err := expandPath("~someone/foo")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "~someone/foo" {
		t.Errorf("expected ~someone/foo unchanged, got %q", got)
	}
}

