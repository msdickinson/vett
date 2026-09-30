//go:build linux

package vettassets

import (
	_ "embed"
)

//go:embed bin/vett-sidecar-linux-amd64
var sidecarBytes []byte

// SidecarBytes returns the embedded sidecar binary for this platform.
func SidecarBytes() ([]byte, error) {
	return sidecarBytes, nil
}

// SidecarFilename returns the platform-specific sidecar binary name.
func SidecarFilename() string { return "vett-sidecar-linux-amd64" }
