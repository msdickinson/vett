package plugin

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
)

// PackageFile maps languages to their dependency file names.
var packageFiles = map[Language][]struct {
	File    string
	Install func(ctx context.Context, dir string) error
}{
	LangPython: {
		{
			File: "requirements.txt",
			Install: func(ctx context.Context, dir string) error {
				py := "python3"
				if runtime.GOOS == "windows" {
					py = "python"
				}
				cmd := exec.CommandContext(ctx, py, "-m", "pip", "install", "-r",
					filepath.Join(dir, "requirements.txt"), "-q")
				cmd.Stderr = os.Stderr
				return cmd.Run()
			},
		},
		{
			File: "pyproject.toml",
			Install: func(ctx context.Context, dir string) error {
				py := "python3"
				if runtime.GOOS == "windows" {
					py = "python"
				}
				cmd := exec.CommandContext(ctx, py, "-m", "pip", "install", "-e", dir, "-q")
				cmd.Stderr = os.Stderr
				return cmd.Run()
			},
		},
	},
	LangTypeScript: {
		{
			File: "package.json",
			Install: func(ctx context.Context, dir string) error {
				cmd := exec.CommandContext(ctx, "npm", "install", "--prefix", dir, "--silent")
				cmd.Stderr = os.Stderr
				return cmd.Run()
			},
		},
	},
	LangGo: {
		{
			File: "go.mod",
			Install: func(ctx context.Context, dir string) error {
				cmd := exec.CommandContext(ctx, "go", "mod", "download")
				cmd.Dir = dir
				cmd.Stderr = os.Stderr
				return cmd.Run()
			},
		},
	},
	LangCSharp: {
		{
			File: "*.csproj",
			Install: func(ctx context.Context, dir string) error {
				cmd := exec.CommandContext(ctx, "dotnet", "restore", dir, "-q")
				cmd.Stderr = os.Stderr
				return cmd.Run()
			},
		},
	},
	LangRust: {
		{
			File: "Cargo.toml",
			Install: func(ctx context.Context, dir string) error {
				// cargo build handles dependencies automatically.
				return nil
			},
		},
	},
	LangRuby: {
		{
			File: "Gemfile",
			Install: func(ctx context.Context, dir string) error {
				cmd := exec.CommandContext(ctx, "bundle", "install", "--gemfile",
					filepath.Join(dir, "Gemfile"), "--quiet")
				cmd.Stderr = os.Stderr
				return cmd.Run()
			},
		},
	},
}

// InstallPackages checks for package files in the given directory and
// runs the appropriate install command. Returns nil if no package file
// found or installation succeeded.
func InstallPackages(ctx context.Context, lang Language, dir string) error {
	entries, ok := packageFiles[lang]
	if !ok {
		return nil
	}

	for _, entry := range entries {
		// Handle glob patterns (*.csproj).
		if filepath.Base(entry.File) != entry.File || entry.File == "*.csproj" {
			matches, _ := filepath.Glob(filepath.Join(dir, entry.File))
			if len(matches) > 0 {
				if err := entry.Install(ctx, dir); err != nil {
					return fmt.Errorf("install %s packages: %w", lang, err)
				}
				return nil
			}
			continue
		}

		path := filepath.Join(dir, entry.File)
		if _, err := os.Stat(path); err == nil {
			if err := entry.Install(ctx, dir); err != nil {
				return fmt.Errorf("install %s packages: %w", lang, err)
			}
			return nil
		}
	}

	return nil
}
