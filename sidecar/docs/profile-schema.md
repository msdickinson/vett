# Vett Profile YAML Schema

A profile is the declarative config for one "agent persona" — system prompt, user template, tools, middleware, LLM settings, sandbox configuration. Profiles live in `profiles/*.yaml` and are loaded by `vett run --profile <name>`.

This document is the canonical schema. Any field that can appear in a profile YAML is listed here.

## Top-level structure

```yaml
name: openhands                  # required
description: ...                 # optional
self_reported_confidence: false  # optional, default false

sandbox:                         # optional (defaults applied)
  run_as_root: false
  home: /tmp

llm:                             # required
  model: ...                     # only if not passed via --model
  endpoint: ...                  # only if not passed via --endpoint
  temperature: 1.0
  top_p: 0.95
  max_tokens: null
  seed: null
  parallel_tool_calls: false

system_prompt: |                 # required
  You are an agent...

user_template: |                 # required for suites that use templated prompts
  I have access to a repository at {working_dir}...

tools:                           # required
  - terminal
  - file_editor
  - task_tracker
  - finish
  - think

middleware:                      # optional, default []
  - submit_detector
  - stuck_detector

max_iterations: 100              # required
timeout_minutes: 30              # optional, default 30

team: null                       # optional — for multi-agent profiles
```

## Required top-level fields

| Field | Type | Description |
|---|---|---|
| `name` | string | Profile identifier. Must be unique across the profile search path. Alphanumeric + `-_` only. |
| `llm` | object | LLM settings block (see below). |
| `system_prompt` | string | Full system prompt. Usually a multi-line `|-` block for precise control over whitespace. Alternatively set `system_prompt_file: ../path/to/prompt.txt` (relative to the YAML) and the loader reads it at load time. Exactly one of the two must be set. |
| `system_prompt_file` | string | Path relative to the profile YAML's directory. Used by `profiles/openhands.yaml` to reference the 12,098-char captured reference at `testdata/ref-system-prompt.txt` instead of inlining it. Symmetric `user_template_file` helper also exists. |
| `tools` | array | List of tool references (see "Tool references" below). |
| `max_iterations` | int | Maximum agent loop iterations per run. Loop exits with `max_iterations` stop reason if this is hit. |

## Optional top-level fields

| Field | Type | Default | Description |
|---|---|---|---|
| `description` | string | `""` | Human-readable description of the profile. |
| `self_reported_confidence` | bool | `false` | Whether this profile asks the model to self-report confidence. |
| `sandbox` | object | (defaults) | Sandbox configuration block. |
| `user_template` | string | `""` | Template for the first user message. Rendered with suite-provided variables. |
| `middleware` | array | `[]` | Middleware chain (see "Middleware" below). |
| `timeout_minutes` | int | `30` | Per-instance wall-clock timeout. |
| `team` | object | `null` | Multi-agent configuration (see "Teams" below). If present, this is a team profile. |

## The `llm` block

```yaml
llm:
  model: qwen3-coder-next      # optional, default from env/flag
  endpoint: http://...         # optional, default from env/flag
  temperature: 1.0             # required for mirror fidelity
  top_p: 0.95                  # required for mirror fidelity
  max_tokens: null             # optional, default null (not sent on wire)
  seed: null                   # optional, default null
  parallel_tool_calls: false   # optional, default false
  request_timeout_seconds: 300 # optional, default 300
  num_retries: 5               # optional, default 5
```

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `model` | string | no | from --model or env | Model name sent in the request. |
| `endpoint` | string | no | from --endpoint or env | Endpoint URL. |
| `temperature` | float | yes | — | Sampling temperature. |
| `top_p` | float | yes | — | Top-p sampling. |
| `max_tokens` | int | no | `null` | Max tokens in the response. `null` means "don't send on wire" — openhands-sdk omits it. |
| `seed` | int | no | `null` | Random seed. `null` means "don't send on wire". |
| `parallel_tool_calls` | bool | no | `false` | Whether to send `parallel_tool_calls: true` in the request. openhands-sdk does NOT send this, so leave false. |
| `request_timeout_seconds` | int | no | `300` | Per-HTTP-request timeout. |
| `num_retries` | int | no | `5` | Retry count for transient network errors. |

