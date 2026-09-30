// Package config loads and validates profile and suite YAML files.
package config

import (
	"fmt"
	"os"
	"path/filepath"

	"gopkg.in/yaml.v3"
)

// Profile is the parsed Vett profile YAML. See docs/profile-schema.md.
type Profile struct {
	Name                   string          `yaml:"name"`
	Description            string          `yaml:"description,omitempty"`
	SelfReportedConfidence bool            `yaml:"self_reported_confidence,omitempty"`
	Sandbox                SandboxBlock    `yaml:"sandbox,omitempty"`
	LLM                    LLMBlock        `yaml:"llm"`
	SystemPrompt           string          `yaml:"system_prompt,omitempty"`
	SystemPromptFile       string          `yaml:"system_prompt_file,omitempty"` // relative to profile YAML
	UserTemplate           string          `yaml:"user_template,omitempty"`
	UserTemplateFile       string          `yaml:"user_template_file,omitempty"` // relative to profile YAML
	Tools                  []ToolRef       `yaml:"tools"`
	Middleware             []MiddlewareRef `yaml:"middleware,omitempty"`
	MaxIterations          int             `yaml:"max_iterations"`
	TimeoutMinutes         int             `yaml:"timeout_minutes,omitempty"`
	// Team is optional and unused in Phase 1 (openhands is single-agent).
	Team *TeamBlock `yaml:"team,omitempty"`
}

type SandboxBlock struct {
	// Type selects the sandbox backend. Values: "" or "docker" (default)
	// uses Docker containers; "local" runs the sidecar natively on the
	// host (no Docker). Used by `vett chat` for local coding sessions.
	Type string `yaml:"type,omitempty"`

	RunAsRoot   bool   `yaml:"run_as_root,omitempty"`
	Home        string `yaml:"home,omitempty"`
	ImagePrefix string `yaml:"image_prefix,omitempty"`
	DefaultCwd  string `yaml:"default_cwd,omitempty"`

	// WorkdirBacking lets the runner put the agent's /testbed on
	// fast host-side storage (SSD) instead of the container's
	// writable layer (usually on HDD via Docker's data-root). The
	// runner creates the host dir, docker-cp's the baked workdir
	// contents out of the image, and bind-mounts them back in.
	// Leave empty to use the image's native writable layer.
	WorkdirBacking WorkdirBackingBlock `yaml:"workdir_backing,omitempty"`

	// TmpfsMounts maps to --tmpfs flags on docker run. Typical
	// use: /tmp:size=2g and /root/.cache:size=1g to keep pytest and
	// pip caches in RAM instead of the container's writable layer.
	TmpfsMounts []string `yaml:"tmpfs_mounts,omitempty"`
}

// WorkdirBackingBlock describes where to back the agent's working
// directory. Phase 2 supports one type: host_path.
type WorkdirBackingBlock struct {
	// Type is one of: "" (default — no override, use image),
	// "host_path" (create a host dir per run/instance and bind-mount).
	Type string `yaml:"type,omitempty"`

	// HostBase is a template path resolved per run/instance. Supports
	// the tokens {run_id} and {instance_id}. Example:
	//   /mnt/ssd/vett/runs/{run_id}/instances/{instance_id}/testbed
	// Tilde (~) at the start expands to $HOME.
	HostBase string `yaml:"host_base,omitempty"`

	// ContainerPath is the path inside the container to bind-mount
	// onto. Defaults to default_cwd (e.g. /testbed).
	ContainerPath string `yaml:"container_path,omitempty"`
}

type LLMBlock struct {
	Model                 string  `yaml:"model,omitempty"`
	Endpoint              string  `yaml:"endpoint,omitempty"`
	Temperature           float64 `yaml:"temperature"`
	TopP                  float64 `yaml:"top_p"`
	MaxTokens             *int    `yaml:"max_tokens,omitempty"`
	Seed                  *int    `yaml:"seed,omitempty"`
	ParallelToolCalls     bool    `yaml:"parallel_tool_calls,omitempty"`
	RequestTimeoutSeconds int     `yaml:"request_timeout_seconds,omitempty"`
	NumRetries            int     `yaml:"num_retries,omitempty"`
}

// ToolRef is either a bare string ("terminal") or an explicit object
// ({key: "openhands_terminal", name: "terminal", ...}). YAML supports
// both via a custom unmarshaller.
type ToolRef struct {
	Key                string              `yaml:"key"`
	Name               string              `yaml:"name,omitempty"`
	Aliases            []string            `yaml:"aliases,omitempty"`
	Config             map[string]any      `yaml:"config,omitempty"`
	// ParameterOverrides lets a profile tighten a tool's schema per-step.
	// Example: mark a parameter as required without forking the tool.
	ParameterOverrides map[string]ParamOverride `yaml:"parameter_overrides,omitempty"`
}

// ParamOverride modifies a single parameter's schema for this profile.
type ParamOverride struct {
	Required    *bool  `yaml:"required,omitempty"`
	Description string `yaml:"description,omitempty"`
	Default     string `yaml:"default,omitempty"`
}

func (t *ToolRef) UnmarshalYAML(node *yaml.Node) error {
	if node.Kind == yaml.ScalarNode {
		t.Key = node.Value
		t.Name = node.Value
		return nil
	}
	type alias ToolRef
	var a alias
	if err := node.Decode(&a); err != nil {
		return err
	}
	*t = ToolRef(a)
	if t.Name == "" {
		t.Name = t.Key
	}
	return nil
}

