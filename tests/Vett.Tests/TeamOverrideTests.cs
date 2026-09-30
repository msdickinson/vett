using Vett.Config;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Runtime team shaping. The thing under test is that ONE profile can be
/// resized at the point of use, and that every way of getting it wrong is
/// reported rather than silently absorbed into a smaller team.
/// </summary>
public class TeamOverrideTests
{
    private static Profile BaseProfile() => new()
    {
        Name = "test-team",
        Compaction = new CompactionConfig { ThresholdTokens = 100_000, KeepLastMessages = 8 },
        Team = new TeamConfig
        {
            MaxConcurrentDispatches = 4,
            Leader = new MemberConfig
            {
                Name = "lead",
                SystemPrompt = "You are a team leader.\nAvailable members: implementer, researcher, reviewer\nDelegate.",
                MaxIterations = 200,
            },
            Members =
            [
                new MemberConfig { Name = "implementer", SystemPrompt = "impl", Tools = ["terminal", "file_editor"], MaxIterations = 100 },
                new MemberConfig { Name = "researcher", SystemPrompt = "res", Tools = ["terminal"], MaxIterations = 25 },
                new MemberConfig { Name = "reviewer", SystemPrompt = "rev", Tools = ["terminal"], MaxIterations = 20 },
            ],
        },
    };

