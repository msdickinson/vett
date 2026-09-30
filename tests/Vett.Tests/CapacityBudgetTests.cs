using Vett.Capacity;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Spend budgets. A cloud provider's limit is money, not cache, and Mark's
/// requirement was that both shapes work: "$50 for pro and $50 for flash
/// instead of shared, or it may be shared — it may depend."
///
/// So the axis under test is WALLET IDENTITY. Two providers share a budget
/// exactly when they name the same one; omitting the name gives a provider its
/// own. The tests below pin both shapes, and pin that the default — say nothing
/// about money — still behaves as it did before budgets existed.
/// </summary>
public class CapacityBudgetTests : IDisposable
{
    private readonly string _dir;

    public CapacityBudgetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-budget-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const int LocalPool = 100_000;
    private const int LocalWindow = 65_536;

    private CapacityBroker Broker(Func<DateTimeOffset>? now = null) => new(_dir, now, _ => true);

    private static readonly ResolvePolicy CloudOk = new(AllowCloud: true);

    /// <param name="flashBudget">budget named by the cloud FLASH provider</param>
    /// <param name="proBudget">budget named by the cloud PRO provider</param>
    /// <param name="limits">budget name → dollar cap; absent means declared but uncapped</param>
    private static CapabilityCatalogue Catalogue(
        string flashBudget = "flash-cloud",
        string proBudget = "pro",
        params (string Name, decimal? Limit)[] limits)
    {
        var cat = new CapabilityCatalogue
        {
            Capabilities =
            {
                new Capability
                {
                    Name = "flash",
                    Providers =
                    {
                        new CapabilityProvider
                        {
                            Name = "local-gpu-1", LocalityRaw = "local",
                            Endpoint = "http://gpu-1:8000/v1", Model = "deepseek-v4-flash",
                            ServedWindowTokens = LocalWindow, PoolTokens = LocalPool, CompactionRatio = 0.75,
                        },
                        new CapabilityProvider
                        {
                            Name = "openrouter-flash", LocalityRaw = "cloud",
                            Endpoint = "https://openrouter.ai/api/v1", Model = "deepseek/deepseek-v4-flash",
                            ApiKeyEnv = "DEEPSEEK_API_KEY", CompactionRatio = 0.75, BudgetRaw = flashBudget,
                        },
                    },
                },
                new Capability
                {
                    Name = "pro",
                    Providers =
                    {
                        new CapabilityProvider
                        {
                            Name = "openrouter-pro", LocalityRaw = "cloud",
                            Endpoint = "https://openrouter.ai/api/v1", Model = "deepseek/deepseek-v4-pro",
                            ApiKeyEnv = "DEEPSEEK_API_KEY", CompactionRatio = 0.78, BudgetRaw = proBudget,
                        },
                    },
                },
            },
        };

        foreach (var (name, limit) in limits)
            cat.Budgets.Add(new Budget { Name = name, LimitUsd = limit, StaleAfterHours = 24 });

        return cat;
    }

    /// <summary>Fill the local pool so a flash request has to reach for cloud.</summary>
    private void FillLocal(CapacityBroker broker, CapabilityCatalogue cat)
    {
        for (var i = 0; i < 8; i++)
            broker.Claim(cat, new CapabilityRequest("flash", 12_000), new ResolvePolicy(AllowCloud: false),
                Priority.Batch, $"filler-{i}");
    }

    // ---------------------------------------------------------------
    // The two shapes.
    // ---------------------------------------------------------------

    [Fact]
    public void SeparateBudgetsMeanSpendingOutFlashLeavesProUntouched()
    {
        var cat = Catalogue(limits: [("flash-cloud", 50m), ("pro", 50m)]);
        var broker = Broker();

        broker.RecordSpend("flash-cloud", 50m, "openrouter /credits");
        broker.RecordSpend("pro", 3m, "openrouter /credits");

        var avail = broker.BuildAvailability(cat, broker.Live());

        Assert.Contains("is spent", avail.For("openrouter-flash").BlockedReason ?? "");
        Assert.Null(avail.For("openrouter-pro").BlockedReason);
    }

