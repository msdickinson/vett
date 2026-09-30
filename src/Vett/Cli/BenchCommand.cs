using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Vett.Config;
using Vett.Live;
using Vett.Llm;
using Vett.Runner;
using Vett.Sandbox;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Cli;

/// <summary>
/// `vett bench` — benchmark utilities.
///   vett bench run --profile swe-rebench-easy-py --limit 10
///   vett bench grade --run &lt;dir&gt; [--eval-jsonl &lt;path&gt;]
/// </summary>
public static class BenchCommand
{
    private static readonly IDeserializer YamlD = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static Command Create(ILogger logger)
    {
        var cmd = new Command("bench", "Benchmark run utilities");
        cmd.Add(CreateRun(logger));
        cmd.Add(CreateEval(logger));
        cmd.Add(CreateGrade(logger));
        cmd.Add(CreateRegress(logger));
        return cmd;
    }

    /// <summary>
    /// `vett bench run` — run a filtered subset of SWE-Rebench instances using a
    /// bench-spec profile (manifest path + language/difficulty/type filter + agent profile).
    ///
    /// Example:
    ///   vett bench run --profile swe-rebench-easy-py --limit 10 \
    ///       --endpoint http://gpu-1:8000/v1 --model deepseek-v4-flash --output results/my-run
    ///
    /// The bench-spec YAML (resolved from bench-profiles/ dirs) declares the manifest
    /// path, filter, and which agent profile to use. Override LLM settings with
    /// --endpoint / --model / --api-key, same as `vett run`.
    /// </summary>
    private static Command CreateRun(ILogger logger)
    {
        var runCmd = new Command("run", "Run a filtered subset of benchmark instances from a bench-spec profile");

        var profileOpt = new Option<string>("--profile") {
            Required = true,
            Description = "Bench-spec profile name (e.g. swe-rebench-easy-py) or path to YAML.",
        };
        var limitOpt = new Option<int>("--limit") {
            Description = "Cap the number of instances to run. 0 = no cap (run all matching).",
            DefaultValueFactory = _ => 0,
        };
        var endpointOpt = new Option<string[]>("--endpoint") {
            AllowMultipleArgumentsPerToken = true,
            Description = "LLM endpoint(s). Multiple = round-robin per instance.",
        };
        var modelOpt = new Option<string>("--model") { Description = "LLM model name." };
        var apiKeyOpt = new Option<string>("--api-key") { Description = "LLM API key." };
        var concurrencyOpt = new Option<int>("--concurrency") {
            DefaultValueFactory = _ => 1,
            Description = "Number of instances to run in parallel.",
        };
        var outputOpt = new Option<string?>("--output") {
            Description = "Output directory. Defaults to results/run-<timestamp>.",
        };
        var filterLangOpt = new Option<string?>("--language") {
            Description = "Override bench-spec filter: language (e.g. python, csharp).",
        };
        var filterDiffOpt = new Option<string?>("--difficulty") {
            Description = "Override bench-spec filter: difficulty (e.g. easy, medium, hard).",
        };
        var filterTypeOpt = new Option<string?>("--type") {
            Description = "Override bench-spec filter: type (e.g. bug, feat).",
        };
        var trajectoryDirOpt = new Option<string?>("--trajectory-dir") {
            Description = "Write per-event JSONL to this directory.",
        };
        var livePortOpt = new Option<int>("--live-port") {
            DefaultValueFactory = _ => 0,
            Description = "SSE live event port (0 = disabled).",
        };
        var dryRunOpt = new Option<bool>("--dry-run") {
            Description = "Print the filtered instance list and exit — no LLM calls, no Docker. Useful for verifying filter + manifest before committing to a full run.",
            DefaultValueFactory = _ => false,
        };
        var enableThinkingOpt = new Option<bool?>("--enable-thinking") {
            Description = "Override the agent profile's enable_thinking (injects chat_template_kwargs.enable_thinking on every /chat/completions call). " +
                          "true = force reasoning on, false = force off, unset = use the profile default. " +
                          "On the DSpark DeepSeek-V4 build the reasoning is emitted INLINE in content (reasoning_content stays empty) and costs ~2.5x tokens.",
        };

        runCmd.Add(profileOpt);
        runCmd.Add(limitOpt);
        runCmd.Add(endpointOpt);
        runCmd.Add(modelOpt);
        runCmd.Add(apiKeyOpt);
        runCmd.Add(concurrencyOpt);
        runCmd.Add(outputOpt);
        runCmd.Add(filterLangOpt);
        runCmd.Add(filterDiffOpt);
        runCmd.Add(filterTypeOpt);
        runCmd.Add(trajectoryDirOpt);
        runCmd.Add(livePortOpt);
        runCmd.Add(dryRunOpt);
        runCmd.Add(enableThinkingOpt);

        runCmd.SetAction(async (pr, ct) =>
        {
            // --- 1. Resolve bench-spec ---
            var profileName = pr.GetValue(profileOpt)!;
            var specPath = Yaml.Resolve(profileName, "bench-profiles", p => p);
            if (specPath is null)
            {
                logger.LogError("Bench-spec '{Name}' not found. Searched: {Dirs}",
                    profileName,
                    string.Join(", ", Yaml.ResolveSearchDirs("bench-profiles")));
                return 1;
            }
            var spec = YamlD.Deserialize<BenchSpec>(File.ReadAllText(specPath));

            // --- 2. Resolve agent profile ---
            var agentProfileName = spec.AgentProfile;
            if (string.IsNullOrEmpty(agentProfileName))
            {
                logger.LogError("bench-spec '{Name}' has no agent_profile set.", spec.Name);
                return 1;
            }
            var agentProfile = Yaml.Resolve(agentProfileName, "profiles", Yaml.LoadProfile);
            if (agentProfile is null)
            {
                logger.LogError("Agent profile '{Name}' not found.", agentProfileName);
                return 1;
            }

            // `vett bench` routes through Runner.cs, which is solo-only — a
            // team block in the agent profile is parsed and then dropped. This
            // matters more here than in `run`: bench WRITES A RESULT ROW, and
            // a solo run filed under a team profile's name is a measurement
            // that reads as something it is not.
            if (Helpers.TeamBlockIgnoredWarning(agentProfile, "bench") is { } teamWarning)
                logger.LogWarning("{Message}", teamWarning);

            // CLI --enable-thinking overrides the profile's enable_thinking for this run.
            var enableThinking = pr.GetValue(enableThinkingOpt);
            if (enableThinking.HasValue)
            {
                agentProfile.Llm.EnableThinking = enableThinking.Value;
                Console.WriteLine($"enable_thinking overridden to {enableThinking.Value} (chat_template_kwargs.enable_thinking on every call).");
            }

            // --- 3. Resolve LLM settings (CLI > env > profile) ---
            var endpoints = pr.GetValue(endpointOpt) ?? [];
            if (endpoints.Length == 0)
            {
                var envEp = Environment.GetEnvironmentVariable("VETT_LLM_ENDPOINT") ?? "";
                if (!string.IsNullOrEmpty(envEp))
                    endpoints = envEp.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            // ⛔ THE PROFILE RUNG WAS MISSING FOR ENDPOINT ONLY. model (below)
            // and apiKey both fell back to the profile, the section header says
            // "CLI > env > profile", and the error message says "or set them in
            // the agent profile" — but endpoint stopped at env, so a profile
            // that declares llm.endpoint was still refused. The fallback that
            // looked like it covered this (`clients.Count == 0` →
            // agentProfile.Llm.Endpoint, further down) was UNREACHABLE: clients
            // is built from endpoints, so it is empty exactly when the guard
            // below has already returned. Three correct-looking signals and no
            // implementation.
            if (endpoints.Length == 0
                && !ChatClientFactory.BindsThroughCapacity(agentProfile.Llm)
                && !string.IsNullOrEmpty(agentProfile.Llm.Endpoint))
                endpoints = [agentProfile.Llm.Endpoint];
            // Rung below the profile: the provider's own base URL (see
            // ChatClientFactory.DefaultEndpointFor).
            if (endpoints.Length == 0
                && ChatClientFactory.DefaultEndpointFor(agentProfile.Llm.Provider) is { } providerDefault)
                endpoints = [providerDefault];
            var model = Helpers.Env(pr.GetValue(modelOpt), "VETT_LLM_MODEL");
            model = ChatClientFactory.FoldProfileValue(agentProfile.Llm, model, agentProfile.Llm.Model);
            var apiKey = Helpers.Env(pr.GetValue(apiKeyOpt), "VETT_LLM_API_KEY");
            if (string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(agentProfile.Llm.ApiKeyEnv))
                apiKey = Environment.GetEnvironmentVariable(agentProfile.Llm.ApiKeyEnv) ?? "";

            // A capability profile names no endpoint of its own, so the list
            // is legitimately empty here. Seed it with ONE empty slot: an
            // empty override reads as "no override" in the factory, which is
            // what lets the capacity resolver choose. Without this the guard
            // below is satisfied but `endpoints.Select(...)` yields ZERO
            // clients -- a silently empty run rather than an error.
            if (endpoints.Length == 0 && ChatClientFactory.BindsThroughCapacity(agentProfile.Llm))
                endpoints = [""];

            var isDryRun = pr.GetValue(dryRunOpt);
            if (!isDryRun && !ChatClientFactory.BindsThroughCapacity(agentProfile.Llm)
                && (endpoints.Length == 0 || string.IsNullOrEmpty(model)))
            {
                logger.LogError("LLM endpoint and model are required. Pass --endpoint + --model, or set them in the agent profile. (For just listing instances, add --dry-run.)");
                return 1;
            }

            // --- 4. Resolve manifest path ---
            var manifestPath = Helpers.ExpandHome(spec.Manifest);
            if (!File.Exists(manifestPath))
            {
                logger.LogError("Manifest not found: {Path}", manifestPath);
                return 1;
            }

            // --- 5. Load + filter instances ---
            var allInstances = BenchmarkRunner.LoadJsonl(manifestPath);

            // CLI flags override bench-spec filter
            var langFilter  = pr.GetValue(filterLangOpt) ?? spec.Filter.Language;
            var diffFilter  = pr.GetValue(filterDiffOpt) ?? spec.Filter.Difficulty;
            var typeFilter  = pr.GetValue(filterTypeOpt) ?? spec.Filter.Type;

            var filtered = allInstances.AsEnumerable();
            if (!string.IsNullOrEmpty(langFilter))
                filtered = filtered.Where(i => string.Equals(i.Language, langFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(diffFilter))
                filtered = filtered.Where(i => string.Equals(i.Difficulty, diffFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(typeFilter))
                filtered = filtered.Where(i => string.Equals(i.InstanceType, typeFilter, StringComparison.OrdinalIgnoreCase));

            var instances = filtered.ToList();
            var limit = pr.GetValue(limitOpt);
            if (limit > 0) instances = instances.Take(limit).ToList();

            if (instances.Count == 0)
            {
                logger.LogError("No instances matched the filter (language={L}, difficulty={D}, type={T}) in manifest: {M}",
                    langFilter ?? "*", diffFilter ?? "*", typeFilter ?? "*", manifestPath);
                return 1;
            }

            // --- Dry-run: print list and exit ---
            if (isDryRun)
            {
                Console.WriteLine($"[dry-run] {instances.Count} instances would run:");
                foreach (var inst in instances)
                    Console.WriteLine($"  {inst.Id}  lang={inst.Language ?? "?"}  diff={inst.Difficulty ?? "?"}  type={inst.InstanceType ?? "?"}  image={inst.Image ?? "(none)"}");
                return 0;
            }

            // --- 6. Build Suite for BenchmarkRunner (manifest instances carry their own image) ---
            var suite = new Suite
            {
                Name = spec.Name,
                Loader = new LoaderConfig { Path = manifestPath },
                Rendering = new RenderingConfig { WorkingDir = spec.WorkingDir },
            };

            // --- 7. Find sidecar ---
            var sidecar = Helpers.FindSidecarWithPaths(out var sidecarSearched);
            if (sidecar is null)
            {
                logger.LogError("Sidecar not found. Searched: {Paths}", string.Join(", ", sidecarSearched));
                return 1;
            }

            // --- 8. Setup output dir ---
            var outputDir = pr.GetValue(outputOpt)
                ?? Path.Combine("results", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{spec.Name}");

            // --- 9. Live streaming (same as vett run) ---
            var trajectoryDir = pr.GetValue(trajectoryDirOpt);
            var livePort = pr.GetValue(livePortOpt);
            if (string.IsNullOrEmpty(trajectoryDir) && livePort > 0)
                trajectoryDir = Path.Combine(outputDir, "trajectory");

            using var liveSink = (string.IsNullOrEmpty(trajectoryDir) && livePort <= 0)
                ? null : new LiveSink(trajectoryDir);
            using var liveServer = (livePort > 0 && liveSink is not null)
                ? new LiveServer(liveSink, livePort) : null;
            liveServer?.Start();

            // --- 10. Build clients + run ---
            // The old `clients.Count == 0` fallback that stood here was dead
            // code — see the endpoint-resolution note above. The profile rung is
            // applied where the other rungs are, so by this point `endpoints` is
            // non-empty or the guard already returned.
            var clients = endpoints.Select(ep => ChatClientFactory.Create(agentProfile.Llm, ep, model, apiKey)).ToList();
            // See ChatClientFactory.EffectiveModel and the note in RunCommand.
            if (clients.Count > 0) model = ChatClientFactory.EffectiveModel(clients[0], model);

            Console.WriteLine($"bench run: {spec.Name}  instances={instances.Count}  language={langFilter ?? "*"}  difficulty={diffFilter ?? "*"}  type={typeFilter ?? "*"}  concurrency={pr.GetValue(concurrencyOpt)}");
            if (clients.Count > 1)
                Console.WriteLine($"  endpoints ({clients.Count}): {string.Join(", ", endpoints)}");

            var summary = await BenchmarkRunner.RunAsync(
                agentProfile, suite, instances, clients, model, sidecar,
                async (image, cwd, innerCt) =>
                {
                    if (string.IsNullOrEmpty(image))
                    {
                        var sb = await LocalSandbox.StartAsync(sidecar, cwd, "agent", innerCt);
                        return (sb.Rpc, sb, sb.SessionId);
                    }
                    var docker = await DockerSandbox.StartAsync(image, sidecar, agentProfile.Sandbox.RunAsRoot, ct: innerCt);
                    await docker.Rpc.SessionCreateAsync("agent", cwd, innerCt);
                    return (docker.Rpc, docker, "agent");
                },
                outputDir,
                pr.GetValue(concurrencyOpt),
                e =>
                {
                    liveSink?.Emit(e);
                    if (e.Type == "instance_end")
                        Console.WriteLine($"  {e.Data.GetValueOrDefault("instance_id")} → {e.Data.GetValueOrDefault("end_reason")}");
                    else if (e.Type == "llm_error")
                        logger.LogError("LLM error: {Msg}", e.Data.GetValueOrDefault("message"));
                },
                ct);

            Console.WriteLine($"\nvett bench run: {summary.InstanceCount} instances, {summary.Completed} ok, {summary.Errored} errors, {summary.DurationSec:F1}s");
            Console.WriteLine($"  results: {outputDir}/summary.json");
            return summary.Errored == 0 ? 0 : 1;
        });

        return runCmd;
    }

    /// <summary>
    /// `vett bench eval` — RUN the tests. For each instance in a completed run's
    /// summary.json, start a container off the pre-pulled bench-daemon image, apply
    /// the model patch + the gold test_patch, run the instance's test command, and
    /// decide tests_passed from the runner's own per-test verdict (FAIL_TO_PASS all
    /// pass AND PASS_TO_PASS all pass). Emits an eval JSONL that `bench grade` reads.
    ///
    /// Honors DOCKER_HOST — point it at the bench daemon:
    ///   DOCKER_HOST=unix:///var/run/docker-bench.sock \
    ///     vett bench eval --run results/run-... --eval-spec ~/.cache/vett/datasets/swe-rebench-v2.eval.jsonl --prove-gold
    ///
    /// --prove-gold grades the gold patch too and marks an instance UNTRUSTED if gold
    /// does not resolve (the evaluator could not self-verify on that instance).
    /// </summary>
    private static Command CreateEval(ILogger logger)
    {
        var evalCmd = new Command("eval", "Run tests for a completed run and emit {instance_id, tests_passed} JSONL");

        var runOpt = new Option<string>("--run") {
            Required = true,
            Description = "Completed run directory (must contain summary.json with per-instance patches).",
        };
        var specOpt = new Option<string>("--eval-spec") {
            Required = true,
            Description = "Path to eval-spec JSONL (per instance: image, repo_dir, test_cmd, test_patch, gold_patch, fail_to_pass, pass_to_pass).",
        };
        var proveGoldOpt = new Option<bool>("--prove-gold") {
            DefaultValueFactory = _ => false,
            Description = "Also grade the gold patch; mark an instance UNTRUSTED if gold does not resolve (evaluator self-check).",
        };
        var concurrencyOpt = new Option<int>("--concurrency") {
            DefaultValueFactory = _ => 2,
            Description = "Instances to grade in parallel.",
        };
        var outputOpt = new Option<string?>("--output") {
            Description = "Eval JSONL output path. Defaults to <run>/eval.jsonl.",
        };

        evalCmd.Add(runOpt);
        evalCmd.Add(specOpt);
        evalCmd.Add(proveGoldOpt);
        evalCmd.Add(concurrencyOpt);
        evalCmd.Add(outputOpt);

        evalCmd.SetAction(async (pr, ct) =>
        {
            var runDir = pr.GetValue(runOpt)!;
            var summaryPath = Path.Combine(runDir, "summary.json");
            if (!File.Exists(summaryPath))
            {
                logger.LogError("summary.json not found at: {Path}", summaryPath);
                return 1;
            }
            var specPath = Helpers.ExpandHome(pr.GetValue(specOpt)!);
            if (!File.Exists(specPath))
            {
                logger.LogError("eval-spec not found: {Path}", specPath);
                return 1;
            }

            var summary = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(summaryPath))
                          ?? throw new InvalidDataException("summary.json deserialized to null");

            // Load eval-specs into a map (JSONL, one EvalSpec per line).
            var specs = new Dictionary<string, EvalSpec>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(specPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var s = JsonSerializer.Deserialize<EvalSpec>(line);
                if (s is not null && !string.IsNullOrEmpty(s.InstanceId)) specs[s.InstanceId] = s;
            }

            var proveGold = pr.GetValue(proveGoldOpt);
            var concurrency = Math.Max(1, pr.GetValue(concurrencyOpt));
            var outputPath = pr.GetValue(outputOpt) ?? Path.Combine(runDir, "eval.jsonl");

            Console.WriteLine($"bench eval: {summary.Instances.Count} instances  spec={Path.GetFileName(specPath)}  prove-gold={proveGold}  concurrency={concurrency}");

            var sem = new SemaphoreSlim(concurrency);
            var results = new List<(string Id, bool? TestsPassed, bool Trusted, EvalOutcome Model, EvalOutcome? Gold, string EndReason)>();
            var resultLock = new object();

            var tasks = summary.Instances.Select(async inst =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    if (!specs.TryGetValue(inst.InstanceId, out var spec))
                    {
                        lock (resultLock) results.Add((inst.InstanceId, null, false,
                            new EvalOutcome { InstanceId = inst.InstanceId, Error = "no_eval_spec" }, null, inst.EndReason));
                        return;
                    }

                    EvalOutcome? gold = null;
                    var trusted = true;
                    if (proveGold)
                    {
                        gold = await Evaluator.EvaluateAsync(spec, spec.GoldPatch, ct);
                        trusted = gold.Resolved; // gold must resolve for us to trust the model verdict
                    }

                    var model = await Evaluator.EvaluateAsync(spec, inst.Patch ?? "", ct);
                    bool? testsPassed = trusted ? model.Resolved : null;

                    lock (resultLock) results.Add((inst.InstanceId, testsPassed, trusted, model, gold, inst.EndReason));
                }
                finally { sem.Release(); }
            }).ToArray();

            await Task.WhenAll(tasks);

            // Emit eval JSONL. `gradable` distinguishes an honest model failure from an
            // instance the evaluator could not self-verify (gold didn't reproduce): the
            // latter is a coverage gap, not a model miss, so `bench grade` buckets it
            // NOT_MODELABLE instead of counting it against the model.
            using (var w = new StreamWriter(outputPath))
            {
                foreach (var r in results.OrderBy(r => r.Id, StringComparer.Ordinal))
                    w.WriteLine(JsonSerializer.Serialize(new {
                        instance_id = r.Id,
                        tests_passed = r.TestsPassed ?? false,
                        gradable = r.Trusted,
                    }));
            }

            // Human table.
            Console.WriteLine();
            Console.WriteLine($"{"instance",-42} {"verdict",-22} {"F2P",-8} {"P2P",-9} {"end_reason",-18} note");
            var resolved = 0;
            foreach (var r in results.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                string verdict = !r.Trusted ? "UNTRUSTED(gold-fail)"
                    : (r.TestsPassed == true ? "RESOLVED" : "unresolved");
                if (r.TestsPassed == true) resolved++;
                var f2p = $"{r.Model.F2pPass}/{r.Model.F2pTotal}";
                var p2p = $"{r.Model.P2pPass}/{r.Model.P2pTotal}";
                Console.WriteLine($"{r.Id,-42} {verdict,-22} {f2p,-8} {p2p,-9} {r.EndReason,-18} {r.Model.Error ?? ""}");
            }
            var untrusted = results.Count(r => !r.Trusted);
            var trustedTotal = results.Count - untrusted;
            Console.WriteLine();
            Console.WriteLine($"RESOLVED: {resolved}/{trustedTotal} trusted" +
                              (untrusted > 0 ? $"  ({untrusted} UNTRUSTED excluded — gold did not reproduce)" : ""));
            Console.WriteLine($"eval.jsonl -> {outputPath}");
            Console.WriteLine($"Next: vett bench grade --run {runDir} --eval-jsonl {outputPath}");
            return 0;
        });

        return evalCmd;
    }

    /// <summary>
    /// `vett bench grade` — read a completed run's summary.json, optionally join
    ///
    /// SWE-bench eval results (tests_passed per instance), classify each instance
    /// into the 5-bucket resolution schema, and emit a per-language human table.
    ///
    /// Eval JSONL format (one JSON object per line):
    ///   {"instance_id": "django__django-12345", "tests_passed": true}
    ///
    /// Example:
    ///   vett bench grade --run results/run-20260707-120000 --eval-jsonl eval.jsonl
    /// </summary>
    private static Command CreateGrade(ILogger logger)
    {
        var gradeCmd = new Command("grade", "Classify run instances into 5-bucket resolution schema and emit per-language table");

        var runOpt = new Option<string>("--run") {
            Required = true,
            Description = "Path to a completed run directory (must contain summary.json).",
        };
        var evalJsonlOpt = new Option<string?>("--eval-jsonl") {
            Description = "Path to SWE-bench evaluator JSONL output with {instance_id, tests_passed} per line. " +
                          "Instances absent from the file get tests_passed=null (classified as FALSE_CONFIDENCE unless " +
                          "they hit a TIMEOUT/ABSTAIN/NOT_MODELABLE bucket first).",
        };
        var outJsonOpt = new Option<string?>("--out-json") {
            Description = "Write the enriched summary.json (with resolution + tests_passed filled in) to this path.",
        };
        var metricsOpt = new Option<string?>("--metrics-json") {
            Description = "Append a run-level summary entry to this metrics.json file (creates if missing). " +
                          "Each entry records run_id, suite, profile, timestamps, and bucket counts for dashboard consumption.",
        };

        gradeCmd.Add(runOpt);
        gradeCmd.Add(evalJsonlOpt);
        gradeCmd.Add(outJsonOpt);
        gradeCmd.Add(metricsOpt);

        gradeCmd.SetAction(async (pr, _) =>
        {
            await Task.Yield(); // make async, no actual async work needed

            var runDir = pr.GetValue(runOpt)!;
            var summaryPath = Path.Combine(runDir, "summary.json");
            if (!File.Exists(summaryPath))
            {
                logger.LogError("summary.json not found at: {Path}", summaryPath);
                return 1;
            }

            RunSummary summary;
            try
            {
                summary = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(summaryPath))
                    ?? throw new InvalidDataException("summary.json deserialized to null");
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to parse summary.json: {Error}", ex.Message);
                return 1;
            }

            // Join eval results if provided.
            var evalPath = pr.GetValue(evalJsonlOpt);
            if (!string.IsNullOrEmpty(evalPath))
            {
                if (!File.Exists(evalPath))
                {
                    logger.LogError("--eval-jsonl file not found: {Path}", evalPath);
                    return 1;
                }
                var evalMap = LoadEvalJsonl(evalPath);
                foreach (var inst in summary.Instances)
                {
                    if (evalMap.TryGetValue(inst.InstanceId, out var e))
                    {
                        if (!e.Gradable)
                            // Evaluator could not self-verify (gold didn't reproduce) — a
                            // coverage gap, not a model miss. Route to NOT_MODELABLE via the
                            // abstain reason the classifier already keys on.
                            inst.GenesisAbstainReason = "eval: gold patch did not reproduce (instance not gradable)";
                        else
                            inst.TestsPassed = e.TestsPassed;
                    }
                }
            }

            // Classify all instances.
            ResolutionClassifier.ClassifyAll(summary.Instances);

            // Emit per-language table.
            EmitTable(summary.Instances);

            // Optionally write enriched summary.
            var outJson = pr.GetValue(outJsonOpt);
            if (!string.IsNullOrEmpty(outJson))
            {
                File.WriteAllText(outJson, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"\nEnriched summary written to: {outJson}");
            }

            // Optionally append to metrics.json for the autonomous dashboard.
            var metricsPath = pr.GetValue(metricsOpt);
            if (!string.IsNullOrEmpty(metricsPath))
                AppendMetrics(metricsPath, summary);

            return 0;
        });

        return gradeCmd;
    }