    [Fact]
    public void AnEmptySpecChangesTheSHAPEOfNothing()
    {
        var p = BaseProfile();
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(), out var err, out var summary));
        Assert.Equal("", err);
        Assert.Equal("", summary);
        Assert.Equal(3, p.Team!.Members.Count);
        Assert.Equal(4, p.Team.MaxConcurrentDispatches);
        Assert.Contains("Available members: implementer, researcher, reviewer", p.Team.Leader.SystemPrompt);
    }

    [Fact]
    public void TheLeaderGetsAnAuthoritativeRosterEvenWithNoOverride()
    {
        // THE DEFECT THIS ENCODES. Before 2026-09-01 the roster block was
        // written only when --team resized the roster, so an ordinary run got
        // nothing and the leader guessed. Observed live (EpicForge rung E1):
        // the leader's FIRST tool call was assign_task("implementer-1") against
        // a roster whose only seat is "implementer", and vett answered
        // `Unknown member "implementer-1"` — with success=true, so no failure
        // counter moved.
        var p = BaseProfile();
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(), out _, out _));

        var prompt = p.Team!.Leader.SystemPrompt;
        Assert.Contains("TEAM ROSTER FOR THIS RUN", prompt);
        Assert.Contains("- implementer", prompt);
        Assert.Contains("- researcher", prompt);
        Assert.Contains("- reviewer", prompt);
        // The seat names it lists must be the seats that EXIST. The block is
        // worthless if it can carry a name the coordinator would reject.
        Assert.DoesNotContain("implementer-1", prompt);
    }

    [Fact]
    public void TheRosterBlockIsIdempotentAndNeverStacks()
    {
        // It is applied on load and AGAIN after a resize. Two blocks would be
        // worse than none: the leader reads the stale one first and dispatches
        // to seats that no longer exist.
        var p = BaseProfile();
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(), out _, out _));
        TeamOverride.TryParseRoster("implementer x2", out var roster, out _);
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(Roster: roster), out _, out _));

        var prompt = p.Team!.Leader.SystemPrompt;
        var occurrences = prompt.Split("TEAM ROSTER FOR THIS RUN").Length - 1;
        Assert.Equal(1, occurrences);
        // and the ONE that survives is the CURRENT shape, not the original
        Assert.Contains("implementer-1, implementer-2", prompt);
        Assert.DoesNotContain("- researcher", prompt);
    }

    [Fact]
    public void ARoleCanBeClonedIntoManyIndependentSeats()
    {
        var p = BaseProfile();
        Assert.True(TeamOverride.TryParseRoster("implementer x5, reviewer x2, researcher", out var roster, out _));
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(Roster: roster), out var err, out _), err);

        var names = p.Team!.Members.Select(m => m.Name).ToList();
        Assert.Equal(
            ["implementer-1", "implementer-2", "implementer-3", "implementer-4", "implementer-5",
             "reviewer-1", "reviewer-2", "researcher"],
            names);

        // Each clone carries the ORIGINAL role's prompt, tools and budget —
        // the override supplies shape, never new behaviour.
        var impls = p.Team.Members.Where(m => m.Name.StartsWith("implementer")).ToList();
        Assert.All(impls, m => Assert.Equal("impl", m.SystemPrompt));
        Assert.All(impls, m => Assert.Equal(100, m.MaxIterations));
        Assert.All(impls, m => Assert.Equal(["terminal", "file_editor"], m.Tools));
    }

    [Fact]
    public void ClonedSeatsDoNotShareMutableState()
    {
        var p = BaseProfile();
        TeamOverride.TryParseRoster("implementer x3", out var roster, out _);
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(Roster: roster), out _, out _));

        // A shallow copy would alias Tools across every seat, so editing one
        // seat's toolset would silently edit all of them.
        p.Team!.Members[0].Tools.Add("mcp");
        Assert.Equal(3, p.Team.Members[0].Tools.Count);
        Assert.Equal(2, p.Team.Members[1].Tools.Count);
        Assert.Equal(2, p.Team.Members[2].Tools.Count);
    }

    [Fact]
    public void ASingleSeatKeepsThePlainRoleNameSoExistingPromptsStillResolve()
    {
        var p = BaseProfile();
        TeamOverride.TryParseRoster("implementer, reviewer", out var roster, out _);
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(Roster: roster), out _, out _));
        Assert.Equal(["implementer", "reviewer"], p.Team!.Members.Select(m => m.Name));
    }

    [Fact]
    public void TheLeaderRosterIsRewrittenNotJustAppendedTo()
    {
        var p = BaseProfile();
        TeamOverride.TryParseRoster("implementer x2", out var roster, out _);
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(Roster: roster), out _, out _));

        var prompt = p.Team!.Leader.SystemPrompt;
        // The STALE line must be gone. A leader that reads "Available members:
        // implementer, researcher, reviewer" and dispatches to `researcher`
        // burns its budget on a member that no longer exists.
        Assert.DoesNotContain("Available members: implementer, researcher, reviewer", prompt);
        Assert.Contains("Available members: implementer-1, implementer-2", prompt);
        Assert.Contains("TEAM ROSTER FOR THIS RUN", prompt);
        Assert.Contains("2 interchangeable implementer seats", prompt);
    }

    [Fact]
    public void TheLeaderCanBeGivenAStrictlyBiggerWindowThanTheWorkers()
    {
        var p = BaseProfile();
        Assert.True(TeamOverride.TryApply(
            p, new TeamOverride.Spec(LeaderContext: 110_000, WorkerContext: 45_000), out var err, out _), err);

        Assert.Equal(110_000, p.Team!.Leader.Compaction!.ThresholdTokens);
        Assert.All(p.Team.Members, m => Assert.Equal(45_000, m.Compaction!.ThresholdTokens));
    }

    [Fact]
    public void APerSeatBlockCarriesTheInheritedKeepLastRatherThanSilentlyDroppingToTheClassDefault()
    {
        // ⛔ The compaction block REPLACES, it does not merge. A naive
        // implementation that news up a CompactionConfig and sets only the
        // threshold would drop this profile's keep_last_messages of 8 to the
        // class default of 5 — a real behaviour change nobody asked for,
        // invisible in every log.
        var p = BaseProfile();
        Assert.Equal(5, new CompactionConfig().KeepLastMessages);
        Assert.True(TeamOverride.TryApply(
            p, new TeamOverride.Spec(LeaderContext: 110_000, WorkerContext: 45_000), out _, out _));

        Assert.Equal(8, p.Team!.Leader.Compaction!.KeepLastMessages);
        Assert.All(p.Team.Members, m => Assert.Equal(8, m.Compaction!.KeepLastMessages));
    }

    [Fact]
    public void APerRoleContextRuleReachesEverySeatClonedFromThatRole()
    {
        var p = BaseProfile();
        TeamOverride.TryParseRoster("implementer x3, reviewer x2", out var roster, out _);
        Assert.True(TeamOverride.TryParsePerRoleContext("implementer=60000, reviewer=30000", out var perRole, out _));
        Assert.True(TeamOverride.TryApply(
            p, new TeamOverride.Spec(Roster: roster, WorkerContext: 45_000, PerRoleContext: perRole), out var err, out _), err);

        foreach (var m in p.Team!.Members)
        {
            var expected = m.Name.StartsWith("implementer") ? 60_000 : 30_000;
            Assert.Equal(expected, m.Compaction!.ThresholdTokens);
        }
    }

    [Fact]
    public void AnUnknownRoleIsAnErrorAndLeavesTheProfileUNTOUCHED()
    {
        // The important half is the second one. If a typo produced a partial
        // roster, the run would quietly execute with fewer seats than asked
        // for and its result would carry a denominator nobody could state.
        var p = BaseProfile();
        TeamOverride.TryParseRoster("implementer x5, tester x2", out var roster, out _);
        Assert.False(TeamOverride.TryApply(p, new TeamOverride.Spec(Roster: roster), out var err, out _));

        Assert.Contains("no member called 'tester'", err);
        Assert.Contains("implementer, researcher, reviewer", err);
        Assert.Equal(3, p.Team!.Members.Count);
        Assert.Equal(["implementer", "researcher", "reviewer"], p.Team.Members.Select(m => m.Name));
        Assert.Contains("Available members: implementer, researcher, reviewer", p.Team.Leader.SystemPrompt);
    }

    [Fact]
    public void AnUnknownRoleInAContextRuleIsAlsoAnError()
    {
        var p = BaseProfile();
        TeamOverride.TryParsePerRoleContext("tester=60000", out var perRole, out _);
        Assert.False(TeamOverride.TryApply(p, new TeamOverride.Spec(PerRoleContext: perRole), out var err, out _));
        Assert.Contains("'tester' is not a member", err);
        Assert.Null(p.Team!.Members[0].Compaction);
    }

    [Fact]
    public void ResizingASoloProfileSaysSoInsteadOfCrashing()
    {
        var solo = new Profile { Name = "ds-solo-flash", Team = null };
        TeamOverride.TryParseRoster("implementer x5", out var roster, out _);
        Assert.False(TeamOverride.TryApply(solo, new TeamOverride.Spec(Roster: roster), out var err, out _));
        Assert.Contains("SOLO profile", err);
        Assert.Contains("ds-solo-flash", err);
    }

    [Theory]
    [InlineData("implementer x5", "implementer", 5)]
    [InlineData("implementer X5", "implementer", 5)]
    [InlineData("implementer *5", "implementer", 5)]
    [InlineData("implementer 5", "implementer", 5)]
    [InlineData("implementerx5", "implementer", 5)]
    [InlineData("  implementer   x5  ", "implementer", 5)]
    [InlineData("implementer", "implementer", 1)]
    public void TheCountIsReadTheWayAHumanWouldTypeIt(string input, string role, int count)
    {
        Assert.True(TeamOverride.TryParseRoster(input, out var roster, out var err), err);
        Assert.Equal(role, roster[0].Role);
        Assert.Equal(count, roster[0].Count);
    }

    [Theory]
    [InlineData("implementer x0", "at least 1")]
    [InlineData("implementer x-3", "at least 1")]
    [InlineData("implementer x999", "Cap is 64")]
    [InlineData("implementer xlots", "could not read a count")]
    public void ANonsenseCountIsRefusedRatherThanRoundedIntoSomethingPlausible(string input, string needle)
    {
        Assert.False(TeamOverride.TryParseRoster(input, out _, out var err));
        Assert.Contains(needle, err);
    }

    [Fact]
    public void WidthZeroIsARealValueMeaningUnlimitedNotAnUnsetFlag()
    {
        var p = BaseProfile();
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(Width: 0), out _, out var summary));
        Assert.Equal(0, p.Team!.MaxConcurrentDispatches);
        Assert.Contains("UNLIMITED", summary);
    }

    [Fact]
    public void WidthIsSetIndependentlyOfTheRoster()
    {
        var p = BaseProfile();
        Assert.True(TeamOverride.TryApply(p, new TeamOverride.Spec(Width: 10), out _, out _));
        Assert.Equal(10, p.Team!.MaxConcurrentDispatches);
        Assert.Equal(3, p.Team.Members.Count);
    }

    [Theory]
    [InlineData("implementer-3", "implementer")]
    [InlineData("implementer", "implementer")]
    [InlineData("feature-lead-a", "feature-lead-a")]
    [InlineData("feature-lead-2", "feature-lead")]
    public void ASeatSuffixIsOnlyStrippedWhenItIsActuallyANumber(string seat, string role)
        => Assert.Equal(role, TeamOverride.StripSeatSuffix(seat));

    [Fact]
    public void TheRealTeamProfileResizesToTenSeatsWithALadderedContextBudget()
    {
        // End-to-end against the shipped workhorse profile rather than a
        // fixture, because the fixture cannot catch a drift in the real file's
        // member names — which is exactly what the override binds to.
        var p = Vett.Config.Yaml.Resolve("ds-team-flash", "profiles", Vett.Config.Yaml.LoadProfile);
        Assert.NotNull(p);

        Assert.True(TeamOverride.TryParseRoster("implementer x6, researcher x2, reviewer x2", out var roster, out var e1), e1);
        Assert.True(TeamOverride.TryParsePerRoleContext("implementer=60000, researcher=45000, reviewer=30000", out var perRole, out var e2), e2);
        Assert.True(TeamOverride.TryApply(
            p!, new TeamOverride.Spec(roster, Width: 10, LeaderContext: 110_000, PerRoleContext: perRole),
            out var err, out _), err);

        Assert.Equal(10, p!.Team!.Members.Count);
        Assert.Equal(10, p.Team.MaxConcurrentDispatches);
        Assert.Equal(110_000, p.Team.Leader.Compaction!.ThresholdTokens);

        // The leader's window is strictly the largest, which is the whole point
        // of the ladder: it holds the plan plus every worker's report.
        Assert.All(p.Team.Members,
            m => Assert.True(m.Compaction!.ThresholdTokens < p.Team.Leader.Compaction!.ThresholdTokens));

        // Every seat stays under the served window (131072 as of 2026-08-28).
        // A trigger ABOVE the server's max_model_len can never fire: vLLM
        // rejects on context length first, so the run dies instead of compacting.
        Assert.True(p.Team.Leader.Compaction!.ThresholdTokens < 131_072);
        Assert.All(p.Team.Members, m => Assert.True(m.Compaction!.ThresholdTokens < 131_072));
    }
}
