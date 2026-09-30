// Package runner drives a full `vett run` invocation: for each instance
// in the suite, boot a sandbox, run the agent loop, extract the patch,
// write the trace, and emit lifecycle events onto the bus.
//
// Phase 1 ships a single-concurrency runner with one suite loader
// (jsonl). Concurrency > 1, HuggingFace datasets, parallel scoring, etc.
// are Phase 3/4 work.
package runner

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/agent"
	"github.com/msdickinson/vett/sidecar/internal/config"
	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/llm"
	"github.com/msdickinson/vett/sidecar/internal/sandbox"
	"github.com/msdickinson/vett/sidecar/internal/trace"
	"github.com/msdickinson/vett/sidecar/internal/version"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

// Options configure a Run invocation.
type Options struct {
	Profile     *config.Profile
	Suite       *config.Suite
	Instances   []Instance
	LLMEndpoint string
	LLMModel    string
	LLMAPIKey   string
	OutputDir   string
	SidecarPath string
	Trace       bool
	// Concurrency is the number of instances to run in parallel.
	// 0 or 1 means sequential (the original Phase 1 behavior).
	// > 1 means a worker pool; each worker owns its own sandbox,
	// sidecar, agent loop, and trace writer. Instances are fed from
	// a channel, workers pull and run, and results stream back into
	// a shared accumulator via a mutex. GPU-2 can handle ~10 concurrent
	// LLM calls per docs; SWE-bench images are ~2.8 GB each so the
	// practical ceiling is Docker/disk bandwidth, not LLM.
	Concurrency int
	// ProfileBytes / SuiteBytes are the raw YAML (or equivalent) bytes
	// used for the sha256 fingerprints on run_start. Optional — empty
	// bytes produce empty sha. CLI supplies these when loading from
	// disk or embed.
	ProfileBytes []byte
	SuiteBytes   []byte
}

// Instance is one SWE-bench-like problem statement the runner executes.
type Instance struct {
	ID               string
	Repo             string
	BaseCommit       string
	ProblemStatement string
	// Raw holds the full decoded instance object so custom rendering
	// fields can be looked up by name.
	Raw map[string]any
}

// InstanceResult is what the runner records per instance.
type InstanceResult struct {
	InstanceID   string        `json:"instance_id"`
	Resolved     *bool         `json:"resolved,omitempty"`
	Patch        string        `json:"-"`
	PatchChars   int           `json:"patch_chars"`
	PatchSHA256  string        `json:"patch_sha256,omitempty"`
	PatchPath    string        `json:"patch_path,omitempty"`
	FilesTouched []string      `json:"files_touched"`
	Iterations   int           `json:"iterations"`
	InputTokens  int           `json:"input_tokens"`
	OutputTokens int           `json:"output_tokens"`
	Duration     time.Duration `json:"-"`
	DurationSec  float64       `json:"duration_seconds"`
	EndReason    string        `json:"end_reason"`
	Error        string        `json:"error,omitempty"`
}

// Summary is the top-level summary.json body written at run end.
//
// Per docs/export-and-storage.md §13 rule 7, the summary must include
// everything needed to derive swebench / csv / json / flat / markdown
// exports without reading the trace files. Every field listed in §2 or
// referenced by a future export format belongs here.
type Summary struct {
	RunID          string           `json:"run_id"`
	VettVersion    string           `json:"vett_version"`
	SidecarSHA256  string           `json:"sidecar_sha256,omitempty"`
	Suite          string           `json:"suite"`
	SuiteSHA256    string           `json:"suite_sha256,omitempty"`
	Profile        string           `json:"profile"`
	ProfileSHA256  string           `json:"profile_sha256,omitempty"`
	Model          string           `json:"model"`
	Endpoint       string           `json:"endpoint"`
	StartedAt      time.Time        `json:"started_at"`
	CompletedAt    time.Time        `json:"completed_at"`
	DurationSec    float64          `json:"duration_seconds"`
	InstanceCount  int              `json:"instance_count"`
	Completed      int              `json:"completed"`
	Errored        int              `json:"errored"`
	Resolved       int              `json:"resolved"`
	ResolutionRate float64          `json:"resolution_rate"`
	Instances      []InstanceResult `json:"instances"`
}

