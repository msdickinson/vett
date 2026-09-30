# Vett Implementation Notes

This document captures decisions that are too small for their own spec doc but too important to leave undocumented. If you're implementing Vett and have a "wait, what should this do?" moment, it probably has an answer here.

## Error handling philosophy

### Three layers of error

1. **Agent-visible errors** — things the LLM should see and react to. Tool call failures, malformed tool args, file-not-found, command-not-found, timeouts. These are returned as tool observations with the error text as the observation's content. The agent loop continues; the LLM gets to decide what to do next. Tools return these as `(error_string, nil)` from their Execute function.

2. **Loop-terminating errors** — things the agent loop can't recover from but the Vett run can continue. Sandbox died, sidecar crashed mid-run, container was killed externally. These abort the current instance with `end_reason: "error"` in the trace, but the next instance runs normally. Tools return these as `("", err)` from Execute.

3. **Run-terminating errors** — things that kill the whole `vett run`. Invalid config, docker daemon down, endpoint unreachable, out of disk. These exit the process with a non-zero exit code (see [cli-spec.md](cli-spec.md#exit-codes)).

**The rule:** every error is classified into one of these three layers. If you're unsure which, default to layer 1 (the LLM can probably handle it).

### Malformed LLM responses

When the LLM returns something that can't be parsed (invalid JSON, missing required fields, malformed tool call structure):

1. Log the raw response to the trace as an `llm_response` with `parse_error` field set
2. Treat it as a layer-1 error: synthesize an empty assistant message with a tool-role error observation like `Error: previous response was malformed and could not be parsed. Please try again.`
3. The loop continues. If the LLM malforms 3 responses in a row, count that as a stuck condition and terminate.

**No automatic retry of the LLM call.** That's too easy to get wrong (doubled tokens, lost context, infinite loops). The loop just adds an error observation and asks the model to try again naturally.

### Context cancellation

Every Go function that does I/O takes a `context.Context` as first argument. Contexts cancel when:
- The user hits Ctrl+C
- The instance exceeds `timeout_minutes`
- The run exceeds a higher-level cancellation
- The parent of a reentrant loop dies

When a context is cancelled:
- Tools currently running see the cancellation and should return promptly (within a few seconds max)
- In-flight sidecar ops are aborted — SidecarClient sends a cancel message, sidecar kills the bash job
- In-flight HTTP requests to the LLM are cancelled
- The loop exits with `end_reason: "cancelled"`
- Cleanup (sidecar shutdown, container kill, trace flush) still runs

**Never ignore context.Done() in long-running code.** Every loop, every select, every wait must check for cancellation.

## Config loading precedence

Profiles and suites load from this order (later wins on name collision with a warning):

1. Explicit path: `--profile ./my-profile.yaml` — highest priority, no search path
2. Working directory: `./profiles/<name>.yaml`
3. User config dir: `$VETT_CONFIG_DIR/profiles/<name>.yaml` (default `~/.vett/profiles/`)
4. Embedded: baked into the binary via `go:embed`

**On collision:** if `./profiles/openhands.yaml` and the embedded `openhands` profile both exist, the local file wins AND Vett logs a warning like `[config] profile 'openhands' has 2 sources: using ./profiles/openhands.yaml, shadowing embedded`. This is intentional — local overrides are a feature, but hidden overrides would be confusing, so they're always logged.

**Cross-reference resolution:** profile YAMLs can reference tools and middlewares by name. These resolve against the Go init-time registry (global, never profile-specific). If a profile references a tool that isn't registered, load fails with a clear error listing all registered tool keys.

## Concurrency model

`vett run --concurrency N` means: **N completely isolated instance runners in parallel**, each with its own container, its own sidecar, its own agent loop, its own task registry. Nothing is shared between them.

**Not:** "one Vett process with N goroutines coordinating on a shared task registry."
**Not:** "one container running N agents."

Each instance runner is independent:
- Fresh `sandbox.Sandbox`
- Fresh `sidecar.Client`
- Fresh `agent.Loop`
- Fresh `tasks.Registry` (for team profiles)
- Fresh event bus subscriber that writes to its own `instances/<id>.trace.jsonl`

A top-level `runner` orchestrator maintains a worker pool of size N and feeds instances to it. When one instance finishes, its runner reports to the orchestrator, which starts the next instance on that worker.

**Shared across workers:** profile/suite config (read-only), LLM endpoint (hit concurrently with retries), Docker daemon (Docker handles its own concurrency).

**Never shared:** trace files (each instance has its own), containers (one per instance), sidecars (one per container), task registries (one per agent loop).

If `--concurrency > 1` and the LLM endpoint can't handle it, that's a runtime error from the endpoint, not a Vett bug. Vett retries per the profile's `num_retries` setting but doesn't throttle globally.

## Middleware scope in reentrant loops

When an agent dispatches a sub-agent, the sub-agent runs its own agent loop with a fresh middleware chain constructed from the **sub-agent's profile (role config)**, not the parent's.

**Each middleware instance is tied to one agent loop.** Parent and child have separate StuckDetector instances that track separate histories. This means:
- Parent's stuck-detection state doesn't flow to child
- Child's middleware can't see parent's conversation
- Each sub-agent's middleware publishes events with its own `depth` field so consumers can filter

**Why this way:** the alternative (shared middleware instances across parent and child) creates subtle bugs where a child's failure trips the parent's stuck detector. Keeping them separate matches what the agent loop models naturally.

**Cost:** middleware construction happens on every reentrant dispatch. Cheap because middleware instances are small Go structs. A fresh chain for each dispatch is fine.

## Graceful shutdown

When Vett receives SIGINT (Ctrl+C) or SIGTERM:

1. **Immediately** set a shutdown flag in the top-level runner
2. **Cancel the root context** — this propagates to all running instances, sidecar ops, LLM calls
3. **Give instances 30 seconds to exit cleanly** — they flush their trace files, kill containers, destroy sidecar sessions
4. **After 30 seconds**, force-kill remaining containers via `docker kill`
5. **Write an incomplete `summary.json`** marking the run as "cancelled" with partial results
6. **Exit with code 130** (standard Unix Ctrl+C exit code)

**What gets cleaned up:**
- Running containers are killed (they auto-remove thanks to `--rm`)
- Sidecar subprocesses exit when their stdin closes
- Trace files are flushed and fsync'd
- The orchestrator writes a final `run_end` event to the run trace

**What doesn't get cleaned up:**
- Partially written patches — they're in memory and are lost on cancellation
- Temporary files the agent created in `/tmp` inside the container (container dies, they go with it)

**Second Ctrl+C:** if the user hits Ctrl+C again during the 30-second grace period, Vett skips graceful shutdown and force-exits immediately. This is the "I'm really serious" escape hatch.

## Token counting

Tokens are tracked in two places:

1. **LLM response** — the vLLM endpoint returns `usage.prompt_tokens` and `usage.completion_tokens` in each response. Vett reads these and records them in `iteration_end` events.
2. **Local token estimation** — NOT done in Phase 1. If we later want to count tokens before sending (for pre-flight budget checks or compaction triggers), we'd add a tokenizer dependency. For now, trust the endpoint.

The trace's `input_tokens` and `output_tokens` fields are always what the endpoint reported, not an estimate.

## Empty patch handling

When `git add -A && git diff --cached` returns no output at the end of a run:

1. Record a `patch_generated` event with `patch_chars: 0, files_touched: []`
2. Write the instance's `end_reason` as whatever caused the loop to exit
3. Set `patch: ""` in the instance's result
4. The suite's scorer sees an empty patch and produces `resolved: false` with reason `empty_patch`

Not a failure in itself — it's a legitimate outcome if the agent never made changes. The summary reflects it as "completed, not resolved."

## File editor encoding

**Phase 1: UTF-8 only, fail loud on anything else.**

When reading a file, the sidecar attempts UTF-8 decode. If the file has non-UTF-8 bytes, return an error like `Error: file /testbed/foo.bin is not valid UTF-8 — binary files are not supported`. The LLM sees the error and can decide to skip the file.

When writing a file, Vett always writes UTF-8. If the `file_text` arg from the LLM contains surrogates or invalid sequences (shouldn't happen through JSON unmarshaling), fail loud.

**Not Phase 1 but on the roadmap:** full encoding detection (UTF-8 BOM, latin-1, etc.) matching openhands-tools' `EncodingManager`. Deferred because SWE-bench Python repos are ~99% UTF-8 and handling edge cases early wastes time.

## Working directory semantics

Each session has a persistent working directory. When a session is created, its cwd is set to whatever the `session_create(name, cwd)` call specified. After that:

- Every `bash_exec` runs in the session's current cwd (whatever bash considers "PWD" at the moment)
- If the LLM's command includes `cd /somewhere`, the cwd changes for subsequent commands in that session
- The session's cwd is reported in the `bash_exec` result's `cwd` field so Vett knows where bash is
- The session's cwd does NOT reset between tool calls (persistent bash semantics)

**For openhands profile specifically:** the default session starts with `cwd=/testbed`. If the LLM runs `cd /tmp && ls`, subsequent commands run in `/tmp` until the LLM `cd`s back. This matches openhands-sdk behavior.

**Vett never prepends `cd` to user commands.** See [sidecar-protocol.md](sidecar-protocol.md#bash_exec) for the raw passthrough rule.

## Docker image management

Vett does NOT pull images automatically. If an instance's image isn't available locally, the run fails with a clear error and a suggested `docker pull` command. This prevents:
- Silent long waits while Docker downloads 10GB images
- Confusing failures when the network is down
- Disk exhaustion from runaway pulls

The exception is `vett preflight`, which can optionally run `docker pull` for a few common images as part of its setup verification. This is opt-in with `--pull-images`.

## Test fixture images

Integration tests that need a container use a minimal fixture image: `vett-test:alpine-3.19` or similar. This is a small Alpine image with bash + coreutils + Python3 installed, good enough for the test suite. Heavier tests use real SWE-bench images when available.

The fixture image is built by `testdata/build-fixtures.sh` and pinned to a specific tag in tests. Never use `:latest`.

## Version discipline

- **Vett binary version:** from git commit + build tag. Embedded via `go:embed` or `-ldflags`.
- **Sidecar binary version:** baked into the sidecar binary at build time. Host checks via the `hello` op and refuses to proceed on version mismatch.
- **Protocol version:** see [sidecar-protocol.md](sidecar-protocol.md#version-negotiation).
- **Trace format version:** per-event `v` field, see [trace-format.md](trace-format.md#versioning).

When anything breaks compatibility, bump the relevant version number and add a note to the project CHANGELOG.

## The "don't optimize yet" list

Things Vett does inefficiently on purpose because the simpler version is fine:

- Docker CLI shell-out instead of Docker API library (simpler, one fewer dep, Docker's CLI is stable)
- Unsigned tar transfers for sidecar binary deployment instead of image-based (simpler, no registry)
- Single goroutine per instance for LLM calls (no pooling — requests aren't the bottleneck)
- No HTTP keep-alive pooling in the LLM client (one client per instance, restarts are cheap)
- JSON everywhere (no protobuf, no MessagePack — human-readable wins for debugging)
- Trace files are JSONL on disk, not a database (grep works)

If any of these become real bottlenecks, revisit. Until then, resist "optimizing" them.

## Things decided during Phase 1

Phase 1 implementation resolved the deferred questions and surfaced a few
new ones:

- **Go module path:** `github.com/msdickinson/vett/sidecar` (resolved 1a).
- **Cobra command structure:** flat — `vett run`, `vett preflight`,
  `vett profiles show X`, `vett suites show X`, `vett version`. No
  sub-sub-commands yet (resolved 1d).
- **Event bus API:** channel-based, one channel per subscriber, per-
  subscriber lossy/non-lossy policy chosen at Subscribe time. `trace.Writer`
  is the canonical non-lossy subscriber (resolved 1c).
- **Reentrant loop goroutines:** deferred. Phase 1 ships the `Registry`
  skeleton in `internal/agent/tasks/` but no dispatch tools or sub-agent
  execution yet. When Phase 2 teams work begins the choice will be
  one-goroutine-per-sub-agent (simpler) unless benchmarks show it's a
  bottleneck.

## Deferred items surfaced during Phase 1

These landed in the docs from code-level discoveries and still need
doing. None block Phase 1 end-to-end; all are parked for later phases.

1. **Bash session survives timeout.** Currently a `bash_exec` timeout
   kills the bash child along with the foreground job because SIGINT
   to the pgid takes bash with it in non-interactive mode. Phase 1b
   workaround: mark the session dead on timeout, agent loop creates a
   fresh session. Proper fix is `set -m` + per-job pgid tracking.
2. **File editor `insert` / `undo_edit` tool parity.** The sidecar
   implements them; the thin `tool.Sandbox` interface doesn't expose
   them yet; the tool returns placeholder observations. Extend the
   interface in Phase 2 when an instance actually exercises them.
3. **File editor response string parity (byte-exact) with openhands.**
   Phase 1 ships ~90% parity. Envelope strings (`"The file X has been
   edited..."` etc.) roughly match; golden test is deferred to Phase 2.
4. **`LlmSummarizingCondenser`.** Stubbed by absence in Phase 1 per
   handoff. Phase 3 feature. Only matters on 200+ event runs.
5. **HuggingFace dataset loader.** Phase 1 uses `jsonl` loader only.
   HuggingFace requires a parquet dep we didn't take. Phase 3.
6. **`--concurrency > 1` in `vett run`.** Phase 1 ships single-instance
   runner. Per-instance isolation is architected (fresh sandbox +
   sidecar + loop per worker) but not wired up. Phase 3.
7. **`vett results` and `vett verify`.** Phase 4 per handoff.
