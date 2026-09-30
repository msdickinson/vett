using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Llm;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// The stall watchdog had ZERO tests before 2026-08-27. Every hit for
/// `StallTimeoutSec` / "stalled" under tests/ was a compiled artifact in bin/,
/// except HarnessStopReasonTests, which pins a string -> classification mapping
/// and never exercises the watchdog itself.
///
/// Two defects are covered here.
///
/// W1 — the threshold was a CONSTANT (1200s) justified against the WRONG
/// QUANTITY. Its comment reasoned against one `request_timeout_seconds` (900s in
/// the deepseek profiles) and asserted a hung call "self-heals at 900s (timeout
/// -> retry -> event)". A retry emits NO event: RequestRetryPolicy retries inside
/// the SDK pipeline, beneath the single GetResponseAsync the loop is awaiting. So
/// the real silent window is (num_retries + 1) x request_timeout_seconds, and
/// 1200s sat BELOW it for the deepseek family and exactly ON it for coding/spec.
///
/// W2 — a stall resolved under two different labels depending on where the
/// cancellation was observed, and the more common label was indistinguishable
/// from a user-requested cancel.
/// </summary>
public class StallWatchdogTests
{
    // ---------------------------------------------------------------- W1

    [Theory]
    // request_timeout_seconds, num_retries, expected silent window
    [InlineData(900, 2, 2700)]   // ds-*            — 1200 was BELOW this
    [InlineData(300, 3, 1200)]   // coding*/spec-*  — 1200 was EXACTLY this
    [InlineData(300, 5, 1800)]   // openhands
    public void Retry_budget_is_the_whole_stack_not_one_attempt(int timeout, int retries, int expected)
    {
        var cfg = new LlmConfig { RequestTimeoutSeconds = timeout, NumRetries = retries };
        Assert.Equal(expected, ChatClientFactory.EffectiveRetryBudgetSeconds(cfg));
    }

    [Fact]
    public void Retry_budget_uses_the_same_defaults_the_client_is_built_with()
    {
        // The seam exists so the watchdog and CreateSingle cannot drift. If
        // someone changes one fallback without the other, this fails.
        var unset = new LlmConfig();
        Assert.Equal(
            (ChatClientFactory.DefaultNumRetries + 1) * ChatClientFactory.DefaultRequestTimeoutSeconds,
            ChatClientFactory.EffectiveRetryBudgetSeconds(unset));
    }

