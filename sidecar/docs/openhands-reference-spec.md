# OpenHands Reference Spec (for Vett Go rewrite)

Canonical spec extracted from the real openhands-sdk source and the captured
reference run on the runner box. This is what the `openhands` profile in Vett v2 (Go) must
reproduce bit-for-bit. Every claim here has a source path you can re-check.

**Status of each area:** ✅ verified, 🟡 spec captured but implementation TBD, ❌ gap.

## 0. Sources

All paths are on the runner box (`ssh runner`).

| What | Path |
|---|---|
| openhands-sdk source | `~/openhands-eval/benchmarks/vendor/software-agent-sdk/openhands-sdk/openhands/sdk/` |
| openhands-tools source | `~/openhands-eval/benchmarks/vendor/software-agent-sdk/openhands-tools/openhands/tools/` |
| Reference run patches (500) | `~/openhands-eval/benchmarks/results/openhands-full-500-backup-django11119/princeton-nlp__SWE-bench_Verified-test/openai/qwen3-coder-next_sdk_62c2e7c_maxiter_300/` |
| Reference run metadata.json | same dir, `metadata.json` |
| Captured ref request (turn 1, test msg) | `/tmp/ref-request.json` |
| Vett's last request dump | `/tmp/vett-first-request.json` |
| Filtered 5-instance submission | `/tmp/ref-5astropy.jsonl` |
| SWE-bench harness | `~/openhands-eval/benchmarks/.venv/bin/python -m swebench.harness.run_evaluation` |

## 1. Wire format ✅

Verified bit-exact between Vett's `/tmp/vett-first-request.json` and openhands-sdk's `/tmp/ref-request.json` for all 5 tool schemas. Spec:

### Request body
```json
{
  "model": "qwen3-coder-next",
  "messages": [ ... ],
  "tools": [ ... ],
  "temperature": 1.0,
  "top_p": 0.95
}
```
NO `max_tokens`, NO `parallel_tool_calls`, NO `frequency_penalty`, NO `presence_penalty`, NO `seed` on the wire. openhands-sdk uses litellm which sends `api_base`/`api_key`/`drop_params`/`seed`/`timeout` as kwargs, but those are litellm-internal and **do not appear in the HTTP body** to vLLM.

### Message format
Each message: `{role, content}` where `content` is a LIST of content parts.
- **system/user/assistant** messages: `content = [{"type": "text", "text": "..."}]`
  - Key order: `type` first, `text` second. Verified from the captured
    reference at `/tmp/ref-request.json` (Vett Phase 1a finding — the
    earlier "text-first alphabetical" claim in this doc was wrong).
- **tool-role** messages: `content = "plain string"` (NOT a list)
- `assistant` with tool calls also includes `tool_calls: [...]`

### Empty assistant content
When assistant emits tool calls, `content` may still be present as empty structured content. Preserve whatever the API returns, don't flatten to `null`.

## 2. LLM settings ✅

Source: `metadata.json` of the reference run.

| Setting | Value | Notes |
|---|---|---|
| temperature | 1.0 | **Required** — qwen3-coder-next was tuned for 1.0. 0.6 over-concentrates exploration calls. |
| top_p | 0.95 | |
| seed | null | No reproducibility pin; run-to-run variance is expected |
| max_tokens | not sent | litellm drops it when null |
| parallel_tool_calls | not sent | |
| max_message_chars | 30000 | Applied server-side by litellm; not a wire field |
| timeout | 300s per request | Applied client-side |
| num_retries | 5 | Applied client-side |

## 3. System prompt ✅

Verbatim V1 OpenHands system prompt. **12,098 characters exactly.** No trailing newline (YAML `|-` form). Already in `profiles/openhands.yaml` — copy verbatim into the Go rewrite.

## 4. User template ✅

8-phase SWE-bench user message template from `benchmarks/swebench/prompts/default.j2`. Rendered with `{problem_statement}`, `{working_dir}`, `{base_commit}`. Already in `profiles/openhands.yaml`.

## 5. Tool schemas ✅ (all 5 bit-exact)

Verified via `python3 diff` of Vett's request against `/tmp/ref-request.json`. Tool order matters:

```
[terminal, file_editor, task_tracker, finish, think]
```

### 5.1 terminal
- required: `[command, security_risk]`
- parameters: `command` (string), `is_input` (bool), `timeout` (number), `reset` (bool), `security_risk` (enum UNKNOWN/LOW/MEDIUM/HIGH), `summary` (string)
- description: see `tools/terminal.yaml` — already verbatim
- `output_envelope: openhands_bash` applied at tool-call return time (not at sidecar level — that caused double-envelope bug)

