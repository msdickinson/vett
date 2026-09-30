# VETT sidecar (Go)

A small static Go binary that the .NET `vett` host starts and talks to over
JSON-RPC on stdin/stdout. It does the agent's hands-on work: bash commands and
file-editor operations. It runs either natively on the host (`LocalSandbox`)
or inside a benchmark's Docker container (`DockerSandbox`).

Why Go: one static binary with no runtime, which `docker cp` can drop into any
Linux image, SWE-bench images included, without installing anything in them.

## Build

Needs Go (version in `go.mod`).

```bash
make sidecar-all        # linux, windows, macOS amd64 + arm64
```

On Windows without `make`:

```powershell
.\build.ps1
```

Both write to `sidecar/bin/` and copy the binaries to `bin/` at the repo root,
where the .NET CLI looks for them and packs them from. The lookup order is in
`src/Vett/Cli/Helpers.cs` (`FindSidecarWithPaths`): `VETT_SIDECAR_PATH`, next
to `vett.dll`, the repo `bin/`, then `~/.vett/bin/`.

Docker sandboxes always run the Linux build, whatever the host OS, so keep
`vett-sidecar-linux-amd64` next to the host's own build.

## Protocol

Newline-delimited JSON-RPC on stdin/stdout. Ops:

- Management: `hello`, `session_create`, `session_destroy`, `session_list`
- Bash: `bash_exec`
- Files: `file_view`, `file_create`, `file_str_replace`, `file_insert`,
  `file_undo`, `file_exists`, `list_dir`

Full spec: [docs/sidecar-protocol.md](docs/sidecar-protocol.md). The .NET
client is `src/Vett/Sandbox/RpcClient.cs`.

## What else is in here

- `docs/` holds the design specs written when VETT was planned as a full Go
  rewrite: protocol, tools, middleware, profile and suite schemas, trace
  format, test strategy. The .NET host kept these designs.
  [docs/product.md](docs/product.md) explains how that turned out.
- `cmd/vett` is the Go host CLI prototype from that period. It still builds
  and its tests run in CI, but the supported CLI is the .NET one.
