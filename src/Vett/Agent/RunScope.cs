namespace Vett.Agent;

/// <summary>
/// ⭐ THIS PROCESS'S RUN IDENTITY. One short token, stable for the lifetime of
/// the process, used to qualify session ids so two vett processes running at
/// the same time cannot land in each other's state.
///
/// WHY THIS EXISTS — the session id is not just a label:
///
///   TeamCoordinator's <c>sessionId</c> parameter becomes BOTH
///     (a) the dispatch worktree PANEL ID — Coordinator.cs:344 constructs
///         <see cref="DispatchWorktreeManager"/> with it, and worktrees live
///         at <c>~/.vett/dispatches/&lt;panelId&gt;/&lt;taskId&gt;</c>, and
///     (b) every agent's SANDBOX SESSION name (AgentEnvironment.SessionId,
///         which AgentLoop passes to SessionCreateAsync/SessionDestroyAsync).
///
///   That root is GLOBAL — per USER, not per run, not per workspace, not per
///   process. So the panel id is the ONLY thing separating two concurrent vett
///   processes' dispatch directories, and both entry points were handing it a
///   value that is IDENTICAL across runs:
///
///     vett chat            -> the literal "local"           (ChatCommand.cs)
///     vett team-bench      -> "team-bench-" + instance.Id   (Harness.cs)
///
///   MEASURED 2026-08-26: the width-10 Flash run and the width-10 Pro run —
///   two separate processes, minutes apart — produced 23 and 15 panel ids
///   respectively, of which 13 WERE THE SAME STRING. Under `vett chat` it is
///   worse than that: EVERY chat session on the machine shared the single
///   namespace "local", and the default member names make the task ids collide
///   too (`implementer-1` is `implementer-1` in every session).
///
/// ⛔ THE RECOVERY PATH IS NOT A SUBSTITUTE FOR THIS.
///   DispatchWorktreeManager.CreateAsync already detects an occupied canonical
///   path and provisions a unique sibling instead (added 2026-08-24, covered by
///   DispatchWorktreeCollisionTests). That is genuinely good, and it stays. But
///   it is a RECOVERY, and it has one branch that deletes: when
///   <c>IsLiveWorktreeDir</c> says the occupant is an ORPHAN (gitdir gone) the
///   directory is removed outright. A concurrent run whose parent repo has
///   already been torn down — which is exactly what `team-bench` does to its
///   workspace on PASS — can present as an orphan. Relying on a liveness probe
///   to protect another process's data is strictly weaker than never aiming at
///   it in the first place. Isolation by construction; recovery as the backstop.
///
/// ⚠ THE TOKEN IS DELIBERATELY SHORT, AND THAT IS A CONSTRAINT, NOT A STYLE
///   CHOICE. The qualified id is a DIRECTORY COMPONENT, and nested teams append
///   the parent's task id to it on every level
///   (<see cref="TeamCoordinator.DeriveSubSessionId"/>), so it grows with depth:
///   <c>local-p1a2fbe3-feature-lead-a-1-implementer-2</c>. On Windows that sits
///   under a 260-char ceiling together with the whole checked-out source tree.
///   Ten characters is affordable; a GUID or a timestamp+suite string is not.
///
/// ⚠ IT IS ALSO NOT REUSED ACROSS A RESTART, and that is the accepted trade.
///   With the old fixed ids, a crashed run's leftovers were reclaimed by NAME on
///   the next run. They are now reclaimed only by the age sweep
///   (<c>dispatch_max_age_days</c>, Coordinator.cs:352). Slower cleanup is the
///   price of never deleting a live peer's work; the sweep is the thing that
///   bounds disk, and it already ran on every dispatch-enabled profile.
/// </summary>
public static class RunScope
{
    /// <summary>
    /// Short token unique to this process. The pid half makes a stray worktree
    /// attributable to a still-running process while you are looking at it; the
    /// random half covers pid REUSE, which is real on Windows and would
    /// otherwise reintroduce the collision this type exists to remove.
    /// </summary>
    public static string Token { get; } = DeriveToken(Environment.ProcessId, Guid.NewGuid());

    /// <summary>
    /// The token rule as a PURE FUNCTION of its two inputs.
    ///
    /// ⭐ EXTRACTED SO IT CAN BE FALSIFIED. The property that matters here is
    /// "two processes get different tokens", and a test running inside ONE
    /// process cannot observe that through <see cref="Token"/> — there is
    /// exactly one of it per process, so every assertion available on the
    /// property itself is vacuously true. Taking pid and guid as parameters is
    /// what lets RunScopeTests actually construct the two-process case instead
    /// of asserting something weaker and calling it the same thing.
    /// </summary>
    internal static string DeriveToken(int processId, Guid nonce) =>
        $"p{processId:x}{nonce.ToString("N")[..4]}";

    /// <summary>
    /// Qualify a human-meaningful session id with this process's token.
    /// Keep the base id first so the directory listing still sorts and greps
    /// by what the run WAS, with the disambiguator trailing.
    /// </summary>
    public static string Qualify(string baseId) => $"{baseId}-{Token}";
}