### 5.2 file_editor
- required: `[command, path, security_risk]`
- parameters: `command` (enum view/create/str_replace/insert/undo_edit), `path` (absolute), `file_text`, `view_range` (array of 2 ints), `old_str`, `new_str`, `insert_line`, `security_risk`, `summary`
- description: exact text from `/tmp/ref-request.json` — includes trailing "Your current working directory is: /tmp\nWhen exploring project structure, start with this directory instead of the root filesystem."
- Uses canonical Anthropic computer-use-demo semantics (see §7)

### 5.3 task_tracker
- required: `[security_risk]` only (NOT `command`)
- parameters: `command` (enum view/plan, not required), `task_list` (array of objects with `title` required), `security_risk`, `summary`
- description: 5052-char V1 text with Application Guidelines / Scenarios / Counter-examples / Status Management sections — must be verbatim (already in `tools/task-tracker.cs`)

### 5.4 finish
- required: `[message]` only — **NO security_risk, NO summary required**
- parameters: `message` (string, required), `summary` (string, optional)
- description: verbatim V1 text with a trailing `\n` (ref is 442 chars with that newline)

### 5.5 think
- required: `[thought]` only — **NO security_risk, NO summary required**
- parameters: `thought` (string, required), `summary` (string, optional)
- description: verbatim V1 text, no trailing newline

## 6. StuckDetector 🟡 (spec captured, impl needs 3 more scenarios)

Source: `openhands-sdk/openhands/sdk/conversation/stuck_detector.py`

### Defaults (`StuckDetectionThresholds`)
```
action_observation:  4
action_error:        3
monologue:           3
alternating_pattern: 6
```

### Window
`MAX_EVENTS_TO_SCAN_FOR_STUCK_DETECTION = 20`

### Reset-on-user rule
Only scan events AFTER the last user message. A new user message clears the detection window — agent can repeat itself after a user interjects.

