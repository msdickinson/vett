package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"regexp"
	"strings"
	"syscall"
	"time"

	"github.com/spf13/cobra"
	"github.com/spf13/pflag"

	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/runner"
)

func runCmd() *cobra.Command {
	var (
		suiteFlag     string
		profileFlag   string
		instancesN    int
		instanceIDs   string
		filterRegex   string
		endpointFlag  string
		modelFlag     string
		apiKeyFlag    string
		outputDir     string
		traceFlag     bool
		sidecarPath   string
		concurrency   int
		remoteHost    string
		detachRemote  bool
		fetchOnDone   bool
	)
	cmd := &cobra.Command{
		Use:   "run",
		Short: "Run a benchmark suite against a profile",
		RunE: func(cmd *cobra.Command, args []string) error {
			if suiteFlag == "" || profileFlag == "" {
				return fmt.Errorf("--suite and --profile are required")
			}
			// --remote path: vett becomes a launcher. Sync workspace
			// over SSH, exec vett on the remote, stream or detach.
			if remoteHost != "" {
				return runOnRemote(remoteHost, detachRemote, fetchOnDone, rebuildRunArgs(cmd))
			}
			profile, profileSrc, profileBytes, err := profileSourceBytes(profileFlag)
			if err != nil {
				return err
			}
			suite, suiteSrc, suiteDir, suiteBytes, err := suiteSourceBytes(suiteFlag)
			if err != nil {
				return err
			}
			_ = profileSrc
			_ = suiteSrc
			endpoint := pickEnv(endpointFlag, "VETT_LLM_ENDPOINT")
			model := pickEnv(modelFlag, "VETT_LLM_MODEL")
			apiKey := pickEnv(apiKeyFlag, "VETT_LLM_API_KEY")
			if endpoint == "" {
				return fmt.Errorf("--endpoint or VETT_LLM_ENDPOINT is required")
			}
			if model == "" {
				return fmt.Errorf("--model or VETT_LLM_MODEL is required")
			}
			if sidecarPath == "" {
				resolved, err := resolveSidecarPath()
				if err != nil {
					return err
				}
				sidecarPath = resolved
			} else if _, err := os.Stat(sidecarPath); err != nil {
				return fmt.Errorf("sidecar binary not found at %s (from --sidecar)", sidecarPath)
			}

			if suiteDir == "" {
				// Embedded suite: it has no sibling fixtures file on disk.
				// Only jsonl loaders need a base dir; if this suite is a
				// jsonl suite, LoadInstances will return a clear error.
				suiteDir = "."
			}
			instances, err := runner.LoadInstances(suite, suiteDir)
			if err != nil {
				return err
			}
			instances = filterInstances(instances, instanceIDs, filterRegex, instancesN)
			if len(instances) == 0 {
				return fmt.Errorf("no instances matched filters")
			}

			if outputDir == "" {
				outputDir = filepath.Join("results", "run-"+time.Now().UTC().Format("20060102-150405"))
			}

			// Ctrl+C handling: first sigint cancels the root context
			// (loop exits gracefully); a second forces exit.
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			sigs := make(chan os.Signal, 1)
			signal.Notify(sigs, os.Interrupt, syscall.SIGTERM)
			go func() {
				<-sigs
				fmt.Fprintln(os.Stderr, "\nvett: received interrupt; shutting down (another Ctrl+C to force)")
				cancel()
				<-sigs
				os.Exit(130)
			}()

			bus := eventbus.NewBus()
			opts := runner.Options{
				Profile:      profile,
				Suite:        suite,
				Instances:    instances,
				LLMEndpoint:  endpoint,
				LLMModel:     model,
				LLMAPIKey:    apiKey,
				OutputDir:    outputDir,
				SidecarPath:  sidecarPath,
				Trace:        traceFlag,
				Concurrency:  concurrency,
				ProfileBytes: profileBytes,
				SuiteBytes:   suiteBytes,
			}
			summary, err := runner.Run(ctx, opts, bus)
			if err != nil {
				return err
			}
			fmt.Printf("vett run complete: %d instances, %d completed, %d errored, %.2fs elapsed\n",
				summary.InstanceCount, summary.Completed, summary.Errored, summary.DurationSec)
			fmt.Printf("results: %s\n", outputDir)
			return nil
		},
	}
	cmd.Flags().StringVar(&suiteFlag, "suite", "", "Suite name or path")
	cmd.Flags().StringVar(&profileFlag, "profile", "", "Profile name or path")
	cmd.Flags().IntVar(&instancesN, "instances", 0, "Max instances to run (0 = all)")
	cmd.Flags().StringVar(&instanceIDs, "instance-ids", "", "Comma-separated instance IDs")
	cmd.Flags().StringVar(&filterRegex, "filter", "", "Regex to match instance IDs")
	cmd.Flags().StringVar(&endpointFlag, "endpoint", "", "LLM endpoint URL (or VETT_LLM_ENDPOINT)")
	cmd.Flags().StringVar(&modelFlag, "model", "", "LLM model name (or VETT_LLM_MODEL)")
	cmd.Flags().StringVar(&apiKeyFlag, "api-key", "", "LLM API key (or VETT_LLM_API_KEY)")
	cmd.Flags().StringVar(&outputDir, "output", "", "Output directory (default: results/run-<timestamp>)")
	cmd.Flags().BoolVar(&traceFlag, "trace", false, "Write JSONL trace files")
	cmd.Flags().StringVar(&sidecarPath, "sidecar", "", "Path to the vett-sidecar-linux-amd64 binary (default: ./bin/vett-sidecar-linux-amd64)")
	cmd.Flags().IntVar(&concurrency, "concurrency", 1, "How many instances to run in parallel (default 1)")
	cmd.Flags().StringVar(&remoteHost, "remote", "", "Run on the given SSH host instead of locally (rsync workspace, exec remote vett)")
	cmd.Flags().BoolVar(&detachRemote, "detach", false, "With --remote: launch the remote run under nohup and return immediately")
	cmd.Flags().BoolVar(&fetchOnDone, "fetch-on-complete", false, "With --remote: scp results back to local ./results/ when the remote run finishes (best-effort)")
	return cmd
}

// rebuildRunArgs reconstructs the flag arguments that were passed to
// this `vett run` invocation, minus --remote / --detach / --fetch-on-
// complete (because those apply only to the local launcher, not the
// remote process). Used when runOnRemote needs to pass all non-remote
// flags through to the remote vett.
func rebuildRunArgs(cmd *cobra.Command) []string {
	var out []string
	cmd.Flags().Visit(func(f *pflag.Flag) {
		switch f.Name {
		case "remote", "detach", "fetch-on-complete":
			return
		}
		out = append(out, "--"+f.Name, f.Value.String())
	})
	return out
}

func filterInstances(all []runner.Instance, idsCSV, regexStr string, limit int) []runner.Instance {
	var out []runner.Instance
	if idsCSV != "" {
		want := map[string]bool{}
		for _, id := range strings.Split(idsCSV, ",") {
			want[strings.TrimSpace(id)] = true
		}
		for _, i := range all {
			if want[i.ID] {
				out = append(out, i)
			}
		}
	} else if regexStr != "" {
		re, err := regexp.Compile(regexStr)
		if err == nil {
			for _, i := range all {
				if re.MatchString(i.ID) {
					out = append(out, i)
				}
			}
		}
	} else {
		out = all
	}
	if limit > 0 && len(out) > limit {
		out = out[:limit]
	}
	return out
}

func pickEnv(flag, envKey string) string {
	if flag != "" {
		return flag
	}
	return os.Getenv(envKey)
}

