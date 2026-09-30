//go:build !linux && !windows

package main

import (
	"fmt"
	"os"
)

func run() int {
	fmt.Fprintln(os.Stderr, "vett-sidecar runs only on linux; cross-compile with GOOS=linux GOARCH=amd64.")
	return 1
}
