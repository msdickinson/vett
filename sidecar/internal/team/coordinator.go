package team

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/msdickinson/vett/sidecar/internal/agent"
	"github.com/msdickinson/vett/sidecar/internal/config"
	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/llm"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

// Coordinator runs a team: one leader agent with delegation tools,
// and N member agents that can be spawned on demand.
type Coordinator struct {
	Profile     *config.Profile
	Client      *llm.Client
	Model       string
	Sandbox     tool.Sandbox
	SessionID   string
	Bus         *eventbus.Bus
	Board       *Board
}

// TeamResult is the output of a team run.
type TeamResult struct {
	LeaderResult *agent.Result
	Tasks        []*Task
}

// Run executes the team flow: starts the leader agent loop with
// delegation tools, spawns members on demand.
func (c *Coordinator) Run(ctx context.Context, systemPrompt, userMessage string) (*TeamResult, error) {
	if c.Profile.Team == nil {
		return nil, fmt.Errorf("profile has no team configuration")
	}

	if c.Board == nil {
		c.Board = NewBoard()
	}

	// Build the member lookup.
	members := map[string]*config.MemberConfig{}
	for i := range c.Profile.Team.Members {
		m := &c.Profile.Team.Members[i]
		members[m.Name] = m
	}

	// RunMember spawns a member agent loop.
	runMember := func(ctx context.Context, memberName, taskDescription string) (string, error) {
		member, ok := members[memberName]
		if !ok {
			return "", fmt.Errorf("unknown team member %q (available: %s)", memberName, memberNames(members))
		}

		// Build member's tool map.
		memberTools := map[string]*tool.Tool{}
		memberToolKeys := make([]string, 0)
		for _, ref := range member.Tools {
			t := tool.Lookup(ref.Key)
			if t == nil {
				return "", fmt.Errorf("member %s: tool %q not registered", memberName, ref.Key)
			}
			name := ref.Name
			if name == "" {
				name = ref.Key
			}
			memberTools[name] = t
			memberToolKeys = append(memberToolKeys, ref.Key)
		}

		// Build member's middleware.
		var memberMWs []mw.Middleware
		mwRefs := member.Middleware
		if len(mwRefs) == 0 {
			mwRefs = c.Profile.Middleware // inherit from profile
		}
		for _, ref := range mwRefs {
			factory := mw.Lookup(ref.Name)
			if factory != nil {
				memberMWs = append(memberMWs, factory(ref.Config))
			}
		}

		// Merge LLM settings.
		temp := c.Profile.LLM.Temperature
		topP := c.Profile.LLM.TopP
		if member.LLM != nil {
			if member.LLM.Temperature != 0 {
				temp = member.LLM.Temperature
			}
			if member.LLM.TopP != 0 {
				topP = member.LLM.TopP
			}
		}

		maxIter := member.MaxIterations
		if maxIter == 0 {
			maxIter = 100
		}

		memberPrompt := member.SystemPrompt
		if memberPrompt == "" {
			memberPrompt = "You are a team member. Complete the task given to you."
		}

		// Run the member's agent loop.
		memberLoop := &agent.Loop{
			Client:        c.Client,
			Model:         c.Model,
			Sandbox:       c.Sandbox,
			SessionID:     c.SessionID,
			InstanceID:    memberName,
			Tools:         memberTools,
			WireTools:     llm.ToolSchemasForKeys(memberToolKeys),
			Middlewares:   memberMWs,
			Temperature:   temp,
			TopP:          topP,
			MaxIterations: maxIter,
			Bus:           c.Bus,
		}

		result, err := memberLoop.Run(ctx, memberPrompt, taskDescription)
		if err != nil {
			return "", fmt.Errorf("member %s: %w", memberName, err)
		}

		// Extract the member's final text output.
		return extractFinalContent(result), nil
	}

	// Build leader's tool map: profile tools + delegation tools.
	leaderTools := map[string]*tool.Tool{}
	leaderToolKeys := make([]string, 0)

	// Add profile-level tools (if leader has tools configured).
	leaderConfig := c.Profile.Team.Leader
	leaderToolRefs := leaderConfig.Tools
	if len(leaderToolRefs) == 0 {
		leaderToolRefs = c.Profile.Tools // inherit
	}
	for _, ref := range leaderToolRefs {
		t := tool.Lookup(ref.Key)
		if t != nil {
			name := ref.Name
			if name == "" {
				name = ref.Key
			}
			leaderTools[name] = t
			leaderToolKeys = append(leaderToolKeys, ref.Key)
		}
	}

	// Add delegation tools (always available to leader).
	for name, t := range LeaderToolset(c.Board, runMember) {
		leaderTools[name] = t
	}

	// Build wire schemas: profile tool schemas + leader tool schemas.
	wireTools := llm.ToolSchemasForKeys(leaderToolKeys)
	for _, schema := range LeaderToolSchemas() {
		wireTools = append(wireTools, json.RawMessage(schema))
	}

	// Build leader's middleware.
	var leaderMWs []mw.Middleware
	for _, ref := range c.Profile.Middleware {
		factory := mw.Lookup(ref.Name)
		if factory != nil {
			leaderMWs = append(leaderMWs, factory(ref.Config))
		}
	}

	// Merge leader LLM settings.
	temp := c.Profile.LLM.Temperature
	topP := c.Profile.LLM.TopP
	if leaderConfig.LLM != nil {
		if leaderConfig.LLM.Temperature != 0 {
			temp = leaderConfig.LLM.Temperature
		}
		if leaderConfig.LLM.TopP != 0 {
			topP = leaderConfig.LLM.TopP
		}
	}

	maxIter := leaderConfig.MaxIterations
	if maxIter == 0 {
		maxIter = 50
	}

	leaderPrompt := leaderConfig.SystemPrompt
	if leaderPrompt == "" {
		leaderPrompt = c.Profile.SystemPrompt
	}

	// Run the leader loop.
	leaderLoop := &agent.Loop{
		Client:        c.Client,
		Model:         c.Model,
		Sandbox:       c.Sandbox,
		SessionID:     c.SessionID,
		InstanceID:    "leader",
		Tools:         leaderTools,
		WireTools:     wireTools,
		Middlewares:   leaderMWs,
		Temperature:   temp,
		TopP:          topP,
		MaxIterations: maxIter,
		Bus:           c.Bus,
	}

	leaderResult, err := leaderLoop.Run(ctx, leaderPrompt, userMessage)

	return &TeamResult{
		LeaderResult: leaderResult,
		Tasks:        c.Board.All(),
	}, err
}

func extractFinalContent(result *agent.Result) string {
	if result == nil || len(result.Messages) == 0 {
		return ""
	}
	// Walk backward to find the last assistant message with text.
	for i := len(result.Messages) - 1; i >= 0; i-- {
		msg := result.Messages[i]
		if msg.Role == "assistant" && len(msg.Parts) > 0 {
			return msg.Parts[0].Text
		}
	}
	return ""
}

func memberNames(members map[string]*config.MemberConfig) string {
	names := make([]string, 0, len(members))
	for n := range members {
		names = append(names, n)
	}
	return fmt.Sprintf("%v", names)
}
