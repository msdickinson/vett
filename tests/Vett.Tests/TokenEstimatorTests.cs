using Microsoft.Extensions.AI;
using Vett.Llm;

namespace Vett.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void EstimatesNonZeroForText()
    {
        var tokens = TokenEstimator.Estimate("Hello, world! This is a test sentence.", null);

        Assert.True(tokens > 0);
    }

    [Fact]
    public void EmptyTextReturnsZero()
    {
        Assert.Equal(0, TokenEstimator.Estimate("", null));
    }

    [Fact]
    public void LongerTextHasMoreTokens()
    {
        var short_ = TokenEstimator.Estimate("hi");
        var long_ = TokenEstimator.Estimate(new string('x', 1000));

        Assert.True(long_ > short_);
    }

    [Fact]
    public void MessagesIncludeOverhead()
    {
        var messages = new List<ChatMessage>
        {
            Chat.System("You are helpful."),
            Chat.User("Hello"),
        };

        var tokens = TokenEstimator.Estimate(messages);

        // Should be more than just the text — includes per-message overhead.
        var textOnly = TokenEstimator.Estimate("You are helpful.") + TokenEstimator.Estimate("Hello");
        Assert.True(tokens > textOnly);
    }

    [Fact]
    public void DifferentModelsGiveDifferentEstimates()
    {
        var text = new string('a', 400);
        var gpt4 = TokenEstimator.Estimate(text, "gpt-4o");
        var claude = TokenEstimator.Estimate(text, "claude-sonnet");

        // ⚠ THIS ASSERTION WAS RIGHT FOR THE WRONG REASON UNTIL 2026-08-26. The
        // comment here read "3.5 vs 3.9" but the lookup was first-match-wins, so
        // "gpt-4o" actually matched the gpt-4 row and the real contrast was
        // 3.5 vs 3.8. The inequality held either way, which is precisely why the
        // dead row survived: a passing test whose stated basis is false looks
        // like coverage. The ratios are now pinned by name below rather than
        // inferred from an inequality.
        Assert.True(claude > gpt4);
    }

    [Fact]
    public void UnknownModelUsesDefault()
    {
        var text = new string('a', 400);
        var unknown = TokenEstimator.Estimate(text, "some-obscure-model");
        var default_ = TokenEstimator.Estimate(text, null);

        Assert.Equal(default_, unknown);
    }

    /// <summary>
    /// ⛔ THE ROW-REACHABILITY GATE.
    ///
    /// Every key in the ratio table is a SUBSTRING matched against the model
    /// name, so a key that contains another key shadows it. Before 2026-08-26
    /// the lookup returned on the first match while enumerating the dictionary,
    /// which made the <c>gpt-4o</c> row unreachable: <c>"gpt-4o"</c> contains
    /// <c>"gpt-4"</c>, the shorter key sat first, and so every gpt-4o model
    /// silently took 3.8 instead of its own 3.9.
    ///
    /// This gate states the general law rather than that one instance: a row
    /// that cannot be selected by its own key is dead config. It enumerates the
    /// real table, so it fails automatically the next time someone adds a key
    /// that shadows an existing one (say a <c>claude-4</c> row next to
    /// <c>claude</c>) — the class of bug, not the one specimen.
    /// </summary>
    [Fact]
    public void Every_ratio_row_is_reachable_by_its_own_key()
    {
        var rows = TokenEstimator.ModelRatios;

        // Liveness: a gate over an empty or gutted table would pass vacuously.
        Assert.True(rows.Count >= 8, $"ratio table has only {rows.Count} rows — did it get gutted?");

        var dead = rows
            .Where(r => TokenEstimator.RatioFor(r.Key) != r.Value)
            .Select(r => $"{r.Key}: table says {r.Value}, lookup returns {TokenEstimator.RatioFor(r.Key)}")
            .ToList();

        Assert.True(dead.Count == 0,
            $"{dead.Count} of {rows.Count} ratio rows are UNREACHABLE — shadowed by a shorter key:\n  "
            + string.Join("\n  ", dead));
    }

    /// <summary>
    /// The specimen, pinned by value on both sides of the shadowing pair. This
    /// is the test that actually reddens against the old first-match lookup:
    /// it returned 3.8 for BOTH names.
    /// </summary>
    [Theory]
    [InlineData("gpt-4o", 3.9)]          // the formerly-unreachable row
    [InlineData("gpt-4o-mini", 3.9)]     // longer name, same row
    [InlineData("gpt-4-turbo", 3.8)]     // the shorter row is still reachable
    [InlineData("gpt-4", 3.8)]
    public void The_longer_matching_key_wins(string model, double expected)
        => Assert.Equal(expected, TokenEstimator.RatioFor(model));

    /// <summary>
    /// Ties are broken ordinally, not by dictionary layout.
    ///
    /// <see cref="Dictionary{TKey,TValue}"/> does not promise an enumeration
    /// order, so a rule that stopped at "first match of the longest length"
    /// would still be a coin flip decided by the initialiser's line order. Two
    /// keys here are both 6 characters — <c>claude</c> and <c>gemini</c> — and a
    /// name containing both must resolve the same way on every run.
    /// </summary>
    [Fact]
    public void Equal_length_matches_break_ordinally_not_by_table_order()
    {
        Assert.Equal(TokenEstimator.ModelRatios["claude"], TokenEstimator.RatioFor("claude-gemini-hybrid"));
        Assert.Equal(TokenEstimator.ModelRatios["claude"], TokenEstimator.RatioFor("gemini-claude-hybrid"));
    }

    /// <summary>
    /// ⚠ DOCUMENTS A KNOWN GAP RATHER THAN PAPERING OVER IT.
    ///
    /// Every live profile runs <c>deepseek-v4-flash</c> or
    /// <c>deepseek/deepseek-v4-pro</c>, and there is no deepseek row, so every
    /// production seat takes the 4.0 default. Fixing the lookup above changed
    /// nothing for them — the reachability bug and the coverage gap are separate
    /// defects and only one of them is fixed.
    ///
    /// This test exists so that if someone later ADDS a deepseek row, it fails
    /// and forces them to say where the number came from. A ratio invented to
    /// make a table look complete is worse than a documented default, because
    /// the default is visibly a default and the invention is not.
    /// </summary>
    [Theory]
    [InlineData("deepseek-v4-flash")]
    [InlineData("deepseek/deepseek-v4-pro")]
    public void Production_models_take_the_uncalibrated_default(string model)
    {
        Assert.Equal(4.0, TokenEstimator.RatioFor(model));

        // …and 4.0 here IS the no-match default, not a deepseek row that happens
        // to hold 4.0. Stated separately so adding such a row still reddens.
        Assert.Equal(TokenEstimator.RatioFor(null), TokenEstimator.RatioFor(model));
        Assert.DoesNotContain(TokenEstimator.ModelRatios.Keys,
            k => model.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
