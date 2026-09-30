using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;
using Vett.Agent;
using Vett.Config;
using Vett.Plugin;
using Vett.Tools;

namespace Vett.Cli;

public static class ValidateCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command("validate", "Validate workspace profiles, tools, and middleware");

        var profileOpt = new Option<string>("--profile") { Description = "Validate a specific profile (default: all in profiles/)" };
        cmd.Add(profileOpt);

        // Opt-in because it touches the network. Every call is GET /models —
        // a catalogue read, never a completion — so it costs nothing on a
        // metered provider, but a validator that silently makes outbound
        // requests is not something to turn on by default.
        var checkEndpointsOpt = new Option<bool>("--check-endpoints")
        {
            Description = "Also ask each profile's endpoints whether they are up and actually serve the named model "
                        + "(GET /models only — no completions, no token spend). Off by default: plain validate is offline.",
        };
        cmd.Add(checkEndpointsOpt);

        // 20s, not 8. Measured 2026-08-25: openrouter.ai serves a ~688KB
        // catalogue in 0.38s warm but 11.76s cold, so an 8s deadline sat BELOW
        // the natural cost of the fetch and reported a live host as dead — on
        // every run, not as a one-off. Correcting a deadline that is under the
        // operation's real duration is a ruler fix; it is not tuning a number
        // until a particular result turns green.
        var probeTimeoutOpt = new Option<int>("--endpoint-timeout") { Description = "Seconds to wait for each endpoint probe (default 20; a cold TLS fetch of a large model catalogue measured 11.8s)." };
        probeTimeoutOpt.DefaultValueFactory = _ => 20;
        cmd.Add(probeTimeoutOpt);

        // Every team node in a profile, with a readable path.
        //
        // ⚠ NOT shared with EndpointProbe.WalkTeam, deliberately, despite that
        // walker's comment warning against duplicate traversals. WalkTeam visits
        // SEATS and hands back their LlmConfig; these checks need the TEAM node
        // (to see all its members at once, which is what a duplicate-name check
        // is) and the MemberConfig itself. Different unit, not a second copy of
        // the same question.
        //
        // Bounded for the same reason CollectTeam is: YamlDotNet resolves
        // anchors/aliases, so a hand-written profile can be cyclic, and validate
        // is the command you run WHEN a profile is malformed.
        // ⚠ DELEGATES rather than reimplements. This was a byte-identical copy of
        // the walk in Yaml.TeamNodes; two copies of "which team nodes exist" let
        // `validate` and the run-time check in ValidateProfileForRun disagree
        // about the population they span, and a gate that spans a different set
        // than the thing it guards passes vacuously on the difference.
        static IEnumerable<(string Path, TeamConfig Team)> TeamsOf(TeamConfig? team, string path, int depth)
            => Yaml.TeamNodes(team, path, depth);

        cmd.SetAction((pr) =>
        {
            var errors = 0;
            var warnings = 0;
            var profileName = pr.GetValue(profileOpt);
            var checkEndpoints = pr.GetValue(checkEndpointsOpt);
            var probeTimeout = TimeSpan.FromSeconds(Math.Max(1, pr.GetValue(probeTimeoutOpt)));

            // Validate profiles.
            //
            // Discovery goes through Yaml.ResolveSearchDirs — the SAME
            // precedence the runtime uses (cwd > ~/.vett > install-dir) —
            // because a validator that reads a different set of files than
            // the thing it is validating is not measuring the thing it is
            // validating. This was cwd/profiles only, which was wrong in
            // BOTH directions at once:
            //   - `--profile X` reported "file not found" for a profile that
            //     lives in ~/.vett and runs perfectly well. A false ERROR.
            //   - the sweep never opened the ~26 profiles in ~/.vett or the
            //     install-dir snapshot, all of which `vett run --profile X`
            //     can load. A gate that covers a subset of the population it
            //     claims to cover passes VACUOUSLY over the rest.
            // `vett profiles` already enumerated this way; validate did not,
            // so the two commands disagreed about what exists.
            Console.WriteLine("Profiles:");
            var searchDirs = Yaml.ResolveSearchDirs("profiles").ToList();
            var profileFiles = new List<string>();

            // name -> (winner, losers). Only recorded when the copies DIFFER:
            // the install-dir snapshot is a build-time copy of profiles/, so
            // most shadowing is byte-identical and benign. Warning on those
            // too would bury the case that actually bites — two files, one
            // name, different contents, and which one you get depends on your
            // working directory.
            var divergent = new List<(string Name, string Winner, List<string> Losers)>();

            static string? HashOf(string path)
            {
                try
                {
                    using var s = File.OpenRead(path);
                    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
                }
                catch { return null; }
            }

            if (!string.IsNullOrEmpty(profileName))
            {
                // A literal path wins outright, matching Yaml.ResolvePath.
                var hits = File.Exists(profileName) ? [profileName] : new List<string>();
                hits.AddRange(searchDirs
                    .Select(d => Path.Combine(d, profileName + ".yaml"))
                    .Where(File.Exists));

                if (hits.Count == 0)
                {
                    Console.WriteLine($"  ✗ {profileName} — not found in any of: {string.Join(", ", searchDirs)}");
                    errors++;
                }
                else
                {
                    profileFiles.Add(hits[0]);
                    var losers = hits.Skip(1).Where(h => HashOf(h) != HashOf(hits[0])).ToList();
                    if (losers.Count > 0) divergent.Add((profileName, hits[0], losers));
                }
            }
            else
            {
                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in searchDirs)
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*.yaml").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var n = Path.GetFileNameWithoutExtension(f);
                        if (seen.TryGetValue(n, out var winner))
                        {
                            if (HashOf(f) != HashOf(winner))
                            {
                                var row = divergent.FindIndex(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase));
                                if (row >= 0) divergent[row].Losers.Add(f);
                                else divergent.Add((n, winner, [f]));
                            }
                            continue;   // first store wins — exactly what the runtime loads
                        }
                        seen[n] = f;
                        profileFiles.Add(f);
                    }
                }
            }

            // ONE session around the WHOLE sweep. Creating it inside the loop
            // is the defect this shape prevents: the connection and the
            // catalogue cache would be rebuilt per profile, so nine profiles
            // naming the same endpoint would each pay their own cold fetch and
            // some would lose the race and be reported DEAD while their
            // identical siblings were reported live. See EndpointProbe.ProbeSession.
            using var probeSession = new EndpointProbe.ProbeSession();

            var cwdProfiles = Path.Combine(Directory.GetCurrentDirectory(), "profiles");
            foreach (var file in profileFiles)
            {
                // Say which STORE each row came from. A bare profile name is
                // ambiguous across three stores, and "the run recorded only
                // the name" is how you end up unable to prove which ruler you
                // used. cwd rows stay unadorned so the common case is quiet.
                var origin = Path.GetDirectoryName(Path.GetFullPath(file));
                var name = Path.GetFileNameWithoutExtension(file)
                    + (string.Equals(origin, Path.GetFullPath(cwdProfiles), StringComparison.OrdinalIgnoreCase)
                        ? "" : $"  [{origin}]");
                if (!File.Exists(file))
                {
                    Console.WriteLine($"  \u2717 {name} — file not found");
                    errors++;
                    continue;
                }

                try
                {
                    var profile = Yaml.LoadProfile(file);

                    // Check tools exist.
                    //
                    // ⚠ `?? []` IS NOT DEFENSIVE PADDING. `Tools` is initialised
                    // to `[]`, but YAML written as a bare `tools:` (key present,
                    // value empty) deserialises to NULL and overwrites that
                    // initialiser. Measured 2026-08-26: the unguarded form threw
                    // ArgumentNullException here, which the catch at the bottom
                    // of this block reported as "parse error" — on a profile
                    // whose YAML parses perfectly and which runs fine. Same for
                    // every other `?? []` in this file.
                    var builtinTools = Builtins.All();
                    var missingTools = (profile.Tools ?? [])
                        .Where(t => !builtinTools.ContainsKey(t))
                        .ToList();

                    // Check system prompt.
                    var hasPrompt = !string.IsNullOrEmpty(profile.SystemPrompt);

                    // Check system_prompt_file exists.
                    if (!string.IsNullOrEmpty(profile.SystemPromptFile) && !hasPrompt)
                    {
                        var promptPath = Path.Combine(Path.GetDirectoryName(file) ?? ".", profile.SystemPromptFile);
                        if (!File.Exists(promptPath))
                        {
                            Console.WriteLine($"  \u2717 {name} — system_prompt_file not found: {profile.SystemPromptFile}");
                            errors++;
                            continue;
                        }
                    }

                    // Only validate ranges if explicitly set; null means "use default".
                    if (profile.Llm.Temperature is double t && (t < 0 || t > 2))
                    {
                        Console.WriteLine($"  \u2717 {name} — temperature {t} out of range [0, 2]");
                        errors++;
                        continue;
                    }

                    if (profile.Llm.TopP is double p && (p <= 0 || p > 1))
                    {
                        Console.WriteLine($"  \u2717 {name} — top_p {p} out of range (0, 1]");
                        errors++;
                        continue;
                    }

                    // llm.priority, at the root AND at every seat.
                    //
                    // ⛔ AN ERROR, NOT A WARNING. CapacityBinding.PriorityOf THROWS on
                    // an unknown word, so the run dies. A validator that were gentler
                    // than the runtime would tell the author the profile is usable
                    // when it is not — the exact shape of a gate that fails open.
                    //
                    // The message is the RUNTIME’S OWN (see BadPrioritiesOf): the
                    // legal words are never restated here, so this cannot drift out
                    // of agreement with the code that enforces them.
                    var badPriorities = EndpointProbe.BadPrioritiesOf(profile);
                    if (badPriorities.Count > 0)
                    {
                        foreach (var bp in badPriorities)
                            Console.WriteLine($"  \u2717 {name} — {bp.Path}.priority: {bp.Message}");
                        errors += badPriorities.Count;
                        continue;
                    }

                    // Check schemas exist for tools.
                    var missingSchemas = (profile.Tools ?? [])
                        .Where(t =>
                        {
                            var p = Path.Combine(AppContext.BaseDirectory, "schemas", $"{t}.json");
                            var p2 = Path.Combine(Directory.GetCurrentDirectory(), "schemas", $"{t}.json");
                            return !File.Exists(p) && !File.Exists(p2);
                        })
                        .ToList();

                    // Keys the YAML deserialiser will silently drop. This is
                    // the failure mode `validate` exists for: everything
                    // parses, everything runs green, and the setting the
                    // author wrote never reached the code.
                    var audit = ProfileKeyAudit.AuditProfileFile(file);

                    // Report.
                    if (audit.KeysOrEmpty.Count > 0)
                    {
                        foreach (var u in audit.KeysOrEmpty)
                            Console.WriteLine($"  ⚠ {name} — unrecognised key (IGNORED at load): {u}");
                        warnings += audit.KeysOrEmpty.Count;
                    }

                    // An audit that could not run must not earn a tick.
                    // `validate` exists to answer "did every setting reach the
                    // code?" - and the honest answer here is "I could not
                    // tell", which is a warning, not a pass.
                    if (!audit.Measured)
                    {
                        Console.WriteLine($"  ⚠ {name} — key audit DID NOT RUN ({audit.UnmeasuredReason}); "
                            + "this profile is UNVERIFIED, not clean");
                        warnings++;
                    }

                    if (missingTools.Count > 0)
                    {
                        Console.WriteLine($"  \u26A0 {name} — unknown tools (may be plugins): {string.Join(", ", missingTools)}");
                        warnings += missingTools.Count;
                    }

                    if (missingSchemas.Count > 0)
                    {
                        Console.WriteLine($"  \u26A0 {name} — missing schemas: {string.Join(", ", missingSchemas)}");
                        warnings += missingSchemas.Count;
                    }

                    if (!hasPrompt)
                    {
                        Console.WriteLine($"  \u26A0 {name} — no system prompt");
                        warnings++;
                    }

                    // Structural completeness \u2014 NO NETWORK, so unlike the
                    // liveness probe below it runs on every validate rather
                    // than behind --check-endpoints. "Does this profile even
                    // name an endpoint and a model?" is a question the YAML
                    // answers by itself, and a profile that answers "no"
                    // cannot start: `chat` refuses with "endpoint and model
                    // are required" before a single request is made.
                    //
                    // Measured 2026-08-26 on `coding`, the VETT Chat
                    // extension's DEFAULT profile: plain validate printed
                    // "\u2713 coding" / "0 error(s), 0 warning(s)" while the
                    // extension could not open a session with it at all. Same
                    // false-green class as the 31 suites below, one layer
                    // earlier.
                    //
                    // Warning, not error: endpoint/model can legitimately
                    // arrive from --endpoint/--model or VETT_LLM_*, which is
                    // how the benchmark profiles are driven. Withholding the
                    // tick is the fix; a manufactured failure is not.
                    var incomplete = EndpointProbe.IncompleteSeatsOf(profile);
                    foreach (var gap in incomplete)
                    {
                        Console.WriteLine($"  \u26a0 {name} [{gap.Path}] \u2014 no {gap.Missing} declared; "
                            + "cannot start without --endpoint/--model or VETT_LLM_ENDPOINT/VETT_LLM_MODEL");
                        warnings++;
                    }

                    // ⛔ UNBOUNDED GENERATION. `request_timeout_seconds` caps how
                    // long one attempt may TAKE; nothing caps how much it tries
                    // to PRODUCE. With no `max_output_tokens` the response length
                    // -- and therefore its duration -- is the model's choice, so
                    // whether the call fits inside its own timeout stops being a
                    // property of the configuration. Each retry is then a fresh
                    // unbounded generation, and the whole retry budget can be
                    // spent on a request that never had room to finish.
                    //
                    // Warning, not error, and NOT because the hazard is small: an
                    // unbounded seat is the right choice for plenty of short
                    // single-shot profiles. What the YAML cannot see is whether
                    // this profile drives long agentic turns. So the verdict
                    // carries the exposure -- timeout and retry count -- instead
                    // of asserting a number this file has no way to know.
                    foreach (var seat in EndpointProbe.UnboundedOutputSeatsOf(profile))
                    {
                        Console.WriteLine($"  \u26a0 {name} [{seat.Path}] \u2014 no `max_output_tokens`, so one "
                            + "generation has NO ceiling while each attempt is cut off at "
                            + $"{seat.RequestTimeoutSeconds}s and retried {seat.NumRetries} time(s). "
                            + "A response the model decides to make long cannot finish, and every retry "
                            + "restarts the same unbounded generation. Set `max_output_tokens` unless this "
                            + "seat is deliberately one short reply.");
                        warnings++;
                    }

                    // ⛔ THE RUN CAP AND THE RETRY BUDGET ARE A JOINT CLAIM. Each
                    // key is sensible alone; together they can contradict. A run
                    // capped shorter than ONE call's retry stack dies part-way
                    // through a stack it was configured to complete, and the
                    // symptom is a run that simply stops -- which reads as a hang,
                    // not as a configuration error.
                    foreach (var gap in EndpointProbe.RunCapShorterThanRetryStackOf(profile))
                    {
                        Console.WriteLine($"  \u26a0 {name} [{gap.Path}] \u2014 `timeout_minutes` gives the whole "
                            + $"run {gap.RunCapSeconds}s, but ONE call may spend {gap.RetryBudgetSeconds}s retrying "
                            + "((num_retries + 1) x request_timeout_seconds). The run will be killed mid-retry "
                            + "and will look like a hang. Raise `timeout_minutes`, or lower "
                            + "`request_timeout_seconds` / `num_retries`.");
                        warnings++;
                    }

                    // ⭐ TOPOLOGY. Can this profile's team shape actually RUN?
                    //
                    // NO NETWORK and no model — nesting depth is declared in
                    // the YAML, so it is knowable offline. Measured 2026-08-26:
                    // a profile declaring five agent levels validated ✓ with
                    // "0 error(s), 0 warning(s)" and rc=0, then would have
                    // thrown mid-dispatch after paying for every token spent
                    // getting down to the level that gets refused.
                    //
                    // ERROR, not warning — and the distinction is not cosmetic.
                    // The incomplete-seat check above warns because --endpoint
                    // /--model or VETT_LLM_* can still supply what's missing at
                    // launch. NOTHING can rescue an over-deep topology: the cap
                    // is a compile-time constant, so the run is already decided
                    // to fail. Withholding the tick is not enough when the
                    // honest verdict is "this cannot work".
                    var declaredDepth = TeamCoordinator.DeclaredNestingDepth(profile.Team);
                    var depthOk = declaredDepth <= TeamCoordinator.MaxDispatchDepth;
                    if (!depthOk)
                    {
                        Console.WriteLine($"  ✗ {name} — team nests {declaredDepth} level(s) deep, "
                            + $"but the dispatch cap is {TeamCoordinator.MaxDispatchDepth} "
                            + $"(agent chain at most {TeamCoordinator.MaxDispatchDepth + 2} long: "
                            + "manager, lead, worker, sub-worker). This profile would throw mid-run.");
                        errors++;
                    }

                    // ⭐ SEAT-LEVEL CHECKS THE TOP-LEVEL SWEEP SKIPS.
                    //
                    // Everything above this point inspects `profile.Tools` —
                    // the LEADER's list. Members carry their own `tools:`, and
                    // nothing looked at them, so the profile-summary defect
                    // ProfileSummaryBindingTests exists for ("a TEAM profile is
                    // summarized by its leader alone") had an exact twin here in
                    // the validator.
                    var dupOk = true;
                    var memberToolGaps = 0;
                    foreach (var (tpath, node) in TeamsOf(profile.Team, "team", 0))
                    {
                        // ⛔ DUPLICATE MEMBER NAMES ARE A GUARANTEED CRASH, not a
                        // style problem. Coordinator builds `team.Members
                        // .ToDictionary(m => m.Name)` at two sites, and
                        // ToDictionary THROWS on a repeated key — so the run dies
                        // at start-up, every time, before any work happens.
                        // Statically decidable, and validate said ✓.
                        // Ordinal grouping to match ToDictionary's default
                        // string comparer exactly; a looser comparer here would
                        // report a collision the runtime does not actually have.
                        foreach (var g in (node.Members ?? [])
                                     .Where(m => m is not null)
                                     .GroupBy(m => m.Name, StringComparer.Ordinal)
                                     .Where(g => g.Count() > 1))
                        {
                            var shown = string.IsNullOrEmpty(g.Key) ? "(unnamed)" : g.Key;
                            Console.WriteLine($"  ✗ {name} [{tpath}] — {g.Count()} members share the name "
                                + $"'{shown}'; member names are dictionary keys at dispatch, so this "
                                + "profile throws at start-up.");
                            errors++;
                            dupOk = false;
                        }

                        // ⛔ A NULL NAME THROWS EVEN WITHOUT A DUPLICATE. The loop
                        // above only fires at count > 1, but Dictionary rejects a
                        // NULL key outright — so ONE member written as a bare
                        // `name:` is the same guaranteed start-up crash with a
                        // count of one. Measured 2026-08-26: that profile got a
                        // clean ✓ and rc=0 from the duplicate check alone.
                        // An EMPTY name is deliberately NOT an error here: "" is a
                        // perfectly legal dictionary key, so it only breaks when
                        // it collides — which is the loop above's job.
                        var nullNamed = (node.Members ?? []).Count(m => m is not null && m.Name is null);
                        if (nullNamed > 0)
                        {
                            Console.WriteLine($"  ✗ {name} [{tpath}] — {nullNamed} member(s) have no "
                                + "`name:` value; member names are dictionary keys at dispatch and a "
                                + "null key throws, so this profile throws at start-up.");
                            errors++;
                            dupOk = false;
                        }

                        // ⛔ A NEGATIVE CEILING SILENTLY MEANS "UNLIMITED" — the
                        // exact opposite of what writing one down means. The
                        // guard at the dispatch site is `> 0`, so -1 fails it
                        // and every dispatch is waved through. Nothing at
                        // runtime can report this: an uncapped run looks
                        // identical to a run whose author never asked for a cap.
                        // Statically decidable, so it belongs here, and it is
                        // the same false-green shape as the null `name:` above.
                        // ⭐ ABSENT IS NOW AN ERROR, NOT A DEFAULT (Mark, 2026-08-27).
                        // Measured the same day: 0 of 12 team profiles set this key,
                        // so "0 = unlimited by default" was not protecting a subset —
                        // it left every team node in the repo uncapped. Absence and an
                        // explicit 0 are only distinguishable because the property is
                        // `int?`; see the doc on TeamConfig.MaxConcurrentDispatches.
                        if (node.MaxConcurrentDispatches is null)
                        {
                            Console.WriteLine($"  ✗ {name} [{tpath}] — `max_concurrent_dispatches` is "
                                + "REQUIRED on every team node and is absent here. An omitted ceiling "
                                + "means UNLIMITED, and nothing at runtime reports an uncapped run. Add "
                                + "`max_concurrent_dispatches: <n>` (one slot per member plus one is a "
                                + "reasonable start), or `0` to be deliberately uncapped.");
                            errors++;
                            dupOk = false;
                        }
                        else if (node.MaxConcurrentDispatches < 0)
                        {
                            Console.WriteLine($"  ✗ {name} [{tpath}] — `max_concurrent_dispatches: "
                                + $"{node.MaxConcurrentDispatches}` is negative, which reads as UNLIMITED "
                                + "at dispatch — the opposite of a cap. Use a positive number for a "
                                + "ceiling, or 0 to mean unlimited deliberately.");
                            errors++;
                            dupOk = false;
                        }
                        else if (node.MaxConcurrentDispatches == 0)
                        {
                            // ⚠ WARN, NOT ERROR — and the distinction is the whole
                            // point of requiring the key. `0` is a legal, deliberate
                            // "this leader is uncapped"; what is banned is arriving
                            // there by SILENCE. Warning keeps the decision visible in
                            // the output without overriding an author who meant it.
                            Console.WriteLine($"  ⚠ {name} [{tpath}] — `max_concurrent_dispatches: 0` "
                                + "means UNLIMITED fan-out for this leader. That is legal and explicit, "
                                + "so it is not an error — but nothing will stop this node from "
                                + "dispatching as wide as the model chooses.");
                            warnings++;
                        }

                        // ⛔ `leader: tools:` PARSES AND IS THEN DISCARDED.
                        //
                        // The leader is a MemberConfig (Profile.cs:348), so it
                        // carries a `Tools` list and any value written there is
                        // schema-valid. But the Coordinator filters tools ONLY for
                        // members — three filter blocks (Coordinator.cs 726/735/741,
                        // 1440/1445/1450, 1983/1988/1994) plus the inheritance at
                        // Coordinator.cs:453, all keyed on `m.Tools.Count > 0` — and
                        // there is no leader equivalent at any of them. `Leader.Tools`
                        // has ZERO reads anywhere in src/ outside this warning
                        // (re-swept 2026-08-28). The leader always receives the full
                        // profile toolset.
                        //
                        // This is a WARNING, not an error, and deliberately NOT
                        // honoured: the leader's tool map is where assign_task,
                        // accept_dispatch and reject_dispatch are injected, so
                        // enforcing a hand-written list would strip the coordination
                        // surface and turn a silent no-op into a dead team. Telling
                        // the author it does nothing is the safe half of the fix; the
                        // unsafe half needs a design decision about which tools are
                        // structural and cannot be filtered out.
                        //
                        // No shipped profile sets it (checked across both stores), so
                        // this fires on nothing today and exists to catch the next
                        // author who reasonably assumes symmetry with members.
                        if (node.Leader.Tools.Count > 0)
                        {
                            Console.WriteLine($"  ⚠ {name} [{tpath}] — `leader.tools` is set "
                                + $"({node.Leader.Tools.Count} entr{(node.Leader.Tools.Count == 1 ? "y" : "ies")}) "
                                + "but is NEVER READ. Tool filtering is implemented for members only; the "
                                + "leader always gets the full profile toolset plus the injected "
                                + "coordination tools. Remove the key, or move the restriction to the "
                                + "members that should carry it — leaving it in place reads as a "
                                + "restriction that is not in force.");
                            warnings++;
                        }

                        // ⛔ BEST-OF WITHOUT WORKTREES IS NOT BEST-OF — IT IS NOTHING.
                        // `assign_best_of` is registered INSIDE the dispatchManager
                        // branch (Coordinator, "BEST-OF-N: needs worktrees"), so with
                        // `dispatch_worktree` off the tool is never registered at all.
                        // The leader is handed a prompt describing best-of and a
                        // toolset that does not contain it.
                        //
                        // ⚠ `dispatch_worktree` DEFAULTS TO FALSE — this is a bare
                        // `bool` with no initialiser. So a profile that declares
                        // `best_of` and simply never mentions `dispatch_worktree`
                        // lands here. That is the DEFAULT path, not an opt-out, which
                        // is why this is an error and not a warning.
                        //
                        // Found 2026-08-27 while proving the runtime exemption: the
                        // first draft of BestOfCeilingExemptionTests ran with
                        // worktrees off and measured ZERO candidates.
                        if (node.BestOf is not null && !node.DispatchWorktree)
                        {
                            Console.WriteLine($"  ✗ {name} [{tpath}] — `best_of` is declared but "
                                + "`dispatch_worktree` is false (the default when the key is absent), "
                                + "so `assign_best_of` is never registered and best-of silently "
                                + "does nothing. Add "
                                + "`dispatch_worktree: true`, or remove `best_of`.");
                            errors++;
                            dupOk = false;
                        }

                        // ⚠ TWO AUTHOR-DECLARED WIDTHS THAT DISAGREE. Best-of is
                        // exempt from the ceiling by design (its `n` is the
                        // author's, not the leader's — see Coordinator's
                        // RunOneCandidate), so a profile asking for best-of-5
                        // under a ceiling of 2 gets FIVE concurrent candidates,
                        // not two. That is the intended behaviour and not an
                        // error, but an author who wrote both numbers deserves
                        // to be told they do not mean what they look like.
                        //
                        // ⛔ GATED ON `DispatchWorktree` TOO, and that conjunct is NOT
                        // redundant. Without it this warning fires in the exact state
                        // the error above just declared, printing two contradictory
                        // sentences about ONE node on consecutive lines:
                        //     ✗ … best-of silently does nothing
                        //     ⚠ … so this profile can run 5 candidates at once.
                        // The warning would be false precisely when the error is true:
                        // the real answer there is ZERO candidates, not five.
                        // Found 2026-08-27 by an adversarial audit OF THIS REPAIR —
                        // the error above shipped its own neighbouring defect.
                        if (node.BestOf is not null && node.DispatchWorktree
                            && node.MaxConcurrentDispatches > 0
                            && node.BestOf.N > node.MaxConcurrentDispatches)
                        {
                            Console.WriteLine($"  ⚠ {name} [{tpath}] — `best_of.n` is {node.BestOf.N} but "
                                + $"`max_concurrent_dispatches` is {node.MaxConcurrentDispatches}. Best-of "
                                + "is EXEMPT from the ceiling (its width is yours, not the leader's), so "
                                + $"this profile can run {node.BestOf.N} candidates at once.");
                            warnings++;
                        }

                        foreach (var m in node.Members ?? [])
                        {
                            if (m is null) continue;
                            var unknown = (m.Tools ?? []).Where(x => !builtinTools.ContainsKey(x)).ToList();
                            if (unknown.Count == 0) continue;
                            var who = string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name;
                            Console.WriteLine($"  ⚠ {name} [{tpath}/{who}] — unknown tools "
                                + $"(may be plugins): {string.Join(", ", unknown)}");
                            warnings += unknown.Count;
                            memberToolGaps += unknown.Count;
                        }
                    }

                    // Liveness. A profile whose seats cannot reach a server
                    // that serves their model is BROKEN, however clean its
                    // shape is \u2014 that is the exact state 31 suites sat in
                    // for weeks while validate reported them green.
                    var probeClean = true;
                    // ⛔ "nothing to probe" is a THIRD state, not a pass. Kept
                    // separate from probeClean because an unprobed profile is
                    // neither live nor dead — and a tick reading "(endpoints
                    // live)" over a profile nothing asked about is precisely
                    // the false green F7 removed from the key audit.
                    var probeUnchecked = false;
                    if (checkEndpoints)
                    {
                        var targets = EndpointProbe.TargetsOf(profile);
                        var verdicts = EndpointProbe
                            .ProbeAsync(probeSession, targets, probeTimeout)
                            .GetAwaiter().GetResult();

                        foreach (var v in verdicts.Where(v => v.Level != ProbeLevel.Ok))
                        {
                            var glyph = v.Level == ProbeLevel.Error ? "\u2717" : "\u26a0";
                            Console.WriteLine($"  {glyph} {name} [{v.Path}] \u2014 {v.Message}");
                            if (v.Level == ProbeLevel.Error) { errors++; probeClean = false; }
                            else warnings++;
                        }

                        // Say so out loud when a profile declares no endpoint
                        // at all: "nothing to probe" must not read as "probed
                        // and fine".
                        if (targets.Count == 0)
                        {
                            Console.WriteLine($"  \u26a0 {name} \u2014 no endpoint declared; liveness UNCHECKED, not clean");
                            warnings++;
                            probeUnchecked = true;
                        }
                    }

                    if (missingTools.Count == 0 && missingSchemas.Count == 0 && hasPrompt
                        && audit.Measured && audit.Keys!.Count == 0 && probeClean && !probeUnchecked
                        && incomplete.Count == 0 && depthOk && dupOk && memberToolGaps == 0)
                        Console.WriteLine($"  \u2713 {name}{(checkEndpoints ? " (endpoints live)" : "")}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  \u2717 {name} — parse error: {ex.Message}");
                    errors++;
                }
            }

            // Publish how many distinct catalogues were actually fetched. This
            // is the number that gives the sharing away: when the cache was
            // rebuilt per profile it silently equalled the profile count, and
            // nothing on screen said so. A reader who sees it approach the
            // profile count is looking at a broken instrument, not a big store.
            if (checkEndpoints)
            {
                Console.WriteLine($"\n{probeSession.Fetches} distinct endpoint(s) probed "
                    + $"across {profileFiles.Count} profile(s).");
            }

            // One name, several stores, DIFFERENT bytes. The winner is what
            // runs, and which file wins depends on the working directory — so
            // the same `--profile X` is two different rulers from two
            // different shells. Identical copies are not reported; only a
            // genuine divergence is.
            foreach (var (dn, winner, losers) in divergent)
            {
                Console.WriteLine($"  ⚠ {dn} — DIVERGENT copies in {losers.Count + 1} stores; "
                    + $"runtime loads {winner}");
                foreach (var l in losers)
                    Console.WriteLine($"      shadowed (different bytes): {l}");
                warnings++;
            }

            // Validate tools.
            Console.WriteLine("\nTools:");
            var plugins = Scanner.Scan(Directory.GetCurrentDirectory());
            var toolPlugins = plugins.Where(p => p.Kind == "tool").ToList();

            if (toolPlugins.Count == 0)
            {
                Console.WriteLine("  (none found in tools/)");
            }
            else
            {
                foreach (var p in toolPlugins)
                {
                    var sdkOk = p.Language == Lang.Yaml || Builder.SdkAvailable(p.Language switch
                    {
                        Lang.Go => "go",
                        Lang.Python => OperatingSystem.IsWindows() ? "python" : "python3",
                        Lang.CSharp => "dotnet",
                        Lang.TypeScript => "node",
                        Lang.Rust => "cargo",
                        Lang.Ruby => "ruby",
                        _ => "",
                    });

                    if (!sdkOk)
                    {
                        Console.WriteLine($"  \u26A0 {p.Name} [{p.Language}] — SDK not available");
                        warnings++;
                    }
                    else
                    {
                        Console.WriteLine($"  \u2713 {p.Name} [{p.Language}]");
                    }
                }
            }

            // Validate schemas.
            Console.WriteLine("\nSchemas:");
            var schemaDir = Path.Combine(Directory.GetCurrentDirectory(), "schemas");
            if (Directory.Exists(schemaDir))
            {
                foreach (var f in Directory.GetFiles(schemaDir, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    try
                    {
                        var doc = JsonDocument.Parse(File.ReadAllText(f));
                        var fn = doc.RootElement.GetProperty("function").GetProperty("name").GetString();
                        Console.WriteLine($"  \u2713 {name} (function: {fn})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  \u2717 {name} — invalid JSON: {ex.Message}");
                        errors++;
                    }
                }
            }
            else
            {
                Console.WriteLine("  (none found in schemas/)");
            }

            Console.WriteLine($"\n{errors} error(s), {warnings} warning(s)");

            // THE POINT OF A VALIDATOR IS ITS EXIT CODE. This counted errors
            // carefully, printed them, and then threw the number away: the
            // action returned void, so `vett validate` exited 0 on a missing
            // profile, an unparseable YAML, a broken schema — every failure it
            // is capable of detecting. Anything scripting it (a pre-run check,
            // a CI step, a wrapper) read success. Verified empirically before
            // the fix: `--profile vett-no-such-profile-xyz` printed
            // "1 error(s), 0 warning(s)" and returned rc=0.
            //
            // 1, not 2: errors here mean "ran and found problems". 2 is
            // reserved for "could not run at all", per the convention the run
            // commands use. Warnings deliberately do NOT fail — they cover
            // "unverified" and "unknown tool, may be a plugin", which are not
            // defects on their own.
            return errors > 0 ? 1 : 0;
        });

        return cmd;
    }
}
