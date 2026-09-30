using System.Reflection;
using Vett.Config;
using Vett.Llm;
using YamlDotNet.Serialization;

namespace Vett.Tests;

/// <summary>
/// ⭐⭐ DOES <c>ChatClientFactory.Merge</c> STILL SPAN EVERY FIELD OF THE THING
/// IT MERGES?
///
/// The existing merge tests are one-per-field, each written the day its field
/// was added. That is a gate that grows a hole every time <c>LlmConfig</c>
/// grows a property: the author of field N+1 has no reason to look at a file
/// full of tests about fields 1..N, and nothing fails when they don't.
///
/// It had already happened. <c>max_output_tokens</c> was added to LlmConfig
/// and never added to Merge, so <c>Merge(base, anyOverride)</c> returned a
/// config whose MaxOutputTokens was null no matter what the base said. That is
/// exactly the failure mode this file's sibling describes — "not a crash, it is
/// silence" — and the per-field tests could not see it because none of them was
/// about that field.
///
/// ⛔ WHERE IT BIT, AND WHY IT WAS NEARLY INVISIBLE. Most seats never ask Merge
/// for this value: Coordinator reads it as
/// <c>m.Llm?.MaxOutputTokens ?? profile.Llm.MaxOutputTokens</c> at five sites,
/// a hand-written two-level fallback that happens to be correct. Merge's copy
/// of the answer is consumed in exactly one place that matters —
/// <c>RunNestedTeamAsync</c> (Coordinator.cs:449) builds a NESTED sub-team's
/// profile with <c>Llm = Merge(parentProfile.Llm, m.Llm)</c>. The sub-team then
/// reads its bound off that merged config. So a nested team under a profile
/// that sets <c>max_output_tokens: 4096</c> ran with NO output ceiling, and
/// only when the sub-team's own member carried an <c>llm:</c> block (a null
/// override short-circuits Merge and returns the base untouched).
///
/// ⛔ AND THE SECOND CONSUMER IS INERT, WHICH IS WHY "IT'S USED IN TWO PLACES"
/// WOULD HAVE BEEN THE WRONG READ. Coordinator.cs:2331 merges only to build an
/// IChatClient, and ChatClientFactory.Create never reads MaxOutputTokens — the
/// bound travels per-request through LlmSettings. Counting call sites would
/// have doubled the apparent blast radius; reading what each one does with the
/// result is what sized it correctly.
///
/// The gate below is written against the TYPE, not against a list of field
/// names, so field 15 cannot repeat this.
/// </summary>
public class LlmConfigMergeCoverageTests
{
    /// <summary>
    /// Every YAML-bound property of LlmConfig, discovered by reflection. A
    /// hard-coded list here would be the same hole one level up.
    /// </summary>
    private static PropertyInfo[] YamlProperties() =>
        typeof(LlmConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<YamlMemberAttribute>() is not null)
            .Where(p => p.CanWrite)
            .OrderBy(p => p.Name)
            .ToArray();

    /// <summary>
    /// A base config with EVERY yaml-bound field set to a value distinguishable
    /// from the type default, so "was it carried?" is answerable per field.
    /// </summary>
    private static LlmConfig FullyPopulatedBase() => new()
    {
        Provider = "openai",
        Endpoint = "https://example.invalid/v1",
        Model = "base-model",
        ApiKeyEnv = "BASE_KEY",
        Temperature = 0.11,
        TopP = 0.22,
        RequestTimeoutSeconds = 111,
        NumRetries = 7,
        MaxOutputTokens = 4096,
        // law 135: bool? whose type default (false) is the OPT-OUT; true is
        // the only value IsTypeDefault can tell from unset.
        Stream = true,
        EnableThinking = true,
        Capability = "base-capability",
        Priority = "base-priority",
        ContextTokens = 65_536,
        // Non-zero AND distinguishable from each other: IsTypeDefault treats
        // 0.0 as unset, and two fields sharing a value would let a merge that
        // crossed them over pass this gate.
        PresencePenalty = 0.33,
        FrequencyPenalty = 0.44,
        Fallbacks = [new LlmConfig { Model = "fallback-model" }],
    };