    /// <summary>tests_passed plus gradable (default true; false = evaluator could not self-verify).</summary>
    private readonly record struct EvalVerdict(bool TestsPassed, bool Gradable);

    private static Dictionary<string, EvalVerdict> LoadEvalJsonl(string path)
    {
        var map = new Dictionary<string, EvalVerdict>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!;
                if (!obj.TryGetValue("instance_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString()!;
                bool passed = obj.TryGetValue("tests_passed", out var tpEl) && tpEl.ValueKind == JsonValueKind.True;
                // Absent gradable = true (back-compat with plain SWE-bench eval files).
                bool gradable = !obj.TryGetValue("gradable", out var gEl) || gEl.ValueKind != JsonValueKind.False;
                map[id] = new EvalVerdict(passed, gradable);
            }
            catch { /* skip malformed lines */ }
        }
        return map;
    }

    private static void EmitTable(List<InstanceResult> instances)
    {
        // Group by language; null/empty → "unknown".
        var byLang = instances
            .GroupBy(r => string.IsNullOrEmpty(r.Language) ? "unknown" : r.Language,
                     StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine();
        const string header = "{0,-16}{1,10}{2,11}{3,16}{4,20}{5,24}{6,20}";
        Console.WriteLine(string.Format(header,
            "Language", "Attempted", "Completed",
            "Pass (Rate)", "FalseConf (Rate)", "Abstain/Timeout (Rate)", "NotModelable (Rate)"));
        Console.WriteLine(new string('-', 120));

        foreach (var grp in byLang)
        {
            var all = grp.ToList();
            int attempted    = all.Count;
            int nml          = all.Count(r => r.Resolution == ResolutionClassifier.NotModelable);
            int timeout      = all.Count(r => r.Resolution == ResolutionClassifier.Timeout);
            int abstain      = all.Count(r => r.Resolution == ResolutionClassifier.Abstain);
            int pass         = all.Count(r => r.Resolution == ResolutionClassifier.Pass);
            int falseConf    = all.Count(r => r.Resolution == ResolutionClassifier.FalseConfidence);
            int completed    = attempted - nml;
            int abstainOrTo  = abstain + timeout;

            string Pct(int n) => attempted == 0 ? "0%" : $"{100.0 * n / attempted:F1}%";

            Console.WriteLine(string.Format(header,
                grp.Key,
                attempted,
                completed,
                $"{pass} ({Pct(pass)})",
                $"{falseConf} ({Pct(falseConf)})",
                $"{abstainOrTo} ({Pct(abstainOrTo)})",
                $"{nml} ({Pct(nml)})"));
        }

        int totalAttempted = instances.Count;
        int totalNml       = instances.Count(r => r.Resolution == ResolutionClassifier.NotModelable);
        int totalTimeout   = instances.Count(r => r.Resolution == ResolutionClassifier.Timeout);
        int totalAbstain   = instances.Count(r => r.Resolution == ResolutionClassifier.Abstain);
        int totalPass      = instances.Count(r => r.Resolution == ResolutionClassifier.Pass);
        int totalFc        = instances.Count(r => r.Resolution == ResolutionClassifier.FalseConfidence);
        int totalCompleted = totalAttempted - totalNml;
        int totalAbstainTo = totalAbstain + totalTimeout;

        string TotalPct(int n) => totalAttempted == 0 ? "0%" : $"{100.0 * n / totalAttempted:F1}%";

        Console.WriteLine(new string('-', 120));
        Console.WriteLine(string.Format(header,
            "TOTAL",
            totalAttempted,
            totalCompleted,
            $"{totalPass} ({TotalPct(totalPass)})",
            $"{totalFc} ({TotalPct(totalFc)})",
            $"{totalAbstainTo} ({TotalPct(totalAbstainTo)})",
            $"{totalNml} ({TotalPct(totalNml)})"));
        Console.WriteLine();
    }

    /// <summary>
    /// `vett bench regress` — compare the latest metrics.json entry against the best historical
    /// baseline and report any regressions. Exits 1 (blocker) when thresholds are exceeded.
    ///
    /// Thresholds (rates are relative to `attempted`):
    ///   Δpass  &lt; -2pp  → PASS regression
    ///   ΔFC    &gt; +2pp  → FALSE_CONFIDENCE regression (trust-killer)
    ///   ΔNML   &gt; +5pp  → NOT_MODELABLE regression (coverage dropped)
    ///
    /// Usage:
    ///   vett bench regress --metrics-json /path/to/metrics.json
    /// </summary>
    private static Command CreateRegress(ILogger logger)
    {
        var cmd = new Command("regress", "Compare latest metrics.json run against best baseline and flag regressions");

        var metricsOpt = new Option<string>("--metrics-json") {
            Required = true,
            Description = "Path to metrics.json written by `vett bench grade --metrics-json`.",
        };
        var passThreshOpt = new Option<double>("--pass-threshold") {
            DefaultValueFactory = _ => 2.0,
            Description = "Max allowed drop in PASS rate (percentage points). Default 2.",
        };
        var fcThreshOpt = new Option<double>("--fc-threshold") {
            DefaultValueFactory = _ => 2.0,
            Description = "Max allowed rise in FALSE_CONFIDENCE rate (percentage points). Default 2.",
        };
        var nmlThreshOpt = new Option<double>("--nml-threshold") {
            DefaultValueFactory = _ => 5.0,
            Description = "Max allowed rise in NOT_MODELABLE rate (percentage points). Default 5.",
        };

        cmd.Add(metricsOpt);
        cmd.Add(passThreshOpt);
        cmd.Add(fcThreshOpt);
        cmd.Add(nmlThreshOpt);

        cmd.SetAction(async (pr, _) =>
        {
            await Task.Yield();

            var metricsPath = pr.GetValue(metricsOpt)!;
            if (!File.Exists(metricsPath))
            {
                logger.LogError("metrics.json not found: {Path}", metricsPath);
                return 1;
            }

            List<MetricsEntry> entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<MetricsEntry>>(File.ReadAllText(metricsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to parse metrics.json: {Error}", ex.Message);
                return 1;
            }

            if (entries.Count == 0)
            {
                Console.WriteLine("metrics.json is empty — no baseline to compare against.");
                return 0;
            }
            if (entries.Count == 1)
            {
                Console.WriteLine($"Only one entry in metrics.json (run_id={entries[0].RunId}). No baseline to compare against yet.");
                PrintEntry(entries[0]);
                return 0;
            }

            // Latest = last entry; best baseline = highest pass rate across all prior entries.
            var latest = entries[^1];
            var prior  = entries[..^1];

            double PassRate(MetricsEntry e) => e.Attempted == 0 ? 0 : 100.0 * e.Pass / e.Attempted;
            double FcRate(MetricsEntry e)   => e.Attempted == 0 ? 0 : 100.0 * e.FalseConfidence / e.Attempted;
            double NmlRate(MetricsEntry e)  => e.Attempted == 0 ? 0 : 100.0 * e.NotModelable / e.Attempted;

            var bestPass = prior.Max(PassRate);
            var bestFc   = prior.Min(FcRate);
            var bestNml  = prior.Min(NmlRate);

            var latestPass = PassRate(latest);
            var latestFc   = FcRate(latest);
            var latestNml  = NmlRate(latest);

            var deltaPass = latestPass - bestPass;
            var deltaFc   = latestFc   - bestFc;
            var deltaNml  = latestNml  - bestNml;

            var passThresh = pr.GetValue(passThreshOpt);
            var fcThresh   = pr.GetValue(fcThreshOpt);
            var nmlThresh  = pr.GetValue(nmlThreshOpt);

            Console.WriteLine($"Regression check: latest run_id={latest.RunId}");
            Console.WriteLine($"  Baseline (best of {prior.Count} prior runs):");
            Console.WriteLine($"    PASS={bestPass:F1}%  FC={bestFc:F1}%  NML={bestNml:F1}%");
            Console.WriteLine($"  Latest:  PASS={latestPass:F1}%  FC={latestFc:F1}%  NML={latestNml:F1}%");
            Console.WriteLine($"  Delta:   PASS={deltaPass:+0.0;-0.0}pp  FC={deltaFc:+0.0;-0.0}pp  NML={deltaNml:+0.0;-0.0}pp");

            var blockers = new List<string>();
            if (deltaPass < -passThresh) blockers.Add($"PASS dropped {-deltaPass:F1}pp (threshold {passThresh}pp)");
            if (deltaFc   >  fcThresh)   blockers.Add($"FALSE_CONFIDENCE rose {deltaFc:F1}pp (threshold {fcThresh}pp)");
            if (deltaNml  >  nmlThresh)  blockers.Add($"NOT_MODELABLE rose {deltaNml:F1}pp (threshold {nmlThresh}pp)");

            if (blockers.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("BLOCKER — regression detected:");
                foreach (var b in blockers)
                    Console.WriteLine($"  ✗ {b}");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("OK — no regressions above thresholds.");
            return 0;
        });

        return cmd;
    }

    private static void PrintEntry(MetricsEntry e)
    {
        int att = e.Attempted;
        string Pct(int n) => att == 0 ? "0%" : $"{100.0 * n / att:F1}%";
        Console.WriteLine($"  run_id={e.RunId}  attempted={att}  pass={e.Pass}({Pct(e.Pass)})  fc={e.FalseConfidence}({Pct(e.FalseConfidence)})  nml={e.NotModelable}({Pct(e.NotModelable)})");
    }

    private sealed class MetricsEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("run_id")]          public string RunId { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("attempted")]       public int Attempted { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pass")]            public int Pass { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("false_confidence")] public int FalseConfidence { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("not_modelable")]   public int NotModelable { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("timeout")]         public int Timeout { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("abstain")]         public int Abstain { get; set; }
    }

    /// <summary>
    /// Append a single-line JSON entry to metrics.json (creates the file as a JSON array if missing).
    /// Each entry is a compact object: run_id, suite, timestamps, and per-bucket counts.
    /// Consumers (autonomous dashboard) read the array and compute trends across runs.
    /// </summary>
    private static void AppendMetrics(string metricsPath, RunSummary summary)
    {
        var instances = summary.Instances;
        var entry = new
        {
            run_id        = summary.RunId,
            suite         = summary.Suite,
            profile       = summary.Profile,
            model         = summary.Model,
            started_at    = summary.StartedAt,
            completed_at  = summary.CompletedAt,
            attempted     = instances.Count,
            not_modelable = instances.Count(r => r.Resolution == ResolutionClassifier.NotModelable),
            timeout       = instances.Count(r => r.Resolution == ResolutionClassifier.Timeout),
            abstain       = instances.Count(r => r.Resolution == ResolutionClassifier.Abstain),
            pass          = instances.Count(r => r.Resolution == ResolutionClassifier.Pass),
            false_confidence = instances.Count(r => r.Resolution == ResolutionClassifier.FalseConfidence),
        };

        // A bare `catch { }` here used to leave `existing` empty and then write it
        // back, silently REPLACING the whole metrics history with one entry — and
        // printing "(total entries: 1)", which reads as a healthy first run. Two
        // distinct failures reached that catch and only one of them is corruption:
        // a transient IO failure (a concurrent process holding the file — routine
        // in this tree) destroyed the history just as thoroughly as bad JSON.
        // Neither is allowed to delete data now.
        List<JsonElement> existing = [];
        var startedFresh = false;
        if (File.Exists(metricsPath))
        {
            string? text = null;
            for (var attempt = 1; attempt <= 3 && text is null; attempt++)
            {
                try { text = File.ReadAllText(metricsPath); }
                catch (IOException) when (attempt < 3) { Thread.Sleep(150 * attempt); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // TRANSIENT, not corrupt. Refuse to write rather than overwrite a
                    // history we merely failed to read. "Could not measure" is not "empty".
                    throw new IOException(
                        $"Could not read existing metrics at {metricsPath} after 3 attempts " +
                        $"({ex.GetType().Name}: {ex.Message}). Refusing to write, because writing " +
                        $"here would REPLACE the existing history with a single entry. " +
                        $"This run's metrics were NOT recorded; the existing file is untouched.", ex);
                }
            }

            if (text is not null)
            {
                try { existing = JsonSerializer.Deserialize<List<JsonElement>>(text) ?? []; }
                catch (JsonException ex)
                {
                    // Genuinely corrupt. Preserve it instead of overwriting it — moving
                    // aside is the substitute for deleting.
                    var aside = $"{metricsPath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                    File.Move(metricsPath, aside);
                    startedFresh = true;
                    Console.WriteLine(
                        $"⚠ Metrics file was not valid JSON ({ex.Message.Split('\n')[0]}). " +
                        $"PRESERVED as: {aside}");
                    Console.WriteLine("⚠ Starting a fresh metrics file. The entry count below counts ONLY the new file.");
                }
            }
        }

        existing.Add(JsonSerializer.SerializeToElement(entry));
        File.WriteAllText(metricsPath, JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }));
        var provenance = startedFresh ? "  [FRESH FILE — prior history moved aside, NOT continued]" : "";
        Console.WriteLine($"Metrics appended to: {metricsPath}  (total entries: {existing.Count}){provenance}");
    }
}
