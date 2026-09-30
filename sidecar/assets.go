// Package vettassets is the root-level assets bundle for the Vett binary.
// It embeds the canonical built-in profiles, suites, and captured system
// prompt directly into the `vett` host binary via go:embed.
//
// The sidecar binary is embedded in platform-specific files
// (assets_linux.go, assets_windows.go) so each build only carries its
// own platform's sidecar.
//
// Mental model: assets here are the "core" — read-only, shipped with
// the install, invisible to the user. They live alongside whatever
// per-workspace files the user creates in their own directories under
// profiles/, suites/, and results/.
//
// Lookup order for CLI commands:
//  1. Explicit path on the command line (--profile ./foo.yaml)
//  2. Current workspace ./profiles/<name>.yaml, ./suites/<name>.yaml
//  3. This embedded core (fallback for the built-in openhands profile,
//     test-canned suite, etc.)
//
// The sidecar binary is embedded but also extracted to
// ~/.vett/bin/ on demand by `vett install` or lazily by `vett run`.
package vettassets

import (
	"embed"
	"io/fs"
)

// sharedFS contains profiles, suites, and test data. The sidecar is
// in a separate platform-specific embed (see assets_linux.go /
// assets_windows.go).
//
//go:embed profiles/*.yaml suites/*.yaml testdata/ref-system-prompt.txt
var sharedFS embed.FS

// CoreFS returns the unified embedded tree for profiles and suites.
func CoreFS() fs.FS { return sharedFS }

// ReadFile returns the bytes for a path inside the core FS.
func ReadFile(name string) ([]byte, error) {
	return fs.ReadFile(sharedFS, name)
}

// ListProfiles returns the base names (without directory or extension)
// of every embedded profile.
func ListProfiles() []string { return listYAMLBases("profiles") }

// ListSuites returns the base names of every embedded suite.
func ListSuites() []string { return listYAMLBases("suites") }

func listYAMLBases(dir string) []string {
	entries, err := fs.ReadDir(sharedFS, dir)
	if err != nil {
		return nil
	}
	var out []string
	for _, e := range entries {
		name := e.Name()
		if len(name) > 5 && name[len(name)-5:] == ".yaml" {
			out = append(out, name[:len(name)-5])
		}
	}
	return out
}
