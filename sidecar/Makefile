.PHONY: all build test test-full test-nightly clean install fmt vet \
        sidecar sidecar-windows sidecar-darwin-amd64 sidecar-darwin-arm64 sidecar-all

all: build

# sidecar cross-compiles the in-container Linux binary. The host vett
# binary embeds this file via go:embed, so it MUST exist on disk before
# `go install` runs. The install target depends on it.
sidecar:
	GOOS=linux GOARCH=amd64 go build -o bin/vett-sidecar-linux-amd64 ./cmd/vett-sidecar

sidecar-windows:
	GOOS=windows GOARCH=amd64 go build -o bin/vett-sidecar-windows-amd64.exe ./cmd/vett-sidecar

# macOS targets — Intel + Apple Silicon. NOTE: Cross-compiling from
# Linux/Windows produces a runnable binary but does NOT codesign it,
# so on a real Mac it'll trip Gatekeeper. For a smooth user experience
# the macOS sidecar should be built on a Mac (or via a GitHub Actions
# macOS runner) and either notarized or distributed with `xattr -d
# com.apple.quarantine` instructions.
sidecar-darwin-amd64:
	GOOS=darwin GOARCH=amd64 go build -o bin/vett-sidecar-darwin-amd64 ./cmd/vett-sidecar

sidecar-darwin-arm64:
	GOOS=darwin GOARCH=arm64 go build -o bin/vett-sidecar-darwin-arm64 ./cmd/vett-sidecar

# Build every supported sidecar in one shot, then copy them to bin/ at the
# repo root, where the .NET CLI looks for them and packs them from.
sidecar-all: sidecar sidecar-windows sidecar-darwin-amd64 sidecar-darwin-arm64
	mkdir -p ../bin && cp bin/vett-sidecar-* ../bin/

build: sidecar
	go build -o bin/vett ./cmd/vett

build-windows: sidecar-windows
	GOOS=windows GOARCH=amd64 go build -o bin/vett.exe ./cmd/vett

# install: full end-user install. Cross-compiles the sidecar, then
# `go install ./cmd/vett` which embeds the sidecar and drops vett into
# $GOPATH/bin. After this, typing `vett` from any directory works.
install: sidecar
	go install ./cmd/vett

test:
	go test ./...

test-full:
	go test -tags integration ./...

test-nightly:
	go test -tags integration,e2e ./...

fmt:
	gofmt -w .

vet:
	go vet ./...

clean:
	rm -rf bin/

capture-ref:
	./testdata/capture-ref.sh
