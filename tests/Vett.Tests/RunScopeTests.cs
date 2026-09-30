using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// THE CROSS-PROCESS ISOLATION CONTRACT for session ids.
///
/// A session id is not a label. TeamCoordinator turns it into the dispatch
/// worktree PANEL ID (Coordinator.cs:344) and into every agent's sandbox
/// session name, and dispatch worktrees live under a root that is per-USER —
/// <c>~/.vett/dispatches/&lt;panelId&gt;/&lt;taskId&gt;</c>. So the panel id is
/// the only thing standing between two vett processes and one directory.
///
/// Both entry points used to hand it a value that is byte-identical across
/// runs: <c>vett chat</c> passed the literal <c>"local"</c>, and the team bench
/// passed <c>"team-bench-" + instance.Id</c>. MEASURED 2026-08-26 — the
/// width-10 Flash run and the width-10 Pro run, two processes minutes apart,
/// emitted 23 and 15 panel ids of which 13 were the SAME STRING.
///
/// ⚠ WHAT THESE TESTS CAN AND CANNOT SEE. <see cref="RunScope.Token"/> is one
/// value per process, so a test running inside a single process cannot observe
/// "two processes differ" through it — every assertion phrased against the
/// property itself passes for the trivial reason that there is only one sample.
/// That is why the rule is extracted as <c>DeriveToken(pid, nonce)</c>: the
/// two-process case is CONSTRUCTED below rather than assumed. The tests that do
/// run against the live singleton are deliberately limited to the claims a
/// single sample can actually support — stability, shape, and length.
/// </summary>
public class RunScopeTests
{
    /// <summary>
    /// ⭐ THE POINT OF THE WHOLE TYPE. Two processes, same base id, different
    /// tokens. Constructed from the derivation rule because the singleton
    /// cannot exhibit it (see the class remarks).
    /// </summary>
    [Fact]
    public void Two_processes_deriving_the_same_base_id_do_not_collide()
    {
        var a = RunScope.DeriveToken(1234, Guid.NewGuid());
        var b = RunScope.DeriveToken(5678, Guid.NewGuid());

        Assert.NotEqual(a, b);
        Assert.NotEqual($"local-{a}", $"local-{b}");
    }