    [Fact]
    public void OneSharedBudgetShutsBothProvidersAtOnce()
    {
        // The other shape Mark named: a single wallet across both models. Two
        // providers share it by naming the same budget — nothing else changes.
        var cat = Catalogue(flashBudget: "deepseek", proBudget: "deepseek", limits: [("deepseek", 50m)]);
        var broker = Broker();

        broker.RecordSpend("deepseek", 50m, "openrouter /credits");
        var avail = broker.BuildAvailability(cat, broker.Live());

        Assert.Contains("is spent", avail.For("openrouter-flash").BlockedReason ?? "");
        Assert.Contains("is spent", avail.For("openrouter-pro").BlockedReason ?? "");
    }

    [Fact]
    public void UnderSeparateBudgetsProSurvivesAFlashOverrunAndStillResolves()
    {
        // The behavioural half of the separate-wallets claim: not merely that
        // pro is "not blocked", but that a pro run actually binds while flash's
        // wallet is shut.
        var cat = Catalogue(limits: [("flash-cloud", 50m), ("pro", 50m)]);
        var broker = Broker();
        broker.RecordSpend("flash-cloud", 99m, "openrouter /credits");
        broker.RecordSpend("pro", 1m, "openrouter /credits");

        var pro = Assert.IsType<ClaimResult.Granted>(
            broker.Claim(cat, new CapabilityRequest("pro", 32_000), CloudOk, Priority.Batch, "reviewer"));
        Assert.Equal("openrouter-pro", pro.Lease.Provider);
    }

    // ---------------------------------------------------------------
    // The asymmetry, now driven by money rather than by cache.
    // ---------------------------------------------------------------

    [Fact]
    public void AnExhaustedFlashWalletFallsBackToLocalAtASmallSize()
    {
        // Mark's exact case: "a cloud may run out of cash on a flash model we
        // have and then we may just wait until it clears and use the local."
        // At a size local can serve, the wallet closing costs nothing at all.
        var cat = Catalogue(limits: [("flash-cloud", 50m)]);
        var broker = Broker();
        broker.RecordSpend("flash-cloud", 50m, "openrouter /credits");

        var g = Assert.IsType<ClaimResult.Granted>(
            broker.Claim(cat, new CapabilityRequest("flash", 32_000), CloudOk, Priority.Batch, "impl"));
        Assert.Equal("local-gpu-1", g.Lease.Provider);
    }

    [Fact]
    public void AnExhaustedFlashWalletWaitsWhenLocalIsAlsoFull()
    {
        var cat = Catalogue(limits: [("flash-cloud", 50m)]);
        var broker = Broker();
        broker.RecordSpend("flash-cloud", 50m, "openrouter /credits");
        FillLocal(broker, cat);

        // Waiting, not refusing: the wallet can be topped up and the local pool
        // drains on its own, so both doors reopen without anyone intervening.
        var q = Assert.IsType<ClaimResult.Queued>(
            broker.Claim(cat, new CapabilityRequest("flash", 32_000), CloudOk, Priority.Batch, "impl"));
        Assert.Contains("openrouter-flash", q.WaitingOn);
        Assert.Contains("local-gpu-1", q.WaitingOn);
    }

    [Fact]
    public void AnExhaustedProWalletWaitsAndIsNeverServedByFlash()
    {
        // Pro is cloud-only, so its wallet has no local alternative behind it.
        // The one thing that must never happen is a silent downgrade.
        var cat = Catalogue(limits: [("pro", 50m)]);
        var broker = Broker();
        broker.RecordSpend("pro", 50m, "openrouter /credits");

        var q = Assert.IsType<ClaimResult.Queued>(
            broker.Claim(cat, new CapabilityRequest("pro", 32_000), CloudOk, Priority.Batch, "reviewer"));

        Assert.All(q.WaitingOn, w => Assert.DoesNotContain("flash", w));
        Assert.Contains("openrouter-pro", q.WaitingOn);
    }

    // ---------------------------------------------------------------
    // Fail-closed. A spend gate that admits work it cannot price is not a gate.
    // ---------------------------------------------------------------

