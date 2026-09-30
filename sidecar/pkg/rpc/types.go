// Package rpc defines the newline-JSON request/response protocol between
// the Vett host and the vett-sidecar subprocess. Both sides import this
// package so they can't drift on field names or types.
//
// See docs/sidecar-protocol.md for the canonical spec.
package rpc

import "encoding/json"

// Protocol version — bump on breaking changes.
const ProtocolVersion = 1

// Op names.
const (
	OpHello          = "hello"
	OpSessionCreate  = "session_create"
	OpSessionDestroy = "session_destroy"
	OpSessionList    = "session_list"
	OpBashExec       = "bash_exec"
	OpFileView       = "file_view"
	OpFileCreate     = "file_create"
	OpFileStrReplace = "file_str_replace"
	OpFileInsert     = "file_insert"
	OpFileUndo       = "file_undo"
	OpFileExists     = "file_exists"
	OpListDir        = "list_dir"
)

// Error codes.
const (
	ErrSessionNotFound    = "session_not_found"
	ErrSessionExists      = "session_exists"
	ErrInvalidArgs        = "invalid_args"
	ErrTimeout            = "timeout"
	ErrBashDead           = "bash_dead"
	ErrFileNotFound       = "file_not_found"
	ErrFileExists         = "file_exists"
	ErrStrReplaceNoMatch  = "str_replace_no_match"
	ErrStrReplaceMultiple = "str_replace_multi_match"
	ErrFileTooLarge       = "file_too_large"
	ErrUnknownOp          = "unknown_op"
	ErrInternal           = "internal"
)

// Request is the envelope the host sends.
type Request struct {
	ID   string          `json:"id"`
	Op   string          `json:"op"`
	Args json.RawMessage `json:"args"`
}

// Response is the envelope the sidecar sends back (non-streaming).
type Response struct {
	ID     string          `json:"id"`
	OK     bool            `json:"ok"`
	Result json.RawMessage `json:"result,omitempty"`
	Error  *Error          `json:"error,omitempty"`
}

// StreamChunk is a streaming response line for ops that support it.
// Mutually exclusive with a Response: a request either gets N StreamChunks
// followed by a final Response, or just a Response.
type StreamChunk struct {
	ID     string `json:"id"`
	Stream bool   `json:"stream"`
	Chunk  string `json:"chunk"`
}

// Error is the error details on a failed response.
type Error struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

// ---------------- hello ----------------

type HelloArgs struct {
	HostVersion     string `json:"host_version"`
	ProtocolVersion int    `json:"protocol_version"`
}

type HelloResult struct {
	SidecarVersion  string   `json:"sidecar_version"`
	ProtocolVersion int      `json:"protocol_version"`
	SupportedOps    []string `json:"supported_ops"`
}

// ---------------- sessions ----------------

type SessionCreateArgs struct {
	Name string            `json:"name"`
	Cwd  string            `json:"cwd,omitempty"`
	Env  map[string]string `json:"env,omitempty"`
}

type SessionCreateResult struct {
	Name string `json:"name"`
	PID  int    `json:"pid"`
}

type SessionDestroyArgs struct {
	Name string `json:"name"`
}

type SessionDestroyResult struct {
	Name string `json:"name"`
}

type SessionListResult struct {
	Sessions []SessionInfo `json:"sessions"`
}

type SessionInfo struct {
	Name string `json:"name"`
	PID  int    `json:"pid"`
	Cwd  string `json:"cwd"`
}

// ---------------- bash_exec ----------------

type BashExecArgs struct {
	SessionID      string `json:"session_id"`
	Command        string `json:"command"`
	TimeoutSeconds int    `json:"timeout_seconds,omitempty"`
	Stream         bool   `json:"stream,omitempty"`
}

type BashExecResult struct {
	Stdout   string `json:"stdout"`
	ExitCode int    `json:"exit_code"`
	Cwd      string `json:"cwd"`
	TimedOut bool   `json:"timed_out"`
}

// ---------------- file ops ----------------

type FileViewArgs struct {
	SessionID string `json:"session_id"`
	Path      string `json:"path"`
	ViewRange *[2]int `json:"view_range,omitempty"`
}

type FileViewResult struct {
	Content     string `json:"content"`
	IsDirectory bool   `json:"is_directory"`
}

type FileCreateArgs struct {
	SessionID string `json:"session_id"`
	Path      string `json:"path"`
	FileText  string `json:"file_text"`
}

type FileCreateResult struct {
	Content string `json:"content"`
}

type FileStrReplaceArgs struct {
	SessionID string `json:"session_id"`
	Path      string `json:"path"`
	OldStr    string `json:"old_str"`
	NewStr    string `json:"new_str"`
}

type FileStrReplaceResult struct {
	Content string `json:"content"`
}

type FileInsertArgs struct {
	SessionID  string `json:"session_id"`
	Path       string `json:"path"`
	InsertLine int    `json:"insert_line"`
	NewStr     string `json:"new_str"`
}

type FileInsertResult struct {
	Content string `json:"content"`
}

type FileUndoArgs struct {
	SessionID string `json:"session_id"`
	Path      string `json:"path"`
}

type FileUndoResult struct {
	Content string `json:"content"`
}

type FileExistsArgs struct {
	SessionID string `json:"session_id"`
	Path      string `json:"path"`
}

type FileExistsResult struct {
	Exists bool `json:"exists"`
	IsFile bool `json:"is_file"`
	IsDir  bool `json:"is_dir"`
}

type ListDirArgs struct {
	SessionID string `json:"session_id"`
	Path      string `json:"path"`
	MaxDepth  int    `json:"max_depth,omitempty"`
}

type ListDirResult struct {
	Entries []string `json:"entries"`
}
