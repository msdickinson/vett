package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"

	"github.com/spf13/cobra"

	"github.com/msdickinson/vett/sidecar/internal/llm"
)


func analyzeCmd() *cobra.Command {
	var (
		endpointFlag string
		modelFlag    string
		apiKeyFlag   string
	)
	cmd := &cobra.Command{
		Use:   "analyze <results.json>",
		Short: "Analyze benchmark failures using an LLM",
		Long: `analyze reads a run results file and sends each failed instance
to an LLM for failure classification. Outputs a categorized report
of what went wrong (tool errors, wrong approach, context limits, etc.).`,
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			resultsPath := args[0]

			endpoint := pickEnv(endpointFlag, "VETT_LLM_ENDPOINT")
			model := pickEnv(modelFlag, "VETT_LLM_MODEL")
			apiKey := pickEnv(apiKeyFlag, "VETT_LLM_API_KEY")

			if endpoint == "" {
				return fmt.Errorf("--endpoint or VETT_LLM_ENDPOINT is required")
			}
			if model == "" {
				return fmt.Errorf("--model or VETT_LLM_MODEL is required")
			}

			// Load results.
			data, err := os.ReadFile(resultsPath)
			if err != nil {
				return fmt.Errorf("read results: %w", err)
			}

			var results struct {
				Instances []struct {
					InstanceID string `json:"instance_id"`
					EndReason  string `json:"end_reason"`
					Error      string `json:"error"`
					Patch      string `json:"patch"`
					Resolved   *bool  `json:"resolved"`
					Iterations int    `json:"iterations"`
				} `json:"instances"`
			}
			if err := json.Unmarshal(data, &results); err != nil {
				return fmt.Errorf("parse results: %w", err)
			}

			// Find failures.
			var failures []int
			for i, inst := range results.Instances {
				if inst.Resolved != nil && !*inst.Resolved {
					failures = append(failures, i)
				} else if inst.Error != "" {
					failures = append(failures, i)
				}
			}

			if len(failures) == 0 {
				fmt.Println("No failures found — all instances resolved or no resolution data.")
				return nil
			}

			fmt.Printf("Found %d failures to analyze...\n\n", len(failures))

			client := llm.NewClient(endpoint, apiKey)
			ctx := context.Background()

			categories := map[string][]string{}

			for _, idx := range failures {
				inst := results.Instances[idx]

				prompt := fmt.Sprintf(`Analyze this benchmark failure and categorize it.

Instance: %s
End reason: %s
Error: %s
Iterations used: %d
Patch generated: %v

Respond with exactly one category from:
- tool_error: A tool crashed or returned unexpected results
- wrong_approach: Agent took the wrong approach to solve the problem
- context_limit: Ran out of context or iterations
- test_flake: The test itself is flaky
- missing_info: Not enough information in the problem statement
- model_limitation: The model couldn't understand the codebase

Category:`, inst.InstanceID, inst.EndReason, inst.Error,
					inst.Iterations, inst.Patch != "")

				req := llm.Request{
					Model: model,
					Messages: []llm.Message{
						{Role: "user", Parts: []llm.ContentPart{{Type: "text", Text: prompt}}},
					},
					Temperature: 0.1,
					TopP:        0.95,
				}

				resp, _, err := client.Send(ctx, req)
				if err != nil {
					fmt.Printf("  %s: analysis failed: %v\n", inst.InstanceID, err)
					continue
				}

				category := "unknown"
				if len(resp.Choices) > 0 && resp.Choices[0].Message.Content != "" {
					category = resp.Choices[0].Message.Content
				}

				categories[category] = append(categories[category], inst.InstanceID)
				fmt.Printf("  %s → %s\n", inst.InstanceID, category)
			}

			fmt.Println("\n--- Summary ---")
			for cat, ids := range categories {
				fmt.Printf("  %s: %d instances\n", cat, len(ids))
			}

			return nil
		},
	}

	cmd.Flags().StringVar(&endpointFlag, "endpoint", "", "LLM endpoint URL")
	cmd.Flags().StringVar(&modelFlag, "model", "", "LLM model name")
	cmd.Flags().StringVar(&apiKeyFlag, "api-key", "", "LLM API key")

	return cmd
}

