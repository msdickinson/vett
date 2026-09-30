//go:build windows

package vettassets

import (
	_ "embed"
)

//go:embed bin/vett-sidecar-windows-amd64.exe
var sidecarBytes []byte

// SidecarBytes returns the embedded sidecar binary for this platform.
func SidecarBytes() ([]byte, error) {
	return sidecarBytes, nil
}

// SidecarFilename returns the platform-specific sidecar binary name.
func SidecarFilename() string { return "vett-sidecar-windows-amd64.exe" }
