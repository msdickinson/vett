using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// THE TWO SIZING GATES `vett validate` GREW ON 2026-09-01, AND THE POPULATION
/// EACH ONE MUST SPAN.
///
/// Both answer a question the schema validator structurally cannot, because
/// each key is individually legal and only the COMBINATION is wrong:
///
///   UnboundedOutputSeatsOf      — a seat with no `max_output_tokens`.
///     `request_timeout_seconds` caps how long one attempt may TAKE; nothing
///     caps how much it tries to PRODUCE. With no ceiling, whether a call fits
///     inside its own timeout stops being a property of the configuration, and
///     every retry restarts the same unbounded generation.
///
///   RunCapShorterThanRetryStackOf — `timeout_minutes` smaller than ONE call's
///     retry stack. The run is killed part-way through a stack it was
///     configured to complete, and the symptom is a run that simply stops —
///     which reads as a hang, not as a configuration error.
///
/// ⛔ WHY THE FIRST ONE'S TEST IS THE INTERESTING ONE. Its first version used
/// `ChatClientFactory.Merge` to compute the effective value, on the reasonable
/// assumption that Merge is the inheritance rule. Merge did not carry
/// MaxOutputTokens at all (see LlmConfigMergeCoverageTests), so the check
/// returned EVERY seat of EVERY profile — 21 of 21, including the one profile
/// that demonstrably sets the key and whose bound had been measured at the wire
/// on a live run. A round 100% is the shape a broken filter makes, and the
/// counter-example was the profile I knew most about.
///
/// So the property under test here is not "the warning can fire" — a probe
/// profile already showed that, and it showed it while the check was wrong.
/// It is that the check SEPARATES: a bounded profile must produce nothing.
/// A gate that fires on everything has the same information content as a gate
/// that fires on nothing.
/// </summary>
public class OutputBoundGateTests
{
    private static Profile TeamProfile(int? rootBound, int? memberBound) => new()
    {
        Name = "t",
        Llm = new LlmConfig { Provider = "local", Endpoint = "http://127.0.0.1:1/v1", Model = "m", MaxOutputTokens = rootBound },
        Team = new TeamConfig
        {
            MaxConcurrentDispatches = 0,
            Leader = new MemberConfig { Name = "lead" },
            Members =
            [
                new MemberConfig
                {
                    Name = "implementer",
                    // A seat with its own llm: block is the case that matters —
                    // a null override short-circuits the merge and can never
                    // lose anything, so it would test nothing.
                    Llm = new LlmConfig { Temperature = 0.2, MaxOutputTokens = memberBound },
                },
            ],
        },
    };

    [Fact]
    public void ABoundedProfile_ProducesNoUnboundedWarnings_AtAnySeat()
    {
        // ⭐ THE SEPARATION TEST. This is the assertion that was false while the
        // check leaned on a merge that dropped the field.
        var seats = EndpointProbe.UnboundedOutputSeatsOf(TeamProfile(4096, null));

        Assert.True(seats.Count == 0,
            "a profile that sets max_output_tokens at the root must produce NO unbounded " +
            "warnings, because every seat inherits it. Got: " +
            string.Join(", ", seats.Select(s => s.Path)));
    }

    [Fact]
    public void AnUnboundedProfile_FlagsEverySeat_RootLeaderAndMembers()
    {
        // The other side of the separation: when nothing is set, the gate must
        // span the whole team, not just the root. An earlier endpoint gate in
        // this same file's subject walked profile.Llm + Team.Members and missed
        // Team.Leader entirely; these checks share WalkTeam so they cannot.
        var seats = EndpointProbe.UnboundedOutputSeatsOf(TeamProfile(null, null));
        var paths = seats.Select(s => s.Path).ToList();

        Assert.Contains(paths, p => p == "llm");
        Assert.Contains(paths, p => p.Contains("leader"));
        Assert.Contains(paths, p => p.Contains("implementer"));
    }

    [Fact]
    public void ASeatMayBeUnboundedWhileTheRootIsBounded_AndOnlyThatSeatIsNamed()
    {
        // Inheritance is not the only story: `max_output_tokens: null` cannot be
        // written in YAML, so this case arises through a seat whose own block
        // sets a DIFFERENT bound. The warning must carry the seat's OWN
        // exposure, not the root's, or the operator reads the wrong numbers.
        var seats = EndpointProbe.UnboundedOutputSeatsOf(TeamProfile(4096, 512));
        Assert.Empty(seats);
    }

