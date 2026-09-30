# Vett Trace File Format

Vett writes a **JSONL trace file** for each run (and optionally one per instance). This document is the canonical schema. Any code that reads traces — tests, the VSCode extension, future viewers, external consumers like the ACT project — must only rely on fields documented here.

## File layout

For a run invoked as `vett run --output ./results/myrun ...`:

```
results/myrun/
├── summary.json                                 # aggregate run summary (final results)
├── run.trace.jsonl                              # run-level events
└── instances/
    ├── astropy__astropy-12907.trace.jsonl       # per-instance trace
    ├── astropy__astropy-13033.trace.jsonl
    └── ...
```

The **run trace** contains events that span the whole run (run start/end, instance start/end).
Each **instance trace** contains every event for a single SWE-bench instance (iterations, LLM calls, tool calls, middleware events, patch extraction).

## Line format

One JSON object per line, no trailing commas, LF line endings. Every line is a complete event that can be parsed independently.

Every event has these required fields:

| Field | Type | Description |
|---|---|---|
| `ts` | string (RFC3339Nano) | Event timestamp in UTC, e.g. `2026-04-15T14:30:00.123456789Z` |
| `event` | string | Event type — one of the strings in the table below |
| `v` | int | Schema version for this event type. Starts at 1. Bumps on breaking changes. |

Additional fields depend on event type.

## Event types

### Run-level events (in `run.trace.jsonl`)

#### `run_start`
Written at the start of a `vett run` invocation.
```json
{"ts":"2026-04-15T14:30:00Z","event":"run_start","v":1,
 "run_id":"run-20260415-143000",
 "suite":"swe-bench-verified",
 "profile":"openhands",
 "model":"qwen3-coder-next",
 "endpoint":"http://old-gpu-a:8000/v1",
 "vett_version":"0.1.0",
 "sidecar_sha256":"abc123...",
 "concurrency":1,
 "instance_count":5}
```

#### `run_end`
Written at the end of a run.
```json
{"ts":"2026-04-15T15:45:22Z","event":"run_end","v":1,
 "run_id":"run-20260415-143000",
 "duration_seconds":4522,
 "completed":5,"errored":0,
 "total_input_tokens":1234567,"total_output_tokens":23456}
```

#### `instance_start`
Written when an instance's container is starting.
```json
{"ts":"...","event":"instance_start","v":1,
 "run_id":"...","instance_id":"astropy__astropy-12907",
 "docker_image":"swebench/sweb.eval.x86_64.astropy_1776_astropy-12907"}
```

#### `instance_end`
Written when an instance completes (success, failure, timeout, error).
```json
{"ts":"...","event":"instance_end","v":1,
 "run_id":"...","instance_id":"astropy__astropy-12907",
 "duration_seconds":1204,
 "iterations":87,
 "input_tokens":2209222,"output_tokens":12428,
 "patch_chars":6536,
 "end_reason":"finish_tool",    // one of: finish_tool, stuck_detector, max_iterations, step_timeout, error
 "failure_reason":null}          // human-readable when end_reason != finish_tool
```

### Instance-level events (in `instances/<id>.trace.jsonl`)

Every instance trace starts with a copy of `instance_start` as line 1, and ends with a copy of `instance_end` as the last line. Between them:

#### `iteration_start`
```json
{"ts":"...","event":"iteration_start","v":1,"iteration":1}
```

#### `iteration_end`
```json
{"ts":"...","event":"iteration_end","v":1,
 "iteration":1,
 "input_tokens":7448,"output_tokens":438,
 "duration_ms":41110}
```

#### `llm_request`
Emitted just before an HTTP request to the LLM endpoint.
```json
{"ts":"...","event":"llm_request","v":1,
 "iteration":1,
 "request_id":"req-abc123",
 "message_count":2,
 "tool_count":5,
 "temperature":1.0,
 "top_p":0.95,
 "request_body_sha256":"..."}    // hash of the outgoing JSON body for golden-test comparison
```

#### `llm_chunk`
Emitted only when streaming is enabled. One per chunk from the LLM.
```json
{"ts":"...","event":"llm_chunk","v":1,
 "request_id":"req-abc123",
 "delta":"Hello"}
```

#### `llm_response`
Emitted when the LLM response is fully received. Full response included for trace replay.
```json
{"ts":"...","event":"llm_response","v":1,
 "iteration":1,
 "request_id":"req-abc123",
 "response_id":"chatcmpl-xyz",
 "finish_reason":"tool_calls",
 "content":"I'll start by exploring...",
 "tool_calls":[
   {"id":"call_001","name":"terminal","arguments":"{\"command\":\"ls\"}"}
 ],
 "input_tokens":7448,"output_tokens":438,
 "duration_ms":41110}
```

