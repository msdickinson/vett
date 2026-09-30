//go:build integration

package llm_test

import (
	"context"
	"os"
	"path/filepath"
	"runtime"
	"testing"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/llm"
)

// TestLiveWireAccept — Full tier. Sends Vett's openhands turn-1 request
// against the real LLM endpoint and asserts the server accepts it. This
// is the "endpoint understands our bytes" wire smoke check from
// docs/test-strategy.md §Layer 3b.
//
// Skip conditions:
//   - VETT_SKIP_LIVE_LLM_TESTS=1
//   - VETT_LLM_ENDPOINT / VETT_LLM_MODEL env vars unset
func TestLiveWireAccept(t *testing.T) {
	if os.Getenv("VETT_SKIP_LIVE_LLM_TESTS") == "1" {
		t.Skip("VETT_SKIP_LIVE_LLM_TESTS=1")
	}
	endpoint := os.Getenv("VETT_LLM_ENDPOINT")
	model := os.Getenv("VETT_LLM_MODEL")
	if endpoint == "" || model == "" {
		t.Skip("set VETT_LLM_ENDPOINT and VETT_LLM_MODEL to run this test")
	}

	// Load the captured system prompt so we exercise the exact
	// openhands bytes openhands-sdk sends.
	_, thisFile, _, _ := runtime.Caller(0)
	root := filepath.Clean(filepath.Join(filepath.Dir(thisFile), "..", ".."))
	sysPromptBytes, err := os.ReadFile(filepath.Join(root, "testdata", "ref-system-prompt.txt"))
	if err != nil {
		t.Fatalf("read system prompt: %v", err)
	}

	client := llm.NewClient(endpoint, os.Getenv("VETT_LLM_API_KEY"))
	req := llm.BuildRequest(
		model,
		string(sysPromptBytes),
		"Respond briefly with just the word: ack",
		llm.OpenHandsToolSchemas(),
		1.0,  // canonical temp per §2
		0.95, // canonical top_p
	)

	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	resp, _, err := client.Send(ctx, req)
	if err != nil {
		t.Fatalf("live send: %v", err)
	}
	if len(resp.Choices) == 0 {
		t.Fatal("endpoint returned zero choices")
	}
	if resp.Usage.PromptTokens == 0 {
		t.Errorf("expected non-zero prompt_tokens in usage, got %+v", resp.Usage)
	}
}