// Run executes the full run end-to-end. Returns the summary and any
// run-terminating error. Per-instance errors do not terminate the run —
// they show up in the summary with EndReason="error".
func Run(ctx context.Context, opts Options, bus *eventbus.Bus) (*Summary, error) {
	if opts.Profile == nil {
		return nil, fmt.Errorf("runner: Options.Profile is required")
	}
	if len(opts.Instances) == 0 {
		return nil, fmt.Errorf("runner: no instances to run")
	}
	if opts.OutputDir == "" {
		return nil, fmt.Errorf("runner: OutputDir is required")
	}
	if err := os.MkdirAll(filepath.Join(opts.OutputDir, "instances"), 0o755); err != nil {
		return nil, fmt.Errorf("runner: make output dirs: %w", err)
	}

	// Derive run_id from the --output path's basename when set, so
	// workdir_backing's {run_id} token resolves to the SAME directory
	// the --output flag named. Otherwise we'd end up with two parallel
	// dirs on SSD: one for the trace/summary files and a sibling for
	// the /testbed bind mount, because they used different run_ids.
	runID := filepath.Base(opts.OutputDir)
	if runID == "" || runID == "." || runID == "/" || runID == "\\" {
		runID = "run-" + time.Now().UTC().Format("20060102-150405")
	}
	profileSHA := sha256Hex(opts.ProfileBytes)
	suiteSHA := sha256Hex(opts.SuiteBytes)
	sidecarSHA := sha256FileOrEmpty(opts.SidecarPath)
	summary := &Summary{
		RunID:         runID,
		VettVersion:   version.Vett,
		SidecarSHA256: sidecarSHA,
		Suite:         suiteName(opts.Suite),
		SuiteSHA256:   suiteSHA,
		Profile:       opts.Profile.Name,
		ProfileSHA256: profileSHA,
		Model:         opts.LLMModel,
		Endpoint:      opts.LLMEndpoint,
		StartedAt:     time.Now().UTC(),
		InstanceCount: len(opts.Instances),
	}

	// Run-level trace writer. Filter so only run-level events land here
	// (run_start, run_end, instance_start, instance_end). Per
	// docs/trace-format.md §File layout.
	var runWriter *trace.Writer
	if opts.Trace {
		w, err := trace.NewFileWriter(
			filepath.Join(opts.OutputDir, "run.trace.jsonl"),
			bus,
			eventbus.RunLevelOnly(),
		)
		if err != nil {
			return nil, err
		}
		runWriter = w
	}

	bus.Publish(eventbus.Event{
		Type: eventbus.RunStart,
		Data: map[string]any{
			"run_id":         runID,
			"vett_version":   version.Vett,
			"sidecar_sha256": sidecarSHA,
			"suite":          summary.Suite,
			"suite_sha256":   suiteSHA,
			"profile":        opts.Profile.Name,
			"profile_sha256": profileSHA,
			"model":          opts.LLMModel,
			"endpoint":       opts.LLMEndpoint,
			"instance_count": len(opts.Instances),
			// extensions is reserved per docs/export-and-storage.md §13
			// rule 6 — empty in Phase 1, future producers (TicketForge,
			// etc.) populate it.
			"extensions": map[string]any{},
		},
	})

	runInstances(ctx, opts, bus, runID, summary)

	summary.CompletedAt = time.Now().UTC()
	summary.DurationSec = summary.CompletedAt.Sub(summary.StartedAt).Seconds()
	if summary.InstanceCount > 0 && summary.Resolved > 0 {
		summary.ResolutionRate = float64(summary.Resolved) / float64(summary.InstanceCount)
	}

	bus.Publish(eventbus.Event{
		Type: eventbus.RunEnd,
		Data: map[string]any{
			"run_id":              runID,
			"duration_seconds":    summary.DurationSec,
			"completed":           summary.Completed,
			"errored":             summary.Errored,
			"total_input_tokens":  totalInputTokens(summary),
			"total_output_tokens": totalOutputTokens(summary),
			"extensions":          map[string]any{},
		},
	})

	// Write summary.json.
	summaryPath := filepath.Join(opts.OutputDir, "summary.json")
	if b, err := json.MarshalIndent(summary, "", "  "); err == nil {
		_ = os.WriteFile(summaryPath, b, 0o644)
	}

	// Give the trace writer a moment to drain, then close the bus so
	// its consumer goroutine exits.
	bus.Close()
	if runWriter != nil {
		select {
		case <-runWriter.Done():
		case <-time.After(2 * time.Second):
		}
		_ = runWriter.Close()
	}
	return summary, nil
}

