package plugin

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"gopkg.in/yaml.v3"
)

// HookPhase is when a hook runs.
type HookPhase string

const (
	HookPreSuite      HookPhase = "pre-process/suite"
	HookPostSuite     HookPhase = "post-process/suite"
	HookPreInstance    HookPhase = "pre-process/instance"
	HookPostInstance   HookPhase = "post-process/instance"
)

// HookDef is one hook loaded from a YAML file.
type HookDef struct {
	Name        string `yaml:"name"`
	Description string `yaml:"description,omitempty"`
	Command     string `yaml:"command"`
	WorkDir     string `yaml:"workdir,omitempty"`
}

// HookFile is a YAML file containing a list of hooks.
type HookFile struct {
	Hooks []HookDef `yaml:"hooks"`
}

// LoadHooks loads all hook definitions from a directory.
// Looks for *.yaml files in the specified phase directory.
func LoadHooks(workspaceDir string, phase HookPhase) ([]HookDef, error) {
	dir := filepath.Join(workspaceDir, string(phase))
	if _, err := os.Stat(dir); err != nil {
		return nil, nil // no hooks directory — that's fine
	}

	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, fmt.Errorf("read hooks dir %s: %w", dir, err)
	}

	var hooks []HookDef
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		ext := strings.ToLower(filepath.Ext(entry.Name()))
		if ext != ".yaml" && ext != ".yml" {
			continue
		}

		data, err := os.ReadFile(filepath.Join(dir, entry.Name()))
		if err != nil {
			return nil, fmt.Errorf("read hook file %s: %w", entry.Name(), err)
		}

		var hf HookFile
		if err := yaml.Unmarshal(data, &hf); err != nil {
			return nil, fmt.Errorf("parse hook file %s: %w", entry.Name(), err)
		}

		hooks = append(hooks, hf.Hooks...)
	}

	return hooks, nil
}

// RunHooks executes a list of hooks sequentially. Each hook is a shell
// command. Environment variables are passed through env.
func RunHooks(ctx context.Context, hooks []HookDef, env map[string]string) error {
	for _, h := range hooks {
		cmd := exec.CommandContext(ctx, shellName(), shellFlag(), h.Command)
		if h.WorkDir != "" {
			cmd.Dir = h.WorkDir
		}
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr

		if len(env) > 0 {
			cmd.Env = os.Environ()
			for k, v := range env {
				cmd.Env = append(cmd.Env, k+"="+v)
			}
		}

		if err := cmd.Run(); err != nil {
			return fmt.Errorf("hook %q failed: %w", h.Name, err)
		}
	}
	return nil
}

func shellName() string {
	if isWindows() {
		return "cmd"
	}
	return "/bin/bash"
}

func shellFlag() string {
	if isWindows() {
		return "/c"
	}
	return "-c"
}
