using Vett.Agent;
using Vett.Sandbox;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Failure tests for the `declare_done` REQUIRED-PATHS gate's path matching.
///
/// WHY THIS FILE EXISTS. The gate asked
/// <c>wholePorcelainOutput.Contains(requiredPath)</c>. Substring containment
/// is not path equality, and the gap is not a corner case — it is exactly the
/// shape of the behaviour the gate exists to catch:
///
///  - Ticket requires <c>Money.cs</c>. The agent copies it to
///    <c>Money.cs.orig</c> and never edits the original. Porcelain reports
///    <c> M Money.cs.orig</c>; <c>"money.cs"</c> is a substring of that line;
///    the gate reports the required path as TOUCHED. The run is accepted
///    having done none of the asked-for work.
///  - Ticket requires <c>Api/Money.cs</c>, the agent edits
///    <c>Api/MoneyFormatter.cs</c>. Same false GREEN with no backup file
///    involved — one required path being a prefix of an unrelated one is
///    enough.
///
/// Both are SILENT false GREENs: no refusal, and (since F3) no fail-open tag
/// either, because the gate believed it measured successfully. That is the
/// worst of the three outcomes — worse than the untagged fail-open F3 fixed,
/// because nothing in the output hints that the answer is unreliable.
///
/// DIRECTION OF THE FIX: strictly GREEN→RED. Every case below that changes
/// behaviour turns a former pass into a refusal. Nothing that previously
/// refused now passes.
///
/// The healthy controls are load-bearing. A matcher that answered "not
/// touched" to everything would satisfy every failure test here and refuse
/// every real run; <c>An_exactly_matching_path_IS_touched</c>,
/// <c>An_untracked_new_file_IS_touched</c> and the rename and normalisation
/// tests are what stop that.
/// </summary>
public class LeaderToolsPorcelainPathTests
{
    // ---- section 1: the two false GREENs, isolated ----