// runInstances dispatches all opts.Instances across a worker pool of
// size max(1, opts.Concurrency). Workers feed from a shared channel;
// results stream back into the shared summary under a mutex. Runs
// preserve input order in summary.Instances by using an index slice
// and sorting at the end.
//
// Single-concurrency behavior matches the previous sequential loop
// exactly. Concurrency > 1 uses goroutines; each instance gets its
// own sandbox / sidecar / agent loop (sandbox.Start is per-instance,
// so nothing is shared).
// indexedResult pairs an InstanceResult with its input-order index so
// results can be reordered after concurrent execution.
type indexedResult struct {
	idx int
	res *InstanceResult
}

// indexedResultForTest is the exported shape the test file uses (the
// real type is unexported but the test is in the same package, so it
// can reference indexedResult directly — this alias keeps the test
// file readable).
type indexedResultForTest = struct {
	idx int
	id  string
}

// sortByIdx sorts in place by idx. Used to preserve input ordering
// in summary.Instances after concurrent workers append in completion
// order. Test hook — real callers use sort.Slice directly.
func sortByIdx(s []indexedResultForTest) {
	sort.Slice(s, func(i, j int) bool { return s[i].idx < s[j].idx })
}

// effectiveWorkers clamps requested concurrency into a safe range.
// < 1 becomes 1 (sequential). More workers than instances clamps to
// instance count so no workers sit idle.
func effectiveWorkers(requested, instanceCount int) int {
	if requested < 1 {
		return 1
	}
	if requested > instanceCount {
		return instanceCount
	}
	return requested
}

func runInstances(ctx context.Context, opts Options, bus *eventbus.Bus, runID string, summary *Summary) {
	workers := effectiveWorkers(opts.Concurrency, len(opts.Instances))

	results := make([]indexedResult, 0, len(opts.Instances))
	var mu sync.Mutex

	type job struct {
		idx  int
		inst Instance
	}
	jobs := make(chan job, len(opts.Instances))
	for i, inst := range opts.Instances {
		jobs <- job{idx: i, inst: inst}
	}
	close(jobs)

	var wg sync.WaitGroup
	for w := 0; w < workers; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for j := range jobs {
				if ctx.Err() != nil {
					return
				}
				res := runOneInstance(ctx, opts, bus, j.inst, runID)
				mu.Lock()
				results = append(results, indexedResult{idx: j.idx, res: res})
				if res.Error == "" {
					summary.Completed++
				} else {
					summary.Errored++
				}
				if res.PatchChars > 0 {
					patchPath := filepath.Join(opts.OutputDir, "instances", j.inst.ID+".patch")
					_ = os.WriteFile(patchPath, []byte(res.Patch), 0o644)
				}
				mu.Unlock()
			}
		}()
	}
	wg.Wait()

	// Preserve input order in summary.Instances regardless of
	// completion order.
	sort.Slice(results, func(i, j int) bool { return results[i].idx < results[j].idx })
	for _, r := range results {
		summary.Instances = append(summary.Instances, *r.res)
	}
}

