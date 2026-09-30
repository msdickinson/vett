package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"strings"

	"github.com/spf13/cobra"

	"github.com/msdickinson/vett/sidecar/internal/sandbox"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

func callCmd() *cobra.Command {
	var (
		argsFlags []string
		jsonFlag  string
	)
	cmd := &cobra.Command{
		Use:   "call <tool-name>",
		Short: "Invoke a single tool for testing",
		Long: `call runs one tool with the given arguments and prints the result.
Useful for debugging tool implementations.

Examples:
  vett call terminal --arg command="ls -la"
  vett call file_editor --arg command_name=view --arg path=/testbed/main.py
  vett call think --json '{"thought":"testing"}'`,
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			toolName := args[0]

			t := tool.Lookup(toolName)
			if t == nil {
				fmt.Fprintf(os.Stderr, "Error: tool %q not registered\n", toolName)
				fmt.Fprintln(os.Stderr, "\nAvailable tools:")
				for key := range tool.All() {
					fmt.Fprintf(os.Stderr, "  %s\n", key)
				}
				return fmt.Errorf("tool %q not found", toolName)
			}

			// Parse arguments.
			argMap := map[string]any{}
			if jsonFlag != "" {
				if err := json.Unmarshal([]byte(jsonFlag), &argMap); err != nil {
					return fmt.Errorf("parse --json: %w", err)
				}
			}
			for _, a := range argsFlags {
				parts := strings.SplitN(a, "=", 2)
				if len(parts) != 2 {
					return fmt.Errorf("invalid --arg %q (expected key=value)", a)
				}
				argMap[parts[0]] = parts[1]
			}

			// Start a local sandbox for tools that need it.
			sidecarPath, err := resolveSidecarPath()
			if err != nil {
				return fmt.Errorf("resolve sidecar: %w", err)
			}

			ctx := context.Background()
			cwd, _ := os.Getwd()
			sb, err := sandbox.StartLocal(ctx, sandbox.LocalOptions{
				SidecarPath: sidecarPath,
				Cwd:         cwd,
				SessionName: "call",
			})
			if err != nil {
				return fmt.Errorf("start sandbox: %w", err)
			}
			defer sb.Close(context.Background())

			env := tool.Env{
				Sandbox:   sb.Client,
				SessionID: sb.SessionID,
				CallID:    "call-1",
			}

			fmt.Printf("Calling %s with args: %v\n\n", toolName, argMap)

			result, toolErr := t.Execute(ctx, argMap, env)

			if toolErr != nil {
				fmt.Printf("--- ERROR ---\n%v\n", toolErr)
			}
			fmt.Printf("--- RESULT ---\n%s\n", result)

			return nil
		},
	}

	cmd.Flags().StringArrayVar(&argsFlags, "arg", nil, "Tool argument as key=value (repeatable)")
	cmd.Flags().StringVar(&jsonFlag, "json", "", "Tool arguments as JSON object")

	return cmd
}
