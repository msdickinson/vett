using Vett.Cli;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// `vett run` and `vett bench` are SOLO-ONLY, and both silently discarded a
/// profile's whole `team:` block (found 2026-08-26).
///
/// Runner.cs never reads <c>profile.Team</c> — it builds one LlmSettings from
/// the top-level <c>llm:</c> and calls AgentLoop directly. Only `vett chat`
/// and `vett team-bench` construct TeamCoordinator. So a team profile handed
/// to `run`/`bench` parsed completely, dropped every seat, ran solo, exited 0,
/// and filed a result under a team profile's name.
///
/// ProfileKeyAudit is structurally unable to catch it: that audit reports keys
/// no property will receive, and these keys bind perfectly before being
/// ignored. PARSED and USED are different claims and only the first had an
/// instrument.
/// </summary>
public class TeamProfileOnSoloCommandTests
{
    /// <summary>
    /// The repo's own `profiles/` directory, found by walking up from the test
    /// binary. Deliberately NOT Yaml.Resolve: that searches cwd, ~/.vett and
    /// the build-output snapshot, so it could answer from a STALE COPY and the
    /// test would be about the snapshot rather than about what ships.
    /// </summary>
    private static string ProfilesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, "profiles");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "ds-team-flash.yaml")))
                return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("could not locate the repo's profiles/ directory");
    }

    // ---- two-sided on hand-built profiles ---------------------------------

    [Fact]
    public void A_solo_profile_produces_no_warning()
    {
        var p = new Profile { Name = "solo", Llm = new LlmConfig { Model = "m" } };
        Assert.Null(Helpers.TeamBlockIgnoredWarning(p, "run"));
    }

    /// <summary>
    /// Not just "a warning appeared" — the warning has a JOB, which is to stop
    /// a reader recording this as a team result. So it must name the profile,
    /// say the block is ignored, say the run is solo, and point at the command
    /// that would actually honour the team. A warning that merely says
    /// "something was ignored" leaves the reader exactly as wrong as silence.
    /// </summary>
    [Fact]
    public void A_team_profile_warns_and_the_warning_says_what_the_reader_needs()
    {
        var p = new Profile
        {
            Name = "ds-team-flash",
            Llm = new LlmConfig { Model = "deepseek-v4-flash" },
            Team = new TeamConfig { Members = [new MemberConfig { Name = "implementer" }] },
        };

        var w = Helpers.TeamBlockIgnoredWarning(p, "bench");
        Assert.NotNull(w);
        Assert.Contains("ds-team-flash", w);
        Assert.Contains("IGNORED", w);
        Assert.Contains("SOLO", w);
        Assert.Contains("team-bench", w);
        Assert.Contains("deepseek-v4-flash", w);   // WHICH model actually answers
    }

    // ---- and it must span the REAL population -----------------------------

    /// <summary>
    /// A predicate that is correct on hand-built objects and vacuous on the
    /// shipped profiles would pass every test above and protect nobody. This
    /// loads the profiles that actually exist and asserts the split is real in
    /// BOTH directions — so the test cannot pass by the team profiles having
    /// quietly stopped parsing their own `team:` block.
    ///
    /// It also fails if `profiles/` ever contains only solo profiles, which
    /// would make the warning dead code rather than a working guard.
    /// </summary>
    [Fact]
    public void Every_shipped_team_profile_warns_and_every_shipped_solo_profile_does_not()
    {
        var warned = new List<string>();
        var quiet = new List<string>();
        var skipped = new List<string>();

        foreach (var file in Directory.GetFiles(ProfilesDir(), "*.yaml"))
        {
            Profile p;
            // PUBLISH THE DISCARD COUNT. The first draft of this test swallowed
            // every exception with a bare `continue` — and because it was also
            // calling LoadProfile (which takes a PATH) with file CONTENT, all
            // 13 profiles threw and were dropped. The failure surfaced as
            // "collection was empty", which says nothing about the cause. A
            // skip that is not counted is a result that is not measured.
            try { p = Yaml.LoadProfile(file); }
            catch (Exception ex) { skipped.Add($"{Path.GetFileName(file)}: {ex.GetType().Name}"); continue; }

            var w = Helpers.TeamBlockIgnoredWarning(p, "run");
            (p.Team is null ? quiet : warned).Add(Path.GetFileName(file));

            if (p.Team is null)
                Assert.True(w is null, $"{Path.GetFileName(file)} has no team block but produced a warning");
            else
                Assert.True(w is not null, $"{Path.GetFileName(file)} declares a team but produced NO warning — "
                                         + "`vett run`/`vett bench` would drop it silently");
        }

        var ledger = $"team={warned.Count} solo={quiet.Count} unparseable={skipped.Count}"
                   + (skipped.Count > 0 ? $" [{string.Join("; ", skipped)}]" : "");

        Assert.True(warned.Count > 0, $"no shipped profile declares a team, so the guard is vacuous. {ledger}");
        Assert.True(quiet.Count > 0, $"every shipped profile warned, so the predicate is not discriminating. {ledger}");
    }
}