// runOneInstance drives a single instance end-to-end.
func runOneInstance(parentCtx context.Context, opts Options, bus *eventbus.Bus, inst Instance, runID string) *InstanceResult {
	start := time.Now()
	result := &InstanceResult{InstanceID: inst.ID}

	// Enforce the profile's per-instance wall-clock timeout. This is
	// the last-resort cap that bounds run wall time even if the LLM
	// hangs, the sidecar deadlocks, or the agent loops past its
	// max_iterations check somehow. Default profile value is 30 min
	// (profiles/openhands.yaml). A zero or missing value disables it.
	ctx := parentCtx
	if opts.Profile != nil && opts.Profile.TimeoutMinutes > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(parentCtx, time.Duration(opts.Profile.TimeoutMinutes)*time.Minute)
		defer cancel()
	}

	// Per-instance trace writer (if tracing enabled). Filters on
	// Data["instance_id"] == inst.ID — the agent loop tags every event
	// it publishes via Loop.tag, runner-side lifecycle events include
	// instance_id explicitly. run_start/run_end never carry an
	// instance_id so they're naturally excluded.
	var instWriter *trace.Writer
	if opts.Trace {
		w, err := trace.NewFileWriter(
			filepath.Join(opts.OutputDir, "instances", inst.ID+".trace.jsonl"),
			bus,
			eventbus.ForInstance(inst.ID),
		)
		if err == nil {
			instWriter = w
			defer func() {
				select {
				case <-instWriter.Done():
				case <-time.After(1 * time.Second):
				}
				_ = instWriter.Close()
			}()
		}
	}

	image := renderImageTemplate(opts.Suite, inst)
	bus.Publish(eventbus.Event{
		Type: eventbus.InstanceStart,
		Data: map[string]any{
			"run_id":       runID,
			"instance_id":  inst.ID,
			"docker_image": image,
			"extensions":   map[string]any{},
		},
	})

	sandboxOpts := sandbox.DockerOptions{
		Image:           image,
		RunAsRoot:       opts.Profile.Sandbox.RunAsRoot,
		HomeEnv:         opts.Profile.Sandbox.Home,
		SidecarHostPath: opts.SidecarPath,
		TmpfsMounts:     opts.Profile.Sandbox.TmpfsMounts,
	}

	// Configure the workdir backing (SSD bind mount) if the profile
	// asked for it. Creates the per-instance host directory, populates
	// DockerOptions.BindMounts + WorkdirExtractFrom so sandbox.Start
	// does the docker cp + bind mount dance. Leaves sandboxOpts alone
	// when Type is empty.
	if backing := opts.Profile.Sandbox.WorkdirBacking; backing.Type == "host_path" {
		containerPath := backing.ContainerPath
		if containerPath == "" {
			containerPath = opts.Profile.Sandbox.DefaultCwd
			if containerPath == "" {
				containerPath = "/testbed"
			}
		}
		hostPath := renderWorkdirBacking(backing.HostBase, runID, inst.ID)
		hostPath, err := expandHomeTilde(hostPath)
		if err != nil {
			result.Error = fmt.Sprintf("workdir_backing: %v", err)
			result.EndReason = "error"
			result.DurationSec = time.Since(start).Seconds()
			bus.Publish(eventbus.Event{Type: eventbus.InstanceEnd, Data: instanceEndData(result, image, runID)})
			return result
		}
		if err := os.MkdirAll(hostPath, 0o755); err != nil {
			result.Error = fmt.Sprintf("workdir_backing mkdir %s: %v", hostPath, err)
			result.EndReason = "error"
			result.DurationSec = time.Since(start).Seconds()
			bus.Publish(eventbus.Event{Type: eventbus.InstanceEnd, Data: instanceEndData(result, image, runID)})
			return result
		}
		sandboxOpts.BindMounts = append(sandboxOpts.BindMounts, sandbox.BindMount{
			Host:      hostPath,
			Container: containerPath,
		})
		sandboxOpts.WorkdirExtractFrom = containerPath
	}
	sb, err := sandbox.Start(ctx, sandboxOpts)
	if err != nil {
		result.Error = fmt.Sprintf("sandbox start: %v", err)
		result.EndReason = "error"
		result.Duration = time.Since(start)
		result.DurationSec = result.Duration.Seconds()
		bus.Publish(eventbus.Event{Type: eventbus.ErrorEvent, Data: map[string]any{"where": "sandbox_start", "message": err.Error()}})
		bus.Publish(eventbus.Event{Type: eventbus.InstanceEnd, Data: instanceEndData(result, image, runID)})
		return result
	}
	defer sb.Close(context.Background())

	// Create the default session.
	cwd := opts.Profile.Sandbox.DefaultCwd
	if cwd == "" {
		cwd = "/testbed"
	}
	if err := sb.Client.SessionCreate(ctx, "agent", cwd, nil); err != nil {
		result.Error = fmt.Sprintf("session_create: %v", err)
		result.EndReason = "error"
		result.Duration = time.Since(start)
		result.DurationSec = result.Duration.Seconds()
		bus.Publish(eventbus.Event{Type: eventbus.InstanceEnd, Data: instanceEndData(result, image, runID)})
		return result
	}

	// Init execs: conda PATH + git safe.directory. Per the spec §10.
	initCommands := []string{
		`if [ -d /opt/miniconda3/envs/testbed/bin ]; then export PATH=/opt/miniconda3/envs/testbed/bin:$PATH; export CONDA_PREFIX=/opt/miniconda3/envs/testbed; export CONDA_DEFAULT_ENV=testbed; fi`,
		`git config --global --add safe.directory '*' 2>/dev/null || true`,
	}
	for _, cmd := range initCommands {
		_, _ = sb.Client.BashExec(ctx, "agent", cmd, 10*time.Second, nil)
	}

	// Build the profile's tool map.
	toolMap := map[string]*tool.Tool{}
	for _, ref := range opts.Profile.Tools {
		t := tool.Lookup(ref.Key)
		if t == nil {
			result.Error = fmt.Sprintf("tool %q not registered", ref.Key)
			result.EndReason = "error"
			result.Duration = time.Since(start)
			result.DurationSec = result.Duration.Seconds()
			bus.Publish(eventbus.Event{Type: eventbus.InstanceEnd, Data: instanceEndData(result, image, runID)})
			return result
		}
		toolMap[ref.Name] = t
	}

	// Build middleware chain.
	mws := make([]mw.Middleware, 0, len(opts.Profile.Middleware))
	for _, ref := range opts.Profile.Middleware {
		factory := mw.Lookup(ref.Name)
		if factory == nil {
			result.Error = fmt.Sprintf("middleware %q not registered", ref.Name)
			result.EndReason = "error"
			result.Duration = time.Since(start)
			result.DurationSec = result.Duration.Seconds()
			bus.Publish(eventbus.Event{Type: eventbus.InstanceEnd, Data: instanceEndData(result, image, runID)})
			return result
		}
		mws = append(mws, factory(ref.Config))
	}

	// Build LLM client.
	client := llm.NewClient(opts.LLMEndpoint, opts.LLMAPIKey)

	// RecoverSession closure: called by the agent loop when a bash
	// timeout kills the session. Destroys the dead handle, creates a
	// fresh one at the profile's default cwd, and replays the init
	// execs so conda + git config are back in place. Cwd resets to
	// default (not last-known) — Phase 1 limitation noted in
	// docs/implementation-notes.md.
	recover := func(rctx context.Context) error {
		_ = sb.Client.SessionDestroy(rctx, "agent")
		if err := sb.Client.SessionCreate(rctx, "agent", cwd, nil); err != nil {
			return err
		}
		for _, cmd := range initCommands {
			_, _ = sb.Client.BashExec(rctx, "agent", cmd, 10*time.Second, nil)
		}
		return nil
	}

	// Build agent loop.
	loop := &agent.Loop{
		Client:         client,
		Model:          opts.LLMModel,
		Sandbox:        sb.Client,
		SessionID:      "agent",
		InstanceID:     inst.ID,
		Tools:          toolMap,
		WireTools:      llm.OpenHandsToolSchemas(),
		Middlewares:    mws,
		Temperature:    opts.Profile.LLM.Temperature,
		TopP:           opts.Profile.LLM.TopP,
		MaxIterations:  opts.Profile.MaxIterations,
		Bus:            bus,
		RecoverSession: recover,
	}

	// Render the user message template.
	workingDir := opts.Suite.Rendering.WorkingDir
	if workingDir == "" {
		workingDir = cwd
	}
	userMsg := renderUserTemplate(opts.Profile.UserTemplate, map[string]string{
		"working_dir":       workingDir,
		"base_commit":       inst.BaseCommit,
		"problem_statement": inst.ProblemStatement,
	})

	loopResult, loopErr := loop.Run(ctx, opts.Profile.SystemPrompt, userMsg)
	if loopErr != nil {
		result.Error = loopErr.Error()
		if loopResult == nil {
			result.EndReason = "error"
			result.Duration = time.Since(start)
			result.DurationSec = result.Duration.Seconds()
			bus.Publish(eventbus.Event{Type: eventbus.InstanceEnd, Data: instanceEndData(result, image, runID)})
			return result
		}
	}
	result.Iterations = loopResult.Iterations
	result.InputTokens = loopResult.InputTokens
	result.OutputTokens = loopResult.OutputTokens
	result.EndReason = loopResult.StopReason
	if result.EndReason == "" {
		result.EndReason = "max_iterations"
	}

	// Patch extraction via raw BashExec (NOT the terminal tool, so no
	// envelope wrapping). Uses a FRESH sidecar session isolated from
	// the agent's session — if the agent's bash died mid-run (e.g. a
	// silent exit the recover logic didn't catch), the agent session
	// can't extract its own patch. A fresh session is guaranteed to
	// work because the container is still up and the bind mount is
	// still attached. Phase 2 finding from 7336 where the agent made
	// real edits but `git diff --cached` came back empty because the
	// session had silently died.
	const patchSession = "patch-extract"
	if err := sb.Client.SessionCreate(ctx, patchSession, workingDir, nil); err == nil {
		// Replay git safe.directory so the fresh session can read the
		// bind-mounted .git tree (often owned by a different uid than
		// the container's user).
		_, _ = sb.Client.BashExec(ctx, patchSession,
			"git config --global --add safe.directory '*' 2>/dev/null || true",
			10*time.Second, nil)
		defer func() { _ = sb.Client.SessionDestroy(context.Background(), patchSession) }()
	}
	patchCmd := fmt.Sprintf("cd %s && git add -A && git diff --cached", workingDir)
	patchRes, perr := sb.Client.BashExec(ctx, patchSession, patchCmd, 60*time.Second, nil)
	if perr != nil {
		// Fresh session unavailable for some reason — fall back to
		// the agent session. If that also fails, the patch is just
		// empty and we'll surface it in the trace.
		patchRes, perr = sb.Client.BashExec(ctx, "agent", patchCmd, 60*time.Second, nil)
	}
	if perr == nil && patchRes != nil {
		result.Patch = patchRes.Stdout
		result.PatchChars = len(patchRes.Stdout)
		result.PatchSHA256 = sha256Hex([]byte(patchRes.Stdout))
		result.FilesTouched = parseFilesTouched(patchRes.Stdout)
	}
	if result.FilesTouched == nil {
		result.FilesTouched = []string{}
	}
	if result.PatchChars > 0 {
		result.PatchPath = filepath.Join("instances", inst.ID+".patch")
	}

	bus.Publish(eventbus.Event{
		Type: eventbus.PatchGenerated,
		Data: map[string]any{
			"run_id":            runID,
			"instance_id":       inst.ID,
			"patch_chars":       result.PatchChars,
			"patch_sha256":      result.PatchSHA256,
			"files_touched":     result.FilesTouched,
			"extraction_method": "git_diff_cached",
		},
	})

	result.Duration = time.Since(start)
	result.DurationSec = result.Duration.Seconds()
	bus.Publish(eventbus.Event{
		Type: eventbus.InstanceEnd,
		Data: instanceEndData(result, image, runID),
	})
	return result
}

