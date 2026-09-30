using System.Text.RegularExpressions;

namespace Vett.Tests;

/// <summary>
/// SUITE → PROFILE BINDING GATE.
///
/// THE DEFECT THIS PINS. On 2026-08-25 every one of the 34 suites in this repo
/// named a profile that was absent, dead, or both. Thirty named `coding-team`,
/// which does not exist in this repo's profiles/ at all — so it resolved out of
/// the SHARED ~/.vett/profiles store onto old-gpu-a, a host that has been
/// down for weeks, running the retired AEON Qwen3.6-27B. Nothing complained.
/// `vett validate` was green the whole time, because a suite naming a profile
/// that resolves *somewhere* is structurally fine.
///
/// ⭐ THAT IS THE POINT: the failure was not a crash, it was a SILENT FALLBACK.
/// The three-store resolution order (cwd → ~/.vett → install dir) means a
/// missing file in this repo is not an error, it is a redirection to a copy
/// nobody in this repo can see, review, or update. A fix committed here would
/// simply not be present in what the binary read.
///
/// So the property under test is deliberately NOT "the endpoint is up" — that
/// would be a network test, flaky by construction and useless in CI. It is the
/// static, hermetic half that actually catches the fallback:
///
///     every suite's `profile:` MUST resolve inside THIS repo.
///
/// A suite whose profile lives only in the user store is the bug, whatever that
/// copy happens to contain today.
/// </summary>
public class SuiteProfileBindingTests
{
    /// <summary>
    /// Suites knowingly left broken, each with the reason. ⛔ Keep this as a
    /// last resort and never as a parking space: <see cref="Exemptions_are_still_needed"/>
    /// fails when an entry stops being necessary, so the list cannot quietly rot
    /// into a list of things nobody fixed.
    /// </summary>
    // Empty, and that is the healthy state. `team-python-web-tier2` was the
    // last entry: both its arms were bound to old-gpu-a, and repointing only
    // the default arm would have read as repaired while measuring nothing. Both
    // arms now resolve in-repo against gpu-1 / deepseek-v4-flash, proven by
    // a live PASS of the suite (pw1-flask-url-typo, 142.8s, profile=coding-team-v2)
    // rather than by the profile file merely existing — so the exemption was
    // deleted per Exemptions_are_still_needed.
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal);

    [Fact]
    public void Every_suite_names_a_profile_that_exists_in_THIS_repo()
    {
        var (suiteDir, profileDir) = Locate();

        var suites = Directory.GetFiles(suiteDir, "*.yaml");

        // ⛔ FAIL CLOSED. An empty or unfound directory must not read as "all
        // suites pass" — that is the vacuous green this whole file exists to
        // prevent. The floor is well under the real count (34 at the time of
        // writing) so ordinary additions and deletions do not trip it, but a
        // wrong path or an empty glob does.
        Assert.True(suites.Length >= 25,
            $"expected to find the suite corpus under {suiteDir}, found {suites.Length} file(s). "
          + "The gate cannot pass by finding nothing.");

        var broken = new List<string>();
        var checkedCount = 0;

        foreach (var suite in suites)
        {
            var name = Path.GetFileNameWithoutExtension(suite);
            var declared = DeclaredProfile(suite);

            // A suite with no `profile:` picks one at the command line every
            // time; there is no default to be stale.
            if (declared is null) continue;

            checkedCount++;
            if (Exempt.ContainsKey(name)) continue;

            var path = Path.Combine(profileDir, declared + ".yaml");
            if (!File.Exists(path))
                broken.Add($"  {name} → '{declared}' (no {declared}.yaml in profiles/; "
                         + "it would resolve out of ~/.vett/profiles or the install dir)");
        }

        // Guards the loop itself: if the parser stopped recognising `profile:`
        // lines, every suite would be skipped and the assertion below would
        // pass on an empty list.
        Assert.True(checkedCount >= 25,
            $"only {checkedCount} suite(s) declared a parseable profile — the binding parser "
          + "is probably broken, so this gate proved nothing.");

        Assert.True(broken.Count == 0,
            $"{broken.Count} suite(s) name a profile absent from this repo, so they silently "
          + $"fall through to a store this repo cannot see:\n{string.Join("\n", broken)}");
    }

    /// <summary>
    /// ⭐ THE OTHER SIDE OF THE EXEMPTION LIST. An allowlist nobody re-checks is
    /// how a known defect becomes a permanent one. This fails when an exempt
    /// suite starts passing on its own, forcing the entry to be deleted rather
    /// than left to accumulate.
    /// </summary>
    [Fact]
    public void Exemptions_are_still_needed()
    {
        var (suiteDir, profileDir) = Locate();

        foreach (var (name, reason) in Exempt)
        {
            var suite = Path.Combine(suiteDir, name + ".yaml");

            // An exemption naming a suite that no longer exists is also stale.
            Assert.True(File.Exists(suite),
                $"exempt suite '{name}' no longer exists — delete the exemption. Reason given: {reason}");

            var declared = DeclaredProfile(suite);
            var resolves = declared is not null
                        && File.Exists(Path.Combine(profileDir, declared + ".yaml"));

            Assert.False(resolves,
                $"exempt suite '{name}' now resolves to '{declared}' inside this repo, so the "
              + $"exemption is obsolete — DELETE IT from Exempt. Reason it was added: {reason}");
        }
    }

    /// <summary>
    /// The retired ADDRESSES, pinned. old-gpu-a and old-gpu-b:8000 were
    /// vacated by the 2026-08-22 DHCP re-lease — they are stale addresses, NOT
    /// dead machines: both Sparks are alive at gpu-1 / .100. Nothing answers on
    /// the old addresses, so a profile a suite actually depends on must not name
    /// them. Scoped to profiles REACHED FROM
    /// A SUITE on purpose — unreferenced profiles are retirement candidates and
    /// failing on those would just be noise.
    /// </summary>
    [Fact]
    public void No_suite_depends_on_a_profile_pointing_at_a_retired_host()
    {
        var (suiteDir, profileDir) = Locate();

        // ⛔ The address vocabulary is <see cref="DeadAddress"/> — ONE list, shared
        // with the population gate below. It used to be a second literal array
        // here, which is how .60 came to be pinned in neither: a duplicated value
        // list drifts silently, because updating one copy looks and feels
        // complete. This test keeps its own SCOPING (whole-text, not
        // binding-key) on purpose — see the note at the end of this comment.
        var offenders = new List<string>();
        var reached = 0;

        foreach (var suite in Directory.GetFiles(suiteDir, "*.yaml"))
        {
            var declared = DeclaredProfile(suite);
            if (declared is null) continue;

            var path = Path.Combine(profileDir, declared + ".yaml");
            if (!File.Exists(path)) continue;   // the other test's job

            reached++;
            var body = File.ReadAllText(path);

            // Skip commented lines: the repaired profiles explain in prose WHY
            // they moved off old-gpu-a, and a gate that trips on its own documentation
            // trains people to delete the documentation.
            //
            // Deliberately still a WHOLE-LINE scan rather than the binding-key
            // walk used by the population gate: for ADDRESSES the broader net is
            // the right one, since a retired host is wrong wherever it appears —
            // in a command fragment, an env var, a sandbox allowlist — not only
            // as the value of a key this file happens to know the name of.
            // (Only the MODEL half needs key scoping, because `aeon-mtp` appears
            // legitimately in block-scalar prose.)
            foreach (var line in body.Split('\n'))
            {
                if (line.TrimStart().StartsWith('#')) continue;

                var hit = DeadAddress.Match(line);
                if (hit.Success)
                    offenders.Add($"  {Path.GetFileNameWithoutExtension(suite)} → {declared} names retired host {hit.Value}");
            }
        }

        Assert.True(reached >= 25,
            $"only {reached} suite(s) resolved to a profile in-repo — nothing meaningful was scanned.");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} suite(s) depend on a profile on a dead host:\n{string.Join("\n", offenders.Distinct())}");
    }

    /// <summary>
    /// Profiles that are knowingly unusable and MUST NOT be repointed, each with
    /// the reason. ⛔ Same discipline as <see cref="Exempt"/>: <see
    /// cref="Quarantined_profiles_are_still_broken"/> fails when an entry stops
    /// being necessary.
    /// </summary>
    // Empty, and — unlike Exempt — that is NOT because the entries were repaired.
    // `coding-vision` and `qwen3b-tools` both bound to port 8001, which (probed
    // 2026-08-25) is served by NO host: gpu-1:8001, .100:8001 and .100:8000 all
    // refuse, only gpu-1:8000 answers. They were a DEPLOYMENT gap, not an address
    // gap, so repointing .63 → gpu-1 would have produced profiles equally dead but
    // no longer looking it.
    //
    // On 2026-08-26 both were ARCHIVED to profiles-archive/2026-08-26/ rather than
    // repaired, in the prune that took the picker from 49 profiles to 12. A
    // quarantine entry naming a file that is no longer in profiles/ is exactly the
    // rot Quarantined_profiles_are_still_broken exists to force out, so the entries
    // go with the files. If the :8001 vision/tool service is ever deployed, restore
    // the profiles from the archive — the reason they are gone is recorded here and
    // in the archive directory, not in a list that would otherwise sit here forever
    // describing files nobody can see.
    private static readonly Dictionary<string, string> UndeployedProfiles = new(StringComparer.Ordinal);

    /// <summary>
    /// ⭐ POPULATION GATE — the companion to <see
    /// cref="No_suite_depends_on_a_profile_pointing_at_a_retired_host"/>, which is
    /// scoped to suite-reachable profiles ON PURPOSE.
    ///
    /// THE GAP THIS CLOSES. That scoping was correct for the BENCH, where an
    /// unreferenced profile is a retirement candidate nobody runs. It stopped
    /// being correct when vett-chat shipped: its profile picker lists the UNION of
    /// all three stores, so every profile on disk became a thing a human can
    /// select from a dropdown. A profile no suite names is no longer noise — it is
    /// a menu item. The old gate therefore passes VACUOUSLY over most of what the
    /// picker offers, which is the population that actually reaches a user.
    ///
    /// TWO VOCABULARY ADDITIONS, and the second matters more than the first:
    ///
    ///   • old-runner — the runner box's pre-re-lease address, vacated by the same
    ///     2026-08-22 DHCP event as .63/.65 and simply missed when those two were
    ///     pinned.
    ///
    ///   • ⛔ aeon-mtp — the alias Mark switched OFF on 2026-08-25. This is the
    ///     dangerous one, because it STILL RESOLVES: gpu-1:8000 currently serves
    ///     both `aeon-mtp` and `deepseek-v4-flash` (measured, GET /v1/models). A
    ///     profile repointed to the live host but left on the retired alias
    ///     therefore does not error — it runs, quietly, on the model that was
    ///     deliberately retired, and every downstream number is silently attributed
    ///     to the wrong model. An address gate cannot catch that, because the
    ///     address is correct. Only a MODEL gate can.
    ///
    /// ⛔ WHY THIS IS BINDING-KEY-SCOPED AND THE OLDER TEST IS NOT. A plain text
    /// scan for "aeon-mtp" over non-comment lines matches THREE healthy profiles
    /// here — coding-team-v2, deepseek-team-mixed and ds-solo-flash all discuss
    /// the alias in prose inside block scalars while binding correctly to
    /// deepseek-v4-flash. Block-scalar prose is not a comment, so the older test's
    /// leading-# skip does not exclude it. Matching only VALUES ON BINDING KEYS is
    /// what makes the model half of this gate possible at all; it is not tidiness.
    /// </summary>
    [Fact]
    public void Every_profile_the_chat_picker_offers_binds_to_a_live_target()
    {
        var (_, profileDir) = Locate();

        var profiles = Directory.GetFiles(profileDir, "*.yaml");

        // ⛔ FAIL CLOSED, same reasoning as the suite floor above. The corpus was
        // pruned 49 → 12 on 2026-08-26, so this floor moved 20 → 8 with it.
        Assert.True(profiles.Length >= 8,
            $"expected the profile corpus under {profileDir}, found {profiles.Length} file(s). "
          + "The gate cannot pass by finding nothing.");

        var offenders = new List<string>();
        var quarantined = new List<string>();
        var tally = new ScanTally();

        foreach (var profile in profiles.OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(profile);
            var findings = DeadBindings(File.ReadAllText(profile), tally);

            if (findings.Count == 0) continue;

            if (UndeployedProfiles.ContainsKey(name))
            {
                // ⭐ PUBLISH THE DISCARD COUNT. A quarantine that is silently
                // subtracted reads, in the green case, exactly like a corpus that
                // was entirely clean.
                quarantined.Add($"  {name}: {string.Join("; ", findings)}  [{UndeployedProfiles[name]}]");
                continue;
            }

            foreach (var f in findings)
                offenders.Add($"  {name}: {f}");
        }

        // Guards the loop the same way checkedCount does above: if the binding
        // parser stopped recognising `model:`/`endpoint:` lines, every profile
        // would yield nothing and this gate would pass over an empty scan.
        Assert.True(tally.Inspected >= 20,
            $"only {tally.Inspected} binding(s) were parsed across {profiles.Length} profiles — the "
          + "binding parser is probably broken, so this gate proved nothing.");

        // ⭐ THE CONJUNCT THAT ACTUALLY HAS TEETH, and the reason the count floor
        // above can be loose. A bare total is a weak guard that gets weaker every
        // time the corpus is pruned — and it is satisfiable by a walk that only
        // ever sees the top-level `llm:` block, which is PRECISELY the defect this
        // whole file was written against. Team profiles carry their real bindings
        // hundreds of lines down (ds-team-lead-pro headlines at L31 and binds
        // members at L369/L417), so requiring bindings found DEEP in a file proves
        // the walk descended past the headline rather than merely that it ran.
        //
        // Deliberately phrased over the corpus rather than naming a profile: the
        // names churn (this corpus has been renamed dsv4-* → ds-* once already),
        // and a gate that breaks on a rename teaches people to weaken the gate.
        Assert.True(tally.Deep >= 6,
            $"only {tally.Deep} binding(s) were found past line {ScanTally.DeepLine} of any profile. "
          + "Team profiles bind their members far below the headline `llm:` block, so a walk that "
          + "finds none of those is reading only the top of each file — the exact blind spot this "
          + "gate exists to close.");

        if (quarantined.Count > 0)
            Console.WriteLine($"[quarantined, not counted as failures] {quarantined.Count} profile(s):\n"
                            + string.Join("\n", quarantined));

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} binding(s) in the picker's population point at a retired host or the "
          + $"switched-off aeon-mtp alias:\n{string.Join("\n", offenders)}");
    }

    /// <summary>
    /// ⭐ THE OTHER SIDE OF THE QUARANTINE LIST — mirrors <see
    /// cref="Exemptions_are_still_needed"/>. Fails when a quarantined profile
    /// becomes clean, so the entry must be deleted rather than left to rot into a
    /// permanent excuse.
    /// </summary>
    [Fact]
    public void Quarantined_profiles_are_still_broken()
    {
        var (_, profileDir) = Locate();

        foreach (var (name, reason) in UndeployedProfiles)
        {
            var path = Path.Combine(profileDir, name + ".yaml");

            Assert.True(File.Exists(path),
                $"quarantined profile '{name}' no longer exists — delete the entry. Reason given: {reason}");

            var findings = DeadBindings(File.ReadAllText(path), new ScanTally());

            Assert.True(findings.Count > 0,
                $"quarantined profile '{name}' is now clean, so the quarantine is obsolete — DELETE IT "
              + $"from UndeployedProfiles. Reason it was added: {reason}");
        }
    }

    /// <summary>
    /// What a scan actually inspected, so a caller can assert the walk ran AND
    /// that it went deeper than the headline. <see cref="Deep"/> counts bindings
    /// below <see cref="DeepLine"/>; every profile's top-level `llm:` block sits
    /// well above it, so a walk that never descends scores zero there while still
    /// producing a healthy-looking <see cref="Inspected"/> total.
    /// </summary>
    private sealed class ScanTally
    {
        /// Comfortably below the first nested member binding in the deepest team
        /// profile (ds-team-lead-pro, L369) and comfortably above every top-level
        /// block (the highest headline in this corpus is ds-team-pro at L49).
        internal const int DeepLine = 100;

        internal int Inspected;
        internal int Deep;
    }

    private static readonly Regex BindingLine =
        new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex DeadAddress =
        new(@"192\.168\.0\.(60|63|65)\b", RegexOptions.Compiled);

    private const string BindingKeys = "endpoint base_url url api_base model leader_model member_model";

    /// <summary>
    /// Every binding in <paramref name="yaml"/> that cannot work, as human-readable
    /// strings. Walks lines rather than deserialising on purpose: the point is to
    /// catch NESTED per-member bindings, which is exactly what a top-level
    /// <c>vett profiles --json</c> summary cannot show you — a team profile whose
    /// leader is healthy and whose members are dead reads as healthy there.
    ///
    /// <paramref name="tally"/> accumulates every binding INSPECTED (not every one
    /// rejected), so the caller can prove the walk actually walked — and how deep.
    /// </summary>
    private static List<string> DeadBindings(string yaml, ScanTally tally)
    {
        var found = new List<string>();
        var lines = yaml.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            var m = BindingLine.Match(line);
            if (!m.Success) continue;

            var key = m.Groups[1].Value;
            if (!BindingKeys.Split(' ').Contains(key, StringComparer.Ordinal)) continue;

            var val = m.Groups[2].Value.Trim().Trim('"', '\'').Trim();
            if (val.Length == 0) continue;

            tally.Inspected++;
            if (i + 1 > ScanTally.DeepLine) tally.Deep++;

            if (DeadAddress.IsMatch(val))
                found.Add($"L{i + 1} {key}: {val}  <-- retired address (vacated 2026-08-22)");
            // Whole-value match, never a substring: prose cannot be exactly this,
            // and `deepseek-v4-flash` must never be flagged for containing it.
            else if (string.Equals(val, "aeon-mtp", StringComparison.Ordinal))
                found.Add($"L{i + 1} {key}: {val}  <-- aeon switched off 2026-08-25 (STILL RESOLVES on gpu-1 — fails silently-well)");
        }

        return found;
    }

    /// <summary>
    /// Drops a full-line comment and a trailing <c> # …</c>, but never a <c>#</c>
    /// inside a quoted scalar. Erring toward under-stripping is the safe
    /// direction: a missed strip can only ADD a finding, which a human then reads,
    /// whereas over-stripping removes one silently.
    /// </summary>
    private static string StripComment(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        char? quote = null;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null)
            {
                sb.Append(c);
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) break;
            sb.Append(c);
        }

        return sb.ToString();
    }

    // `profile: name` at column 0, tolerating CRLF and a trailing comment.
    // Anchored at the line start so a nested `profile:` inside an instance
    // block cannot be mistaken for the suite-level binding.
    private static readonly Regex ProfileLine =
        new(@"^profile:[ \t]*([^\s#]+)", RegexOptions.Multiline | RegexOptions.Compiled);

    private static string? DeclaredProfile(string suitePath)
    {
        var m = ProfileLine.Match(File.ReadAllText(suitePath));
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// ⛔ ANCHOR ON global.json, NOT ON "has suites/ and profiles/".
    ///
    /// Vett.csproj copies profiles/** and suites/** into the build output
    /// (lines 45-46, CopyToOutputDirectory). So AppContext.BaseDirectory —
    /// bin/Debug/net10.0 — HAS BOTH DIRECTORIES, and a walk-up that stops at
    /// the first match stops immediately, on build-output copies.
    ///
    /// That is not a hypothetical. The first version of this file did exactly
    /// that, and all three tests passed while reading stale copies: reverting a
    /// suite to `coding-team` in the repo left the gate green, because the gate
    /// was never looking at the repo. A gate that reads the artifact its own
    /// build produced cannot detect anything about the source.
    ///
    /// global.json sits at the repo root and is in no Content Include, so it
    /// never appears in bin/. Requiring it alongside both directories pins the
    /// walk to the real root.
    /// </summary>
    private static (string SuiteDir, string ProfileDir) Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var suites = Path.Combine(dir.FullName, "suites");
            var profiles = Path.Combine(dir.FullName, "profiles");
            if (File.Exists(Path.Combine(dir.FullName, "global.json"))
                && Directory.Exists(suites) && Directory.Exists(profiles))
            {
                // Belt and braces: name the failure mode out loud rather than
                // silently reading copies again if the anchor ever moves.
                Assert.DoesNotContain($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    suites, StringComparison.OrdinalIgnoreCase);
                return (suites, profiles);
            }
            dir = dir.Parent;
        }

        // Not Assert.Fail-and-return-empty: a located-nothing run must stop
        // here rather than hand the caller two paths that glob to zero files.
        throw new DirectoryNotFoundException(
            "could not find the repo root (a directory holding global.json, suites/ and "
          + $"profiles/) above {AppContext.BaseDirectory}");
    }
}
