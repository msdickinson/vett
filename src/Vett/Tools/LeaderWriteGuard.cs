using System.Text.RegularExpressions;

namespace Vett.Tools;

/// <summary>
/// Makes the team-leader's "manager, not implementer" role STRUCTURAL by
/// rejecting terminal commands that write files.
///
/// WHY THIS EXISTS (measured, 2026-07-12/13):
/// The leader's system prompt says "You do NOT write code yourself." It does
/// not hold. With `file_editor` in its toolset, BOTH Flash and Pro leaders
/// edited the workspace directly instead of dispatching. Removing file_editor
/// from the profile-level `tools:` list fixed that completely — 0 leader
/// file_editor calls across 15 subsequent runs — but the leader still needs
/// `terminal` for the build gate, and terminal can write files too. In 2 of
/// those 15 runs the leader escaped through the shell:
///
///   dsv4-team-local-sizing / t2-2-sequential-pipeline:
///     cp "C:\Users\...\.vett\dispatches\<task>\..." <workspace>
///     -> the leader HAND-MERGED a dispatch worktree, bypassing
///        accept_dispatch entirely. The run PASSED — because it cheated.
///
///   dsv4-team-local-architect / t2-5-long-file-edit:
///     sed -i 's/max_connections = 100/max_connections = 250/' app.conf
///     -> the leader edited the file instead of dispatching. The run FAILED
///        on exactly the right assertions (pending_review, accept_dispatch).
///
/// A prompt could not hold this line; a tool boundary can. Same lesson as
/// file_editor: you cannot prompt a model out of using a capability you
/// handed it.
///
/// SCOPE: reads, builds and tests stay fully allowed — the leader MUST be
/// able to run `dotnet build` / `dotnet test` for its verify gate, and
/// `git ls-files` / `grep` / `cat` to orient. Only file-MUTATING commands are
/// rejected. Opt-in per profile via `team.leader_terminal_readonly: true`
/// (default false, so existing profiles are unaffected).
/// </summary>
public static class LeaderWriteGuard
{
    // stderr plumbing and output-discarding — NOT file writes.
    //   2>/dev/null   2>&1   &>/dev/null   >/dev/null
    //   2>nul         >NUL           <- the WINDOWS null device. Missing this
    //                                   made the guard block `git ls-files ...
    //                                   2>nul`, a pure READ (observed
    //                                   2026-07-13, t2-3-researcher-then-impl).
    //                                   Leaders run on Windows here; both
    //                                   spellings of "throw it away" must pass.
    private static readonly Regex StderrNoise = new(
        @"\d?>\s*&\s*\d|\d?>\s*/dev/null|\d?>\s*\bnul\b|&>\s*\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A redirect into anything that is not a null device writes a file.
    private static readonly Regex Redirect = new(
        @">>?\s*(?!/dev/null\b)(?!nul\b)[\w./\\$'""-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Commands that mutate files without needing a redirect.
    // `cp`/`mv` are here because the observed bypass was a `cp` out of a
    // dispatch worktree — that is a hand-merge, the exact thing
    // accept_dispatch exists to do.
    private static readonly Regex WriteCommand = new(
        @"(^|[\s;&|])(tee|dd|truncate|patch|install|cp|mv|rm|rmdir|mkdir|touch|chmod|chown|ln)(\s|$)"
        + @"|(^|[\s;&|])sed\s+(-\S*\s+)*-i"
        + @"|(^|[\s;&|])(git)\s+(apply|checkout|restore|merge|cherry-pick|stash|reset)(\s|$)",
        RegexOptions.Compiled);

    /// <summary>True when <paramref name="command"/> would create or modify a file.</summary>
    public static bool IsFileWrite(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (WriteCommand.IsMatch(command)) return true;
        var stripped = StderrNoise.Replace(command, " ");
        return Redirect.IsMatch(stripped);
    }

    private const string Refusal =
        "REJECTED by the leader write-guard: this command writes files, and you are the "
        + "TEAM LEADER — you do not write code yourself.\n"
        + "Your terminal is for READING and for the BUILD/TEST gate only "
        + "(dotnet build, dotnet test, git ls-files, grep, cat).\n"
        + "To change a file: assign_task(implementer, ...).\n"
        + "To merge a completed dispatch: accept_dispatch(task_id, review=\"...\") — "
        + "NEVER copy or patch files out of a dispatch worktree by hand.";

    /// <summary>
    /// Wraps the leader's `terminal` tool so file-writing commands are refused
    /// before they reach the sandbox. Mutates <paramref name="leaderTools"/> in
    /// place. No-op when the leader has no terminal tool.
    /// </summary>
    public static void Apply(
        Dictionary<string, ToolFn> leaderTools,
        Action<string, Dictionary<string, object?>>? emit = null)
    {
        if (!leaderTools.TryGetValue("terminal", out var inner)) return;

        leaderTools["terminal"] = (args, sandbox, sessionId, ct) =>
        {
            var command = args.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
            if (IsFileWrite(command))
            {
                emit?.Invoke("leader_write_blocked", new Dictionary<string, object?>
                {
                    ["command"] = command,
                });
                return Task.FromResult(Refusal);
            }
            return inner(args, sandbox, sessionId, ct);
        };
    }
}