### Scenarios (all 5)
1. **action-observation loop (4x)** — last 4 ActionEvents equal AND last 4 ObservationEvents equal → stuck
2. **action-error loop (3x)** — last 3 ActionEvents equal AND last 3 Observations are all `AgentErrorEvent` → stuck
3. **monologue (3x)** — 3 consecutive `MessageEvent` with `source=agent` without user interruption → stuck (CondensationSummaryEvent does not break the monologue)
4. **alternating action-observation (A-B-A-B-A-B, 6x)** — pairs alternating: `actions[i] == actions[i+2]` for all i in window AND same for observations → stuck
5. **context window error loop** — TODO in openhands source (issue #282). **Skip in Vett v2 as well.**

### Event equality (`_event_eq`)
Ignore IDs/metrics. Compare:
- `ActionEvent`: source, thought, action, tool_name (IGNORE tool_call_id, llm_response_id, action_id)
- `ObservationEvent`: source, observation, tool_name (IGNORE action_id, tool_call_id)
- `AgentErrorEvent`: source, error (IGNORE action_id)
- `MessageEvent`: source, llm_message

### Behavior on trigger
**Ref terminates immediately on first trip. No nudge, no recovery prompt, no second chance.** Vett's `openhands` profile must match this exactly — terminate the loop, set the stop reason, return. Any "helpful" intervention like injecting a pivot message is a deviation that breaks the mirror and must not exist in this profile. (A `vett-enhanced` profile could test whether a nudge helps as a measured delta against this baseline — but not here.)

## 7. FileEditor semantics 🟡

Source: `openhands-tools/openhands/tools/file_editor/editor.py` (745 lines)

Based on [Anthropic's computer-use-demo `edit.py`](https://github.com/anthropics/anthropic-quickstarts/blob/main/computer-use-demo/computer_use_demo/tools/edit.py). Key constants from `utils/config.py` and `utils/constants.py`:

| Constant | Value |
|---|---|
| `MAX_FILE_SIZE_MB` | 10 |
| `MAX_RESPONSE_LEN_CHAR` | 16000 (pre-truncation) |
| `SNIPPET_CONTEXT_WINDOW` | 4 (lines of context around edit) |
| `TEXT_FILE_CONTENT_TRUNCATED_NOTICE` | `"<response clipped><NOTE>To save on context only part of this file has been shown to you. You should retry this tool after you have searched inside the file with `grep -n` in order to find the line numbers of what you are looking for.</NOTE>"` |
| `DIRECTORY_CONTENT_TRUNCATED_NOTICE` | similar to above |
| `BINARY_FILE_CONTENT_TRUNCATED_NOTICE` | similar |

### Commands

- `view` — `cat -n`-style output for files, `find -maxdepth 2 -not -path "*/\.*"` for directories, preceded by `"Here's the result of running \`cat -n\` on {path}:\n"` for files, `"Here's the files and directories up to 2 levels deep in {path}, excluding hidden items:\n"` for directories
- `create` — fails if path exists
- `str_replace` — exact match, rejects multiple matches with a line-numbered error, snippet-of-replacement returned with 4 lines of context
- `insert` — insert after line N, returns snippet
- `undo_edit` — pops last change from per-file history

### expandtabs
All content operations call Python's `str.expandtabs(8)` before comparison. Go port uses the equivalent in `sidecar/file_editor.go` already.

### History
Per-file undo stack. Global `FileHistoryManager` keeps it across calls. **Session scope** — not persisted across agent runs, cleared on sidecar restart.

### Encoding
`EncodingManager` detects file encoding and preserves it on write. Edge case: UTF-8 BOM, latin-1 fallback. Our Go port needs to handle this or at least not corrupt non-UTF8 files.

## 8. LlmSummarizingCondenser 🟡 (not urgent for short runs)

Source: `openhands-sdk/openhands/sdk/context/condenser/llm_summarizing_condenser.py`

### Trigger conditions (ANY of)
- Explicit condensation request in the event stream (`unhandled_condensation_request`)
- Token count > `max_tokens` threshold (if set)
- Event count > `max_size` (default 240)

### Defaults
| Setting | Default | Notes |
|---|---|---|
| `max_size` | 240 events | Event-count trigger |
| `keep_first` | 2 | System prompt + first user message always preserved |
| `minimum_progress` | 0.1 | Must condense ≥10% of events for the call to count |
| `max_tokens` | None | Token-count trigger disabled by default |
| `hard_context_reset_max_retries` | 5 | |
| `hard_context_reset_context_scaling` | 0.8 | Shrinks event string max length each retry |

### Behavior
1. When triggered, halves the event list (`target_size = max_size // 2`)
2. Selects events to forget: everything between `keep_first` and the tail
3. Renders `summarizing_prompt.j2` with the forgotten events as strings
4. **Calls the LLM** with just that prompt (separate API call)
5. Replaces the forgotten events with a single `CondensationSummaryEvent` containing the returned summary
6. Inserts at `summary_offset` so later events still refer to earlier ones correctly

### Prompt template (verbatim)
Source: `openhands-sdk/openhands/sdk/context/condenser/prompts/summarizing_prompt.j2`

```
You are maintaining a context-aware state summary for an interactive agent.
You will be given a list of events corresponding to actions taken by the agent, which will include previous summaries.
If the events being summarized contain ANY task-tracking, you MUST include a TASK_TRACKING section to maintain continuity.
When referencing tasks make sure to preserve exact task IDs and statuses.

Track:

USER_CONTEXT: (Preserve essential user requirements, goals, and clarifications in concise form)

TASK_TRACKING: {Active tasks, their IDs and statuses - PRESERVE TASK IDs}

COMPLETED: (Tasks completed so far, with brief results)
PENDING: (Tasks that still need to be done)
CURRENT_STATE: (Current variables, data structures, or relevant state)

For code-specific tasks, also include:
CODE_STATE: {File paths, function signatures, data structures}
TESTS: {Failing cases, error messages, outputs}
CHANGES: {Code edits, variable updates}
DEPS: {Dependencies, imports, external calls}
VERSION_CONTROL_STATUS: {Repository state, current branch, PR status, commit history}

PRIORITIZE:
1. Adapt tracking format to match the actual task type
2. Capture key user requirements and goals
3. Distinguish between completed and pending tasks
4. Keep all sections concise and relevant

SKIP: Tracking irrelevant details for the current task type

Example formats:

For code tasks:
USER_CONTEXT: Fix FITS card float representation issue
COMPLETED: Modified mod_float() in card.py, all tests passing
PENDING: Create PR, update documentation
CODE_STATE: mod_float() in card.py updated
TESTS: test_format() passed
CHANGES: str(val) replaces f"{val:.16G}"
DEPS: None modified
VERSION_CONTROL_STATUS: Branch: fix-float-precision, Latest commit: a1b2c3d

For other tasks:
USER_CONTEXT: Write 20 haikus based on coin flip results
COMPLETED: 15 haikus written for results [T,H,T,H,T,H,T,T,H,T,H,T,H,T,H]
PENDING: 5 more haikus needed
CURRENT_STATE: Last flip: Heads, Haiku count: 15/20

{% for event in events %}
<EVENT>
{{ event }}
</EVENT>
{% endfor %}

Now summarize the events using the rules above.
```

### When it matters for Vett
- **Not urgent.** Our runs cap at 100 iterations ≈ ~200 events. The 240-event trigger is above that floor; **we may never hit compaction on short runs**.
- **Critical for full 300-iter runs.** If we want to reproduce ref's maxiter_300 resolution rate, compaction must be implemented and must match.
- **Phase 3 feature.** Not blocking Phase 1 or 2 of the Go rewrite. Stub it with a no-op for now, implement properly when we start running long instances.

## 9. Observation envelope format ✅

For tool-role messages going BACK to the LLM after a `terminal` call:

```
{raw stdout}
[Current working directory: {cwd}]
[Command finished with exit code {exit_code}]
```

- Applied by the TOOL, not the sandbox. Vett v1's bug was double-enveloping (sidecar + yaml loader both did it).
- For non-zero exit: exit code is the real integer, not a coerced -1.
- For timeouts: `"Error: Command timed out after {N}s.\n{partial_stdout}"` prepended, exit code `-1`.

Source: `YamlToolLoader.cs:139` in current Vett (correct implementation) and `openhands-sdk` CmdOutputObservation (parity-verified).

## 10. Container environment ✅

On container startup, before the agent loop runs:

1. **Activate conda testbed env** (NOT via `conda activate` — broken in --norc --noprofile bash):
   ```sh
   if [ -d /opt/miniconda3/envs/testbed/bin ]; then
     export PATH=/opt/miniconda3/envs/testbed/bin:$PATH
     export CONDA_PREFIX=/opt/miniconda3/envs/testbed
     export CONDA_DEFAULT_ENV=testbed
   fi
   ```
   Verify via `python --version` printing `Python 3.9.x` (testbed) not `3.11.x` (base).

2. **Git safe.directory** for uid mismatches:
   ```sh
   git config --global --add safe.directory '*'
   ```

3. **Container flags** for SWE-bench images:
   - `--entrypoint /bin/bash` (overrides image's Python server entrypoint)
   - `-e HOME=/tmp` (non-root user needs writable HOME for .gitconfig)
   - optional `--user root` if profile sets `sandbox.run_as_root: true`

## 11. Patch extraction ✅

```sh
cd {workspace}
git add -A
git diff --cached
```

Take stdout directly as the patch. **Must not** have bash envelope wrapped around it (Vett v1 bug). Use a native `git_diff` op in the sidecar if available, otherwise ensure `bash_exec` returns raw stdout without envelope at this call site.

## 12. Tool-name alias remapping ❌ NOT in the openhands profile

qwen3-coder-next occasionally emits malformed tool calls where `name` is a file_editor command (`str_replace`, `view`, etc.) or a terminal alias (`run`, `execute_bash`). Vett v1 had a remap to catch these before dispatch.

**The openhands profile does NOT include this remap.** openhands-sdk doesn't remap, so neither do we. If the model emits a malformed call, it gets a "tool not found" error just like it would against openhands-sdk. Matching their failure behavior is part of the mirror.

This remap idea is saved in `docs/future-improvements.md` as a candidate for a future `vett-enhanced` profile, where it would be measured as a delta against the openhands baseline. Not here.

## 13. Open gaps ❌

These are items where spec coverage is still incomplete. Phase 1 of the Go rewrite can proceed without closing all of them, but they need to be tracked.

1. **`is_input` / `reset` behavior in terminal** — ✅ **CLOSED.** Across 20 sampled ref instances (1785 terminal calls), `is_input=true` and `reset=true` were used zero times. They exist as pydantic fields in openhands-tools but are never invoked by qwen3-coder-next on SWE-bench. Vett v2 accepts the params and ignores them — no implementation needed for bit-exact mirror behavior.
2. **Multi-turn ref request captures** — 🟡 still open. We have turn 1. Need turn 2, turn 5, turn 30+ captures to golden-test continuation format (tool-call echo-back, prior-observation structure, multi-turn message shape). Capture via `testdata/capture-ref.sh`.
3. **ObservationWrapper middleware** — ✅ **CLOSED with a surprise.** This middleware **does not exist in openhands-sdk**. It was invented by Vett v1 during early design and carried forward by assumption. The openhands profile's middleware chain should NOT include it. Vett v1 `openhands.yaml` is incorrect and the v2 profile must remove it.
4. **Reference pass rate on astropy-5** — 🟡 still open. Scoring the saved ref patches against the SWE-bench test harness is blocked on a broken venv on the runner box. Not required for Phase 1 but needed to calibrate "is Vett matching openhands" in Phase 3.

## 14. Go rewrite checklist (per Phase)

**Phase 0** (scaffold): use §0 paths to copy verbatim content (system prompt, user template, tool descriptions). Embed via `go:embed`. Write golden files to `testdata/` from captured refs.

**Phase 1** (end-to-end, one tool): implement §1 (wire format), §2 (LLM settings), §5.1 (terminal schema), §9 (envelope), §10 (container env), §11 (patch extraction). Golden test against `testdata/ref-requests/turn-1-initial.json`. One instance runnable end-to-end.

**Phase 2** (full openhands profile): implement §5.2-5.5 (remaining tool schemas), §7 (file editor semantics), §6 all 5 stuck scenarios (terminate on first trip, NO nudge). Verify the real openhands middleware chain — do NOT include ObservationWrapper. 5-10 astropy instances produce patches that match ref file targets.

**Phase 3** (correctness and scale): implement §8 (compaction) if long runs needed, fix §13 gap 2 (multi-turn captures) and gap 4 (ref pass rate baseline). Run 25-50 instances and validate pass rate against ref baseline.

**Phase 4** (operations): scoring pipeline integrated as part of suite runner. `vett verify` command usable end-to-end.

## 14b. Multi-agent teams — NOT mirrored

openhands-sdk is a **single-agent** system. It has no concept of teams, leaders, members, or reentrant agent dispatch. The `openhands` profile therefore does NOT include:

- `create_task` / `create_tasks_parallel` / `dispatch_task_async` / `check_task` / `wait_for_task` / `list_tasks` / `cancel_task` tools
- Any `team:` block in the profile YAML
- Multi-session sandbox usage (only the default session is used)
- Async tool invocation (tool schemas do not expose the `async` param)

Vett has all this infrastructure built into Phase 1 for future profiles (like `vett-swe-bench-team`) but the `openhands` profile is single-agent, single-session, sync-only. This is part of mirror discipline — anything openhands-sdk doesn't do, this profile doesn't do either.

## 15. Deviations from openhands — NONE planned for the openhands profile

The openhands profile is a faithful mirror. No deviations. Any behavior difference from openhands-sdk is a bug in Vett's openhands profile and must be fixed.

Vett's infrastructure layer does differ from openhands-sdk — Vett has profiles, suites, a multi-profile runner, a sidecar architecture, its own trace format, OpenTelemetry spans, its own CLI. None of this affects what the LLM observes. The openhands profile uses Vett's infrastructure to reproduce openhands-sdk's agent behavior; the infrastructure is invisible to the model.

Features Vett v1 had that are NOT in the v2 openhands profile:
- **Tool-name alias remap** — removed (see §12)
- **StuckDetector nudge-before-terminate** — removed (see §6 behavior on trigger)
- **ObservationWrapper middleware** — removed (doesn't exist in openhands, see §13 gap 3)

These candidates are saved in `docs/future-improvements.md` for possible inclusion in a separate `vett-enhanced` profile where their effect can be measured as a delta against the openhands baseline.

## Appendix: how to regenerate this spec

If openhands-sdk is upgraded and the vendor tree changes, re-run these commands and diff:

```bash
# Re-capture ref request (requires test run with litellm monkey-patch)
# See /tmp/gen_ref_request.py on the runner box

# Re-verify tool schemas bit-exact
python3 -c 'import json; ...'  # see docs/openhands-reproduction.md §"How we compare"

# Re-read stuck_detector.py
cat ~/openhands-eval/benchmarks/vendor/software-agent-sdk/openhands-sdk/openhands/sdk/conversation/stuck_detector.py

# Re-read condenser source
cat ~/openhands-eval/benchmarks/vendor/software-agent-sdk/openhands-sdk/openhands/sdk/context/condenser/llm_summarizing_condenser.py
```

Any change to any referenced openhands source file is a signal to update this doc.
