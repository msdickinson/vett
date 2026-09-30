# VETT CLI Specification

This document defines the command surface of the `vett` binary. It's the authoritative reference for what every command does, what flags it accepts, and what exit codes it returns. Anything the binary does must appear here; anything that appears here must be implemented.

## Design principles

1. **Minimal command surface.** Start with the commands that matter and resist the urge to add "convenience" subcommands that nobody uses.
2. **Predictable flag names.** Every command shares the same global flags for endpoint, model, and verbosity. No snowflakes.
3. **Machine-readable output when asked.** `--json` flag on any command that produces structured results.
4. **Deterministic exit codes.** Scripts can rely on specific codes for specific failure modes.
5. **Fast failures.** Every command validates its flags before doing work. Bad flags return exit 2 immediately with a clear error.

## Commands

### `vett run`
Run a benchmark suite against a profile.

**Usage:**
```
vett run --suite <name> --profile <name> [options]
```

**Required flags:**
| Flag | Description |
|---|---|
| `--suite <name>` | Suite name (e.g. `swe-bench-verified`) or path to a suite YAML file |
| `--profile <name>` | Profile name (e.g. `openhands`) or path to a profile YAML file |

**Run selection flags:**
| Flag | Default | Description |
|---|---|---|
| `--instances <n>` | all | Number of instances to run (default: entire suite) |
| `--instance-ids <list>` | — | Comma-separated list of specific instance IDs to run |
| `--filter <regex>` | — | Regex to match instance IDs |
| `--shuffle` | false | Randomize instance order before running |
| `--seed <n>` | — | Seed for shuffling (for reproducibility) |

**LLM flags (or use env vars):**
| Flag | Env var | Description |
|---|---|---|
| `--endpoint <url>` | `VETT_LLM_ENDPOINT` | LLM endpoint URL (e.g. `http://old-gpu-a:8000/v1`) |
| `--model <name>` | `VETT_LLM_MODEL` | Model name (e.g. `qwen3-coder-next`) |
| `--api-key <key>` | `VETT_LLM_API_KEY` | API key (optional, for hosted endpoints) |

**Output flags:**
| Flag | Default | Description |
|---|---|---|
| `--output <dir>` | `./results/<timestamp>` | Directory to write results |
| `--trace` | false | Write per-instance JSONL traces |
| `--live` | false | Print streaming tool output to stdout as the agent runs |
| `--quiet` / `-q` | false | Suppress progress output |

**Execution flags:**
| Flag | Default | Description |
|---|---|---|
| `--concurrency <n>` | 1 | How many instances to run in parallel |
| `--max-iterations <n>` | from profile | Override profile's max iterations |
| `--timeout <duration>` | from profile | Override profile's per-instance timeout |
| `--skip-preflight` | false | Don't run preflight check before starting |
| `--strict-preflight` | false | Abort on any preflight warning, not just errors |

**Behavior:**
1. Validates all flags and resolves the profile + suite
2. Runs `vett preflight` implicitly unless `--skip-preflight` is set (warnings logged, errors abort)
3. For each instance:
   - Boots a container
   - Runs the agent loop
   - Extracts the patch
   - Writes trace file (if `--trace`)
4. Writes summary JSON to `<output>/summary.json`
5. Exits with code 0 if all instances completed (regardless of whether they passed), 4 if any instance errored in an unexpected way

**Examples:**
```bash
# Run 5 instances of swe-bench-verified through the openhands profile
vett run --suite swe-bench-verified --profile openhands --instances 5 \
  --endpoint http://old-gpu-a:8000/v1 --model qwen3-coder-next --trace

# Run a specific instance by ID
vett run --suite swe-bench-verified --profile openhands \
  --instance-ids astropy__astropy-12907 --trace

# Run the whole suite in parallel with live output
vett run --suite swe-bench-verified --profile openhands \
  --concurrency 4 --live --output ./results/full-run-2026-04-15
```

---

### `vett preflight`
Verify the endpoint, model, and profile are configured correctly before running an expensive benchmark.

**Usage:**
```
vett preflight --profile <name> [options]
```

**Required flags:**
| Flag | Description |
|---|---|
| `--profile <name>` | Profile to preflight |

**Optional flags:**
| Flag | Env var | Description |
|---|---|---|
| `--endpoint <url>` | `VETT_LLM_ENDPOINT` | LLM endpoint URL |
| `--model <name>` | `VETT_LLM_MODEL` | Model name |
| `--strict` | — | Exit nonzero on any warning, not just hard errors |
| `--json` | — | Output machine-readable JSON instead of human text |

**Behavior:**
Runs a small set of single-turn smoke checks:
1. **Endpoint reachability** — `GET /v1/models` or similar
2. **Tool schema acceptance** — send a minimal request with the profile's tool schemas, verify the endpoint accepts it
3. **Per-tool smoke checks** — one check per tool in the profile, prompting the model to make a simple call. Warnings (not failures) if the model doesn't respond as expected.

Outputs a traffic-light summary:
```
Vett preflight: openhands @ qwen3-coder-next
  ✓ Endpoint reachable
  ✓ Tool schema accepted (5 tools registered)
  ✓ terminal - model called correctly
  ✓ file_editor - model called correctly
  ✓ think - model called correctly
  ✓ finish - model called correctly
  ⚠ task_tracker - model did not call it when asked

5 ✓  1 ⚠  0 ✗   (proceed? use --skip-preflight to bypass in vett run)
```

