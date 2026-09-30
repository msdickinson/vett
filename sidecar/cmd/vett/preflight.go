package main

import (
	"context"
	"fmt"
	"net/http"
	"os"
	"time"

	"github.com/spf13/cobra"

	"github.com/msdickinson/vett/sidecar/internal/config"
	"github.com/msdickinson/vett/sidecar/internal/llm"
)

func preflightCmd() *cobra.Command {
	var (
		profileFlag  string
		endpointFlag string
		modelFlag    string
		strict       bool
	)
	cmd := &cobra.Command{
		Use:   "preflight",
		Short: "Verify endpoint, model, and profile before a benchmark run",
		RunE: func(cmd *cobra.Command, args []string) error {
			if profileFlag == "" {
				return fmt.Errorf("--profile is required")
			}
			profile, _, err := profileSource(profileFlag)
			if err != nil {
				return err
			}
			endpoint := pickEnv(endpointFlag, "VETT_LLM_ENDPOINT")
			model := pickEnv(modelFlag, "VETT_LLM_MODEL")
			if endpoint == "" {
				return fmt.Errorf("--endpoint or VETT_LLM_ENDPOINT is required")
			}
			if model == "" {
				return fmt.Errorf("--model or VETT_LLM_MODEL is required")
			}

			fmt.Printf("Vett preflight: %s @ %s\n", profile.Name, model)
			ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
			defer cancel()

			checks := 0
			warnings := 0
			errors := 0

			// Check 1: endpoint reachable.
			if err := checkEndpointReachable(ctx, endpoint); err != nil {
				fmt.Printf("  ✗ Endpoint reachable — %v\n", err)
				errors++
			} else {
				fmt.Printf("  ✓ Endpoint reachable\n")
			}
			checks++

			// Check 2: tool schema accepted — send a minimal request with
			// the full openhands tool set and a trivial user message.
			if err := checkToolSchemaAccepted(ctx, endpoint, model, profile); err != nil {
				fmt.Printf("  ✗ Tool schema accepted — %v\n", err)
				errors++
			} else {
				fmt.Printf("  ✓ Tool schema accepted (%d tools registered)\n", len(profile.Tools))
			}
			checks++

			passes := checks - errors - warnings
			fmt.Printf("\n%d ✓  %d ⚠  %d ✗\n", passes, warnings, errors)
			if errors > 0 {
				os.Exit(3)
			}
			if strict && warnings > 0 {
				os.Exit(10)
			}
			return nil
		},
	}
	cmd.Flags().StringVar(&profileFlag, "profile", "", "Profile name or path")
	cmd.Flags().StringVar(&endpointFlag, "endpoint", "", "LLM endpoint URL")
	cmd.Flags().StringVar(&modelFlag, "model", "", "LLM model name")
	cmd.Flags().BoolVar(&strict, "strict", false, "Exit nonzero on warnings")
	return cmd
}

func checkEndpointReachable(ctx context.Context, endpoint string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint+"/models", nil)
	if err != nil {
		return err
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 500 {
		return fmt.Errorf("endpoint returned %d", resp.StatusCode)
	}
	return nil
}

func checkToolSchemaAccepted(ctx context.Context, endpoint, model string, profile *config.Profile) error {
	client := llm.NewClient(endpoint, "")
	req := llm.BuildRequest(
		model,
		profile.SystemPrompt,
		"Respond briefly: ack",
		llm.OpenHandsToolSchemas(),
		profile.LLM.Temperature,
		profile.LLM.TopP,
	)
	resp, _, err := client.Send(ctx, req)
	if err != nil {
		return err
	}
	if len(resp.Choices) == 0 {
		return fmt.Errorf("endpoint returned zero choices")
	}
	return nil
}
