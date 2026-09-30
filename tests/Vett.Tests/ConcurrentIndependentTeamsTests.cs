using System.Collections.Concurrent;
using System.Diagnostics;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// ⭐⭐ N INDEPENDENT TEAMS AT ONCE — a DIFFERENT AXIS from leader fan-out.
///
/// ⛔ WHY THIS IS NOT <see cref="DispatchFanOutConcurrencyTests"/>. That class
/// proves ONE leader fanning out to many members: a SINGLE panel id, a SINGLE
/// <see cref="DispatchWorktreeManager"/>. Width inside one team was proven to
/// 10. What was never proven is the axis the Minecraft driver actually needs:
/// **10-20 concurrent INDEPENDENT VETT teams**, each its own panel, each its own
/// manager, all contending on the SAME parent repository at the same moment.
///
/// Those are not the same test and one does not imply the other. Fan-out shares
/// a manager, so anything that manager serializes internally protects every
/// member in it. Independent teams share NOTHING but the git repo itself — so
/// this is the first thing that exercises concurrent `git worktree add` from
/// separate managers with separate panel roots, which is precisely the shape
/// that has never run.
///
/// ⭐ WHERE THE ASSERTIONS READ. The nested-sibling worktree collision
/// (2026-08-26) was invisible for a full run because the assertions scored the
/// TOP-LEVEL workspace — upstream of the corruption — and read green while
/// siblings were sharing a checkout underneath. So every assertion here reads
/// DOWNSTREAM, per-team and per-member: each team's own panel directory, each
/// member's own captured diff. A global "did anything fail" check would repeat
/// the original mistake.
///
/// Needs `git` on PATH, same as the manager under test.
/// </summary>
public class ConcurrentIndependentTeamsTests : IDisposable
{
    private readonly string _repo;
    private readonly ConcurrentBag<(DispatchWorktreeManager Mgr, DispatchWorktree Wt)> _created = new();
    private readonly ConcurrentBag<string> _panels = new();

    public ConcurrentIndependentTeamsTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-teams-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);

        Git(_repo, "init");
        Git(_repo, "config", "user.email", "test@vett.local");
        Git(_repo, "config", "user.name", "vett-test");
        Git(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
    }

    public void Dispose()
    {
        foreach (var (mgr, wt) in _created)
        {
            try { mgr.DiscardAsync(wt).GetAwaiter().GetResult(); } catch { }
        }
        foreach (var p in _panels)
        {
            try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, p), true); } catch { }
        }
        try { Directory.Delete(_repo, true); } catch { }
    }

    /// <summary>
    /// ⭐ THE HEADLINE. <paramref name="teams"/> independent teams, each fanning
    /// out to <see cref="MembersPerTeam"/> members, ALL released into
    /// `git worktree add` at the same instant against one repo.
    ///
    /// At 20 teams that is 60 concurrent worktree adds from 20 separate
    /// managers — well past anything VETT has ever been shown to survive.
    ///
    /// Every outcome is collected before any is asserted on: <see
    /// cref="Task.WhenAll{T}(IEnumerable{Task{T}})"/> rethrows only the FIRST
    /// fault, and a partial-failure report that discards 59 of 60 outcomes is
    /// the exact instrument defect DispatchFanOutConcurrencyTests was rebuilt to
    /// avoid. Repeating it here would make a red run undiagnosable.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public async Task N_INDEPENDENT_teams_never_share_a_worktree_or_a_panel(int teams)
    {
        var results = await RunTeamsAsync(teams);

        // 1. Every team got every worktree it asked for. A partial result is
        //    the fault that latches isolation OFF for a whole session.
        Assert.Equal(teams, results.Count);
        foreach (var t in results)
            Assert.Equal(MembersPerTeam, t.Worktrees.Count);

        // 2. GLOBAL path distinctness. Per-team distinctness is not enough:
        //    two DIFFERENT teams landing on one checkout is exactly the
        //    sibling-collision shape, and a per-team check cannot see it.
        var allPaths = results.SelectMany(t => t.Worktrees.Select(w => w.Path)).ToList();
        Assert.Equal(teams * MembersPerTeam, allPaths.Count);
        Assert.Equal(allPaths.Count, allPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // 3. Each worktree is a REAL checkout, not a directory git registered
        //    and then failed to populate under contention.
        foreach (var t in results)
            foreach (var wt in t.Worktrees)
            {
                Assert.True(Directory.Exists(wt.Path), $"team {t.Index}: missing worktree dir {wt.Path}");
                Assert.True(File.Exists(Path.Combine(wt.Path, "seed.txt")),
                    $"team {t.Index}: worktree has no checkout at {wt.Path}");
            }

        // 4. ⭐ THE ISOLATION BOUNDARY, read downstream. Every one of a team's
        //    worktrees must sit under THAT TEAM'S panel directory. This is the
        //    assertion the sibling-collision defect would have failed, and the
        //    one a top-level "did the run succeed" check cannot make.
        foreach (var t in results)
        {
            var panelRoot = Path.Combine(DispatchWorktreeManager.DispatchesRoot, t.PanelId);
            foreach (var wt in t.Worktrees)
                Assert.StartsWith(panelRoot, wt.Path, StringComparison.OrdinalIgnoreCase);
        }

        // 5. git's own account agrees. The managers' return values are not
        //    evidence that git's metadata survived the contention.
        var listed = Git(_repo, "worktree", "list");
        foreach (var p in allPaths)
        {
            var leaf = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar));
            Assert.Contains(leaf, listed, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// ⭐ THE CORRUPTION TEST, and the one that actually matters for unattended
    /// runs. Creation succeeding proves the directories exist; it says nothing
    /// about whether team 7's work can end up in team 3's captured diff.
    ///
    /// Every member writes a file whose name carries BOTH its team and its
    /// member index, then every diff is captured concurrently. A diff that is
    /// empty, or that carries another team's marker, is silent cross-team
    /// contamination — the case where a leader accepts work nobody on its team
    /// did, and no error is ever raised.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public async Task Independent_teams_diffs_NEVER_carry_another_teams_work(int teams)
    {
        var results = await RunTeamsAsync(teams);
        Assert.Equal(teams, results.Count);

        // Each member writes its own uniquely-named file.
        foreach (var t in results)
            for (var m = 0; m < t.Worktrees.Count; m++)
            {
                var cwd = DispatchWorktreeManager.MemberCwd(t.Worktrees[m].Path, "");
                File.WriteAllText(Path.Combine(cwd, Marker(t.Index, m)),
                    $"work by team {t.Index} member {m}\n");
            }

        // Capture every team's diffs concurrently — capture contends on the
        // repo index just as creation did, so serializing it here would test a
        // path the real run never takes.
        var captured = await Task.WhenAll(results.Select(async t =>
            (Team: t, Diffs: await Task.WhenAll(t.Worktrees.Select(w => t.Mgr.CaptureAsync(w))))));

        foreach (var (team, diffs) in captured)
        {
            for (var m = 0; m < diffs.Length; m++)
            {
                var diff = diffs[m].Diff ?? "";

                // POSITIVE CONJUNCT FIRST: an empty diff would vacuously pass
                // every "does not contain" check below.
                Assert.Contains(Marker(team.Index, m), diff);

                // And nothing from any OTHER team or member.
                foreach (var (other, _) in captured)
                    for (var om = 0; om < MembersPerTeam; om++)
                    {
                        if (other.Index == team.Index && om == m) continue;
                        Assert.DoesNotContain(Marker(other.Index, om), diff);
                    }
            }
        }
    }

    private const int MembersPerTeam = 3;

    /// <summary>File name carrying both coordinates, so a stray file is attributable.</summary>
    private static string Marker(int team, int member) => $"t{team:D2}-m{member}.txt";

    private sealed record TeamResult(
        int Index, string PanelId, DispatchWorktreeManager Mgr, List<DispatchWorktree> Worktrees);

    /// <summary>
    /// Build <paramref name="teams"/> independent managers and release ALL of
    /// their dispatches simultaneously. Failures are collected, never thrown,
    /// so a red run reports every outcome rather than the first one.
    /// </summary>
    private async Task<List<TeamResult>> RunTeamsAsync(int teams)
    {
        var gate = new TaskCompletionSource();
        var managers = new List<(int Index, string PanelId, DispatchWorktreeManager Mgr)>();
        for (var i = 0; i < teams; i++)
        {
            var panelId = $"testteams-{Guid.NewGuid():N}"[..24];
            _panels.Add(panelId);
            managers.Add((i, panelId, new DispatchWorktreeManager(_repo, panelId)));
        }

        var tasks = managers.SelectMany(t => Enumerable.Range(0, MembersPerTeam).Select(async m =>
        {
            await gate.Task;
            try
            {
                // ⭐⭐ THE MEMBER NAME IS DELIBERATELY THE SAME IN EVERY TEAM.
                //
                // An earlier draft used `t07-implementer-2`, i.e. names that were
                // globally unique. That draft was GREEN AND POWERLESS: with
                // unique names the panel id is not load-bearing, so a manager
                // that dropped the panel from the path entirely would still hand
                // out distinct directories and every assertion below would pass.
                //
                // Real teams all have an `implementer-1`. Using the same three
                // names in all N teams makes the PANEL ID the only thing keeping
                // team 3's implementer-1 out of team 7's checkout — which is the
                // isolation property this file claims to prove.
                var wt = await t.Mgr.CreateAsync($"implementer-{m}");
                _created.Add((t.Mgr, wt));
                return (t.Index, t.PanelId, t.Mgr, Member: m, Wt: (DispatchWorktree?)wt, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (t.Index, t.PanelId, t.Mgr, Member: m, Wt: (DispatchWorktree?)null, Error: (Exception?)ex);
            }
        })).ToArray();

        gate.SetResult();
        var outcomes = await Task.WhenAll(tasks);

        var failed = outcomes.Where(o => o.Error is not null).ToList();
        if (failed.Count > 0)
            Assert.Fail(Diagnose(teams, outcomes, failed));

        return outcomes
            .GroupBy(o => o.Index)
            .OrderBy(g => g.Key)
            .Select(g => new TeamResult(
                g.Key,
                g.First().PanelId,
                g.First().Mgr,
                g.OrderBy(o => o.Member).Select(o => o.Wt!).ToList()))
            .ToList();
    }

    /// <summary>
    /// Everything needed to classify a red run without reproducing it: which
    /// TEAM each failure belonged to (a failure clustered in one team is a
    /// different bug from failures scattered across all of them), the exception
    /// type and message, and git's own view of the repo.
    /// </summary>
    private string Diagnose(
        int teams,
        (int Index, string PanelId, DispatchWorktreeManager Mgr, int Member, DispatchWorktree? Wt, Exception? Error)[] outcomes,
        List<(int Index, string PanelId, DispatchWorktreeManager Mgr, int Member, DispatchWorktree? Wt, Exception? Error)> failed)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{failed.Count} of {teams * MembersPerTeam} concurrent dispatches failed "
            + $"across {teams} independent teams.");
        sb.AppendLine();

        foreach (var g in failed.GroupBy(f => f.Index).OrderBy(g => g.Key))
        {
            sb.AppendLine($"  team {g.Key} ({g.Count()}/{MembersPerTeam} members failed):");
            foreach (var f in g.OrderBy(f => f.Member))
            {
                sb.AppendLine($"    member {f.Member}: {f.Error!.GetType().Name}");
                foreach (var line in (f.Error.Message ?? "").Split('\n'))
                    sb.AppendLine($"        {line.TrimEnd()}");
            }
        }

        var okTeams = outcomes.Where(o => o.Error is null).GroupBy(o => o.Index)
            .Where(g => g.Count() == MembersPerTeam).Select(g => g.Key).OrderBy(i => i);
        sb.AppendLine();
        sb.AppendLine($"  teams that fully succeeded: [{string.Join(", ", okTeams)}]");

        // Wrapped: a repo wedged badly enough to fail the adds may also fail
        // these, and a diagnostic that throws replaces the real message.
        try { sb.AppendLine().AppendLine("git worktree list:").AppendLine(Git(_repo, "worktree", "list")); }
        catch (Exception ex) { sb.AppendLine($"(git worktree list itself failed: {ex.Message})"); }

        return sb.ToString();
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }
}