#### `tool_call_start`
Before a tool is dispatched.
```json
{"ts":"...","event":"tool_call_start","v":1,
 "iteration":1,
 "call_id":"call_001",
 "tool_name":"terminal",
 "arguments":{"command":"ls","security_risk":"LOW"}}
```

#### `tool_call_end`
After a tool returns (or errors).
```json
{"ts":"...","event":"tool_call_end","v":1,
 "iteration":1,
 "call_id":"call_001",
 "tool_name":"terminal",
 "duration_ms":234,
 "success":true,
 "result_length":512,
 "result_sha256":"...",                      // hash of the result string
 "error":null}
```

#### `tool_call_chunk`
Emitted only for tools that stream output (e.g. streaming terminal). One per chunk.
```json
{"ts":"...","event":"tool_call_chunk","v":1,
 "call_id":"call_001",
 "chunk":"file1.py\nfile2.py\n"}
```

#### `sub_agent_start`
A dispatch tool spawned a sub-agent.
```json
{"ts":"...","event":"sub_agent_start","v":1,
 "parent_call_id":"call_001",
 "task_id":"task-abc",
 "role":"implementer",
 "depth":1}
```

#### `sub_agent_end`
Sub-agent completed.
```json
{"ts":"...","event":"sub_agent_end","v":1,
 "parent_call_id":"call_001",
 "task_id":"task-abc",
 "state":"completed",                          // completed, failed, cancelled
 "iterations":12,
 "patch_chars":800,
 "duration_ms":45200}
```

#### `middleware_event`
A middleware did something noteworthy (stuck detected, compaction triggered, observation rewrote, etc.).
```json
{"ts":"...","event":"middleware_event","v":1,
 "middleware":"StuckDetector",
 "action":"terminate",
 "reason":"action_observation_loop_4x",
 "iteration":42,
 "details":{"scenario":1,"tool_name":"file_editor"}}
```

#### `patch_generated`
After patch extraction at instance end.
```json
{"ts":"...","event":"patch_generated","v":1,
 "patch_chars":6536,
 "patch_sha256":"...",
 "files_touched":["astropy/modeling/separable.py"],
 "extraction_method":"git_diff_cached"}
```

#### `error`
Any unrecoverable error.
```json
{"ts":"...","event":"error","v":1,
 "where":"llm_client",
 "message":"endpoint returned 502",
 "recoverable":false}
```

## summary.json

Aggregate results written at run end. Not JSONL — a single JSON document.

```json
{
  "run_id": "run-20260415-143000",
  "suite": "swe-bench-verified",
  "profile": "openhands",
  "model": "qwen3-coder-next",
  "endpoint": "http://old-gpu-a:8000/v1",
  "started_at": "2026-04-15T14:30:00Z",
  "completed_at": "2026-04-15T15:45:22Z",
  "duration_seconds": 4522,
  "instance_count": 5,
  "completed": 5,
  "errored": 0,
  "resolved": 2,
  "resolution_rate": 0.4,
  "instances": [
    {
      "instance_id": "astropy__astropy-12907",
      "resolved": true,
      "iterations": 87,
      "input_tokens": 2209222,
      "output_tokens": 12428,
      "patch_chars": 6536,
      "duration_seconds": 1204,
      "end_reason": "finish_tool"
    }
  ]
}
```

## Versioning

Every event carries a `v` field. When a new field is added to an event type, the schema stays v1 as long as the new field is optional. When a field is removed, renamed, or its meaning changes, bump `v` for that event type. Readers should tolerate unknown fields and unknown event types (forward compatibility).

The overall trace format version is tracked in `run_start`'s `vett_version` field. Breaking changes across versions are documented in `CHANGELOG.md`.

## Canonical field names and conventions

- **Timestamps:** always RFC3339Nano UTC. Never local time, never Unix seconds.
- **IDs:** strings, treat as opaque. Don't parse them.
- **Hashes:** hex SHA256 lowercase. Used for golden comparisons and deduplication.
- **Durations:** prefer milliseconds for fine-grained events, seconds for whole-instance/run totals.
- **Null vs missing:** optional fields may be omitted. Readers must treat missing and `null` the same.
- **Unknown fields:** writers may add fields not in this doc; readers must ignore them.
- **Snake_case:** all field names in snake_case, matching the openhands-sdk convention for ecosystem compatibility.

## What's NOT in the trace

- Raw LLM endpoint credentials (redacted)
- Environment variables of the host (not captured)
- File contents inside the container (except as `tool_call_end.result` which is already part of the conversation)
- Network traffic from tools (if a tool makes HTTP requests, that's opaque to Vett)

If a reader needs something not in this schema, add an event type or extend an existing event's optional fields. Don't overload existing fields.
