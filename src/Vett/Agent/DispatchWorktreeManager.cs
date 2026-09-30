using System.Diagnostics;
using System.Text;

namespace Vett.Agent;

/// <summary>
/// Per-dispatch git worktree manager.
///
/// When a team member is dispatched (assign_task / assign_async) and the
/// active profile has team.dispatch_worktree=true, the coordinator asks
/// this manager to provision an isolated working directory for the
/// member. The member runs the agent loop with its sandbox rooted at
/// that path; on completion the leader sees a captured diff and decides
/// whether to apply (accept_dispatch) or discard (reject_dispatch).
///
/// Worktrees live at:
///   ~/.vett/dispatches/&lt;panelId&gt;/&lt;taskId&gt;/
/// Branch name in the parent repo:
///   vett/dispatch/&lt;panelId&gt;-&lt;taskId&gt;
///
/// Backing tech: `git worktree add`. Cheap on big repos because objects
/// are shared with the parent .git. Requires the parent cwd to be a git
/// working tree; if it isn't, callers should fall back to in-place
/// dispatch (and the manager throws WorktreeNotSupportedException so the
/// fall-back is explicit, not silent).
///
/// Lifecycle:
///   Create  -&gt; new worktree on fresh detached branch from parent HEAD
///   Capture -&gt; stage everything, return diff blob + diff stat summary
///   Apply   -&gt; git apply --3way the diff into the parent worktree;
///              on success, Discard the dispatch worktree
///   Discard -&gt; remove the worktree dir + delete the branch (no apply)
/// </summary>
public sealed class DispatchWorktreeManager
{
    /// <summary>Root directory under which all dispatch worktrees live.</summary>
    public static string DispatchesRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vett", "dispatches");

    private readonly string _parentCwd;
    private readonly string _panelId;
    private readonly IReadOnlyList<string>? _sparseDirs;

    /// <param name="sparseDirs">
    /// Explicit sparse cones, for tests. Null (every production caller) =
    /// resolve from `VETT_DISPATCH_SPARSE` at create time. Tests inject the
    /// list instead of mutating process env, which xUnit's parallel test
    /// classes would race on.
    /// </param>
    public DispatchWorktreeManager(string parentCwd, string panelId,
                                   IReadOnlyList<string>? sparseDirs = null)
    {
        _parentCwd = parentCwd;
        _panelId = SanitizeId(panelId);
        _sparseDirs = sparseDirs;
    }

    /// <summary>
    /// Directories a ticket allows dispatch worktrees to be narrowed to,
    /// from `VETT_DISPATCH_SPARSE` (separated by ';' or ','; repo-toplevel-
    /// relative; git cone-mode semantics, so root-level FILES are always
    /// included). Empty when unset — full checkout, today's behavior.
    ///
    /// Why: on the DickinsonBros monorepo a full `git worktree add` writes
    /// 15.6 GB and takes ~83 s per member (measured warm, 2026-07-09; cold
    /// is worse). The same worktree narrowed to the ticket's two
    /// directories is 0.5 GB in 3.5 s and passes the identical verify.sh
    /// gate (228/228 + 90/90 + 68/68 — proven before this landed). A
    /// shakedown run creates several of these; the disk churn also feeds
    /// keep-on-failure retention (5 retained worktrees = 78 GB).
    /// </summary>
    public static List<string> SparseDirs() =>
        SparseDirs(Environment.GetEnvironmentVariable("VETT_DISPATCH_SPARSE") ?? "");

