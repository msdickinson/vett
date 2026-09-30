package runner

import (
	"path/filepath"
	"runtime"
	"testing"

	"github.com/msdickinson/vett/sidecar/internal/config"
)

func repoRoot(t *testing.T) string {
	t.Helper()
	_, f, _, _ := runtime.Caller(0)
	return filepath.Clean(filepath.Join(filepath.Dir(f), "..", ".."))
}

func TestLoadInstancesJSONL(t *testing.T) {
	root := repoRoot(t)
	suitePath := filepath.Join(root, "suites", "test-canned.yaml")
	suite, err := config.LoadSuite(suitePath)
	if err != nil {
		t.Fatalf("load suite: %v", err)
	}
	instances, err := LoadInstances(suite, filepath.Dir(suitePath))
	if err != nil {
		t.Fatalf("load instances: %v", err)
	}
	if len(instances) != 2 {
		t.Fatalf("expected 2 instances, got %d", len(instances))
	}
	if instances[0].ID != "test__canned-001" {
		t.Errorf("expected test__canned-001, got %q", instances[0].ID)
	}
	if !contains(instances[0].ProblemStatement, "function foo") {
		t.Errorf("problem statement not parsed: %q", instances[0].ProblemStatement)
	}
}

func TestLoadInstancesHuggingFaceNotImplemented(t *testing.T) {
	suite := &config.Suite{
		Name:   "x",
		Loader: config.LoaderBlock{Type: "huggingface"},
	}
	_, err := LoadInstances(suite, ".")
	if err == nil {
		t.Error("expected error for huggingface loader")
	}
}

func TestRenderImageTemplate(t *testing.T) {
	s := &config.Suite{Sandbox: config.SuiteSandbox{ImageTemplate: "swebench/sweb.eval.x86_64.{repo}_1776_{instance}"}}
	inst := Instance{ID: "astropy__astropy-12907", Repo: "astropy"}
	got := renderImageTemplate(s, inst)
	want := "swebench/sweb.eval.x86_64.astropy_1776_astropy__astropy-12907"
	if got != want {
		t.Errorf("got %q want %q", got, want)
	}
}

func TestRenderUserTemplate(t *testing.T) {
	tmpl := "working_dir={working_dir}\nbase_commit={base_commit}\nproblem={problem_statement}"
	out := renderUserTemplate(tmpl, map[string]string{
		"working_dir":       "/testbed",
		"base_commit":       "abc",
		"problem_statement": "fix it",
	})
	if !contains(out, "working_dir=/testbed") || !contains(out, "problem=fix it") {
		t.Errorf("template render failed:\n%s", out)
	}
}

func contains(haystack, needle string) bool {
	return len(haystack) >= len(needle) && findSubstring(haystack, needle)
}

func findSubstring(h, n string) bool {
	for i := 0; i+len(n) <= len(h); i++ {
		if h[i:i+len(n)] == n {
			return true
		}
	}
	return false
}
