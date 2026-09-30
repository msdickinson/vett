package plugin

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sync"
)

// Manager owns all external plugin processes. It scans the workspace,
// builds what needs building, spawns processes, and keeps them warm.
type Manager struct {
	workspaceDir string
	cacheDir     string
	plugins      map[string]*LoadedPlugin
	mu           sync.Mutex
}

// LoadedPlugin is a running plugin process with its metadata.
type LoadedPlugin struct {
	Meta    PluginMeta
	Process *Process
	Binary  string // path to built binary or source script
}

// NewManager creates a plugin manager for the given workspace.
func NewManager(workspaceDir string) *Manager {
	return &Manager{
		workspaceDir: workspaceDir,
		cacheDir:     filepath.Join(workspaceDir, ".vett", "cache"),
		plugins:      make(map[string]*LoadedPlugin),
	}
}

// BuildResult is the outcome of building one plugin.
type BuildResult struct {
	Name     string
	Language Language
	Kind     PluginKind
	Built    bool
	Cached   bool
	Skipped  string // reason if skipped (e.g. "SDK not found")
	Error    error
	Duration float64 // seconds
}

// Build scans the workspace and builds all plugins. Returns results
// for each plugin found.
func (m *Manager) Build(ctx context.Context) []BuildResult {
	scan := ScanDir(m.workspaceDir)
	var results []BuildResult

	_ = os.MkdirAll(m.cacheDir, 0o755)
	hashDir := filepath.Join(m.cacheDir, "hashes")
	_ = os.MkdirAll(hashDir, 0o755)

	for _, meta := range scan.Plugins {
		r := BuildResult{
			Name:     meta.Name,
			Language: meta.Language,
			Kind:     meta.Kind,
		}

		// Check SDK.
		if err := CheckSDK(meta.Language); err != nil {
			r.Skipped = err.Error()
			results = append(results, r)
			continue
		}

		info := GetLangInfo(meta.Language)
		if info == nil || !info.NeedsBuild {
			// Interpreted language — syntax check only for Python.
			if meta.Language == LangPython {
				if err := pythonSyntaxCheck(ctx, meta.SourcePath); err != nil {
					r.Error = err
				}
			}
			r.Built = true
			results = append(results, r)
			continue
		}

		// Check cache.
		hash, _ := HashFile(meta.SourcePath)
		hashFile := filepath.Join(hashDir, meta.Name+".sha256")
		if cached, _ := os.ReadFile(hashFile); string(cached) == hash && hash != "" {
			r.Built = true
			r.Cached = true
			results = append(results, r)
			continue
		}

		// Build.
		command, args, _, err := BuildCommand(meta.Language, meta.SourcePath, m.cacheDir)
		if err != nil {
			r.Error = err
			results = append(results, r)
			continue
		}

		cmd := exec.CommandContext(ctx, command, args...)
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			r.Error = fmt.Errorf("build %s: %w", meta.Name, err)
			results = append(results, r)
			continue
		}

		// Update cache hash.
		_ = os.WriteFile(hashFile, []byte(hash), 0o644)

		r.Built = true
		results = append(results, r)
	}

	return results
}

// WarmUp builds all plugins and spawns their processes. Call this at
// startup so the first tool call has zero latency.
func (m *Manager) WarmUp(ctx context.Context) ([]BuildResult, error) {
	results := m.Build(ctx)

	scan := ScanDir(m.workspaceDir)
	for _, meta := range scan.Plugins {
		// Skip if build failed.
		var buildOK bool
		for _, r := range results {
			if r.Name == meta.Name && r.Built {
				buildOK = true
				break
			}
		}
		if !buildOK {
			continue
		}

		// Determine the binary/script path.
		info := GetLangInfo(meta.Language)
		var binaryPath string
		if info != nil && info.NeedsBuild {
			_, _, binaryPath, _ = BuildCommand(meta.Language, meta.SourcePath, m.cacheDir)
		} else {
			binaryPath = meta.SourcePath
		}

		// Spawn the process.
		command, cmdArgs := RunCommand(meta.Language, binaryPath)
		proc, err := StartProcess(ctx, meta.Name, command, cmdArgs...)
		if err != nil {
			// Record as error in results but don't fail startup.
			for i := range results {
				if results[i].Name == meta.Name {
					results[i].Error = fmt.Errorf("spawn: %w", err)
					results[i].Built = false
				}
			}
			continue
		}

		m.mu.Lock()
		m.plugins[meta.Name] = &LoadedPlugin{
			Meta:    meta,
			Process: proc,
			Binary:  binaryPath,
		}
		m.mu.Unlock()
	}

	return results, nil
}

// GetTool returns a running tool plugin by name.
func (m *Manager) GetTool(name string) *LoadedPlugin {
	m.mu.Lock()
	defer m.mu.Unlock()
	p := m.plugins[name]
	if p != nil && p.Meta.Kind == KindTool {
		return p
	}
	return nil
}

// GetMiddleware returns a running middleware plugin by name.
func (m *Manager) GetMiddleware(name string) *LoadedPlugin {
	m.mu.Lock()
	defer m.mu.Unlock()
	p := m.plugins[name]
	if p != nil && p.Meta.Kind == KindMiddleware {
		return p
	}
	return nil
}

// ToolNames returns the names of all loaded tool plugins.
func (m *Manager) ToolNames() []string {
	m.mu.Lock()
	defer m.mu.Unlock()
	var names []string
	for name, p := range m.plugins {
		if p.Meta.Kind == KindTool {
			names = append(names, name)
		}
	}
	return names
}

// GenerateSchema builds an OpenAI function schema from plugin metadata.
func (m *Manager) GenerateSchema(meta PluginMeta) json.RawMessage {
	params := map[string]any{
		"type":       "object",
		"properties": map[string]any{},
		"required":   []string{},
	}

	props := params["properties"].(map[string]any)
	required := params["required"].([]string)

	for _, p := range meta.Params {
		props[p.Name] = map[string]any{
			"type":        p.Type,
			"description": p.Description,
		}
		required = append(required, p.Name)
	}
	params["required"] = required

	schema := map[string]any{
		"type": "function",
		"function": map[string]any{
			"name":        meta.Name,
			"description": meta.Description,
			"parameters":  params,
		},
	}

	data, _ := json.Marshal(schema)
	return data
}

// Close shuts down all plugin processes.
func (m *Manager) Close() {
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, p := range m.plugins {
		_ = p.Process.Close()
	}
	m.plugins = make(map[string]*LoadedPlugin)
}

func pythonSyntaxCheck(ctx context.Context, path string) error {
	py := "python3"
	if isWindows() {
		py = "python"
	}
	cmd := exec.CommandContext(ctx, py, "-m", "py_compile", path)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("syntax error: %s", string(out))
	}
	return nil
}

func isWindows() bool {
	return os.PathSeparator == '\\'
}
