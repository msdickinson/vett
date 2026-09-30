# Future improvements (NOT for the openhands profile)

This file is a parking lot for ideas that might be worth implementing in a separate Vett profile (e.g., `vett-enhanced`, `vett-swe-bench`, `vett-swe-bench-team`) but that must NOT live in the `openhands` profile, because the openhands profile is a faithful mirror of openhands-sdk.

**Rule:** If it isn't in openhands-sdk, it doesn't go in the `openhands` profile. Ideas here are candidates for *other* profiles where their effect can be measured as a delta against the openhands baseline.

Every entry should eventually become a measured delta: "adding this to the `vett-X` profile changed the pass rate on instance set Y from Z% to Z+N%." Without that measurement, it's just speculation.

## Candidates

### Tool-name alias remapping

**What:** When the LLM emits a tool call with `name=str_replace` (a file_editor command) or `name=run` (a terminal alias), remap it to the correct tool name before dispatch.

**Why it might help:** qwen3-coder-next occasionally emits these malformations and loses a tool-call slot for them. Catching and remapping lets the call succeed instead of failing.

**Why it's not in openhands profile:** openhands-sdk doesn't remap. They either don't hit this pattern or they let it fail. Adding it to the openhands profile would silently improve Vett's results vs. openhands, poisoning the baseline.

**Candidate for:** `vett-swe-bench` profile (a Vett-branded variant of the openhands approach with measured tweaks).

**Expected delta:** small positive (~1-3% on pass rate). Measure once the baseline is established.

**Implementation sketch:** middleware that runs after the LLM call and before dispatch, rewrites tool calls with hallucinated names:
```
Hallucinated name → Correct tool
str_replace, view, create, insert, undo_edit → file_editor (with command=<name>)
run, execute_bash, bash, shell → terminal
```

### StuckDetector nudge-before-terminate

**What:** When StuckDetector fires, instead of terminating the loop immediately, inject a "you're repeating yourself, try a different approach" message into the last tool observation and give the model one more chance. Terminate only on second trip.

**Why it might help:** qwen3-coder-next sometimes gets stuck briefly and then recovers if nudged. openhands terminates on first trip which might end runs prematurely.

**Why it's not in openhands profile:** openhands terminates on first trip. Match exactly.

**Candidate for:** `vett-swe-bench` profile.

**Expected delta:** small positive (~2-5% on pass rate). Or possibly negative if the model's stuckness is terminal and nudging just wastes iterations. Measure.

**Implementation sketch:** replace the terminate-immediately behavior in StuckDetector with a state machine: first trip → inject nudge marker into last tool result, don't stop. Second trip (nudge marker already present) → stop for real.

### Native grep_files op in sidecar

**What:** Add a native Go op to the sidecar that walks the file system with `filepath.Walk` and applies a regex, returning structured results. Used by a `grep_files` tool instead of shelling out to `grep -rn`.

**Why it might help:** removes shell escaping risk for grep patterns (which often contain special characters). Faster on cold cache too.

**Why it's not in openhands profile:** openhands uses bash grep, not a native op. Match exactly.

**Candidate for:** `vett-swe-bench` profile or as a neutral infrastructure feature if we can prove it doesn't affect LLM-visible behavior.

**Note:** this is borderline. It doesn't change what the LLM sees as long as the output format matches bash grep's output format. If byte-for-byte output matching is maintained, this might actually be safe in the openhands profile. Needs investigation.

### Native git_diff op in sidecar

**What:** `git_diff` as a native sidecar op, used for patch extraction instead of `bash_exec("git diff --cached")`.

**Why it might help:** patch extraction is correctness-critical and bash_exec output has historically been polluted by envelope wrapping bugs. A native op bypasses the shell entirely.

**Why it's borderline:** this is **infrastructure**, not agent behavior. The LLM never sees the patch. Using a native op here doesn't change the mirror. **This one can probably go in the openhands profile's sandbox without breaking parity** — the LLM sees nothing different.

**Candidate for:** openhands profile (it's invisible to the LLM, so it doesn't violate the mirror rule). Low priority, but useful for robustness.

### OpenTelemetry span emission

**What:** Emit OTEL spans for every major lifecycle event (container start, tool dispatch, LLM call, patch extraction). Export to Jaeger/Grafana.

**Why it's not a profile concern:** this is pure observability. Doesn't affect the LLM, doesn't affect correctness, doesn't change anyone's score. It's independent of mirror discipline.

**Candidate for:** Vett infrastructure, enabled by default or via `--trace-otel` flag. Neutral feature.

### Live streaming mode for dev

**What:** `vett run --live` pipes sidecar streaming output to stdout so you can watch an instance in real time.

**Why it's not a profile concern:** pure UX. The LLM still gets a buffered result at the end. Streaming is for humans watching.

**Candidate for:** Vett infrastructure. Build into the CLI, not the profile.

### Teams mode with worktrees

**What:** Multi-agent profiles where a leader delegates to members, each in their own git worktree and sidecar session.

**Why it's not in openhands profile:** openhands is a single-agent SDK. Not applicable.

**Candidate for:** `vett-swe-bench-team` profile. Big feature, eventually.

**Expected delta:** unknown. Team decomposition might help with long-horizon tasks or might add communication overhead. This is exactly the kind of thing Vett exists to measure.

## Process for promoting an idea from this doc to a real profile

1. Pick a profile to host the feature (existing or new).
2. Establish the baseline — measure the target profile (usually `openhands`) on the instance set.
3. Implement the feature in the new profile. Do NOT touch the baseline profile.
4. Re-run the same instance set through the new profile.
5. Compare: pass rate, trajectory shape, iteration counts, tool call frequencies, error patterns.
6. If the feature helps: keep it, document the measured delta in this file.
7. If it doesn't help or hurts: delete it, document why it didn't work.

## Status tracking

| Idea | Status | Target profile | Measured delta |
|---|---|---|---|
| Tool-name alias remap | queued | `vett-swe-bench` | TBD |
| StuckDetector nudge | queued | `vett-swe-bench` | TBD |
| Native grep_files | queued | infrastructure or `vett-swe-bench` | TBD |
| Native git_diff | queued | infrastructure (openhands profile safe) | TBD |
| OTEL spans | queued | infrastructure | N/A (neutral) |
| Live streaming mode | queued | infrastructure | N/A (UX) |
| Teams mode | design phase | `vett-swe-bench-team` | TBD |