    /// <summary>Parse a raw VETT_DISPATCH_SPARSE value. Split out so the
    /// parser is testable without touching process env.</summary>
    public static List<string> SparseDirs(string raw)
    {
        return raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                  .Select(p => p.Trim().Replace('\\', '/').TrimEnd('/'))
                  .Where(p => p.Length > 0)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    /// <summary>
    /// How many times <c>git worktree add</c> is attempted before the failure
    /// is reported. 1 would mean "no retry" — the pre-2026-08-26 behaviour.
    ///
    /// Small ON PURPOSE. The transient this absorbs is a metadata race
    /// between siblings starting at the same instant; it clears in
    /// milliseconds or it is not that race. A large budget would convert a
    /// genuine, permanent-but-unrecognised failure into a long stall that
    /// looks like a slow model instead of a broken repo.
    /// </summary>
    internal const int WorktreeAddAttempts = 4;

    /// <summary>
    /// True when a failed <c>git worktree add</c> CANNOT succeed on a retry,
    /// so the caller should report it rather than back off.
    ///
    /// ⚠ READ THE POLARITY. This enumerates the PERMANENT causes; everything
    /// else is treated as transient and retried. That direction is the safety
    /// property, and it is not interchangeable with the opposite one:
    ///
    ///   - Mis-classifying a permanent failure as transient costs three extra
    ///     git subprocesses and ~1s, then reports the true error anyway.
    ///   - Mis-classifying a transient as permanent loses the dispatch's
    ///     isolation, and its diff is never captured.
    ///
    /// Enumerating the TRANSIENT set instead would be a predicate built from
    /// the ONE race message observed so far — a guessed vocabulary, which
    /// returns a confident zero the first time git words a race differently
    /// or a new transient (locked path, full disk, AV holding a handle)
    /// appears. There is no list of every way concurrency can fail; there IS
    /// a short list of ways a repo is unusable.
    ///
    /// <paramref name="lowercasedGitOutput"/> must already be lowercased —
    /// callers combine stderr+stdout because git splits these across streams
    /// inconsistently between versions.
    /// </summary>
    internal static bool IsPermanentAddFailure(string lowercasedGitOutput)
        => lowercasedGitOutput.Contains("invalid reference: head")
        || lowercasedGitOutput.Contains("not a valid object name")
        || lowercasedGitOutput.Contains("no such ref");

    /// <summary>
    /// Create a per-task worktree rooted at parent HEAD.
    /// Returns the absolute path of the worktree.
    /// Throws <see cref="WorktreeNotSupportedException"/> if the parent
    /// cwd isn't a git repo or git itself fails.
    /// </summary>
    /// <param name="onDiagnostic">
    /// Optional sink for non-fatal provisioning diagnostics (e.g. recovering
    /// from an undeletable stale worktree directory). Null = silent, which is
    /// what the tests and any non-Coordinator caller want.
    /// </param>
    public async Task<DispatchWorktree> CreateAsync(string taskId, CancellationToken ct = default,
                                                    Action<string>? onDiagnostic = null)
    {
        if (!await IsGitRepoAsync(_parentCwd, ct))
            throw new WorktreeNotSupportedException(
                $"{_parentCwd} is not a git repository — `git worktree add` is unavailable. " +
                "Initialize git in the workspace, or disable team.dispatch_worktree in the profile.")
                { IsPermanent = true };

        var safeTaskId = SanitizeId(taskId);
        var path = Path.Combine(DispatchesRoot, _panelId, safeTaskId);
        var branch = $"vett/dispatch/{_panelId}-{safeTaskId}";

        // `git worktree add` always checks out the WHOLE repo, so the
        // worktree root corresponds to the repo toplevel — not to the
        // parent's cwd. When vett runs from a subdirectory (e.g. cwd =
        // <repo>/apps/static-analysis) dropping the member at the
        // worktree root shifts every relative path by that offset: the
        // member looks for src/Foo.cs, finds nothing, and reports the
        // file missing. Capture the offset so the member can be placed
        // at the SAME logical directory inside the worktree.
        var prefix = await GetRepoPrefixAsync(ct);

        // Pre-clean: a stale worktree from a prior run with the same id
        // would block `git worktree add`. Belt-and-braces cleanup that
        // handles all three independent failure modes (dir on disk,
        // .git/worktrees/ metadata, lingering branch). Order matters:
        // remove worktree before deleting branch, then prune metadata,
        // then nuke any leftover dir, then force-delete the branch.
        // All ignoreErrors=true because each op is idempotent.
        await RunGitAsync(_parentCwd, ["worktree", "remove", "--force", path], ct, ignoreErrors: true);
        // worktree prune handles the case where auto-prune deleted the
        // dir off disk but the parent's .git/worktrees/<id>/ metadata
        // is still registered — without this, branch -D below would
        // refuse with "branch is checked out at <path>" even though
        // the path is gone.
        await RunGitAsync(_parentCwd, ["worktree", "prune"], ct, ignoreErrors: true);
        // ...but NEVER onto a directory that is still a LIVE worktree of some
        // repo. The canonical path contains nothing that identifies the RUN
        // (see IsLiveWorktreeDir for the collision mechanics), so this
        // Directory.Delete was capable of reaching into a concurrent vett
        // process's dispatch and deleting its member's only copy of the work,
        // mid-run, silently. Reclaiming an ORPHAN (gitdir gone — e.g. a bench
        // workspace deleted on PASS) destroys nothing and still happens.
        var occupiedByLiveWorktree = IsLiveWorktreeDir(path);
        if (!occupiedByLiveWorktree)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
        }
        await RunGitAsync(_parentCwd, ["branch", "-D", branch], ct, ignoreErrors: true);

        Directory.CreateDirectory(Path.Combine(DispatchesRoot, _panelId));

        // ASSERT the pre-clean above actually worked.
        //
        // Every step of it is best-effort: the three git calls pass
        // ignoreErrors:true and the Directory.Delete is wrapped in a
        // `catch { }`. So a directory the OS refuses to delete survives
        // SILENTLY — a live handle, a process whose cwd is inside it, an
        // AV scanner, or (the case seen in the wild) a worktree registered
        // to a DIFFERENT repo, so `git worktree remove` here doesn't know
        // about it and Directory.Delete is the only remaining defence.
        //
        // The old code then walked straight into `git worktree add`, which
        // fails with "'<path>' already exists" (exit 128) and throws
        // WorktreeNotSupportedException. In Coordinator that tripped a
        // SESSION-WIDE latch, so ONE undeletable directory stripped
        // isolation from every LATER dispatch in the run, their diffs were
        // never captured, and the run reported a plain timeout rather than
        // a fault. Measured 2026-08-24 (armD-run4): 12 of 14 dispatches
        // lost, root cause invisible in the summary.
        //
        // Isolation is the requirement; the exact directory NAME is not.
        // If the canonical path is still occupied, take a unique sibling
        // rather than failing. Deterministic path first (so the common
        // case stays stable and greppable), unique only on collision.
        if (Directory.Exists(path))
        {
            var unique = $"{safeTaskId}-r{Guid.NewGuid().ToString("N")[..8]}";
            var recoveredPath = Path.Combine(DispatchesRoot, _panelId, unique);
            onDiagnostic?.Invoke(occupiedByLiveWorktree
                ? $"the canonical dispatch path '{path}' holds a live git worktree belonging to another " +
                  $"run (its gitdir still exists), so it was deliberately NOT reclaimed — deleting it " +
                  $"would destroy that run's only copy of its member's work. Provisioning at " +
                  $"'{recoveredPath}' instead so this dispatch keeps its isolation."
                : $"stale dispatch worktree at '{path}' could not be removed (pre-clean is best-effort); " +
                  $"provisioning at '{recoveredPath}' instead so this dispatch keeps its isolation. " +
                  $"The stale directory was left untouched for inspection.");
            path = recoveredPath;
            branch = $"vett/dispatch/{_panelId}-{unique}";
        }

        // Sparse mode: register the worktree without materializing files,
        // narrow it to the ticket's cones (plus the parent's own subdir so
        // the member cwd exists), then check out. Any sparse step failing
        // falls back to a FULL checkout — slow but always correct, and a
        // typo'd cone can never produce a member that's missing its repo.
        var sparse = _sparseDirs?.ToList() ?? SparseDirs();
        int ec; string stdout, stderr;

        // ⭐ CONCURRENT FAN-OUT RETRY (2026-08-26). `git worktree add` is not
        // safe to run N-at-a-time against one parent repo, and AgentLoop
        // dispatches N members through Task.WhenAll with no cap. MEASURED:
        // 2 of 8 full-suite runs went red at n=10, and the failure names a
        // race in git's own metadata, not in ours:
        //
        //   implementer-2: git worktree add failed (exit 128)
        //   fatal: failed to read .git/worktrees/implementer-4/commondir
        //
        // i.e. one member's add died reading ANOTHER member's half-written
        // registration. Reproduced OUTSIDE vett with the same message.
        //
        // ⚠ THIS RETRY IS DELIBERATELY CAUSE-AGNOSTIC. The tempting fix is a
        // per-repo lock, but serializing the sparse path's checkouts costs a
        // large share of a dispatch budget, and the cause was still unproven
        // when this was written — fixing an unproven cause at that price is a
        // worse bug than the flake. A retry costs NOTHING on the healthy path
        // and works whatever the transient turns out to be. It also survives
        // the case an in-process lock cannot touch: two vett PROCESSES on one
        // repo.
        //
        // The polarity is the safety property: the PERMANENT set is the
        // enumerated one (it is the same set the throw below already knows),
        // and everything else is retried. Enumerating the TRANSIENT set
        // instead would be a predicate over a vocabulary guessed from ONE
        // observed message — it would return a confident zero the first time
        // git worded a race differently.
        //
        // Each attempt takes a FRESH unique path+branch, so a retry can never
        // collide with whatever a failed attempt half-registered. That is the
        // same "isolation is the requirement, the exact directory NAME is
        // not" principle the stale-path recovery above already relies on.
        var attemptPath = path;
        var attemptBranch = branch;
        for (var attempt = 1; ; attempt++)
        {
            string[] addArgs = sparse.Count == 0
                ? ["worktree", "add", "-b", attemptBranch, attemptPath, "HEAD"]
                : ["worktree", "add", "--no-checkout", "-b", attemptBranch, attemptPath, "HEAD"];

            (ec, stdout, stderr) = await RunGitAsync(_parentCwd, addArgs, ct);
            if (ec == 0)
            {
                path = attemptPath;
                branch = attemptBranch;
                break;
            }

            var why = (stderr + " " + stdout).ToLowerInvariant();
            if (attempt >= WorktreeAddAttempts || IsPermanentAddFailure(why))
                break;   // fall through to the existing throw, with git's own stderr intact

            // Best-effort: drop anything this attempt half-created. Narrow
            // (this path only) rather than a global `worktree prune`, which
            // walks EVERY sibling's metadata and is itself a candidate cause
            // of the race being retried.
            await RunGitAsync(_parentCwd, ["worktree", "remove", "--force", attemptPath], ct, ignoreErrors: true);
            await RunGitAsync(_parentCwd, ["branch", "-D", attemptBranch], ct, ignoreErrors: true);

            onDiagnostic?.Invoke(
                $"`git worktree add` for '{taskId}' failed on attempt {attempt} of {WorktreeAddAttempts} " +
                $"and is being retried — this is the concurrent-fan-out race, not a task failure. " +
                $"git said: {stderr.Trim()}");

            // Backoff with JITTER. Every member of a fan-out fails at the same
            // instant and would otherwise retry at the same instant too,
            // reproducing the contention it is backing off from.
            var jitter = Random.Shared.Next(40, 160);
            await Task.Delay(attempt * 120 + jitter, ct);

            var retryId = $"{safeTaskId}-a{attempt + 1}{Guid.NewGuid().ToString("N")[..4]}";
            attemptPath = Path.Combine(DispatchesRoot, _panelId, retryId);
            attemptBranch = $"vett/dispatch/{_panelId}-{retryId}";
        }

        if (ec == 0 && sparse.Count > 0)
        {
            var cones = new List<string>(sparse);
            var pfx = prefix.Trim().Replace('\\', '/').TrimEnd('/');
            if (pfx.Length > 0 && !cones.Contains(pfx, StringComparer.OrdinalIgnoreCase))
                cones.Add(pfx);

            var (sec, _, _) = await RunGitAsync(
                path, ["sparse-checkout", "set", "--cone", .. cones], ct, ignoreErrors: true);
            if (sec != 0)
                await RunGitAsync(path, ["sparse-checkout", "disable"], ct, ignoreErrors: true);

            // `worktree add --no-checkout` leaves HEAD on the branch with
            // an empty working tree; a plain `checkout` materializes it
            // honoring the sparse spec (or fully, if we just disabled it).
            (ec, stdout, stderr) = await RunGitAsync(path, ["checkout"], ct);
        }
        if (ec != 0)
        {
            // Specific error for "no commits yet" — git init creates a
            // working tree but HEAD points at refs/heads/<branch> which
            // doesn't exist until the first commit. `git worktree add
            // ... HEAD` fails with "invalid reference: HEAD" (older git)
            // or "No such ref" (newer git). Detect and translate so the
            // user knows the fix is `git commit --allow-empty -m init`.
            // Without this they see a confusing fallback warning and may
            // think worktrees just don't work.
            //
            // Shares IsPermanentAddFailure with the retry loop ON PURPOSE.
            // Two copies of "is this permanent?" would drift, and the drift
            // is silent in the dangerous direction: the retry would keep
            // re-running an add that can never succeed, or — worse — give up
            // instantly on the transient it exists to absorb.
            var combined = (stderr + " " + stdout).ToLowerInvariant();
            if (IsPermanentAddFailure(combined))
            {
                throw new WorktreeNotSupportedException(
                    $"Repo at {_parentCwd} has no commits yet — git worktree add needs at least one commit to branch from. " +
                    $"Run `git commit --allow-empty -m init` (or commit any file), then re-trigger the dispatch. " +
                    $"To opt out of per-task worktrees entirely, set team.dispatch_worktree=false in the profile.")
                    { IsPermanent = true };
            }
            throw new WorktreeNotSupportedException(
                $"git worktree add failed (exit {ec}): {stderr.Trim()}\nstdout: {stdout.Trim()}");
        }

        // Capture the base SHA so CaptureAsync can diff against it
        // regardless of whether the implementer committed inside the
        // worktree. Without this, a `git commit` inside the worktree
        // would move HEAD past the changes and `git diff --cached`
        // would silently report nothing — the dispatch would look like
        // a no-op even though the implementer did real work. Some LLMs
        // reflexively `git commit -am` after edits in a git repo; the
        // implementer prompt doesn't ask for it but doesn't ban it.
        var (shaEc, shaOut, shaErr) = await RunGitAsync(path, ["rev-parse", "HEAD"], ct);
        var baseSha = shaEc == 0 ? shaOut.Trim() : "";
        if (string.IsNullOrEmpty(baseSha))
        {
            // rev-parse failed somehow — capture is degraded but not
            // broken. Fall through with empty baseSha; CaptureAsync
            // will fall back to `git diff --cached` (HEAD-relative),
            // matching pre-fix behavior. Skip seeding (nothing to anchor
            // the seed commit against).
            return new DispatchWorktree(this, taskId, path, branch, baseSha, MemberCwd(path, prefix));
        }

        // Seed the fresh worktree with the parent's accumulated uncommitted
        // changes so SEQUENTIAL dispatches build on prior accepted work.
        // (In a sparse worktree a seed diff touching out-of-cone files makes
        // `git apply` fail atomically — the aec != 0 branch below skips the
        // seed and the dispatch degrades to fork-from-HEAD, never corrupts.)
        // Replay the parent's pending diff into the worktree and commit it
        // LOCAL TO THE WORKTREE (parent HEAD is never moved — the parent
        // working tree stays uncommitted for the grading `git diff`), then
        // re-anchor baseSha at that commit so CaptureAsync still captures
        // ONLY this member's new work, not the inherited seed. Best-effort:
        // any failure falls back to the original HEAD baseSha, which is
        // exactly today's (pre-fix) behavior — never worse.
        try
        {
            var seed = await CaptureParentPendingDiffAsync(ct);
            if (!string.IsNullOrEmpty(seed))
            {
                var seedFile = Path.GetTempFileName();
                try
                {
                    await File.WriteAllTextAsync(seedFile, seed, ct);
                    var (aec, _, _) = await RunGitAsync(
                        path, ["apply", "--whitespace=nowarn", "--binary", seedFile], ct, ignoreErrors: true);
                    if (aec == 0)
                    {
                        await RunGitAsync(path, ["add", "-A"], ct);
                        var (cec, _, _) = await RunGitAsync(
                            path,
                            ["-c", "user.name=vett", "-c", "user.email=vett@localhost",
                             "-c", "commit.gpgsign=false",
                             "commit", "--no-verify", "-m", "vett: seed from parent working tree"],
                            ct, ignoreErrors: true);
                        if (cec == 0)
                        {
                            var (rec, rout, _) = await RunGitAsync(path, ["rev-parse", "HEAD"], ct);
                            if (rec == 0) baseSha = rout.Trim();
                        }
                        else
                        {
                            // Apply succeeded but commit didn't (empty commit,
                            // hook, identity) — restore the worktree to HEAD so
                            // CaptureAsync doesn't double-count the seed as this
                            // member's diff. Degrades cleanly to pre-fix state.
                            await RunGitAsync(path, ["reset", "--hard", baseSha], ct, ignoreErrors: true);
                        }
                    }
                }
                finally { try { File.Delete(seedFile); } catch { } }
            }
        }
        catch { /* seeding is best-effort; never block a dispatch on it */ }

        return new DispatchWorktree(this, taskId, path, branch, baseSha, MemberCwd(path, prefix));
    }

