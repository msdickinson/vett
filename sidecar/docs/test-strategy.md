# VETT Test Strategy

This document defines how Vett proves itself correct. The goal is a test suite you can trust so well that when a test passes, you KNOW the code is right, and when a test fails, you KNOW something real broke. Flaky tests are poison — they train you to ignore failures, and then real bugs slip through. Everything here is designed to avoid that.

## The seven guarantees

When Vett's full test suite passes, you should be able to say with high confidence:

1. The harness dispatches tool calls correctly (no lost calls, no duplicated calls, no silently mutated args).
2. The harness sends bit-exact wire format to the LLM (no subtle protocol drift).
3. The harness executes commands in the container faithfully (**byte-for-byte**, no escaping bugs, no cwd confusion, no envelope leakage).
4. The harness extracts patches correctly (no corruption, no missing files, no stale state).
5. The middleware pipeline runs in deterministic order and does exactly what each middleware says.
6. The `openhands` profile specifically produces trajectories behaviorally indistinguishable from openhands-sdk on the same inputs.
7. Changes to any of the above fail the suite immediately — no regression can silently slip in.

## Four tiers plus preflight

Tests are grouped by **frequency and flake tolerance**, not by what they test. Running a fast test before every commit and a slow test nightly is a frequency decision. Running a flaky test only with a human watching is a tolerance decision.

### Tier 1 — **Quick tests**
- **Frequency:** every `make test` or file save during dev
- **Runtime target:** under 10 seconds total
- **Flake tolerance:** zero — fully deterministic
- **LLM usage:** none
- **Contents:** unit tests (Layer 1) + fast integration tests (Layer 2 without docker) + static mirror tests (Layer 3a)

These are what runs constantly during development. If they take more than 10 seconds, something is wrong. If they ever flake, the offending test gets fixed immediately or deleted.

### Tier 2 — **Full tests**
- **Frequency:** before merging a PR (or manually via `make test-full`)
- **Runtime target:** under 5 minutes total
- **Flake tolerance:** essentially zero — deterministic except for network to a real vLLM endpoint
- **LLM usage:** 2-4 live calls for wire smoke
- **Contents:** Tier 1 + docker-based integration tests (slow Layer 2) + wire smoke (Layer 3b)

Runs on every PR to catch integration bugs and verify the endpoint accepts our requests. The only source of flakiness is "is the vLLM endpoint up?" and if it's not, the test fails loudly and you know why.

### Tier 3 — **Comprehensive tests**
- **Frequency:** nightly (or on-demand via `make test-nightly`)
- **Runtime target:** 1-3 hours
- **Flake tolerance:** tolerates sampling variance from real LLM calls
- **LLM usage:** 100-500 live calls
- **Contents:** Tier 2 + end-to-end tests (Layer 4) — 3-5 real SWE-bench instances, trajectory comparison, resolution-rate regression check

These are the ones that catch behavioral drift across multi-turn conversations. They're expensive, so they run while you sleep. Sampling variance means a test can fail once and pass on retry; the framework is aware of this and reports "failed but within variance band" as yellow, not red.

### Tier 4 — **Preflight (NOT a test)**
- **Frequency:** manual, before expensive benchmark runs
- **Runtime target:** 15-30 seconds
- **Flake tolerance:** high — a human reads the output
- **LLM usage:** 5-7 live calls
- **Contents:** `vett preflight` command — single-turn tool-use smoke checks

