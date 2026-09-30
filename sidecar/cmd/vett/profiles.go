package main

import (
	"fmt"

	"github.com/spf13/cobra"
)

func profilesCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "profiles [list|show <name>]",
		Short: "List or inspect profiles",
	}
	cmd.AddCommand(&cobra.Command{
		Use:   "list",
		Short: "List available profiles (workspace + built-in)",
		RunE: func(cmd *cobra.Command, args []string) error {
			for _, p := range listAllProfiles() {
				fmt.Println(p)
			}
			return nil
		},
	})
	cmd.AddCommand(&cobra.Command{
		Use:   "show <name>",
		Short: "Show a parsed profile config",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			p, source, err := profileSource(args[0])
			if err != nil {
				return err
			}
			fmt.Printf("source:         %s\n", source)
			fmt.Printf("name:           %s\n", p.Name)
			fmt.Printf("temperature:    %v\n", p.LLM.Temperature)
			fmt.Printf("top_p:          %v\n", p.LLM.TopP)
			fmt.Printf("max_iterations: %d\n", p.MaxIterations)
			fmt.Printf("tools:\n")
			for _, t := range p.Tools {
				fmt.Printf("  - %s\n", t.Name)
			}
			fmt.Printf("middleware:\n")
			for _, m := range p.Middleware {
				fmt.Printf("  - %s\n", m.Name)
			}
			fmt.Printf("system_prompt:  %d chars\n", len(p.SystemPrompt))
			return nil
		},
	})
	cmd.RunE = cmd.Commands()[0].RunE
	return cmd
}

func suitesCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "suites [list|show <name>]",
		Short: "List or inspect suites",
	}
	cmd.AddCommand(&cobra.Command{
		Use:   "list",
		Short: "List available suites (workspace + built-in)",
		RunE: func(cmd *cobra.Command, args []string) error {
			for _, s := range listAllSuites() {
				fmt.Println(s)
			}
			return nil
		},
	})
	cmd.AddCommand(&cobra.Command{
		Use:   "show <name>",
		Short: "Show a parsed suite config",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			s, source, _, err := suiteSource(args[0])
			if err != nil {
				return err
			}
			fmt.Printf("source: %s\n", source)
			fmt.Printf("name:   %s\n", s.Name)
			fmt.Printf("loader: %s\n", s.Loader.Type)
			fmt.Printf("image:  %s\n", s.Sandbox.ImageTemplate)
			fmt.Printf("scorer: %s\n", s.Scorer.Type)
			return nil
		},
	})
	cmd.RunE = cmd.Commands()[0].RunE
	return cmd
}