    [Fact]
    public void ADeclaredLimitWithNoReadingBlocksRatherThanAssumingNothingWasSpent()
    {
        var cat = Catalogue(limits: [("pro", 50m)]);
        var avail = Broker().BuildAvailability(cat, Array.Empty<Lease>());

        var why = avail.For("openrouter-pro").BlockedReason;
        Assert.Contains("no billing reading", why ?? "");
        // The message has to say what to run, or fail-closed is just a wall.
        Assert.Contains("vett capacity spend", why!);
    }

    [Fact]
    public void AStaleReadingIsTreatedAsNoReadingAtAll()
    {
        // A stale figure errs in the PERMISSIVE direction — it says $10 while
        // the real balance is $60 — which is the direction that spends money
        // nobody approved.
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = Broker(clock);
        var cat = Catalogue(limits: [("pro", 50m)]);

        broker.RecordSpend("pro", 1m, "openrouter /credits");
        Assert.Null(broker.BuildAvailability(cat, Array.Empty<Lease>()).For("openrouter-pro").BlockedReason);

        now = now.AddHours(25); // past the 24h staleness limit

        var why = broker.BuildAvailability(cat, Array.Empty<Lease>()).For("openrouter-pro").BlockedReason;
        Assert.Contains("stale", why ?? "");
    }

    [Fact]
    public void AnUnreadableSpendFileBlocksRatherThanReadingAsNothingSpent()
    {
        var cat = Catalogue(limits: [("pro", 50m), ("flash-cloud", 50m)]);
        var broker = Broker();
        broker.RecordSpend("pro", 1m, "openrouter /credits");

        File.WriteAllText(Path.Combine(_dir, "spend.json"), "{ this is not json");

        var avail = broker.BuildAvailability(cat, Array.Empty<Lease>());

        // Asserting merely that SOMETHING blocked would have no power here: a
        // broker that read the corrupt file as an empty ledger also blocks, on
        // the "no billing reading" rule. A mutation run proved exactly that —
        // deleting the poison record left this test green. So the reason has to
        // be pinned, not just the outcome.
        foreach (var provider in new[] { "openrouter-pro", "openrouter-flash" })
        {
            var why = avail.For(provider).BlockedReason;
            Assert.NotNull(why);
            Assert.Contains("unreadable", why!);
            Assert.Contains("NOT the same as", why);
        }
    }

