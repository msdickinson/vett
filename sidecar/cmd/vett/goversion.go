package main

import "runtime"

// goVersion returns the Go version the binary was compiled with.
func goVersion() string { return runtime.Version() }
