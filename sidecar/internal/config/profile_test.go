package config

import (
	"path/filepath"
	"runtime"
	"testing"
)

func repoRoot(t *testing.T) string {
	t.Helper()
	_, f, _, _ := runtime.Caller(0)
	return filepath.Clean(filepath.Join(filepath.Dir(f), "..", ".."))
}

func TestLoadOpenHandsProfile(t *testing.T) {
	path := filepath.Join(repoRoot(t), "profiles", "openhands.yaml")
	p, err := LoadProfile(path)
	if err != nil {
		t.Fatalf("load openhands: %v", err)
	}
	if p.Name != "openhands" {
		t.Errorf("expected name openhands, got %q", p.Name)
	}
	if p.LLM.Temperature != 1.0 {
		t.Errorf("expected temperature 1.0, got %v", p.LLM.Temperature)
	}
	if p.LLM.TopP != 0.95 {
		t.Errorf("expected top_p 0.95, got %v", p.LLM.TopP)
	}
	if p.MaxIterations != 100 {
		t.Errorf("expected max_iterations 100, got %d", p.MaxIterations)
	}
	if !p.Sandbox.RunAsRoot {
		t.Error("expected run_as_root=true")
	}
	if p.Sandbox.DefaultCwd != "/testbed" {
		t.Errorf("expected default_cwd /testbed, got %q", p.Sandbox.DefaultCwd)
	}
	wantTools := []string{"terminal", "file_editor", "task_tracker", "finish", "think"}
	if len(p.Tools) != len(wantTools) {
		t.Fatalf("expected %d tools, got %d", len(wantTools), len(p.Tools))
	}
	for i, want := range wantTools {
		if p.Tools[i].Name != want {
			t.Errorf("tool[%d]: expected %q, got %q", i, want, p.Tools[i].Name)
		}
	}
	wantMW := []string{"output_truncation", "submit_detector", "stuck_detector"}
	if len(p.Middleware) != len(wantMW) {
		t.Fatalf("expected %d middlewares, got %d", len(wantMW), len(p.Middleware))
	}
	for i, want := range wantMW {
		if p.Middleware[i].Name != want {
			t.Errorf("middleware[%d]: expected %q, got %q", i, want, p.Middleware[i].Name)
		}
	}
	if len(p.SystemPrompt) < 10000 {
		t.Errorf("expected system_prompt_file to be loaded (got %d chars)", len(p.SystemPrompt))
	}
}
