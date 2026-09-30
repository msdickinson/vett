package main

import (
	"fmt"
	"os"
	"path/filepath"

	vettassets "github.com/msdickinson/vett/sidecar"
)

// resolveSidecarPath finds a usable sidecar binary for the current platform.
// Search order:
//  1. ./bin/<sidecar-filename> (repo-local, when running from the
//     VETT working tree after `make build`)
//  2. ~/.vett/bin/<sidecar-filename> (installed via `vett install`)
//  3. Lazy-extract from the embedded asset to ~/.vett/bin/ and chmod +x
//
// Returns an absolute path.
func resolveSidecarPath() (string, error) {
	name := vettassets.SidecarFilename()
	// (1) repo-local
	if wd, err := os.Getwd(); err == nil {
		p := filepath.Join(wd, "bin", name)
		if _, err := os.Stat(p); err == nil {
			return p, nil
		}
	}
	// (2) ~/.vett/bin/
	installed, err := userSidecarPath()
	if err != nil {
		return "", err
	}
	if _, err := os.Stat(installed); err == nil {
		return installed, nil
	}
	// (3) Lazy extract from embed.
	bytes, err := vettassets.SidecarBytes()
	if err != nil {
		return "", fmt.Errorf("no sidecar binary available (no ./bin copy, not installed, embed missing): %w", err)
	}
	if err := os.MkdirAll(filepath.Dir(installed), 0o755); err != nil {
		return "", fmt.Errorf("create ~/.vett/bin: %w", err)
	}
	if err := os.WriteFile(installed, bytes, 0o755); err != nil {
		return "", fmt.Errorf("write sidecar to %s: %w", installed, err)
	}
	return installed, nil
}

// userSidecarPath returns ~/.vett/bin/<sidecar-filename> with
// forward-slash-consistent path joining.
func userSidecarPath() (string, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return "", fmt.Errorf("could not determine home dir: %w", err)
	}
	return filepath.Join(home, ".vett", "bin", vettassets.SidecarFilename()), nil
}
