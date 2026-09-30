// Package main is the vett-sidecar binary: the in-container agent-server
// that services bash_exec + file editor RPC over stdin/stdout.
//
// Always cross-compiled for linux/amd64 because it runs inside Linux
// containers. On non-linux hosts the binary builds to a stub that prints
// a guidance message and exits 1.
package main

import "os"

const sidecarVersion = "0.1.0-dev"

func main() {
	os.Exit(run())
}
