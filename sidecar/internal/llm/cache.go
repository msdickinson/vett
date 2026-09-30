package llm

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"sync"
)

// CacheMode controls how the LLM cache behaves.
type CacheMode int

const (
	// CacheCaptureOnly logs all calls but never returns cached results.
	CacheCaptureOnly CacheMode = iota
	// CacheReplayStrict returns cache hits; errors on miss.
	CacheReplayStrict
	// CacheReplayFallback returns cache hits; calls LLM on miss and caches.
	CacheReplayFallback
)

// CacheEntry is one request/response pair stored in the cache.
type CacheEntry struct {
	RequestHash string    `json:"request_hash"`
	InstanceID  string    `json:"instance_id,omitempty"`
	Iteration   int       `json:"iteration,omitempty"`
	Request     Request   `json:"request"`
	Response    *Response `json:"response"`
}

// Cache wraps an LLM client with a request/response cache for
// deterministic replay of benchmark runs.
type Cache struct {
	inner   *Client
	mode    CacheMode
	entries []CacheEntry
	index   map[string]int // hash → index into entries
	mu      sync.Mutex

	// CurrentInstanceID and CurrentIteration are set by the runner
	// to tag cache entries for debugging.
	CurrentInstanceID string
	CurrentIteration  int
}

// NewCache wraps a client with caching.
func NewCache(client *Client, mode CacheMode) *Cache {
	return &Cache{
		inner:   client,
		mode:    mode,
		entries: nil,
		index:   make(map[string]int),
	}
}

// LoadFrom reads cached entries from a JSON file.
func (c *Cache) LoadFrom(path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	var entries []CacheEntry
	if err := json.Unmarshal(data, &entries); err != nil {
		return err
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.entries = entries
	c.index = make(map[string]int, len(entries))
	for i, e := range entries {
		c.index[e.RequestHash] = i
	}
	return nil
}

// SaveTo writes all cached entries to a JSON file.
func (c *Cache) SaveTo(path string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	data, err := json.MarshalIndent(c.entries, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, data, 0o644)
}

// Send handles a request according to the cache mode.
func (c *Cache) Send(ctx context.Context, req Request) (*Response, []byte, error) {
	hash := hashRequest(req)

	c.mu.Lock()
	idx, hit := c.index[hash]
	c.mu.Unlock()

	switch c.mode {
	case CacheReplayStrict:
		if !hit {
			return nil, nil, fmt.Errorf("llm cache miss (strict mode): hash=%s", hash)
		}
		c.mu.Lock()
		resp := c.entries[idx].Response
		c.mu.Unlock()
		return resp, nil, nil

	case CacheReplayFallback:
		if hit {
			c.mu.Lock()
			resp := c.entries[idx].Response
			c.mu.Unlock()
			return resp, nil, nil
		}
		// Fall through to real call.

	case CacheCaptureOnly:
		// Always call the real LLM.
	}

	// Real LLM call.
	resp, raw, err := c.inner.Send(ctx, req)
	if err != nil {
		return nil, raw, err
	}

	// Store in cache.
	entry := CacheEntry{
		RequestHash: hash,
		InstanceID:  c.CurrentInstanceID,
		Iteration:   c.CurrentIteration,
		Request:     req,
		Response:    resp,
	}
	c.mu.Lock()
	c.index[hash] = len(c.entries)
	c.entries = append(c.entries, entry)
	c.mu.Unlock()

	return resp, raw, nil
}

// EntryCount returns the number of cached entries.
func (c *Cache) EntryCount() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.entries)
}

func hashRequest(req Request) string {
	// Hash the deterministic parts of the request.
	h := sha256.New()
	h.Write([]byte(req.Model))
	h.Write([]byte(fmt.Sprintf("%.6f", req.Temperature)))
	h.Write([]byte(fmt.Sprintf("%.6f", req.TopP)))
	for _, msg := range req.Messages {
		raw, _ := json.Marshal(msg)
		h.Write(raw)
	}
	for _, tool := range req.Tools {
		h.Write([]byte(tool))
	}
	return hex.EncodeToString(h.Sum(nil))
}
