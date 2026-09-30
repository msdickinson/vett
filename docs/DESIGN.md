# VETT Design

VETT is one agent loop — prompt the model, parse its tool calls, run them in a
sandbox, append the results, repeat until the model calls `finish` — reused
across four workflows that would otherwise each need their own copy of that
loop: grading a model against SWE-bench, running a custom benchmark suite,
driving an interactive chat session against a real codebase, and coordinating
a team of agents through a shared task board. The bet is that owning one loop
and reusing it everywhere is worth more than four loops each optimized for
one job. When a tool schema changes, a retry policy gets smarter, or context
compaction improves, every workflow inherits it at once.

This document explains the *why* behind the structural decisions, not just
the *what*. The *what* is in the code, which is commented more heavily than
usual precisely because the reasoning kept getting rediscovered the hard way.
Where a decision came from a real incident, the incident is described,
including the times it went wrong — this is meant to be a useful account, not
a sales page.

## Architecture at a glance

```
                          ┌─────────────────────────┐
                          │        vett CLI          │
                          │  run / chat / bench /     │
                          │  capacity / validate /…   │
                          └─────────────┬─────────────┘
                                        │
                     ┌──────────────────┼──────────────────┐
                     │                  │                  │
             ┌───────▼───────┐  ┌───────▼────────┐  ┌──────▼───────┐
             │   AgentLoop    │  │ TeamCoordinator │  │ LiveServer    │
             │ (single agent) │  │ (leader + N     │  │ (SSE /events, │
             │                │  │  members, task  │  │  healthz)     │
             │                │  │  board)         │  │               │
             └───────┬────────┘  └───────┬─────────┘  └───────────────┘
                     │                   │
        ┌────────────┼───────────────────┼─────────────────┐
        │            │                   │                 │
 ┌──────▼─────┐ ┌────▼─────┐   ┌─────────▼─────────┐ ┌─────▼──────┐
 │ Middleware  │ │ Tools /  │   │ DispatchWorktree   │ │ CapacityBroker│
 │ (compaction,│ │ Plugins  │   │ Manager (git        │ │ (cross-process│
 │  permissions,│ │(YAML +  │   │ worktree per        │ │  lease ledger,│
 │  stuck/      │ │ subprocess│  │ dispatch)           │ │  budgets)     │
 │  retry)      │ │ protocol) │  └─────────────────────┘ └───────────────┘
 └──────┬──────┘ └────┬─────┘
        │             │
        └──────┬──────┘
               │  JSON-RPC over stdin/stdout
        ┌──────▼──────────┐
        │  vett-sidecar     │   (Go binary, runs inside the
        │  (bash + file ops)│    container / worktree)
        └───────────────────┘
```

The CLI is a thin dispatcher (`src/Vett/Cli/RunCommand.cs`,
`ChatCommand.cs`, `BenchCommand.cs`, `TeamBenchCommand.cs`,
`CapacityCommand.cs`, `ValidateCommand.cs`, `AnalyzeCommand.cs`,
`ReplayCommand.cs`, `CallCommand.cs`, `EditCommand.cs`,
`EscalationLedgerCommand.cs`) over two real engines: `AgentLoop` for a single
agent and `TeamCoordinator` (`src/Vett/Agent/Coordinator.cs`) for a
leader coordinating members through `TaskBoard`
(`src/Vett/Agent/TaskBoard.cs`). Both engines share the same tool and
middleware plumbing and the same sandbox abstraction underneath.

## Core principles

`SPEC.md` states six ground rules up front, and the codebase holds to them
closely enough that they're worth repeating verbatim in intent:

1. No unnecessary abstractions — no interfaces with a single implementation,
   no registries, no adapter layers.
2. Constructor injection everywhere, no global state.
3. Events are one `Action<Event>` callback, not a pub/sub bus.
4. Two projects only — `Vett` and `Vett.Tests` — no `Vett.Core` /
   `Vett.Abstractions` split.
5. A Go sidecar handles the sandbox, talking JSON-RPC over stdin/stdout.
6. Plugins (tools and middleware) are a subprocess protocol — JSON in, JSON
   out, any language — not a compiled extension model.

The practical effect shows up everywhere: tools and middleware are plain
delegates (`ToolFn`, `MiddlewareFn`) registered into dictionaries, not classes
implementing an interface; the sandbox has an `ISandbox` surface but only two
real shapes behind it (local direct-bash, and the Dockerized sidecar); a
member agent inside a team is *the same* `AgentLoop` as the top-level one,
just with a different tool set and a worktree wrapped around it. There's no
separate "team agent" class. This matters more than it sounds — it's the
reason team orchestration, chat, and benchmark grading didn't fork into three
codebases.

