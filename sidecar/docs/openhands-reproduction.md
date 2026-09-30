# OpenHands Reproduction Effort — Status & Playbook

## The high-level goal

Vett is our benchmark engine. The `openhands` profile inside Vett is supposed to
**bit-for-bit reproduce** the real `openhands-sdk` harness running against
`qwen3-coder-next` on SWE-bench Verified. The test is simple: on the same model,
same endpoint, same instances, Vett and openhands-sdk should produce
statistically equivalent pass rates. If they do, Vett is a valid drop-in that
can run our six other benchmark suites — which is the whole point of Vett.

Why bother reproducing openhands specifically? Because openhands is the
published reference and we have a real ground-truth run of it on the runner box at
`~/openhands-eval/benchmarks/results/openhands-full-500-backup-django11119/`.
That's our "control harness" — what we're trying to match, not beat.

## The two things that could be wrong

1. **Vett's wire format** — what Vett sends to vLLM differs from what
   openhands-sdk sends. Different bytes on the wire → different model behavior
   → different patches.
2. **Vett's runtime** — tools, sandbox, middleware, patch extraction. Even with
   identical requests, if the tool results we feed back differ from what
   openhands feeds back, the conversation diverges.

Everything we've been fixing has fallen into one of these two buckets.

## Architecture in one paragraph

Vett (.NET 10) opens a Docker container for each SWE-bench instance, copies a
Go sidecar binary in, and runs an agent loop: LLM → tool calls → sandbox exec →
observation → LLM. The sidecar is a long-lived in-container process that
provides persistent bash state and file_editor operations over a newline-JSON
RPC protocol on stdin/stdout. Tools are defined in `tools/*.cs|*.yaml|*.go`.
Middleware pipelines (`middleware/*.cs`) shape the message list before/after
each LLM call. Profiles (`profiles/*.yaml`) wire it all together. At finish,
`PipelineRunner.GeneratePatchAsync` runs `git add -A && git diff --cached` in
the container and that's the submission.

## How we compare to the control

Three signals, in order of strength:

