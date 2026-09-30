using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// COMPACTION WIRING GATE.
///
/// THE DEFECT THIS PINS. A team member that declares no <c>middleware:</c> block
/// does not inherit the profile's — it falls back to
/// <c>Builtins.DefaultMiddleware()</c>, which contains NO condenser
/// (MiddlewareResolver.ResolveOrDefault, reached from Coordinator.cs:347/965/1444).
/// So a profile with a perfectly good top-level <c>compaction:</c> block can still
/// run members whose context grows without bound until the model refuses. The
/// config is read, parsed, and then simply not applied to them — which reads
/// exactly like config that works.
///
/// ⭐ WHY THIS FILE WAS RESTRUCTURED (2026-08-26). It used to be
/// DeepseekProfileCompactionTests, and every test named one shipped profile:
/// deepseek-team, deepseek-team-mixed, deepseek-team-longrun,
/// deepseek-dotnet-unity. When those four were archived in the 49 → 12 profile
/// prune, all four tests failed — not because the ENGINE regressed, but because
/// their subjects had been retired. A test coupled to one corpus filename cannot
/// tell "the behaviour broke" from "the file moved", and the temptation at that
/// point is to delete the red, which deletes the instrument with it.
///
/// So the coverage is now split by what each assertion is actually about:
///
///   • CORPUS tests — "the profiles we ship are wired correctly" — repointed at
///     the live ds-* six, and generalised to SPAN them rather than naming one.
///   • ENGINE tests — "the override mechanism works at all" — moved onto inline
///     fixtures. Two of the old tests pinned capabilities (a member-level
///     compaction override, and replan_checkpoint) that NO live profile currently
///     exercises. Against a fixture they keep testing the engine; against the
///     corpus they could only have been deleted.
/// </summary>
public class ProfileCompactionTests
{
    private static Profile Load(string name)
    {
        var dir = TestRepo.ProfilesDir();
        Assert.True(dir is not null,
            "could not locate the repo profiles/ directory from " + AppContext.BaseDirectory);

        var path = Path.Combine(dir!, name + ".yaml");
        Assert.True(File.Exists(path),
            $"'{name}' is not in the shipped corpus. If it was deliberately retired, move this "
          + "assertion to another live profile — do NOT delete it, or the wiring it pins stops "
          + "being checked anywhere.");

        return Yaml.LoadProfile(path);
    }

    private const string Condenser = "llm_summarizing_condenser";

    /// <summary>
    /// ⛔ ENUMERATED FROM MiddlewareResolver, NOT GUESSED. The first draft of this
    /// gate checked for `llm_summarizing_condenser` and `replan_checkpoint` — two
    /// names picked from memory — and promptly reported `coding-team-v2` as having
    /// "no compaction at all". It has `milestone_checkpoint`, which is
    /// CompactionMiddleware.MilestoneCheckpoint (MiddlewareResolver.cs:53) and
    /// compacts perfectly well. A predicate over a guessed vocabulary returns a
    /// confident wrong answer, and it reads exactly like a real finding.
    ///
    /// If a fourth strategy is added to MiddlewareResolver, it belongs here too.
    /// </summary>
    private static readonly string[] CompactionStrategies =
        ["llm_summarizing_condenser", "milestone_checkpoint", "replan_checkpoint"];

    /// <summary>
    /// The strategies whose trigger is <c>compaction.threshold_tokens</c>, which
    /// DEFAULTS TO 30_000 when the block is omitted (CompactionConfig, Profile.cs).
    /// `replan_checkpoint` is excluded: it reads its own
    /// <c>replan_threshold_tokens</c>, which defaults to 0 = disabled.
    /// </summary>
    private static readonly string[] ThresholdDriven =
        ["llm_summarizing_condenser", "milestone_checkpoint"];

    /// <summary>
    /// A profile whose budget is at least this large has to say something about
    /// compaction. Below it, a run cannot plausibly fill a window before its own
    /// caps stop it, and demanding a condenser would be cargo-cult.
    ///
    /// ⛔ THE THRESHOLD IS A JOINT CLAIM WITH THE DEFAULTS. Profile.cs:18-19 gives
    /// an undeclared profile MaxIterations = 100 and TimeoutMinutes = 30, so a
    /// profile that declares NEITHER lands inside this rule by default — which is
    /// the correct answer, not an accident: "I didn't think about budget" is not
    /// evidence the run is short.
    /// </summary>
    private const int BudgetIterations = 40;
    private const int BudgetMinutes = 30;

