package runner

import (
	"encoding/json"
	"strings"
	"testing"
)

// Unified diff samples are regular Git output. Kept here as literals so
// the parser test is self-contained (no testdata fixture file needed).
const sampleSingleFilePatch = `diff --git a/astropy/modeling/separable.py b/astropy/modeling/separable.py
index abc123..def456 100644
--- a/astropy/modeling/separable.py
+++ b/astropy/modeling/separable.py
@@ -240,7 +240,7 @@ def _coord_matrix(model, pos, noutp):
-    mat = np.zeros((noutp, model.n_inputs))
+    mat = np.zeros((noutp, model.n_inputs), dtype=np.int64)
`

const sampleMultiFilePatch = `diff --git a/a/foo.py b/a/foo.py
index 111..222 100644
--- a/a/foo.py
+++ b/a/foo.py
@@ -1 +1 @@
-x
+y
diff --git a/b/bar/baz.py b/b/bar/baz.py
new file mode 100644
index 000..333
--- /dev/null
+++ b/b/bar/baz.py
@@ -0,0 +1 @@
+hello
`

func TestParseFilesTouchedSingleFile(t *testing.T) {
	got := parseFilesTouched(sampleSingleFilePatch)
	want := []string{"astropy/modeling/separable.py"}
	if !equalSlices(got, want) {
		t.Errorf("got %v want %v", got, want)
	}
}

func TestParseFilesTouchedMultipleFiles(t *testing.T) {
	got := parseFilesTouched(sampleMultiFilePatch)
	want := []string{"a/foo.py", "b/bar/baz.py"}
	if !equalSlices(got, want) {
		t.Errorf("got %v want %v", got, want)
	}
}

func TestParseFilesTouchedEmpty(t *testing.T) {
	got := parseFilesTouched("")
	if len(got) != 0 {
		t.Errorf("expected empty slice, got %v", got)
	}
}

func TestParseFilesTouchedIgnoresNonDiffLines(t *testing.T) {
	got := parseFilesTouched("some random output\nnot a diff\n")
	if len(got) != 0 {
		t.Errorf("expected empty slice, got %v", got)
	}
}

// TestParseFilesTouchedFiltersNoise verifies that __pycache__ and
// friends are stripped from the human-readable file list (they'd
// otherwise pollute files_touched when pytest or similar runs inside
// the container with `git add -A`).
func TestParseFilesTouchedFiltersNoise(t *testing.T) {
	diff := `diff --git a/mathlib.py b/mathlib.py
index abc..def 100644
--- a/mathlib.py
+++ b/mathlib.py
@@ -1 +1 @@
-x
+y
diff --git a/__pycache__/mathlib.cpython-311.pyc b/__pycache__/mathlib.cpython-311.pyc
new file mode 100644
diff --git a/.pytest_cache/CACHEDIR.TAG b/.pytest_cache/CACHEDIR.TAG
new file mode 100644
diff --git a/nested/__pycache__/foo.cpython-311.pyc b/nested/__pycache__/foo.cpython-311.pyc
new file mode 100644
`
	got := parseFilesTouched(diff)
	want := []string{"mathlib.py"}
	if !equalSlices(got, want) {
		t.Errorf("got %v want %v", got, want)
	}
}

func TestIsNoisePath(t *testing.T) {
	cases := map[string]bool{
		"__pycache__/mathlib.cpython-311.pyc":        true,
		"nested/__pycache__/foo.pyc":                 true,
		".pytest_cache/CACHEDIR.TAG":                 true,
		".mypy_cache/3.11/mathlib.meta.json":         true,
		".ruff_cache/content-hash":                   true,
		"node_modules/foo/bar.js":                    true,
		"mathlib.py":                                 false,
		"src/mathlib.py":                             false,
		"tests/test_add.py":                          false,
		"build/lib/mathlib.py":                       false,
		"cache.txt":                                  false,
	}
	for path, want := range cases {
		if got := isNoisePath(path); got != want {
			t.Errorf("isNoisePath(%q) = %v want %v", path, got, want)
		}
	}
}

// TestSha256HexIsStableHex verifies the hex encoder output shape —
// 64-char lowercase.
func TestSha256HexIsStableHex(t *testing.T) {
	h := sha256Hex([]byte("hello world"))
	if len(h) != 64 {
		t.Errorf("expected 64-char hex, got %d: %q", len(h), h)
	}
	for _, c := range h {
		if !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) {
			t.Errorf("non-hex char in output: %q", h)
		}
	}
	if sha256Hex(nil) != "" {
		t.Errorf("empty input should yield empty sha, got %q", sha256Hex(nil))
	}
}

// TestSummaryJSONIncludesMandatoryFields — docs/export-and-storage.md
// §13 rule 7: summary.json must include enough to derive swebench/csv/
// json/markdown exports. This test locks the field presence.
func TestSummaryJSONIncludesMandatoryFields(t *testing.T) {
	resolved := true
	s := Summary{
		RunID:         "run-test",
		VettVersion:   "0.1.0-dev",
		SidecarSHA256: "abc",
		Suite:         "x",
		SuiteSHA256:   "def",
		Profile:       "openhands",
		ProfileSHA256: "ghi",
		Model:         "qwen3-coder-next",
		Endpoint:      "http://example.local:8000/v1",
		InstanceCount: 1,
		Completed:     1,
		Instances: []InstanceResult{{
			InstanceID:   "astropy__astropy-12907",
			Resolved:     &resolved,
			PatchChars:   1234,
			PatchSHA256:  "beef",
			PatchPath:    "instances/astropy__astropy-12907.patch",
			FilesTouched: []string{"astropy/foo.py"},
			Iterations:   5,
			InputTokens:  100,
			OutputTokens: 50,
			DurationSec:  12.3,
			EndReason:    "finish_tool",
		}},
	}
	b, err := json.Marshal(&s)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	raw := string(b)
	mandatory := []string{
		`"run_id"`, `"vett_version"`, `"sidecar_sha256"`,
		`"suite"`, `"suite_sha256"`, `"profile"`, `"profile_sha256"`,
		`"model"`, `"endpoint"`,
		`"instances"`, `"files_touched"`, `"patch_sha256"`, `"patch_path"`,
		`"iterations"`, `"input_tokens"`, `"output_tokens"`,
		`"duration_seconds"`, `"end_reason"`, `"resolved"`,
	}
	for _, field := range mandatory {
		if !strings.Contains(raw, field) {
			t.Errorf("summary.json missing mandatory field %s", field)
		}
	}
}

func equalSlices(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}