## Why an OpenHands baseline profile

The bundled `openhands` profile (`profiles/openhands.yaml`) renders a system
prompt that is byte-identical to OpenHands SDK 1.14.0, verified against a
captured reference (`testdata/ref-system-prompt.txt`). The point isn't to
imitate OpenHands for its own sake — it's to have a fidelity baseline. If
VETT's numbers on a benchmark differ from OpenHands' published numbers, the
difference should be attributable to the engine (the agent loop, the sandbox,
the model serving) rather than to a slightly different prompt, a different
tool description, or a different default iteration budget quietly changing
what's being measured. Comparable results require holding the variables that
aren't the thing you're testing genuinely constant, and the system prompt is
the biggest one.

This took more than copying a prompt string. The default `max_iterations:
500` and `timeout_minutes: 240` in the profile aren't arbitrary — they were
set after reading OpenHands' own SWE-bench runner source and confirming its
actual default, because grading two harnesses under different iteration
budgets isn't a fair fight even if every prompt word matches. The profile's
own description is explicit about what still isn't identical (no
LLM-summarizing context condenser, no equivalent finish-critic, no terminal
soft-timeout semantics) — it targets "matches OpenHands within stated
bounds," not indistinguishable at the wire level. See
`docs/openhands-fidelity.md` for the specifics of what was compared and how.

## Why a Go sidecar over JSON-RPC

The part of VETT that actually touches an untrusted codebase — running shell
commands, reading and editing files inside a SWE-bench container or a git
worktree — is a separate Go binary (`sidecar/`, built as `vett-sidecar`) that
the .NET host talks to over newline-delimited JSON on stdin/stdout
(`sidecar/docs/sidecar-protocol.md`). A few reasons this is a process
boundary and not a library call:

- **A small, static, cross-platform binary is what you want running inside a
  container.** No .NET runtime, no dependency tree, negligible cold-start —
  it starts, says `hello`, and serves `bash_exec` / `file_view` /
  `file_str_replace` / etc. against one or more bash sessions.
- **The raw-passthrough invariant is easier to hold at a process boundary.**
  The protocol spec calls this out explicitly: the command string the agent
  sends reaches bash byte-for-byte — the sidecar wraps it only for output
  framing (`{ cmd; } 2>&1` plus a sentinel `printf` to capture exit code and
  cwd), never for escaping or rewriting. A test (`TestBashPassthroughIsBitExact`)
  locks this so it can't regress silently.
- **Sessions are isolated and independently concurrent.** Each named bash
  session has its own child process and its own request queue; requests
  *within* a session run serially (one bash child, one command at a time),
  but different sessions run concurrently — which is exactly the shape a team
  of agents dispatched into the same container needs.
- **It has a second life as a reference implementation.** An earlier,
  Go-only version of VETT existed before the current C# harness and had
  already passed easy SWE-bench instances cleanly. Its sidecar code was
  carried forward rather than rewritten, and — when the C# port misbehaves —
  diffing against that earlier implementation's behavior is still part of how
  bugs get tracked down. Inheriting working code beat reinventing it.

## Agent teams: leader, members, and a shared task board

