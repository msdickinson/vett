package sandbox

import "github.com/msdickinson/vett/sidecar/pkg/tool"

// Compile-time check: Client must satisfy tool.Sandbox so the agent
// loop can pass a *Client directly into tool dispatch without adapters.
var _ tool.Sandbox = (*Client)(nil)