    [Fact]
    public void AnUnreadableSpendFileIsNeverReportedAsNothingSpent()
    {
        // The case where "block" and "read as empty" genuinely diverge: an
        // operator listing spend. An empty list says "nothing has been spent",
        // which about a corrupt ledger is a confident, wrong answer.
        var broker = Broker();
        broker.RecordSpend("pro", 41m, "openrouter /credits");
        File.WriteAllText(Path.Combine(_dir, "spend.json"), "{ this is not json");

        var spend = broker.Spend();

        Assert.NotEmpty(spend);
        Assert.Contains(spend, r => r.Source.Contains("unreadable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ABudgetWithNoLimitNeverBlocksAnything()
    {
        // Budgets are opt-in. A catalogue that names budgets but caps none must
        // behave exactly as it did before budgets existed, or shipping the
        // feature would break every existing run.
        var cat = Catalogue(limits: [("flash-cloud", null), ("pro", null)]);
        var avail = Broker().BuildAvailability(cat, Array.Empty<Lease>());

        Assert.Null(avail.For("openrouter-flash").BlockedReason);
        Assert.Null(avail.For("openrouter-pro").BlockedReason);
    }

    [Fact]
    public void AProviderWhoseBudgetIsNotDeclaredAtAllIsUnlimited()
    {
        var cat = Catalogue(); // no budgets list at all
        var avail = Broker().BuildAvailability(cat, Array.Empty<Lease>());

        Assert.Null(avail.For("openrouter-flash").BlockedReason);
        Assert.Null(avail.For("openrouter-pro").BlockedReason);
    }

    // ---------------------------------------------------------------
    // The reading itself.
    // ---------------------------------------------------------------

    [Fact]
    public void ASpendReadingMustNameWhereItCameFrom()
    {
        // An unsourced number cannot gate a run: the whole point is that this
        // figure was READ from billing rather than computed from token counts,
        // and nothing but the source distinguishes the two.
        var broker = Broker();
        Assert.Throws<ArgumentException>(() => broker.RecordSpend("pro", 10m, ""));
        Assert.Throws<ArgumentException>(() => broker.RecordSpend("pro", -1m, "openrouter /credits"));
        Assert.Throws<ArgumentException>(() => broker.RecordSpend("", 10m, "openrouter /credits"));
    }

    [Fact]
    public void RecordingABudgetTwiceKeepsOnlyTheLatestReading()
    {
        var broker = Broker();
        broker.RecordSpend("pro", 10m, "first");
        broker.RecordSpend("pro", 22m, "second");

        var all = broker.Spend().Where(r => r.Budget == "pro").ToList();
        Assert.Single(all);
        Assert.Equal(22m, all[0].Usd);
        Assert.Equal("second", all[0].Source);
    }

    [Fact]
    public void SpendReadingsSurviveAcrossBrokerInstances()
    {
        // Same reason the lease ledger is on disk: two vett processes must see
        // one wallet, not two.
        Broker().RecordSpend("pro", 12.34m, "openrouter /credits");
        var seen = Broker().Spend().Single(r => r.Budget == "pro");
        Assert.Equal(12.34m, seen.Usd);
    }

    // ---------------------------------------------------------------
    // Catalogue validation.
    // ---------------------------------------------------------------

    [Fact]
    public void ATypoedBudgetNameIsRejectedRatherThanSilentlyMeaningUnlimited()
    {
        var p = Path.Combine(_dir, "typo.yaml");
        File.WriteAllText(p, """
            capabilities:
              - name: pro
                providers:
                  - name: openrouter-pro
                    locality: cloud
                    endpoint: https://openrouter.ai/api/v1
                    model: m
                    api_key_env: K
                    budget: prro
            budgets:
              - name: pro
                limit_usd: 50
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => CatalogueLoader.Load(p));
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("UNLIMITED", ex.Message);
    }

    [Fact]
    public void ADuplicateBudgetDeclarationIsRejected()
    {
        var p = Path.Combine(_dir, "dupe.yaml");
        File.WriteAllText(p, """
            capabilities:
              - name: pro
                providers:
                  - name: openrouter-pro
                    locality: cloud
                    endpoint: https://openrouter.ai/api/v1
                    model: m
                    api_key_env: K
            budgets:
              - name: pro
                limit_usd: 50
              - name: pro
                limit_usd: 500
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => CatalogueLoader.Load(p));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void ANonPositiveStalenessWindowIsRejected()
    {
        // It would make every reading stale on arrival, shutting the budget
        // permanently — a cap that looks configured and is actually a wall.
        var p = Path.Combine(_dir, "stale.yaml");
        File.WriteAllText(p, """
            capabilities:
              - name: pro
                providers:
                  - name: openrouter-pro
                    locality: cloud
                    endpoint: https://openrouter.ai/api/v1
                    model: m
                    api_key_env: K
            budgets:
              - name: pro
                limit_usd: 50
                stale_after_hours: 0
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => CatalogueLoader.Load(p));
        Assert.Contains("stale_after_hours", ex.Message);
    }

    // ---------------------------------------------------------------
    // The file we ship.
    // ---------------------------------------------------------------

    [Fact]
    public void TheShippedCatalogueDeclaresBudgetsButCapsNoneOfThem()
    {
        // Shipping a live cap would shut cloud on upgrade for everyone who has
        // not yet recorded a billing reading. The budgets are present and
        // documented; turning one on is a deliberate edit.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "capabilities", "default.yaml")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var cat = CatalogueLoader.Load(Path.Combine(dir!.FullName, "capabilities", "default.yaml"));

        Assert.NotEmpty(cat.Budgets);
        Assert.All(cat.Budgets, b => Assert.Null(b.LimitUsd));

        // And every cloud provider is attached to one, so turning a cap on is a
        // one-line change rather than a wiring exercise.
        foreach (var p in cat.Capabilities.SelectMany(c => c.Providers).Where(p => p.Locality == Locality.Cloud))
            Assert.NotNull(cat.FindBudget(p.BudgetName));
    }
}
