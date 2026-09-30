using System.Text.RegularExpressions;
using Vett.Agent;

namespace Vett.Bench.Team;

/// <summary>
/// Evaluates a list of assertions against the captured event stream
/// + workspace state from a single benchmark run. Pure-deterministic —
/// no LLM judge, no network calls, no flakiness.
/// </summary>
public static class AssertionEngine
{
    public static List<AssertionResult> Evaluate(
        List<TeamBenchAssertion> assertions,
        IReadOnlyList<Event> events,
        string workspaceDir,
        int leaderIterations,
        IReadOnlyDictionary<string, int> memberIterations,
        bool countsComplete,
        IReadOnlyList<string>? assistantTexts = null)
    {
        var texts = assistantTexts ?? Array.Empty<string>();
        var results = new List<AssertionResult>();
        foreach (var a in assertions)
        {
            results.Add(EvaluateOne(
                a, events, workspaceDir, leaderIterations, memberIterations, countsComplete, texts));
        }
        return results;
    }

    private static AssertionResult EvaluateOne(
        TeamBenchAssertion a,
        IReadOnlyList<Event> events,
        string workspaceDir,
        int leaderIterations,
        IReadOnlyDictionary<string, int> memberIterations,
        bool countsComplete,
        IReadOnlyList<string> assistantTexts)
    {
        if (a.Event is not null) return EvalEvent(a, events);
        if (a.NoEvent is not null) return EvalNoEvent(a, events);
        if (a.ToolCall is not null) return EvalToolCall(a, events);
        if (a.File is not null) return EvalFile(a, workspaceDir);
        if (a.Budget is not null) return EvalBudget(a, leaderIterations, memberIterations, countsComplete);
        if (a.AssistantTextContains is not null) return EvalAssistantTextContains(a, assistantTexts);
        if (a.RunCommand is not null) return EvalRunCommand(a.RunCommand, workspaceDir);
        if (a.DotnetTest is not null) return EvalDotnetTest(a.DotnetTest, workspaceDir);
        return new AssertionResult { Description = "(empty assertion)", Pass = false, Detail = "no kind set" };
    }

    private static AssertionResult EvalRunCommand(RunCommandSpec spec, string workspaceDir)
    {
        var desc = $"run_command: `{spec.Command}` (cwd={spec.Cwd}, exit={spec.ExitCode ?? 0})";
        var (cwd, escaped) = ResolveInside(workspaceDir, spec.Cwd);
        if (escaped)
            return Fail(desc, $"cwd escapes workspace: '{spec.Cwd}' resolved to '{cwd}', outside '{workspaceDir}'");
        var (exit, output, killed) = RunShell(spec.Command, cwd, spec.TimeoutSeconds);
        if (killed) return Fail(desc, $"timeout after {spec.TimeoutSeconds}s; output head: {Head(output, 200)}");
        var expectedExit = spec.ExitCode ?? 0;
        if (exit != expectedExit) return Fail(desc, $"exit={exit} expected={expectedExit}; output head: {Head(output, 200)}");
        if (spec.StdoutMatches is not null)
        {
            var rx = new Regex(spec.StdoutMatches, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!rx.IsMatch(output))
                return Fail(desc, $"stdout did not match /{spec.StdoutMatches}/; head: {Head(output, 300)}");
        }
        return Pass(desc);
    }

