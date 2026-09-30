package llm

import "encoding/json"

// BuildRequest constructs a Request for one LLM turn. Phase 1a uses this to
// prove wire-level parity against the captured reference; later phases call
// it from the agent loop with a full message history.
//
// systemPrompt and userMessage are the raw strings that become the two
// initial turns of the conversation. tools is the wire-order list of
// tool schema JSON blobs (use OpenHandsToolSchemas() for the openhands
// profile).
func BuildRequest(model, systemPrompt, userMessage string, tools []json.RawMessage, temperature, topP float64) Request {
	return Request{
		Model: model,
		Messages: []Message{
			{
				Role:  "system",
				Parts: []ContentPart{{Type: "text", Text: systemPrompt}},
			},
			{
				Role:  "user",
				Parts: []ContentPart{{Type: "text", Text: userMessage}},
			},
		},
		Tools:       tools,
		TopP:        topP,
		Temperature: temperature,
	}
}
