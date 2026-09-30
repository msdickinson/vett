package plugin

import (
	"fmt"
	"os"
	"strings"

	"gopkg.in/yaml.v3"
)

// YAMLToolDef is a declarative tool defined entirely in YAML.
// No code needed — just a bash command template.
//
// Example:
//
//	name: pytest
//	description: Run pytest on the project
//	parameters:
//	  args:
//	    type: string
//	    description: Extra pytest arguments
//	command: "pytest {args}"
//	timeout_seconds: 120
type YAMLToolDef struct {
	Name           string                       `yaml:"name"`
	Description    string                       `yaml:"description"`
	Parameters     map[string]YAMLToolParam     `yaml:"parameters"`
	Command        string                       `yaml:"command"`
	TimeoutSeconds int                          `yaml:"timeout_seconds"`
}

// YAMLToolParam describes one parameter of a YAML tool.
type YAMLToolParam struct {
	Type        string `yaml:"type"`
	Description string `yaml:"description"`
	Default     string `yaml:"default"`
	Required    bool   `yaml:"required"`
}

// LoadYAMLTool loads a YAML tool definition from a file.
func LoadYAMLTool(path string) (*YAMLToolDef, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read yaml tool %s: %w", path, err)
	}
	var def YAMLToolDef
	if err := yaml.Unmarshal(data, &def); err != nil {
		return nil, fmt.Errorf("parse yaml tool %s: %w", path, err)
	}
	if def.Name == "" {
		def.Name = strings.TrimSuffix(fileBase(path), ".yaml")
		def.Name = strings.TrimSuffix(def.Name, ".yml")
	}
	if def.TimeoutSeconds <= 0 {
		def.TimeoutSeconds = 60
	}
	return &def, nil
}

// RenderCommand substitutes {param} placeholders with argument values.
func (d *YAMLToolDef) RenderCommand(args map[string]any) string {
	cmd := d.Command
	for name, param := range d.Parameters {
		placeholder := "{" + name + "}"
		val := ""
		if v, ok := args[name]; ok {
			val = fmt.Sprintf("%v", v)
		} else if param.Default != "" {
			val = param.Default
		}
		cmd = strings.ReplaceAll(cmd, placeholder, val)
	}
	return cmd
}

// ToPluginMeta converts a YAML tool definition to PluginMeta.
func (d *YAMLToolDef) ToPluginMeta(sourcePath string) PluginMeta {
	meta := PluginMeta{
		Name:        d.Name,
		Description: d.Description,
		Kind:        KindTool,
		Language:    LangYAML,
		SourcePath:  sourcePath,
	}
	for name, param := range d.Parameters {
		meta.Params = append(meta.Params, ParamMeta{
			Name:        name,
			Type:        param.Type,
			Description: param.Description,
		})
	}
	return meta
}

func fileBase(path string) string {
	// Returns filename without directory.
	for i := len(path) - 1; i >= 0; i-- {
		if path[i] == '/' || path[i] == '\\' {
			return path[i+1:]
		}
	}
	return path
}
