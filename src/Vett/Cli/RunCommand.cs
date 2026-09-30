using Microsoft.Extensions.Logging;
using System.CommandLine;
using Vett.Config;
using Vett.Live;
using Vett.Llm;
using Microsoft.Extensions.AI;
using Vett.Sandbox;

namespace Vett.Cli;

public static class RunCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command("run", "Run a benchmark suite");

        var suiteOpt = new Option<string>("--suite") { Required = true };
        var profileOpt = new Option<string>("--profile") { Required = true };
        // Repeatable. Multiple --endpoint flags => instances are pinned round-robin
        // (instance i hits endpoint[i % N]) so the entire conversation stays on one
        // vLLM, preserving the prefix cache. Per-request round-robin would cause
        // catastrophic re-prefill at long context.
        var endpointOpt = new Option<string[]>("--endpoint") { AllowMultipleArgumentsPerToken = true };
        var modelOpt = new Option<string>("--model");
        var apiKeyOpt = new Option<string>("--api-key");
        var instancesOpt = new Option<int>("--instances");
        var outputOpt = new Option<string>("--output");
        var concurrencyOpt = new Option<int>("--concurrency") { DefaultValueFactory = _ => 1 };
        // Optional live event-stream surfaces. Both are off by default —
        // existing behaviour is preserved when neither is set.
        // --trajectory-dir: append every Event to <dir>/events.jsonl plus
        //   per-instance <dir>/instances/<id>/events.jsonl. Cheap, ~1 KB/event,
        //   survives the run, AI-Timeline can replay later.
        // --live-port: also host an SSE endpoint at http://0.0.0.0:N/events
        //   for real-time dashboards (browser EventSource or `curl -N`). The
        //   built-in HTML viewer is at http://0.0.0.0:N/.
        var trajectoryDirOpt = new Option<string?>("--trajectory-dir");
        var livePortOpt = new Option<int>("--live-port") { DefaultValueFactory = _ => 0 };

        cmd.Add(suiteOpt);
        cmd.Add(profileOpt);
        cmd.Add(endpointOpt);
        cmd.Add(modelOpt);
        cmd.Add(apiKeyOpt);
        cmd.Add(instancesOpt);
        cmd.Add(outputOpt);
        cmd.Add(concurrencyOpt);
        cmd.Add(trajectoryDirOpt);
        cmd.Add(livePortOpt);

        // ⛔ THE ACTION RETURNS int, AND THAT IS THE POINT. Until 2026-08-25 this
        // lambda returned plain Task and every fatal path below was a bare
        // `return;` after a LogError — so `vett run` printed a fatal error and
        // exited 0. Measured: a nonexistent --profile AND a nonexistent --suite
        // both exited 0. Any wrapper, script or CI step gating on $? read a
        // total config failure as success, and the run it thought it had was
        // never executed.
        //
        // Codes match TeamBenchCommand's existing convention rather than
        // inventing a second one: 2 = config/usage error (nothing ran),
        // 1 = ran but instances errored, 0 = clean.
        cmd.SetAction(async (pr, ct) =>
        {
            // ⛔ THE PROFILE IS RESOLVED FIRST BECAUSE IT IS THE THIRD PRECEDENCE
            // RUNG. Until 2026-08-26 the endpoint/model guard below ran BEFORE
            // this line and consulted only the CLI and VETT_LLM_*, so `vett run`
            // refused every profile that declared its own llm.endpoint /
            // llm.model — which is all of them. Measured: ds-solo-pro passes
            // `validate --check-endpoints` with "(endpoints live)" and then
            // `run` exits 2 with "--endpoint + --model required".
            //
            // The documented order is CLI > env > profile, and ChatCommand
            // (:119-122) and EditCommand (:64-67) both implement all three.
            // Only run and bench stopped at rung 2.
            //
            // The refusal was not the whole cost. The workaround is to restate
            // the endpoint on the command line, and CLI values REBIND THE
            // PRIMARY SEAT (ChatClientFactory.Create: "CLI overrides apply only
            // to the primary"). So a stale paste silently runs a different
            // model than the profile names while every artifact still records
            // the profile — an A/B whose arms are pinned to whatever was typed.
            // Members are unaffected: Coordinator:1588 builds them from their
            // own merged blocks with no overrides.
            var profile = Yaml.Resolve(pr.GetValue(profileOpt)!, "profiles", Yaml.LoadProfile);
            var suite = Yaml.Resolve(pr.GetValue(suiteOpt)!, "suites", Yaml.LoadSuite);
            if (profile is null || suite is null)
            {
                // Name WHICH one is missing and WHERE it was looked for. The old
                // text was "profile or suite not found" — an or, with no search
                // path — so the reader could not tell which of the two names was
                // wrong, nor that three stores are consulted in a fixed order.
                if (profile is null)
                    logger.LogError("Profile '{Name}' not found in any of: {Dirs}",
                        pr.GetValue(profileOpt), string.Join(", ", Yaml.ResolveSearchDirs("profiles")));
                if (suite is null)
                    logger.LogError("Suite '{Name}' not found in any of: {Dirs}",
                        pr.GetValue(suiteOpt), string.Join(", ", Yaml.ResolveSearchDirs("suites")));
                return 2;
            }

            // CLI endpoints win over env, env over the profile. Env supports a
            // comma-separated list for parity with the multi-endpoint CLI, e.g.
            // VETT_LLM_ENDPOINT=u1,u2.
            //
            // The endpoint list has to be filled in here rather than left to
            // ChatClientFactory (which does fall back to config.Endpoint on an
            // empty override): the list drives `endpoints.Select(...)` below, so
            // an empty list builds ZERO clients regardless of what the profile
            // says. model/apiKey need no such treatment — CreateSingle resolves
            // config.Model and config.ApiKeyEnv itself when the override is
            // empty — but both are read here anyway so the guard can tell the
            // operator which rung is actually missing.
            var endpoints = pr.GetValue(endpointOpt) ?? [];
            if (endpoints.Length == 0)
            {
                var envEndpoint = Environment.GetEnvironmentVariable("VETT_LLM_ENDPOINT") ?? "";
                if (!string.IsNullOrEmpty(envEndpoint))
                    endpoints = envEndpoint.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            if (endpoints.Length == 0
                && !ChatClientFactory.BindsThroughCapacity(profile.Llm)
                && !string.IsNullOrEmpty(profile.Llm.Endpoint))
                endpoints = [profile.Llm.Endpoint];
            // Rung below the profile: the provider's own base URL (see
            // ChatClientFactory.DefaultEndpointFor). Keeps `run` consistent
            // with `chat` for openai / anthropic / google profiles.
            if (endpoints.Length == 0
                && ChatClientFactory.DefaultEndpointFor(profile.Llm.Provider) is { } providerDefault)
                endpoints = [providerDefault];

            // A capability profile names no endpoint of its own, so the list
            // is legitimately empty here. Seed it with ONE empty slot: an
            // empty override reads as "no override" in the factory, which is
            // what lets the capacity resolver choose. Without this the guard
            // below is satisfied but `endpoints.Select(...)` yields ZERO
            // clients -- a silently empty run rather than an error.
            if (endpoints.Length == 0 && ChatClientFactory.BindsThroughCapacity(profile.Llm))
                endpoints = [""];

            var model = Helpers.Env(pr.GetValue(modelOpt), "VETT_LLM_MODEL");
            model = ChatClientFactory.FoldProfileValue(profile.Llm, model, profile.Llm.Model);
            var apiKey = Helpers.Env(pr.GetValue(apiKeyOpt), "VETT_LLM_API_KEY");

            if (!ChatClientFactory.BindsThroughCapacity(profile.Llm)
                && (endpoints.Length == 0 || string.IsNullOrEmpty(model)))
            {
                // Name the rung that is missing and the file that was consulted.
                // "--endpoint + --model required" sent the reader to the command
                // line for something the profile is supposed to supply.
                logger.LogError(
                    "Error: no LLM endpoint/model. endpoint={Endpoint} model={Model}. "
                    + "Set llm.endpoint / llm.model in the profile [{Path}], or pass "
                    + "--endpoint / --model, or set VETT_LLM_ENDPOINT / VETT_LLM_MODEL.",
                    endpoints.Length == 0 ? "(none)" : string.Join(",", endpoints),
                    string.IsNullOrEmpty(model) ? "(none)" : model,
                    profile.SourcePath ?? pr.GetValue(profileOpt)!);
                return 2;
            }
            try
            {
                // Name the FILE, not the CLI argument: the same name can come
                // from <cwd>/profiles, ~/.vett/profiles or the install dir, and
                // "profile foo has 2 configuration errors" is unactionable when
                // the reader cannot tell which foo.
                Yaml.ValidateProfileForRun(profile, profile.SourcePath ?? pr.GetValue(profileOpt)!,
                    string.Join(",", endpoints), model);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError("{Message}", ex.Message);
                return 2;
            }

            // Keys the deserialiser silently dropped (IgnoreUnmatchedProperties).
            // Non-fatal, never silent — a dropped key is a knob that did not
            // reach the code, which makes any A/B against another arm suspect.
            if (profile.SourcePath is { } profileSrc)
            {
                var audit = ProfileKeyAudit.AuditProfileFile(profileSrc);
                foreach (var u in audit.KeysOrEmpty)
                    logger.LogWarning("Profile key ignored — {Key} [{Path}]", u.ToString(), profileSrc);

                // An audit that could not run is not a clean profile.
                if (!audit.Measured)
                    logger.LogWarning(
                        "Profile key audit DID NOT RUN — {Reason} [{Path}]. This run's config is "
                        + "UNVERIFIED: a misspelled knob would be silently ignored.",
                        audit.UnmeasuredReason, profileSrc);
            }

            // A team block here is not a dropped KEY (the audit above cannot
            // see it — every key binds) but a dropped FEATURE: `vett run` is
            // solo-only. Same consequence, larger blast radius.
            if (Helpers.TeamBlockIgnoredWarning(profile, "run") is { } teamWarning)
                logger.LogWarning("{Message}", teamWarning);

            var sidecar = Helpers.FindSidecarWithPaths(out var sidecarSearched);
            if (sidecar is null)
            {
                logger.LogError(
                    "Sidecar binary not found. Searched:\n  {Paths}\n\n" +
                    "Fix: either set VETT_SIDECAR_PATH to point at the binary, or " +
                    "build it with `make sidecar-all` (or .\build.ps1) in sidecar/, which " +
                    "writes to bin/ at the repo root. Release builds of the dotnet " +
                    "tool carry the binaries next to it.",
                    string.Join("\n  ", sidecarSearched));
                return 2;
            }

            // Resolve suite file path for JSONL loading.
            var suiteResolvedPath = Yaml.Resolve(pr.GetValue(suiteOpt)!, "suites", p => p);
            var suiteDir = Path.GetDirectoryName(suiteResolvedPath ?? ".") ?? ".";

            // ⛔ A TEAM SUITE HAS NO loader.path, AND Path.Combine(dir, "")
            // RETURNS THE DIRECTORY. `vett run --suite team-minor-edit-tier1`
            // therefore handed LoadJsonl the suites/ FOLDER and died with an
            // unhandled UnauthorizedAccessException + stack trace at rc=1 —
            // which reads as "permissions are broken on your checkout", not as
            // "that suite runs under a different command". Both suite kinds
            // live in the same directory and resolve through the same
            // --suite name, so picking the wrong one is the expected mistake,
            // not an exotic one.
            if (string.IsNullOrWhiteSpace(suite.Loader.Path))
            {
                logger.LogError(
                    "Suite '{Name}' declares no loader.path [{Path}]. `vett run` executes "
                    + "JSONL-backed benchmark suites; a suite with no loader is a TEAM suite "
                    + "and runs under `vett team-bench {Name} --profile <profile>`.",
                    pr.GetValue(suiteOpt), suiteResolvedPath ?? "unresolved", pr.GetValue(suiteOpt));
                return 2;
            }

            var jsonlPath = Helpers.ExpandHome(suite.Loader.Path);
            if (!Path.IsPathRooted(jsonlPath))
                jsonlPath = Path.Combine(suiteDir, jsonlPath);
            if (!File.Exists(jsonlPath))
            {
                // Same reasoning: name the file, do not throw a stack trace at
                // an operator who mistyped a manifest path.
                logger.LogError(
                    "Suite '{Name}' points at a manifest that does not exist: {Jsonl} "
                    + "(loader.path '{Declared}' resolved against {Dir}) [{Path}].",
                    pr.GetValue(suiteOpt), jsonlPath, suite.Loader.Path, suiteDir,
                    suiteResolvedPath ?? "unresolved");
                return 2;
            }

            var instances = Runner.BenchmarkRunner.LoadJsonl(jsonlPath);

            var max = pr.GetValue(instancesOpt);
            if (max > 0)
                instances = instances.Take(max).ToList();

            var outputDir = pr.GetValue(outputOpt)
                ?? Path.Combine("results", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");

            // One IChatClient per endpoint. Each instance is pinned to one client
            // (round-robin by index in the runner) so its prefix cache stays warm
            // on a single vLLM.
            var clients = endpoints
                .Select(ep => ChatClientFactory.Create(profile.Llm, ep, model, apiKey))
                .ToList();
            // Join the model name back up with the clients. See
            // ChatClientFactory.EffectiveModel: for a capability profile `model`
            // is legitimately empty and only the client knows what the catalogue
            // chose. Read it from the FIRST client -- every client here shares
            // one profile, so they share one capability and one resolved model;
            // if that ever stops being true this is the line that has to change.
            if (clients.Count > 0) model = ChatClientFactory.EffectiveModel(clients[0], model);
            if (clients.Count > 1)
                logger.LogInformation("Using {Count} endpoints (round-robin per instance): {Endpoints}",
                    clients.Count, string.Join(", ", endpoints));

            // Live streaming: file sink and/or SSE server. Both opt-in. If
            // neither is requested, no extra threads or files are created.
            var trajectoryDir = pr.GetValue(trajectoryDirOpt);
            // Default trajectory dir to <output>/trajectory if --live-port is set
            // but --trajectory-dir wasn't — gives the SSE consumer a file replay
            // for free without forcing the user to pass two flags.
            var livePort = pr.GetValue(livePortOpt);
            if (string.IsNullOrEmpty(trajectoryDir) && livePort > 0)
                trajectoryDir = Path.Combine(outputDir, "trajectory");

            using var liveSink = (string.IsNullOrEmpty(trajectoryDir) && livePort <= 0)
                ? null
                : new LiveSink(trajectoryDir);
            using var liveServer = (livePort > 0 && liveSink is not null)
                ? new LiveServer(liveSink, livePort)
                : null;
            liveServer?.Start();
            if (liveServer is not null)
                logger.LogInformation("Live event stream: http://0.0.0.0:{Port}/events  (browser viewer: http://0.0.0.0:{Port}/)", livePort, livePort);
            if (!string.IsNullOrEmpty(trajectoryDir))
                logger.LogInformation("Trajectory file sink: {Dir}", trajectoryDir);

            var summary = await Runner.BenchmarkRunner.RunAsync(
                profile, suite, instances, clients, model, sidecar,
                async (image, cwd, innerCt) =>
                {
                    if (string.IsNullOrEmpty(image))
                    {
                        var sb = await LocalSandbox.StartAsync(sidecar, cwd, "agent", innerCt);
                        return (sb.Rpc, sb, sb.SessionId);
                    }

                    var docker = await DockerSandbox.StartAsync(
                        image, sidecar, profile.Sandbox.RunAsRoot, ct: innerCt);
                    await docker.Rpc.SessionCreateAsync("agent", cwd, innerCt);
                    return (docker.Rpc, docker, "agent");
                },
                outputDir,
                pr.GetValue(concurrencyOpt),
                e =>
                {
                    // First: tee the event into the file/SSE sink if one is
                    // active. Logging and console output below are unchanged.
                    liveSink?.Emit(e);

                    if (e.Type == "instance_end")
                    {
                        var id = e.Data.GetValueOrDefault("instance_id");
                        var reason = e.Data.GetValueOrDefault("end_reason");
                        Console.WriteLine($"  {id} \u2192 {reason}");
                    }
                    // Surface diagnostic events. Without these, debugging a
                    // failed run means reading summary.json instead of seeing
                    // the actual error in the terminal.
                    else if (e.Type == "llm_error")
                        logger.LogError("LLM error: {Message}", e.Data.GetValueOrDefault("message"));
                    else if (e.Type == "tool_call_end" && e.Data.TryGetValue("success", out var ok) && ok is false)
                        logger.LogWarning("tool {Tool} failed: {Preview}",
                            e.Data.GetValueOrDefault("tool_name"),
                            e.Data.GetValueOrDefault("result_preview"));
                    else if (e.Type == "session_recovery")
                        logger.LogInformation("session recovered (iter {Iter}, reason {Reason})",
                            e.Data.GetValueOrDefault("iteration"),
                            e.Data.GetValueOrDefault("reason"));
                    else if (e.Type == "session_recovery_failed")
                        logger.LogError("session recovery FAILED (iter {Iter}): {Error}",
                            e.Data.GetValueOrDefault("iteration"),
                            e.Data.GetValueOrDefault("error"));
                    else if (e.Type == "patch_capture_failed")
                        logger.LogWarning("Patch capture failed for {Id}: {Error}",
                            e.Data.GetValueOrDefault("instance_id"), e.Data.GetValueOrDefault("error"));
                    else if (e.Type == "post_hook_failed")
                        logger.LogWarning("Post-process hook failed for {Id}: {Error}",
                            e.Data.GetValueOrDefault("instance_id"), e.Data.GetValueOrDefault("error"));
                },
                ct);

            Console.WriteLine($"\nvett run: {summary.InstanceCount} instances, {summary.Completed} ok, {summary.Errored} errors, {summary.DurationSec:F1}s");

            // `Errored` counts instances whose record carries an Error
            // (Runner.cs: `if (r.Error is null) Completed++ else Errored++`) —
            // a HARNESS/agent failure, NOT an unresolved benchmark instance. An
            // instance the model simply failed to solve has Error == null and
            // lands in Completed. So a legitimately low resolve rate still exits
            // 0, and only actual breakage exits 1. Conflating those two would
            // score a valid measurement as a failure.
            return summary.Errored == 0 ? 0 : 1;
        });

        return cmd;
    }
}