    /// <summary>
    /// The parent cwd's path relative to the repo toplevel, e.g.
    /// "apps/static-analysis/" — or "" when vett runs at the toplevel
    /// (the common bench case). Best-effort: on any git failure we
    /// return "", which reproduces the historical root-cwd behavior.
    /// </summary>
    private async Task<string> GetRepoPrefixAsync(CancellationToken ct)
    {
        var (ec, stdout, _) = await RunGitAsync(
            _parentCwd, ["rev-parse", "--show-prefix"], ct, ignoreErrors: true);
        return ec == 0 ? stdout.Trim() : "";
    }

    /// <summary>
    /// Where a dispatched member should be dropped inside the worktree:
    /// the worktree root plus the parent's subdirectory offset, so that
    /// relative paths mean the same thing to the member as they do to
    /// the leader. Falls back to the worktree root when the offset is
    /// empty or the joined path doesn't exist.
    /// </summary>
    public static string MemberCwd(string worktreeRoot, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return worktreeRoot;
        // show-prefix is slash-separated and trailing-slashed ("apps/proj/").
        // Normalize both so the result is a plain directory path callers can
        // compare and hand to a process WorkingDirectory.
        var rel = prefix.Trim().Replace('/', Path.DirectorySeparatorChar)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rel.Length == 0) return worktreeRoot;
        var joined = Path.GetFullPath(Path.Combine(worktreeRoot, rel));
        return Directory.Exists(joined) ? joined : worktreeRoot;
    }

    /// <summary>
    /// Stage all changes in the worktree and capture the diff. Caller
    /// keeps the diff blob; on accept_dispatch we replay it; on reject
    /// we just discard. Returns null Diff if nothing changed.
    /// </summary>
    public async Task<DispatchCapture> CaptureAsync(DispatchWorktree wt, CancellationToken ct = default)
    {
        // COULD-NOT-MEASURE IS NOT MEASURED-ZERO.
        //
        // Every git call below used to have its exit code discarded, so a
        // FAILING git (index.lock held by a concurrent process, full disk,
        // pruned object store, worktree yanked out from under us) produced
        // empty stdout, which produced FilesChanged=0, which produced
        // HasChanges=false — byte-identical to a member that genuinely
        // changed nothing. Coordinator.cs:623-628 then acts on that
        // destructively ("No changes — clean up the worktree right away"),
        // so the ONE case where the worktree is the last surviving copy of
        // the member's work is the case that deletes it, and the leader is
        // told "DISPATCH COMPLETED — NO CHANGES".
        //
        // The codebase already applied this law one level up: Coordinator
        // emits files_changed as null, NOT 0, when no capture ran
        // (Coordinator.cs:643-654, written after 12 lost dispatches in
        // armD-run4 read as 12 clean no-ops). It was never applied INSIDE
        // the capture. Throwing routes into Coordinator's existing
        // `dispatch_capture_failed` handler, so the failure becomes a
        // visible event instead of a silent zero.
        //
        // Stage everything (including untracked files) so the diff sees
        // a complete picture. -A picks up deletes too.
        var (addEc, _, addErr) = await RunGitAsync(wt.Path, ["add", "-A"], ct);

        // WINDOWS RESERVED DEVICE NAMES POISON THE WHOLE STAGE, NOT ONE FILE.
        //
        // Measured 2026-08-26, team-fanout-tier2/fan5 under ds-team-lead-pro.
        // The Pro leader fanned out five members correctly (the assign_async
        // gate passed at min_count=5) and every one of the five lost its work:
        //
        //     error: short read while indexing nul
        //     error: nul: failed to insert into database
        //     error: unable to index file 'nul'
        //     fatal: adding files failed
        //
        // A member had run a build with a DOS-style `> nul` redirect. Under the
        // bash sandbox that does not discard output — it creates a FILE named
        // `nul`, which Win32 resolves to the NUL *device*, so git's read
        // returns nothing and indexing fails. `git add -A` is all-or-nothing,
        // so one junk byte-less artifact takes the entire dispatch diff with
        // it: five members' real edits to five real files, unrecoverable.
        //
        // The retry below is deliberately scoped so the healthy path is
        // untouched — it runs ONLY after an add has already failed, and only
        // for entries that are ENUMERATED FROM THE WORKTREE rather than parsed
        // out of git's stderr (stderr names one file per line and stops at the
        // first fatal, so it is a lower bound on the problem, not a list of
        // it). If the walk finds nothing reserved, the original failure is
        // rethrown unchanged: an add that failed for some OTHER reason must
        // still read as could-not-measure, not as a silent partial success.
        //
        // What is dropped is published, not swallowed — the skipped paths ride
        // out on DispatchCapture.SkippedPaths and are appended to DiffStat, so
        // the reviewing leader and the event stream both see them. A file whose
        // content cannot be read is not content that was lost; the member's
        // real edits are.
        IReadOnlyList<string> skipped = [];
        if (addEc != 0)
        {
            skipped = FindReservedDeviceNames(wt.Path);
            if (skipped.Count == 0)
                throw new DispatchCaptureFailedException(
                    $"git add -A failed (exit {addEc}) in {wt.Path} — the dispatch's changes could NOT be " +
                    $"measured. This is not the same as 'no changes'.\nstderr: {addErr.Trim()}");

            string[] retryArgs = ["add", "-A", "--", ".",
                .. skipped.Select(p => $":(exclude,literal){p}")];
            var (retryEc, _, retryErr) = await RunGitAsync(wt.Path, retryArgs, ct);
            if (retryEc != 0)
                throw new DispatchCaptureFailedException(
                    $"git add -A failed (exit {addEc}) in {wt.Path}, and the retry excluding " +
                    $"{skipped.Count} Windows reserved device name(s) [{string.Join(", ", skipped)}] " +
                    $"ALSO failed (exit {retryEc}) — the dispatch's changes could NOT be measured. " +
                    $"This is not the same as 'no changes'.\n" +
                    $"first stderr: {addErr.Trim()}\nretry stderr: {retryErr.Trim()}");
        }

        // Diff against the base SHA recorded at worktree creation, NOT
        // against current HEAD. This way an implementer that committed
        // inside the worktree (some LLMs do) still produces a correct
        // diff: index+committed_changes vs original parent state.
        // Falls back to HEAD-relative if the base SHA wasn't captured.
        var diffArgs = !string.IsNullOrEmpty(wt.BaseSha)
            ? new[] { "diff", "--cached", wt.BaseSha }
            : new[] { "diff", "--cached" };
        var statArgs = !string.IsNullOrEmpty(wt.BaseSha)
            ? new[] { "diff", "--cached", "--stat", wt.BaseSha }
            : new[] { "diff", "--cached", "--stat" };
        var binArgs = !string.IsNullOrEmpty(wt.BaseSha)
            ? new[] { "diff", "--cached", "--binary", wt.BaseSha }
            : new[] { "diff", "--cached", "--binary" };

        var (statEc, statOut, statErr) = await RunGitAsync(wt.Path, statArgs, ct);
        if (statEc != 0)
            throw new DispatchCaptureFailedException(
                $"git {string.Join(' ', statArgs)} failed (exit {statEc}) in {wt.Path} — the dispatch's " +
                $"changes could NOT be measured. This is not the same as 'no changes'.\nstderr: {statErr.Trim()}");
        // Compare via numeric diff (`git diff --cached --numstat <base>`)
        // for HasChanges instead of `git status` — status shows
        // working-tree-vs-index, which after `git add -A` is empty
        // even when the worktree has committed changes vs the base.
        // numstat against baseSha shows true change count.
        var numstatArgs = !string.IsNullOrEmpty(wt.BaseSha)
            ? new[] { "diff", "--cached", "--numstat", wt.BaseSha }
            : new[] { "diff", "--cached", "--numstat" };
        var (numEc, numstatOut, numErr) = await RunGitAsync(wt.Path, numstatArgs, ct);
        if (numEc != 0)
            throw new DispatchCaptureFailedException(
                $"git {string.Join(' ', numstatArgs)} failed (exit {numEc}) in {wt.Path} — the dispatch's " +
                $"changes could NOT be measured. This is not the same as 'no changes'.\nstderr: {numErr.Trim()}");

        var (diffEc, diffOut, diffErr) = await RunGitAsync(wt.Path, binArgs, ct);
        if (diffEc != 0)
            throw new DispatchCaptureFailedException(
                $"git {string.Join(' ', binArgs)} failed (exit {diffEc}) in {wt.Path} — the diff itself " +
                $"could NOT be read, so accepting this dispatch would apply nothing.\nstderr: {diffErr.Trim()}");

        var filesChanged = CountStatusEntries(numstatOut);
        var changed = filesChanged > 0;

        // PUBLISH THE DISCARD COUNT. DiffStat is what the leader reads in
        // review_dispatch and what rides out as the `diff_summary` event field,
        // so the note has to live here to reach either.
        var stat = changed ? statOut.Trim() : "(no changes)";
        if (skipped.Count > 0)
            stat += $"\n(skipped {skipped.Count} unreadable Windows reserved device name(s): " +
                    $"{string.Join(", ", skipped)} — these cannot be staged or applied)";

        return new DispatchCapture(
            HasChanges: changed,
            Diff: changed ? diffOut : null,
            DiffStat: stat,
            FilesChanged: filesChanged,
            SkippedPaths: skipped);
    }

    /// <summary>
    /// Win32 device names. A path whose final component's stem (the text
    /// before its first '.') matches one of these case-insensitively resolves
    /// to a DEVICE, not a file — `nul.txt` is the NUL device just as `nul` is —
    /// so git can create the directory entry but never read content back.
    /// </summary>
    private static readonly HashSet<string> ReservedDeviceStems =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    /// <summary>
    /// True when <paramref name="fileName"/> is a Windows reserved device name.
    /// Internal so the test suite can assert the classifier two-sided without
    /// having to manufacture an unopenable file on disk.
    /// </summary>
    internal static bool IsReservedDeviceName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var dot = fileName.IndexOf('.');
        var stem = dot < 0 ? fileName : fileName[..dot];
        return ReservedDeviceStems.Contains(stem);
    }

    /// <summary>
    /// Walk the worktree and return the repo-relative, forward-slashed paths of
    /// every entry git cannot index. ENUMERATED, not guessed: git's stderr stops
    /// at its first fatal, so it reports a lower bound rather than the set.
    /// Returns empty on any walk failure — the caller then rethrows the original
    /// add error, which is the correct could-not-measure outcome.
    /// </summary>
    internal static IReadOnlyList<string> FindReservedDeviceNames(string root)
    {
        var found = new List<string>();
        try
        {
            Walk(root, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        found.Sort(StringComparer.Ordinal);
        return found;

        void Walk(string dir, string rel)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(entry);
                // .git is git's own store; never walk into it and never
                // consider its contents skippable.
                if (rel.Length == 0 && name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;

                var childRel = rel.Length == 0 ? name : $"{rel}/{name}";
                if (IsReservedDeviceName(name))
                {
                    found.Add(childRel);
                    continue;
                }
                if (Directory.Exists(entry))
                    Walk(entry, childRel);
            }
        }
    }

    /// <summary>
    /// Apply a captured diff to the parent worktree atomically. Uses
    /// plain `git apply` (NOT --3way): on conflict, fails cleanly
    /// without modifying the parent. The previous --3way approach was
    /// a footgun — when conflicts couldn't be auto-resolved, git wrote
    /// `&lt;&lt;&lt;&lt;&lt;&lt;&lt;` / `=======` / `&gt;&gt;&gt;&gt;&gt;&gt;&gt;` markers
    /// directly into the user's actual files, leaving their workspace
    /// in a half-applied state that the leader couldn't undo. Plain
    /// apply does a `--check` pass internally and refuses to touch
    /// anything if the patch can't apply cleanly.
    ///
    /// Trade-off: parallel dispatches that both modify the SAME file
    /// (even at different lines) will conflict on the second accept
    /// even if a 3-way merge could have resolved them. The OWNS rule
    /// in the leader prompt is supposed to prevent this; if it slips
    /// through, the leader gets a clean conflict error and can
    /// re-dispatch with merged context. Better than silent corruption.
    /// </summary>
    public async Task ApplyAsync(string diff, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(diff))
            return;

        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, diff, ct);

            // Phase 1: dry-run check. `git apply --check` reports whether
            // the patch would apply cleanly without actually modifying
            // anything. If it fails, we abort before touching the parent.
            var (checkEc, checkOut, checkErr) = await RunGitAsync(
                _parentCwd,
                ["apply", "--check", "--whitespace=nowarn", "--binary", tmp],
                ct);
            if (checkEc != 0)
            {
                throw new DispatchApplyConflictException(
                    $"git apply --check failed (exit {checkEc}) — the patch can't apply cleanly. " +
                    $"The parent worktree is unchanged. The dispatch worktree is preserved at {_parentCwd} for inspection.\n" +
                    $"stderr: {checkErr.Trim()}\nstdout: {checkOut.Trim()}");
            }

            // Phase 2: apply. Should succeed since check passed, but a
            // race against parent file changes between check and apply
            // could still fail. Treat as conflict.
            var (ec, stdout, stderr) = await RunGitAsync(
                _parentCwd,
                ["apply", "--whitespace=nowarn", "--binary", tmp],
                ct);
            if (ec != 0)
            {
                throw new DispatchApplyConflictException(
                    $"git apply failed (exit {ec}) — patch passed --check but failed to apply, " +
                    $"likely a race against concurrent parent changes.\n" +
                    $"stderr: {stderr.Trim()}\nstdout: {stdout.Trim()}");
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Tear down a dispatch worktree. Idempotent: missing dirs are fine.
    /// `git worktree remove` cleans up the parent .git's worktree
    /// metadata; the branch is deleted afterwards. We force because the
    /// branch is detached / unmerged on purpose.
    /// </summary>
    public async Task DiscardAsync(DispatchWorktree wt, CancellationToken ct = default)
    {
        await RunGitAsync(_parentCwd, ["worktree", "remove", "--force", wt.Path], ct, ignoreErrors: true);
        await RunGitAsync(_parentCwd, ["branch", "-D", wt.Branch], ct, ignoreErrors: true);
        // Remove dir if `git worktree remove` left it (happens when the
        // worktree was already half-dead).
        try { if (Directory.Exists(wt.Path)) Directory.Delete(wt.Path, recursive: true); } catch { }
    }

    /// <summary>
    /// Best-effort cleanup BY TASK ID, used when CreateAsync is cancelled
    /// mid-execution (or any other failure that doesn't return a
    /// <see cref="DispatchWorktree"/> handle). Reconstructs the path +
    /// branch deterministically from the same id-sanitization rules
    /// CreateAsync uses, then runs the same pre-clean ops. Idempotent —
    /// safe to call even when nothing was actually created.
    /// </summary>
    public async Task PreCleanByTaskIdAsync(string taskId, CancellationToken ct = default)
    {
        var safeTaskId = SanitizeId(taskId);
        var path = Path.Combine(DispatchesRoot, _panelId, safeTaskId);
        var branch = $"vett/dispatch/{_panelId}-{safeTaskId}";

        await RunGitAsync(_parentCwd, ["worktree", "remove", "--force", path], ct, ignoreErrors: true);
        await RunGitAsync(_parentCwd, ["worktree", "prune"], ct, ignoreErrors: true);
        // Same guard as CreateAsync's pre-clean: this is the cancellation
        // cleanup route, and a cancelled dispatch in THIS run must never reap
        // a concurrent run's live worktree that happens to share the path.
        if (!IsLiveWorktreeDir(path))
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }
        await RunGitAsync(_parentCwd, ["branch", "-D", branch], ct, ignoreErrors: true);
    }

    /// <summary>
    /// True when <paramref name="path"/> is the working directory of a git
    /// worktree whose backing repository is STILL THERE — i.e. it belongs to
    /// a run that can still read it, not to a dead one.
    ///
    /// Why this exists: the dispatch path is
    /// ~/.vett/dispatches/&lt;panelId&gt;/&lt;taskId&gt; and carries NO
    /// per-run component. panelId is the coordinator's sessionId, which
    /// team-bench derives from the INSTANCE id ("team-bench-&lt;id&gt;"), so
    /// two vett processes running the same suite instance concurrently — the
    /// normal campaign shape — resolve to the same directory while owning
    /// different parent repos. `git worktree remove` in the pre-clean is
    /// scoped to OUR repo and is a no-op against the other run's worktree,
    /// which left an unconditional Directory.Delete as the thing that
    /// actually ran: it deleted a live dispatch's working files before that
    /// member's diff had ever been captured. Nothing logged it; the run just
    /// scored as a member that changed nothing.
    ///
    /// The test is precise rather than heuristic. `git worktree add` writes
    /// the worktree's `.git` as a FILE containing
    /// `gitdir: &lt;repo&gt;/.git/worktrees/&lt;name&gt;`. If that gitdir
    /// still exists the directory is live and reclaiming it destroys
    /// evidence; if it is gone (the owning repo was deleted — exactly what a
    /// PASSing bench run leaves behind) the directory is an orphan no
    /// process can ever read as a worktree again, and reclaiming it costs
    /// nothing. That keeps the disk-reclamation behaviour this pre-clean was
    /// written for while removing its ability to reach into another run.
    ///
    /// Unreadable / malformed / absent `.git` ⇒ false, which reproduces the
    /// historical behaviour exactly. The guard only ever WITHHOLDS a delete.
    /// </summary>
    internal static bool IsLiveWorktreeDir(string path)
    {
        try
        {
            var dotGit = Path.Combine(path, ".git");
            // A real repo has .git as a DIRECTORY; a worktree has it as a
            // FILE. Only the file form is a registered worktree.
            if (!File.Exists(dotGit)) return false;
            foreach (var line in File.ReadLines(dotGit))
            {
                var t = line.Trim();
                if (!t.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) continue;
                var gitdir = t["gitdir:".Length..].Trim();
                if (gitdir.Length == 0) return false;
                if (!Path.IsPathRooted(gitdir))
                    gitdir = Path.GetFullPath(Path.Combine(path, gitdir));
                return Directory.Exists(gitdir);
            }
            return false;
        }
        catch
        {
            // Can't read it ⇒ can't claim it's live. Falling through to the
            // delete is the pre-fix behaviour, so this can only ever be as
            // destructive as before, never more.
            return false;
        }
    }

    /// <summary>
    /// Sweep ~/.vett/dispatches for worktree directories older than
    /// <paramref name="maxAge"/>. Run on vett startup. Best-effort: any
    /// entry that fails to remove is left for the next sweep so a single
    /// stuck dir doesn't poison the rest. Worktrees still registered with
    /// their parent repo's .git get cleaned up via `git worktree prune`
    /// inside the parent — but because we don't know the parent here, we
    /// rely on the user running git worktree prune from their workspace
    /// occasionally; vett dispatches clean (followup) will handle that
    /// case explicitly.
    /// </summary>
    public static int AutoPrune(TimeSpan maxAge)
    {
        if (!Directory.Exists(DispatchesRoot))
            return 0;

        var cutoff = DateTime.UtcNow - maxAge;
        int removed = 0;

        string[] panelDirs;
        try { panelDirs = Directory.GetDirectories(DispatchesRoot); }
        catch { return 0; }

        foreach (var panelDir in panelDirs)
        {
            // Wrap the inner sweep in try/catch — a panel dir vanishing
            // mid-iteration (concurrent vett instance, user manually
            // cleaning up) would throw DirectoryNotFoundException from
            // EnumerateDirectories. Skip and continue rather than
            // aborting the whole sweep.
            try
            {
                foreach (var dispatchDir in Directory.GetDirectories(panelDir))
                {
                    try
                    {
                        var info = new DirectoryInfo(dispatchDir);
                        if (info.LastWriteTimeUtc < cutoff)
                        {
                            Directory.Delete(dispatchDir, recursive: true);
                            removed++;
                        }
                    }
                    catch { /* skip on error, retry next sweep */ }
                }
            }
            catch { /* panel dir vanished; on to the next */ continue; }

            // Empty panel dir? Drop it too.
            try
            {
                if (Directory.Exists(panelDir) && !Directory.EnumerateFileSystemEntries(panelDir).Any())
                    Directory.Delete(panelDir);
            }
            catch { }
        }
        return removed;
    }

    /// <summary>
    /// Snapshot the parent working tree's uncommitted state — tracked
    /// modifications AND untracked files (honoring .gitignore), deletions
    /// included — as a single HEAD-relative binary patch, WITHOUT touching
    /// the parent's real index. Uses a throwaway GIT_INDEX_FILE so the
    /// `git add -A` here has zero visible effect on the user's staging area.
    /// Returns "" when the working tree matches HEAD (the common
    /// first-dispatch case, where there's nothing accumulated to seed).
    ///
    /// This is what lets SEQUENTIAL dispatches accumulate: accept_dispatch
    /// applies a member's diff to the parent working tree without
    /// committing (so the end-of-run `git diff` grading capture still sees
    /// everything), but `git worktree add ... HEAD` forks from the last
    /// COMMIT. Replaying this snapshot into a fresh worktree makes the next
    /// member start from the current accumulated state, not the seed.
    /// </summary>
    private async Task<string> CaptureParentPendingDiffAsync(CancellationToken ct)
    {
        var tmpIndex = Path.GetTempFileName();
        try
        {
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tmpIndex };
            // Seed the temp index from HEAD first so `add -A` computes a
            // true delta (deletions surface as removals) instead of
            // treating every file as brand new.
            await RunGitAsync(_parentCwd, ["read-tree", "HEAD"], ct, env: env);
            await RunGitAsync(_parentCwd, ["add", "-A"], ct, env: env);
            var (ec, diffOut, _) = await RunGitAsync(
                _parentCwd, ["diff", "--cached", "--binary", "HEAD"], ct, env: env);
            return ec == 0 ? diffOut : "";
        }
        finally
        {
            try { File.Delete(tmpIndex); } catch { }
        }
    }

    // ----- helpers -----

    private static async Task<bool> IsGitRepoAsync(string cwd, CancellationToken ct)
    {
        var (ec, _, _) = await RunGitAsync(cwd, ["rev-parse", "--is-inside-work-tree"], ct, ignoreErrors: true);
        return ec == 0;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string cwd, string[] args, CancellationToken ct, bool ignoreErrors = false,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            // Redirect stdin so the child doesn't inherit the parent's
            // (the JSON-RPC pipe to VS Code in chat mode). Without this,
            // git can hang forever waiting on stdin it'll never receive,
            // and from the user's view "the dispatch just sits there
            // doing nothing" — exactly the symptom that caught us in
            // smoke testing. We close stdin immediately below so git
            // sees EOF and proceeds. DirectBash has the same workaround.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Optional per-invocation env overrides (e.g. GIT_INDEX_FILE for a
        // throwaway index so `git add -A` doesn't touch the real staging
        // area). Set after psi construction so they layer over inherited env.
        if (env is not null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        Process? proc;
        try
        {
            // Process.Start throws Win32Exception when the executable isn't
            // found on PATH (NOT returns null) — the previous `if (proc is
            // null)` path was dead code in that case. Catch here so callers
            // (especially IsGitRepoAsync with ignoreErrors=true) get a clean
            // sentinel result instead of an unhandled Win32Exception that
            // tears down the dispatch with no fallback.
            proc = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            if (ignoreErrors) return (-1, "", $"git start failed: {ex.Message}");
            throw new WorktreeNotSupportedException(
                $"Failed to start git: {ex.Message}. Is git installed and on PATH?");
        }

        if (proc is null)
        {
            if (ignoreErrors) return (-1, "", "git not found");
            throw new WorktreeNotSupportedException("Failed to start git process — is git on PATH?");
        }

        // Close stdin immediately so git sees EOF instead of waiting on
        // the inherited JSON-RPC pipe. Without this, git operations hang
        // indefinitely in chat mode (parent stdin is the VS Code pipe,
        // child git inherits it, never sees EOF, blocks). Mirrors the
        // same workaround in DirectBash.BashExecAsync.
        try { proc.StandardInput.Close(); } catch { }

        using (proc)
        {
            // Kill the subprocess on outer cancellation. Without this,
            // a cancelled long-running git op (e.g. mid-`git apply` on
            // a large diff) keeps running orphaned — Process.Dispose
            // releases the handle but does NOT kill the child. Worst
            // case: half-applied patch files in the parent worktree.
            using var killReg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* already gone, can't kill, fine */ }
            });

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // killReg above already requested termination; give the
                // process a moment to actually exit so we can read its
                // (partial) output, then propagate.
                try { proc.WaitForExit(2000); } catch { }
                throw;
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (proc.ExitCode, stdout, stderr);
        }
    }

    private static int CountStatusEntries(string porcelain)
    {
        if (string.IsNullOrWhiteSpace(porcelain)) return 0;
        return porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string SanitizeId(string raw)
    {
        // Allow [a-zA-Z0-9_-]; replace anything else with '_'. Keeps
        // path/branch names safe across Windows and POSIX without
        // leaking weird chars from upstream.
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }
        var s = sb.ToString();
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }
}

