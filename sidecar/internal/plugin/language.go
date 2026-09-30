package plugin

import (
	"fmt"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

// Language identifies a supported plugin language.
type Language string

const (
	LangGo         Language = "go"
	LangPython     Language = "python"
	LangCSharp     Language = "csharp"
	LangTypeScript Language = "typescript"
	LangRust       Language = "rust"
	LangRuby       Language = "ruby"
	LangYAML       Language = "yaml"
	LangBinary     Language = "binary"
	LangUnknown    Language = "unknown"
)

// LangInfo describes how to detect, build, and run a language.
type LangInfo struct {
	Language    Language
	Extensions  []string // file extensions (e.g., ".go", ".py")
	SDKCheck    string   // binary to check for SDK (e.g., "go", "python")
	InstallHint string   // how to install the SDK
	NeedsBuild  bool     // whether a compile step is required
}

var languages = []LangInfo{
	{
		Language:    LangGo,
		Extensions:  []string{".go"},
		SDKCheck:    "go",
		InstallHint: "winget install GoLang.Go",
		NeedsBuild:  true,
	},
	{
		Language:    LangPython,
		Extensions:  []string{".py"},
		SDKCheck:    "python3",
		InstallHint: "winget install Python.Python.3.12",
		NeedsBuild:  false,
	},
	{
		Language:    LangCSharp,
		Extensions:  []string{".cs", ".csproj"},
		SDKCheck:    "dotnet",
		InstallHint: "winget install Microsoft.DotNet.SDK.10",
		NeedsBuild:  true,
	},
	{
		Language:    LangTypeScript,
		Extensions:  []string{".ts", ".js"},
		SDKCheck:    "node",
		InstallHint: "winget install OpenJS.NodeJS",
		NeedsBuild:  false,
	},
	{
		Language:    LangRust,
		Extensions:  []string{".rs"},
		SDKCheck:    "cargo",
		InstallHint: "curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh",
		NeedsBuild:  true,
	},
	{
		Language:    LangRuby,
		Extensions:  []string{".rb"},
		SDKCheck:    "ruby",
		InstallHint: "winget install RubyInstallerTeam.Ruby",
		NeedsBuild:  false,
	},
	{
		Language:    LangYAML,
		Extensions:  []string{".yaml", ".yml"},
		SDKCheck:    "", // no SDK needed — bash only
		InstallHint: "",
		NeedsBuild:  false,
	},
}

// DetectLanguage returns the language for a file extension.
func DetectLanguage(filename string) Language {
	ext := strings.ToLower(filepath.Ext(filename))
	for _, lang := range languages {
		for _, e := range lang.Extensions {
			if e == ext {
				return lang.Language
			}
		}
	}
	return LangUnknown
}

// GetLangInfo returns info about a language.
func GetLangInfo(lang Language) *LangInfo {
	for i := range languages {
		if languages[i].Language == lang {
			return &languages[i]
		}
	}
	return nil
}

// CheckSDK returns nil if the SDK for the given language is available.
func CheckSDK(lang Language) error {
	info := GetLangInfo(lang)
	if info == nil {
		return fmt.Errorf("unknown language: %s", lang)
	}

	check := info.SDKCheck
	if runtime.GOOS == "windows" && lang == LangPython {
		check = "python" // Windows uses "python" not "python3"
	}

	// YAML tools need no SDK — they're bash wrappers.
	if lang == LangYAML {
		return nil
	}

	_, err := exec.LookPath(check)
	if err != nil {
		return fmt.Errorf("%s SDK not found. Install: %s", lang, info.InstallHint)
	}
	return nil
}

// BuildCommand returns the command to build a plugin for the given language.
// Returns (command, args, outputPath, error).
func BuildCommand(lang Language, sourcePath, cacheDir string) (string, []string, string, error) {
	name := strings.TrimSuffix(filepath.Base(sourcePath), filepath.Ext(sourcePath))
	ext := ""
	if runtime.GOOS == "windows" {
		ext = ".exe"
	}

	switch lang {
	case LangGo:
		outPath := filepath.Join(cacheDir, name+ext)
		return "go", []string{"build", "-o", outPath, sourcePath}, outPath, nil

	case LangCSharp:
		// For a single .cs file, we need a directory with a .csproj.
		// For a directory with .csproj, build that.
		dir := sourcePath
		if !isDir(sourcePath) {
			dir = filepath.Dir(sourcePath)
		}
		outPath := filepath.Join(cacheDir, name+ext)
		return "dotnet", []string{"build", dir, "-o", cacheDir, "-q"}, outPath, nil

	case LangRust:
		outPath := filepath.Join(cacheDir, name+ext)
		return "cargo", []string{"build", "--manifest-path", sourcePath, "--target-dir", cacheDir, "--release"}, outPath, nil

	case LangPython, LangTypeScript, LangRuby, LangYAML:
		// Interpreted — no build step. Return the source as the "binary."
		return "", nil, sourcePath, nil

	default:
		return "", nil, "", fmt.Errorf("don't know how to build %s", lang)
	}
}

// RunCommand returns the command+args to execute a built plugin.
func RunCommand(lang Language, binaryOrSource string) (string, []string) {
	switch lang {
	case LangGo, LangRust, LangBinary:
		return binaryOrSource, nil
	case LangPython:
		py := "python3"
		if runtime.GOOS == "windows" {
			py = "python"
		}
		return py, []string{binaryOrSource}
	case LangCSharp:
		return "dotnet", []string{"run", "--project", binaryOrSource, "--no-build"}
	case LangTypeScript:
		ext := filepath.Ext(binaryOrSource)
		if ext == ".ts" {
			return "npx", []string{"tsx", binaryOrSource}
		}
		return "node", []string{binaryOrSource}
	case LangRuby:
		return "ruby", []string{binaryOrSource}
	case LangYAML:
		// YAML tools are handled specially by the manager — not spawned
		// as subprocesses. This is a fallback that shouldn't be reached.
		return "echo", []string{"yaml-tool-not-subprocess"}
	default:
		return binaryOrSource, nil
	}
}

func isDir(path string) bool {
	// Quick check — won't be called on missing paths.
	info, err := statFile(path)
	if err != nil {
		return false
	}
	return info.IsDir()
}
