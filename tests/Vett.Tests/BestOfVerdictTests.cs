using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// The verdict parser decides whether a diff LANDS. It FAILS CLOSED: anything
/// it cannot parse is NONE. A spurious NONE costs one re-dispatch; a spurious
/// apply puts wrong code in the tree.
/// </summary>
public class BestOfVerdictTests
{
    [Theory]
    [InlineData("reasoning...\nVERDICT: 2", 2)]
    [InlineData("VERDICT: 1", 1)]
    [InlineData("blah\nverdict: 3\n", 3)]
    public void Parses_Winner(string text, int expected)
    {
        var (winner, parsed) = BestOfTools.ParseVerdict(text, 3);
        Assert.True(parsed);
        Assert.Equal(expected, winner);
    }

    [Theory]
    [InlineData("all three are broken\nVERDICT: NONE")]
    [InlineData("VERDICT: none")]
    public void Parses_None(string text)
    {
        var (winner, parsed) = BestOfTools.ParseVerdict(text, 3);
        Assert.True(parsed);
        Assert.Null(winner);      // NONE is a real answer
    }

    [Theory]
    [InlineData("")]                                   // judge produced nothing
    [InlineData("I like candidate 2 the best")]        // no VERDICT line at all
    [InlineData("VERDICT: 9")]                         // out of range
    [InlineData("VERDICT: maybe")]                     // garbage
    [InlineData("(judge failed: endpoint died)")]      // judge crashed
    public void FailsClosed_To_None(string text)
    {
        var (winner, _) = BestOfTools.ParseVerdict(text, 3);
        Assert.Null(winner);      // nothing is applied
    }

    [Fact]
    public void LastVerdict_Wins_WhenModelRestates()
    {
        // Models often restate: "...I was going to say VERDICT: 1 but VERDICT: 3"
        var (winner, parsed) = BestOfTools.ParseVerdict(
            "First I thought VERDICT: 1\nOn reflection candidate 3 is correct.\nVERDICT: 3", 3);
        Assert.True(parsed);
        Assert.Equal(3, winner);
    }
}
