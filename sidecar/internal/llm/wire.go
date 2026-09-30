// Package llm is the wire-format layer. It defines the exact JSON shape Vett
// sends to an OpenAI-compatible endpoint (vLLM/litellm) and owns the HTTP
// client that ships requests across.
//
// Mirror discipline: the byte layout of Request must match what openhands-sdk
// sends on the wire. Struct field order controls JSON key order. Do not
// reorder fields without running the golden tests.
package llm

import (
	"bytes"
	"encoding/json"
	"fmt"
)

// Request is the top-level body Vett POSTs to /v1/chat/completions.
//
// Wire key order (observed in the reference capture, after stripping
// litellm-internal kwargs): model, messages, tools, top_p, temperature.
// Struct field order must match exactly.
type Request struct {
	Model       string            `json:"model"`
	Messages    []Message         `json:"messages"`
	Tools       []json.RawMessage `json:"tools"`
	TopP        float64           `json:"top_p"`
	Temperature float64           `json:"temperature"`
}

// Message is one entry in the chat. System, user, and assistant messages
// carry structured content (a list of ContentPart). Tool-role messages
// carry a plain string. The distinction matters for the wire format, and
// Message marshals itself based on Role.
type Message struct {
	Role       string        `json:"-"`
	Parts      []ContentPart `json:"-"`
	Text       string        `json:"-"` // used for tool-role messages
	ToolCallID string        `json:"-"` // used for tool-role messages
	ToolCalls  []ToolCall    `json:"-"` // used for assistant messages with tool calls
}

// ContentPart is one structured segment of a message's content.
//
// Wire key order from the captured reference: {type, text}. Note: the
// openhands-reference-spec.md §1 text currently claims alphabetical
// ("text first"), but the captured bytes in testdata/ref-requests show
// type first. Capture is the source of truth.
type ContentPart struct {
	Type string `json:"type"`
	Text string `json:"text"`
}

// ToolCall is one emitted call on an assistant message. Echoed back to
// the LLM on subsequent turns. Matches OpenAI's tool-call schema.
type ToolCall struct {
	ID       string      `json:"id"`
	Type     string      `json:"type"`
	Function ToolCallFn  `json:"function"`
}

type ToolCallFn struct {
	Name      string `json:"name"`
	Arguments string `json:"arguments"`
}

// MarshalJSON emits:
//   tool messages:     {"content":"<text>","role":"tool","tool_call_id":"..."}
//   assistant + calls: {"content":null,"role":"assistant","tool_calls":[...]}
//   others:            {"content":[{...}],"role":"..."}
// Key order is {content, role, (extras)} — alphabetical-ish, matching
// the captured reference.
func (m Message) MarshalJSON() ([]byte, error) {
	var buf bytes.Buffer
	buf.WriteByte('{')

	// content first
	buf.WriteString(`"content":`)
	switch m.Role {
	case "tool":
		textJSON, err := json.Marshal(m.Text)
		if err != nil {
			return nil, err
		}
		buf.Write(textJSON)
	default:
		if len(m.Parts) > 0 {
			partsJSON, err := json.Marshal(m.Parts)
			if err != nil {
				return nil, err
			}
			buf.Write(partsJSON)
		} else if m.Role == "assistant" && len(m.ToolCalls) > 0 {
			// Assistant emitting tool calls with no textual content.
			buf.WriteString(`null`)
		} else {
			buf.WriteString(`[]`)
		}
	}

	// role
	buf.WriteString(`,"role":`)
	roleJSON, err := json.Marshal(m.Role)
	if err != nil {
		return nil, err
	}
	buf.Write(roleJSON)

	// tool_call_id for tool-role messages
	if m.Role == "tool" && m.ToolCallID != "" {
		buf.WriteString(`,"tool_call_id":`)
		idJSON, err := json.Marshal(m.ToolCallID)
		if err != nil {
			return nil, err
		}
		buf.Write(idJSON)
	}

	// tool_calls for assistant messages
	if m.Role == "assistant" && len(m.ToolCalls) > 0 {
		buf.WriteString(`,"tool_calls":`)
		callsJSON, err := json.Marshal(m.ToolCalls)
		if err != nil {
			return nil, err
		}
		buf.Write(callsJSON)
	}

	buf.WriteByte('}')
	return buf.Bytes(), nil
}

// UnmarshalJSON is a helper for tests that want to read turn-1-style
// golden JSON back into a Message. Only handles system/user/assistant
// messages with structured content.
func (m *Message) UnmarshalJSON(data []byte) error {
	var raw struct {
		Role       string          `json:"role"`
		Content    json.RawMessage `json:"content"`
		ToolCallID string          `json:"tool_call_id,omitempty"`
		ToolCalls  []ToolCall      `json:"tool_calls,omitempty"`
	}
	if err := json.Unmarshal(data, &raw); err != nil {
		return err
	}
	m.Role = raw.Role
	m.ToolCallID = raw.ToolCallID
	m.ToolCalls = raw.ToolCalls
	// Content can be a string (tool) or array (others) or null.
	trimmed := bytes.TrimSpace(raw.Content)
	if len(trimmed) == 0 || bytes.Equal(trimmed, []byte("null")) {
		return nil
	}
	if trimmed[0] == '"' {
		var s string
		if err := json.Unmarshal(raw.Content, &s); err != nil {
			return err
		}
		m.Text = s
		return nil
	}
	if trimmed[0] == '[' {
		return json.Unmarshal(raw.Content, &m.Parts)
	}
	return fmt.Errorf("unexpected content shape: %s", string(trimmed))
}
