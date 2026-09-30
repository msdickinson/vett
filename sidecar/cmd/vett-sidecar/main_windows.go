//go:build windows

package main

import (
	"context"
	"fmt"
	"os"

	"github.com/msdickinson/vett/sidecar/internal/sidecar"
)

func run() int {
	srv := sidecar.NewServer(os.Stdin, os.Stdout)
	if err := srv.Serve(context.Background()); err != nil {
		fmt.Fprintln(os.Stderr, "vett-sidecar:", err)
		return 1
	}
	return 0
}