// MiddlewareRef is either a bare string or an object with config.
type MiddlewareRef struct {
	Name   string         `yaml:"name"`
	Config map[string]any `yaml:"config,omitempty"`
}

func (m *MiddlewareRef) UnmarshalYAML(node *yaml.Node) error {
	if node.Kind == yaml.ScalarNode {
		m.Name = node.Value
		return nil
	}
	// Could be {name: X, config: {...}} or {X: {...}} shorthand.
	type alias MiddlewareRef
	var a alias
	if err := node.Decode(&a); err == nil && a.Name != "" {
		*m = MiddlewareRef(a)
		return nil
	}
	var short map[string]map[string]any
	if err := node.Decode(&short); err == nil && len(short) == 1 {
		for k, v := range short {
			m.Name = k
			m.Config = v
		}
		return nil
	}
	return fmt.Errorf("middleware ref must be a string, {name, config}, or {name: {config}}")
}

// TeamBlock is the multi-agent team config. The leader delegates tasks
// to members via built-in tools (assign_task, assign_async, check_task,
// wait_task, declare_done).
type TeamBlock struct {
	Leader  MemberConfig   `yaml:"leader"`
	Members []MemberConfig `yaml:"members"`
}

// MemberConfig defines a team member (or leader). Each member runs its
// own agent loop with its own system prompt, tools, and LLM settings.
type MemberConfig struct {
	Name         string          `yaml:"name"`
	SystemPrompt string          `yaml:"system_prompt,omitempty"`
	Tools        []ToolRef       `yaml:"tools,omitempty"`
	Middleware   []MiddlewareRef `yaml:"middleware,omitempty"`
	LLM          *LLMBlock       `yaml:"llm,omitempty"`
	MaxIterations int            `yaml:"max_iterations,omitempty"`
	TimeoutMinutes int           `yaml:"timeout_minutes,omitempty"`
	// ReceivesFrom is for sequential mode (no leader). This member
	// runs after the named members and receives their output as context.
	ReceivesFrom []string `yaml:"receives_from,omitempty"`
}

// ReadSibling resolves a sibling file's path (given as "../testdata/foo.txt"
// or similar, relative to the profile YAML's directory) and returns its
// bytes. Separates the profile parser from the storage layer so the same
// code handles both on-disk and embedded (fs.FS) profiles.
type ReadSibling func(rel string) ([]byte, error)

// LoadProfile reads a profile YAML from disk and validates it.
func LoadProfile(path string) (*Profile, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read profile %s: %w", path, err)
	}
	profileDir := filepath.Dir(path)
	read := func(rel string) ([]byte, error) {
		return os.ReadFile(filepath.Join(profileDir, rel))
	}
	p, err := ParseProfile(raw, read)
	if err != nil {
		return nil, fmt.Errorf("profile %s: %w", path, err)
	}
	return p, nil
}

// ParseProfile parses a profile YAML from raw bytes and resolves
// sibling-file fields (system_prompt_file, user_template_file) via
// the provided reader. Storage-agnostic.
func ParseProfile(raw []byte, read ReadSibling) (*Profile, error) {
	var p Profile
	if err := yaml.Unmarshal(raw, &p); err != nil {
		return nil, fmt.Errorf("parse yaml: %w", err)
	}
	if p.SystemPromptFile != "" && p.SystemPrompt == "" {
		sp, err := read(p.SystemPromptFile)
		if err != nil {
			return nil, fmt.Errorf("read system_prompt_file %q: %w", p.SystemPromptFile, err)
		}
		p.SystemPrompt = string(sp)
	}
	if p.UserTemplateFile != "" && p.UserTemplate == "" {
		ut, err := read(p.UserTemplateFile)
		if err != nil {
			return nil, fmt.Errorf("read user_template_file %q: %w", p.UserTemplateFile, err)
		}
		p.UserTemplate = string(ut)
	}
	if err := p.Validate(); err != nil {
		return nil, fmt.Errorf("validate: %w", err)
	}
	return &p, nil
}

// Validate applies the rules in docs/profile-schema.md §validation.
func (p *Profile) Validate() error {
	if p.Name == "" {
		return fmt.Errorf("name is required")
	}
	if p.SystemPrompt == "" {
		return fmt.Errorf("system_prompt is required")
	}
	if len(p.Tools) == 0 {
		return fmt.Errorf("tools list is required")
	}
	if p.MaxIterations <= 0 {
		return fmt.Errorf("max_iterations must be > 0")
	}
	if p.LLM.Temperature < 0 || p.LLM.Temperature > 2 {
		return fmt.Errorf("llm.temperature must be in [0, 2]")
	}
	if p.LLM.TopP <= 0 || p.LLM.TopP > 1 {
		return fmt.Errorf("llm.top_p must be in (0, 1]")
	}
	// Uniqueness: no two tools share a wire name.
	seen := map[string]bool{}
	for _, t := range p.Tools {
		if seen[t.Name] {
			return fmt.Errorf("duplicate tool wire name %q", t.Name)
		}
		seen[t.Name] = true
	}
	return nil
}