**For the openhands profile specifically:** `temperature: 1.0`, `top_p: 0.95`, everything else default. See [openhands-reference-spec.md](openhands-reference-spec.md#2-llm-settings-) for the canonical source.

## The `sandbox` block

```yaml
sandbox:
  run_as_root: false
  home: /tmp
  image_prefix: ""
  default_cwd: /testbed
```

| Field | Type | Default | Description |
|---|---|---|---|
| `run_as_root` | bool | `false` | If true, container runs with `--user root`. SWE-bench-verified profile sets this to true. |
| `home` | string | `/tmp` | `HOME` env var inside the container. Non-root users need a writable HOME. |
| `image_prefix` | string | `""` | Prefix added to suite-provided image names. For overrides during testing. |
| `default_cwd` | string | `/testbed` | Working directory for the default session. Overridden per-instance if the suite specifies. |

## Tool references

The `tools` list can use shorthand or explicit form:

### Shorthand (key equals name)
```yaml
tools:
  - terminal
  - file_editor
  - finish
```

Each bare string refers to a registered tool by its `Key`. The tool's wire name (what the LLM sees) defaults to the same as the key.

### Explicit form (key and wire name differ)
```yaml
tools:
  - key: openhands_file_editor
    name: file_editor
    aliases:
      - str_replace
      - view
  - key: openhands_terminal
    name: terminal
```

| Field | Type | Required | Description |
|---|---|---|---|
| `key` | string | yes | Registered tool key (from the Go registry). |
| `name` | string | no (defaults to `key`) | What the LLM sees in the tool schema. |
| `aliases` | array | no | Extra names the dispatcher will accept and route to this tool. Used for enhancement profiles that want to catch model hallucinations. The openhands profile does NOT use aliases. |
| `config` | object | no | Tool-specific config passed to the tool at dispatch time. Only used by tools that declare they accept config. |

### Uniqueness rules
- Within one profile, no two tools may share the same `name` (wire name) — the LLM can't distinguish them.
- Within one profile, no two tools may share the same alias — the dispatcher can't disambiguate.
- Aliases cannot collide with any `name` in the same profile.
- The global tool registry key is always unique (enforced at Go init time).

## Middleware

### Shorthand (no config)
```yaml
middleware:
  - submit_detector
  - stuck_detector
  - output_truncation
```

Each bare string refers to a registered middleware by name, using default config.

### Explicit form (with config)
```yaml
middleware:
  - name: output_truncation
    config:
      max_chars: 16000
  - name: submit_detector
    config: {}
  - name: stuck_detector
    config:
      action_observation_threshold: 4
      action_error_threshold: 3
      monologue_threshold: 3
      alternating_threshold: 6
```

Each middleware has its own config schema documented in the middleware's source file. Unknown config keys are an error at profile-load time.

### Chain order
The order of the middleware list is the order they run at each iteration. See [middleware-interface.md](middleware-interface.md#lifecycle) for the lifecycle details.

### For the openhands profile specifically
The canonical openhands middleware chain (from openhands-sdk source) is:

```yaml
middleware:
  - output_truncation
  - submit_detector
  - stuck_detector
```

Note the absence of `ObservationWrapper`, `LlmSummarizingCompaction`, and similar names that appeared in Vett v1 — those were v1-specific inventions and NOT in openhands-sdk. See [openhands-reference-spec.md §13](openhands-reference-spec.md#13-open-gaps-) gap 3.

## Teams block (for multi-agent profiles)

```yaml
team:
  leader:
    role: leader
    system_prompt: |
      ...
    user_template: |
      ...
    tools:
      - create_task
      - finish
    middleware:
      - submit_detector
      - stuck_detector
    llm:
      temperature: 1.0
    max_iterations: 20
    max_dispatch_depth: 2

  members:
    - role: implementer
      system_prompt: |
        ...
      tools:
        - terminal
        - file_editor
        - finish
      llm:
        temperature: 1.0
      max_iterations: 50

    - role: reviewer
      system_prompt: |
        ...
      tools:
        - file_view
        - list_dir
        - finish
      llm:
        temperature: 0.2
      max_iterations: 20
```

A team block, when present, defines one leader and one or more members. The leader is what runs on each instance — its agent loop is the top-level loop. The leader dispatches members via `create_task` (or the other dispatch tools).

Each member has the same fields as a top-level profile (prompts, tools, middleware, llm, max_iterations) but scoped to that role. When a member is dispatched, Vett constructs a fresh sub-loop using the member's config.

| Field | Type | Required | Description |
|---|---|---|---|
| `leader` | object | yes | Leader config (see top-level fields). |
| `leader.max_dispatch_depth` | int | no, default 2 | Maximum nesting depth for reentrant dispatch. |
| `members` | array | yes (at least one) | List of member configs. |
| `members[].role` | string | yes | Role identifier used by `create_task(role=...)`. |

**For the openhands profile specifically:** the `team` block is NOT set. OpenHands is a single-agent SDK. Future profiles like `vett-swe-bench-team` will use `team`.

## Validation at load time

When Vett loads a profile, it validates:

1. All required fields are present
2. `name` is unique in the search path (warns on collision)
3. Every tool reference resolves to a registered tool key (errors otherwise)
4. No two tools in the same profile share a wire name
5. Every middleware reference resolves to a registered middleware name
6. Middleware config keys are all known (errors on typos)
7. `max_iterations > 0`
8. `llm.temperature >= 0 && llm.temperature <= 2`
9. `llm.top_p > 0 && llm.top_p <= 1`
10. If `team` is present, `team.leader` and `team.members[*]` recursively validate

Validation errors are human-readable and include file path + line number when possible.

## Search path

Profiles are loaded from these directories in order (later wins on name collision with a warning):

1. `./profiles/` (relative to cwd)
2. `~/.vett/profiles/` (or `$VETT_CONFIG_DIR/profiles/`)
3. Embedded profiles (baked into the `vett` binary via `go:embed`)

You can also pass an explicit path: `vett run --profile ./my-profile.yaml`.

## Example: the canonical openhands profile

```yaml
name: openhands
description: |
  Faithful mirror of openhands-sdk V1. System prompt, tool schemas, and
  middleware chain all match the reference run byte-for-byte.

sandbox:
  run_as_root: true
  home: /tmp

llm:
  temperature: 1.0
  top_p: 0.95

system_prompt: |-
  You are OpenHands agent, a helpful AI assistant...
  [full 12,098-char system prompt]

user_template: |
  I have access to a python code repository in the directory {working_dir}...
  [full SWE-bench user template]

tools:
  - terminal
  - file_editor
  - task_tracker
  - finish
  - think

middleware:
  - output_truncation
  - submit_detector
  - stuck_detector

max_iterations: 100
timeout_minutes: 30
```

## Example: a hypothetical teams profile

```yaml
name: vett-swe-bench-team
description: |
  Leader + implementer + reviewer team profile. Uses the openhands tool
  schemas for terminal and file_editor. Adds create_task for dispatch.

sandbox:
  run_as_root: true
  home: /tmp

llm:
  temperature: 1.0
  top_p: 0.95

system_prompt: |-
  (team-leader system prompt)

tools:
  - create_task
  - create_tasks_parallel
  - finish

middleware:
  - submit_detector
  - stuck_detector

max_iterations: 20
timeout_minutes: 30

team:
  leader:
    role: leader
    max_dispatch_depth: 2

  members:
    - role: implementer
      system_prompt: (implementer prompt)
      tools: [terminal, file_editor, finish]
      max_iterations: 50
      llm:
        temperature: 1.0
    - role: reviewer
      system_prompt: (reviewer prompt)
      tools: [file_view, list_dir, finish]
      max_iterations: 20
      llm:
        temperature: 0.2
```

The leader's top-level `tools` list contains only dispatch tools and `finish`. The actual work happens inside sub-agents with their own tool lists.
