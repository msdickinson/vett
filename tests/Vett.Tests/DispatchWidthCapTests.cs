using Vett.Agent;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// ⭐ THE WIDTH CEILING — the gap Mark named as *"so it never over does it and
/// trashes"*.
///
/// ⛔ WHY IT EXISTS. Depth has been capped and tested since 2026-08-26. **Width
/// was capped by nothing at all.** `assign_async` fires `Task.Run`
/// unconditionally and its own tool description invites the model to make
/// "multiple parallel calls", so the only ceiling was the model's restraint.
///
/// Measured across the four manager-width arms, not hypothesised:
///
/// - the widest single lead fanned out to **32** sub-workers (Pro w20),
/// - and at Pro w20, `stop_reason == max_iterations` fired on **11 of 20 leads'
///   own loops** while **no worker in any arm ever exhausted its budget**.
///
/// So the failure mode is not "workers run out of room". It is leads starving
/// themselves trying to manage a fan-out nothing bounded.
///
/// ⚠ THE DEFAULT IS OFF (0 = unlimited) AND THAT IS DELIBERATE. Every one of
/// those measurements was taken uncapped. A ceiling switched on by default would
/// silently change five shipped profiles — `ds-manager-*-w20` among them — and
/// retroactively invalidate the campaign's own numbers. `Default_is_UNLIMITED…`
/// below is the test that keeps it that way.
/// </summary>
public class DispatchWidthCapTests
{
    /// <summary>
    /// ⭐⭐ THE RACE IS THE WHOLE POINT, AND IT IS THE TEST MOST LIKELY TO HAVE
    /// BEEN SKIPPED.
    ///
    /// The obvious implementation — `if (board.InFlight() &lt; cap) board.Create(…)`
    /// at the call site — is WRONG in exactly the situation the cap exists for.
    /// Two lock acquisitions mean every racer can observe `cap - 1` and then
    /// every racer creates, so the ceiling is overshot by precisely the amount
    /// of concurrency it was added to limit. A single-threaded test cannot tell
    /// the two implementations apart.
    ///
    /// 64 racers against a ceiling of 8, released together.
    /// </summary>
    [Fact]
    public async Task CONCURRENT_reservations_never_exceed_the_ceiling()
    {
        const int cap = 8, racers = 64;
        var board = new TaskBoard { MaxConcurrentDispatches = cap };

        using var gate = new SemaphoreSlim(0);
        var granted = 0;

        var runners = Enumerable.Range(0, racers).Select(_ => Task.Run(async () =>
        {
            await gate.WaitAsync();                       // all block, then all go at once
            if (board.TryCreate("worker", "t", out _) is not null)
                Interlocked.Increment(ref granted);
        })).ToArray();

        gate.Release(racers);
        await Task.WhenAll(runners);

        Assert.Equal(cap, granted);
        Assert.Equal(cap, board.InFlight());

        // ⭐ THE POSITIVE CONJUNCT. Without it, a TryCreate that returned null
        // unconditionally would satisfy "never exceeds the ceiling" perfectly.
        Assert.True(granted > 0, "no reservation succeeded at all — this rig cannot observe a grant");
    }

    /// <summary>
    /// Two-sided at the boundary: the Nth dispatch is allowed, the N+1th is not.
    /// A cap that refused one slot early would pass a "refuses past the cap" test
    /// while quietly costing every profile a worker.
    /// </summary>
    [Fact]
    public void The_ceiling_ACCEPTS_the_last_slot_and_refuses_only_past_it()
    {
        var board = new TaskBoard { MaxConcurrentDispatches = 3 };

        Assert.NotNull(board.TryCreate("w", "a", out _));
        Assert.NotNull(board.TryCreate("w", "b", out _));
        Assert.NotNull(board.TryCreate("w", "c", out var third));
        Assert.Equal(2, third);                       // observed BEFORE this one landed

        Assert.Null(board.TryCreate("w", "d", out var fourth));
        Assert.Equal(3, fourth);                      // and it reports what it saw
    }

    /// <summary>
    /// ⛔ ON THE BOARD, 0 STILL MEANS UNLIMITED — this pins the ENFORCEMENT
    /// layer's behaviour, which is what the 2026-08-26 campaign arms rely on:
    /// they now carry `max_concurrent_dispatches: 0` explicitly, so their runs
    /// must behave exactly as the uncapped measured ones did.
    ///
    /// ⚠ NOT A TEST THAT ABSENCE IS ALLOWED. Since 2026-08-27 a profile that
    /// omits the key is REJECTED before a board is ever built
    /// (`ValidateProfileForRun`, `Coordinator.RequiredWidth`). This constructs a
    /// TaskBoard directly, which is not a profile an author wrote.
    /// </summary>
    [Fact]
    public void An_explicit_zero_is_UNLIMITED_so_the_measured_arms_keep_their_behaviour()
    {
        var board = new TaskBoard();
        Assert.Equal(0, board.MaxConcurrentDispatches);

        // Comfortably past the widest fan-out ever measured (32, Pro w20).
        for (var i = 0; i < 40; i++)
            Assert.NotNull(board.TryCreate("w", $"t{i}", out _));

        Assert.Equal(40, board.InFlight());
    }