A "team" in VETT is a leader agent with extra tools
(`src/Vett/Agent/LeaderTools.cs`) — `assign_task` (synchronous, blocks for a
result), `assign_async` (fire-and-forget, poll later), `continue_task`,
`check_task`, `check_tasks`, `wait_task`, `cancel_task`, `inject_into_task`,
and `declare_done` — plus a `TaskBoard` both sides read and write. The board
(`src/Vett/Agent/TaskBoard.cs`) is a small, thread-safe state machine:
`TryCreate` reserves a task id atomically (so two racing `assign_async` calls
can't double-book one id), `Complete`/`Fail` resolve it, and
`ResolveTaskId` does fuzzy matching against in-flight ids so a leader that
slightly misremembers an id (a real failure mode — models hallucinate ids)
can still be routed correctly instead of erroring out.

Members can themselves declare a `team:`, becoming leaders of their own
sub-team nested inside the parent dispatch — Tier 3. Nesting is capped
(`MaxDispatchDepth = 2`, so an agent chain can run leader → member →
sub-member → sub-sub-member, four levels deep) and width is enforced
separately per team (`TaskBoard.MaxConcurrentDispatches`, required — absent
config is a hard error, not an unlimited default). Depth and width are kept
as two different failure modes on purpose: exceeding depth is a structural
violation and throws; exceeding width is a normal "try again" refusal the
leader can react to. Collapsing them would mean a leader could no longer tell
"you asked for something the topology doesn't allow" from "the team is just
busy right now."

Team orchestration exists because real work fans out — this is also how
VETT gets used against its own codebase by an internal orchestrator that
dispatches multi-step engineering tasks, which is where a lot of the
multi-level nesting and capacity-contention edge cases below were first
found, not invented in a test.

## Worktree-per-dispatch

When a member runs inside a team, it doesn't share the leader's working
directory. `DispatchWorktreeManager` (`src/Vett/Agent/DispatchWorktreeManager.cs`)
provisions an isolated `git worktree` per dispatch at
`~/.vett/dispatches/<panelId>/<taskId>/` on a branch
`vett/dispatch/<panelId>-<taskId>`. The lifecycle is Create → Capture →
Apply/Discard: the member does its work in complete isolation, the manager
captures the resulting diff (`git diff` + a stat summary) when the member
finishes, and the leader reviews that diff and calls `accept_dispatch` (which
applies it onto the shared parent tree) or `reject_dispatch` (which discards
it). This is what makes concurrent members safe — five agents editing the
same repository concurrently, in the same working tree, would corrupt each
other's changes; five agents each in their own worktree can't.

A few details exist because the naive version broke in practice:

- **Sparse checkouts** (`VETT_DISPATCH_SPARSE`) cut provisioning cost sharply
  on large repositories — checking out only the directories a member's task
  actually touches instead of the whole tree, with a full-checkout fallback
  if the sparse set can't be determined.
- **A concurrent fan-out retry with jittered backoff** exists because
  provisioning several worktrees against the same repo at once can race git's
  own metadata: under concurrency, some full-suite runs would intermittently
  fail with `fatal: failed to read .git/worktrees/<name>/commondir` — a
  transient race, not a real conflict — and now get retried instead of
  killing the dispatch.
- **`IsLiveWorktreeDir` guards against deleting a concurrent run's live
  worktree** during pre-clean/collision recovery — an early version of the
  collision-recovery path was capable of deleting another in-flight member's
  directory out from under it, which is about as bad as a bug in this system
  gets (see Lessons Learned).
- **Windows reserved device names** (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`,
  `LPT1-9`) are detected and worked around during capture, because a
  generated file literally named `nul` poisoned an entire `git add -A` for
  one team's capture step on Windows.

## Gates: "declare done needs evidence"

The single idea that shapes `declare_done` the most is: a model's self-report
that it finished is not evidence — the diff is. `declare_done`
(`src/Vett/Agent/LeaderTools.cs`) checks, in order: whether any tasks are
still in flight (can't finish while work is running), whether every path
named by `VETT_REQUIRED_PATHS` actually shows as touched in
`git status --porcelain` (parsed properly, not substring-matched — an earlier
version could be fooled by a path that was merely *mentioned* in an unrelated
diff line), whether every substring named by `VETT_REQUIRED_CONTENT` actually
appears in the *added* lines of `git diff HEAD` (again, not a whole-diff
substring check — that version could be satisfied by content that had just
been *deleted*), and finally an optional `VETT_VERIFY_CMD` — a build or test
command that has to exit clean.

A refusal budget (`MaxDeclareDoneRefusals = 10`) caps how many times the gate
can say no before the leader is told to stop retrying and report the
blockage honestly instead of looping forever against a gate it can't satisfy.

The gates also **fail open on a measurement failure** — a non-git workspace,
a `git` command timing out — rather than blocking forever, but a failed
measurement is never silently reported as "satisfied." It's tagged
`[gate-failed-open: ...]` so a human or a downstream process can tell "we
checked and it passed" apart from "we couldn't check, so we let it through."
This distinction — *could-not-measure is not measured-zero* — turns out to be
one of the most load-bearing ideas in the whole codebase; it recurs in the
dispatch-capture path, the token-counter fields on every dispatch event, and
the capacity ledger's stale-reading handling, and is worth naming once here
because the failure it prevents is subtle: code downstream tends to treat a
zero as "nothing happened, safe to clean up," and a failed measurement
dressed as zero can trigger exactly the wrong cleanup.

## Capacity broker: multi-endpoint, prefix-cache-aware scheduling

Running a team of agents — or a `vett run` and a `vett chat` at the same time
— against a small number of local GPUs means something has to decide who
gets how much context, on which endpoint, without either starving a session
or silently over-committing the server's KV cache. `CapacityBroker`
(`src/Vett/Capacity/CapacityBroker.cs`) is a small cross-process ledger for
this: a directory of lease files guarded by a lock file, no daemon, no port.

Two things make this necessary rather than decorative:

- **In-process semaphores don't see each other.** Every existing
  concurrency gate in VETT is a `SemaphoreSlim`, which is invisible across
  process boundaries — a benchmark sweep and an interactive chat session
  have no way to know about each other's GPU usage unless something outside
  either process tracks it.
- **The inference server can't do this job for you.** A vLLM server run
  without a hard admission reservation doesn't refuse work when its KV cache
  is full — it preempts (evicts and recomputes), so over-subscription shows
  up as a latency collapse, not an error you can catch and back off from.
  Reservations have to be *declared* by clients up front; they can't be
  inferred from server behavior after the fact.

A `Capability` (`src/Vett/Capacity/Capability.cs`) is a named thing a profile
can ask for — "flash," "pro" — resolved to a concrete `CapabilityProvider`
(an endpoint + model + locality) only at claim time, not baked into the
profile at authoring time. This indirection is what "multi-endpoint,
prefix-cache-aware" buys: a profile that hard-codes an endpoint has already
made a locality decision months before anyone knows what's actually free;
a profile that states a capability lets the broker route it to whichever
real endpoint has room right now, and keeps requests for the same capability
pinned toward the same endpoint so its KV prefix cache stays warm instead of
scattering identical prefixes across machines that each have to recompute
them. Substitutability is evaluated *per request size*, not per capability —
a capability with both a local and a cloud provider only has two real doors
at sizes the local provider's context window can actually serve; past that,
the same capability is effectively cloud-only, and the broker treats it that
way rather than quietly handing back a smaller window than was asked for. A
capability with no local provider at any size (like "pro" in the bundled
catalogue) always waits for cloud capacity rather than silently downgrading
to a cheaper model — answering a different question than the one asked, even
correctly, is treated as a bug.

Money is a separate axis from context: cloud providers draw on named
`Budget`s tracked in dollars, gated only against a recorded billing reading
(`RecordSpend`, with a required `source`) — never estimated from logged
token counts times a list price, because cache discounts break that
correspondence unevenly enough that the error doesn't even cancel out across
runs. A budget with no declared limit is unbounded, matching the pre-budget
behavior; once a limit *is* declared, the gate fails closed — a spend gate
that admits work when it can't see the balance isn't a spend gate.

Honestly: priority-based preemption in the broker is further along in
"detected and logged" than in "enforced." The broker can correctly select
and mark a lower-priority lease for preemption, and a human can see that mark
via a status command — but nothing in the production path currently reads
that mark and acts on it (the one production caller of the heartbeat path
discards it). What ships today is correct victim *selection* and a visible
note; actually vacating a preempted seat is a wiring gap, documented as one
in the code rather than glossed over.

## Configuration: YAML profiles, suites, capabilities — strict by default

Everything that shapes a run is YAML: `profiles/` (model, sampling
parameters, tool list, middleware list, system/user prompt files),
`suites/` (which instance set, how to load it, what Docker image template
renders per instance — see `suites/swe-bench-verified.yaml`),
`capabilities/` (the broker's catalogue), and `pipelines/` for multi-phase
runs. The consistent rule is that an absent required key is an *error*, not
a silently-applied default. A team node without `max_concurrent_dispatches`
set doesn't get an implicit "unlimited" — it fails to load. A profile
missing its LLM endpoint or model doesn't get a generic HTTP 401 five steps
into a run — validation names exactly which field is missing before the run
starts. The `capabilities/default.yaml` catalogue's comments are themselves
a good demonstration of the failure mode this rule guards against: a probed
context-window value that goes stale after an unrelated server restart is
*worse* than no value, because it closes off real headroom (or opens
real over-commitment) while looking authoritative — the file documents
exactly that happening and being caught by a re-probe, not a stale line
quietly shipping forever.

Tools and middleware extend the same way: a plugin is a YAML manifest plus a
script in any language, talking a subprocess JSON protocol (one request,
one JSON response, on stdin/stdout — the same shape as the sidecar protocol,
deliberately), with a comment-based convention for declaring its schema and
SHA256-based build caching so a plugin with a compiled step isn't rebuilt on
every run it isn't touched. `vett` scans `<workdir>/plugins/` at startup and
the tool set is additive — plugin and MCP tools are never silently excluded
by a role's built-in tool whitelist.

## Middleware: compaction, permissions, and runaway detection

Middleware runs between agent turns and can rewrite state, and the built-ins
cover the three problems a long-running agent actually hits:

- **Compaction** (`src/Vett/Tools/CompactionMiddleware.cs`) has three
  strategies of increasing aggressiveness: observation elision (truncate old
  tool outputs, keep the message sequence), a milestone/LLM-summarizing
  checkpoint (collapse history into a summary block, keep the last few
  messages verbatim), and a full re-plan checkpoint (persist the whole
  conversation to a resumable JSONL, ask the model for a forward-looking
  handoff brief, then hard-reset context to just that brief — the mechanism
  that lets a very long autonomous run keep going without ever running out of
  context). Every strategy that clears history writes it to disk *first* —
  not because it's tidy, but because the earlier version of the milestone and
  LLM-condenser strategies didn't, and on a long run that meant the bulk of
  the actual work existed only as whatever survived into a short summary.
- **Permissions** (`src/Vett/Tools/Permissions.cs`) gate interactive chat
  sessions — reads and safe read-only terminal commands auto-allow, edits and
  unsafe terminal commands ask the user, and the "safe" classifier is a
  conservative allowlist (no shell metacharacters, a fixed set of read-only
  first tokens, sub-allowlisted verbs for `git`/`npm`/`docker`) rather than an
  attempt to parse shell syntax properly. Benchmark runs skip this system
  entirely — there's no one to ask.
- **Stuck detection** stops a monologuing agent (four or more consecutive
  text-only assistant turns with no tool call) and an agent stuck in a repeat
  error loop. The error-loop half of this shipped with a real bug: it counted
  individual failed tool *messages*, which works for an agent making one tool
  call per turn but not for a leader that fans out several `assign_async`
  calls in a single turn and then polls all of them in the next. Four
  benign "still running" poll results from one legitimate turn looked
  identical to four consecutive failures, and killed a leader mid-fan-out —
  discarding five members' worth of already-correct, already-diffed work
  because the leader that had to accept those diffs was dead before it got
  the chance. The fix required two changes, not one: count by *turn*, not by
  message, and stop treating "task still running" as an error at all.

## Live events: SSE stream consumed by AI Timeline

Passing `--live-port` starts `LiveServer` (`src/Vett/Live/LiveServer.cs`), a
small `HttpListener`-based Server-Sent-Events endpoint — deliberately not the
full ASP.NET Core stack, since `vett` is meant to stay a small console app.
`GET /events` streams every event the run emits as one JSON object per SSE
frame; `GET /healthz` is a liveness probe; `GET /` serves a minimal
self-contained HTML page that renders the stream directly in a browser, so
watching a run live doesn't require standing up a separate consumer.
**[AI Timeline](https://github.com/msdickinson/ai-timeline)**, a companion
public project, subscribes to this stream (or parses the same events after
the fact from each instance's `events.jsonl`) and renders Gantt, token, and
tool-pattern views of a run. Keeping the event schema and transport this
plain — one JSON object per line, one event type, no bespoke wire format —
is what let a visualization tool be built as a fully separate project instead
of a module living inside VETT itself.

## Benchmark grading: the gold-patch trust check

`vett run` against a suite like SWE-bench Verified doesn't grade itself —
it produces a `predictions.jsonl` in the exact shape the official SWE-bench
harness expects, and the actual PASS/FAIL verdict comes from running that
harness's own evaluation, executing the instance's real test suite inside a
fresh container and reading its structured output (`src/Vett/Runner/Evaluator.cs`
parses `PASSED <nodeid>` / `FAILED <nodeid>` lines from the test runner's own
summary, not a freeform-log heuristic). A candidate patch only counts as
resolved if **both** every `FAIL_TO_PASS` test and every `PASS_TO_PASS` test
comes back passing after the patch is applied — the model claiming success is
not part of that computation anywhere.

This matters because the model's own exit behavior is a weak signal on its
own: instances where the agent called `finish` confidently were not reliably
the same set as instances where the patch was actually correct, and
conversely, some instances that hit the iteration cap without the agent ever
calling `finish` still had a fully correct patch sitting in the diff. Grading
by actually running the tests is what catches both directions of that gap.
`ResolutionClassifier` (`src/Vett/Runner/ResolutionClassifier.cs`) sorts
every finished instance into one of five buckets — not modelable, timeout,
abstained, pass, or "false confidence" (a specific, named bucket for exactly
the case where the model sounded done and wasn't) — precisely so that a
summary number doesn't collapse "actually correct" and "sounded done" into
the same count.

On the 25 easiest SWE-bench Verified instances, running the `openhands`
profile against DeepSeek V4 Flash served with vLLM across two NVIDIA DGX
Sparks (tensor-parallel 2, five instances at a time, 500-iteration cap)
resolved **23/25 (92%)** in 54 minutes of agent time. Earlier runs used
Qwen3-Coder-Next FP8: one Spark running five instances at a time resolved **21/25 (84%)** at 100
iterations per instance, and **22/25 (88%)** at 200 iterations, for roughly
39% more wall-clock time. Two
Sparks running ten at a time — double the concurrency, same 100-iteration
budget — resolved **20/25 (80%)** in under half the wall-clock time of the
single-Spark run. These are the easiest 25 instances, not the full
benchmark, and each row is a single run; treat them as evidence the harness
works end to end, not as a leaderboard score.

## Lessons learned

A few incidents shaped the code more than any design session did, and left
comments at the site describing what actually happened:

- **A 120-second default tool timeout was too short for real SWE-bench
  commands**, and a `SIGINT` sent to the whole sandbox process group was
  killing the bash session itself rather than just a runaway child process
  inside it. Combined with a command-framing bug — wrapping the user's
  command as `{ cmd; }` on one line broke any heredoc ending in `EOF`,
  because the closing brace landed glued onto the heredoc's terminator line
  and made the whole thing invalid shell — the sandbox's bash session was
  dying repeatedly in production: 60 deaths across one 25-instance run.
  Diagnosing it needed a fourth fix first: capturing the dying shell's exit
  code, signal, and last command into the RPC error string, because until
  that existed the three failure modes were indistinguishable from each
  other in the logs. Fixing all three (timeout raised to 600s, signal scoped
  to the actual runaway child, framing fixed) took deaths from 60 to 0 on a
  repeat run, with no throughput cost.
- **A git worktree collision-recovery bug destroyed real work.** An early
  version of dispatch provisioning could, under certain collision conditions,
  delete a *concurrent* in-flight member's live worktree instead of only
  cleaning up its own stale ones — a single run lost 12 of 14 dispatched
  members' work this way. The fix added an explicit "is this worktree
  currently live" check before any collision cleanup touches a directory,
  and unique sibling paths so two dispatches never contend for the same
  directory in the first place.
- **A stale anchor turned compaction into a self-sustaining shredder.**
  Every context-clearing middleware strategy has to reset the token estimate
  it's comparing against the threshold after it rewrites the message list —
  miss that step and the estimator keeps reporting the *pre-compaction* size
  on every subsequent turn, so the threshold stays tripped and compaction
  fires again immediately, eating another window of conversation each time,
  forever, driven by a number that never gets corrected.
- **A context-window gauge was wrong by exactly 2x** in the interactive chat
  UI because it read a package default (131072) instead of the server's
  actual configured window (65536) — the gauge read half-full at the exact
  point the inference server started rejecting requests for being too long.
  The fix made an unmeasured window value explicitly distinguishable from a
  measured one, rather than letting a guess and a probe look the same.
- **Capacity preemption is honestly incomplete.** The broker can correctly
  decide which lower-priority lease should yield and mark it — that part is
  unit-tested and a human can see the mark — but nothing in production
  currently *acts* on that mark to actually free the seat. It's documented
  in the code as a known gap rather than presented as finished.

## Companion projects

- **[AI Timeline](https://github.com/msdickinson/ai-timeline)** (MIT) —
  visualizes a VETT run live via the SSE stream above, or after the fact by
  parsing `events.jsonl`.
- **[VETT Chat](https://github.com/msdickinson/vett-chat)** — a VS Code
  extension wrapping `vett chat --stdio` in a webview; same agent loop this
  document describes, used interactively instead of for grading.

## Credit

The `openhands` profile's system prompt and default benchmark parameters are
derived from [OpenHands](https://github.com/All-Hands-AI/OpenHands) (MIT
license), SDK version 1.14.0. See `docs/openhands-fidelity.md` for detail.

## License

MIT — see `LICENSE`.