// ---------------- helpers ----------------

func suiteName(s *config.Suite) string {
	if s == nil {
		return ""
	}
	return s.Name
}

func renderImageTemplate(s *config.Suite, inst Instance) string {
	if s == nil || s.Sandbox.ImageTemplate == "" {
		return ""
	}
	out := s.Sandbox.ImageTemplate
	out = strings.ReplaceAll(out, "{instance}", inst.ID)
	out = strings.ReplaceAll(out, "{repo}", inst.Repo)
	return out
}

// renderUserTemplate is a trivial {field} substitution. Phase 1 ships
// this instead of a full template engine.
func renderUserTemplate(tmpl string, fields map[string]string) string {
	out := tmpl
	for k, v := range fields {
		out = strings.ReplaceAll(out, "{"+k+"}", v)
	}
	return out
}

func instanceEndData(r *InstanceResult, image, runID string) map[string]any {
	return map[string]any{
		"run_id":           runID,
		"instance_id":      r.InstanceID,
		"docker_image":     image,
		"duration_seconds": r.DurationSec,
		"iterations":       r.Iterations,
		"input_tokens":     r.InputTokens,
		"output_tokens":    r.OutputTokens,
		"patch_chars":      r.PatchChars,
		"patch_sha256":     r.PatchSHA256,
		"files_touched":    r.FilesTouched,
		"end_reason":       r.EndReason,
		"error":            r.Error,
		"extensions":       map[string]any{},
	}
}