/// <summary>
/// Live dispatch worktree handle. Owned by the coordinator across the
/// member's run; passed to LeaderTools so accept_dispatch / reject_dispatch
/// can resolve task_id back to a path + branch.
///
/// <see cref="BaseSha"/> is the parent's HEAD at the moment the worktree
/// was created. CaptureAsync diffs against this so committed-in-worktree
/// changes still surface; without it, an implementer that ran
/// `git commit -am` would silently produce an empty diff. Empty string
/// when rev-parse failed at creation (rare).
/// </summary>
public sealed record DispatchWorktree(
    DispatchWorktreeManager Manager,
    string TaskId,
    string Path,
    string Branch,
    string BaseSha,
    string Cwd);

/// <summary>Captured state from a finished dispatch worktree.</summary>
public sealed record DispatchCapture(
    bool HasChanges,
    string? Diff,
    string DiffStat,
    int FilesChanged,
    /// <summary>
    /// Repo-relative paths deliberately left out of the stage because they
    /// could not be read (Windows reserved device names). Empty on the healthy
    /// path. A non-empty list means the capture is COMPLETE for everything that
    /// was readable — it is not a partial or degraded diff — but the caller
    /// should surface the names rather than treat them as absent.
    /// </summary>
    IReadOnlyList<string> SkippedPaths);

