package config

import (
	"fmt"
	"os"

	"gopkg.in/yaml.v3"
)

// Suite is the parsed Vett suite YAML. See docs/suite-schema.md.
type Suite struct {
	Name           string         `yaml:"name"`
	Description    string         `yaml:"description,omitempty"`
	Loader         LoaderBlock    `yaml:"loader"`
	InstanceFilter InstanceFilter `yaml:"instance_filter,omitempty"`
	Rendering      RenderingBlock `yaml:"rendering"`
	Sandbox        SuiteSandbox   `yaml:"sandbox"`
	Scorer         ScorerBlock    `yaml:"scorer"`
}

type LoaderBlock struct {
	Type     string `yaml:"type"`
	Dataset  string `yaml:"dataset,omitempty"`
	Split    string `yaml:"split,omitempty"`
	CacheDir string `yaml:"cache_dir,omitempty"`
	Revision string `yaml:"revision,omitempty"`
	Path     string `yaml:"path,omitempty"`
}

type InstanceFilter struct {
	Repos      []string `yaml:"repos,omitempty"`
	ExcludeIDs []string `yaml:"exclude_ids,omitempty"`
	IncludeIDs []string `yaml:"include_ids,omitempty"`
}

type RenderingBlock struct {
	WorkingDir string            `yaml:"working_dir"`
	Fields     map[string]string `yaml:"fields"`
}

type SuiteSandbox struct {
	ImageTemplate string `yaml:"image_template"`
	Entrypoint    string `yaml:"entrypoint,omitempty"`
	DefaultCwd    string `yaml:"default_cwd,omitempty"`
}

type ScorerBlock struct {
	Type   string         `yaml:"type"`
	Config map[string]any `yaml:"config,omitempty"`
}

// LoadSuite reads a suite YAML from disk.
func LoadSuite(path string) (*Suite, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read suite %s: %w", path, err)
	}
	return ParseSuite(raw)
}

// ParseSuite parses raw suite YAML bytes. Storage-agnostic helper used by
// CLI code that needs to load suites from an embed.FS.
func ParseSuite(raw []byte) (*Suite, error) {
	var s Suite
	if err := yaml.Unmarshal(raw, &s); err != nil {
		return nil, fmt.Errorf("parse suite yaml: %w", err)
	}
	if err := s.Validate(); err != nil {
		return nil, fmt.Errorf("validate suite: %w", err)
	}
	return &s, nil
}

func (s *Suite) Validate() error {
	if s.Name == "" {
		return fmt.Errorf("name is required")
	}
	if s.Loader.Type == "" {
		return fmt.Errorf("loader.type is required")
	}
	if s.Sandbox.ImageTemplate == "" {
		return fmt.Errorf("sandbox.image_template is required")
	}
	if s.Scorer.Type == "" {
		return fmt.Errorf("scorer.type is required")
	}
	return nil
}