    /// <summary>
    /// The pid alone is NOT sufficient, and this test is the reason the nonce
    /// exists. Windows recycles pids; a `vett chat` started after an earlier one
    /// exited can legitimately be handed the same pid, and with a pid-only token
    /// it would inherit the dead session's namespace — which is precisely the
    /// collision this type removes, reintroduced through the back door.
    /// </summary>
    [Fact]
    public void A_recycled_pid_still_yields_a_different_token()
    {
        const int recycled = 4242;

        var first = RunScope.DeriveToken(recycled, Guid.NewGuid());
        var second = RunScope.DeriveToken(recycled, Guid.NewGuid());

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Attributability. A stray worktree found on disk should name the process
    /// that owns it, so a human can check whether that process is still alive
    /// before deleting anything. Asserted as "the pid appears in hex", which is
    /// the form the derivation writes.
    /// </summary>
    [Fact]
    public void Token_carries_the_pid_so_a_stray_worktree_is_attributable()
    {
        var token = RunScope.DeriveToken(0xBEEF, Guid.Empty);

        Assert.Contains("beef", token, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⛔ THE LENGTH BOUND IS A CORRECTNESS CONSTRAINT, NOT TIDINESS.
    ///
    /// The qualified id is a DIRECTORY COMPONENT, and nested teams append the
    /// parent's task id to it at every level (TeamCoordinator.DeriveSubSessionId),
    /// so a depth-2 dispatch path carries the token plus two task ids plus the
    /// whole checked-out source tree beneath it — under a 260-character ceiling
    /// on Windows. A GUID token (32 chars) or a timestamp-plus-suite-name token
    /// would spend that budget on the disambiguator.
    ///
    /// 16 is slack over the real shape (`p` + up to 8 hex pid digits + 4 nonce
    /// = 13 worst case); it is here to fail if someone swaps in a full GUID,
    /// not to pin the current format to the character.
    /// </summary>
    [Fact]
    public void Token_stays_short_enough_to_nest()
    {
        Assert.True(RunScope.Token.Length <= 16,
            $"token '{RunScope.Token}' is {RunScope.Token.Length} chars; the qualified id is a "
          + "directory component that nesting appends task ids to, under Windows' 260-char path "
          + "ceiling — a long disambiguator spends the budget the source tree needs");

        // Same bound against the widest pid the derivation can be handed, so the
        // check does not silently depend on this process happening to have a
        // short pid.
        Assert.True(RunScope.DeriveToken(int.MaxValue, Guid.NewGuid()).Length <= 16);
    }

    /// <summary>
    /// Stability within a process. Sequential dispatches accumulate in ONE
    /// worktree per task id (DispatchWorktreeManager: accept_dispatch applies
    /// into the parent, and a repeat dispatch to the same member reuses the
    /// canonical path). A token that re-rolled per call would give every
    /// dispatch a fresh namespace and quietly break that accumulation.
    /// </summary>
    [Fact]
    public void Token_is_stable_for_the_life_of_the_process()
    {
        Assert.Equal(RunScope.Token, RunScope.Token);
        Assert.Equal(RunScope.Qualify("local"), RunScope.Qualify("local"));
    }

    /// <summary>
    /// The base id stays the PREFIX. Someone reading `ls ~/.vett/dispatches`
    /// still finds a run by what it was — `team-bench-mgrw10-...` — with the
    /// disambiguator trailing rather than leading. This is the property that
    /// keeps every existing grep and every doc that quotes a panel-id shape
    /// working after the change.
    /// </summary>
    [Fact]
    public void Qualified_id_keeps_the_base_id_as_its_prefix()
    {
        var qualified = RunScope.Qualify("team-bench-mgrw10-ten-independent-value-objects");

        Assert.StartsWith("team-bench-mgrw10-ten-independent-value-objects-", qualified, StringComparison.Ordinal);
        // ...and is genuinely different from it, which is the regression this
        // whole file guards: a revert to the bare literal would pass every
        // other assertion here.
        Assert.NotEqual("team-bench-mgrw10-ten-independent-value-objects", qualified);
    }

    /// <summary>
    /// The token must survive the path sanitizer unmangled — it is only useful
    /// if it reaches the directory name intact. DispatchWorktreeManager.SanitizeId
    /// replaces anything outside <c>[A-Za-z0-9_-]</c> with '_', so two tokens
    /// differing ONLY in a rejected character would sanitize to the same panel
    /// id and fold the two processes straight back together.
    ///
    /// ⚠ ASSERTED AGAINST A LOCAL RESTATEMENT of that allow-list, because
    /// SanitizeId is private. That makes this a check on the TOKEN ALPHABET,
    /// not on the sanitizer — if the sanitizer's rule ever narrows, this test
    /// keeps passing while the property breaks. It is here to catch a change to
    /// the token (a base64 nonce, a timestamp with ':' in it), which is the far
    /// likelier edit of the two.
    /// </summary>
    [Fact]
    public void Token_alphabet_is_a_fixed_point_of_the_path_sanitizer()
    {
        static bool SanitizerKeeps(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_';

        foreach (var c in RunScope.Token)
            Assert.True(SanitizerKeeps(c),
                $"token '{RunScope.Token}' contains '{c}', which DispatchWorktreeManager.SanitizeId "
              + "rewrites to '_' — two processes whose tokens differ only in rewritten characters "
              + "would sanitize to ONE panel id");

        // The whole qualified id too: the base ids in use ("local",
        // "team-bench-<instance>") must not be smuggling anything either.
        foreach (var c in RunScope.Qualify("team-bench-mgrw10-ten-independent-value-objects"))
            Assert.True(SanitizerKeeps(c));
    }
}