    private static AssertionResult EvalDotnetTest(DotnetTestSpec spec, string workspaceDir)
    {
        var desc = $"dotnet_test: cwd={spec.Cwd}";
        if (spec.MinPassed is not null) desc += $" min_passed={spec.MinPassed}";
        if (spec.MaxFailed is not null) desc += $" max_failed={spec.MaxFailed}";

        var (cwd, escaped) = ResolveInside(workspaceDir, spec.Cwd);
        if (escaped)
            return Fail(desc, $"cwd escapes workspace: '{spec.Cwd}' resolved to '{cwd}', outside '{workspaceDir}'");

        // --nologo cuts boilerplate; verbosity quiet keeps output small;
        // the console logger summary line is what we parse.
        var cmd = "dotnet test --nologo --verbosity quiet";
        var (exit, output, killed) = RunShell(cmd, cwd, spec.TimeoutSeconds);
        if (killed) return Fail(desc, $"timeout after {spec.TimeoutSeconds}s; output head: {Head(output, 400)}");

        // Parse passed/failed counts from the dotnet test summary. We look
        // for both the older "Passed: N" / "Failed: N" lines (VSTest console
        // logger) and the newer terraform-y "Passed:    N" with extra spaces.
        var summary = ParseTestSummaries(output);
        if (summary.Kind == SummaryKind.None)
        {
            // `dotnet test` EXITS 0 WHEN ZERO TESTS ARE DISCOVERED. An
            // exit-code-only assertion therefore scores an empty test project
            // GREEN — which is exactly why min_passed demands a parsed POSITIVE
            // count and this branch fails closed no matter which case it is.
            //
            // But WHICH case it is decides who to blame, and the two answers
            // are opposites:
            //   no tests discovered -> the ARTIFACT is defective (none written)
            //   summary unparseable -> the RULER is defective (false red on
            //                          code that is actually fine)
            // The old text said "could not parse", asserting the second when
            // the first was true. Name the evidence instead of guessing, and
            // say "cannot tell" when we cannot tell.
            var noTests = output.Contains("No test is available", StringComparison.OrdinalIgnoreCase)
                       || output.Contains("TestAdapterPath", StringComparison.OrdinalIgnoreCase);
            var diagnosis =
                noTests ? "ZERO TESTS DISCOVERED — vstest reported no runnable tests (note: `dotnet test` still exits 0 in this case, so exit code proves nothing here). The artifact is missing tests."
                : exit == 0 ? "NO TEST SUMMARY, exit=0 — CANNOT DISTINGUISH 'zero tests ran' from 'this ruler failed to parse a valid summary'. Read the output below before blaming the artifact."
                : "NO TEST SUMMARY, non-zero exit — the test project most likely failed to build, or the test host crashed.";
            // ⛔ CORRECTED 2026-08-24 06:0x, re-derived FROM THE STORED ARTIFACT.
            // This comment previously claimed the 400-char head had truncated
            // vstest's "No test is available in <dll>..." in the arm C run,
            // leaving only its tail. BOTH HALVES OF THAT ARE FALSE:
            //   * the stored detail is 467 chars = a 75-char prefix + 392 chars
            //     of output. 392 < 400, so it was NEVER TRUNCATED; it ends on a
            //     complete sentence.
            //   * it contains no "No test is available" AT ALL, tail or otherwise.
            // The REAL signature of an empty test project (verified by
            // enumerating the surviving workspace
            // empty-git-repo-20260824-103426-64a0db: tests/ held EXACTLY ONE
            // file, the .csproj, and ZERO .cs — while src/ had 15 source files)
            // is all three of:
            //     "A total of N test files matched the specified pattern."
            //   + the "/TestAdapterPath" hint
            //   + NO Passed:/Failed: summary line
            // ⚠ CONSEQUENCE FOR THE MARKERS ABOVE: "No test is available" did
            // NOT fire on the one real artifact we have. "TestAdapterPath" is
            // carrying the ENTIRE detection on its own. It LOOKS like the loose,
            // redundant belt next to the specific one — it is not. Deleting it
            // as "redundant" silently disables zero-test detection.
            // ⚠ And it is not merely loose-but-lucky here: the adapter WAS
            // correctly registered (xunit.runner.visualstudio 2.8.2,
            // Microsoft.NET.Test.Sdk 17.12.0), so "no tests written" is the
            // right blame, not "runner misconfigured".
            // The 1200-char head stays — more room to read is still right, just
            // not for the reason originally written here.
            return Fail(desc, $"{diagnosis}; exit={exit}; output head: {Head(output, 1200)}");
        }
        // ⛔ `dotnet test` on a SOLUTION prints ONE SUMMARY PER TEST PROJECT,
        // and Match() returns only the FIRST. The whole solution was therefore
        // graded on whichever project happened to print first — a green
        // Api.Tests ahead of a red Core.Tests scored PASS with 12 failures
        // still outstanding. A silent false GREEN: the ruler reported a number
        // that was true of one project and false of the run.
        // ⚠ SCOPE, so this is not read as wider than it is: every suite
        // bundled today seeds EXACTLY ONE test project, so the defect was
        // reachable but NOT TRIGGERED, and no historical result is
        // invalidated by this fix. It arms the moment a suite seeds two.
        // Sum every summary instead of trusting the first.
        //
        // Both counts sit on the SAME summary line ("Failed: N, Passed: M"),
        // so one `Passed:` per `Failed:` is the invariant. If they disagree,
        // something that is not a summary matched and the totals cannot be
        // explained — fail CLOSED rather than grade on a number we do not
        // understand. Failing open here is what the whole finding is about.
        if (summary.Kind == SummaryKind.Mismatched)
            return Fail(desc, $"UNPARSEABLE SUMMARY — {summary.PassedLines} 'Passed:' vs {summary.FailedLines} 'Failed:' matches; " +
                              "each summary line carries both, so these must pair. Refusing to grade; " +
                              $"exit={exit}; output head: {Head(output, 1200)}");

        var projects = summary.Projects;
        var passed = summary.Passed;
        var failed = summary.Failed;
        var across = projects > 1 ? $" (summed across {projects} test projects)" : "";

        if (spec.MaxFailed is int mf && failed > mf)
            return Fail(desc, $"failed={failed} > max_failed={mf}; passed={passed}{across}; output: {Head(output, 400)}");
        if (spec.MinPassed is int mp && passed < mp)
            return Fail(desc, $"passed={passed} < min_passed={mp}; failed={failed}{across}; output: {Head(output, 400)}");
        return Pass(desc + $" → passed={passed}, failed={failed}{across}");
    }

