package runner

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/msdickinson/vett/sidecar/internal/config"
)

// expandPath replaces a leading "~/" with the user's home directory.
// Returns an error only if the home dir is unavailable and "~" is
// actually referenced. Plain absolute and relative paths pass through.
func expandPath(path string) (string, error) {
	if path == "" || !strings.HasPrefix(path, "~") {
		return path, nil
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return path, fmt.Errorf("cannot expand %q: %w", path, err)
	}
	if path == "~" {
		return home, nil
	}
	if strings.HasPrefix(path, "~/") {
		return filepath.Join(home, path[2:]), nil
	}
	return path, nil
}

// LoadInstances loads instances from a suite. Phase 1 supports the jsonl
// loader; HuggingFace and directory loaders return an informative error.
func LoadInstances(suite *config.Suite, suiteDir string) ([]Instance, error) {
	switch suite.Loader.Type {
	case "jsonl":
		path, err := expandPath(suite.Loader.Path)
		if err != nil {
			return nil, err
		}
		if !filepath.IsAbs(path) {
			path = filepath.Join(suiteDir, path)
		}
		return loadJSONL(path, suite)
	case "huggingface":
		return nil, fmt.Errorf("huggingface loader is not implemented in Phase 1 — use loader.type=jsonl with a local fixture")
	case "directory":
		return nil, fmt.Errorf("directory loader is not implemented in Phase 1 — use loader.type=jsonl with a local fixture")
	default:
		return nil, fmt.Errorf("unknown loader.type %q", suite.Loader.Type)
	}
}

func loadJSONL(path string, suite *config.Suite) ([]Instance, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("open %s: %w", path, err)
	}
	defer f.Close()
	var out []Instance
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 64*1024), 16*1024*1024)
	lineNum := 0
	for sc.Scan() {
		lineNum++
		line := sc.Bytes()
		if len(line) == 0 {
			continue
		}
		var raw map[string]any
		if err := json.Unmarshal(line, &raw); err != nil {
			return nil, fmt.Errorf("%s:%d: %w", path, lineNum, err)
		}
		inst := Instance{Raw: raw}
		inst.ID = asString(raw["instance_id"], asString(raw["id"], ""))
		inst.Repo = asString(raw["repo"], "")
		inst.BaseCommit = asString(raw[fieldName(suite, "base_commit")], asString(raw["base_commit"], ""))
		inst.ProblemStatement = asString(raw[fieldName(suite, "problem_statement")], asString(raw["problem_statement"], ""))
		out = append(out, inst)
	}
	if err := sc.Err(); err != nil {
		return nil, err
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("jsonl loader read zero instances from %s", path)
	}
	return out, nil
}

func fieldName(suite *config.Suite, logical string) string {
	if suite == nil || suite.Rendering.Fields == nil {
		return logical
	}
	if v, ok := suite.Rendering.Fields[logical]; ok {
		return v
	}
	return logical
}

func asString(v any, def string) string {
	if s, ok := v.(string); ok {
		return s
	}
	return def
}