    /// <summary>
    /// ⛔ THE ONE EXEMPTION, AND WHY IT IS NOT A LOOPHOLE.
    ///
    /// `openhands` is a PINNED BENCH BASELINE. Four bench-profiles name it —
    /// swe-rebench-easy-cs, swe-rebench-easy-py, swe-rebench-v2-cs,
    /// swe-rebench-v2-py (× the defaults/ copies of each) — via
    /// `agent_profile: openhands`. Adding a condenser to it would change what
    /// every past run of those four suites was measuring, so every historical
    /// number would silently stop being comparable to every future one. The
    /// profile is deliberately frozen; it is NOT an oversight of the same kind
    /// this gate exists to catch.
    ///
    /// ⭐ THE EXEMPTION IS ITSELF GATED. `The_openhands_exemption_is_still_earned`
    /// below fails the moment nothing references the profile any more, so this
    /// cannot quietly outlive its own justification the way a bare skip-list
    /// would. An exemption with no expiry test is just a hole.
    /// </summary>
    private static readonly string[] FrozenBaselineProfiles = ["openhands"];

    // ── CORPUS: what we actually ship ────────────────────────────────────────

    /// <summary>
    /// ⭐ THE SPANNING GATE, and the one with real teeth. The old version asserted
    /// this for `deepseek-team` alone; every other shipped team profile could have
    /// had an uncovered member and nothing would have said so. A gate that names
    /// one member of a population cannot speak for the population.
    ///
    /// ⛔ TWO BLIND SPOTS CLOSED 2026-08-26, both of which had let a real defect
    /// through on the day they were found:
    ///
    ///   1. IT ONLY LOOKED AT profiles/. The <c>defaults/profiles/</c> tree is a
    ///      second corpus that `vett install defaults` drops at RUNG 1 of the
    ///      resolution chain — see TestRepo.DefaultsProfilesDir. Three of its five
    ///      profiles had uncovered members, and the fixed copies in profiles/ were
    ///      being shadowed by the unfixed ones in exactly the workspaces users
    ///      install into.
    ///
    ///   2. IT SKIPPED EVERY SOLO PROFILE — `if (p.Team is null) continue;` — so
    ///      the gate could not see `coding.yaml`, which is the VETT Chat
    ///      extension's DEFAULT profile (vett-chat.profile, package.json:177) and
    ///      was running 200 iterations across 60 minutes with no compaction
    ///      strategy at all. The gate was green the whole time. A `continue` past
    ///      most of the population is a gate that passes VACUOUSLY, and it reads
    ///      identically to one that passed by checking.
    /// </summary>
    [Fact]
    public void Every_shipped_profile_and_team_member_carries_compaction_middleware()
    {
        var dirs = CorpusDirs();

        var gaps = new List<string>();
        var membersChecked = 0;
        var teamProfiles = 0;
        var soloProfiles = 0;
        var filesChecked = 0;

        foreach (var (label, dir) in dirs)
        foreach (var path in Directory.GetFiles(dir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var name = $"{label}/{stem}";
            filesChecked++;

            Profile p;
            try { p = Yaml.LoadProfile(path); }
            catch (Exception ex)
            {
                // ⛔ COULD-NOT-MEASURE IS NOT MEASURED-CLEAN. A profile that fails
                // to parse must be reported, never skipped into the green.
                gaps.Add($"  {name}: COULD NOT PARSE — {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var isTeam = p.Team is not null && p.Team.Members.Count > 0;
            if (isTeam) teamProfiles++; else soloProfiles++;
            if (isTeam) membersChecked += p.Team!.Members.Count;

            gaps.AddRange(Audit(name, stem, p));
        }

        // ⛔ FLOORS, NOT DECORATION. Each one names a way this gate could report a
        // confident zero without having looked at anything: a moved corpus, a
        // `team:` block that stopped deserialising, or a filter that quietly
        // matched nothing. A gate must span its run's population or it passes
        // vacuously, and a vacuous pass is indistinguishable from a real one.
        Assert.True(filesChecked >= 15,
            $"only {filesChecked} profile file(s) were scanned across {dirs.Count} corpus dir(s) — "
          + "the gate cannot pass by finding nothing.");
        Assert.True(teamProfiles >= 8,
            $"only {teamProfiles} team profile(s) were found — the team block is probably not "
          + "deserialising, so this gate proved nothing about members.");
        Assert.True(soloProfiles >= 6,
            $"only {soloProfiles} solo profile(s) were found. Solo profiles were invisible to this "
          + "gate until 2026-08-26; a count that collapses means the blind spot is back.");
        Assert.True(membersChecked >= 20,
            $"only {membersChecked} member(s) were inspected across {teamProfiles} team profiles.");

        Assert.True(gaps.Count == 0,
            $"{gaps.Count} compaction gap(s) in the shipped corpus:\n{string.Join("\n", gaps)}");
    }

    /// <summary>
    /// THE RULE ITSELF, lifted out of the corpus loop so it can be driven with
    /// KNOWN-BAD input.
    ///
    /// ⛔ WHY THIS IS NOT INLINED. A corpus gate that only ever sees a clean corpus
    /// reports the same green whether it is checking correctly or checking nothing
    /// at all — and this file has already shipped one of each (a guessed-vocabulary
    /// predicate that confidently mis-reported coding-team-v2, and a `continue`
    /// that skipped every solo profile). An invariant is only as real as its
    /// failure test, so <c>The_gate_flags_*</c> below verify the TOOL rather than
    /// the artifact: they hand it profiles that are definitely broken and assert it
    /// says so.
    /// </summary>
    private static List<string> Audit(string name, string stem, Profile p)
    {
        var gaps = new List<string>();

        var isTeam = p.Team is not null && p.Team.Members.Count > 0;
        var frozen = FrozenBaselineProfiles.Contains(stem, StringComparer.Ordinal);
        var hasStrategy = p.Middleware.Intersect(CompactionStrategies).Any();

        // A team profile ALWAYS needs a strategy: its leader coordinates for the
        // whole run. A solo profile needs one once its own budget makes a full
        // window reachable — below that the demand would be cargo-cult.
        var budgeted = p.MaxIterations >= BudgetIterations || p.TimeoutMinutes >= BudgetMinutes;
        var needsStrategy = !frozen && (isTeam || budgeted);

        if (needsStrategy && !hasStrategy)
            gaps.Add($"  {name}: middleware [{string.Join(",", p.Middleware)}] contains no compaction "
                   + $"strategy (one of: {string.Join(", ", CompactionStrategies)}), but the profile "
                   + $"is allowed {p.MaxIterations} iterations across {p.TimeoutMinutes} minutes"
                   + (isTeam ? " AND coordinates a team" : "")
                   + ". Its context grows monotonically until the model refuses. Note that "
                   + "output_truncation caps ONE tool result and stuck_detector watches for loops — "
                   + "neither removes anything from the transcript, so neither is compaction.");

        // ⭐ THE SILENT-DEFAULT TRAP, and the reason this is its own check rather
        // than folded into the one above. A profile can list a perfectly good
        // strategy and still be misconfigured: threshold_tokens DEFAULTS TO 30_000
        // when the `compaction:` block is omitted, which is the historical
        // hardcoded value from before the block existed. On a 128k model that
        // compacts at under a quarter of the window — the run works, nothing
        // errors, it is just needlessly lossy and slow. This is exactly the gap the
        // original version of this file was written to close, and it came back in a
        // profile nobody re-read.
        if (!frozen && p.Compaction is null && p.Middleware.Intersect(ThresholdDriven).Any())
            gaps.Add($"  {name}: lists {string.Join("/", p.Middleware.Intersect(ThresholdDriven))} but "
                   + "declares NO `compaction:` block, so threshold_tokens silently defaults to "
                   + "30000 — the pre-block hardcoded value. Declare it explicitly, tuned to the "
                   + "model's window.");

        if (!isTeam) return gaps;

        AuditMembers(name, p.Team!.Members, p.Middleware, gaps);
        return gaps;
    }

    /// <summary>
    /// The member rule, applied to EVERY member at EVERY nesting depth.
    ///
    /// ⛔ THE RULE INVERTS FOR A `member.team:` SEAT, AND THIS GATE USED TO GET IT
    /// BACKWARDS. For a PLAIN member, absence of `middleware:` means
    /// DefaultMiddleware() — no compaction — which is the whole defect this file
    /// pins. But a member that carries its own `team:` never reaches that path:
    /// Coordinator.cs dispatches it through RunNestedTeamAsync, whose synthesised
    /// sub-profile sets
    ///     Middleware = m.Middleware.Count > 0 ? m.Middleware : parentProfile.Middleware
    /// i.e. absence means INHERITANCE. The `memberCaps` built from
    /// MiddlewareResolver.ResolveOrDefault is used ONLY in the non-team branch of
    /// that call site. So flagging a bare `member.team:` seat as "falls back to
    /// DefaultMiddleware()" states the opposite of what runs.
    ///
    /// ⭐ AND THE HOLE THAT MATTERED MORE. The old loop walked ONLY top-level
    /// members, so once `member.team:` shipped (first profile: 2026-08-26,
    /// ds-manager-flash-cloud) every member INSIDE a sub-team was invisible to this
    /// gate — unbounded-context seats it was written to catch, sitting one level
    /// below where it looked. Recursing closes that; the inversion above is only
    /// the false positive that exposed it.
    /// </summary>
    private static void AuditMembers(
        string name, List<MemberConfig> members, List<string> inherited, List<string> gaps)
    {
        foreach (var m in members)
        {
            // A sub-team seat: absence INHERITS. Check what actually runs, then
            // descend with that as the sub-team's inherited list.
            if (m.Team is not null)
            {
                var effective = m.Middleware.Count > 0 ? m.Middleware : inherited;
                if (!effective.Intersect(CompactionStrategies).Any())
                    gaps.Add($"  {name} → sub-team '{m.Name}': its effective middleware "
                           + $"[{string.Join(",", effective)}] "
                           + (m.Middleware.Count > 0 ? "(declared on the member)" : "(INHERITED from the parent profile)")
                           + " contains no compaction strategy, so the sub-leader coordinates "
                           + "its whole team with a transcript that only grows.");

                AuditMembers($"{name} → {m.Name}", m.Team.Members, effective, gaps);
                continue;
            }

            // The whole point: absence means DefaultMiddleware(), not inheritance.
            if (m.Middleware.Count == 0)
            {
                gaps.Add($"  {name} → member '{m.Name}': declares NO middleware, so it falls back to "
                       + "DefaultMiddleware() = [submit_detector, output_truncation, stuck_detector] "
                       + "(BuiltinTools.cs:127) — no compaction of any kind. Its context grows "
                       + "unbounded regardless of the profile-level compaction block.");
                continue;
            }

            if (!m.Middleware.Intersect(CompactionStrategies).Any())
                gaps.Add($"  {name} → member '{m.Name}': declares middleware "
                       + $"[{string.Join(",", m.Middleware)}] but no compaction strategy among them.");
        }
    }

    // ── THE GATE'S OWN FAILURE TESTS ─────────────────────────────────────────

    /// <summary>
    /// The exact shape `coding.yaml` shipped in: a long-running SOLO profile whose
    /// middleware looks busy but contains nothing that removes anything from the
    /// transcript. The old gate returned zero gaps for this input.
    /// </summary>
    [Fact]
    public void The_gate_flags_a_long_running_solo_profile_with_no_compaction()
    {
        var p = LoadInline("""
            name: fixture-solo-longrun
            middleware:
              - output_truncation
              - stuck_detector
            max_iterations: 200
            timeout_minutes: 60
            """);

        var gaps = Audit("fixture", "fixture-solo-longrun", p);

        Assert.Single(gaps);
        Assert.Contains("no compaction strategy", gaps[0]);
        Assert.Contains("200 iterations across 60 minutes", gaps[0]);
        // And it must explain WHY the two listed middlewares don't count, because
        // "it already has middleware" is the reason this went unnoticed for so long.
        Assert.Contains("neither removes anything from the transcript", gaps[0]);
    }

    /// <summary>
    /// RECURSION, PROVEN BY FAILURE. A member two levels down with no middleware
    /// is exactly the seat this gate exists to catch — and before 2026-08-26 the
    /// walk stopped at the top level, so it returned a confident zero for this
    /// input. The nested implementer here is bare; the gate must name it.
    /// </summary>
    [Fact]
    public void The_gate_flags_a_bare_member_inside_a_sub_team()
    {
        var p = LoadInline("""
            name: fixture-nested-bare-worker
            middleware:
              - milestone_checkpoint
            # ⚠ NOT DECORATION. Omitting this block trips the SILENT-DEFAULT rule
            # (:238) at PROFILE level, and this test asserts Assert.Single — so a
            # fixture that is itself misconfigured would produce a second gap and
            # fail for a reason that has nothing to do with the nesting claim. The
            # fixture has to be clean everywhere EXCEPT the one seat under test.
            compaction:
              threshold_tokens: 48000
            max_iterations: 200
            timeout_minutes: 60
            team:
              leader:
                name: manager
              members:
                - name: feature-lead
                  team:
                    leader:
                      name: feature-lead-lead
                    members:
                      - name: implementer
            """);

        var gaps = Audit("fixture", "fixture-nested-bare-worker", p);

        // The bare NESTED worker is named. The sub-team seat itself is NOT a gap:
        // it inherits milestone_checkpoint from the parent profile.
        Assert.Single(gaps);
        Assert.Contains("implementer", gaps[0]);
        Assert.Contains("DefaultMiddleware()", gaps[0]);
    }

    /// <summary>
    /// THE INVERSION, PINNED IN BOTH DIRECTIONS. For a `member.team:` seat,
    /// absence of `middleware:` means INHERITANCE (RunNestedTeamAsync), not
    /// DefaultMiddleware. So a bare sub-team seat under a compacting parent is
    /// CLEAN, and the same seat under a non-compacting parent is a GAP. A gate
    /// that only ever saw one of these could not tell the two apart.
    /// </summary>
    [Fact]
    public void A_bare_sub_team_seat_inherits_and_is_flagged_only_when_the_parent_lacks_compaction()
    {
        const string shape = """
            name: fixture-inherit-{0}
            middleware:
            {1}
            # See the note in the sibling test: the fixture must be clean at
            # PROFILE level or the "no gaps" arm fails on the wrong rule.
            compaction:
              threshold_tokens: 48000
            max_iterations: 200
            timeout_minutes: 60
            team:
              leader:
                name: manager
              members:
                - name: feature-lead
                  team:
                    leader:
                      name: feature-lead-lead
                    members:
                      - name: implementer
                        middleware:
                          - milestone_checkpoint
            """;

        // Parent DOES compact → the bare sub-team seat inherits it → no gap.
        var clean = Audit("fixture", "fixture-inherit-good",
            LoadInline(string.Format(shape, "good", "  - milestone_checkpoint")));
        Assert.Empty(clean);

        // Parent does NOT compact → the inherited list has no strategy → gap,
        // and it must say the middleware was INHERITED, since that is the fact
        // that makes the finding actionable at the right level.
        var dirty = Audit("fixture", "fixture-inherit-bad",
            LoadInline(string.Format(shape, "bad", "  - stuck_detector")));
        Assert.Contains(dirty, g => g.Contains("sub-team 'feature-lead'")
                                 && g.Contains("INHERITED from the parent profile"));
    }

    /// <summary>
    /// The other side of the budget rule. A 3-iteration / 2-minute one-shot cannot
    /// fill a window before its own caps stop it, so demanding a condenser there
    /// would be cargo-cult — and a rule that fires on everything teaches people to
    /// silence it.
    /// </summary>
    [Fact]
    public void The_gate_does_not_flag_a_short_budget_solo_profile()
    {
        var p = LoadInline("""
            name: fixture-oneshot
            middleware:
              - output_truncation
              - stuck_detector
            max_iterations: 3
            timeout_minutes: 2
            """);

        Assert.Empty(Audit("fixture", "fixture-oneshot", p));
    }

    /// <summary>
    /// Either budget dimension alone is enough — they are OR'd, not AND'd. A
    /// profile capped at 5 iterations but allowed to run for hours can still fill
    /// a window on a handful of enormous tool results.
    /// </summary>
    [Theory]
    [InlineData(200, 5)]   // iterations alone
    [InlineData(5, 60)]    // minutes alone
    public void The_gate_flags_either_budget_dimension_on_its_own(int iterations, int minutes)
    {
        var p = LoadInline($"""
            name: fixture-budget
            middleware:
              - output_truncation
            max_iterations: {iterations}
            timeout_minutes: {minutes}
            """);

        Assert.Single(Audit("fixture", "fixture-budget", p));
    }

    /// <summary>
    /// A member that declares no middleware, under a profile whose own compaction
    /// block is impeccable. The profile-level block does NOT reach it, and this is
    /// the single most load-bearing assertion in the file.
    /// </summary>
    [Fact]
    public void The_gate_flags_a_member_that_declares_no_middleware()
    {
        var p = LoadInline("""
            name: fixture-team
            middleware:
              - milestone_checkpoint
            compaction:
              threshold_tokens: 48000
            max_iterations: 50
            team:
              leader:
                name: lead
              members:
                - name: covered
                  middleware: [milestone_checkpoint]
                - name: bare
            """);

        var gaps = Audit("fixture", "fixture-team", p);

        Assert.Single(gaps);
        Assert.Contains("member 'bare'", gaps[0]);
        Assert.Contains("declares NO middleware", gaps[0]);
        Assert.DoesNotContain(gaps, g => g.Contains("'covered'"));
    }

    /// <summary>
    /// A member with a non-empty middleware list that still contains no compaction
    /// strategy — the failure mode a bare "did they declare anything?" check misses.
    /// </summary>
    [Fact]
    public void The_gate_flags_a_member_whose_middleware_has_no_compaction_strategy()
    {
        var p = LoadInline("""
            name: fixture-team2
            middleware:
              - milestone_checkpoint
            compaction:
              threshold_tokens: 48000
            max_iterations: 50
            team:
              leader:
                name: lead
              members:
                - name: busy-but-uncompacted
                  middleware: [output_truncation, stuck_detector, observation_elision]
            """);

        var gaps = Audit("fixture", "fixture-team2", p);

        Assert.Single(gaps);
        Assert.Contains("busy-but-uncompacted", gaps[0]);
        Assert.Contains("no compaction strategy among them", gaps[0]);
    }

    /// <summary>
    /// The silent-30k trap: a listed threshold-driven strategy with the
    /// <c>compaction:</c> block omitted. Nothing errors, nothing logs, and the
    /// profile reads as if it had no opinion when it actually carries a stale one.
    /// </summary>
    [Fact]
    public void The_gate_flags_a_threshold_driven_strategy_with_no_compaction_block()
    {
        var p = LoadInline("""
            name: fixture-silent-default
            middleware:
              - llm_summarizing_condenser
            max_iterations: 50
            """);

        var gaps = Audit("fixture", "fixture-silent-default", p);

        Assert.Single(gaps);
        Assert.Contains("declares NO `compaction:` block", gaps[0]);
        Assert.Contains("30000", gaps[0]);
    }

    /// <summary>
    /// `replan_checkpoint` is a compaction strategy but is NOT threshold-driven —
    /// it reads its own replan_threshold_tokens. It must satisfy the strategy rule
    /// without tripping the silent-30k rule, or the gate would demand a block that
    /// the strategy never reads.
    /// </summary>
    [Fact]
    public void The_gate_does_not_demand_a_threshold_block_for_replan_checkpoint()
    {
        var p = LoadInline("""
            name: fixture-replan
            middleware:
              - replan_checkpoint
            max_iterations: 200
            timeout_minutes: 180
            """);

        Assert.Empty(Audit("fixture", "fixture-replan", p));
    }

    /// <summary>
    /// ⛔ THE EXEMPTION MUST BE KEYED ON THE NAME, NOT ON THE SHAPE. Two profiles
    /// with byte-identical config must get different verdicts purely because one of
    /// them is the frozen bench baseline — otherwise the exemption is a hole that
    /// any profile can fall into by accident.
    /// </summary>
    [Fact]
    public void The_frozen_baseline_exemption_applies_to_that_name_only()
    {
        const string yaml = """
            name: whatever
            middleware:
              - output_truncation
              - submit_detector
              - stuck_detector
            max_iterations: 500
            timeout_minutes: 240
            """;

        Assert.Empty(Audit("profiles/openhands", "openhands", LoadInline(yaml)));
        Assert.Single(Audit("profiles/not-openhands", "not-openhands", LoadInline(yaml)));
    }

    /// <summary>
    /// ⭐ THE EXEMPTION'S OWN EXPIRY TEST. `openhands` is skipped by the gate above
    /// for exactly one reason: changing it would un-pool every historical
    /// swe-rebench run. The moment nothing references it, that reason is gone and
    /// the skip becomes an unexamined hole — so this fails and forces the choice
    /// to be made again deliberately rather than inherited by default.
    /// </summary>
    [Fact]
    public void The_openhands_exemption_is_still_earned()
    {
        var root = TestRepo.Root();
        Assert.True(root is not null, "could not locate the repo root");

        var benchDirs = new[]
        {
            Path.Combine(root!, "bench-profiles"),
            Path.Combine(root!, "defaults", "bench-profiles"),
        }.Where(Directory.Exists).ToArray();

        Assert.True(benchDirs.Length > 0,
            "no bench-profiles/ directory found — cannot confirm the openhands exemption is earned, "
          + "and 'could not measure' is not 'measured clean'.");

        var referrers = benchDirs
            .SelectMany(d => Directory.GetFiles(d, "*.yaml"))
            .Where(f => File.ReadAllLines(f).Any(l => l.Trim() == "agent_profile: openhands"))
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.True(referrers.Length >= 4,
            $"only {referrers.Length} bench-profile(s) still pin `agent_profile: openhands` "
          + $"[{string.Join(", ", referrers)}]. The compaction exemption for that profile exists "
          + "SOLELY to keep those historical runs comparable. If nothing pins it any more, either "
          + "give openhands a compaction strategy like every other profile, or delete it — do not "
          + "leave a skip whose justification has expired.");
    }

    /// <summary>
    /// ⛔ SHIPPED-COPY IDENTITY. Everything under <c>defaults/</c> that also exists
    /// at the repo root must be byte-identical, because `vett install defaults`
    /// copies the defaults/ tree into <c>&lt;cwd&gt;/</c> and cwd is RUNG 1 — the
    /// highest-priority store. A fix applied to <c>profiles/coding.yaml</c> that
    /// misses <c>defaults/profiles/coding.yaml</c> is not a fix: the installed
    /// workspace keeps reading the old one, and nothing warns.
    ///
    /// This is deliberately a whole-tree rule rather than a profiles-only one. It
    /// found its first two hits outside profiles/: <c>defaults/suites/</c> still
    /// carried the <c>rendering.fields:</c> key that was deleted from the engine
    /// on 2026-08-25, because the removal swept src/ and suites/ but not defaults/.
    /// A retraction that doesn't reach every copy keeps shipping.
    /// </summary>
    [Fact]
    public void Every_defaults_file_is_byte_identical_to_its_repo_twin()
    {
        var root = TestRepo.Root();
        Assert.True(root is not null, "could not locate the repo root");

        var defaults = Path.Combine(root!, "defaults");
        Assert.True(Directory.Exists(defaults), $"no defaults/ tree at {defaults}");

        var drift = new List<string>();
        var pairsChecked = 0;
        var defaultsOnly = 0;

        foreach (var file in Directory.GetFiles(defaults, "*", SearchOption.AllDirectories)
                                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(defaults, file);
            var twin = Path.Combine(root!, rel);

            // defaults-only files (coding-team.yaml, team-example.yaml, the
            // *.template stubs) have no twin to drift from. They are still
            // covered by the compaction gate above, which scans the directory.
            if (!File.Exists(twin)) { defaultsOnly++; continue; }

            pairsChecked++;
            if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(twin)))
                drift.Add($"  {rel}: defaults/ copy differs from the repo copy. `vett install defaults` "
                        + "puts the defaults/ version at <cwd>/, which OUTRANKS the repo version — so "
                        + "whichever one is wrong is the one that ships.");
        }

        Assert.True(pairsChecked >= 10,
            $"only {pairsChecked} defaults/repo pair(s) were compared ({defaultsOnly} defaults-only). "
          + "The gate cannot pass by finding nothing.");

        Assert.True(drift.Count == 0,
            $"{drift.Count} defaults/ file(s) have drifted from their repo twin:\n"
          + string.Join("\n", drift));
    }

    /// <summary>
    /// Both corpora, labelled. defaults/profiles/ is required to exist — if it
    /// vanishes, that is a finding, not a reason to silently narrow the scan.
    /// </summary>
    private static List<(string Label, string Dir)> CorpusDirs()
    {
        var dir = TestRepo.ProfilesDir();
        Assert.True(dir is not null, "could not locate the repo profiles/ directory");

        var defaults = TestRepo.DefaultsProfilesDir();
        Assert.True(defaults is not null,
            "could not locate defaults/profiles/. That tree is installed at RUNG 1 by "
          + "`vett install defaults`, so dropping it from this scan would restore the exact blind "
          + "spot this gate was widened to close on 2026-08-26.");

        return [("profiles", dir!), ("defaults/profiles", defaults!)];
    }

    /// <summary>
    /// The 128k-model profiles must not carry the historical 30k trigger. Named
    /// profiles here on purpose: the THRESHOLD is a per-profile tuning decision,
    /// not a corpus-wide invariant, so this cannot be generalised the way the
    /// member gate above can.
    /// </summary>
    /// ⚠ FLASH RAISED 48_000 -> 100_000 on 2026-08-28, matching the pro rows.
    /// This is NOT a gate being lowered to match a change: the gate's stated job
    /// is "must not carry the historical 30k trigger", and 100k satisfies it more
    /// strongly than 48k did. The 48k value was itself a temporary DERATE taken
    /// when gpu-1 served max_model_len=65536; the profiles carried their own
    /// committed precondition, "RESTORE to 100000 only after re-confirming
    /// max_model_len >= 131072", which was re-confirmed against
    /// `curl -s http://gpu-1:8000/v1/models` before the restore.
    [Theory]
    [InlineData("ds-team-pro", 100_000)]
    [InlineData("ds-solo-pro", 100_000)]
    [InlineData("ds-team-flash", 100_000)]
    [InlineData("ds-solo-flash", 100_000)]
    public void Shipped_profile_compacts_at_its_declared_threshold(string name, int expected)
    {
        var p = Load(name);

        Assert.NotNull(p.Compaction);
        Assert.Equal(expected, p.Compaction!.ThresholdTokens);
        Assert.Contains(Condenser, p.Middleware);
    }

    // ── ENGINE: the mechanisms, independent of who uses them ─────────────────

    /// <summary>
    /// A member-level <c>compaction:</c> block must override the profile-level one.
    ///
    /// ⭐ ON A FIXTURE, NOT THE CORPUS, DELIBERATELY. This used to assert against
    /// deepseek-team-mixed, whose aeon member carried a smaller window. Aeon was
    /// switched off on 2026-08-25 and the profile archived on 08-26, and NO live
    /// profile currently overrides compaction per member — so as a corpus test
    /// this could only have been deleted. The override path is still live code
    /// (`m.Compaction ?? profile.Compaction`, Coordinator.cs:347/965/1444), and
    /// the day someone writes a mixed-window team it must work. A fixture tests
    /// the mechanism whether or not anything shipped happens to use it.
    /// </summary>
    [Fact]
    public void Member_level_compaction_overrides_the_profile_level_threshold()
    {
        var p = LoadInline("""
            name: fixture
            compaction:
              threshold_tokens: 100000
              keep_last_messages: 8
            middleware:
              - llm_summarizing_condenser
            team:
              leader:
                name: lead
              members:
                - name: big-window
                  middleware: [llm_summarizing_condenser]
                - name: small-window
                  middleware: [llm_summarizing_condenser]
                  compaction:
                    threshold_tokens: 24000
                    keep_last_messages: 6
            """);

        Assert.Equal(100_000, p.Compaction!.ThresholdTokens);

        var inherits = p.Team!.Members.First(m => m.Name == "big-window");
        var overrides = p.Team!.Members.First(m => m.Name == "small-window");

        // Two-sided: absence must stay absent (so the ?? falls through to the
        // profile), and presence must actually carry the smaller number.
        Assert.Null(inherits.Compaction);
        Assert.NotNull(overrides.Compaction);
        Assert.Equal(24_000, overrides.Compaction!.ThresholdTokens);
        Assert.Equal(6, overrides.Compaction!.KeepLastMessages);
        Assert.True(overrides.Compaction!.ThresholdTokens < p.Compaction!.ThresholdTokens);
    }

    /// <summary>
    /// The replan strategy parses and is distinct from the condenser.
    ///
    /// Same reasoning as the override test: `replan_checkpoint` is live middleware
    /// but no shipped profile uses it since deepseek-team-longrun was archived.
    /// The two strategies are mutually exclusive by design — replan resets the
    /// window to a forward plan, the condenser carries a growing summary — so
    /// asserting the negative half matters as much as the positive.
    /// </summary>
    [Fact]
    public void Replan_checkpoint_parses_with_its_own_threshold_and_excludes_the_condenser()
    {
        var p = LoadInline("""
            name: fixture-longrun
            middleware:
              - replan_checkpoint
              - observation_elision
            compaction:
              replan_threshold_tokens: 100000
            team:
              leader:
                name: lead
              members:
                - name: worker
                  middleware: [replan_checkpoint]
            """);

        Assert.Contains("replan_checkpoint", p.Middleware);
        Assert.DoesNotContain(Condenser, p.Middleware);
        Assert.Equal(100_000, p.Compaction!.ReplanThresholdTokens);
        Assert.Contains("replan_checkpoint", p.Team!.Members.Single().Middleware);
    }

    /// <summary>
    /// Round-trips YAML through a temp file, because Yaml.LoadProfile is the
    /// path the runtime actually uses — parsing a string by another route would
    /// test a different loader than the one that ships.
    /// </summary>
    private static Profile LoadInline(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), "vett-compaction-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, yaml);
            return Yaml.LoadProfile(path);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