**Exit codes:**
- `0` — all checks passed (✓)
- `3` — one or more hard errors (✗), benchmark run would fail
- `10` — `--strict` and at least one warning (⚠)

**Examples:**
```bash
# Standard preflight before a big run
vett preflight --profile openhands --endpoint http://old-gpu-a:8000/v1 --model qwen3-coder-next

# Strict preflight for automation
vett preflight --profile openhands --strict --json > preflight.json
```

---

### `vett profiles`
List or inspect profiles.

**Usage:**
```
vett profiles [list|show <name>]
```

**Subcommands:**
- `list` (default) — print all available profiles
- `show <name>` — print the parsed profile config

**Flags:**
| Flag | Description |
|---|---|
| `--json` | Output JSON instead of human-readable |

**Behavior:**
Scans the profile search path (in order):
1. `./profiles/` (current dir)
2. `~/.vett/profiles/` (user dir)
3. Embedded profiles (shipped with binary)

Later paths override earlier ones on name collision (with a warning).

**Examples:**
```bash
vett profiles                # list all profiles
vett profiles list           # explicit list
vett profiles show openhands # show the openhands profile config
```

---

### `vett suites`
List or inspect suites.

Mirrors `vett profiles` in every way, but for suites.

```bash
vett suites
vett suites show swe-bench-verified
```

---

### `vett results`
Inspect past run results.

**Usage:**
```
vett results [<run-id>]
```

**Flags:**
| Flag | Description |
|---|---|
| `--results-dir <dir>` | Default `./results` — directory to search for past runs |
| `--json` | Machine-readable output |
| `--compare <run-id>` | Compare against another run |

**Behavior:**
- With no args: list all runs in `--results-dir` with summary stats (timestamp, suite, profile, resolution rate, total duration)
- With a run id: show the detailed summary for that run
- With `--compare`: diff two runs side by side (instance-by-instance pass/fail deltas)

**Examples:**
```bash
vett results
vett results full-run-2026-04-15
vett results full-run-2026-04-15 --compare full-run-2026-04-14
```

---

### `vett verify`
Re-score a past run's patches against the suite's test harness.

**Usage:**
```
vett verify <run-id>
```

**Behavior:**
Takes an existing run's results (specifically its saved patches), re-runs the suite's scorer against them, and writes an updated `summary.json` with the scoring results. Used when:
- A run completed but scoring was skipped (e.g. Docker issues at the time)
- The scoring logic changed and you want to rescore old runs
- You want to verify a run's reported scores weren't corrupted

**Exit codes:**
- `0` — all instances scored successfully
- `4` — some instances failed to score (their images are missing, etc.)

---

### `vett version`
Print version info.

**Usage:**
```
vett version
```

**Output:**
```
vett 0.1.0
  built:     2026-04-15T15:30:00Z
  commit:    a1b2c3d
  go:        go1.22.0
  sidecar:   vett-sidecar 0.1.0 (sha256:abc...)
```

---

## Global flags

These work on every command:

| Flag | Env var | Description |
|---|---|---|
| `--verbose` / `-v` | `VETT_VERBOSE` | Enable verbose logging |
| `--quiet` / `-q` | — | Suppress non-essential output |
| `--help` / `-h` | — | Show command help |
| `--no-color` | `NO_COLOR` | Disable ANSI color codes |

---

## Environment variables

| Variable | Purpose |
|---|---|
| `VETT_LLM_ENDPOINT` | Default LLM endpoint URL |
| `VETT_LLM_MODEL` | Default LLM model name |
| `VETT_LLM_API_KEY` | LLM API key (optional) |
| `VETT_CONFIG_DIR` | Override default `~/.vett/` config directory |
| `VETT_VERBOSE` | Enable verbose logging |
| `VETT_SKIP_LIVE_LLM_TESTS` | Skip tests that hit real LLM (for dev) |
| `NO_COLOR` | Disable ANSI colors |

---

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | General failure (unhandled error) |
| `2` | Bad command-line usage (invalid flags, missing required args) |
| `3` | Preflight hard failure |
| `4` | Run completed but with errors (e.g. some instances failed to score) |
| `5` | Docker / infrastructure error (daemon not running, image not found) |
| `6` | Profile or suite not found |
| `7` | LLM endpoint unreachable |
| `10` | Strict mode triggered (preflight warning, lint warning, etc.) |
| `130` | Interrupted (Ctrl+C) — standard Unix convention |

Scripts should check for specific codes and not rely on "nonzero means failure" alone, especially to distinguish `4` (partial success — worth investigating) from `3`, `5`, `7` (setup failures — fix config and retry).

---

## Config file (future)

Vett currently reads all config from flags and env vars. If config-file support is added, the format will be YAML at `~/.vett/config.yaml`:

```yaml
llm:
  endpoint: http://old-gpu-a:8000/v1
  model: qwen3-coder-next
defaults:
  output_dir: ./results
  concurrency: 4
```

**Not in v1.** Flags + env vars are sufficient until someone asks for it.

---

## Changelog

- **2026-04-15:** Initial spec. All commands listed as planned; no implementation yet.
