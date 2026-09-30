package plugin

import (
	"bufio"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
)

// PluginKind is "tool" or "middleware".
type PluginKind string

const (
	KindTool       PluginKind = "tool"
	KindMiddleware PluginKind = "middleware"
)

// PluginMeta describes a discovered plugin file.
type PluginMeta struct {
	Name        string
	Description string
	Kind        PluginKind
	Language    Language
	SourcePath  string     // path to the source file or directory
	Params      []ParamMeta // extracted from comments (tools only)
}

// ParamMeta describes a tool parameter extracted from source comments.
type ParamMeta struct {
	Name        string
	Type        string // "string", "integer", "boolean"
	Description string
}

// ScanResult is the output of scanning a workspace directory.
type ScanResult struct {
	Plugins   []PluginMeta
	Errors    []string // non-fatal scan errors
}

// ScanDir scans a directory for plugin files. Looks for:
//   - tools/*.go, tools/*.py, tools/*.cs, tools/*.ts, tools/*.rs
//   - middleware/*.go, middleware/*.py, etc.
//   - Subdirectories with tool.yaml or a main file.
func ScanDir(workspaceDir string) ScanResult {
	var result ScanResult

	for _, kind := range []PluginKind{KindTool, KindMiddleware} {
		dir := filepath.Join(workspaceDir, string(kind)+"s")
		if _, err := statFile(dir); err != nil {
			continue
		}

		entries, err := os.ReadDir(dir)
		if err != nil {
			result.Errors = append(result.Errors, fmt.Sprintf("scan %s: %v", dir, err))
			continue
		}

		for _, entry := range entries {
			path := filepath.Join(dir, entry.Name())

			if entry.IsDir() {
				// Look for main file in subdirectory.
				meta := scanSubdir(path, kind)
				if meta != nil {
					result.Plugins = append(result.Plugins, *meta)
				}
				continue
			}

			lang := DetectLanguage(entry.Name())
			if lang == LangUnknown {
				continue
			}

			meta := extractMeta(path, kind, lang)
			result.Plugins = append(result.Plugins, meta)
		}
	}

	return result
}

// scanSubdir looks for a main entry point in a subdirectory.
func scanSubdir(dir string, kind PluginKind) *PluginMeta {
	// Check for known entry points.
	candidates := []struct {
		file string
		lang Language
	}{
		{"main.go", LangGo},
		{"main.py", LangPython},
		{"Program.cs", LangCSharp},
		{"index.ts", LangTypeScript},
		{"index.js", LangTypeScript},
		{"main.rs", LangRust},
	}

	for _, c := range candidates {
		path := filepath.Join(dir, c.file)
		if _, err := statFile(path); err == nil {
			meta := extractMeta(path, kind, c.lang)
			meta.SourcePath = dir // build the whole directory
			return &meta
		}
	}

	// Check for .csproj (C# project directory).
	matches, _ := filepath.Glob(filepath.Join(dir, "*.csproj"))
	if len(matches) > 0 {
		meta := PluginMeta{
			Name:       filepath.Base(dir),
			Kind:       kind,
			Language:   LangCSharp,
			SourcePath: dir,
		}
		return &meta
	}

	return nil
}

// extractMeta reads a file and extracts plugin metadata from comments.
// Supports:
//   - Go:         //vett:tool name "description"
//   - Python:     #vett:tool name "description"
//   - C#:         ///vett:tool name "description"
//   - TypeScript: //vett:tool name "description"
//   - Rust:       //vett:tool name "description"
//
// Parameters:
//   - //vett:param name type "description"
func extractMeta(path string, kind PluginKind, lang Language) PluginMeta {
	meta := PluginMeta{
		Name:       strings.TrimSuffix(filepath.Base(path), filepath.Ext(path)),
		Kind:       kind,
		Language:   lang,
		SourcePath: path,
	}

	f, err := os.Open(path)
	if err != nil {
		return meta
	}
	defer f.Close()

	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())

		// Normalize comment prefixes.
		var content string
		switch {
		case strings.HasPrefix(line, "///"):
			content = strings.TrimSpace(line[3:])
		case strings.HasPrefix(line, "//"):
			content = strings.TrimSpace(line[2:])
		case strings.HasPrefix(line, "#") && !strings.HasPrefix(line, "#!"):
			content = strings.TrimSpace(line[1:])
		default:
			continue
		}

		if strings.HasPrefix(content, "vett:tool ") {
			parts := parseDirective(content[len("vett:tool "):])
			if len(parts) >= 1 {
				meta.Name = parts[0]
			}
			if len(parts) >= 2 {
				meta.Description = parts[1]
			}
			meta.Kind = KindTool
		} else if strings.HasPrefix(content, "vett:middleware ") {
			parts := parseDirective(content[len("vett:middleware "):])
			if len(parts) >= 1 {
				meta.Name = parts[0]
			}
			if len(parts) >= 2 {
				meta.Description = parts[1]
			}
			meta.Kind = KindMiddleware
		} else if strings.HasPrefix(content, "vett:param ") {
			rest := content[len("vett:param "):]
			parts := parseDirective(rest)
			if len(parts) >= 2 {
				param := ParamMeta{Name: parts[0], Type: parts[1]}
				if len(parts) >= 3 {
					param.Description = parts[2]
				}
				meta.Params = append(meta.Params, param)
			}
		}
	}

	return meta
}

// parseDirective splits "name type \"description\"" respecting quotes.
func parseDirective(s string) []string {
	var parts []string
	s = strings.TrimSpace(s)

	for s != "" {
		s = strings.TrimSpace(s)
		if s == "" {
			break
		}
		if s[0] == '"' {
			end := strings.Index(s[1:], "\"")
			if end < 0 {
				parts = append(parts, s[1:])
				break
			}
			parts = append(parts, s[1:end+1])
			s = s[end+2:]
		} else {
			idx := strings.IndexByte(s, ' ')
			if idx < 0 {
				parts = append(parts, s)
				break
			}
			parts = append(parts, s[:idx])
			s = s[idx+1:]
		}
	}
	return parts
}

// HashFile returns the SHA256 hex digest of a file.
func HashFile(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	h := sha256.Sum256(data)
	return hex.EncodeToString(h[:]), nil
}

// statFile is a test hook for os.Stat.
var statFile = func(path string) (fs.FileInfo, error) {
	return os.Stat(path)
}
