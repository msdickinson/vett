using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vett.Agent;
using Vett.Config;
using Microsoft.Extensions.AI;
using Vett.Llm;
using Vett.Plugin;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Runner;

public sealed class Instance
{
    public string Id { get; set; } = "";
    public string Repo { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public string ProblemStatement { get; set; } = "";
    /// <summary>Optional. Loaded from manifest JSONL when present (field "language").</summary>
    public string? Language { get; set; }
    /// <summary>Optional. Loaded from manifest JSONL when present (field "diff").</summary>
    public string? Difficulty { get; set; }
    /// <summary>Optional. Loaded from manifest JSONL when present (field "type").</summary>
    public string? InstanceType { get; set; }
    /// <summary>Per-instance docker image (e.g. from SWE-Rebench manifest "image_name" field).
    /// When set, ResolveImage uses this directly instead of the suite's DockerImageTemplate.</summary>
    public string? Image { get; set; }
}

// Field names below use [JsonPropertyName] to lock the wire format to
// snake_case. The AI Timeline parser expects this shape (instance_id,
// total_iterations, etc.); historically we shipped PascalCase which
// the parser couldn't read. Don't rename the JSON keys lightly — every
// downstream tool reads them.
public sealed class InstanceResult
{
    [JsonPropertyName("instance_id")]      public string InstanceId { get; set; } = "";
    [JsonPropertyName("patch")]            public string Patch { get; set; } = "";
    [JsonPropertyName("total_iterations")] public int Iterations { get; set; }
    [JsonPropertyName("total_input_tokens")]  public int InputTokens { get; set; }
    [JsonPropertyName("total_output_tokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("duration_seconds")] public double DurationSec { get; set; }
    [JsonPropertyName("end_reason")]       public string EndReason { get; set; } = "";
    [JsonPropertyName("error")]            public string? Error { get; set; }
    [JsonPropertyName("started_at")]       public string? StartedAt { get; set; }
    [JsonPropertyName("completed_at")]     public string? CompletedAt { get; set; }

    // --- 5-bucket resolution fields ---
    /// <summary>True when the agent called the finish tool as its stop reason.</summary>
    [JsonPropertyName("finish_tool_called")] public bool FinishToolCalled { get; set; }
    /// <summary>Last assistant text turn (≤2000 chars). Used by ResolutionClassifier to detect ABSTAIN.</summary>
    [JsonPropertyName("last_assistant_message")] public string? LastAssistantMessage { get; set; }
    /// <summary>Null until GENESIS is wired; non-null means analyzer abstained before LLM ran → NOT_MODELABLE.</summary>
    [JsonPropertyName("genesis_abstain_reason")] public string? GenesisAbstainReason { get; set; }
    /// <summary>Language of the instance (e.g. "python", "csharp"). Loaded from instance manifest when present.</summary>
    [JsonPropertyName("language")] public string? Language { get; set; }
    /// <summary>Difficulty tier (e.g. "easy", "medium", "hard"). Loaded from manifest when present.</summary>
    [JsonPropertyName("difficulty")] public string? Difficulty { get; set; }
    /// <summary>Instance type (e.g. "bug", "feat"). Loaded from manifest when present.</summary>
    [JsonPropertyName("instance_type")] public string? InstanceType { get; set; }
    /// <summary>Set at grade time from SWE-bench evaluator output.</summary>
    [JsonPropertyName("tests_passed")] public bool? TestsPassed { get; set; }
    /// <summary>One of PASS / FALSE_CONFIDENCE / ABSTAIN / TIMEOUT / NOT_MODELABLE. Set by ResolutionClassifier.</summary>
    [JsonPropertyName("resolution")] public string? Resolution { get; set; }
}

public sealed class RunSummary
{
    [JsonPropertyName("run_id")]                public string RunId { get; set; } = "";
    [JsonPropertyName("suite_name")]            public string Suite { get; set; } = "";
    [JsonPropertyName("profile_name")]          public string Profile { get; set; } = "";
    /// <summary>Absolute path of the profile FILE the run loaded. profile_name
    /// here is the profile's own `name:` field, which is even weaker than the
    /// CLI name for identifying the ruler — three stores can supply a file
    /// under one name and they need not agree. Null on legacy summaries and
    /// on profiles built in code rather than loaded from disk.</summary>
    [JsonPropertyName("profile_path")]          public string? ProfilePath { get; set; }
    [JsonPropertyName("model")]                 public string Model { get; set; } = "";
    [JsonPropertyName("total")]                 public int InstanceCount { get; set; }
    [JsonPropertyName("completed")]             public int Completed { get; set; }
    [JsonPropertyName("errored")]               public int Errored { get; set; }
    [JsonPropertyName("total_duration_seconds")] public double DurationSec { get; set; }
    [JsonPropertyName("total_input_tokens")]   public int TotalInputTokens { get; set; }
    [JsonPropertyName("total_output_tokens")]  public int TotalOutputTokens { get; set; }
    [JsonPropertyName("started_at")]           public string? StartedAt { get; set; }
    [JsonPropertyName("completed_at")]         public string? CompletedAt { get; set; }
    [JsonPropertyName("instances")]            public List<InstanceResult> Instances { get; set; } = [];
}

public static class BenchmarkRunner
{
    public static Task<RunSummary> RunAsync(
        Profile profile, Suite suite, List<Instance> instances,
        IChatClient client, string model, string sidecarPath,
        Func<string, string, CancellationToken, Task<(RpcClient Rpc, IDisposable Life, string Session)>> sandboxFactory,
        string outputDir, int concurrency, Action<Event>? onEvent, CancellationToken ct)
        => RunAsync(profile, suite, instances, new List<IChatClient> { client }, model, sidecarPath,
            sandboxFactory, outputDir, concurrency, onEvent, ct);

    /// <summary>
    /// Multi-endpoint variant. Each instance is pinned to clients[i % clients.Count]
    /// at scheduling time so its full conversation (prefix cache) lives on a single
    /// vLLM. Per-request round-robin would force a cold re-prefill on every turn —
    /// catastrophic at long context.
    /// </summary>
    public static async Task<RunSummary> RunAsync(
        Profile profile, Suite suite, List<Instance> instances,
        IList<IChatClient> clients, string model, string sidecarPath,
        Func<string, string, CancellationToken, Task<(RpcClient Rpc, IDisposable Life, string Session)>> sandboxFactory,
        string outputDir, int concurrency, Action<Event>? onEvent, CancellationToken ct)
    {
        if (clients.Count == 0)
            throw new ArgumentException("At least one IChatClient is required", nameof(clients));
        Directory.CreateDirectory(Path.Combine(outputDir, "instances"));
        var runId = Path.GetFileName(outputDir);
        var summary = new RunSummary {
            RunId = runId, Suite = suite.Name, Profile = profile.Name, Model = model,
            ProfilePath = profile.SourcePath,
            InstanceCount = instances.Count,
            StartedAt = DateTime.UtcNow.ToString("O"),
        };
        var sem = new SemaphoreSlim(Math.Max(1, concurrency));
        var resultLock = new object();
        var start = DateTime.UtcNow;

        var workDir = Directory.GetCurrentDirectory();
        Hooks.Run(Hooks.Load(workDir, "pre-process/suite"));

        // Load workspace plugins once for the whole suite. Without this,
        // YAML/plugin tools and middlewares listed in profile.* would be
        // silently dropped by the per-instance filter in RunInstanceAsync.
        using var pluginPool = new PluginPool(workDir);
        await pluginPool.WarmUpAsync(ct);
        var pluginTools = new Dictionary<string, ToolFn>();
        var pluginSchemas = new Dictionary<string, JsonElement>();
        foreach (var pname in pluginPool.ToolNames())
        {
            var pfn = pluginPool.GetTool(pname);
            if (pfn is null) continue;
            pluginTools[pname] = pfn;
            var schema = pluginPool.GenerateSchema(pname);
            if (schema.ValueKind != JsonValueKind.Undefined) pluginSchemas[pname] = schema;
        }
        var pluginMiddlewares = pluginPool.AllMiddlewares();

        onEvent?.Invoke(new Event("run_start", new() { ["run_id"] = runId, ["instance_count"] = instances.Count }));

        var tasks = instances.Select((inst, i) => Task.Run(async () =>
        {
            await sem.WaitAsync(ct);
            try
            {
                // Pin instance to one client by index — keeps the full conversation
                // (and its KV/prefix cache) on a single vLLM endpoint.
                var instClient = clients[i % clients.Count];
                var r = await RunInstanceAsync(profile, suite, inst, instClient, model, sidecarPath, sandboxFactory, pluginTools, pluginSchemas, pluginMiddlewares, onEvent, ct);
                lock (resultLock) { summary.Instances.Add(r); if (r.Error is null) summary.Completed++; else summary.Errored++; }
            }
            finally { sem.Release(); }
        }, ct)).ToArray();

        await Task.WhenAll(tasks);
        summary.DurationSec = (DateTime.UtcNow - start).TotalSeconds;
        summary.CompletedAt = DateTime.UtcNow.ToString("O");
        // Roll up per-instance token totals so consumers don't have to walk
        // the instances array. (AI Timeline's vett.ts parser reads these.)
        foreach (var r in summary.Instances)
        {
            summary.TotalInputTokens += r.InputTokens;
            summary.TotalOutputTokens += r.OutputTokens;
        }

        // Write summary.
        File.WriteAllText(Path.Combine(outputDir, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        // SWE-bench zip.
        using (var zip = ZipFile.Open(Path.Combine(outputDir, "swebench.zip"), ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("predictions.jsonl");
            using var w = new StreamWriter(entry.Open());
            foreach (var r in summary.Instances)
                w.WriteLine(JsonSerializer.Serialize(new { instance_id = r.InstanceId, model_patch = r.Patch, model_name_or_path = model }));
        }

        Hooks.Run(Hooks.Load(workDir, "post-process/suite"));

        onEvent?.Invoke(new Event("run_end", new() { ["completed"] = summary.Completed, ["errored"] = summary.Errored }));
        return summary;
    }

    private static async Task<InstanceResult> RunInstanceAsync(
        Profile profile, Suite suite, Instance inst, IChatClient client, string model, string sidecarPath,
        Func<string, string, CancellationToken, Task<(RpcClient, IDisposable, string)>> sandboxFactory,
        Dictionary<string, ToolFn> pluginTools, Dictionary<string, JsonElement> pluginSchemas,
        Dictionary<string, MiddlewareFn> pluginMiddlewares,
        Action<Event>? onEvent, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var result = new InstanceResult {
            InstanceId = inst.Id,
            StartedAt = start.ToString("O"),
            Language = inst.Language,
            Difficulty = inst.Difficulty,
            InstanceType = inst.InstanceType,
        };
        var workDir = Directory.GetCurrentDirectory();
        Hooks.Run(Hooks.Load(workDir, "pre-process/instance"));

        onEvent?.Invoke(new Event("instance_start", new() { ["instance_id"] = inst.Id }));

        try
        {
            var cwd = ResolveInstanceWorkingDir(profile, suite, inst);
            var image = ResolveImage(profile, suite, inst);
            var (rpc, life, session) = await sandboxFactory(image, cwd, ct);
            using var _ = life;

            // Builtins + plugins. Plugin tools were previously dropped by the
            // profile.Tools filter because they weren't in Builtins.All().
            var tools = Builtins.All();
            foreach (var (k, v) in pluginTools) tools[k] = v;

            // JSON-escape cwd before substituting into the raw schema text so a
            // backslash or quote in the path can't corrupt the JSON. Trim the
            // surrounding quotes JsonSerializer.Serialize adds because the
            // placeholder already sits inside a JSON string.
            var cwdEscaped = JsonSerializer.Serialize(cwd).Trim('"');
            var schemas = profile.Tools.Select(name =>
            {
                if (pluginSchemas.TryGetValue(name, out var pluginSchema)) return pluginSchema;
                var p = Path.Combine(AppContext.BaseDirectory, "schemas", $"{name}.json");
                if (!File.Exists(p)) return default;
                // Substitute {working_dir} so file_editor matches OpenHands' dynamically
                // appended cwd hint. No-op for schemas that don't contain the placeholder.
                var raw = File.ReadAllText(p).Replace("{working_dir}", cwdEscaped);
                return JsonDocument.Parse(raw).RootElement.Clone();
            }).Where(e => e.ValueKind != JsonValueKind.Undefined).ToList();

            var filteredTools = profile.Tools
                .Where(tools.ContainsKey)
                .ToDictionary(k => k, k => tools[k]);

            var userMsg = profile.UserTemplate
                .Replace("{working_dir}", cwd)
                .Replace("{base_commit}", inst.BaseCommit)
                .Replace("{problem_statement}", inst.ProblemStatement);

            var llm = new LlmSettings(client, model, profile.Llm.Temperature ?? 1.0, profile.Llm.TopP, profile.Llm.MaxOutputTokens,
            profile.Llm.PresencePenalty, profile.Llm.FrequencyPenalty);

            // ⛔ 2026-08-26 — this call used to pass NEITHER `llm` NOR
            // `compaction`, and both omissions cost the same thing: compaction.
            //
            // Without `compaction`, the profile's `compaction:` block was
            // discarded and threshold_tokens silently reverted to the 30000
            // default — so a profile tuned to 48000 for a 65536-token window
            // compacted at the wrong point, on the one code path (`vett run`)
            // that executes an entire benchmark suite unattended.
            //
            // Without `llm`, `llm_summarizing_condenser` did not even register
            // and the name was silently skipped. ds-solo-flash and ds-solo-pro
            // name the condenser as their ONLY compaction strategy, so on this
            // path they ran with NO COMPACTION AT ALL and grew until the
            // provider rejected the request. The sibling Bench/Team/Harness.cs
            // already passed `compaction` here, which is what made the gap look
            // deliberate rather than like the oversight it was.
            //
            // Threading `llm` also arms agent_finished_critic, whose omission on
            // benchmark paths IS deliberate — an extra LLM call per instance
            // doesn't pay at scale and the grader is the authoritative judge. It
            // costs nothing today: no shipped profile lists that name, verified
            // across all three resolution stores. Should one start to, exclude
            // the critic by name here rather than by withholding `llm` and
            // taking compaction down with it.
            var caps = new AgentCapabilities(
                filteredTools, schemas,
                MiddlewareResolver.ResolveOrDefault(
                    profile.Middleware, pluginMiddlewares, llm, profile.Compaction,
                    onDiagnostic: msg => Console.Error.WriteLine($"[middleware:{inst.Id}] {msg}")));

            // Wrap onEvent so AgentLoop events (which don't know which instance
            // they belong to) get instance_id stamped on the way out. Critical
            // for concurrent runs (--concurrency > 1) — without this, every
            // instance's iteration_start / tool_call / llm_response events
            // arrive at the sink with no instance_id and are unattributable.
            Action<Event>? wrappedOnEvent = onEvent == null ? null : (Event e) =>
            {
                if (e.Data.ContainsKey("instance_id"))
                {
                    onEvent(e);
                }
                else
                {
                    var stamped = new Dictionary<string, object?>(e.Data) { ["instance_id"] = inst.Id };
                    onEvent(new Event(e.Type, stamped));
                }
            };
            var env = new AgentEnvironment(rpc, session, profile.MaxIterations, wrappedOnEvent);

            var loopResult = await AgentLoop.RunAsync(llm, caps, env, profile.SystemPrompt, userMsg, ct);
            result.Iterations = loopResult.Iterations;
            result.InputTokens = loopResult.InputTokens;
            result.OutputTokens = loopResult.OutputTokens;
            result.EndReason = loopResult.StopReason;
            result.FinishToolCalled = loopResult.StopReason == "finish_tool";

            // Capture the agent's FINAL WORD for ABSTAIN detection at grade
            // time. ⛔ NOT merely the last assistant TEXT: a model that refuses
            // inside the `finish` argument has none, and used to arrive at
            // ResolutionClassifier null — missing ABSTAIN and falling through to
            // FALSE_CONFIDENCE. See AgentResult.FinalTurnText for the
            // measurement and for why it must not look past the last turn.
            var finalWord = loopResult.FinalTurnText();
            if (!string.IsNullOrEmpty(finalWord))
                result.LastAssistantMessage = finalWord.Length > 2000 ? finalWord[..2000] : finalWord;

            try
            {
                // git diff HEAD (not bare git diff): a model that stages its edits
                // (git add) would otherwise produce an EMPTY patch -> false grade
                // failure. Same diff-basis law the relay gates use (#57 class).
                var patch = await rpc.BashExecAsync(session, $"cd {cwd} && git diff HEAD", 30, ct);
                result.Patch = patch.Stdout;
            }
            catch (Exception ex)
            {
                onEvent?.Invoke(new Event("patch_capture_failed", new() { ["instance_id"] = inst.Id, ["error"] = ex.Message }));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result.Error = ex.Message; result.EndReason = "error"; }

        result.DurationSec = (DateTime.UtcNow - start).TotalSeconds;
        result.CompletedAt = DateTime.UtcNow.ToString("O");

        // Post-process hooks run even on error so cleanup hooks (kill orphan
        // containers, archive workspace, etc.) still execute.
        try { Hooks.Run(Hooks.Load(workDir, "post-process/instance")); }
        catch (Exception ex) { onEvent?.Invoke(new Event("post_hook_failed", new() { ["instance_id"] = inst.Id, ["error"] = ex.Message })); }

        onEvent?.Invoke(new Event("instance_end", new() { ["instance_id"] = inst.Id, ["end_reason"] = result.EndReason }));
        return result;
    }

    /// <summary>
    /// Resolve the working directory for an instance.
    /// Precedence: suite.rendering.working_dir → profile.sandbox.default_cwd → "/testbed".
    /// Suite wins because it describes the dataset's expected layout.
    /// </summary>
    public static string ResolveWorkingDir(Profile profile, Suite suite)
    {
        if (!string.IsNullOrEmpty(suite.Rendering.WorkingDir))
            return suite.Rendering.WorkingDir;
        if (!string.IsNullOrEmpty(profile.Sandbox.DefaultCwd))
            return profile.Sandbox.DefaultCwd;
        return "/testbed";
    }

    /// <summary>
    /// Resolve the working directory for a specific instance, expanding per-instance
    /// placeholders in the configured working_dir:
    ///   {repo}      → full repo (e.g. "adamchainz/flake8-comprehensions")
    ///   {repo_name} → repo basename (e.g. "flake8-comprehensions")
    /// SWE-Rebench v2 images clone the repo at /&lt;repo-basename&gt; (the image's own
    /// WorkingDir), not /testbed — so those bench-specs set working_dir: /{repo_name}.
    /// When no placeholder is present this is identical to <see cref="ResolveWorkingDir"/>.
    /// </summary>
    public static string ResolveInstanceWorkingDir(Profile profile, Suite suite, Instance inst)
    {
        var cwd = ResolveWorkingDir(profile, suite);
        if (cwd.Contains("{repo", StringComparison.Ordinal) && !string.IsNullOrEmpty(inst.Repo))
        {
            var repoName = inst.Repo.Contains('/') ? inst.Repo[(inst.Repo.LastIndexOf('/') + 1)..] : inst.Repo;
            cwd = cwd.Replace("{repo_name}", repoName).Replace("{repo}", inst.Repo);
        }
        return cwd;
    }

    /// <summary>
    /// Resolve the docker image for an instance.
    /// Local profiles return "". Otherwise: suite.rendering.docker_image_template (with
    /// {repo} and {instance} substitution) → suite.sandbox.docker_image → empty (caller errors).
    /// Repo slashes become underscores so the result is a valid docker tag.
    /// </summary>
    public static string ResolveImage(Profile profile, Suite suite, Instance inst)
    {
        if (profile.Sandbox.Type == "local")
            return "";

        // Per-instance image wins (e.g. SWE-Rebench manifest's "image_name").
        if (!string.IsNullOrEmpty(inst.Image))
            return inst.Image;

        if (!string.IsNullOrEmpty(suite.Rendering.DockerImageTemplate))
        {
            var safeRepo = inst.Repo.Replace('/', '_');
            return suite.Rendering.DockerImageTemplate
                .Replace("{repo}", safeRepo)
                .Replace("{instance}", inst.Id);
        }

        return suite.Sandbox.DockerImage;
    }

    public static List<Instance> LoadJsonl(string path)
    {
        return File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(line =>
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!;
            return new Instance
            {
                Id = Str(raw, "instance_id", Str(raw, "id", "")),
                Repo = Str(raw, "repo", ""),
                BaseCommit = Str(raw, "base_commit", ""),
                ProblemStatement = Str(raw, "problem_statement", ""),
                Language   = NullIfEmpty(Str(raw, "language", "")),
                Difficulty = NullIfEmpty(Str(raw, "diff", Str(raw, "difficulty", ""))),
                InstanceType = NullIfEmpty(Str(raw, "type", "")),
                Image      = NullIfEmpty(Str(raw, "image_name", Str(raw, "image", ""))),
            };
        }).ToList();

        static string Str(Dictionary<string, JsonElement> d, string k, string def)
            => d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;
        static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