    /// <summary>
    /// ⭐ THE INVARIANT, STATED OVER THE POPULATION THAT ACTUALLY SHIPS.
    ///
    /// A silence-based watchdog is only correct if it outlasts the longest
    /// legitimate silence. Asserting that for one hand-picked config would be
    /// overfitting to a pinned example, so this walks every profile in
    /// profiles/ and requires the property to hold for each.
    ///
    /// The premise guard matters: if the glob returns nothing (wrong cwd, moved
    /// folder), a "for every profile" assertion passes VACUOUSLY and reports a
    /// confident green over an empty set.
    /// </summary>
    [Fact]
    public void Every_shipped_profile_gets_a_stall_timeout_that_outlasts_its_retry_stack()
    {
        var files = ShippedProfiles();
        Assert.True(files.Count >= 10,
            $"PREMISE FAILED: expected the shipped profile set, found {files.Count} files. " +
            "A vacuous pass over an empty glob is not evidence.");

        var checkedAny = 0;
        foreach (var (path, profile) in files)
        {
            var budget = ChatClientFactory.EffectiveRetryBudgetSeconds(profile.Llm);
            var timeout = TeamCoordinator.StallTimeoutFor(profile.Llm, null);
            Assert.True(timeout > budget,
                $"{Path.GetFileName(path)}: stall timeout {timeout}s does not outlast its " +
                $"silent window of {budget}s — the watchdog would reap a member that is " +
                "merely waiting out the SDK's retry stack.");
            checkedAny++;
        }
        Assert.True(checkedAny >= 10, "PREMISE FAILED: no profile was actually checked.");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the test above. It has power only if the OLD
    /// constant would have failed it — otherwise it is green for free and would
    /// have stayed green through the entire defect.
    /// </summary>
    [Fact]
    public void The_old_1200s_constant_fails_that_invariant_on_shipped_profiles()
    {
        var offenders = ShippedProfiles()
            .Where(f => TeamCoordinator.StallTimeoutFloorSec
                        <= ChatClientFactory.EffectiveRetryBudgetSeconds(f.Profile.Llm))
            .Select(f => Path.GetFileName(f.Path))
            .ToList();

        Assert.True(offenders.Count > 0,
            "The old 1200s constant satisfies the invariant everywhere, so the test above " +
            "proves nothing. Either the profiles changed or the invariant is toothless.");
    }

    [Theory]
    [InlineData(900, 2, 3375)]   // 2700 * 1.25 — margin clears it
    [InlineData(300, 3, 1500)]   // 1200 * 1.25 — was an exact tie at the floor
    [InlineData(60, 0, 1200)]    // 60 * 1.25 = 75 -> floor wins (a big `dotnet test`)
    public void Stall_timeout_is_the_budget_plus_margin_or_the_floor_whichever_is_larger(
        int timeout, int retries, int expected)
    {
        var cfg = new LlmConfig { RequestTimeoutSeconds = timeout, NumRetries = retries };
        Assert.Equal(expected, TeamCoordinator.StallTimeoutFor(cfg, null));
    }

    [Fact]
    public void A_member_override_raises_the_timeout_and_never_lowers_it()
    {
        var profile = new LlmConfig { RequestTimeoutSeconds = 300, NumRetries = 1 };  // 600s
        var slower = new LlmConfig { RequestTimeoutSeconds = 900, NumRetries = 2 };   // 2700s
        var faster = new LlmConfig { RequestTimeoutSeconds = 60, NumRetries = 0 };    // 60s

        // The slower member's budget must win — it is the one that would be
        // wrongly reaped.
        Assert.Equal(TeamCoordinator.StallTimeoutFor(slower, null),
                     TeamCoordinator.StallTimeoutFor(profile, slower));

        // A faster member must NOT drag the ceiling down: the profile-level
        // client can still be the one sitting in a long retry stack.
        Assert.Equal(TeamCoordinator.StallTimeoutFor(profile, null),
                     TeamCoordinator.StallTimeoutFor(profile, faster));
    }

    // ---------------------------------------------------------------- W2

    private static AgentResult Absorbed() => new()
    {
        Messages = [Chat.Assistant("partial work")],
        StopReason = "cancelled",     // what AgentLoop.cs:430 returns
        Iterations = 7,
        InputTokens = 1234,
        OutputTokens = 567,
        CountersMeasured = true,      // it really did measure them
    };

    [Fact]
    public void A_watchdog_kill_absorbed_by_the_agent_loop_is_relabelled_stalled()
    {
        var r = TeamCoordinator.ApplyStallVerdict(Absorbed(), watchdogFired: true,
                                              outerCancelled: false, "impl", 2700);

        Assert.Equal("stalled", r.StopReason);
        Assert.Contains(r.Messages, m => m.GetText().Contains("stalled"));
    }

    /// <summary>
    /// ⭐ THE CONTROL THAT KEEPS THE TEST ABOVE HONEST. Relabelling everything
    /// "stalled" would satisfy that assertion perfectly. A genuine cancel_task
    /// must still read as "cancelled", or the new label means nothing.
    /// </summary>
    [Fact]
    public void A_real_cancellation_is_not_relabelled_stalled()
    {
        var r = TeamCoordinator.ApplyStallVerdict(Absorbed(), watchdogFired: false,
                                              outerCancelled: false, "impl", 2700);

        Assert.Equal("cancelled", r.StopReason);
        Assert.DoesNotContain(r.Messages, m => m.GetText().Contains("stalled"));
        Assert.Same(Absorbed().StopReason, r.StopReason);  // untouched, not rebuilt
    }

    [Fact]
    public void An_outer_cancellation_racing_the_watchdog_stays_cancelled()
    {
        var r = TeamCoordinator.ApplyStallVerdict(Absorbed(), watchdogFired: true,
                                              outerCancelled: true, "impl", 2700);

        Assert.Equal("cancelled", r.StopReason);
    }

    /// <summary>
    /// The counters are the reason the relabel exists at all: the branch that
    /// used to say "stalled" was the branch that had NO numbers, so every
    /// stall report carried fabricated zeros. Relabelling must not repeat that.
    /// </summary>
    [Fact]
    public void Relabelling_keeps_the_counters_it_was_given()
    {
        var r = TeamCoordinator.ApplyStallVerdict(Absorbed(), watchdogFired: true,
                                              outerCancelled: false, "impl", 2700);

        Assert.Equal(7, r.Iterations);
        Assert.Equal(1234, r.InputTokens);
        Assert.Equal(567, r.OutputTokens);
        Assert.True(r.CountersMeasured);
    }

    [Fact]
    public void The_escaped_path_is_not_double_labelled_and_stays_unmeasured()
    {
        // What the synthesizing catch in RunMemberFull builds.
        var escaped = new AgentResult
        {
            Messages = [Chat.Assistant("[Member impl stalled — no events for 2700s. …]")],
            StopReason = "stalled",
            CountersMeasured = false,
        };

        var r = TeamCoordinator.ApplyStallVerdict(escaped, watchdogFired: true,
                                              outerCancelled: false, "impl", 2700);

        Assert.Single(r.Messages);          // no duplicate note appended
        Assert.False(r.CountersMeasured);   // still could-not-measure
    }

    /// <summary>
    /// COULD-NOT-MEASURE IS NOT MEASURED-ZERO. The counters are plain `int`, so
    /// the value is 0 either way — the flag is the only thing that separates a
    /// killed run from one that ran and did nothing. Defaulting it to `true`
    /// keeps every existing construction site honest without edits.
    /// </summary>
    [Fact]
    public void Counters_are_measured_by_default_and_only_the_stall_path_says_otherwise()
    {
        Assert.True(new AgentResult().CountersMeasured);

        var unmeasured = new AgentResult { StopReason = "stalled", CountersMeasured = false };
        Assert.Equal(0, unmeasured.Iterations);          // indistinguishable by value...
        Assert.False(unmeasured.CountersMeasured);       // ...distinguishable by flag
    }

    // ---------------------------------------------------------------- helpers

    private static List<(string Path, Profile Profile)> ShippedProfiles()
    {
        var dir = FindProfilesDir();
        var loaded = new List<(string, Profile)>();
        if (dir is null) return loaded;

        foreach (var f in Directory.GetFiles(dir, "*.yaml"))
        {
            try { loaded.Add((f, Yaml.LoadProfile(f))); }
            catch { /* a profile this loader rejects is another test's problem */ }
        }
        return loaded;
    }

    /// <summary>
    /// TRIPWIRE — ApplyStallVerdict REBUILDS AgentResult FIELD BY FIELD, so a
    /// property added to AgentResult and not added THERE is silently dropped on
    /// every stalled dispatch. Nothing catches it: an object initializer is
    /// perfectly happy with a subset, and the dropped field then reads
    /// downstream as its default — a fabricated zero or null wearing the shape
    /// of a measurement, which is the exact defect CountersMeasured exists to
    /// prevent one field over.
    ///
    /// Asserted by REFLECTION over the live property list, NOT against a
    /// hard-coded count of 13: a count has to be maintained by the same person
    /// who forgot the field, and this way a 14th property fails the moment it
    /// exists and the failure message names it.
    ///
    /// Messages and StopReason are the two the method is SUPPOSED to rewrite,
    /// so they are checked explicitly rather than exempted silently.
    /// </summary>
    [Fact]
    public void ApplyStallVerdict_carries_every_AgentResult_field()
    {
        var original = new AgentResult
        {
            Messages = new List<ChatMessage> { Chat.Assistant("work happened") },
            StopReason = "max_iterations",
            Iterations = 17,
            InputTokens = 1234,
            OutputTokens = 567,
            CountersMeasured = false,
            DispatchDiff = "diff --git a/x b/x",
            DispatchDiffStat = " x | 2 +-",
            DispatchFilesChanged = 3,
            DispatchWorktreePath = "/tmp/wt",
            DispatchBranch = "vett/dispatch/abc",
            SelfAssessment = "partial",
            SelfAssessmentNotes = "left the tests failing",
        };

        var stalled = TeamCoordinator.ApplyStallVerdict(
            original, watchdogFired: true, outerCancelled: false,
            memberName: "impl", stallTimeoutSec: 1200);

        // The two the method exists to change.
        Assert.Equal("stalled", stalled.StopReason);
        Assert.Equal(original.Messages.Count + 1, stalled.Messages.Count);
        Assert.Contains("stalled", stalled.Messages[^1].GetText());

        // Every other property must survive untouched.
        var carried = new List<string>();
        var dropped = new List<string>();
        foreach (var p in typeof(AgentResult).GetProperties())
        {
            if (p.Name is nameof(AgentResult.Messages) or nameof(AgentResult.StopReason))
                continue;
            var before = p.GetValue(original);
            var after = p.GetValue(stalled);
            if (Equals(before, after)) carried.Add(p.Name);
            else dropped.Add($"{p.Name}: {before ?? "(null)"} -> {after ?? "(null)"}");
        }

        Assert.True(dropped.Count == 0,
            "ApplyStallVerdict DROPPED field(s) while rebuilding AgentResult. A stalled "
            + "dispatch will publish these as defaults, and no consumer can tell that "
            + "apart from a measurement:\n  " + string.Join("\n  ", dropped));

        // LIVENESS: the loop must actually have compared something. Renaming or
        // removing these properties would otherwise leave this test passing
        // vacuously over an empty list — a green that proves nothing.
        Assert.True(carried.Count >= 11,
            $"only {carried.Count} propert(ies) were compared; this tripwire has stopped "
            + "spanning AgentResult and is no longer protecting anything.");
    }

    private static string? FindProfilesDir()
    {
        // Walk up from the test binary to the repo's profiles/ folder rather
        // than hard-coding a relative depth that breaks when the output path
        // changes (net10.0/Debug/... is not stable across configurations).
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, "profiles");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "ds-*.yaml").Length > 0)
                return candidate;
            d = d.Parent;
        }
        return null;
    }
}
