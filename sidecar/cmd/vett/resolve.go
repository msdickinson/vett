package main

import (
	"fmt"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"strings"

	vettassets "github.com/msdickinson/vett/sidecar"
	"github.com/msdickinson/vett/sidecar/internal/config"
)

// profileSource locates and parses a profile by name or path, searching:
//  1. Explicit path (if nameOrPath looks like a path and exists)
//  2. ./profiles/<name>.yaml in the current workspace
//  3. Embedded core assets
//
// Returns the parsed profile, a human-readable source label for logging,
// and an error.
func profileSource(nameOrPath string) (*config.Profile, string, error) {
	p, src, _, err := profileSourceBytes(nameOrPath)
	return p, src, err
}

// profileSourceBytes is the same as profileSource but also returns the
// raw YAML bytes. Used by `vett run` to fingerprint the profile for
// run_start events and summary.json per docs/export-and-storage.md §13.
func profileSourceBytes(nameOrPath string) (*config.Profile, string, []byte, error) {
	// (1) Explicit path
	if _, err := os.Stat(nameOrPath); err == nil {
		raw, _ := os.ReadFile(nameOrPath)
		p, err := config.LoadProfile(nameOrPath)
		if err != nil {
			return nil, nameOrPath, raw, err
		}
		return p, nameOrPath, raw, nil
	}

	// (2) Current workspace
	wd, _ := os.Getwd()
	workspacePath := filepath.Join(wd, "profiles", nameOrPath+".yaml")
	if _, err := os.Stat(workspacePath); err == nil {
		raw, _ := os.ReadFile(workspacePath)
		p, err := config.LoadProfile(workspacePath)
		if err != nil {
			return nil, workspacePath, raw, err
		}
		return p, workspacePath, raw, nil
	}

	// (3) Embedded core
	embedPath := "profiles/" + nameOrPath + ".yaml"
	raw, err := fs.ReadFile(vettassets.CoreFS(), embedPath)
	if err == nil {
		p, err := config.ParseProfile(raw, fsSiblingReader(vettassets.CoreFS(), embedPath))
		if err != nil {
			return nil, "embed:" + embedPath, raw, err
		}
		return p, "embed:" + embedPath, raw, nil
	}

	return nil, "", nil, fmt.Errorf("profile %q not found in workspace %s/profiles/ or in built-in core", nameOrPath, wd)
}

// suiteSourceBytes extends suiteSource with the raw YAML bytes for
// fingerprinting.
func suiteSourceBytes(nameOrPath string) (*config.Suite, string, string, []byte, error) {
	s, src, dir, err := suiteSource(nameOrPath)
	if err != nil || src == "" {
		return s, src, dir, nil, err
	}
	if strings.HasPrefix(src, "embed:") {
		raw, rerr := fs.ReadFile(vettassets.CoreFS(), strings.TrimPrefix(src, "embed:"))
		return s, src, dir, raw, rerr
	}
	raw, _ := os.ReadFile(src)
	return s, src, dir, raw, nil
}

// suiteSource does the same for suites. Returns the parsed Suite, the
// absolute path (for filesystem sources) or empty (for embedded), and
// an error. The returned "baseDir" is what jsonl loaders resolve
// relative paths against.
func suiteSource(nameOrPath string) (*config.Suite, string, string, error) {
	// (1) Explicit path
	if _, err := os.Stat(nameOrPath); err == nil {
		s, err := config.LoadSuite(nameOrPath)
		if err != nil {
			return nil, nameOrPath, filepath.Dir(nameOrPath), err
		}
		return s, nameOrPath, filepath.Dir(nameOrPath), nil
	}

	// (2) Current workspace
	wd, _ := os.Getwd()
	workspacePath := filepath.Join(wd, "suites", nameOrPath+".yaml")
	if _, err := os.Stat(workspacePath); err == nil {
		s, err := config.LoadSuite(workspacePath)
		if err != nil {
			return nil, workspacePath, filepath.Dir(workspacePath), err
		}
		return s, workspacePath, filepath.Dir(workspacePath), nil
	}

	// (3) Embedded core. Note: jsonl suites embedded in core (like
	// test-canned) reference instance files relative to the suite YAML.
	// Those instance files are NOT embedded — test-canned is intended
	// for repo-internal unit tests, not for running via `vett run` from
	// an installed binary. If we decide to make embedded suites runnable
	// standalone later, we'd embed their fixtures here too.
	embedPath := "suites/" + nameOrPath + ".yaml"
	raw, err := fs.ReadFile(vettassets.CoreFS(), embedPath)
	if err == nil {
		s, err := config.ParseSuite(raw)
		if err != nil {
			return nil, "embed:" + embedPath, "", err
		}
		return s, "embed:" + embedPath, "", nil
	}

	return nil, "", "", fmt.Errorf("suite %q not found in workspace %s/suites/ or in built-in core", nameOrPath, wd)
}

// fsSiblingReader builds a sibling-file reader rooted at the directory
// of sourcePath inside fsys. Resolves "../testdata/foo.txt" type paths
// via path.Clean, then reads from the fs.FS.
func fsSiblingReader(fsys fs.FS, sourcePath string) config.ReadSibling {
	return func(rel string) ([]byte, error) {
		dir := path.Dir(sourcePath)
		joined := path.Clean(path.Join(dir, rel))
		if strings.HasPrefix(joined, "../") || joined == ".." {
			return nil, fmt.Errorf("sibling path %q escapes embed root", rel)
		}
		return fs.ReadFile(fsys, joined)
	}
}

// listAllProfiles returns profile names from both the current workspace
// (./profiles/*.yaml) and the embedded core, de-duplicated with workspace
// taking precedence.
func listAllProfiles() []string {
	seen := map[string]bool{}
	var out []string
	// Workspace first (so "source: workspace" wins on name collision).
	if wd, err := os.Getwd(); err == nil {
		matches, _ := filepath.Glob(filepath.Join(wd, "profiles", "*.yaml"))
		for _, m := range matches {
			name := trimExt(filepath.Base(m))
			if !seen[name] {
				seen[name] = true
				out = append(out, name+"  (workspace)")
			}
		}
	}
	// Embedded core.
	for _, name := range vettassets.ListProfiles() {
		if !seen[name] {
			seen[name] = true
			out = append(out, name+"  (built-in)")
		}
	}
	return out
}

func listAllSuites() []string {
	seen := map[string]bool{}
	var out []string
	if wd, err := os.Getwd(); err == nil {
		matches, _ := filepath.Glob(filepath.Join(wd, "suites", "*.yaml"))
		for _, m := range matches {
			name := trimExt(filepath.Base(m))
			if !seen[name] {
				seen[name] = true
				out = append(out, name+"  (workspace)")
			}
		}
	}
	for _, name := range vettassets.ListSuites() {
		if !seen[name] {
			seen[name] = true
			out = append(out, name+"  (built-in)")
		}
	}
	return out
}

func trimExt(name string) string {
	return strings.TrimSuffix(name, filepath.Ext(name))
}
