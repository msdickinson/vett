package llm

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// Client is an OpenAI-compatible chat completions client. One per agent loop.
type Client struct {
	Endpoint   string        // e.g. "http://old-gpu-a:8000/v1"
	APIKey     string        // optional; sent as "Authorization: Bearer <key>" if non-empty
	HTTPClient *http.Client  // nil means a default client is constructed
	Timeout    time.Duration // per-request timeout, default 300s if zero
}

// NewClient returns a Client with the given endpoint and api key. The
// returned client has its own http.Client — callers must not share.
//
// The default 900s (15 min) timeout is chosen because qwen3-coder-next
// on GPU-2 with a large accumulated context (200K+ input tokens, common
// by iteration 15+ on a real SWE-bench instance) can take several
// minutes for a single chat/completions call. The older 300s default
// was caught failing real SWE-bench runs mid-loop. Per-instance wall
// clock is still bounded by Profile.TimeoutMinutes in the runner.
func NewClient(endpoint, apiKey string) *Client {
	return &Client{
		Endpoint:   strings.TrimRight(endpoint, "/"),
		APIKey:     apiKey,
		HTTPClient: &http.Client{Timeout: 900 * time.Second},
		Timeout:    900 * time.Second,
	}
}

// Response is the parsed OpenAI-compatible /chat/completions response. Phase
// 1a only needs the structural fields — the agent loop in Phase 1c will
// consume tool_calls.
type Response struct {
	ID      string   `json:"id"`
	Choices []Choice `json:"choices"`
	Usage   Usage    `json:"usage"`
}

type Choice struct {
	Index        int         `json:"index"`
	FinishReason string      `json:"finish_reason"`
	Message      RespMessage `json:"message"`
}

type RespMessage struct {
	Role      string         `json:"role"`
	Content   string         `json:"content"`
	ToolCalls []RespToolCall `json:"tool_calls,omitempty"`
}

type RespToolCall struct {
	ID       string       `json:"id"`
	Type     string       `json:"type"`
	Function RespToolFunc `json:"function"`
}

type RespToolFunc struct {
	Name      string `json:"name"`
	Arguments string `json:"arguments"`
}

type Usage struct {
	PromptTokens     int `json:"prompt_tokens"`
	CompletionTokens int `json:"completion_tokens"`
	TotalTokens      int `json:"total_tokens"`
}

// Send posts the given Request and decodes the response. Returns the raw
// outgoing body for trace capture.
func (c *Client) Send(ctx context.Context, req Request) (*Response, []byte, error) {
	body, err := MarshalRequest(req)
	if err != nil {
		return nil, nil, fmt.Errorf("marshal request: %w", err)
	}

	httpReq, err := http.NewRequestWithContext(ctx, http.MethodPost, c.Endpoint+"/chat/completions", bytes.NewReader(body))
	if err != nil {
		return nil, body, fmt.Errorf("build http request: %w", err)
	}
	httpReq.Header.Set("Content-Type", "application/json")
	if c.APIKey != "" {
		httpReq.Header.Set("Authorization", "Bearer "+c.APIKey)
	}

	resp, err := c.HTTPClient.Do(httpReq)
	if err != nil {
		return nil, body, fmt.Errorf("http post: %w", err)
	}
	defer resp.Body.Close()

	respBody, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, body, fmt.Errorf("read response: %w", err)
	}
	if resp.StatusCode >= 400 {
		return nil, body, fmt.Errorf("llm endpoint returned %d: %s", resp.StatusCode, truncate(string(respBody), 512))
	}

	var parsed Response
	if err := json.Unmarshal(respBody, &parsed); err != nil {
		return nil, body, fmt.Errorf("decode response: %w; body=%s", err, truncate(string(respBody), 512))
	}
	return &parsed, body, nil
}

// MarshalRequest serializes a Request to JSON. The encoder is configured to
// skip HTML-escaping (openhands-sdk does not escape < > & in its output) so
// byte-level mirror tests compare cleanly.
func MarshalRequest(req Request) ([]byte, error) {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	if err := enc.Encode(req); err != nil {
		return nil, err
	}
	// json.Encoder.Encode appends a trailing newline; strip it — the raw
	// request body sent over HTTP does not include one, and golden tests
	// compare structurally (parsed) rather than by trailing whitespace.
	out := buf.Bytes()
	if n := len(out); n > 0 && out[n-1] == '\n' {
		out = out[:n-1]
	}
	return out, nil
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "...(truncated)"
}