// renderWorkdirBacking substitutes {run_id} and {instance_id} tokens
// in the profile's workdir_backing.host_base template.
func renderWorkdirBacking(template, runID, instanceID string) string {
	out := template
	out = strings.ReplaceAll(out, "{run_id}", runID)
	out = strings.ReplaceAll(out, "{instance_id}", instanceID)
	return out
}

// expandHomeTilde replaces a leading "~" or "~/" with the user's home
// directory. Used so profile YAMLs can reference ~/whatever without
// needing an absolute path. Matches loader.go's expandPath but lives
// here to avoid circular helper imports.
func expandHomeTilde(p string) (string, error) {
	if p == "" || !strings.HasPrefix(p, "~") {
		return p, nil
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return p, fmt.Errorf("cannot expand %q: %w", p, err)
	}
	if p == "~" {
		return home, nil
	}
	if strings.HasPrefix(p, "~/") {
		return filepath.Join(home, p[2:]), nil
	}
	return p, nil
}

// sha256Hex returns the lowercase hex digest of b. Empty input → "".
func sha256Hex(b []byte) string {
	if len(b) == 0 {
		return ""
	}
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

// sha256FileOrEmpty reads the file and returns its sha256 hex. Empty on
// any error — this is used for best-effort sidecar fingerprinting on
// run_start, not for anything correctness-critical.
func sha256FileOrEmpty(path string) string {
	if path == "" {
		return ""
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return sha256Hex(b)
}

// parseFilesTouched walks a unified diff and extracts the list of file
// paths modified. Handles "diff --git a/PATH b/PATH" headers which is
// what `git diff --cached` produces. Used for the summary.json
// files_touched field per docs/export-and-storage.md §13 rule 7.
//
// Filters out noise paths the agent didn't really edit: Python
// __pycache__ bytecode from pytest, .pytest_cache, .mypy_cache, .ruff
// cache, and similar artifact directories. Fixture/benchmark images
// often lack a .gitignore, so git add -A picks these up even though
// they're not meaningful edits. The underlying .patch file still
// contains them (so scorers get the full diff) — this list is the
// human-readable summary, not the patch itself.
func parseFilesTouched(diff string) []string {
	if diff == "" {
		return []string{}
	}
	seen := map[string]bool{}
	var out []string
	for _, line := range strings.Split(diff, "\n") {
		if !strings.HasPrefix(line, "diff --git ") {
			continue
		}
		parts := strings.Fields(line)
		if len(parts) < 4 {
			continue
		}
		bPath := strings.TrimPrefix(parts[3], "b/")
		if bPath == "" || seen[bPath] {
			continue
		}
		if isNoisePath(bPath) {
			continue
		}
		seen[bPath] = true
		out = append(out, bPath)
	}
	return out
}

// isNoisePath returns true for paths inside common build/cache/
// artifact directories that shouldn't appear in the human-readable
// files_touched list.
func isNoisePath(path string) bool {
	noiseDirs := []string{
		"__pycache__/",
		".pytest_cache/",
		".mypy_cache/",
		".ruff_cache/",
		".tox/",
		".coverage",
		"node_modules/",
	}
	for _, d := range noiseDirs {
		if strings.HasPrefix(path, d) || strings.Contains(path, "/"+d) {
			return true
		}
	}
	if strings.HasSuffix(path, ".pyc") || strings.HasSuffix(path, ".pyo") {
		return true
	}
	return false
}

func totalInputTokens(s *Summary) int {
	n := 0
	for _, i := range s.Instances {
		n += i.InputTokens
	}
	return n
}

func totalOutputTokens(s *Summary) int {
	n := 0
	for _, i := range s.Instances {
		n += i.OutputTokens
	}
	return n
}
