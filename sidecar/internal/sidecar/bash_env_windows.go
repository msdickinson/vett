//go:build windows

package sidecar

import "os"

func syscallEnviron() []string { return os.Environ() }
