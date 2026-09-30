using Vett.Config;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>
/// ⛔ DOES `vett validate` SPAN THE `llm.priority` VOCABULARY, OR PASS OVER IT?
///
/// <c>CapacityBinding.PriorityOf</c> throws on an unknown word, so a typo fails
/// CLOSED — but it fails closed MID-RUN, after a profile has loaded and a run
/// has started. That is fail-late, not fail-open. These tests pin the two ways
/// the validate-time gate could be worthless anyway:
///
///   1. VACUOUS SPAN. <c>ChatClientFactory.Merge</c> carries <c>Priority</c>,
///      so a leader or a nested member can author its own and reach
///      <c>PriorityOf</c> at run time. A gate reading only <c>profile.Llm</c>
///      would go green on every one of them. The seat tests below are the ones
///      that fail if the walker is dropped.
///
///   2. DRIFT. If the legal words were restated inside the validator, validate
///      and the runtime could disagree — silently, and in either direction.
///      <see cref="The_reported_message_is_the_runtime_s_own_not_a_restatement"/>
///      pins that the reported text comes from the thrown exception itself.
///
/// The duplicate-suppression test is TWO-SIDED. "One bad root produces one
/// error" would pass just as happily on an implementation that reported nothing
/// at all, so it is paired against a profile where the same word is authored on
/// a seat and MUST be counted.
/// </summary>
public class PriorityVocabularyGateTests
{
    private const string Host = "http://top.invalid:8000/v1";

    /// <summary>A profile with a leader and one nested member, three seats deep.</summary>
    private static Profile Shaped(
        string? rootPriority = null,
        string? leaderPriority = null,
        string? memberPriority = null,
        string? nestedPriority = null) => new()
        {
            Llm = new LlmConfig
            {
                Endpoint = Host,
                Model = "top-model",
                Priority = rootPriority ?? "",
            },
            Team = new TeamConfig
            {
                Leader = new MemberConfig
                {
                    Name = "lead",
                    Llm = new LlmConfig { Priority = leaderPriority ?? "" },
                },
                Members =
                [
                    new MemberConfig
                    {
                        Name = "impl",
                        Llm = new LlmConfig { Priority = memberPriority ?? "" },
                        Team = new TeamConfig
                        {
                            Leader = new MemberConfig { Name = "sublead" },
                            Members =
                            [
                                new MemberConfig
                                {
                                    Name = "nested",
                                    Llm = new LlmConfig { Priority = nestedPriority ?? "" },
                                },
                            ],
                        },
                    },
                ],
            },
        };

    [Theory]
    [InlineData("interactive")]
    [InlineData("batch")]
    [InlineData("background")]
    [InlineData("INTERACTIVE")]   // PriorityOf lowercases
    [InlineData("  batch  ")]     // ...and trims
    [InlineData("")]              // empty is "use the default", not a typo
    public void A_word_the_runtime_accepts_is_never_reported(string word)
    {
        // The control that makes every positive below mean something: if this
        // failed, "reported an error" would be the gate's answer for
        // everything, and the positives would be measuring nothing.
        Assert.Empty(EndpointProbe.BadPrioritiesOf(Shaped(rootPriority: word)));
        Assert.Empty(EndpointProbe.BadPrioritiesOf(Shaped(leaderPriority: word)));
        Assert.Empty(EndpointProbe.BadPrioritiesOf(Shaped(nestedPriority: word)));
    }

    [Fact]
    public void A_bad_word_at_the_profile_root_is_reported()
    {
        var bad = Assert.Single(EndpointProbe.BadPrioritiesOf(Shaped(rootPriority: "intractive")));
        Assert.Equal("llm", bad.Path);
        Assert.Equal("intractive", bad.Word);
    }

    /// <summary>
    /// ⚠ THE SPAN TEST FOR THE LEADER. Historically the leader seat was
    /// invisible to <c>EndpointProbe</c> and validate printed a green tick over
    /// it. This fails if the priority gate reads only <c>profile.Llm</c>.
    /// </summary>
    [Fact]
    public void A_bad_word_on_the_LEADER_seat_is_reported()
    {
        var bad = Assert.Single(EndpointProbe.BadPrioritiesOf(Shaped(leaderPriority: "urgent")));
        Assert.Contains(".leader[lead].llm", bad.Path, StringComparison.Ordinal);
        Assert.Equal("urgent", bad.Word);
    }

    /// <summary>
    /// ⚠ THE SPAN TEST THAT MATTERS MOST: a seat two team-levels down. A gate
    /// that walked only <c>profile.Team.Members</c> would miss it, and so would
    /// one that read only the root.
    /// </summary>
    [Fact]
    public void A_bad_word_on_a_NESTED_member_seat_is_reported()
    {
        var bad = Assert.Single(EndpointProbe.BadPrioritiesOf(Shaped(nestedPriority: "asap")));
        Assert.Contains("nested", bad.Path, StringComparison.Ordinal);
        Assert.Equal("asap", bad.Word);
    }

    /// <summary>
    /// TWO-SIDED. One bad root word must produce ONE error, not one per seat —
    /// an inherited value is not the inheriting seat's fault, and a count that
    /// grew with team size would point at blameless YAML. The second half is
    /// what stops this passing on a gate that reports nothing: the SAME word
    /// authored on a seat must be counted there.
    /// </summary>
    [Fact]
    public void An_inherited_bad_word_is_reported_once_at_its_source()
    {
        var rootOnly = EndpointProbe.BadPrioritiesOf(Shaped(rootPriority: "intractive"));
        Assert.Single(rootOnly);
        Assert.Equal("llm", rootOnly[0].Path);

        // Same word, authored in two places: now there are two authors, so two
        // errors. Without this arm the assertion above is satisfied by silence.
        var both = EndpointProbe.BadPrioritiesOf(
            Shaped(rootPriority: "intractive", nestedPriority: "intractive"));
        Assert.Equal(2, both.Count);
        Assert.Contains(both, g => g.Path == "llm");
        Assert.Contains(both, g => g.Path.Contains("nested", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⛔ THE ANTI-DRIFT TEST. The validator must not restate the legal words:
    /// if it did, validate could go green on a word the runtime rejects, or red
    /// on one it accepts, and nothing would notice. Asserting the reported text
    /// IS the thrown text pins that they come from one source.
    /// </summary>
    [Fact]
    public void The_reported_message_is_the_runtime_s_own_not_a_restatement()
    {
        var thrown = Assert.Throws<VettException>(() => CapacityBinding.PriorityOf("intractive"));
        var bad = Assert.Single(EndpointProbe.BadPrioritiesOf(Shaped(rootPriority: "intractive")));
        Assert.Equal(thrown.Message, bad.Message);
    }

    /// <summary>
    /// The gate reports what the runtime would do, so anything it passes must
    /// actually survive <c>PriorityOf</c>. This is the round trip: no word is
    /// cleared by validate and then rejected at run time.
    /// </summary>
    [Theory]
    [InlineData("interactive")]
    [InlineData("batch")]
    [InlineData("background")]
    [InlineData("")]
    public void Anything_the_gate_clears_the_runtime_also_accepts(string word)
    {
        Assert.Empty(EndpointProbe.BadPrioritiesOf(Shaped(rootPriority: word)));
        CapacityBinding.PriorityOf(word);   // must not throw
    }
}
