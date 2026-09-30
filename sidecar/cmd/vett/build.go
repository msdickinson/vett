package main

import (
	"context"
	"fmt"
	"os"

	"github.com/spf13/cobra"

	"github.com/msdickinson/vett/sidecar/internal/plugin"
)

func buildCmd() *cobra.Command {
	return &cobra.Command{
		Use:   "build",
		Short: "Build all workspace plugins (tools and middleware)",
		Long: `build scans the workspace tools/ and middlewares/ directories,
detects the language of each file, checks for the required SDK, and
compiles anything that needs compiling. Interpreted languages (Python,
JavaScript) get a syntax check.

Results are cached — unchanged files are not rebuilt.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			wd, err := os.Getwd()
			if err != nil {
				return err
			}

			mgr := plugin.NewManager(wd)
			results := mgr.Build(context.Background())

			var built, cached, skipped, errored int
			for _, r := range results {
				icon := "✓"
				status := "built"
				if r.Error != nil {
					icon = "✗"
					status = r.Error.Error()
					errored++
				} else if r.Skipped != "" {
					icon = "⚠"
					status = "skipped: " + r.Skipped
					skipped++
				} else if r.Cached {
					icon = "✓"
					status = "cached"
					cached++
				} else {
					built++
				}

				kindTag := string(r.Kind)
				fmt.Printf("  %s %-20s [%s] %s (%s)\n", icon, r.Name, r.Language, status, kindTag)
			}

			fmt.Println()
			total := built + cached + skipped + errored
			if total == 0 {
				fmt.Println("No plugins found in tools/ or middlewares/.")
				fmt.Println("Add files to get started:")
				fmt.Println("  tools/search.py        — Python tool")
				fmt.Println("  tools/lint.go           — Go tool")
				fmt.Println("  middlewares/limiter.py  — Python middleware")
				return nil
			}

			fmt.Printf("%d/%d ready", built+cached, total)
			if errored > 0 {
				fmt.Printf(", %d error(s)", errored)
			}
			if skipped > 0 {
				fmt.Printf(", %d skipped", skipped)
			}
			fmt.Println()

			if errored > 0 {
				return fmt.Errorf("%d plugin(s) failed to build", errored)
			}
			return nil
		},
	}
}