    [Fact]
    public void FullyPopulatedBase_ActuallyPopulatesEveryYamlField()
    {
        // ⛔ THE FIXTURE IS PART OF THE GATE. If a new property is added to
        // LlmConfig and NOT added to FullyPopulatedBase, the spanning test
        // below would compare default-to-default and pass vacuously on the one
        // field it was supposed to be watching. This asserts the fixture spans
        // the type before the gate leans on it.
        var b = FullyPopulatedBase();
        var unset = YamlProperties()
            .Where(p => IsTypeDefault(p.GetValue(b)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(unset.Count == 0,
            "FullyPopulatedBase() leaves these LlmConfig fields at their type default, so the " +
            "merge-coverage gate cannot see them: " + string.Join(", ", unset) +
            ". Set each to a distinctive value.");
    }

    private static bool IsTypeDefault(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0,
        int i => i == 0,
        double d => d == 0.0,
        bool b => b == false,
        _ => false,
    };

    [Fact]
    public void Merge_WithAnEmptyOverride_CarriesEveryFieldOfTheBase()
    {
        // ⭐ THE WHOLE GATE, IN ONE SHAPE. An override object that sets NOTHING
        // is still non-null, so Merge does not short-circuit — it rebuilds the
        // config field by field, and any field it forgot to name comes back at
        // its type default. "Inherit everything" is the one case with no
        // legitimate exceptions: same provider, same endpoint, so even
        // ApiKeyEnv (deliberately host-scoped) inherits here.
        var baseConfig = FullyPopulatedBase();
        var merged = ChatClientFactory.Merge(baseConfig, new LlmConfig());

        var dropped = new List<string>();
        foreach (var p in YamlProperties())
        {
            var expected = p.GetValue(baseConfig);
            var actual = p.GetValue(merged);
            if (!Equals(Describe(expected), Describe(actual)))
                dropped.Add($"{p.Name}: base={Describe(expected)} merged={Describe(actual)}");
        }

        Assert.True(dropped.Count == 0,
            "ChatClientFactory.Merge DROPPED " + dropped.Count + " field(s) that an empty " +
            "override should have left alone. Every dropped field is silently lost by every " +
            "seat that inherits through Merge — including a nested sub-team's whole profile " +
            "(Coordinator.RunNestedTeamAsync). Add the field to Merge:\n  " +
            string.Join("\n  ", dropped));
    }

    private static string Describe(object? v) => v switch
    {
        null => "<null>",
        List<LlmConfig> l => "[" + string.Join(",", l.Select(x => x.Model)) + "]",
        _ => v.ToString() ?? "<null>",
    };

    // ---------------------------------------------------------------- the field

    [Fact]
    public void Merge_MaxOutputTokens_InheritsFromTheBase()
    {
        var merged = ChatClientFactory.Merge(
            new LlmConfig { Model = "m", MaxOutputTokens = 4096 },
            new LlmConfig { Temperature = 0.3 });

        Assert.Equal(4096, merged.MaxOutputTokens);
    }

    [Fact]
    public void Merge_MaxOutputTokens_ExplicitSeatValueWins()
    {
        var merged = ChatClientFactory.Merge(
            new LlmConfig { Model = "m", MaxOutputTokens = 4096 },
            new LlmConfig { MaxOutputTokens = 512 });

        Assert.Equal(512, merged.MaxOutputTokens);
    }

    [Fact]
    public void Merge_MaxOutputTokens_UnsetOnBothStaysNull()
    {
        // null is meaningful: it is what "unbounded generation" looks like, and
        // it must NOT be manufactured into a number by the merge.
        var merged = ChatClientFactory.Merge(
            new LlmConfig { Model = "m" },
            new LlmConfig { Temperature = 0.3 });

        Assert.Null(merged.MaxOutputTokens);
    }

    [Fact]
    public void Merge_MaxOutputTokens_AgreesWithTheCoordinatorsHandWrittenRule()
    {
        // ⭐ TWO RULES FOR ONE FIELD, AND THEY MUST NOT DRIFT. Coordinator reads
        // the bound as `m.Llm?.MaxOutputTokens ?? profile.Llm.MaxOutputTokens`
        // at five sites; Merge computes its own answer for the nested-team path.
        // A future edit to either one that does not touch the other reintroduces
        // exactly the divergence this file was opened to fix, so the equivalence
        // is asserted rather than assumed — across the four cases that exist.
        int?[] baseVals = [null, 4096];
        int?[] seatVals = [null, 512];
        foreach (var b in baseVals)
        foreach (var s in seatVals)
        {
            var profileLlm = new LlmConfig { Model = "m", MaxOutputTokens = b };
            var seatLlm = new LlmConfig { MaxOutputTokens = s };
            var coordinatorRule = seatLlm.MaxOutputTokens ?? profileLlm.MaxOutputTokens;
            var mergeRule = ChatClientFactory.Merge(profileLlm, seatLlm).MaxOutputTokens;

            Assert.True(coordinatorRule == mergeRule,
                $"base={b?.ToString() ?? "null"} seat={s?.ToString() ?? "null"}: " +
                $"coordinator says {coordinatorRule?.ToString() ?? "null"}, " +
                $"merge says {mergeRule?.ToString() ?? "null"}");
        }
    }

    // ------------------------------------------------ fields fifteen and sixteen

    [Fact]
    public void Merge_Penalties_InheritFromTheBase()
    {
        var merged = ChatClientFactory.Merge(
            new LlmConfig { Model = "m", PresencePenalty = 0.33, FrequencyPenalty = 0.44 },
            new LlmConfig { Temperature = 0.3 });

        Assert.Equal(0.33, merged.PresencePenalty);
        Assert.Equal(0.44, merged.FrequencyPenalty);
    }

    [Fact]
    public void Merge_Penalties_ExplicitSeatValueWins_AndTheyDoNotCross()
    {
        var merged = ChatClientFactory.Merge(
            new LlmConfig { Model = "m", PresencePenalty = 0.33, FrequencyPenalty = 0.44 },
            new LlmConfig { PresencePenalty = 1.5, FrequencyPenalty = -0.5 });

        Assert.Equal(1.5, merged.PresencePenalty);
        Assert.Equal(-0.5, merged.FrequencyPenalty);
    }

    [Fact]
    public void Merge_Penalties_UnsetOnBothStayNull()
    {
        // null is what "omit the parameter, let the provider default" looks
        // like (vLLM: 0). A merge that manufactured 0.0 here would be sending
        // an explicit penalty no profile asked for.
        var merged = ChatClientFactory.Merge(
            new LlmConfig { Model = "m" },
            new LlmConfig { Temperature = 0.3 });

        Assert.Null(merged.PresencePenalty);
        Assert.Null(merged.FrequencyPenalty);
    }

    [Fact]
    public void Merge_Penalties_AgreeWithTheCoordinatorsHandWrittenRule()
    {
        // Same two-rules-for-one-field hazard as MaxOutputTokens above: the
        // Coordinator reads these as `m.Llm?.X ?? profile.Llm.X` at five sites
        // and was correct without Merge for the whole time Merge was silently
        // dropping them.
        double?[] baseVals = [null, 0.33];
        double?[] seatVals = [null, 1.5];
        foreach (var b in baseVals)
        foreach (var s in seatVals)
        {
            var profileLlm = new LlmConfig { Model = "m", PresencePenalty = b };
            var seatLlm = new LlmConfig { PresencePenalty = s };
            var coordinatorRule = seatLlm.PresencePenalty ?? profileLlm.PresencePenalty;
            var mergeRule = ChatClientFactory.Merge(profileLlm, seatLlm).PresencePenalty;

            Assert.True(coordinatorRule == mergeRule,
                $"base={b?.ToString() ?? "null"} seat={s?.ToString() ?? "null"}: " +
                $"coordinator says {coordinatorRule?.ToString() ?? "null"}, " +
                $"merge says {mergeRule?.ToString() ?? "null"}");
        }
    }
}
