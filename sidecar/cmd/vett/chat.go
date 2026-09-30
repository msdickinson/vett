package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"

	"github.com/spf13/cobra"

	"github.com/msdickinson/vett/sidecar/internal/agent"
	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/llm"
	"github.com/msdickinson/vett/sidecar/internal/plugin"
	"github.com/msdickinson/vett/sidecar/internal/sandbox"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

func chatCmd() *cobra.Command {
	var (
		profileFlag  string
		endpointFlag string
		modelFlag    string
		apiKeyFlag   string
		stdioFlag    bool
		cwdFlag      string
	)
	cmd := &cobra.Command{
		Use:   "chat",
		Short: "Start an interactive coding agent in the current directory",
		Long: `chat starts a local interactive coding session. The agent runs
natively on your machine (no Docker) with the same tools, middleware,
and LLM client as the benchmark harness.

In terminal mode (default), you type messages and see the agent work
in your terminal. In stdio mode (--stdio), the agent communicates via
newline-delimited JSON on stdin/stdout, designed for the VETT-CHAT
VSCode extension.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			// Resolve working directory.
			cwd := cwdFlag
			if cwd == "" {
				wd, err := os.Getwd()
				if err != nil {
					return fmt.Errorf("get working directory: %w", err)
				}
				cwd = wd
			}
			absCwd, err := filepath.Abs(cwd)
			if err != nil {
				return fmt.Errorf("resolve cwd: %w", err)
			}
			cwd = absCwd

			// Resolve profile.
			profile, _, err := profileSource(profileFlag)
			if err != nil {
				return fmt.Errorf("load profile %q: %w", profileFlag, err)
			}

			// Resolve LLM settings.
			endpoint := pickEnv(endpointFlag, "VETT_LLM_ENDPOINT")
			model := pickEnv(modelFlag, "VETT_LLM_MODEL")
			apiKey := pickEnv(apiKeyFlag, "VETT_LLM_API_KEY")
			if endpoint == "" {
				return fmt.Errorf("--endpoint or VETT_LLM_ENDPOINT is required")
			}
			if model == "" {
				return fmt.Errorf("--model or VETT_LLM_MODEL is required")
			}

			// Resolve sidecar binary.
			sidecarPath, err := resolveSidecarPath()
			if err != nil {
				return fmt.Errorf("resolve sidecar: %w", err)
			}

			// Ctrl+C handling.
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			sigs := make(chan os.Signal, 1)
			signal.Notify(sigs, os.Interrupt, syscall.SIGTERM)
			defer signal.Stop(sigs)
			go func() {
				<-sigs
				cancel()
				<-sigs
				os.Exit(130)
			}()

			// Start local sandbox.
			sb, err := sandbox.StartLocal(ctx, sandbox.LocalOptions{
				SidecarPath: sidecarPath,
				Cwd:         cwd,
				SessionName: "chat",
			})
			if err != nil {
				return fmt.Errorf("start local sandbox: %w", err)
			}
			defer sb.Close(context.Background())

			// Warm up workspace plugins (tools + middleware).
			mgr := plugin.NewManager(cwd)
			pluginResults, _ := mgr.WarmUp(ctx)
			defer mgr.Close()

			// Report plugin status.
			for _, r := range pluginResults {
				if r.Error != nil {
					fmt.Fprintf(os.Stderr, "[plugins] %s ✗ %v\n", r.Name, r.Error)
				} else if r.Skipped != "" {
					fmt.Fprintf(os.Stderr, "[plugins] %s ⚠ %s\n", r.Name, r.Skipped)
				} else if r.Cached {
					fmt.Fprintf(os.Stderr, "[plugins] %s ✓ cached (%s)\n", r.Name, r.Kind)
				} else if r.Built {
					fmt.Fprintf(os.Stderr, "[plugins] %s ✓ built (%s)\n", r.Name, r.Kind)
				}
			}

			// Build tool map from profile (built-in tools).
			toolMap := map[string]*tool.Tool{}
			toolKeys := make([]string, 0, len(profile.Tools))
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

			// Register external plugin tools — wrap each as a tool.Tool.
			var wireSchemas []json.RawMessage
			for _, name := range mgr.ToolNames() {
				lp := mgr.GetTool(name)
				if lp == nil {
					continue
				}
				proc := lp.Process
				toolMap[name] = &tool.Tool{
					Key:  name,
					Name: name,
					Execute: func(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
						return proc.ExecuteTool(env.CallID, env.SessionID, args)
					},
				}
				wireSchemas = append(wireSchemas, mgr.GenerateSchema(lp.Meta))
			}

			// Build middleware chain.
			mws := make([]mw.Middleware, 0, len(profile.Middleware))
			for _, ref := range profile.Middleware {
				factory := mw.Lookup(ref.Name)
				if factory == nil {
					return fmt.Errorf("middleware %q not registered", ref.Name)
				}
				mws = append(mws, factory(ref.Config))
			}

			// Build LLM client.
			client := llm.NewClient(endpoint, apiKey)

			// Build event bus.
			bus := eventbus.NewBus()
			defer bus.Close()

			// Combine built-in + plugin tool schemas.
			allSchemas := llm.ToolSchemasForKeys(toolKeys)
			allSchemas = append(allSchemas, wireSchemas...)

			// Build agent loop.
			loop := &agent.Loop{
				Client:        client,
				Model:         model,
				Sandbox:       sb.Client,
				SessionID:     sb.SessionID,
				Tools:         toolMap,
				WireTools:     allSchemas,
				Middlewares:   mws,
				Temperature:   profile.LLM.Temperature,
				TopP:          profile.LLM.TopP,
				MaxIterations: profile.MaxIterations,
				Bus:           bus,
			}

			systemPrompt := profile.SystemPrompt

			if stdioFlag {
				return stdioChatLoop(ctx, loop, sb, bus, systemPrompt, cwd)
			}
			return terminalChatLoop(ctx, loop, sb, bus, systemPrompt, cwd)
		},
	}
	cmd.Flags().StringVar(&profileFlag, "profile", "coding", "Profile name or path (default: coding)")
	cmd.Flags().StringVar(&endpointFlag, "endpoint", "", "LLM endpoint URL (or VETT_LLM_ENDPOINT)")
	cmd.Flags().StringVar(&modelFlag, "model", "", "LLM model name (or VETT_LLM_MODEL)")
	cmd.Flags().StringVar(&apiKeyFlag, "api-key", "", "LLM API key (or VETT_LLM_API_KEY)")
	cmd.Flags().BoolVar(&stdioFlag, "stdio", false, "JSON protocol on stdin/stdout (for VSCode extension)")
	cmd.Flags().StringVar(&cwdFlag, "cwd", "", "Working directory (default: current dir)")
	return cmd
}
