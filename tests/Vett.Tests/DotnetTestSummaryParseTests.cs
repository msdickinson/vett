using Vett.Bench.Team;

namespace Vett.Tests;

/// <summary>
/// The `dotnet_test` assertion's counting rule.
///
/// `dotnet test` on a SOLUTION prints one summary line per test project, and
/// the ruler read only the FIRST match — so the whole solution was graded on
/// whichever project happened to print first. A green Api.Tests ahead of a red
/// Core.Tests scored PASS with 12 failures still outstanding: a silent false
/// GREEN, the direction that accepts wrong work.
///
/// ⚠ SCOPE: every suite bundled today seeds exactly ONE test project, so the
/// defect was REACHABLE BUT NOT TRIGGERED. No historical result is invalidated
/// by the fix — it arms the moment a suite seeds two.
/// </summary>
public class DotnetTestSummaryParseTests
{
    /// <summary>The real VSTest console shape: `Failed:` precedes `Passed:` on
    /// the same line, which is why first-match-only read them consistently
    /// from the same (first) project and looked coherent while being wrong.</summary>
    private static string Summary(string verdict, int failed, int passed, string dll) =>
        $"{verdict}! - Failed:  {failed}, Passed:  {passed}, Skipped:     0, " +
        $"Total:    {failed + passed}, Duration: 1 s - {dll}\n";

    [Fact]
    public void SingleProject_ReadsItsCounts()
    {
        var s = AssertionEngine.ParseTestSummaries(Summary("Passed", 0, 12, "Api.Tests.dll"));

        Assert.Equal(AssertionEngine.SummaryKind.Ok, s.Kind);
        Assert.Equal(12, s.Passed);
        Assert.Equal(0, s.Failed);
        Assert.Equal(1, s.Projects);
    }

    [Fact]
    public void GreenProjectFirst_DoesNotHideASecondProjectsFAILURES()
    {
        // THE DEFECT. Before the fix this returned failed=0 and the assertion
        // scored PASS with 12 tests red.
        var output = Summary("Passed", 0, 30, "Api.Tests.dll")
                   + Summary("Failed", 12, 45, "Core.Tests.dll");

        var s = AssertionEngine.ParseTestSummaries(output);

        Assert.Equal(AssertionEngine.SummaryKind.Ok, s.Kind);
        Assert.Equal(12, s.Failed);
        Assert.Equal(75, s.Passed);
        Assert.Equal(2, s.Projects);
    }

    [Fact]
    public void RedProjectFirst_SumsRatherThanStoppingEarly()
    {
        // The mirror image: ordering must not change the verdict at all.
        // If it does, the ruler is reading print order, not the run.
        var output = Summary("Failed", 12, 45, "Core.Tests.dll")
                   + Summary("Passed", 0, 30, "Api.Tests.dll");

        var s = AssertionEngine.ParseTestSummaries(output);

        Assert.Equal(12, s.Failed);
        Assert.Equal(75, s.Passed);
    }

    [Fact]
    public void ThreeProjects_AllCounted()
    {
        var output = Summary("Passed", 0, 5, "A.dll")
                   + Summary("Passed", 0, 7, "B.dll")
                   + Summary("Failed", 3, 9, "C.dll");

        var s = AssertionEngine.ParseTestSummaries(output);

        Assert.Equal(3, s.Failed);
        Assert.Equal(21, s.Passed);
        Assert.Equal(3, s.Projects);
    }

    [Fact]
    public void NoSummaryAtAll_IsNone_NotAZeroReading()
    {
        // `dotnet test` EXITS 0 when zero tests are discovered, so "no summary"
        // must never collapse into "0 failed". COULD-NOT-MEASURE is not
        // MEASURED-ZERO; the caller has to keep failing closed here.
        var s = AssertionEngine.ParseTestSummaries(
            "A total of 1 test files matched the specified pattern.\n");

        Assert.Equal(AssertionEngine.SummaryKind.None, s.Kind);
    }

    [Fact]
    public void UnpairedCounts_AreMismatched_NotSummed()
    {
        // Something that is not a summary matched. Totals cannot be explained,
        // so the parser refuses rather than grading on a number of unknown
        // provenance — failing OPEN here is the whole finding.
        var s = AssertionEngine.ParseTestSummaries(
            Summary("Passed", 0, 12, "Api.Tests.dll") + "Passed:  99\n");

        Assert.Equal(AssertionEngine.SummaryKind.Mismatched, s.Kind);
        Assert.Equal(2, s.PassedLines);
        Assert.Equal(1, s.FailedLines);
    }

    [Fact]
    public void MismatchedNeverReportsCounts()
    {
        // A refusal must not also hand back tempting numbers; a caller that
        // ignored Kind would otherwise grade on them.
        var s = AssertionEngine.ParseTestSummaries("Failed: 3\nFailed: 4\nPassed: 1\n");

        Assert.Equal(AssertionEngine.SummaryKind.Mismatched, s.Kind);
        Assert.Equal(0, s.Passed);
        Assert.Equal(0, s.Failed);
    }
}
