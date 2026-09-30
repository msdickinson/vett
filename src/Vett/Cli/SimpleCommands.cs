using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vett.Config;
using Vett.Plugin;

namespace Vett.Cli;

public static class SimpleCommands
{
    public static Command Version()
    {
        var cmd = new Command("version", "Print version");
        cmd.SetAction(_ => Console.WriteLine($"vett {Helpers.Version} (C#/.NET {Environment.Version})"));
        return cmd;
    }

    public static Command Init(ILogger logger)
    {
        var cmd = new Command("init", "Scaffold a workspace");
        cmd.SetAction(_ =>
        {
            var dirs = new[]
            {
                "profiles", "suites", "tools", "middlewares", "results", "schemas",
                "pre-process/suite", "pre-process/instance",
                "post-process/suite", "post-process/instance",
            };
            foreach (var d in dirs)
            {
                Directory.CreateDirectory(d);
                logger.LogInformation("  {Dir}/", d);
            }
            logger.LogInformation("Workspace initialized");
        });
        return cmd;
    }

    public static Command Install(ILogger logger)
    {
        var installCmd = new Command("install", "Install bundled defaults");
        var defaultsCmd = new Command("defaults", "Drop bundled profiles, tools, schemas into workspace");
        defaultsCmd.SetAction(_ =>
        {
            var src = Path.Combine(AppContext.BaseDirectory, "defaults");
            if (!Directory.Exists(src)) { logger.LogError("No bundled defaults found"); return; }
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, f);
                var dest = Path.Combine(Directory.GetCurrentDirectory(), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) { File.Copy(f, dest); logger.LogInformation("  {File}", rel); }
            }
            logger.LogInformation("Defaults installed");
        });
        installCmd.Add(defaultsCmd);
        return installCmd;
    }

    public static Command Build(ILogger logger)
    {
        var cmd = new Command("build", "Build workspace plugins");
        cmd.SetAction(_ =>
        {
            var pool = new PluginPool(Directory.GetCurrentDirectory());
            foreach (var r in pool.Build())
            {
                if (r.Error is not null) logger.LogError("  {Name} [{Lang}] {Error}", r.Name, r.Language, r.Error.Message);
                else if (r.Skipped is not null) logger.LogWarning("  {Name} [{Lang}] skipped: {Reason}", r.Name, r.Language, r.Skipped);
                else if (r.Cached) logger.LogInformation("  {Name} [{Lang}] cached", r.Name, r.Language);
                else logger.LogInformation("  {Name} [{Lang}] built", r.Name, r.Language);
            }
        });
        return cmd;
    }

    public static Command Doctor(ILogger logger)
    {
        var cmd = new Command("doctor", "Check SDK availability");
        cmd.SetAction(_ =>
        {
            logger.LogInformation("vett doctor");
            // Per-tool version flag — Go uses bare "version" (no double-dash);
            // everything else takes "--version".
            var checks = new (string Name, string Cmd, string Flag, string Hint)[]
            {
                ("Docker", "docker", "--version", "Install Docker Desktop"),
                ("Go",     "go",     "version",   "winget install GoLang.Go"),
                ("Python", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3", "--version", "winget install Python.Python.3.12"),
                (".NET",   "dotnet", "--version", "winget install Microsoft.DotNet.SDK.10"),
                ("Node",   "node",   "--version", "winget install OpenJS.NodeJS"),
                ("Rust",   "cargo",  "--version", "curl https://sh.rustup.rs -sSf | sh"),
                ("Ruby",   "ruby",   "--version", "winget install RubyInstallerTeam.Ruby"),
            };
            foreach (var (name, cmd2, flag, hint) in checks)
            {
                try
                {
                    var psi = new ProcessStartInfo(cmd2, flag)
                    {
                        // Redirect stderr too — otherwise tools that print warnings
                        // to stderr (e.g. wrong-flag usage) leak past our logger.
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = Process.Start(psi);
                    if (p is null) { logger.LogWarning("  {Name,-10} not found ({Hint})", name, hint); continue; }

                    if (!p.WaitForExit(5000))
                    {
                        // Process hung — kill it so we don't leak it.
                        try { p.Kill(); } catch { }
                        logger.LogWarning("  {Name,-10} timed out ({Hint})", name, hint);
                        continue;
                    }

                    if (p.ExitCode == 0) logger.LogInformation("  {Name,-10} {Version}", name, p.StandardOutput.ReadToEnd().Trim());
                    else logger.LogWarning("  {Name,-10} not found ({Hint})", name, hint);
                }
                catch { logger.LogWarning("  {Name,-10} not found ({Hint})", name, hint); }
            }
        });
        return cmd;
    }

    public static Command Profiles(ILogger logger)
    {
        var cmd = new Command("profiles", "List available profiles");
        var jsonOpt = new Option<bool>("--json", "-j") { Description = "Emit a machine-readable JSON array on stdout (one object per profile)." };
        cmd.Add(jsonOpt);
        cmd.SetAction(parseResult =>
        {
            var asJson = parseResult.GetValue(jsonOpt);
            // Match Yaml.Resolve precedence: workspace wins over bundled.
            // Dedupe so a profile present in both dirs is only listed once.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<ProfileSummary>();
            // Use the same precedence as Yaml.Resolve so the listing
            // matches what the runtime will actually load.
            foreach (var d in Yaml.ResolveSearchDirs("profiles"))
            {
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d, "*.yaml"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (!seen.Add(name)) continue;
                    if (!asJson)
                    {
                        logger.LogInformation("  {Profile}", name);
                        continue;
                    }
                    rows.Add(BuildSummary(name, f));
                }
            }
            if (asJson)
            {
                // stdout-only so the JSON is easy to parse from a wrapper
                // (the VS Code extension reads this via spawn).
                Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false }));
            }
        });
        return cmd;
    }

    /// <summary>
    /// Read just enough of a profile YAML to summarize it for the picker.
    /// We deliberately don't run ValidateProfileForRun — listing should
    /// never throw on a half-configured profile, just degrade to "no
    /// description, unknown model".
    /// </summary>
    internal static ProfileSummary BuildSummary(string name, string path)
    {
        try
        {
            var p = Yaml.LoadProfile(path);

            // ⭐ EVERY binding the profile references, not just the top-level
            // pair — see the note on ProfileSummary.endpoints.
            var endpoints = new List<string>();
            var models = new List<string>();
            Collect(p.Llm, endpoints, models);
            CollectTeam(p.Team, endpoints, models, 0);

            // The ROSTER a picker needs to offer "how many of each role?".
            var team = SummarizeTeam(p.Team);

            return new ProfileSummary(
                name,
                Trim(p.Description),
                p.Llm.Model,
                p.Llm.Endpoint,
                p.Llm.Provider,
                p.MaxIterations,
                p.Tools,
                p.Sandbox.Type,
                endpoints,
                models,
                false,
                team
            );
        }
        catch
        {
            // ⛔ COULD-NOT-MEASURE IS NOT MEASURED-EMPTY. This used to return a
            // blank summary, which is byte-identical to a profile that parsed
            // fine and simply declares no model — so an unparseable profile
            // appeared in the picker looking merely under-configured. The flag
            // lets a caller tell "I failed to read this" from "I read this and
            // it says nothing."
            return new ProfileSummary(name, "", "", "", "", 0, new(), "", new(), new(), true);
        }
    }

    /// <summary>
    /// Add one LLM block's endpoint/model to the accumulating sets, deduped and
    /// in first-seen order (so the top-level binding stays first, which is what
    /// a picker shows as the headline).
    /// </summary>
    private static void Collect(LlmConfig? llm, List<string> endpoints, List<string> models)
    {
        if (llm is null) return;
        if (!string.IsNullOrWhiteSpace(llm.Endpoint)
            && !endpoints.Contains(llm.Endpoint, StringComparer.OrdinalIgnoreCase))
            endpoints.Add(llm.Endpoint);
        if (!string.IsNullOrWhiteSpace(llm.Model)
            && !models.Contains(llm.Model, StringComparer.OrdinalIgnoreCase))
            models.Add(llm.Model);
    }

    /// <summary>
    /// Walk a team's leader and members, recursing into nested sub-teams
    /// (<see cref="MemberConfig.Team"/>, the tier-3 topology).
    ///
    /// ⛔ THE DEPTH CAP IS NOT DECORATION. YamlDotNet resolves anchors/aliases,
    /// so a hand-written profile using `&amp;a`/`*a` can produce a CYCLIC object
    /// graph — and `vett profiles` is exactly the command you run when a profile
    /// is malformed. An uncapped walk would hang the picker instead of listing.
    /// The cap sits above TeamCoordinator.MaxDispatchDepth so it can never
    /// truncate a topology that is actually runnable. ⚠ Do not restate that
    /// constant's value here — it was lowered from 5 to 2 on 2026-08-26, and a
    /// number copied into a comment is a number that goes stale silently. What
    /// matters is only the INEQUALITY: this walk's cap must stay strictly
    /// greater than MaxDispatchDepth.
    /// </summary>
    /// <summary>
    /// Depth bound for the summary walk. Named rather than inline so the
    /// inequality documented on <see cref="CollectTeam"/> can be ASSERTED —
    /// an invariant with no failure test is only a comment.
    /// </summary>
    internal const int ProfileWalkDepthCap = 8;

    private static void CollectTeam(TeamConfig? team, List<string> endpoints, List<string> models, int depth)
    {
        if (team is null || depth > ProfileWalkDepthCap) return;
        Collect(team.Leader?.Llm, endpoints, models);
        CollectTeam(team.Leader?.Team, endpoints, models, depth + 1);
        foreach (var m in team.Members)
        {
            Collect(m.Llm, endpoints, models);
            CollectTeam(m.Team, endpoints, models, depth + 1);
        }
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // Profile descriptions are often multi-paragraph blocks. Picker UIs
        // want a one-liner; collapse to the first non-blank line.
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }
        return "";
    }


    /// <summary>
    /// The ROSTER a picker needs to offer "how many of each role?" — the
    /// leader's name, the fan-out width, and every top-level member with the
    /// number of seats it currently has.
    ///
    /// ⭐ WHY THE NAMES COME FROM HERE AND NOWHERE ELSE. `--team` resolves each
    /// requested role against `team.Members.Select(m => m.Name)`
    /// (TeamOverride.cs:132) and treats an unknown name as an ERROR, not as a
    /// smaller team — deliberately, so a typo cannot silently produce a run
    /// whose denominator nobody can name. A UI that offered any OTHER list —
    /// re-parsed from the YAML, or remembered from an older build — could
    /// therefore compose a command that fails at the worst moment: after the
    /// user has finished configuring a run. Publishing the same list the
    /// resolver reads is what makes an invalid request unrepresentable.
    ///
    /// ⛔ TOP-LEVEL MEMBERS ONLY, and that is not an oversight. `--team`
    /// resizes exactly this list. In the tier-3 manager topology each feature
    /// lead owns its own implementer/reviewer pair, and those seats are NOT
    /// reachable by the override — advertising them would promise a control
    /// that does not exist.
    ///
    /// ⛔ `maxConcurrentDispatches` STAYS NULLABLE. An explicit `0` means
    /// UNLIMITED and is a legal, deliberate choice; ABSENT means the author
    /// never decided, which `validate` reports as an error and the runtime
    /// throws on. Those are different states, and flattening null to 0 would
    /// report "unlimited" for a profile that is in fact misconfigured.
    /// </summary>
    private static TeamSummary? SummarizeTeam(TeamConfig? team)
    {
        if (team is null) return null;

        // First-seen order, case-insensitive grouping — the same comparer the
        // resolver uses, so what is displayed groups exactly how it resolves.
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in team.Members ?? [])
        {
            if (string.IsNullOrWhiteSpace(m.Name)) continue;
            if (!counts.TryGetValue(m.Name, out var n))
            {
                order.Add(m.Name);
                n = 0;
            }
            counts[m.Name] = n + 1;
        }

        return new TeamSummary(
            team.Leader?.Name ?? "",
            team.MaxConcurrentDispatches,
            order.Select(r => new TeamRole(r, counts[r])).ToList());
    }

    /// <param name="model">The TOP-LEVEL model only — kept as-is for
    /// backwards compatibility with existing consumers.</param>
    /// <param name="endpoint">The TOP-LEVEL endpoint only, likewise.</param>
    /// <param name="endpoints">
    /// ⭐ EVERY endpoint the profile references — top level, team leader, every
    /// member, and any nested sub-team — deduped, top-level first.
    ///
    /// WHY THIS EXISTS. `model`/`endpoint` describe the top-level LLM block and
    /// nothing else, so a TEAM profile is summarized by its leader alone. That
    /// is not a rounding error, it is the wrong answer: a profile whose leader
    /// is a healthy cloud endpoint and whose five members all point at a host
    /// that no longer exists renders, in a picker, as perfectly healthy. The
    /// breakage can sit hundreds of lines below the binding being displayed.
    ///
    /// This does NOT judge whether a binding works — no reachability probe, no
    /// list of known-dead addresses. Those are environment-specific and belong
    /// in the operator's own gate, not compiled into a shipped tool. It reports
    /// only what the profile actually references, which is the part a summary
    /// was silently dropping.
    /// </param>
    /// <param name="models">The same set, for models.</param>
    /// <param name="parseError">
    /// True when the YAML could not be read at all. Distinguishes an
    /// unparseable profile from one that parsed and declares nothing — those
    /// were previously identical on the wire.
    /// </param>

    /// <summary>One role on a team roster, and how many seats it holds.</summary>
    /// <param name="name">
    /// EXACTLY as `--team` expects it. Case is preserved for display; the
    /// resolver compares case-insensitively.
    /// </param>
    /// <param name="count">
    /// Seats this role has in the profile AS WRITTEN — the value a picker should
    /// pre-fill, so "customize" opens on the profile's own shape rather than on
    /// a guess, and an untouched field round-trips to the same team.
    /// </param>
    internal sealed record TeamRole(string name, int count);

    /// <summary>
    /// A team profile's shape. NULL for a solo profile — absent, not empty: a
    /// solo profile has no roster to resize, which is a different statement
    /// from a team that happens to declare no members.
    /// </summary>
    internal sealed record TeamSummary(
        string leader,
        int? maxConcurrentDispatches,
        List<TeamRole> members);

    internal sealed record ProfileSummary(
        string name,
        string description,
        string model,
        string endpoint,
        string provider,
        int maxIterations,
        List<string> tools,
        string sandboxType,
        List<string> endpoints,
        List<string> models,
        bool parseError,
        TeamSummary? team = null);
}
