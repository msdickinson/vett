//go:build windows

package sidecar

import "os"

// bashPath returns the path to a bash executable on Windows.
// Checks VETT_BASH_PATH env override first, then common Git Bash
// locations, then falls back to bare "bash" on PATH.
func bashPath() string {
	if p := os.Getenv("VETT_BASH_PATH"); p != "" {
		return p
	}
	candidates := []string{
		`C:\Program Files\Git\bin\bash.exe`,
		`C:\Program Files (x86)\Git\bin\bash.exe`,
	}
	for _, c := range candidates {
		if _, err := os.Stat(c); err == nil {
			return c
		}
	}
	return "bash"
}