    /// <summary>
    /// A NEGATIVE value reads as unlimited, which is the OPPOSITE of what
    /// writing a cap means. Pinned here so the behaviour is a decision rather
    /// than an accident — `validate` reports it as an error, which is the layer
    /// that can actually tell the author.
    /// </summary>
    [Fact]
    public void A_negative_ceiling_reads_as_unlimited_which_is_why_validate_rejects_it()
    {
        var board = new TaskBoard { MaxConcurrentDispatches = -5 };
        for (var i = 0; i < 10; i++) Assert.NotNull(board.TryCreate("w", $"t{i}", out _));
    }

    /// <summary>
    /// ⭐ A SLOT FREES WHEN WORK SETTLES — on success AND on failure.
    ///
    /// If only Complete released a slot, one failing worker would permanently
    /// consume capacity and a busy leader would deadlock itself into a ceiling
    /// it can never get back under. That is a worse failure than no cap at all,
    /// because it is silent and it accumulates.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_settled_task_releases_its_slot_whether_it_COMPLETED_or_FAILED(bool succeed)
    {
        var board = new TaskBoard { MaxConcurrentDispatches = 1 };

        var t = board.TryCreate("w", "first", out _);
        Assert.NotNull(t);
        Assert.Null(board.TryCreate("w", "blocked", out _));    // ceiling holds while it runs

        if (succeed) board.Complete(t!.Id, "ok"); else board.Fail(t!.Id, "boom");

        Assert.Equal(0, board.InFlight());
        Assert.NotNull(board.TryCreate("w", "after", out _));   // and the slot came back
    }

    /// <summary>
    /// `MarkRunning` moves Pending → Running. Both are in flight, so the
    /// transition must not hand out a free slot mid-task.
    /// </summary>
    [Fact]
    public void A_task_occupies_its_slot_across_the_Pending_to_Running_transition()
    {
        var board = new TaskBoard { MaxConcurrentDispatches = 1 };

        var t = board.TryCreate("w", "x", out _);
        Assert.NotNull(t);
        Assert.Equal(1, board.InFlight());

        board.MarkRunning(t!.Id);

        Assert.Equal(1, board.InFlight());
        Assert.Null(board.TryCreate("w", "y", out _));
    }

    /// <summary>
    /// THE SETTING REACHES THE BOARD. A ceiling that parses but never arrives is
    /// the "the setting the author wrote never reached the code" class `validate`
    /// exists for — so this asserts on the deserialised config, which is the
    /// value `Coordinator` copies onto the board.
    ///
    /// ⭐ ABSENT DESERIALISES TO `null`, NOT `0`, AND THAT IS THE WHOLE POINT.
    /// While the property was a bare `int`, "the author omitted the key" and
    /// "the author wrote 0" were the same value, so no required-check could
    /// exist — absence was not representable. This test pins the distinction:
    /// if someone "simplifies" the property back to `int`, the null assertion
    /// below stops compiling and the requirement silently dies.
    /// </summary>
    [Fact]
    public void The_profile_setting_deserialises_and_ABSENT_is_null_not_zero()
    {
        var withCap = Yaml.ParseProfile("""
            name: p
            llm: {provider: local, endpoint: 'http://127.0.0.1:1/v1', model: m}
            team:
              leader: {name: manager}
              members: [{name: worker}]
              max_concurrent_dispatches: 6
            """);
        Assert.Equal(6, withCap.Team!.MaxConcurrentDispatches);

        // An EXPLICIT zero is a decision the author made, and must survive as a
        // value distinct from silence — it is what the 2026-08-26 campaign arms
        // now carry to stay uncapped on purpose.
        var explicitZero = Yaml.ParseProfile("""
            name: p
            llm: {provider: local, endpoint: 'http://127.0.0.1:1/v1', model: m}
            team:
              leader: {name: manager}
              members: [{name: worker}]
              max_concurrent_dispatches: 0
            """);
        Assert.Equal(0, explicitZero.Team!.MaxConcurrentDispatches);

        var without = Yaml.ParseProfile("""
            name: p
            llm: {provider: local, endpoint: 'http://127.0.0.1:1/v1', model: m}
            team:
              leader: {name: manager}
              members: [{name: worker}]
            """);
        Assert.Null(without.Team!.MaxConcurrentDispatches);
    }