    internal enum SummaryKind
    {
        /// <summary>No summary line at all — caller decides whether that means
        /// "zero tests discovered" (artifact defective) or "this ruler failed
        /// to parse" (ruler defective); the two blame opposite parties.</summary>
        None,
        /// <summary>`Passed:` and `Failed:` match counts disagree, so what
        /// matched is not a set of summary lines. Totals are untrustworthy.</summary>
        Mismatched,
        Ok,
    }

    internal readonly record struct TestSummary(
        SummaryKind Kind, int Passed, int Failed, int Projects, int PassedLines, int FailedLines);

    private static readonly Regex PassedRx = new(@"Passed:\s*(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex FailedRx = new(@"Failed:\s*(\d+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Total passed/failed ACROSS EVERY test project in the output.
    ///
    /// Extracted from <c>EvalDotnetTest</c> so it can be proven directly: the
    /// caller shells out to `dotnet test`, which made the counting rule —
    /// the part that was wrong — reachable only by building a real solution
    /// on disk. A ruler nobody can test two-sided is a ruler nobody has
    /// checked, and this one had been wrong in the silent direction.
    /// </summary>
    internal static TestSummary ParseTestSummaries(string output)
    {
        var pms = PassedRx.Matches(output);
        var fms = FailedRx.Matches(output);
        if (pms.Count == 0 || fms.Count == 0)
            return new TestSummary(SummaryKind.None, 0, 0, 0, pms.Count, fms.Count);
        if (pms.Count != fms.Count)
            return new TestSummary(SummaryKind.Mismatched, 0, 0, 0, pms.Count, fms.Count);
        return new TestSummary(
            SummaryKind.Ok,
            pms.Sum(m => int.Parse(m.Groups[1].Value)),
            fms.Sum(m => int.Parse(m.Groups[1].Value)),
            pms.Count, pms.Count, fms.Count);
    }

    /// <summary>Run a shell command with output capture + a hard timeout
    /// kill. Returns (exitCode, combinedStdoutStderr, killed).</summary>
    private static (int exit, string output, bool killed) RunShell(string command, string cwd, int timeoutSeconds)
    {
        var isWin = OperatingSystem.IsWindows();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = isWin ? "cmd.exe" : "/bin/bash",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (isWin) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
        else       { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }

        var sb = new System.Text.StringBuilder();
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("could not start shell");
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var killed = !p.WaitForExit(timeoutSeconds * 1000);
        if (killed)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(2000); } catch { }
        }
        return (killed ? -1 : p.ExitCode, sb.ToString(), killed);
    }

