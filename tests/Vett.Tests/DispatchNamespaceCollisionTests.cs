using System.Diagnostics;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// Regression coverage for the SIBLING SUB-TEAM WORKTREE COLLISION (tier-3).
///
/// THE DEFECT (measured live 2026-08-26, suite team-manager-tier3, run mgr2).
/// Dispatch worktrees live at ~/.vett/dispatches/&lt;panelId&gt;/&lt;taskId&gt;, and
/// panelId was simply the session id, passed unchanged into every nested team.
/// Task ids are minted PER TaskBoard, and every nested run builds a fresh
/// board, so each sub-team's numbering restarts at 1. Two sibling sub-teams
/// therefore both minted `implementer-1` into the SAME directory and were both
/// live in it for 18.1 seconds, each running its own edits and `dotnet build`.
/// Worse, CreateAsync pre-cleans the canonical path before checkout, so the
/// second arrival actively deletes the first one's working tree out from under
/// a running agent.
///
/// ⛔ WHY THIS NEEDED A UNIT TEST AND NOT ANOTHER LIVE RUN. THE RUN PASSED.
/// File assertions score the top-level workspace, which is downstream of
/// accept_dispatch — so a trampled sub-team worktree cannot fail any of them.
/// The corruption was visible ONLY as two overlapping live intervals over one
/// path in the event stream. A defect whose live signature is "green" is a
/// defect that live runs cannot be the evidence for; it has to be pinned
/// somewhere that fails loudly and costs nothing to re-run.
///
/// WHAT IS ASSERTED HERE. Not "the ids look different" — these drive the real
/// DispatchWorktreeManager against throwaway git repos and check the property
/// that actually matters: two sibling dispatches bearing the SAME task id get
/// two directories that are simultaneously live and mutually invisible. The
/// pre-fix arrangement is constructed alongside it and shown to collide, so
/// the fix is measured against a demonstrated hazard rather than an assumed
/// one.
///
/// Needs `git` on PATH, same as the manager itself.
/// </summary>
public class DispatchNamespaceCollisionTests : IDisposable
{
    private readonly string _repo;
    private readonly string _session;
    private readonly List<(DispatchWorktreeManager Mgr, DispatchWorktree Wt)> _created = new();
    private readonly List<string> _panelDirs = new();

    public DispatchNamespaceCollisionTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-nsc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        _session = "testns-" + Guid.NewGuid().ToString("N")[..8];

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
            try { mgr.DiscardAsync(wt).GetAwaiter().GetResult(); } catch { /* best-effort */ }
        }
        foreach (var d in _panelDirs)
        {
            try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, d), true); } catch { }
        }
        try { Directory.Delete(_repo, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private DispatchWorktreeManager ManagerFor(string panelId)
    {
        _panelDirs.Add(panelId);
        return new DispatchWorktreeManager(_repo, panelId);
    }

    private async Task<DispatchWorktree> CreateAsync(DispatchWorktreeManager mgr, string taskId)
    {
        var wt = await mgr.CreateAsync(taskId);
        _created.Add((mgr, wt));
        return wt;
    }

    /// <summary>
    /// ⭐ THE CORE PROOF. Two sibling sub-teams, both minting `implementer-1`,
    /// must end up isolated — and isolation is asserted by WRITING through one
    /// handle and reading through the other, not by comparing path strings. A
    /// string comparison would pass against two paths that differ only by a
    /// symlink or case, on a filesystem where they are the same directory.
    /// </summary>
    [Fact]
    public async Task Sibling_subteams_minting_the_same_task_id_get_isolated_worktrees()
    {
        const string taskId = "implementer-1";

        // Exactly what the coordinator derives for each nested team — through
        // the production seam, so a change to the rule breaks this test rather
        // than quietly diverging from it.
        var panelA = TeamCoordinator.DeriveSubSessionId(_session, "feature-lead-a-1");
        var panelB = TeamCoordinator.DeriveSubSessionId(_session, "feature-lead-b-1");
        Assert.NotEqual(panelA, panelB);

        var wtA = await CreateAsync(ManagerFor(panelA), taskId);
        var wtB = await CreateAsync(ManagerFor(panelB), taskId);

        // Both are real, populated checkouts that exist AT THE SAME TIME. The
        // live failure was two agents in one directory, so simultaneity is the
        // property, not "each works when run alone".
        Assert.True(Directory.Exists(wtA.Path));
        Assert.True(Directory.Exists(wtB.Path));
        Assert.True(File.Exists(Path.Combine(wtA.Path, "seed.txt")));
        Assert.True(File.Exists(Path.Combine(wtB.Path, "seed.txt")));

        // Distinct branches too — `git worktree add` refuses to check the same
        // branch out twice, so a shared branch name is its own failure mode
        // independent of the path.
        Assert.NotEqual(wtA.Branch, wtB.Branch);

        // ⭐ ISOLATION BY OBSERVATION. A file written through A must not appear
        // through B, and A's seed must survive B's pre-clean — which is the
        // step that deleted a live sibling's tree in the measured failure.
        await File.WriteAllTextAsync(Path.Combine(wtA.Path, "only-in-a.txt"), "a\n");
        Assert.False(File.Exists(Path.Combine(wtB.Path, "only-in-a.txt")));
        Assert.True(File.Exists(Path.Combine(wtA.Path, "seed.txt")),
            "sibling B's pre-clean removed sibling A's live worktree — this is the original defect");
    }

    /// <summary>
    /// ⛔ THE PREMISE, PROVEN RATHER THAN ASSERTED. This constructs the PRE-FIX
    /// arrangement — both siblings sharing one panel id — and shows the second
    /// Create genuinely destroys the first's work. Without this, the test above
    /// is compatible with a world where the collision never mattered, and it
    /// would keep passing if someone reverted the fix and the harness happened
    /// to tolerate it.
    /// </summary>
    [Fact]
    public async Task Pre_fix_shared_panel_id_really_does_destroy_the_sibling()
    {
        const string taskId = "implementer-1";
        var shared = _session + "-prefix-control";

        // Two managers, one namespace — exactly what passing the parent's
        // sessionId straight down produced.
        var mgrA = ManagerFor(shared);
        var mgrB = new DispatchWorktreeManager(_repo, shared);

        var wtA = await CreateAsync(mgrA, taskId);
        await File.WriteAllTextAsync(Path.Combine(wtA.Path, "sibling-a-work.txt"), "hours of work\n");
        Assert.True(File.Exists(Path.Combine(wtA.Path, "sibling-a-work.txt")));

        // Sibling B arrives at the same canonical path. CreateAsync pre-cleans
        // before checkout, so this is destructive.
        var wtB = await mgrB.CreateAsync(taskId);
        _created.Add((mgrB, wtB));

        Assert.Equal(wtA.Path, wtB.Path);
        Assert.False(File.Exists(Path.Combine(wtA.Path, "sibling-a-work.txt")),
            "the premise did not reproduce: sharing a panel id was expected to be destructive, "
          + "and the fix is only worth its complexity if it is");
    }

    /// <summary>
    /// TOP-LEVEL RUNS MUST BE BYTE-FOR-BYTE UNAFFECTED. The fix qualifies a
    /// namespace, and a qualification that also renamed every ordinary
    /// dispatch would orphan in-flight worktrees and invalidate every path in
    /// prior run artifacts. A top-level dispatch has no parent task id.
    /// </summary>
    [Fact]
    public void Top_level_dispatch_namespace_is_unchanged()
    {
        Assert.Equal(_session, TeamCoordinator.DeriveSubSessionId(_session, ""));
        Assert.Equal(_session, TeamCoordinator.DeriveSubSessionId(_session, null!));
    }

    /// <summary>
    /// The qualified id must survive the path/branch sanitizer INTACT. SanitizeId
    /// rewrites anything outside [A-Za-z0-9_-] to '_', so a separator it did not
    /// permit would map many distinct ids onto one — re-introducing the very
    /// collision this fixes, one layer further down and much harder to see.
    /// </summary>
    [Fact]
    public async Task Qualified_namespace_survives_the_path_sanitizer_intact()
    {
        var panel = TeamCoordinator.DeriveSubSessionId(_session, "feature-lead-a-1");
        var wt = await CreateAsync(ManagerFor(panel), "implementer-1");

        // The panel id appears in the path verbatim — not mangled into
        // underscores, not truncated.
        Assert.Contains(panel, wt.Path);
        Assert.Contains(panel, wt.Branch);
        Assert.DoesNotContain("__", wt.Path[DispatchWorktreeManager.DispatchesRoot.Length..]);
    }

    /// <summary>
    /// ⚠ SUFFICIENCY IS NOT UNIQUENESS — recorded deliberately, as a passing
    /// test rather than a comment, so the limitation cannot be lost.
    ///
    /// The separator is '-', which is legal INSIDE both inputs and is not
    /// escaped. So the encoding is not injective: ("s", "a-1") and ("s-a", "1")
    /// fold to the same namespace. This is unreachable with real rosters —
    /// task ids are &lt;member-name&gt;-&lt;n&gt; over names the profile author
    /// writes, and hitting it needs a member deliberately named to impersonate
    /// a sibling's qualified id. It is NOT unreachable if member names ever
    /// become untrusted input.
    ///
    /// This test asserts the ambiguity EXISTS. If someone later escapes the
    /// separator, it fails and points them here — which is the correct outcome:
    /// the note it protects would by then be wrong.
    /// </summary>
    [Fact]
    public void Namespace_encoding_is_ambiguous_by_construction_and_that_is_documented()
    {
        Assert.Equal(
            TeamCoordinator.DeriveSubSessionId("s", "a-1"),
            TeamCoordinator.DeriveSubSessionId("s-a", "1"));
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
