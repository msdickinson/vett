# Vett Sidecar RPC Protocol

This document is the canonical spec for the RPC protocol between the Vett host process and the `vett-sidecar` binary running inside a Docker container. Both sides must implement this spec exactly — any drift causes subtle bugs that are hard to debug later.

## Transport

- **Medium:** the sidecar's stdin and stdout, attached by `docker exec -i`
- **Framing:** newline-delimited JSON. One complete JSON object per line. No length prefix, no framing header.
- **Encoding:** UTF-8 throughout.
- **Line endings:** `\n` only. Sidecar must not emit `\r\n`.
- **Concurrency:** the host may have multiple requests in flight at once. The sidecar handles them sequentially within a single session (one bash child = one at a time) but can serve different sessions concurrently. Responses are keyed by `id` so the host can correlate.

## Request envelope

Every request has this exact shape:

```json
{
  "id": "req-abc123",
  "op": "bash_exec",
  "args": { ... op-specific ... }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | string | yes | Opaque request identifier. Host generates unique IDs. Sidecar echoes it in the response. |
| `op` | string | yes | Operation name. See ops table below. |
| `args` | object | yes | Operation-specific arguments. Empty object `{}` is allowed if the op takes no args. |

The host must send exactly one request per line. The sidecar must not process incomplete lines.

## Response envelope

Every response has this shape:

```json
{
  "id": "req-abc123",
  "ok": true,
  "result": { ... op-specific ... }
}
```

Or on error:

```json
{
  "id": "req-abc123",
  "ok": false,
  "error": {
    "code": "session_not_found",
    "message": "session 'leader' does not exist"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | string | yes | Echoes the request's `id`. |
| `ok` | bool | yes | Whether the op succeeded. |
| `result` | object | if `ok` | Operation-specific result. |
| `error` | object | if not `ok` | Error details. |
| `error.code` | string | yes on error | Machine-readable error code. |
| `error.message` | string | yes on error | Human-readable error message. |

## Streaming responses

Some operations (`bash_exec` with `stream: true`) emit multiple responses for one request. Format:

```json
{"id":"req-abc123","stream":true,"chunk":"partial output\n"}
{"id":"req-abc123","stream":true,"chunk":"more output\n"}
{"id":"req-abc123","ok":true,"result":{"stdout":"full output","exit_code":0,"cwd":"/testbed","timed_out":false}}
```

Rules:
- All but the last line have `"stream": true` and a `"chunk"` field.
- The final line has the normal `ok` + `result` envelope.
- Chunks may split output at arbitrary byte boundaries — including mid-UTF-8 character. Readers must buffer across chunks if they need to decode.
- The final `result.stdout` is always the complete concatenation of all chunks.

## Error codes

Every error response carries a `code` for programmatic handling. The host can recover from some errors and must fail on others.

| Code | Meaning | Recoverable |
|---|---|---|
| `session_not_found` | Referenced session ID doesn't exist | Host should create or abort |
| `session_exists` | Tried to create a session that already exists | Host bug |
| `invalid_args` | Malformed args in request | Host bug |
| `timeout` | Operation exceeded `timeout_seconds` | Host may retry with different timeout |
| `bash_dead` | Bash child process died | Host must create new session |
| `file_not_found` | Referenced path doesn't exist | Recoverable |
| `file_exists` | `file_create` target already exists | Host should use `str_replace` or `view` |
| `str_replace_no_match` | `old_str` not found in target file | Recoverable, return to agent |
| `str_replace_multi_match` | `old_str` matches multiple places | Recoverable, return to agent |
| `file_too_large` | File exceeds `MAX_FILE_SIZE_MB` | Host should reject upstream |
| `internal` | Sidecar internal error | Fatal, host should kill container |

## Version negotiation

The first request the host sends after attaching MUST be `hello`:

```json
{"id":"req-hello","op":"hello","args":{"host_version":"0.1.0","protocol_version":1}}
```

Sidecar responds:

```json
{"id":"req-hello","ok":true,"result":{"sidecar_version":"0.1.0","protocol_version":1,"supported_ops":["bash_exec","file_view",...]}}
```

If protocol versions don't match, the host aborts and reports a version mismatch. Subsequent ops use features corresponding to the agreed-upon protocol version.

## Operations

### `hello`
Version negotiation. See "Version negotiation" above. Must be the first operation.

### `session_create`
Create a new bash session.

**Args:**
```json
{"name":"agent","cwd":"/testbed","env":{"FOO":"bar"}}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Session identifier. Must be unique. Alphanumeric + `-_` only. |
| `cwd` | string | no | Initial working directory. Default: `/`. |
| `env` | object | no | Additional environment variables. Merged with host env. |

**Result:**
```json
{"name":"agent","pid":1234}
```

**Errors:** `session_exists`, `invalid_args`, `internal`.

### `session_destroy`
Kill a session's bash child and remove it.

**Args:** `{"name":"agent"}`
**Result:** `{"name":"agent"}`
**Errors:** `session_not_found`.

### `session_list`
List all sessions.

**Args:** `{}`
**Result:**
```json
{"sessions":[{"name":"agent","pid":1234,"cwd":"/testbed"},{"name":"worker-1","pid":5678,"cwd":"/testbed"}]}
```

### `bash_exec`
Run a bash command in a session. **This is the most important op — the raw passthrough invariant applies here.**

**Args:**
```json
{
  "session_id": "agent",
  "command": "grep -rn 'foo' /testbed",
  "timeout_seconds": 60,
  "stream": false
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `session_id` | string | yes | Which session to run in. |
| `command` | string | yes | The command to run. **Reaches bash byte-for-byte.** No modification, escaping, or prepending. |
| `timeout_seconds` | int | no | Timeout in seconds. Default 60. Max 3600. |
| `stream` | bool | no | If true, emit chunks as output arrives. Default false. |

**Result:**
```json
{
  "stdout": "file1.py:10:foo bar\n",
  "exit_code": 0,
  "cwd": "/testbed",
  "timed_out": false
}
```

| Field | Type | Description |
|---|---|---|
| `stdout` | string | Merged stdout+stderr output. Raw, no envelope wrapping (envelope is added by the tool layer, not the sidecar). |
| `exit_code` | int | Command exit code. `-1` if timed out. |
| `cwd` | string | The session's cwd AFTER the command. Captures `cd` if the command changed dirs. |
| `timed_out` | bool | True if the command was killed by timeout. |

**Raw passthrough invariant:** the `command` string reaches bash stdin unchanged. The sidecar wraps it with a sentinel printf for output framing, but the user's command is not modified.

```go
script := "{ " + command + "; } 2>&1\n" +
          "printf '\\n__VETT_END_" + marker + " exit=%s cwd=%s\\n' \"$?\" \"$PWD\"\n"
```

The test `TestBashPassthroughIsBitExact` locks this invariant.

**Errors:** `session_not_found`, `bash_dead`, `timeout`, `invalid_args`.

### `file_view`
Read a file or list a directory.

**Args:**
```json
{"session_id":"agent","path":"/testbed/file.py","view_range":[1,20]}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `session_id` | string | yes | |
| `path` | string | yes | Absolute path. |
| `view_range` | [int, int] | no | Line range, inclusive. Only for files, not directories. |

**Result:**
```json
{"content":"Here's the result of running `cat -n` on /testbed/file.py:\n     1\tdef foo():\n...","is_directory":false}
```

| Field | Type | Description |
|---|---|---|
| `content` | string | Formatted file content or directory listing. Matches openhands' format byte-for-byte. |
| `is_directory` | bool | True if path was a directory. |

**Errors:** `session_not_found`, `file_not_found`, `file_too_large`, `invalid_args`.

### `file_create`
Create a new file. Fails if the file already exists.

**Args:** `{"session_id":"agent","path":"/testbed/new.py","file_text":"print('hi')\n"}`
**Result:** `{"content":"File created successfully at: /testbed/new.py"}`
**Errors:** `session_not_found`, `file_exists`.

### `file_str_replace`
Replace a substring in a file. Exact match, must be unique.

**Args:**
```json
{"session_id":"agent","path":"/testbed/file.py","old_str":"def foo():","new_str":"def foo(x):"}
```

**Result:**
```json
{"content":"The file /testbed/file.py has been edited. Here's the result...\n"}
```

**Errors:** `session_not_found`, `file_not_found`, `str_replace_no_match`, `str_replace_multi_match`.

### `file_insert`
Insert text after a given line number.

**Args:** `{"session_id":"agent","path":"/testbed/file.py","insert_line":5,"new_str":"new content\n"}`
**Result:** `{"content":"The file /testbed/file.py has been edited..."}`
**Errors:** `session_not_found`, `file_not_found`, `invalid_args` (line out of range).

### `file_undo`
Undo the last edit to a file from the session's undo history.

**Args:** `{"session_id":"agent","path":"/testbed/file.py"}`
**Result:** `{"content":"Last edit to /testbed/file.py undone successfully. ..."}`
**Errors:** `session_not_found`, `file_not_found`, `invalid_args` (no history).

### `file_exists`
Check if a path exists.

**Args:** `{"session_id":"agent","path":"/testbed/file.py"}`
**Result:** `{"exists":true,"is_file":true,"is_dir":false}`
**Errors:** `session_not_found`.

### `list_dir`
List files in a directory via a native walker (not `ls`).

**Args:** `{"session_id":"agent","path":"/testbed","max_depth":2}`
**Result:** `{"entries":["/testbed","/testbed/file.py",...]}`
**Errors:** `session_not_found`, `file_not_found`.

## Ordering and concurrency

- Requests within a single session are processed **serially**. The sidecar does not start request N+1 in session X until request N has finished.
- Requests in **different sessions** are processed **concurrently**. Each session has its own bash child and its own request queue.
- The host may pipeline requests (send multiple before reading responses). The sidecar must handle this.
- Response order is not guaranteed across sessions. The host must correlate by `id`.

## Lifetime

- The sidecar starts when `docker exec -i /tmp/vett-sidecar` is invoked.
- Sends a `hello` greeting to the host automatically? **No.** The host sends the first `hello` request. Sidecar waits for it.
- When the host closes stdin, the sidecar exits. Any still-running bash children are killed with SIGKILL.
- When the container dies (host's `docker kill` or the container exits normally), the sidecar and all its bash children die with it.

## Reference implementation

The Go implementation lives at `internal/sidecar/` (library code) and `cmd/vett-sidecar/main.go` (the binary entrypoint). Both sides (host `sidecar.Client` and the sidecar binary) import the shared RPC types from `pkg/rpc/`. This guarantees at compile time that the two sides can't drift on field names or types.

The test `TestBashPassthroughIsBitExact` and `TestSidecarSessionsAreIsolated` lock the behavior specified here.