    private static string Head(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    private static AssertionResult EvalAssistantTextContains(
        TeamBenchAssertion a, IReadOnlyList<string> assistantTexts)
    {
        var needle = a.AssistantTextContains!;
        var desc = $"assistant_text_contains: \"{needle}\"";
        if (assistantTexts.Count == 0)
            return Fail(desc, "no leader-to-user assistant text was emitted at all");
        foreach (var t in assistantTexts)
            if (t.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return Pass(desc);
        var head = string.Join(" | ", assistantTexts.Select(t => t.Length > 80 ? t[..80] + "…" : t));
        return Fail(desc, $"substring not found in {assistantTexts.Count} text turn(s): {head}");
    }

    private static AssertionResult EvalEvent(TeamBenchAssertion a, IReadOnlyList<Event> events)
    {
        var matches = events.Where(e => string.Equals(e.Type, a.Event, StringComparison.Ordinal)).ToList();
        var desc = $"event: {a.Event}";
        if (a.Member is not null) desc += $" member={a.Member}";
        if (a.FilesChangedMin is not null) desc += $" files_changed>={a.FilesChangedMin}";
        if (a.PendingReview is not null) desc += $" pending_review={a.PendingReview}";
        var min = a.MinCount ?? 1;
        if (a.MinCount is not null) desc += $" min_count={min}";

        if (matches.Count == 0)
            return Fail(desc, $"no event of type '{a.Event}' fired");

        // Apply field constraints — a matching event must satisfy ALL
        // constraints simultaneously. `min` of them must do so; the default
        // of 1 is the original "return on first match" behaviour.
        var satisfied = 0;
        foreach (var ev in matches)
        {
            if (a.Member is not null && !FieldEquals(ev, "member", a.Member)) continue;
            if (a.FilesChangedMin is not null)
            {
                var fc = FieldInt(ev, "files_changed");
                if (fc is null || fc < a.FilesChangedMin) continue;
            }
            if (a.PendingReview is not null)
            {
                var pr = FieldBool(ev, "pending_review");
                if (pr != a.PendingReview) continue;
            }
            satisfied++;
        }

        if (satisfied >= min) return Pass(desc);

        return satisfied == 0
            ? Fail(desc, $"{matches.Count} event(s) of type '{a.Event}' fired but none satisfied all field constraints")
            : Fail(desc, $"only {satisfied} of {matches.Count} '{a.Event}' event(s) satisfied all field constraints (need {min})");
    }

    private static AssertionResult EvalNoEvent(TeamBenchAssertion a, IReadOnlyList<Event> events)
    {
        var hits = events.Count(e => string.Equals(e.Type, a.NoEvent, StringComparison.Ordinal));
        var max = a.MaxCount ?? 0;
        var desc = max == 0
            ? $"no_event: {a.NoEvent}"
            : $"no_event: {a.NoEvent} max_count={max}";
        if (hits <= max)
            return hits > 0
                ? Pass(desc + $" (saw {hits}, tolerated)")
                : Pass(desc);
        return Fail(desc, $"{hits} '{a.NoEvent}' event(s) fired (max tolerated {max})");
    }

    private static AssertionResult EvalToolCall(TeamBenchAssertion a, IReadOnlyList<Event> events)
    {
        var matches = events
            .Where(e => e.Type == "tool_call_end" && FieldString(e, "tool_name") == a.ToolCall)
            .ToList();
        var desc = $"tool_call: {a.ToolCall}";
        if (a.Success is not null) desc += $" success={a.Success}";
        if (a.ArgsMinLength is not null && a.ArgsMinLength.Count > 0)
            desc += $" args_min_length={{{string.Join(", ", a.ArgsMinLength.Select(kv => $"{kv.Key}>={kv.Value}"))}}}";
        if (a.ArgsEquals is not null && a.ArgsEquals.Count > 0)
            desc += $" args_equals={{{string.Join(", ", a.ArgsEquals.Select(kv => $"{kv.Key}={kv.Value}"))}}}";
        var minCalls = a.MinCount ?? 1;
        if (a.MinCount is not null) desc += $" min_count={minCalls}";

        if (matches.Count == 0)
            return Fail(desc, $"no tool_call_end for '{a.ToolCall}'");

        // tool_call_end has the result, but args came in tool_call_start.
        // Find the matching start to inspect args.
        var starts = events
            .Where(e => e.Type == "tool_call_start" && FieldString(e, "tool_name") == a.ToolCall)
            .ToList();

        var satisfiedCalls = 0;
        foreach (var endEv in matches)
        {
            if (a.Success is not null && FieldBool(endEv, "success") != a.Success) continue;

            var hasArgsConstraint =
                (a.ArgsMinLength is not null && a.ArgsMinLength.Count > 0) ||
                (a.ArgsEquals is not null && a.ArgsEquals.Count > 0);

            if (hasArgsConstraint)
            {
                // Match start by call_id.
                var callId = FieldString(endEv, "call_id");
                var start = starts.FirstOrDefault(s => FieldString(s, "call_id") == callId);
                if (start is null) continue;
                if (!start.Data.TryGetValue("arguments", out var argsObj)) continue;
                var argsDict = argsObj switch
                {
                    Dictionary<string, object?> d => d,
                    System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Object =>
                        je.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value),
                    _ => null
                };
                if (argsDict is null) continue;
                var allArgsMet = true;
                if (a.ArgsMinLength is not null)
                {
                    foreach (var (key, minLen) in a.ArgsMinLength)
                    {
                        if (!argsDict.TryGetValue(key, out var val)) { allArgsMet = false; break; }
                        var s = val switch
                        {
                            string str => str,
                            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString() ?? "",
                            _ => val?.ToString() ?? "",
                        };
                        if (s.Length < minLen) { allArgsMet = false; break; }
                    }
                }
                if (allArgsMet && a.ArgsEquals is not null)
                {
                    foreach (var (key, expected) in a.ArgsEquals)
                    {
                        if (!argsDict.TryGetValue(key, out var val)) { allArgsMet = false; break; }
                        var s = val switch
                        {
                            string str => str,
                            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString() ?? "",
                            _ => val?.ToString() ?? "",
                        };
                        if (!string.Equals(s, expected, StringComparison.Ordinal)) { allArgsMet = false; break; }
                    }
                }
                if (!allArgsMet) continue;
            }

            satisfiedCalls++;
        }

        if (satisfiedCalls >= minCalls) return Pass(desc);

        return satisfiedCalls == 0
            ? Fail(desc, $"{matches.Count} '{a.ToolCall}' call(s) found but none satisfied constraints")
            : Fail(desc, $"only {satisfiedCalls} of {matches.Count} '{a.ToolCall}' call(s) satisfied constraints (need {minCalls})");
    }

    private static AssertionResult EvalFile(TeamBenchAssertion a, string workspaceDir)
    {
        var rel = a.File!;
        var shouldExist = a.Exists ?? true;
        var desc = $"file: {rel} exists={shouldExist}";
        if (a.ContentMatches is not null) desc += $" content~/{a.ContentMatches}/";

        // Workspace containment: reject paths that resolve outside
        // workspaceDir. Path.Combine quietly accepts absolute paths
        // for `rel` (returns rel verbatim) and `..` segments resolve
        // up out of the workspace — both are escape hatches that turn
        // bench assertions into false positives when an agent's cwd
        // drifts. Refuse and fail loudly.
        var (full, escaped) = ResolveInside(workspaceDir, rel);
        if (escaped)
            return Fail(desc, $"path escapes workspace: '{rel}' resolved to '{full}', outside '{workspaceDir}'");

        var exists = System.IO.File.Exists(full);
        if (exists != shouldExist)
            return Fail(desc, exists ? $"file exists but should not ({full})" : $"file not found ({full})");

        if (a.ContentMatches is not null && exists)
        {
            string content;
            try { content = System.IO.File.ReadAllText(full); }
            catch (Exception ex) { return Fail(desc, $"could not read file: {ex.Message}"); }

            // Normalize CRLF -> LF before matching. Git's autocrlf can put
            // \r\n in worktree files on Windows; .NET regex Multiline mode's
            // $ anchor matches before \n but not before \r\n, which makes
            // line-anchored regexes silently fail on otherwise-correct files.
            content = content.Replace("\r\n", "\n");

            var rx = new Regex(a.ContentMatches,
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (!rx.IsMatch(content))
            {
                var head = content.Length > 120 ? content[..120] + "…" : content;
                return Fail(desc, $"content didn't match. head: {head!}");
            }
        }
        return Pass(desc);
    }

    /// <summary>
    /// A budget assertion asks "did anyone exceed the cap?". Answering it
    /// requires the iteration counters to BE the counts — and on an aborted
    /// run they are not.
    ///
    /// ⛔ WHY <paramref name="countsComplete"/> IS HERE. `PartialCountsMarker`
    /// (Harness.cs) already stamps aborted runs with a note whose own text
    /// ends "...do not pool these counts with completed runs and do not
    /// evaluate a budget assertion against them." Nothing enforced that half.
    /// The law was ADOPTED — written down, published on the wire — but never
    /// INSTALLED as a check at the point of use, so `EvalBudget` went on
    /// reading lower bounds as measurements and returning PASS. This is that
    /// check.
    ///
    /// THE DIRECTIONS ARE NOT SYMMETRIC, and the order below encodes that:
    ///  - a lower bound that ALREADY exceeds the cap is a SOUND violation —
    ///    the true count is only higher — so the over-cap tests run FIRST and
    ///    report the violation itself, not the unmeasurability;
    ///  - a lower bound UNDER the cap proves nothing at all, so it must not
    ///    pass. That is the vacuous green this fixes.
    ///
    /// ⚠ AN EMPTY MEMBER DICTIONARY IS NOT THE SAME BUG. With FINAL counters
    /// it is a real measurement — no member ever ran an iteration — so it
    /// passes. But it passes CARRYING |members| = 0, because a bare
    /// "budget: member&lt;=22 ✅" row is read as "the members stayed under 22"
    /// when what happened is that there were none. An empty set is not a
    /// falsified set; the row has to say which one it is.
    /// </summary>
    private static AssertionResult EvalBudget(
        TeamBenchAssertion a,
        int leaderIterations,
        IReadOnlyDictionary<string, int> memberIterations,
        bool countsComplete)
    {
        var b = a.Budget!;
        var desc = "budget:";
        if (b.LeaderItersMax is not null) desc += $" leader<={b.LeaderItersMax}";
        if (b.MemberItersMax is not null) desc += $" member<={b.MemberItersMax}";

        // Sound in both worlds: over the cap is over the cap.
        if (b.LeaderItersMax is not null && leaderIterations > b.LeaderItersMax)
            return Fail(desc, $"leader ran {leaderIterations} iters (cap {b.LeaderItersMax})");

        if (b.MemberItersMax is not null && memberIterations.Count > 0)
        {
            var maxMember = memberIterations.Values.Max();
            if (maxMember > b.MemberItersMax)
            {
                var who = memberIterations.OrderByDescending(kv => kv.Value).First().Key;
                // ⚠ THE NUMBER IS A SUBTREE TOTAL, AND THE SENTENCE HAS TO SAY SO.
                // memberIterations is keyed on the iteration_start thread_id
                // (Harness.IterationThreadId), and TaggedEmit rewrites a nested
                // child's thread_id to its PARENT's task id. So a member that
                // dispatched a sub-team is credited with its own iterations PLUS
                // every iteration underneath it. "member X ran 255 iters" read as
                // one agent looping 255 times is the wrong diagnosis and sends
                // the reader after a stuck agent loop.
                //
                // MEASURED 2026-08-26 (ds-manager-pro-w10): feature-lead-d-2 shows
                // 255 here; dispatch_end.iterations for that dispatch is 60, and
                // its 25 sub-workers account for the other 195 — 60 + 195 = 255
                // exactly. The defect was a lead serially re-dispatching workers,
                // not an agent that would not stop. Totals still reconcile: the
                // sum over memberIterations equals the sum over every
                // dispatch_end.iterations (1117 = 1117), because each iteration is
                // counted once and merely attributed upward.
                //
                // The leading clause is kept byte-identical on purpose —
                // BudgetAssertionCompletenessTests matches on it, and downstream
                // greps predate this note.
                return Fail(desc,
                    $"member {who} ran {maxMember} iters (cap {b.MemberItersMax})"
                  + $" — NOTE: that count is the SUBTREE total for {who} (its own iterations PLUS "
                  + "every iteration of every sub-team it dispatched, because nested iteration_start "
                  + "events are re-tagged to the parent's thread_id). It is NOT one agent looping "
                  + $"{maxMember} times; read dispatch_end.iterations for the seat's own count, and "
                  + "the per-lead dispatch fan-out for the sub-teams it spawned.");
            }
        }

        // Under the cap, on counters that were still moving when we read them.
        // Scoped to a DECLARED cap: an assertion that asked nothing about the
        // counts is not made unmeasurable by them. (Caught by its own failure
        // test — the first cut refused every aborted run outright.)
        if (!countsComplete && (b.LeaderItersMax is not null || b.MemberItersMax is not null))
            return Fail(desc,
                $"{Harness.PartialCountsMarker}: the run aborted before the iteration "
                + $"counters were final, so leader_iterations>={leaderIterations} and "
                + $"member_iterations[{DescribeMembers(memberIterations)}] are LOWER "
                + "BOUNDS. Being under a cap CANNOT be certified from a lower bound — "
                + "this is a COULD-NOT-MEASURE, not a satisfied budget.");

        // Final counters, and nobody was dispatched.
        if (b.MemberItersMax is not null && memberIterations.Count == 0)
            return Pass(desc + " [|members| = 0 — no member iterations existed to bound]");

        return Pass(desc);
    }

    private static string DescribeMembers(IReadOnlyDictionary<string, int> members)
        => members.Count == 0
            ? "none observed"
            : string.Join(", ", members
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}>={kv.Value}"));

    // ---------- workspace containment -----------------------------------

    /// <summary>
    /// Resolves a user-supplied relative path against the workspace root,
    /// then verifies the resolved path is INSIDE the workspace. Returns
    /// (full, escaped) where escaped=true means the path resolved outside
    /// the workspace (absolute path, .. traversal, etc.) and the caller
    /// should fail the assertion rather than touching the resolved path.
    ///
    /// Path.Combine alone is not safe: if the second arg is absolute,
    /// .NET silently returns it verbatim; '..' segments can escape via
    /// normal path resolution. Both turn bench assertions into false
    /// positives when an agent's cwd drifts. We canonicalize and
    /// prefix-check.
    /// </summary>
    private static (string full, bool escaped) ResolveInside(string workspaceDir, string rel)
    {
        var combined = System.IO.Path.IsPathRooted(rel)
            ? rel
            : System.IO.Path.Combine(workspaceDir, rel);
        var full = System.IO.Path.GetFullPath(combined);
        var rootFull = System.IO.Path.GetFullPath(workspaceDir);

        // Normalize trailing separators so the prefix check is robust.
        var rootWithSep = rootFull.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

        // Allow full == rootFull (workspace root itself is "inside"),
        // and any descendant with the separator-prefix.
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var escaped = !(string.Equals(full, rootFull, cmp) || full.StartsWith(rootWithSep, cmp));
        return (full, escaped);
    }

    // ---------- field accessors -----------------------------------------

    private static string? FieldString(Event ev, string key)
    {
        if (!ev.Data.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            string s => s,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString(),
            _ => v.ToString(),
        };
    }

    private static int? FieldInt(Event ev, string key)
    {
        if (!ev.Data.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt32(),
            _ => int.TryParse(v.ToString(), out var n) ? n : null,
        };
    }

    private static bool? FieldBool(Event ev, string key)
    {
        if (!ev.Data.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            bool b => b,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.False => false,
            _ => bool.TryParse(v.ToString(), out var b) ? b : null,
        };
    }

    private static bool FieldEquals(Event ev, string key, string expected)
        => string.Equals(FieldString(ev, key), expected, StringComparison.Ordinal);

    private static AssertionResult Pass(string desc) => new() { Description = desc, Pass = true };
    private static AssertionResult Fail(string desc, string detail) => new() { Description = desc, Pass = false, Detail = detail };
}