/// <summary>
/// Thrown when the parent cwd isn't a git repo or git itself fails.
/// Coordinator catches this and falls back to non-isolated dispatch
/// (with a one-line warning to the user via the dispatch event stream).
/// </summary>
public sealed class WorktreeNotSupportedException : Exception
{
    /// <summary>
    /// True when the cause cannot change for the life of this session (not a
    /// git repo, no commits yet) — retrying every dispatch would just burn
    /// git subprocesses, so Coordinator may latch worktrees off.
    ///
    /// False (the DEFAULT) when the cause is per-task and may not recur — a
    /// transient git failure, a locked path, a full disk. Coordinator must
    /// fall back for THIS dispatch ONLY and try again on the next one.
    ///
    /// Why the distinction exists: it used to be absent, so ONE recoverable
    /// failure disabled isolation for an entire run and every later dispatch
    /// silently lost its worktree (armD-run4, 2026-08-24 — 12 of 14 lost).
    /// Defaulting to false is the safe direction: the cost of being wrong is
    /// a few extra git calls, versus silently voiding a two-hour run.
    /// </summary>
    public bool IsPermanent { get; init; }

    public WorktreeNotSupportedException(string message) : base(message) { }
}

/// <summary>
/// Thrown when accept_dispatch's `git apply --3way` fails to apply the
/// captured diff to the parent worktree. The leader sees the message and
/// can react (re-dispatch with merged base, escalate, etc). The dispatch
/// worktree is intentionally NOT torn down on this exception so the user
/// can inspect what was attempted.
/// </summary>
public sealed class DispatchApplyConflictException : Exception
{
    public DispatchApplyConflictException(string message) : base(message) { }
}

/// <summary>
/// Thrown when <see cref="DispatchWorktreeManager.CaptureAsync"/> could not
/// MEASURE a dispatch's changes — a git command exited non-zero.
///
/// This exists to keep a measurement failure distinguishable from a measured
/// zero. Before it, both rendered as HasChanges=false / FilesChanged=0, and
/// Coordinator.cs:623-628 responded by deleting the worktree — destroying the
/// evidence precisely when it could not be read. Coordinator already catches
/// exceptions from CaptureAsync and emits a `dispatch_capture_failed` event,
/// so raising this makes the failure observable in the session log rather
/// than invisible in a zero.
/// </summary>
public sealed class DispatchCaptureFailedException : Exception
{
    public DispatchCaptureFailedException(string message) : base(message) { }
}
