using System.Text.Json;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Agent;

/// <summary>
/// The 6 delegation tools available to the leader agent.
/// Not globally registered — injected into the leader's tool map at runtime.
/// </summary>
public static class LeaderTools
{
    /// <summary>
    /// Build the leader's delegation toolkit.
    ///
    /// <paramref name="runMember"/> takes (taskId, name, task, ct) so
    /// each dispatch is uniquely identified — that lets multiple
    /// parallel instances of the same member coexist (their events
    /// and state are tracked per-task_id, not per-name).
    ///
    /// <paramref name="continueMember"/> is optional; when non-null,
    /// the leader gets a `continue_task(task_id, message)` tool that
    /// resumes a prior member's conversation with a new turn rather
    /// than spawning a fresh instance.
    /// </summary>
    /// <summary>
    /// Files a ticket declares it MUST modify, from `VETT_REQUIRED_PATHS`
    /// (separated by ';', ',' or whitespace). Empty when unset — every
    /// existing caller keeps today's behavior.
    /// </summary>
    public static List<string> RequiredPaths()
    {
        var raw = Environment.GetEnvironmentVariable("VETT_REQUIRED_PATHS") ?? "";
        return raw.Split([';', ',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                  .Select(p => p.Trim())
                  .Where(p => p.Length > 0)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    /// <summary>
    /// What to tell a leader that just hit <see cref="TaskBoard.MaxConcurrentDispatches"/>.
    /// </summary>
    /// <remarks>
    /// THE ADVICE MUST MATCH THE TOOL TABLE THE LEADER ACTUALLY HAS.
    ///
    /// Both dispatch-limit refusals used to recommend <c>wait_task</c> and
    /// <c>check_tasks</c> unconditionally. In chat mode those tools do not
    /// exist: Coordinator strips <c>check_task</c>, <c>check_tasks</c> and
    /// <c>wait_task</c> from both the tool table and the schemas whenever
    /// <see cref="TaskBoard.AutoInjectCompletions"/> is on, precisely so the
    /// leader cannot freeze the chat by polling.
    ///
    /// So the engine was telling the leader, at the exact moment it was already
    /// blocked, to call a tool the engine had just removed. The leader cannot
    /// comply. It burns an iteration on a refusal, or -- as recorded at
    /// AgentLoop.cs "leader emitted FIVE check_task calls as plain text" --
    /// emits the call as prose, which is worse: no tool runs, no error is
    /// raised, and the turn looks like the model rambling.
    ///
    /// In auto-inject mode the correct move is to stop and end the turn.
    /// Completions are delivered into a later turn on their own, which frees
    /// the slot without the leader doing anything at all.
    /// </remarks>
    internal static string LimitAdvice(TaskBoard board) =>
        board.AutoInjectCompletions
            ? "Do NOT poll and do NOT call a wait/check tool — in this mode they are not "
              + "registered. End your turn instead: results are delivered automatically in a "
              + "later turn, and a slot frees as soon as one lands. Assign again then."
            : "Use `wait_task` to let running work finish (or `check_tasks` to see what is "
              + "outstanding), then assign again once a slot frees.";

    /// <summary>
    /// Why an evidence gate returned "nothing missing" WITHOUT having measured
    /// anything. Empty-because-satisfied and empty-because-blind are the same
    /// value (<c>[]</c>) to a caller; this is what separates them.
    /// </summary>
    public sealed record GateFailOpen(string Gate, string Reason);

    /// <summary>
    /// Of <paramref name="required"/>, which paths show no change in the
    /// working tree? Uses `git status --porcelain`, which reports modified
    /// AND untracked files — a brand-new file is real work and must count.
    /// On any git failure we return empty (fail open): the evidence gate must
    /// never wedge a run in a non-git workspace.
    ///
    /// ⚠ This overload DISCARDS the reason, so empty-because-blind and
    /// empty-because-satisfied are again the same value. `declare_done` calls
    /// <see cref="UntouchedDetailedAsync"/> so it can tag the submission; any
    /// new caller that treats [] as evidence must do the same.
    /// </summary>
    public static async Task<List<string>> UntouchedAsync(
        ISandbox sandbox, string sessionId, List<string> required, CancellationToken ct)
        => (await UntouchedDetailedAsync(sandbox, sessionId, required, ct)).Missing;

    /// <summary>
    /// <see cref="UntouchedAsync"/>, but reporting WHETHER IT COULD MEASURE.
    ///
    /// The fail-open itself is deliberate and stays: a non-git workspace must
    /// not wedge a run. The defect was that it happened SILENTLY — an
    /// unmeasured gate and a satisfied gate both returned <c>[]</c>, so a
    /// completed-with-no-changes run was accepted as evidence-verified.
    ///
    /// ⛔ `r.TimedOut` was CAPTURED BUT NEVER GATED ON. On timeout the sandbox
    /// returns ExitCode -1 (`DirectBash.cs:167-169`), which the old code read
    /// as "git failed, fail open" — while `TimedOut` was consulted a few dozen
    /// lines away for VETT_VERIFY_CMD. That direction matters: `git status` on
    /// a 40k-file worktree with a cold cache or an antivirus scan is exactly
    /// the load-dependent case, so the blind spot fires preferentially on the
    /// biggest, slowest, genuinely-hardest instances.
    ///
    /// ⚠ TWO ROOTS. `git status --porcelain` reports paths relative to the
    /// REPO TOPLEVEL no matter which directory it runs in (measured, not
    /// assumed), while a ticket's VETT_REQUIRED_PATHS are written relative to
    /// the PROJECT — in this monorepo `src/Foo.cs`, not `apps/proj/src/Foo.cs`.
    /// So a required path is checked against BOTH spellings, with the cwd's
    /// repo-relative prefix from <see cref="RepoPrefixAsync"/> supplying the
    /// second. See that method for why this is not a way back to substrings.
    /// </summary>
    public static async Task<(List<string> Missing, GateFailOpen? FailedOpen)> UntouchedDetailedAsync(
        ISandbox sandbox, string sessionId, List<string> required, CancellationToken ct)
    {
        const string gate = "required-paths";
        try
        {
            var r = await sandbox.BashExecAsync(sessionId, "git status --porcelain", 30, ct);
            // TimedOut first: a timeout also sets ExitCode -1, and "timed out"
            // is the more actionable of the two readings.
            if (r.TimedOut)
                return ([], new GateFailOpen(gate, "`git status --porcelain` timed out after 30s"));
            if (r.ExitCode != 0)
                return ([], new GateFailOpen(gate, $"`git status --porcelain` exited {r.ExitCode} (not a git repo?)"));
            var changed = ParsePorcelainPaths(r.Stdout ?? "");
            var prefix = await RepoPrefixAsync(sandbox, sessionId, ct);
            return (required.Where(p => !PathIsTouchedUnderPrefix(p, changed, prefix)).ToList(), null);
        }
        catch (Exception ex)
        {
            return ([], new GateFailOpen(gate, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// The set of paths `git status --porcelain` reports as changed, parsed
    /// into actual paths rather than left as one blob of text.
    ///
    /// ⛔ WHY THIS EXISTS. The gate used to ask
    /// <c>wholePorcelainOutput.Contains(requiredPath)</c>. Substring
    /// containment is not path equality, and it is wrong in BOTH directions
    /// of the thing it is supposed to detect:
    ///
    ///  - A ticket requires <c>Money.cs</c>. The agent copies it to
    ///    <c>Money.cs.orig</c> and never edits the original. Porcelain says
    ///    <c> M Money.cs.orig</c>, <c>"money.cs"</c> is a substring of that,
    ///    the gate reports the path as TOUCHED and the run is accepted having
    ///    done none of the asked-for work. A false GREEN.
    ///  - A ticket requires <c>Money.cs</c> and the agent edits an unrelated
    ///    same-named file elsewhere in the tree, <c>src/vendor/Money.cs</c>.
    ///    Same false GREEN, no backup file needed.
    ///    ⛔ The defect writeup gave this second case as "requires
    ///    <c>Api/Money.cs</c>, edits <c>Api/MoneyFormatter.cs</c>", which is
    ///    simply wrong — <c>Api/Money.cs</c> is not a substring of
    ///    <c>Api/MoneyFormatter.cs</c>, and the assertion written to catch it
    ///    stayed GREEN against the OLD implementation in the power check.
    ///    Measured, not reasoned.
    ///
    /// Both are cases where the gate's own failure mode HIDES the failure it
    /// exists to catch, which is why this is worth parsing properly rather
    /// than tightening the substring.
    ///
    /// FORMAT HANDLED (porcelain v1): two status chars, one space, then the
    /// path; <c>ORIG -&gt; NEW</c> for renames and copies, where BOTH sides
    /// count as changed (the original moved away — that is a change to it);
    /// and git's C-style quoting for paths with spaces or non-ASCII, which
    /// <c>core.quotePath</c> turns on by default.
    ///
    /// ⚠ NOT switched to <c>-uall</c>, deliberately. Plain porcelain collapses
    /// a wholly-untracked directory to one <c>dir/</c> entry, so a required
    /// file inside a brand-new folder never appears by name;
    /// <see cref="PathIsTouched"/> handles that with a prefix rule instead.
    /// <c>-uall</c> would list them individually, but it also walks every
    /// untracked file in the tree — and this call already has a 30s timeout
    /// whose expiry is a fail-open, so making it slower on exactly the
    /// biggest workspaces trades a known bug for a load-dependent one.
    /// </summary>
    public static HashSet<string> ParsePorcelainPaths(string porcelain)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // "XY PATH" — two status chars then exactly one space. Anything
            // shorter is not an entry.
            if (line.Length < 4) continue;
            var rest = line[3..];

            // Renames and copies carry both sides. The separator only means a
            // rename when it sits outside quotes; a literal " -> " inside a
            // quoted filename would otherwise split one path into two.
            var arrow = IndexOfUnquoted(rest, " -> ");
            if (arrow >= 0)
            {
                AddPath(paths, rest[..arrow]);
                AddPath(paths, rest[(arrow + 4)..]);
            }
            else
            {
                AddPath(paths, rest);
            }
        }
        return paths;

        static void AddPath(HashSet<string> into, string p)
        {
            var norm = NormalizePath(Unquote(p));
            if (norm.Length > 0) into.Add(norm);
        }
    }

    /// <summary>
    /// Is <paramref name="required"/> among the paths git reported as changed?
    ///
    /// Equality on normalised paths, plus one prefix rule that is NOT a
    /// relaxation back toward substring matching: porcelain reports a wholly
    /// untracked DIRECTORY as a single <c>dir/</c> entry, so a required file
    /// inside a brand-new folder is genuinely changed but never named. The
    /// rule only fires on entries git itself marked as directories with a
    /// trailing slash, and only for paths beneath them — <c>src/new/</c>
    /// covers <c>src/new/Money.cs</c> and does not cover
    /// <c>src/newer/Money.cs</c>.
    ///
    /// The converse is supported too: a ticket may require a directory, in
    /// which case any changed path beneath it satisfies it.
    /// </summary>
    public static bool PathIsTouched(string required, IReadOnlyCollection<string> changedPaths)
    {
        var want = NormalizePath(required);
        if (want.Length == 0) return false;

        foreach (var got in changedPaths)
        {
            if (string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) return true;

            // git reported an untracked directory; the required file is under it.
            if (got.EndsWith('/')
                && want.StartsWith(got, StringComparison.OrdinalIgnoreCase)) return true;

            // the ticket required a directory; something under it changed.
            if (required.EndsWith('/') || required.EndsWith('\\'))
            {
                if (got.StartsWith(want.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// <see cref="PathIsTouched"/> against BOTH spellings of a required path:
    /// as written (repo-root-relative) and resolved through
    /// <paramref name="repoPrefix"/> (project-relative).
    ///
    /// ⛔ WHY THIS IS NOT A SUFFIX RULE. The tempting cheap fix for the two
    /// roots is "changed path ENDS WITH / + required path", which also makes
    /// the monorepo case work. It is wrong: it matches a same-named file
    /// ANYWHERE in the tree, so a ticket requiring <c>Money.cs</c> is
    /// satisfied by an edit to <c>src/vendor/Money.cs</c> — a false GREEN, the
    /// exact class of bug this parser was written to close. The prefix is a
    /// SINGLE KNOWN STRING that git told us, so both comparisons stay
    /// equality. An unresolvable prefix costs a false RED (a visible refusal
    /// naming the paths), never a false green.
    /// </summary>
    public static bool PathIsTouchedUnderPrefix(
        string required, IReadOnlyCollection<string> changedPaths, string repoPrefix)
    {
        if (PathIsTouched(required, changedPaths)) return true;
        if (repoPrefix.Length == 0) return false;
        // NormalizePath strips a leading "/" and "./" off the required path,
        // so concatenating the trailing-slashed prefix is well-defined.
        return PathIsTouched(repoPrefix + NormalizePath(required), changedPaths);
    }

    /// <summary>
    /// The sandbox cwd's path relative to the repo toplevel, slash-separated
    /// and trailing-slashed ("apps/proj/"), or "" at the toplevel.
    ///
    /// Same question and same semantics as
    /// <c>DispatchWorktreeManager.GetRepoPrefixAsync</c>, which cannot be
    /// reused here: that one shells out against a real directory, this one has
    /// to go through <see cref="ISandbox"/>.
    ///
    /// Best-effort by design. `rev-parse --show-prefix` is O(1) — no tree walk
    /// — so unlike the porcelain call it is not a timeout risk, but if it does
    /// fail we return "" and match repo-root-relative spellings only. That
    /// direction is deliberate: it can only ADD paths to the missing list.
    /// </summary>
    public static async Task<string> RepoPrefixAsync(
        ISandbox sandbox, string sessionId, CancellationToken ct)
    {
        try
        {
            var r = await sandbox.BashExecAsync(sessionId, "git rev-parse --show-prefix", 15, ct);
            if (r.TimedOut || r.ExitCode != 0) return "";
            var p = (r.Stdout ?? "").Trim().Replace('\\', '/').TrimStart('/');
            if (p.Length == 0) return "";
            return p.EndsWith('/') ? p : p + "/";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Separator search that ignores occurrences inside a git-quoted path.</summary>
    static int IndexOfUnquoted(string s, string sep)
    {
        var inQuotes = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && inQuotes) { i++; continue; }
            if (s[i] == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && i + sep.Length <= s.Length
                && string.CompareOrdinal(s, i, sep, 0, sep.Length) == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Undo git's C-style path quoting (`core.quotePath`, on by default).
    /// A path that is not quoted is returned unchanged.
    /// </summary>
    static string Unquote(string p)
    {
        p = p.Trim();
        if (p.Length < 2 || p[0] != '"' || p[^1] != '"') return p;

        var body = p[1..^1];
        var sb = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\') { sb.Append(body[i]); continue; }
            if (++i >= body.Length) break;
            switch (body[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                default:
                    // \NNN octal — git emits these for non-ASCII bytes.
                    if (body[i] >= '0' && body[i] <= '7' && i + 2 < body.Length)
                    {
                        var oct = body.Substring(i, 3);
                        try
                        {
                            sb.Append((char)Convert.ToInt32(oct, 8));
                            i += 2;
                        }
                        catch (Exception) { sb.Append(body[i]); }
                    }
                    else sb.Append(body[i]);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// One spelling for one path: forward slashes, no leading `./` or `/`,
    /// no surrounding whitespace. Case is PRESERVED — comparisons are
    /// case-insensitive (the original gate's semantics, and the right ones on
    /// the Windows dev box), but the stored spelling is git's.
    /// </summary>
    static string NormalizePath(string p)
    {
        var s = p.Trim().Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        return s.TrimStart('/');
    }

    /// <summary>
    /// Substrings a ticket declares its final diff MUST contain, from
    /// `VETT_REQUIRED_CONTENT` (';'-separated). Empty when unset.
    ///
    /// Why paths are not enough: split-ticket runs #53/#54 both landed
    /// `done` conf=95 with the required FILES modified and the gate suite
    /// green — but the actual asked-for switch cases absent. The model
    /// reliably edits the small file (records, registrations) and skips
    /// the hard mid-file cases; a path check cannot tell the difference,
    /// a content check can. Ticket tags: `expect-content:&lt;substring&gt;`.
    /// </summary>
    public static List<string> RequiredContent()
    {
        var raw = Environment.GetEnvironmentVariable("VETT_REQUIRED_CONTENT") ?? "";
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                  .Select(p => p.Trim())
                  .Where(p => p.Length > 0)
                  .Distinct(StringComparer.Ordinal)
                  .ToList();
    }

    /// <summary>
    /// Which required substrings are missing from the workspace's diff
    /// against HEAD. Fails open (empty) on any git failure, matching
    /// <see cref="UntouchedAsync"/> — and, like it, DISCARDING the reason.
    /// `declare_done` calls <see cref="MissingContentDetailedAsync"/> instead
    /// so an unmeasured gate is tagged rather than read as a pass.
    ///
    /// ⛔ ONLY ADDED LINES COUNT. A raw substring search over `git diff HEAD`
    /// reads the '-' (removed) and ' ' (context) lines too, so the gate
    /// passed when the required construct was DELETED — the exact opposite of
    /// what the ticket asked for — and passed again when the construct merely
    /// sat untouched in the context window around an unrelated edit. Both were
    /// silent false GREENs: no refusal, no fail-open tag, wrong work accepted.
    /// Matching only lines starting with '+' (excluding the '+++' file header)
    /// makes the check mean what its name says: the named code was ADDED here.
    ///
    /// ⚠ Content in a brand-new UNTRACKED file is not in `git diff HEAD` at
    /// all, so it reads as missing and the gate REFUSES. That is fail-CLOSED —
    /// a visible refusal naming the substring — NOT the "passes unchecked"
    /// this comment claimed before; that sentence described the wrong
    /// direction. Pair `expect-content` with content landing in a tracked
    /// file, or expect a refusal. Deliberately not "fixed" here: every
    /// portable route needs either a shell pipeline — and the sandbox falls
    /// back to `cmd` when Git Bash is absent (DirectBash.cs:98-100), where a
    /// pipeline exits non-zero and this method fails OPEN, re-creating the
    /// very false GREEN above — or an index mutation (`git add -N`).
    /// </summary>
    public static async Task<List<string>> MissingContentAsync(
        ISandbox sandbox, string sessionId, List<string> required, CancellationToken ct)
        => (await MissingContentDetailedAsync(sandbox, sessionId, required, ct)).Missing;

    /// <summary>
    /// <see cref="MissingContentAsync"/>, but reporting WHETHER IT COULD
    /// MEASURE — same fail-open-but-say-so contract as
    /// <see cref="UntouchedDetailedAsync"/>, including the `TimedOut` reading
    /// that was captured and never gated on.
    /// </summary>
    public static async Task<(List<string> Missing, GateFailOpen? FailedOpen)> MissingContentDetailedAsync(
        ISandbox sandbox, string sessionId, List<string> required, CancellationToken ct)
    {
        const string gate = "expect-content";
        try
        {
            var r = await sandbox.BashExecAsync(sessionId, "git diff HEAD", 60, ct);
            if (r.TimedOut)
                return ([], new GateFailOpen(gate, "`git diff HEAD` timed out after 60s"));
            if (r.ExitCode != 0)
                return ([], new GateFailOpen(gate, $"`git diff HEAD` exited {r.ExitCode} (not a git repo?)"));
            var added = string.Join('\n', (r.Stdout ?? "")
                .Split('\n')
                // "+++ " with the space, not "+++": git always writes its file
                // header as `+++ b/path`, while a genuinely added line whose
                // source text starts with "++" (`++counter;` at column 0)
                // becomes the diff line "+++counter;". Excluding the bare
                // prefix would drop that real addition and refuse the ticket.
                .Where(l => l.StartsWith('+') && !l.StartsWith("+++ ", StringComparison.Ordinal))
                .Select(l => l[1..]));
            return (required.Where(c => !added.Contains(c, StringComparison.Ordinal)).ToList(), null);
        }
        catch (Exception ex)
        {
            return ([], new GateFailOpen(gate, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    // 10, not 3: on task #50 every refusal drove a genuine correction (path
    // refusal -> the extractor finally got edited; verify refusals -> real
    // compile fixes), but the budget ran out mid-convergence and the RED tree
    // then sailed through fail-open as `done` conf=90 — the leader's final
    // summary literally said "The build now fails with new errors". The cap
    // exists only so a wedged run terminates; termination is cheap, a false
    // `done` is not.
    private const int MaxDeclareDoneRefusals = 10;
    private const int VerifyTimeoutSec = 900;   // real suites take minutes

    /// <summary>Last <paramref name="n"/> chars, so a refusal carries the
    /// compiler errors rather than the build's opening banner.</summary>
    private static string Tail(string s, int n) =>
        s.Length <= n ? s : "...\n" + s[^n..];

    /// <summary>
    /// Append the ticket's required files to a dispatch task.
    ///
    /// VETT_REQUIRED_PATHS gated the LEADER's declare_done but never reached
    /// the MEMBER, who had to infer its target from the leader's prose. On
    /// task #48 the leader rejected five dispatches with "diff showing only
    /// Ir.cs changes" — it knew the extractor was untouched — while the
    /// implementer, across eight dispatches, never once opened the file the
    /// ticket named. The leader knew; the member was never told.
    ///
    /// Injecting here (rather than trusting the leader to say it) means a
    /// leader that forgets, paraphrases, or splits the work cannot drop the
    /// requirement. Skipped when the task already names every path, so a
    /// well-worded dispatch isn't given a redundant tail.
    /// </summary>
    public static string WithRequiredFiles(string task)
    {
        if (task.Length == 0) return task;
        var result = task;

        var required = RequiredPaths();
        var missing = required
            .Where(p => !task.Replace('\\', '/').Contains(p.Replace('\\', '/'),
                                                          StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
        {
            result +=
                "\n\n--- REQUIRED FILES (from the ticket, injected by the harness) ---\n" +
                "This ticket cannot be completed without editing:\n" +
                string.Join("\n", required.Select(p => "  - " + p)) +
                "\nIf your part of the work touches one of these, you must OPEN and EDIT it. " +
                "Adding types, records, or tests elsewhere is not a substitute. " +
                "Before your final message, run `git status` and confirm the file(s) you " +
                "were asked to change appear as modified.";
        }

        // Same rationale one level deeper: #53/#54 members modified every
        // required FILE and still skipped the asked-for code inside it.
        // Tell the member exactly which strings the diff must end up
        // containing, so "edited the file" can't pass for "did the work".
        var content = RequiredContent();
        var missingContent = content
            .Where(c => !task.Contains(c, StringComparison.Ordinal))
            .ToList();
        if (missingContent.Count > 0)
        {
            result +=
                "\n\n--- REQUIRED CONTENT (from the ticket, injected by the harness) ---\n" +
                "The final diff must CONTAIN each of these (they are code that must exist " +
                "when you are done):\n" +
                string.Join("\n", content.Select(c => "  - " + c)) +
                "\nBefore your final message, run `git diff HEAD` and confirm each one appears.";
        }

        return result;
    }

    public static Dictionary<string, ToolFn> Create(
        TaskBoard board,
        Func<string, string, string, CancellationToken, Task<string>> runMember,
        Func<string, string, CancellationToken, Task<string>>? continueMember = null)
    {
        // Fresh budget per team construction. This is PER-Create state, not a
        // static: a static field was process-global, so (a) a TIER-3 nested
        // team's own Create() reset the OUTER leader's budget mid-run, handing
        // it an unlimited supply of refusals, and (b) two teams running
        // concurrently in one process (Runner.RunAsync fans out with a
        // SemaphoreSlim + Task.WhenAll) shared one counter — team B's refusals
        // could push team A across the cap and fail team A OPEN on a result the
        // harness never verified. The box is heap-allocated so the closures
        // below share one counter within this team only; increments go through
        // Interlocked because a leader can dispatch declare_done concurrently
        // with other tool calls (AgentLoop parallel dispatch, Task.WhenAll).
        var refusals = new int[1];
        var tools = new Dictionary<string, ToolFn>
        {
            ["assign_task"] = async (args, _, _, ct) =>
            {
                var (member, task) = (S(args, "member"), WithRequiredFiles(S(args, "task")));
                if (member == "" || task == "")
                    return "Error: member and task required";

                // ⭐ THE CEILING APPLIES HERE TOO, and leaving it off was the
                // first defect this fix pass shipped. `assign_task` blocks the
                // leader, so on its own it can only ever add ONE concurrent
                // member — but "only one" is still an overshoot: a leader with
                // its full ceiling of async work already in flight would get a
                // sync dispatch waved through on top, and `max_concurrent_dispatches: 2`
                // would quietly mean 3. A ceiling that is off by one in a way
                // nothing reports is the same false-green class as a negative
                // ceiling meaning unlimited.
                var t = board.TryCreate(member, task, out var inFlight);
                if (t is null)
                    return $"Error: dispatch limit reached — {inFlight} task(s) already in flight "
                        + $"and the limit is {board.MaxConcurrentDispatches}. This is a BLOCKING "
                        + "assignment, so there is no slot for it right now. "
                        + LimitAdvice(board);

                board.MarkRunning(t.Id);

                // Per-task CTS linked to the leader's ct so the leader
                // can interrupt this single task via cancel_task without
                // tearing down the whole turn.
                using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                board.RegisterCts(t.Id, taskCts);
                try
                {
                    var r = await runMember(t.Id, member, task, taskCts.Token);
                    board.Complete(t.Id, r);
                    return $"[{t.Id} — {member} done]\n{r}";
                }
                catch (OperationCanceledException)
                {
                    board.Fail(t.Id, "cancelled");
                    if (ct.IsCancellationRequested) throw;
                    return $"[{t.Id} — {member} cancelled]";
                }
                catch (Exception ex)
                {
                    board.Fail(t.Id, ex.Message);
                    return $"[{t.Id} — {member} FAILED]\n{ex.Message}";
                }
                finally
                {
                    board.UnregisterCts(t.Id);
                }
            },

            ["assign_async"] = (args, _, _, ct) =>
            {
                var (member, task) = (S(args, "member"), WithRequiredFiles(S(args, "task")));
                if (member == "" || task == "")
                    return Task.FromResult("Error: member and task required");

                // ⭐ THE WIDTH CEILING. Reserve-or-refuse in ONE lock acquisition
                // inside the board — this tool's own description invites the
                // model to make "multiple parallel calls", so a check here
                // followed by a create there would let every racer observe
                // `cap - 1` and every racer through.
                var t = board.TryCreate(member, task, out var inFlight);
                if (t is null)
                {
                    // A REFUSAL, NOT A THROW. Depth throws because it is decided
                    // before any work happens and nothing can rescue it. Width is
                    // a moment-in-time state whose correct answer is "wait for a
                    // slot" — something the leader can actually act on, and the
                    // first thing that has ever told a manager its budget.
                    return Task.FromResult(
                        $"Error: dispatch limit reached — {inFlight} task(s) already in flight and "
                        + $"the limit is {board.MaxConcurrentDispatches}. Do NOT retry immediately. "
                        + LimitAdvice(board));
                }

                // Linked CTS so the leader can cancel JUST this task
                // (via cancel_task) without affecting siblings or the
                // leader's own turn.
                var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                board.RegisterCts(t.Id, taskCts);
                _ = Task.Run(async () =>
                {
                    board.MarkRunning(t.Id);
                    try
                    {
                        var result = await runMember(t.Id, member, task, taskCts.Token);
                        board.Complete(t.Id, result);
                    }
                    catch (OperationCanceledException)
                    {
                        board.Fail(t.Id, "cancelled");
                    }
                    catch (Exception ex)
                    {
                        board.Fail(t.Id, ex.Message);
                    }
                    finally
                    {
                        board.UnregisterCts(t.Id);
                        taskCts.Dispose();
                    }
                }, ct);

                // ⛔ THE UNCAPPED STRING IS BYTE-IDENTICAL TO THE PRE-CEILING ONE.
                // Every measured arm ran uncapped; changing what the leader reads
                // back would make those runs unreproducible for a cosmetic gain.
                // The budget line appears ONLY once a ceiling is actually set.
                var budget = board.MaxConcurrentDispatches > 0
                    ? $" [{inFlight + 1}/{board.MaxConcurrentDispatches} dispatches in flight]"
                    : "";
                return Task.FromResult($"{t.Id} assigned to {member} (running in background; results auto-deliver, or use cancel_task to stop).{budget}");
            },

            ["cancel_task"] = (args, _, _, _) =>
            {
                var raw = S(args, "task_id");
                if (raw == "") return Task.FromResult("Error: task_id required");
                var id = board.ResolveTaskId(raw) ?? raw;
                var existed = board.Cancel(id);
                if (!existed)
                    return Task.FromResult($"Error: task_id {raw} not found or already settled. Available ids look like 'researcher-1', 'implementer-2'.");
                var note = id != raw ? $" (resolved '{raw}' → '{id}')" : "";
                return Task.FromResult($"{id} cancellation requested{note}. The task will stop at its next checkpoint and report as cancelled in the auto-delivered result.");
            },

            ["inject_into_task"] = (args, _, _, _) =>
            {
                var raw = S(args, "task_id");
                var msg = S(args, "message");
                if (raw == "" || msg == "")
                    return Task.FromResult("Error: task_id and message required");
                var id = board.ResolveTaskId(raw) ?? raw;
                var existing = board.Get(id);
                if (existing is null)
                    return Task.FromResult($"Error: task_id '{raw}' not found. Live ids look like 'researcher-1', 'implementer-2'.");
                if (existing.Status is TaskStatus.Completed or TaskStatus.Failed)
                    return Task.FromResult($"Error: {id} has already settled ({existing.Status}). Use continue_task instead.");
                var ok = board.Inject(id, msg);
                if (!ok)
                    return Task.FromResult($"Error: {id} has no live injection channel (already settling, or queue is full).");
                var note = id != raw ? $" (resolved '{raw}' → '{id}')" : "";
                return Task.FromResult($"{id} received the new directive{note}. It will be picked up at the member's next iteration boundary; the member can choose to pivot, integrate it, or acknowledge.");
            },

            ["check_task"] = (args, _, _, _) =>
            {
                var t = board.Get(S(args, "task_id"));
                return Task.FromResult(t is null ? "Error: not found" : TaskBoard.Format(t));
            },

            ["check_tasks"] = (_, _, _, _) =>
            {
                return Task.FromResult(TaskBoard.FormatAll(board.All()));
            },

            ["wait_task"] = async (args, _, _, ct) =>
            {
                var id = S(args, "task_id");
                try { await board.WaitAsync(id, ct); }
                catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
                var t = board.Get(id);
                return t is null ? "Error: not found" : TaskBoard.Format(t);
            },

            // declare_done is EVIDENCE-GATED. When VETT_REQUIRED_PATHS names
            // files the ticket must modify, a leader cannot declare success
            // while they sit unchanged.
            //
            // Why this exists: across shakedown tasks #44-#46 the leader
            // declared done at confidence 60, then 95, then 100 while the
            // required file was never touched once. On #46 it cited the
            // suite's UNTOUCHED baseline counts ("228/228") as proof its
            // implementation worked -- it had written no tests, so nothing
            // new ran. Self-report is not evidence. The diff is.
            //
            // Refusing returns the leader to the loop with a concrete next
            // action instead of escalating to a human. Capped, so a genuinely
            // stuck run still terminates and the worker's own expect-paths
            // gate can escalate it.
            ["declare_done"] = async (args, sandbox, sessionId, ct) =>
            {
                var summary = S(args, "summary", "Done.");

                // ⛔ A DOOR, NOT A SENTENCE: declare_done with work still in
                // flight is refused. EpicForge run 33 (2026-09-16, mc-driver
                // S4 arm 2): two of six leads called `assign_async` twice,
                // read "results auto-deliver", and on the very next turn
                // called declare_done with the summary "Dispatched both
                // tickets in parallel". The submit marker ended the run,
                // both implementers were killed at iteration 1-2, and the
                // task returned files_changed=0 after 243 s and 628 s of
                // seat time. Nothing in the harness said no. Every option a
                // harness lists is one a seat takes; this one is no longer
                // listed. The refusal names the ids and the two ways out
                // (wait_task / cancel_task) and spends the same refusal
                // budget as the evidence gates, so a board whose member is
                // wedged forever still terminates -- tagged, as the fail-open
                // path below already does.
                var inFlightTasks = board.All()
                    .Where(t => t.Status is TaskStatus.Pending or TaskStatus.Running)
                    .ToList();
                if (inFlightTasks.Count > 0
                    && Volatile.Read(ref refusals[0]) >= MaxDeclareDoneRefusals)
                    return $"{Builtins.SubmitMarker}{summary}\n" +
                           "[gate-failed-open: declare_done refusal budget exhausted with " +
                           $"{inFlightTasks.Count} dispatch(es) still in flight; " +
                           "this result was NOT verified by the harness]";
                if (inFlightTasks.Count > 0)
                {
                    var nFlight = Interlocked.Increment(ref refusals[0]);
                    var ids = string.Join(", ", inFlightTasks.Select(t => $"{t.Id} ({t.Member}, {t.Status})"));
                    return
                        $"Error: cannot declare done. {inFlightTasks.Count} dispatch(es) are still in flight: {ids}.\n" +
                        "Delegating is not finishing -- a member that has not reported back has " +
                        "produced nothing you can hand in, and declaring done now KILLS it mid-work. " +
                        "Call `wait_task` on each id (results are delivered to you when it finishes), " +
                        "review what came back, then declare done; or `cancel_task` any dispatch you no " +
                        "longer want. " +
                        $"(refusal {nFlight}/{MaxDeclareDoneRefusals})";
                }

                var required = RequiredPaths();
                var requiredContent = RequiredContent();
                if (required.Count == 0 && requiredContent.Count == 0)
                    return $"{Builtins.SubmitMarker}{summary}";
                // Fail-open still terminates a wedged run, but it must never
                // masquerade as a verified pass: tag the submission so the
                // queue worker (and any human reading the report) can see the
                // gates were exhausted, not satisfied. Task #50 landed a
                // 16-error tree as `done` conf=90 through the untagged path.
                if (Volatile.Read(ref refusals[0]) >= MaxDeclareDoneRefusals)
                    return $"{Builtins.SubmitMarker}{summary}\n" +
                           "[gate-failed-open: declare_done refusal budget exhausted; " +
                           "this result was NOT verified by the harness]";

                // Gates that could not measure. They still let the run finish
                // — wedging is worse — but the submission gets tagged so a
                // reader can tell a VERIFIED pass from an UNVERIFIED one.
                var failedOpen = new List<GateFailOpen>();

                List<string> untouched = [];
                if (required.Count > 0)
                {
                    var (miss, failOpen) = await UntouchedDetailedAsync(sandbox, sessionId, required, ct);
                    untouched = miss;
                    if (failOpen is not null) failedOpen.Add(failOpen);
                }
                if (untouched.Count > 0)
                {
                    var nPaths = Interlocked.Increment(ref refusals[0]);
                    return
                        "Error: cannot declare done. The ticket requires these files to be " +
                        $"modified, and they are unchanged: {string.Join(", ", untouched)}.\n" +
                        "A green test suite does not prove the work exists -- if you added no " +
                        "tests, the counts you see are the untouched baseline.\n" +
                        "Dispatch an implementer to make the change, verify it appears in " +
                        "`git status`, then declare done. " +
                        $"(refusal {nPaths}/{MaxDeclareDoneRefusals})";
                }

                // A modified file is necessary; the asked-for change inside it
                // is the point. Runs #53/#54 declared done at conf=95 with the
                // required files touched, the suite green, and the actual
                // switch cases absent — nothing new was tested, so nothing
                // failed. When the ticket names content, the diff must show it.
                List<string> missingContent = [];
                if (requiredContent.Count > 0)
                {
                    var (miss, failOpen) = await MissingContentDetailedAsync(sandbox, sessionId, requiredContent, ct);
                    missingContent = miss;
                    if (failOpen is not null) failedOpen.Add(failOpen);
                }
                if (missingContent.Count > 0)
                {
                    var nContent = Interlocked.Increment(ref refusals[0]);
                    return
                        "Error: cannot declare done. The ticket requires the change to " +
                        $"CONTAIN the following, and the diff against HEAD does not: " +
                        $"{string.Join(", ", missingContent)}.\n" +
                        "Modifying the file is necessary but not sufficient — the named " +
                        "code must actually appear. Dispatch an implementer to add it, " +
                        "confirm it shows in `git diff HEAD`, then declare done. " +
                        $"(refusal {nContent}/{MaxDeclareDoneRefusals})";
                }

                // Touching the file is necessary, not sufficient. Task #47 modified
                // every required path and left seven compiler errors behind. When
                // VETT_VERIFY_CMD names the repo's gate, run it: done means the
                // gate is green, not that a file has a timestamp.
                var verifyCmd = Environment.GetEnvironmentVariable("VETT_VERIFY_CMD");
                if (!string.IsNullOrWhiteSpace(verifyCmd))
                {
                    var v = await sandbox.BashExecAsync(sessionId, verifyCmd, VerifyTimeoutSec, ct);
                    if (v.TimedOut || v.ExitCode != 0)
                    {
                        var nVerify = Interlocked.Increment(ref refusals[0]);
                        var tail = Tail(v.Stdout ?? "", 1500);
                        return
                            $"Error: cannot declare done. The gate `{verifyCmd}` " +
                            (v.TimedOut ? "timed out" : $"failed (exit {v.ExitCode})") + ".\n" +
                            "Fix the failure and re-run it yourself before declaring done. " +
                            "Do not edit tests to make them pass.\n" +
                            $"--- last output ---\n{tail}\n" +
                            $"(refusal {nVerify}/{MaxDeclareDoneRefusals})";
                    }
                }

                if (failedOpen.Count > 0)
                    return $"{Builtins.SubmitMarker}{summary}\n" +
                           "[gate-failed-open: " +
                           string.Join("; ", failedOpen.Select(f => $"{f.Gate} gate could not measure — {f.Reason}")) +
                           "; this result was NOT verified by the harness]";

                return $"{Builtins.SubmitMarker}{summary}";
            },
        };

        // continue_task is only registered when the coordinator opted
        // in by providing a continueMember function. The interactive
        // chat path does; the one-shot benchmark path doesn't.
        if (continueMember is not null)
        {
            tools["continue_task"] = async (args, _, _, ct) =>
            {
                var (rawId, message) = (S(args, "task_id"), S(args, "message"));
                if (rawId == "" || message == "")
                    return "Error: task_id and message required";

                var taskId = board.ResolveTaskId(rawId) ?? rawId;
                var existing = board.Get(taskId);
                if (existing is null)
                    return $"Error: task_id '{rawId}' not found";

                // Defensive: if the task is still running, the leader
                // should be using inject_into_task instead. Returning a
                // misleading "no cached state" error here used to make
                // the leader spawn a duplicate task.
                if (existing.Status is TaskStatus.Pending or TaskStatus.Running)
                    return $"Error: {taskId} is still {existing.Status} — use inject_into_task to deliver a new directive without restarting the member. continue_task only works on settled tasks.";

                try
                {
                    var r = await continueMember(taskId, message, ct);
                    board.Complete(taskId, r);
                    return $"[{taskId} — {existing.Member} continued]\n{r}";
                }
                catch (OperationCanceledException)
                {
                    board.Fail(taskId, "cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    board.Fail(taskId, ex.Message);
                    return $"[{taskId} — continue FAILED]\n{ex.Message}";
                }
            };
        }

        return tools;

        // Same JsonElement handling as BuiltinTools.Str — without it, member/task
        // args from an OpenAI-compatible LLM arrive as JsonElement and look empty.
        static string S(Dictionary<string, object?> a, string k, string d = "")
        {
            if (!a.TryGetValue(k, out var v) || v is null) return d;
            if (v is string s) return s;
            if (v is System.Text.Json.JsonElement je)
                return je.ValueKind == System.Text.Json.JsonValueKind.String ? (je.GetString() ?? d) : je.ToString();
            return v.ToString() ?? d;
        }
    }

    public static readonly List<JsonElement> Schemas = new[]
    {
        // ⭐ THESE TWO DESCRIPTIONS STATE WHEN DELEGATING IS WORTH IT, not just
        // that it is possible (Mark, 2026-08-27: "a worker may not need a sub
        // worker ONLY if we tell it in these cases its useful to use one to save
        // context and be more effective but it may not always help it likely
        // depends"). The previous text said parallel calls "are supported — each
        // is its own instance", which is a capability note that reads as an
        // invitation: it gave the model no reason NOT to fan out, and the only
        // brake on width was the model's own restraint. Naming the trade-off
        // (context saved vs. a start-up and a summary round-trip paid) is the
        // half of the problem a numeric ceiling cannot fix — the ceiling stops a
        // leader going too wide, it cannot stop it delegating work that was
        // cheaper to just do.
        """{"type":"function","function":{"name":"assign_task","description":"Sync delegate to member — BLOCKS until the member finishes, so it costs you the wait as well as the dispatch. Spawns a fresh member instance with its own context. Use only when you cannot take another step without the result; otherwise prefer assign_async. See assign_async for when delegating is worth it at all.","parameters":{"type":"object","properties":{"member":{"type":"string"},"task":{"type":"string"}},"required":["member","task"]}}}""",
        """{"type":"function","function":{"name":"assign_async","description":"Async delegate to member. Returns task_id immediately; the member runs in the background in its OWN context window. WHEN DELEGATING PAYS: the sub-task is self-contained and large enough that doing it inline would fill your context with detail you do not need — you want its conclusion, not its working. WHEN IT DOES NOT: anything you could finish yourself in a few steps. Each dispatch costs a fresh agent start-up plus a summary round-trip, so for small work delegating is slower and produces a worse answer than doing it yourself. Parallel calls (even to the same member) are supported, but are only worth it when the sub-tasks are genuinely independent of each other.","parameters":{"type":"object","properties":{"member":{"type":"string"},"task":{"type":"string"}},"required":["member","task"]}}}""",
        """{"type":"function","function":{"name":"continue_task","description":"Resume a previously-completed (or running) task with a follow-up message. Reuses the same member instance so it remembers the prior conversation. Use this for follow-up clarifications instead of spawning a new researcher/implementer that would have to start over.","parameters":{"type":"object","properties":{"task_id":{"type":"string"},"message":{"type":"string"}},"required":["task_id","message"]}}}""",
        """{"type":"function","function":{"name":"check_task","description":"Check task status.","parameters":{"type":"object","properties":{"task_id":{"type":"string"}},"required":["task_id"]}}}""",
        """{"type":"function","function":{"name":"check_tasks","description":"List all tasks.","parameters":{"type":"object","properties":{}}}}""",
        """{"type":"function","function":{"name":"wait_task","description":"Wait for task completion. AVOID in chat mode — blocks the leader and freezes the chat. Results auto-deliver in a future turn.","parameters":{"type":"object","properties":{"task_id":{"type":"string"}},"required":["task_id"]}}}""",
        """{"type":"function","function":{"name":"cancel_task","description":"Cancel a running async task by id. Stops the member at its next checkpoint; the cancellation surfaces in the auto-delivered result. Use when the user asks to stop a task entirely.","parameters":{"type":"object","properties":{"task_id":{"type":"string"}},"required":["task_id"]}}}""",
        """{"type":"function","function":{"name":"inject_into_task","description":"Inject a new directive into an already-running async task without restarting it. The member preserves all of its prior context (system prompt, tool calls, partial conclusions) and sees the new message as a fresh user turn at its next iteration boundary — it can pivot, refine, or simply integrate the additional guidance. Use when the user adds new info, narrows scope, or redirects mid-flight (e.g. 'tell researcher-1 to also look at X', 'narrow that to just the auth module').","parameters":{"type":"object","properties":{"task_id":{"type":"string"},"message":{"type":"string"}},"required":["task_id","message"]}}}""",
        """{"type":"function","function":{"name":"declare_done","description":"Signal completion.","parameters":{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}}}""",
    }.Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToList();
}
