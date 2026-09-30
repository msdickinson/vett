using Vett.Config;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// The served-window join in <c>EndpointProbe.ParseCatalogue</c>.
///
/// WHY THESE EXIST. This one method decides the number that
/// `vett capacity probe` writes into the catalogue, and the catalogue's
/// window invariant then HONOURS that number when granting a seat. So an
/// over-promise here is not a cosmetic reporting error -- it is a grant the
/// endpoint refuses at request time, which is the exact failure the capacity
/// layer exists to prevent.
///
/// Both defects below were found by checking the probe against a raw curl of
/// the same URL on 2026-08-28, not by a failing run. Verify the tool, not
/// just the artifact.
/// </summary>
public class ServedWindowParsingTests
{
    /// <summary>
    /// ⛔ THE OPTIMISTIC NUMBER IS LISTED FIRST.
    ///
    /// OpenRouter publishes the MODEL's advertised ceiling as
    /// <c>context_length</c> and what the provider that will actually serve
    /// the request offers as <c>top_provider.context_length</c>. Measured on
    /// deepseek/deepseek-v4-flash: 1048576 vs 1024000. Taking the first
    /// candidate found writes the 24576-token over-promise.
    /// </summary>
    [Fact]
    public void TakesTheNarrowestWindow_NotTheFirstOneFound()
    {
        var body = """
            {"data":[{"id":"deepseek/deepseek-v4-flash",
                      "context_length":1048576,
                      "top_provider":{"context_length":1024000}}]}
            """;

        var r = EndpointProbe.ParseCatalogue(body);

        Assert.Null(r.Failure);
        Assert.Equal(1024000, r.Contexts["deepseek/deepseek-v4-flash"]);
    }

    /// <summary>
    /// ⛔ <c>TryGetInt32</c> THROWS on a JSON null — it returns false only for
    /// a number that will not fit. Some of OpenRouter's 398 listed models
    /// carry <c>top_provider: { context_length: null }</c>, which crashed
    /// `vett capacity probe` outright the first time the fix above ran.
    ///
    /// The surviving model in the same payload is the load-bearing half: a
    /// guard that swallowed the null by abandoning the whole parse would pass
    /// a crash test while silently reporting an empty catalogue.
    /// </summary>
    [Fact]
    public void ANullContextLengthDoesNotThrow_AndDoesNotStopTheParse()
    {
        var body = """
            {"data":[{"id":"someone/null-window","context_length":null,
                      "top_provider":{"context_length":null}},
                     {"id":"deepseek/deepseek-v4-pro",
                      "context_length":1048576,
                      "top_provider":{"context_length":1024000}}]}
            """;

        var r = EndpointProbe.ParseCatalogue(body);

        Assert.Null(r.Failure);
        Assert.Contains("someone/null-window", r.ModelIds);
        Assert.False(r.Contexts.ContainsKey("someone/null-window"));
        Assert.Equal(1024000, r.Contexts["deepseek/deepseek-v4-pro"]);
    }

    /// <summary>
    /// The local vLLM shape, which publishes <c>max_model_len</c> and neither
    /// of the OpenRouter fields. NEGATIVE CONTROL for the join: without it,
    /// the two tests above would pass just as confidently if the
    /// <c>max_model_len</c> branch had been deleted, and every local provider
    /// in the catalogue reads through that branch.
    /// </summary>
    [Fact]
    public void ReadsMaxModelLen_TheLocalVllmShape()
    {
        var body = """
            {"data":[{"id":"deepseek-v4-flash","max_model_len":131072},
                     {"id":"aeon-mtp","max_model_len":131072}]}
            """;

        var r = EndpointProbe.ParseCatalogue(body);

        Assert.Null(r.Failure);
        Assert.Equal(131072, r.Contexts["deepseek-v4-flash"]);
        Assert.Equal(131072, r.Contexts["aeon-mtp"]);
    }

    /// <summary>
    /// A model that publishes no window at all must be LISTED but carry no
    /// context. "Could not measure" is not "measured zero": the caller
    /// distinguishes them, and collapsing the two would let an unmeasured
    /// endpoint read as a hard 0-token ceiling.
    /// </summary>
    [Fact]
    public void AModelWithNoWindowIsListedButUnmeasured()
    {
        var r = EndpointProbe.ParseCatalogue("""{"data":[{"id":"bare/model"}]}""");

        Assert.Null(r.Failure);
        Assert.Contains("bare/model", r.ModelIds);
        Assert.False(r.Contexts.ContainsKey("bare/model"));
    }
}
