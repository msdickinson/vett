using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vett.Bench.Team;
using Vett.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Cli;

/// <summary>
/// `vett team-bench` — runs sub-agent dispatch benchmarks. Each suite
/// YAML lists instances with a prompt + workspace template + structural
/// assertions; the harness drives a real chat loop end-to-end and
/// asserts deterministically (no LLM judge).
///
/// Tiers (suite metadata only, the runner doesn't gate on them):
///   1 — must always pass; failure = release blocker
///   2 — should pass; failure = bug to fix soon
///   3 — aspirational; failures expected while dialing in
///
/// Usage:
///   vett team-bench team-smoke-tier1
///   vett team-bench team-smoke-tier1 --instance t3-readme-create
///   vett team-bench team-smoke-tier1 --repeat 5
/// </summary>
public static class TeamBenchCommand
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static Command Create(ILogger logger)
    {
        var cmd = new Command("team-bench", "Run sub-agent dispatch benchmarks");

        var suiteArg = new Argument<string>("suite") { Description = "Suite name (e.g. team-smoke-tier1) or path to YAML." };
        cmd.Add(suiteArg);

        var instanceOpt = new Option<string?>("--instance", "-i") { Description = "Run only the named instance from the suite." };
        cmd.Add(instanceOpt);

        var repeatOpt = new Option<int>("--repeat", "-n") { Description = "Run each instance N times. Reports pass/total per instance.", DefaultValueFactory = _ => 1 };
        cmd.Add(repeatOpt);

        var endpointOpt = new Option<string?>("--endpoint") { Description = "Override LLM endpoint." };
        cmd.Add(endpointOpt);

        var modelOpt = new Option<string?>("--model") { Description = "Override LLM model." };
        cmd.Add(modelOpt);

        var apiKeyOpt = new Option<string?>("--api-key") { Description = "Override LLM API key." };
        cmd.Add(apiKeyOpt);

        var profileOpt = new Option<string?>("--profile") { Description = "Override the profile referenced by the suite (run the same scenarios against a different team config — e.g. coding-team vs coding-team-mixed)." };
        cmd.Add(profileOpt);

        var keepWorktreesOpt = new Option<bool>("--keep-all-worktrees") {
            Description = "Override the profile's dispatch_retention to 'keep-all' for this run — every dispatch worktree survives even on PASS, for cross-profile diagnostics (e.g. measuring malformed_tool_call rates between thinking-on and thinking-off).",
            DefaultValueFactory = _ => false,
        };
        cmd.Add(keepWorktreesOpt);

        var workspaceDirOpt = new Option<string?>("--workspace-dir") {
            Description = "Adopt an existing workspace dir instead of creating a fresh one per instance. Used by Q3 decomposition chains where successive sub-tasks build on the previous step's state. The dir must exist; seed_files are NOT re-applied; the workspace is NEVER auto-deleted on PASS. Only meaningful with --instance (running ONE specific instance against the dir).",
        };
        cmd.Add(workspaceDirOpt);

        var jsonOpt = new Option<bool>("--json") {
            Description = "Emit a single JSON summary document to stdout instead of human-readable text. Progress is written to stderr so it stays visible during long runs. Wire format is stable (snake_case keys, see TeamBenchJsonSummary); this is the foundation for the Tier 1 event stream and replaces text-scraping in downstream consumers.",
            DefaultValueFactory = _ => false,
        };
        cmd.Add(jsonOpt);

        cmd.SetAction(async (pr, ct) =>
        {
            var jsonMode = pr.GetValue(jsonOpt);

            // Force per-line flush so progress is visible in real time
            // when the bench is captured by another process (CI, > file).
            // Default behavior is line-buffered when stdout is a pipe,
            // which means nothing appears until the process exits — and
            // if it hangs in cleanup, you see nothing at all.
            //
            // In --json mode, redirect Console.Out itself to stderr so any
            // logger / progress / harness warnings that try to write to
            // stdout end up on stderr. The actual JSON document at the
            // end goes to the real-stdout writer captured here. Without
            // this, in-flight `logger.LogWarning(...)` calls inside the
            // harness (e.g. "timeout after Ns without settle") corrupt the
            // JSON document downstream consumers parse from stdout.
            Console.Out.Flush();
            var realStdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            if (jsonMode)
            {
                Console.SetOut(Console.Error);
            }
            else
            {
                Console.SetOut(realStdout);
            }

            // `status` is the sink for human-readable lines; in either mode
            // this is now Console.Out (which is real-stdout in text mode,
            // stderr in json mode).
            TextWriter status = Console.Out;

            // In --json mode, validation failures still need a parseable
            // stdout document so consumers can branch on `kind`/`exit_code`
            // instead of trying to recover from a mix of logger output and
            // text. Returns the exit code so callers can `return EmitJsonError(...)`.
            int EmitJsonError(string message, int exitCode)
            {
                if (jsonMode)
                {
                    var err = new TeamBenchJsonSummary {
                        Kind = "team_bench_error",
                        ExitCode = exitCode,
                        // Reuse the `runs[0].error` slot to carry the message
                        // — keeps the shape stable (no extra top-level field
                        // consumers have to learn) and lets the existing
                        // run-iteration deserializer surface it.
                        Runs = [ new TeamBenchJsonRun { Error = message } ],
                    };
                    realStdout.WriteLine(JsonSerializer.Serialize(err, new JsonSerializerOptions { WriteIndented = true }));
                    Console.Error.WriteLine(message);
                }
                else
                {
                    logger.LogError("{Msg}", message);
                }
                return exitCode;
            }

            var suiteName = pr.GetValue(suiteArg) ?? "";
            var only = pr.GetValue(instanceOpt);
            var repeat = Math.Max(1, pr.GetValue(repeatOpt));
            var workspaceOverride = pr.GetValue(workspaceDirOpt);
            if (!string.IsNullOrWhiteSpace(workspaceOverride))
            {
                // Adopted-workspace mode is single-shot: one instance,
                // one repeat, against a caller-owned dir. Reject ambiguous
                // combos so the user can't accidentally run a 22-instance
                // suite against one workspace and trample state.
                if (string.IsNullOrEmpty(only))
                    return EmitJsonError("--workspace-dir requires --instance to be set (running multiple instances against one dir would trample state).", 2);
                if (repeat != 1)
                    return EmitJsonError("--workspace-dir requires --repeat=1 (repeating against one dir compounds state across runs).", 2);
            }
            else
            {
                workspaceOverride = null;
            }

            var suite = Yaml.Resolve(suiteName, "suites", LoadSuite);
            if (suite is null)
            {
                return EmitJsonError($"Suite '{suiteName}' not found in any of: {string.Join(", ", Yaml.ResolveSearchDirs("suites"))}", 2);
            }

            var profileName = pr.GetValue(profileOpt) ?? suite.Profile;
            // Resolve the FILE, not just the object: `ds-team-flash` can
            // name any of three different files (cwd / ~/.vett / install
            // dir) and they are not required to agree, so a run that
            // records only the name cannot prove which ruler it used.
            var (profile, profileResolvedPath) = Yaml.ResolveWithPath(profileName, "profiles", Yaml.LoadProfile);
            if (profile is null || profileResolvedPath is null)
            {
                return EmitJsonError($"Profile '{profileName}' not found in any of: {string.Join(", ", Yaml.ResolveSearchDirs("profiles"))}", 2);
            }
            profileResolvedPath = Path.GetFullPath(profileResolvedPath);
            Yaml.ValidateProfileForRun(profile, profileResolvedPath, pr.GetValue(endpointOpt), pr.GetValue(modelOpt));

            // Keys the deserialiser silently dropped. Non-fatal — an old
            // vett must still run a profile written for a newer one — but
            // NEVER silent: a dropped key means the knob this arm was
            // supposed to differ by never reached the code.
            var profileAudit = ProfileKeyAudit.AuditProfileFile(profileResolvedPath);
            foreach (var u in profileAudit.KeysOrEmpty)
                status.WriteLine($"WARNING: profile key ignored — {u} [{profileResolvedPath}]");

            // ⛔ An audit that could not run is NOT a clean profile. Say so
            // loudly here and publish null on the wire, so a reader can tell
            // an unverified run from a verified one instead of reading `[]`
            // as a positive claim of cleanliness.
            if (!profileAudit.Measured)
                status.WriteLine(
                    $"WARNING: profile key audit DID NOT RUN — {profileAudit.UnmeasuredReason} "
                    + $"[{profileResolvedPath}]. This run's config is UNVERIFIED: a misspelled "
                    + "knob would be silently ignored and an A/B against another arm may have "
                    + "compared two identical configurations.");

            var profileOverridden = profileName != suite.Profile;
            if (profileOverridden)
                status.WriteLine($"Note: profile overridden — suite says '{suite.Profile}', running against '{profileName}'");

            var keepAllWorktrees = pr.GetValue(keepWorktreesOpt);
            if (keepAllWorktrees && profile.Team is not null)
            {
                var prior = profile.Team.DispatchRetention;
                profile.Team.DispatchRetention = "keep-all";
                if (!string.Equals(prior, "keep-all", StringComparison.Ordinal))
                    status.WriteLine($"Note: --keep-all-worktrees set — dispatch_retention overridden from '{prior}' to 'keep-all' for this run.");
            }

            var endpoint = Helpers.Env(pr.GetValue(endpointOpt), "VETT_LLM_ENDPOINT");
            endpoint = Vett.Llm.ChatClientFactory.FoldProfileValue(profile.Llm, endpoint, profile.Llm.Endpoint);
            var model = Helpers.Env(pr.GetValue(modelOpt), "VETT_LLM_MODEL");
            model = Vett.Llm.ChatClientFactory.FoldProfileValue(profile.Llm, model, profile.Llm.Model);
            var apiKey = Helpers.Env(pr.GetValue(apiKeyOpt), "VETT_LLM_API_KEY");
            if (string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(profile.Llm.ApiKeyEnv))
                apiKey = Environment.GetEnvironmentVariable(profile.Llm.ApiKeyEnv) ?? "";

            var instances = suite.Instances;
            if (only is not null) instances = instances.Where(i => i.Id == only).ToList();
            if (instances.Count == 0)
            {
                return EmitJsonError($"No instances to run (suite has {suite.Instances.Count}, filter='{only}').", 2);
            }

            // Print the EFFECTIVE profile, not the suite's declared one: with
            // --profile the two differ, and this header is what a saved log gets
            // read back as months later. The JSON summary already records the
            // override correctly (ProfileName/ProfileOverridden below); this line
            // was the one place a run could be mis-attributed to its suite's
            // default profile.
            status.WriteLine($"Suite: {suite.Name} (tier {suite.Tier})  profile={profileName}{(profileOverridden ? $" (overrides suite '{suite.Profile}')" : "")}  instances={instances.Count} × {repeat} = {instances.Count * repeat} runs");
            // The NAME is ambiguous across the three profile stores; the
            // PATH is what a reader months later needs to re-derive the
            // ruler. Printed unconditionally, next to the name it qualifies.
            status.WriteLine($"Profile file: {profileResolvedPath}");
            status.WriteLine();

            var startedAt = DateTime.UtcNow;
            var runIndexByInstance = new Dictionary<string, int>();
            var allResults = new List<(TeamBenchResult Result, int RunIndex, TeamBenchInstance Instance)>();
            foreach (var instance in instances)
            {
                for (int r = 0; r < repeat; r++)
                {
                    var label = repeat > 1 ? $"{instance.Id} (run {r + 1}/{repeat})" : instance.Id;
                    status.Write($"  {label} ... ");
                    var result = await Harness.RunInstanceAsync(instance, profile, endpoint, model, apiKey, logger, ct, workspaceOverride);
                    var runIndex = runIndexByInstance.TryGetValue(instance.Id, out var cur) ? cur : 0;
                    runIndexByInstance[instance.Id] = runIndex + 1;
                    allResults.Add((result, runIndex, instance));
                    var verdict = result.Pass ? "PASS" : "FAIL";
                    status.WriteLine($"{verdict}  ({result.WallClock.TotalSeconds:F1}s, leader={result.LeaderIterations} iters, members=[{string.Join(", ", result.MemberIterations.Select(kv => $"{kv.Key}={kv.Value}"))}])");
                    if (!result.Pass)
                    {
                        if (!string.IsNullOrEmpty(result.Error))
                            status.WriteLine($"      ! {result.Error}");
                        foreach (var ar in result.AssertionResults)
                        {
                            var mark = ar.Pass ? "  ✓" : "  ✗";
                            status.WriteLine($"    {mark} {ar.Description}{(ar.Detail is not null ? $" — {ar.Detail}" : "")}");
                        }
                        if (!string.IsNullOrEmpty(result.SessionLogPath))
                            status.WriteLine($"    session log: {result.SessionLogPath}");
                    }
                }
            }
            var completedAt = DateTime.UtcNow;

            // Summary
            status.WriteLine();
            var totalPass = allResults.Count(r => r.Result.Pass);
            var totalRuns = allResults.Count;
            status.WriteLine($"Summary: {totalPass}/{totalRuns} runs passed");
            if (repeat > 1)
            {
                foreach (var grp in allResults.GroupBy(r => r.Result.InstanceId))
                {
                    var p = grp.Count(g => g.Result.Pass);
                    var t = grp.Count();
                    status.WriteLine($"  {grp.Key}: {p}/{t}");
                }
            }
            var exitCode = totalPass == totalRuns ? 0 : 1;

            if (jsonMode)
            {
                var summary = new TeamBenchJsonSummary {
                    RunId = $"team-bench-{startedAt:yyyyMMddTHHmmssZ}-{suite.Name}",
                    SuiteName = suite.Name,
                    SuiteTier = suite.Tier,
                    ProfileName = profileName,
                    ProfileOverridden = profileOverridden,
                    ProfilePath = profileResolvedPath,
                    // null when the audit could not run — the sentinel the wire
                    // contract already documents. Emitting `[]` here would be a
                    // claim of cleanliness derived from a failure to measure.
                    ProfileUnknownKeys = profileAudit.Keys?.Select(u => u.ToString()).ToList(),
                    Endpoint = endpoint,
                    Model = model,
                    Repeat = repeat,
                    KeepAllWorktrees = keepAllWorktrees,
                    InstanceCount = instances.Count,
                    TotalRuns = totalRuns,
                    TotalPassed = totalPass,
                    StartedAt = startedAt.ToString("O"),
                    CompletedAt = completedAt.ToString("O"),
                    TotalDurationSeconds = (completedAt - startedAt).TotalSeconds,
                    ExitCode = exitCode,
                    Runs = allResults.Select(t => new TeamBenchJsonRun {
                        InstanceId = t.Result.InstanceId,
                        RunIndex = t.RunIndex,
                        Pass = t.Result.Pass,
                        WallClockSeconds = t.Result.WallClock.TotalSeconds,
                        LeaderIterations = t.Result.LeaderIterations,
                        MemberIterations = new Dictionary<string, int>(t.Result.MemberIterations),
                        Error = t.Result.Error,
                        StopReason = t.Result.StopReason,
                        SessionLogPath = t.Result.SessionLogPath,
                        Assertions = t.Result.AssertionResults.Select(ar => new TeamBenchJsonAssertion {
                            Description = ar.Description,
                            Pass = ar.Pass,
                            Detail = ar.Detail,
                        }).ToList(),
                        Taxonomy = t.Instance.Taxonomy is null ? null : new Dictionary<string, string>(t.Instance.Taxonomy),
                        SelfAssessment = t.Result.SelfAssessment is null ? null : new TeamBenchRunSelfAssessment {
                            Captured = t.Result.SelfAssessment.Captured,
                            CaptureOutcome = t.Result.SelfAssessment.CaptureOutcome,
                            Confidence = t.Result.SelfAssessment.Confidence,
                            PredictedPass = t.Result.SelfAssessment.PredictedPass,
                            Reasoning = t.Result.SelfAssessment.Reasoning,
                            RawText = t.Result.SelfAssessment.RawText,
                        },
                    }).ToList(),
                    PerInstance = allResults.GroupBy(r => r.Result.InstanceId)
                        .Select(g => new TeamBenchJsonPerInstance {
                            InstanceId = g.Key,
                            Passed = g.Count(x => x.Result.Pass),
                            Total = g.Count(),
                        }).ToList(),
                };
                realStdout.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                realStdout.Flush();
            }

            return exitCode;
        });

        return cmd;
    }

    private static TeamBenchSuite LoadSuite(string path)
        => D.Deserialize<TeamBenchSuite>(File.ReadAllText(path));
}