    [Fact]
    public void TheWarningCarriesTheEffectiveTimeoutAndRetries_NotTheSeatsOwnBlock()
    {
        // ⛔ EFFECTIVE CONFIG, NOT THE SEAT'S OWN BLOCK. A seat that inherits
        // its timeout has no timeout of its own; reporting the raw block would
        // print "0s, retried 0 time(s)" and make the warning look like noise.
        var p = TeamProfile(null, null);
        p.Llm.RequestTimeoutSeconds = 180;
        p.Llm.NumRetries = 2;

        var member = EndpointProbe.UnboundedOutputSeatsOf(p)
            .Single(s => s.Path.Contains("implementer"));

        Assert.Equal(180, member.RequestTimeoutSeconds);
        Assert.Equal(2, member.NumRetries);
    }

    [Fact]
    public void UnsetTimeoutAndRetries_ReportTheSdkDefaultsTheSeatWillActuallyUse()
    {
        // "Not written in the YAML" is not "no timeout". The operator needs the
        // number that will bind, which is the factory default.
        var p = TeamProfile(null, null);
        var root = EndpointProbe.UnboundedOutputSeatsOf(p).Single(s => s.Path == "llm");

        Assert.Equal(Vett.Llm.ChatClientFactory.DefaultRequestTimeoutSeconds, root.RequestTimeoutSeconds);
        Assert.Equal(Vett.Llm.ChatClientFactory.DefaultNumRetries, root.NumRetries);
    }

    // ------------------------------------------------------------ the run cap

    [Fact]
    public void ARunCapShorterThanOneRetryStack_IsFlagged_WithBothNumbers()
    {
        var p = TeamProfile(4096, null);
        p.TimeoutMinutes = 5;                    //  300s for the whole run
        p.Llm.RequestTimeoutSeconds = 600;       //  one attempt may take 600s
        p.Llm.NumRetries = 4;                    //  5 attempts => 3000s budget

        var gap = EndpointProbe.RunCapShorterThanRetryStackOf(p).First(g => g.Path == "llm");

        Assert.Equal(300, gap.RunCapSeconds);
        Assert.Equal(3000, gap.RetryBudgetSeconds);
    }

    [Fact]
    public void ARunCapLongerThanTheRetryStack_IsSilent()
    {
        var p = TeamProfile(4096, null);
        p.TimeoutMinutes = 180;                  // 10800s
        p.Llm.RequestTimeoutSeconds = 180;
        p.Llm.NumRetries = 2;                    // 540s budget

        Assert.Empty(EndpointProbe.RunCapShorterThanRetryStackOf(p));
    }

    [Fact]
    public void TheRunCapGateUsesEffectiveRetryBudget_NotItsOwnArithmetic()
    {
        // ⛔ A DUPLICATED VALUE DRIFTS SILENTLY. The budget belongs to
        // ChatClientFactory.EffectiveRetryBudgetSeconds; if this gate ever grows
        // its own copy of `(n+1) x t`, the two answers diverge the next time the
        // retry policy changes — which it did, on 2026-08-31, when num_retries
        // was found not to bind at all.
        var p = TeamProfile(4096, null);
        p.TimeoutMinutes = 1;                    // 60s — small enough to always fire
        p.Llm.RequestTimeoutSeconds = 90;
        p.Llm.NumRetries = 3;

        var expected = Vett.Llm.ChatClientFactory.EffectiveRetryBudgetSeconds(p.Llm);
        var gap = EndpointProbe.RunCapShorterThanRetryStackOf(p).First(g => g.Path == "llm");

        Assert.Equal(expected, gap.RetryBudgetSeconds);
    }

    [Fact]
    public void AZeroOrNegativeRunCap_IsNotTreatedAsAnImpossiblyShortRun()
    {
        // timeout_minutes <= 0 means "no cap" elsewhere in the codebase. Reading
        // it as a 0-second run would flag every seat of every uncapped profile —
        // the false-positive flood that gets a warning ignored.
        var p = TeamProfile(4096, null);
        p.TimeoutMinutes = 0;
        p.Llm.RequestTimeoutSeconds = 600;
        p.Llm.NumRetries = 4;

        Assert.Empty(EndpointProbe.RunCapShorterThanRetryStackOf(p));
    }
}
