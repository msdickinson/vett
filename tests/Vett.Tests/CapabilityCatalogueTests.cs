using Vett.Capacity;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Checks on the catalogue FILE WE SHIP, plus the structural rules that keep
/// a malformed one from producing confident, wrong bindings at runtime.
/// </summary>
public class CapabilityCatalogueTests
{
    private static string ShippedPath()
    {
        // Walk up to the repo root rather than assuming a working directory:
        // the test host's cwd is the test binary's folder, not the solution.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "capabilities", "default.yaml")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "capabilities", "default.yaml");
    }

    private static string TempYaml(string body)
    {
        var p = Path.Combine(Path.GetTempPath(), "vett-cat-" + Guid.NewGuid().ToString("n")[..8] + ".yaml");
        File.WriteAllText(p, body);
        return p;
    }

    // ---------------------------------------------------------------
    // The shipped file. If these drift, scheduling drifts with them.
    // ---------------------------------------------------------------

    [Fact]
    public void ShippedCatalogueLoadsAndDescribesTheRealDeployment()
    {
        var cat = CatalogueLoader.Load(ShippedPath());

        var flash = cat.Find("flash");
        var pro = cat.Find("pro");
        Assert.NotNull(flash);
        Assert.NotNull(pro);

        // Flash exists on both sides; pro is cloud-only. This asymmetry is
        // the whole reason a pro shortage means "wait" and a small-flash
        // shortage means "try the other door".
        Assert.True(flash!.HasLocal);
        Assert.True(flash.HasCloud);
        Assert.False(pro!.HasLocal);
        Assert.True(pro.HasCloud);
    }

    [Fact]
    public void ShippedLocalProviderMatchesTheMeasuredEngine()
    {
        var local = CatalogueLoader.Load(ShippedPath()).Find("flash")!
            .Providers.Single(p => p.Locality == Locality.Local);

        // Measured on gpu-1. max_model_len is a LAUNCH FLAG, not a property of
        // the weights, so this pins what the server was STARTED with, and a
        // restart that changes it should fail here loudly.
        //
        // ⚠ IT HAS DONE EXACTLY THAT, AND THIS PIN IS WHY WE NOTICED. Three
        // readings so far: 131072 (2026-07-12) → 65536 (2026-08-23, the
        // Minecraft containers co-residing in GB10 unified memory forced
        // gpu-memory-utilization down) → 131072 again (2026-08-28, after a
        // restart). The last two share a calendar date, so they are told
        // apart by the KV pool that moved with them, not by the date:
        // 629666 tokens at the 65536 reading, 1408266 at this one. All of
        // the numbers moving together is the signature of a RESTART; one
        // moving alone would be something else and worth investigating.
        //
        // Do NOT "fix" a failure here by editing the constant. Re-probe
        // first (`vett capacity probe`), then move the catalogue and this
        // pin together — they are one claim about one server process.
        Assert.Equal(131_072, local.ServedWindowTokens);
        Assert.Equal("deepseek-v4-flash", local.Model);
        Assert.Equal("http://gpu-1:8000/v1", local.Endpoint);

        // Never the stale alias: gpu-1 also answers to `aeon-mtp`, but that is
        // a second NAME for this same engine, not a second model.
        Assert.DoesNotContain("aeon", local.Model);

        // We hand out less than the 1408266 tokens measured in the KV pool
        // (vllm:cache_config_info on :8000/metrics, same 2026-08-28 reading
        // as the window above; it was 629666 alongside the 65536 window).
        Assert.NotNull(local.PoolTokens);
        Assert.True(local.PoolTokens < 1_408_266,
            "the published pool must leave headroom: vLLM never refuses an oversubscription, "
            + "it preempts, so overcommitting shows up as latency collapse rather than an error");
    }

    [Fact]
    public void NoShippedProviderCarriesAnApiKeyValueInTheFile()
    {
        var text = File.ReadAllText(ShippedPath());
        Assert.DoesNotContain("sk-or-v1", text);
        Assert.DoesNotContain("sk-", text.Replace("sk-or-v1", ""));

        foreach (var p in CatalogueLoader.Load(ShippedPath()).Capabilities.SelectMany(c => c.Providers))
        {
            // Only ever the NAME of an env var.
            Assert.True(p.ApiKeyEnv.Length == 0 || p.ApiKeyEnv.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'));
        }
    }

    [Fact]
    public void EveryCloudProviderInTheShippedCatalogueNamesAKeyEnvAndEveryLocalOneDoesNot()
    {
        foreach (var p in CatalogueLoader.Load(ShippedPath()).Capabilities.SelectMany(c => c.Providers))
        {
            if (p.Locality == Locality.Cloud)
                Assert.False(string.IsNullOrEmpty(p.ApiKeyEnv), $"cloud provider '{p.Name}' names no key env");
            else
                Assert.True(string.IsNullOrEmpty(p.ApiKeyEnv), $"local provider '{p.Name}' should need no key");
        }
    }

    // ---------------------------------------------------------------
    // Structural validation.
    // ---------------------------------------------------------------

    [Fact]
    public void ALocalProviderWithoutAPoolIsRejected()
    {
        var p = TempYaml("""
            capabilities:
              - name: flash
                providers:
                  - name: local-x
                    locality: local
                    endpoint: http://x/v1
                    model: m
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => CatalogueLoader.Load(p));
        Assert.Contains("pool_tokens", ex.Message);
        File.Delete(p);
    }

    [Fact]
    public void AnAmbiguousLocalityIsRejectedRatherThanGuessed()
    {
        // Guessing here is exactly how a paid endpoint ends up charged
        // against the local KV budget.
        var p = TempYaml("""
            capabilities:
              - name: flash
                providers:
                  - name: x
                    locality: onprem
                    endpoint: http://x/v1
                    model: m
                    pool_tokens: 1000
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => CatalogueLoader.Load(p));
        Assert.Contains("must be", ex.Message);
        File.Delete(p);
    }

    [Fact]
    public void ADuplicateProviderNameIsRejectedBecauseNamesKeyTheLedger()
    {
        var p = TempYaml("""
            capabilities:
              - name: flash
                providers:
                  - name: shared
                    locality: cloud
                    endpoint: https://a/v1
                    model: m
              - name: pro
                providers:
                  - name: shared
                    locality: cloud
                    endpoint: https://b/v1
                    model: n
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => CatalogueLoader.Load(p));
        Assert.Contains("merge unrelated pools", ex.Message);
        File.Delete(p);
    }

    [Fact]
    public void ACapabilityWithNoProvidersIsRejected()
    {
        var p = TempYaml("""
            capabilities:
              - name: ghost
                providers: []
            """);
        Assert.Throws<InvalidOperationException>(() => CatalogueLoader.Load(p));
        File.Delete(p);
    }
}
