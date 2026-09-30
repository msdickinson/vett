//go:build linux

package sidecar

import "os"

func syscallEnviron() []string { return os.Environ() }
