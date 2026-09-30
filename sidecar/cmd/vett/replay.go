package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"

	"github.com/spf13/cobra"

	"github.com/msdickinson/vett/sidecar/internal/agent"
	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/llm"
	"github.com/msdickinson/vett/sidecar/internal/sandbox"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

func replayCmd() *cobra.Command {
	var (
		cachePath   string
		profileFlag string
		instanceID  string
		strict      bool
		sidecarFlag string
	)
	cmd := &cobra.Command{
		Use:   "replay",
		Short: "Replay a benchmark instance using cached LLM responses",
		Long: `replay re-runs a single instance using stored LLM responses from a
previous run. This produces deterministic output for debugging — the
agent takes the exact same actions as the original run.

Modes:
  --strict    Error on cache miss (fully deterministic)
  --fallback  Use cache when available, call LLM on miss (default)`,
		RunE: func(cmd *cobra.Command, args []string) error {
			if cachePath == "" {
				return fmt.Errorf("--cache is required (path to .llm-cache.json)")
			}
			if profileFlag == "" {
				return fmt.Errorf("--profile is required")
			}

			profile, _, err := profileSource(profileFlag)
			if err != nil {
				return fmt.Errorf("load profile: %w", err)
			}

			endpoint := pickEnv("", "VETT_LLM_ENDPOINT")
			model := pickEnv("", "VETT_LLM_MODEL")
			apiKey := pickEnv("", "VETT_LLM_API_KEY")

			if !strict && endpoint == "" {
				return fmt.Errorf("--endpoint or VETT_LLM_ENDPOINT required for fallback mode (use --strict for cache-only)")
			}

			// Load cache.
			client := llm.NewClient(endpoint, apiKey)
			mode := llm.CacheReplayFallback
			if strict {
				mode = llm.CacheReplayStrict
			}
			cache := llm.NewCache(client, mode)
			if err := cache.LoadFrom(cachePath); err != nil {
				return fmt.Errorf("load cache %s: %w", cachePath, err)
			}
			fmt.Printf("Loaded %d cached LLM responses from %s\n", cache.EntryCount(), cachePath)

			if instanceID != "" {
				cache.CurrentInstanceID = instanceID
			}

			// Resolve sidecar.
			sidecarPath := sidecarFlag
			if sidecarPath == "" {
				resolved, err := resolveSidecarPath()
				if err != nil {
					return fmt.Errorf("resolve sidecar: %w", err)
				}
				sidecarPath = resolved
			}

			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			sigs := make(chan os.Signal, 1)
			signal.Notify(sigs, os.Interrupt, syscall.SIGTERM)
			defer signal.Stop(sigs)
			go func() { <-sigs; cancel(); <-sigs; os.Exit(130) }()

			cwd, _ := os.Getwd()
			sb, err := sandbox.StartLocal(ctx, sandbox.LocalOptions{
				SidecarPath: sidecarPath,
				Cwd:         cwd,
				SessionName: "replay",
			})
			if err != nil {
				return fmt.Errorf("start sandbox: %w", err)
			}
			defer sb.Close(context.Background())

			// Build tools and middleware.
			toolMap := map[string]*tool.Tool{}
			toolKeys := make([]string, 0)
			for _, ref := range profile.Tools {
				t := tool.Lookup(ref.Key)
				if t == nil {
					return fmt.Errorf("tool %q not registered", ref.Key)
				}
				name := ref.Name
				if name == "" {
					name = ref.Key
				}
				toolMap[name] = t
				toolKeys = append(toolKeys, ref.Key)
			}

			mws := make([]mw.Middleware, 0)
			for _, ref := range profile.Middleware {
				factory := mw.Lookup(ref.Name)
				if factory == nil {
					return fmt.Errorf("middleware %q not registered", ref.Name)
				}
				mws = append(mws, factory(ref.Config))
			}

			bus := eventbus.NewBus()
			defer bus.Close()

			// Subscribe to events for progress display.
			events := bus.Subscribe(64, true)
			go func() {
				for e := range events {
					switch e.Type {
					case eventbus.IterationStart:
						iter, _ := e.Data["iteration"].(int)
						fmt.Printf("  iteration %d...\n", iter)
					case eventbus.ToolCallEnd:
						name, _ := e.Data["tool_name"].(string)
						success, _ := e.Data["success"].(bool)
						icon := "✓"
						if !success {
							icon = "✗"
						}
						fmt.Printf("    %s %s\n", icon, name)
					}
				}
			}()

			loop := &agent.Loop{
				Client:        client, // cache wraps this at the Send level
				Model:         model,
				Sandbox:       sb.Client,
				SessionID:     sb.SessionID,
				Tools:         toolMap,
				WireTools:     llm.ToolSchemasForKeys(toolKeys),
				Middlewares:   mws,
				Temperature:   profile.LLM.Temperature,
				TopP:          profile.LLM.TopP,
				MaxIterations: profile.MaxIterations,
				Bus:           bus,
			}

			userMsg := "Replay instance"
			if instanceID != "" {
				userMsg = fmt.Sprintf("Replay instance %s", instanceID)
			}

			fmt.Printf("Replaying with profile %q (mode: %s)...\n",
				profile.Name, map[bool]string{true: "strict", false: "fallback"}[strict])

			result, err := loop.Run(ctx, profile.SystemPrompt, userMsg)
			if err != nil {
				fmt.Fprintf(os.Stderr, "replay error: %v\n", err)
			}

			if result != nil {
				fmt.Printf("\nReplay complete: %d iterations, %d/%d tokens, reason: %s\n",
					result.Iterations, result.InputTokens, result.OutputTokens, result.StopReason)
			}

			// Save updated cache (for fallback mode — new entries appended).
			outCache := filepath.Join(filepath.Dir(cachePath), "replay-cache.json")
			if err := cache.SaveTo(outCache); err != nil {
				fmt.Fprintf(os.Stderr, "warning: could not save replay cache: %v\n", err)
			}

			return nil
		},
	}

	cmd.Flags().StringVar(&cachePath, "cache", "", "Path to .llm-cache.json from a previous run")
	cmd.Flags().StringVar(&profileFlag, "profile", "", "Profile name or path")
	cmd.Flags().StringVar(&instanceID, "instance-id", "", "Instance ID to replay (optional, for tagging)")
	cmd.Flags().BoolVar(&strict, "strict", false, "Error on cache miss (default: fallback to LLM)")
	cmd.Flags().StringVar(&sidecarFlag, "sidecar", "", "Path to sidecar binary")

	return cmd
}
