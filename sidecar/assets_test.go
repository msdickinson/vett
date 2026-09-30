package vettassets

import (
	"bytes"
	"io/fs"
	"testing"
)

// TestEmbeddedProfilesListed — the embed directives actually captured the
// profiles/*.yaml files and ListProfiles returns their base names.
func TestEmbeddedProfilesListed(t *testing.T) {
	names := ListProfiles()
	found := false
	for _, n := range names {
		if n == "openhands" {
			found = true
		}
	}
	if !found {
		t.Errorf("expected 'openhands' in ListProfiles, got %v", names)
	}
}

// TestEmbeddedSuitesListed — suites subtree captured too.
func TestEmbeddedSuitesListed(t *testing.T) {
	names := ListSuites()
	if len(names) == 0 {
		t.Error("expected at least one embedded suite")
	}
}

// TestEmbeddedOpenhandsReadable — openhands.yaml is not just listed but
// actually readable via fs.ReadFile.
func TestEmbeddedOpenhandsReadable(t *testing.T) {
	raw, err := fs.ReadFile(CoreFS(), "profiles/openhands.yaml")
	if err != nil {
		t.Fatalf("read openhands: %v", err)
	}
	if !bytes.Contains(raw, []byte("name: openhands")) {
		t.Errorf("openhands.yaml does not contain expected content:\n%s", raw)
	}
}

// TestEmbeddedSystemPromptReadable — profiles/openhands.yaml references
// ../testdata/ref-system-prompt.txt. That file must also be embedded
// for the profile to load from embed.
func TestEmbeddedSystemPromptReadable(t *testing.T) {
	raw, err := fs.ReadFile(CoreFS(), "testdata/ref-system-prompt.txt")
	if err != nil {
		t.Fatalf("read ref-system-prompt: %v", err)
	}
	if len(raw) < 10000 {
		t.Errorf("expected >10K chars in ref-system-prompt, got %d", len(raw))
	}
}

// TestEmbeddedSidecarBinary — the platform-specific sidecar binary is
// embedded and readable.
func TestEmbeddedSidecarBinary(t *testing.T) {
	b, err := SidecarBytes()
	if err != nil {
		t.Fatalf("read sidecar: %v", err)
	}
	if len(b) < 1_000_000 {
		t.Errorf("embedded sidecar is suspiciously small: %d bytes", len(b))
	}
	// Check platform-appropriate binary magic bytes.
	if len(b) < 4 {
		t.Fatalf("embedded sidecar too short: %d bytes", len(b))
	}
	switch {
	case b[0] == 0x7f && b[1] == 'E' && b[2] == 'L' && b[3] == 'F':
		// Linux ELF — expected on linux builds
	case b[0] == 'M' && b[1] == 'Z':
		// Windows PE — expected on windows builds
	default:
		t.Errorf("embedded sidecar has unknown binary format (first bytes=% x)", b[:4])
	}
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
