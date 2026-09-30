# AGENTS.md

Orientation for AI coding agents (and people) working on VETT itself.
Design and the reasons behind it: [docs/DESIGN.md](docs/DESIGN.md).

## Layout

- `src/Vett/`: the .NET 10 CLI (`vett`), packed as the dotnet tool `VettBench`.
  - `Agent/`: agent loop, team lead + workers, task board, dispatch worktrees
  - `Capacity/`: multi-endpoint scheduling and the capacity broker
  - `Tools/`: tools and middleware (compaction, permissions, caps, retries)
  - `Sandbox/`: local and Docker sandboxes, JSON-RPC client to the sidecar
  - `Cli/`: one file per command
- `sidecar/`: the Go sandbox binary (see [sidecar/README.md](sidecar/README.md))
- `profiles/`, `defaults/`, `suites/`, `pipelines/`, `bench-profiles/`,
  `capabilities/`, `schemas/`: YAML/JSON config, shipped inside the tool
- `tests/Vett.Tests/`: xUnit tests

## Build and test

```bash
cd sidecar && make sidecar-all && cd ..   # or sidecar\build.ps1 on Windows
dotnet build Vett.slnx
dotnet test Vett.slnx
cd sidecar && go vet ./... && go test ./...
```

Read the test COUNT, not just the word "Passed": `dotnet test` exits 0 when it
finds no test project. A few streaming tests time real silences and can flake
on a loaded machine; rerun those alone before calling it a failure.

## Rules that keep results trustworthy

- **The `openhands` profile is the OpenHands SDK 1.14.0 baseline.** If
  OpenHands doesn't do it, it doesn't go in that profile; put new ideas in
  another profile so the effect can be measured against the baseline. Changes
  to it should only close the known gaps in
  [docs/openhands-fidelity.md](docs/openhands-fidelity.md).
- **Absent config is an error, not a default.** Profiles and suites are
  validated (`vett validate`); a missing key fails loudly instead of silently
  falling back.
- **Never tune against held-out instances.** A pass on something you tuned
  on proves nothing.
- **A task is done only with evidence.** Gates and acceptance checks decide,
  not the agent's own claim.
- Console output is ASCII (`->`, `[OK]`, `[FAIL]`), so it survives any terminal.