1. **Bit-exact wire format** — captured via `VETT_DUMP_FIRST_REQUEST=...` env
   var (dumps Vett's first outgoing request) vs a litellm monkey-patch that
   captures openhands-sdk's request. `python3 /tmp/diff.py` compares them
   tool-by-tool. **This is the strongest signal** because if it matches, any
   remaining difference lives in the runtime.
2. **Tool-call trajectory shape** — for the same instance, does Vett's
   sequence of tool calls roughly track ref's trajectory? Ref's trajectory
   JSONL is already saved; `~/openhands-eval/benchmarks/results/.../output.jsonl`.
3. **File-match rate** — of the 5 instances we sample, how many patch the
   same source file that ref patched? This is the loose proxy for "are we
   at least editing the right code".

**The signal we don't yet have**: ref's actual pass rate on these instances.
The ref run only saved patches — the SWE-bench test-runner hasn't been run on
them so we don't know if ref even resolves them. This is the #1 thing we
should measure before iterating further (see "What to do next").

## Log of what changed and why

Each fix was motivated by an observed bytewise diff against ref or a concrete
failure mode in a trace. Categorized:

### Bucket A: Real bugs in Vett (fixes nothing in ref would have either)

- **Bash envelope leaked into patches** — sidecar's `bash_exec` AND
  `YamlToolLoader.cs` both wrapped output in `[Current working directory: X]\n
  [Command finished with exit code N]`. LLM saw DOUBLE envelope. Worse,
  `git diff --cached` output was getting polluted, corrupting every patch.
  Fix: sidecar returns raw stdout, YamlToolLoader is the sole envelope source.
- **Conda env not activated** — SWE-bench images ship a `testbed` conda env
  with the repo's deps installed. Without activating it, `python` resolved to
  base env (missing erfa, numpy, etc.) and the agent burned 15 iterations on
  `pip install` fumbling. First fix tried `conda activate testbed` but failed
  silently; switched to deterministic `export PATH=/opt/miniconda3/envs/testbed/bin:$PATH`.
- **Sidecar binary CRLF corruption** — transferred the Go binary via
  `ssh runner "cat binary" > local_file` which Git Bash on Windows applied text
  conversion to, breaking the ELF header. Fixed by using tar-pipes (binary-safe).

### Bucket B: Matching ref's wire format

Captured ref via litellm monkey-patch on 2026-04-15, diffed bit-for-bit:

- Messages are structured content `[{text, type}]` (we were sending plain strings)
- Content-part key order is `{text, type}` alphabetical
- Stripped `max_tokens`, `parallel_tool_calls` from outgoing request (ref omits)
- System prompt trailing `\n` stripped (`|` → `|-` in YAML)
- Tool list order `[terminal, file_editor, task_tracker, finish, think]`
- `file_editor` description suffix added
- `task_tracker` description replaced with full 5052-char V1 version
- `finish`/`think`: no `security_risk` required, `summary` optional, finish desc `\n`
- **Temperature 1.0** (metadata.json canonical) — we had 0.6 from a stale snippet

### Bucket C: Runtime workarounds for qwen3-coder quirks

- **Hallucinated tool names** — qwen3 sometimes emits `name=str_replace` with
  a file_editor-command shape instead of `name=file_editor, args.command=str_replace`.
  Added `RemapHallucinatedToolNames` in AgentLoop to fold these back.
- **Stuck-loop nudging** — instead of StuckDetector terminating on first
  repeated action, inject a "stop repeating, pivot" message into the last
  tool result and give the model one more chance. Terminate only on second trip.

## Progress trajectory (honest)

| Run | What changed | Patch quality | Convergence signal |
|---|---|---|---|
| v-10..12 | Env activation dance | Empty | Still chasing startup bugs |
| v-13 | Schema diffs | 1 malformed call | Getting into model's space |
| v-14 | V1 schemas | 1 patch, wrong file, double envelope | First real edit |
| v-15 | Envelope fix | 2 patches, 1 right file | Real diffs flowing |
| v-16 | temp=1.0 | 1 patch, different right file | Sampling variance visible |
| v-17 | Scenario-2 nudge | (mid-run) | — |

Monotonic-ish improvement in patch quality, but we have **never passed a
single test** yet. Pass-rate has been stuck at 0/N.

## Am I in a rabbit hole?

Honest self-assessment:

- **Bucket A fixes were real bugs** that would break any run. Those weren't
  rabbit hole — those were debt we had to pay.
- **Bucket B fixes are ref-matching work**. The per-fix value drops as we
  approach bit-exact. At the point we are now (all 5 tools match), Bucket B
  is almost done. Any remaining Bucket B diffs are diminishing returns.
- **Bucket C workarounds are risk**. Each one is a judgment call. The
  hallucinated-name remap is cheap and clearly right. The stuck-nudge is
  more speculative — openhands may handle this via compaction instead of
  prompting.
- **What I have NOT measured**: whether ref itself passes these 5 instances.
  If ref gets 0/5 on astropy subset, I'm comparing to a broken target and
  chasing ghosts. This is the biggest unknown and I should fix it next.

## What to do next

1. **Run ref's saved patches through the SWE-bench test harness** to get
   ref's actual resolution rate on our sample. Without this baseline, any
   pass-rate comparison is meaningless. The patches are at
   `~/openhands-eval/benchmarks/results/openhands-full-500-backup-django11119/.../output.swebench.jsonl`
   — they can be scored via the standard `swebench.harness.run_evaluation`
   pipeline in a docker container.
2. **Fix the 30-min step timeout**. Ref uses `maxiter_300` with no
   wall-clock ceiling; Vett caps at `timeout_minutes: 30` per step and
   validation-17 shows instances hitting the wall before producing patches.
   Either raise the ceiling to something reasonable (60+ min) or remove it
   and let `max_iterations: 100` be the only bound.
3. **Scale the sample**. 5 instances at temp=1.0 is too noisy; run 25-50
   to get a stable pass-rate and file-match number.
4. **Stop condition**: Vett's pass rate on a 25+-instance sample lands
   within ±5% of ref's on the same instances.

## Where the files are

| What | Where |
|---|---|
| Profile | `Vett/profiles/openhands.yaml` |
| Tool schemas | `Vett/tools/{terminal.yaml,file-editor.cs,task-tracker.cs,think.cs,finish.cs}` |
| LLM client (wire format) | `Vett/src/Vett/LlmClient.cs` |
| Agent loop (tool dispatch + alias remap) | `Vett/src/Vett/AgentLoop.cs` |
| Sandbox + sidecar launch | `Vett/src/Vett/Sandbox/DockerSandbox.cs` |
| Go sidecar | `Vett/src/Vett/sidecar/{main.go,bash.go,file_editor.go}` |
| Middleware pipeline | `Vett/middleware/*.cs` |
| Stuck detector (with nudge) | `Vett/middleware/stuck-detector.cs` |
| Patch extraction | `Vett/src/Vett/PipelineRunner.cs` `GeneratePatchAsync` |
| Reference ground truth | runner box `~/openhands-eval/benchmarks/results/openhands-full-500-backup-django11119/` |
| Captured ref request | runner box `/tmp/ref-request.json` |
| Vett captured request | runner box `/tmp/vett-first-request.json` (run with `VETT_DUMP_FIRST_REQUEST=...`) |