    /// <summary>
    /// ⭐ THE REQUIREMENT, TWO-SIDED, AT THE LAYER THAT ENFORCES IT FOR A RUN.
    ///
    /// `ValidateProfileForRun` is what `vett run` / `vett chat` call, so this is
    /// the gate that decides whether an uncapped profile can START. Both
    /// directions are asserted because a check that rejected everything would
    /// satisfy the negative case alone.
    ///
    /// ⛔ THE NESTED CASE IS THE LOAD-BEARING ONE. Each `team:` block gets its
    /// OWN TaskBoard, so a capped manager over an uncapped sub-team is still an
    /// uncapped run — the fan-out just moves one level down. A check that only
    /// looked at `profile.Team` would pass this profile and cover 12 of the 74
    /// team nodes in this repo.
    /// </summary>
    [Fact]
    public void ValidateProfileForRun_REQUIRES_a_ceiling_on_every_team_node_including_nested()
    {
        const string head = """
            name: p
            system_prompt: hi
            llm: {provider: local, endpoint: 'http://127.0.0.1:1/v1', model: m}
            """;

        // POSITIVE CONJUNCT: every node declares one → no throw.
        var ok = Yaml.ParseProfile(head + """

            team:
              max_concurrent_dispatches: 3
              leader: {name: manager}
              members:
                - name: lead
                  team:
                    max_concurrent_dispatches: 2
                    leader: {name: sub-lead}
                    members: [{name: worker}]
            """);
        Yaml.ValidateProfileForRun(ok, "ok.yaml");

        // TOP-LEVEL absent → throws, and names the node.
        var topless = Yaml.ParseProfile(head + """

            team:
              leader: {name: manager}
              members: [{name: worker}]
            """);
        var e1 = Assert.Throws<InvalidOperationException>(
            () => Yaml.ValidateProfileForRun(topless, "topless.yaml"));
        Assert.Contains("team.max_concurrent_dispatches is required", e1.Message);

        // NESTED absent while the top level is capped → still throws, and the
        // message points at the NESTED node by path, not at "team".
        var nestedless = Yaml.ParseProfile(head + """

            team:
              max_concurrent_dispatches: 3
              leader: {name: manager}
              members:
                - name: lead
                  team:
                    leader: {name: sub-lead}
                    members: [{name: worker}]
            """);
        var e2 = Assert.Throws<InvalidOperationException>(
            () => Yaml.ValidateProfileForRun(nestedless, "nestedless.yaml"));
        Assert.Contains("team/lead.max_concurrent_dispatches is required", e2.Message);

        // A NEGATIVE ceiling reads as unlimited at the dispatch site, so it is
        // rejected here too rather than being waved through as "present".
        var negative = Yaml.ParseProfile(head + """

            team:
              max_concurrent_dispatches: -1
              leader: {name: manager}
              members: [{name: worker}]
            """);
        var e3 = Assert.Throws<InvalidOperationException>(
            () => Yaml.ValidateProfileForRun(negative, "negative.yaml"));
        Assert.Contains("reads as UNLIMITED", e3.Message);

        // A SOLO profile has no team node at all and must stay unaffected —
        // otherwise the requirement would break every non-team profile.
        var solo = Yaml.ParseProfile(head);
        Yaml.ValidateProfileForRun(solo, "solo.yaml");
    }

    /// <summary>
    /// ⛔ THE BACKSTOP FAILS CLOSED. `Coordinator.RequiredWidth` is reached by
    /// entry points that do not call ValidateProfileForRun, and `?? 0` there
    /// would have silently reinstated unlimited — the exact default the
    /// requirement abolishes. Asserted separately from the config check because
    /// they are different layers and a fix to one does not cover the other.
    /// </summary>
    [Fact]
    public void RequiredWidth_throws_rather_than_coalescing_an_absent_ceiling_to_unlimited()
    {
        var absent = new Vett.Config.TeamConfig { Leader = new() { Name = "l" } };
        var ex = Assert.Throws<InvalidOperationException>(
            () => TeamCoordinator.RequiredWidth(absent));
        Assert.Contains("required", ex.Message);

        // Explicit values pass straight through, including a deliberate 0.
        Assert.Equal(0, TeamCoordinator.RequiredWidth(
            new Vett.Config.TeamConfig { MaxConcurrentDispatches = 0 }));
        Assert.Equal(7, TeamCoordinator.RequiredWidth(
            new Vett.Config.TeamConfig { MaxConcurrentDispatches = 7 }));
    }
}
