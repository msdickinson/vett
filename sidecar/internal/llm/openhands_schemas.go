package llm

import (
	_ "embed"
	"encoding/json"
)

// The canonical openhands tool schemas, verbatim from a captured reference
// request (testdata/golden-schemas/*.json). Embedding the JSON bytes directly
// preserves per-property key order (e.g. file_editor.view_range uses
// [type, items, description] — NOT alphabetical). Struct marshaling can't
// produce this reliably, so we embed and trust.

//go:embed schemas/terminal.json
var schemaTerminal []byte

//go:embed schemas/file_editor.json
var schemaFileEditor []byte

//go:embed schemas/task_tracker.json
var schemaTaskTracker []byte

//go:embed schemas/finish.json
var schemaFinish []byte

//go:embed schemas/think.json
var schemaThink []byte

// OpenHandsToolSchemas returns the five openhands tool schemas as raw JSON,
// in the canonical wire order: terminal, file_editor, task_tracker, finish,
// think. The order itself is part of the mirror contract — changing it
// changes the request bytes.
func OpenHandsToolSchemas() []json.RawMessage {
	return []json.RawMessage{
		schemaTerminal,
		schemaFileEditor,
		schemaTaskTracker,
		schemaFinish,
		schemaThink,
	}
}

// schemasByKey maps tool keys to their embedded JSON schemas.
var schemasByKey = map[string]json.RawMessage{
	"terminal":     schemaTerminal,
	"file_editor":  schemaFileEditor,
	"task_tracker": schemaTaskTracker,
	"finish":       schemaFinish,
	"think":        schemaThink,
}

// ToolSchemaByKey returns the embedded JSON schema for a tool key.
// Returns nil if the key is unknown.
func ToolSchemaByKey(key string) json.RawMessage {
	return schemasByKey[key]
}

// ToolSchemasForKeys returns the schemas for the given tool keys, in
// order. Unknown keys are silently skipped.
func ToolSchemasForKeys(keys []string) []json.RawMessage {
	out := make([]json.RawMessage, 0, len(keys))
	for _, k := range keys {
		if s := schemasByKey[k]; s != nil {
			out = append(out, s)
		}
	}
	return out
}
