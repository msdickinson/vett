// Package version exposes build metadata that landed in the CLI binary.
// Written to run_start trace events so future export tools can
// reconstruct exactly which vett + which sidecar produced a run.
package version

// Vett is the host binary version. cmd/vett/main.go overrides this at
// startup via an init() with its own -ldflags-capable variable; this
// file's value is the fallback default for `go run` and dev builds.
var Vett = "0.1.0-dev"