**Preflight is an operational command, not a CI test.** It runs when you tell it to. It can be flaky and that's fine — you're reading the output and deciding whether to proceed. See [cli-spec.md](cli-spec.md#vett-preflight) for the command details. It never blocks commits, never runs in CI, never stops a build.

---

## Layer breakdown

The tiers above map to layers of what's actually being tested. One layer can appear in multiple tiers depending on speed.

### Layer 1 — Unit tests (Quick)
Pure logic functions. No docker, no LLM, no network, no filesystem (unless testing filesystem code specifically). Each test runs in milliseconds.

**What they catch:**
- Serialization bugs (JSON/YAML round-trip correctness)
- Schema construction bugs (tool parameter JSON schema)
- Middleware logic bugs (given a canned message list, does the middleware produce expected output?)
- Tool argument parsing bugs
- Patch extraction logic bugs
- StuckDetector scenario logic bugs (all 5 scenarios from openhands-sdk)
- Event equality comparison bugs

**Target:** ~250 tests, under 10 seconds. Run on every `go test`.

**Key tests:**
- `TestTerminalToolSchemaMatchesRef` — build schema, marshal, diff against golden file
- `TestFileEditorToolSchemaMatchesRef` — same
- `TestTaskTrackerToolSchemaMatchesRef` — same
- `TestFinishToolSchemaMatchesRef` — same
- `TestThinkToolSchemaMatchesRef` — same
- `TestRequestBodyShapeMatchesRef` — full request body diffed against golden
- `TestStructuredMessageContentFormat` — non-tool messages are `[{text, type}]` alphabetical
- `TestToolRoleMessageContentIsString` — tool-role messages are plain strings
- `TestStuckDetectorScenario1..5` — all five scenarios from openhands stuck_detector.py
- `TestStuckDetectorResetsOnUserMessage` — user message clears detection window
- `TestEventEqualityIgnoresIds` — IDs/metrics don't affect equality
- `TestExpandTabsMatchesPython` — file editor expandtabs matches Python's behavior

### Layer 2 — Integration tests (Quick + Full)
Real sandbox, real sidecar, real docker (for the slow subset). No LLM. Tests behavior across component boundaries.

**What they catch:**
- Sidecar protocol bugs (can host and sidecar actually talk?)
- Session management bugs (create, destroy, switch, isolate)
- **Bash passthrough bugs** (the most important one — commands reach bash byte-for-byte)
- File editor semantic bugs (multi-match str_replace, undo history, encoding)
- Docker lifecycle bugs (container start, clean death, image-not-found error message)
- Worktree creation and isolation
- Native op correctness (grep_files, git_diff, etc.)
- Streaming / buffered equivalence
- Patch extraction correctness

**Target:** ~60 tests, under 2 minutes. Fast subset in Quick, full subset in Full.

**Key tests:**
- `TestBashPassthroughIsBitExact` — the critical invariant. Runs a table of tricky commands through real sidecar+bash, asserts bytes bash received == bytes sent.
- `TestWorkingDirectorySetAtSessionInit` — session created with cwd, first command `echo $PWD` returns that cwd, no `cd` prepend visible.
- `TestSidecarSessionsAreIsolated` — two sessions in same container, setting var in one doesn't affect the other.
- `TestFileEditorStrReplaceRejectsMultipleMatches` — error with line numbers.
- `TestFileEditorUndoHistoryPerFile` — undoing file A doesn't affect file B.
- `TestStreamingAndBufferedProduceSameBytes` — run same command both modes, concatenated stream == buffered.
- `TestPatchExtractionCleanNoEnvelopeLeakage` — verify no `[Current working directory: ...]` in output.
- `TestSidecarCrashFailsFast` — kill the sidecar mid-run, host detects it, returns clear error.
- `TestDockerImageNotFoundMessage` — missing image → actionable error, not "manifest unknown".

### Layer 3a — Static mirror tests (Quick)
Compare Vett's outputs against captured reference data from openhands-sdk. No LLM calls, no runtime behavior — just "does Vett produce the same bytes as openhands did when we captured this."

**What they catch:**
- Wire format drift (field order, types, missing fields)
- Tool schema drift after refactoring
- Observation format drift
- Error message format drift

**Target:** ~15 tests, under 30 seconds. Run on every commit.

**Key tests:**
- `TestTurn1RequestByteExactVsRef` — builds Vett's first outgoing request for a canned prompt, diffs against `testdata/ref-requests/turn-1.json`. Byte-identical on `{model, messages, tools, temperature, top_p}`.
- `TestTerminalObservationFormatMatchesRef` — bash command result formatted exactly as openhands formats it.
- `TestFileEditorObservationFormatsMatchesRef` — file editor observation envelope matches.
- `TestToolErrorMessageFormatMatchesRef` — Vett's error messages from failed tool calls match openhands format.

### Layer 3b — Wire smoke (Full)
Minimal live LLM calls — just enough to prove the endpoint accepts what we send and the tool-call parser works.

**What they catch:**
- vLLM rejecting our request format (even though it passes golden tests)
- Tool-call parser (`qwen3_coder`) emitting malformed output
- Endpoint drift after vLLM upgrades

**Target:** 2-4 live calls, under 1 minute. Run on every PR merge.

**Key tests:**
- `TestLiveWireAccept` — send a minimal request, expect 200 OK with parseable completion.
- `TestLiveToolCallParseWellFormed` — send a prompt that should trigger a tool call, verify the response parses.

### Layer 4 — E2E nightly (Comprehensive)
Full SWE-bench instances, end to end, real model, real trajectory, compared against openhands reference.

**What they catch:**
- Behavioral drift across multi-turn conversations
- Aggregate regressions in resolution rate

**Target:** 3-5 instances, 1-3 hours. Run nightly.

**Key tests:**
- `TestOpenhandsProfileOn5AstropyInstances` — runs 5 known instances through the `openhands` profile, records resolution rate, compares against a recorded baseline. Fails if rate drops more than 10% from last baseline.
- `TestTrajectoryShapeMatchesRef` — for one canned instance, record the sequence of tool calls and compare against openhands' saved trajectory (shape, not exact content).

---

## Golden files

A lot of the mirror tests rely on pre-captured reference data. This is critical and needs a clear protocol to avoid silent drift.

### Layout

```
testdata/
├── ref-requests/
│   ├── turn-1-initial.json              # captured from ref, turn 1
│   ├── turn-2-after-terminal.json       # ref turn 2 after a terminal call
│   ├── turn-5-after-file-edit.json      # ref turn 5 after mixed calls
│   └── README.md                        # when these were captured, which model, which vLLM version
├── ref-observations/
│   ├── terminal-success.txt             # ref's envelope format for a success
│   ├── terminal-timeout.txt             # ref's format for a timeout
│   ├── file-editor-str-replace-success.txt
│   └── ...
├── golden-schemas/
│   ├── terminal.json                    # extracted from ref request
│   ├── file_editor.json
│   ├── task_tracker.json
│   ├── finish.json
│   └── think.json
├── fixtures/
│   └── canned-instances/                # synthetic SWE-bench-like instances for tests
└── capture-ref.sh                       # script to regenerate captures from live openhands-sdk
```

### Capture protocol

Captures come from one place: a running openhands-sdk with a litellm monkey-patch that records outgoing requests. The capture script lives at `testdata/capture-ref.sh` and is committed to the repo. When openhands-sdk is upgraded or config changes, you run the script, commit the new captures as part of the same PR as the code change, and the reviewer sees the golden diff alongside the code diff.

**No silent golden drift.** If a golden file changes, a human saw it and approved it in a PR.

### Updating rules

- **Never auto-regenerate goldens during test runs.** That would silently swallow bugs.
- **Regenerate via `make capture-ref`.** Explicit command, explicit commit.
- **PR description must note why** if any golden file changed. "openhands-sdk upgraded from X to Y, schemas shifted, here's the diff."

---

## KNOWN_LIMITS protocol

Some tests fail not because of a Vett bug but because the model genuinely can't do the thing. We track these in `testdata/KNOWN_LIMITS.md`:

```markdown
# Known Model Limits (not Vett bugs)

These test failures are expected and reflect model capability limits, not harness regressions.

## qwen3-coder-next
- `TestPreflightTaskTrackerCall` — model only calls task_tracker ~40% of the time even with explicit prompts. Not a Vett bug.
- `TestToolUseC2TerminalLoopRecovery` — model loops on "command not found" errors ~30% of the time. Known pattern, no harness fix possible.
```

When a test fails, the test runner cross-references this file. If the failure is a documented known limit, it's reported as ⚠ (known-limit) instead of ✗ (regression). Still visible in test output — you can't forget it — but it doesn't fail the build.

**This file is the only way to legitimately "skip" a failing test.** Adding an entry requires a PR where the reviewer sees it. Removing an entry is how you celebrate "the model got better."

---

## CI loops

Three loops running at three speeds:

### Loop 1 — on every `go test` (local dev)
```
make test         # or: go test ./...
```
- Runs: Quick tier
- Duration: <10s
- Never blocks you, runs constantly
- Optional file watcher: `make watch` runs this on every file save

### Loop 2 — on every PR (CI)
```
make test-full    # or: go test -tags integration ./...
```
- Runs: Quick + Full tiers
- Duration: <5 min
- Blocks merges if it fails
- Catches most real regressions

### Loop 3 — nightly (cron)
```
make test-nightly # or: go test -tags integration,e2e ./...
```
- Runs: Quick + Full + Comprehensive tiers
- Duration: 1-3 hours
- Doesn't block anything directly — reports results in the morning
- Catches behavioral regressions that sampling variance can hide

---

## Dev workflow

The intended flow for daily development:

1. **Make a change.** Edit a Go file.
2. **File watcher runs Quick tests automatically.** Within 10 seconds you see pass/fail for unit + fast integration.
3. **If a test fails, fix it.** Red-green-refactor cycle. Never commit with Quick tests red.
4. **Before pushing:** run `make test-full` to catch PR-blocking issues. 5 minutes.
5. **Open a PR:** CI runs `make test-full` automatically, same result.
6. **Overnight:** `make test-nightly` on the CI host catches anything the fast tests missed.

The file watcher is the key productivity lever. With Quick tests under 10 seconds, every save gives you feedback within the time it takes to look away from the keyboard. You stay in flow. You catch bugs the moment you introduce them, not 10 commits later.

---

## Go conventions

How these tiers map to Go's test tooling:

**Build tags** differentiate test layers:
- No tag → Quick tier (Layer 1 + fast Layer 2 + Layer 3a)
- `-tags integration` → adds slow Layer 2 + Layer 3b
- `-tags e2e` → adds Layer 4

Example:
```go
//go:build integration
package sandbox_test

func TestBashPassthroughIsBitExact(t *testing.T) { ... }
```

```go
//go:build e2e
package e2e_test

func TestOpenhandsProfileOn5AstropyInstances(t *testing.T) { ... }
```

**Makefile targets:**
```makefile
test:
	go test ./...

test-full:
	go test -tags integration ./...

test-nightly:
	go test -tags integration,e2e ./...

watch:
	reflex -r '\.go$$' -- go test ./...

capture-ref:
	./testdata/capture-ref.sh
```

**`vett run` does not trigger tests.** The `vett` binary is compiled Go code with its own `main` function. Tests are a separate world accessible only via `go test`. There is no code path from the binary to the test suite and no way for a `vett run` invocation to accidentally run tests.

---

## What we do NOT test

Explicitly out of scope for Vett's test suite:

- **LLM sampling behavior.** If qwen3-coder emits a different tool call today than yesterday due to `temperature=1.0`, that's not a Vett bug. Mirror tests use deterministic canned captures, not live comparison, for this reason.
- **vLLM server correctness.** If the endpoint misparses our request, we can't catch it in Vett. We can only catch our own errors.
- **SWE-bench harness correctness.** If a reference patch passes openhands-sdk's scorer but fails ours, it's usually an environment difference, not Vett.
- **Network reliability.** Retries are in the LLM client but real flaky networks aren't simulated.
- **GPU state on vLLM servers.** Not our problem, not testable from Vett.

---

## LLM usage budget

A summary of when the test suite actually calls a real LLM:

| Layer | LLM calls | Runtime contribution |
|---|---|---|
| Quick tests | 0 | 0s |
| Full tests | 2-4 | ~1 min |
| Comprehensive tests | 100-500 | 1-3 hr |
| Preflight (manual) | 5-7 | ~30s |

**Total LLM budget per dev day:** maybe 10-50 calls if you run preflight and full tests manually a few times. Overnight: 100-500 from the nightly run. This is manageable cost and the LLM is never in the hot dev loop.

**Rule:** if a new test needs an LLM to catch the bug it's looking for, it goes in Tier 2 (Full) at the earliest. If it can catch the bug deterministically, it goes in Tier 1 (Quick). The default for every new test is "can I write this without a live LLM?" and only if the answer is no does it get one.

---

## Adding a new test (checklist)

When adding a new test, the PR must answer these five questions:

1. **Which tier does it belong to?** Quick / Full / Comprehensive.
2. **Which guarantee does it protect?** Cross-reference one of the seven at the top of this doc.
3. **Does it need an LLM? If yes, why couldn't a deterministic test catch the same bug?**
4. **Does it use a golden file? If yes, did you commit the golden and explain its source?**
5. **What's the expected runtime?** If >10s in Quick tier, bump to Full. If >5min in Full, bump to Comprehensive.

Answers go in the PR description. Reviewer checks them. If anything's missing, fix before merge.

---

## Changelog

- **2026-04-15:** Initial strategy document. Four tiers + preflight, golden file protocol, KNOWN_LIMITS, dev workflow.