    [Fact]
    public void A_BACKUP_COPY_does_not_count_as_touching_the_original()
    {
        // The headline defect. The agent's work product is a copy; the file
        // the ticket named was never edited.
        var changed = LeaderTools.ParsePorcelainPaths(" M Money.cs.orig\n?? notes.txt\n");

        Assert.False(LeaderTools.PathIsTouched("Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("Money.cs.orig", changed));
    }

    [Fact]
    public void A_SAME_NAMED_FILE_ELSEWHERE_IN_THE_TREE_does_not_count()
    {
        // Ticket names the root `Money.cs`; the agent edits a different file
        // that merely ends the same way. No backup file needed — the required
        // path is a genuine substring of an unrelated real one.
        var changed = LeaderTools.ParsePorcelainPaths(" M src/vendor/Money.cs\n");

        Assert.False(LeaderTools.PathIsTouched("Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("src/vendor/Money.cs", changed));
    }

    [Fact]
    public void A_PREFIX_SIBLING_never_fired_and_this_test_has_NO_POWER()
    {
        // ⛔ KEPT AS A CORRECTION, NOT AS A GUARD. The defect writeup gave a
        // second example — "required Api/Money.cs, edited Api/MoneyFormatter.cs"
        // — and it is WRONG: `Api/Money.cs` is not a substring of
        // `Api/MoneyFormatter.cs`, because the required path carries the `.cs`
        // the sibling interrupts. The old code answered this case CORRECTLY.
        //
        // Measured, not reasoned: this assertion was written expecting to go
        // red in the power check and stayed GREEN under the reverted
        // implementation, which is what exposed the error in the writeup.
        // A_SAME_NAMED_FILE_ELSEWHERE_IN_THE_TREE_does_not_count above is the
        // real second failure mode and does have power.
        var changed = LeaderTools.ParsePorcelainPaths(" M Api/MoneyFormatter.cs\n");

        Assert.False(LeaderTools.PathIsTouched("Api/Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("Api/MoneyFormatter.cs", changed));
    }

    [Fact]
    public void The_STATUS_CHARACTERS_are_not_part_of_the_path()
    {
        // Under substring matching the status column was matchable text like
        // any other. It is not a path and must not be one.
        var changed = LeaderTools.ParsePorcelainPaths(" M Money.cs\n");

        Assert.Contains("Money.cs", changed);
        Assert.DoesNotContain(" M Money.cs", changed);
        Assert.False(LeaderTools.PathIsTouched("M Money.cs", changed));
    }

    // ---- section 2: healthy controls — what must still count ----

    [Fact]
    public void An_exactly_matching_path_IS_touched()
        => Assert.True(LeaderTools.PathIsTouched(
            "Money.cs", LeaderTools.ParsePorcelainPaths(" M Money.cs\n")));

    [Fact]
    public void An_untracked_new_file_IS_touched()
    {
        // A brand-new file is real work and has always had to count.
        var changed = LeaderTools.ParsePorcelainPaths("?? src/Money.cs\n");
        Assert.True(LeaderTools.PathIsTouched("src/Money.cs", changed));
    }

    [Fact]
    public void A_STAGED_change_IS_touched()
    {
        // "M " (staged) vs " M" (unstaged) vs "MM" (both) — all are changes.
        foreach (var xy in new[] { "M ", " M", "MM", "A ", "AM", "D " })
        {
            var changed = LeaderTools.ParsePorcelainPaths($"{xy} Money.cs\n");
            Assert.True(LeaderTools.PathIsTouched("Money.cs", changed),
                        $"status '{xy}' should count as a change");
        }
    }

    [Fact]
    public void BACKSLASHES_and_leading_dot_slash_normalise_to_one_spelling()
    {
        var changed = LeaderTools.ParsePorcelainPaths(" M Api/Money.cs\n");

        Assert.True(LeaderTools.PathIsTouched(@"Api\Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("./Api/Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("/Api/Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("  Api/Money.cs  ", changed));
    }

    [Fact]
    public void Matching_stays_CASE_INSENSITIVE()
    {
        // Preserved from the original gate deliberately: the dev box is
        // Windows, and a case mismatch there is a spelling difference, not a
        // different file.
        var changed = LeaderTools.ParsePorcelainPaths(" M Api/Money.cs\n");
        Assert.True(LeaderTools.PathIsTouched("api/money.cs", changed));
    }

    // ---- section 3: renames, quoting, and the untracked-directory case ----

    [Fact]
    public void A_RENAME_touches_BOTH_sides()
    {
        // The original moved away — that IS a change to it — and the new path
        // exists. A ticket naming either one has been satisfied.
        var changed = LeaderTools.ParsePorcelainPaths("R  Money.cs -> Cash.cs\n");

        Assert.True(LeaderTools.PathIsTouched("Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("Cash.cs", changed));
    }

    [Fact]
    public void A_QUOTED_path_with_a_space_is_unquoted()
    {
        // core.quotePath is on by default, so this is the normal rendering,
        // not an exotic one.
        var changed = LeaderTools.ParsePorcelainPaths("?? \"my notes.txt\"\n");

        Assert.True(LeaderTools.PathIsTouched("my notes.txt", changed));
        Assert.False(LeaderTools.PathIsTouched("\"my notes.txt\"", changed));
    }

    [Fact]
    public void A_QUOTED_path_containing_the_rename_arrow_is_not_split()
    {
        // " -> " inside a quoted filename is a filename, not a separator.
        var changed = LeaderTools.ParsePorcelainPaths("?? \"a -> b.txt\"\n");

        Assert.True(LeaderTools.PathIsTouched("a -> b.txt", changed));
        Assert.False(LeaderTools.PathIsTouched("a", changed));
        Assert.False(LeaderTools.PathIsTouched("b.txt", changed));
    }

    [Fact]
    public void An_UNTRACKED_DIRECTORY_covers_the_files_beneath_it()
    {
        // Plain porcelain collapses a wholly-new folder to one `dir/` entry,
        // so the required file is genuinely changed but never named. Without
        // this rule the gate would refuse correct work.
        var changed = LeaderTools.ParsePorcelainPaths("?? src/new/\n");

        Assert.True(LeaderTools.PathIsTouched("src/new/Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("src/new/deep/Money.cs", changed));
    }

    [Fact]
    public void The_untracked_DIRECTORY_rule_is_not_a_substring_rule_in_disguise()
    {
        // The guard on the relaxation above: `src/new/` must not cover
        // `src/newer/`. If this ever goes green the prefix rule has decayed
        // back into the defect this file exists to prevent.
        var changed = LeaderTools.ParsePorcelainPaths("?? src/new/\n");

        Assert.False(LeaderTools.PathIsTouched("src/newer/Money.cs", changed));
        Assert.False(LeaderTools.PathIsTouched("src/newest.cs", changed));
    }

    [Fact]
    public void A_ticket_may_require_a_DIRECTORY()
    {
        var changed = LeaderTools.ParsePorcelainPaths(" M src/Api/Money.cs\n");

        Assert.True(LeaderTools.PathIsTouched("src/Api/", changed));
        Assert.False(LeaderTools.PathIsTouched("src/Web/", changed));
    }

    [Fact]
    public void Empty_and_ragged_porcelain_yields_no_paths()
    {
        Assert.Empty(LeaderTools.ParsePorcelainPaths(""));
        Assert.Empty(LeaderTools.ParsePorcelainPaths("\n\n\n"));
        Assert.Empty(LeaderTools.ParsePorcelainPaths(" M\n"));   // too short to hold a path
        Assert.False(LeaderTools.PathIsTouched("Money.cs", LeaderTools.ParsePorcelainPaths("")));
        Assert.False(LeaderTools.PathIsTouched("", LeaderTools.ParsePorcelainPaths(" M Money.cs\n")));
    }

    [Fact]
    public void CRLF_line_endings_do_not_become_part_of_the_path()
    {
        // The sandbox falls back to cmd when Git Bash is absent
        // (DirectBash.cs:98-100), so CRLF is a real case on this dev box, not
        // a hypothetical. A trailing \r would make every path miss.
        var changed = LeaderTools.ParsePorcelainPaths(" M Money.cs\r\n?? notes.txt\r\n");

        Assert.True(LeaderTools.PathIsTouched("Money.cs", changed));
        Assert.True(LeaderTools.PathIsTouched("notes.txt", changed));
    }

    // ---- section 4: end to end through the gate itself ----

    /// <summary>
    /// ⚠ It answers `rev-parse` SEPARATELY. This class used to return the
    /// porcelain blob to every command, which meant the gate's prefix lookup
    /// parsed a prefix out of the STATUS OUTPUT. It happened to match nothing,
    /// so every test still passed — a fake agreeing with itself, not a
    /// measurement. <paramref name="prefix"/> defaults to "" (repo toplevel).
    /// </summary>
    private sealed class PorcelainSandbox(string porcelain, string prefix = "") : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(cmd.Contains("rev-parse")
                ? new BashResult(prefix, 0, "/fake", false)
                : new BashResult(porcelain, 0, "/fake", false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SessionCreateAsync(string n, string cwd, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string cwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }

    [Fact]
    public async Task The_GATE_reports_the_backup_copy_case_as_MISSING_and_is_NOT_blind()
    {
        // The full contract in one assertion pair: the gate must call this
        // MISSING (a visible refusal), and must NOT tag it as unmeasured —
        // git answered fine, the answer is just "no". Confusing the two would
        // swap a correct refusal for a fail-open tag.
        var sandbox = new PorcelainSandbox(" M Money.cs.orig\n");

        var (missing, failedOpen) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["Money.cs"], default);

        Assert.Equal(["Money.cs"], missing);
        Assert.Null(failedOpen);
    }

    [Fact]
    public async Task The_GATE_is_satisfied_by_a_real_edit()
    {
        var sandbox = new PorcelainSandbox(" M Money.cs\n?? Money.cs.orig\n");

        var (missing, failedOpen) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["Money.cs"], default);

        Assert.Empty(missing);
        Assert.Null(failedOpen);
    }

    [Fact]
    public async Task The_GATE_reports_EVERY_missing_path_not_just_the_first()
    {
        var sandbox = new PorcelainSandbox(" M src/vendor/Money.cs\n");

        var (missing, _) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["Money.cs", "Cash.cs", "src/vendor/Money.cs"], default);

        Assert.Equal(["Money.cs", "Cash.cs"], missing);
    }

    // ---- section 5: the TWO ROOTS ----
    //
    // Porcelain paths are repo-toplevel-relative whatever the cwd (RAN: a
    // throwaway repo, `git status --porcelain` from a subdirectory, identical
    // output to the root). Ticket paths are project-relative. The substring
    // gate bridged that by accident; equality has to bridge it on purpose,
    // using the one prefix string git reports.
    //
    // POWER, MEASURED TWO WAYS (not reasoned):
    //  - drop the prefix resolution  => 7 red, all of them the tests that
    //    assert the prefix DOES something; every guard below stayed green.
    //  - swap it for the cheap "suffix at a segment boundary" fix => 4 red,
    //    led by The_PREFIX_rule_is_NOT_A_SUFFIX_RULE.
    // ⚠ The section-1 unit test A_SAME_NAMED_FILE_ELSEWHERE_IN_THE_TREE_
    //   does_not_count did NOT fire against the suffix fix. It calls
    //   PathIsTouched directly, so it has no power over the composition in
    //   PathIsTouchedUnderPrefix. The end-to-end tests here are what cover
    //   that seam — a unit test one layer down is not a substitute.

    [Fact]
    public async Task A_PROJECT_RELATIVE_required_path_RESOLVES_THROUGH_THE_CWD_PREFIX()
    {
        var sandbox = new PorcelainSandbox(
            " M apps/proj/src/Money.cs\n", prefix: "apps/proj/");

        var (missing, failedOpen) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["src/Money.cs"], default);

        Assert.Empty(missing);
        Assert.Null(failedOpen);
    }

    [Fact]
    public async Task A_REPO_RELATIVE_required_path_STILL_MATCHES_when_a_prefix_exists()
    {
        // Ticket authors write either spelling. Both must work — the prefix
        // is an ADDITIONAL candidate, not a replacement.
        var sandbox = new PorcelainSandbox(
            " M apps/proj/src/Money.cs\n", prefix: "apps/proj/");

        var (missing, _) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["apps/proj/src/Money.cs"], default);

        Assert.Empty(missing);
    }

    [Fact]
    public async Task The_PREFIX_rule_is_NOT_A_SUFFIX_RULE_a_same_named_file_under_ANOTHER_prefix_is_MISSING()
    {
        // ⛔ THE GUARD ON THE CHEAP FIX. "ends with / + required" would make
        // this pass and is the obvious way to make two roots meet; it is a
        // false GREEN, because the edit is in a different project entirely.
        var sandbox = new PorcelainSandbox(
            " M apps/OTHER/src/Money.cs\n", prefix: "apps/proj/");

        var (missing, failedOpen) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["src/Money.cs"], default);

        Assert.Equal(["src/Money.cs"], missing);
        Assert.Null(failedOpen);
    }

    [Fact]
    public async Task The_BACKUP_COPY_case_stays_MISSING_even_with_a_prefix_resolved()
    {
        // The prefix must not become a second chance for the substring bug.
        var sandbox = new PorcelainSandbox(
            " M apps/proj/Money.cs.orig\n", prefix: "apps/proj/");

        var (missing, _) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["Money.cs"], default);

        Assert.Equal(["Money.cs"], missing);
    }

    [Fact]
    public async Task An_UNRESOLVABLE_prefix_fails_toward_MISSING_not_toward_satisfied()
    {
        // rev-parse exits non-zero / times out => prefix "". The project-
        // relative path then matches nothing, which is a VISIBLE refusal.
        // The point of the assertion is the DIRECTION, not the inconvenience.
        var sandbox = new PorcelainSandbox(" M apps/proj/src/Money.cs\n");

        var (missing, failedOpen) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["src/Money.cs"], default);

        Assert.Equal(["src/Money.cs"], missing);
        // Still MEASURED: git status answered. Only the prefix was unavailable.
        Assert.Null(failedOpen);
    }

    [Fact]
    public async Task An_UNTRACKED_DIRECTORY_is_still_covered_through_the_prefix()
    {
        // The two rules compose: git collapsed a new folder to one `dir/`
        // entry AND the required path is project-relative.
        var sandbox = new PorcelainSandbox(
            "?? apps/proj/tests/fixtures/24-loops/\n", prefix: "apps/proj/");

        var (missing, _) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["tests/fixtures/24-loops/case.cs"], default);

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("apps/proj/")]
    [InlineData("apps/proj")]      // no trailing slash
    [InlineData("apps\\proj\\")]   // windows separators
    [InlineData(" apps/proj/\n")]  // git's own trailing newline
    public async Task The_PREFIX_is_normalised_to_ONE_SPELLING(string prefix)
    {
        var sandbox = new PorcelainSandbox(" M apps/proj/src/Money.cs\n", prefix);

        var (missing, _) = await LeaderTools.UntouchedDetailedAsync(
            sandbox, "s", ["src/Money.cs"], default);

        Assert.Empty(missing);
    }
}
