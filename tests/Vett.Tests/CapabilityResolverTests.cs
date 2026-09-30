using Vett.Capacity;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// The scheduling rules, as executable statements.
///
/// Every test here corresponds to a decision that is currently made by
/// hand-editing an endpoint into a profile, where it is invisible until a run
/// behaves oddly. The point of moving them here is that a wrong answer now
/// fails loudly instead of costing money or wedging a session.
/// </summary>
public class CapabilityResolverTests
{
    private const int LocalWindow = 65_536;
    private const int LocalPool = 480_000;

    /// <summary>
    /// Mirrors the shipped catalogue's SHAPE (flash local+cloud, pro
    /// cloud-only) without depending on its exact numbers, so these tests
    /// keep testing the rules rather than the current tuning.
    /// </summary>
    private static CapabilityCatalogue Catalogue() => new()
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
                        Name = "local-gpu-1",
                        LocalityRaw = "local",
                        Endpoint = "http://gpu-1:8000/v1",
                        Model = "deepseek-v4-flash",
                        ServedWindowTokens = LocalWindow,
                        PoolTokens = LocalPool,
                        CompactionRatio = 0.75,
                    },
                    new CapabilityProvider
                    {
                        Name = "openrouter-flash",
                        LocalityRaw = "cloud",
                        Endpoint = "https://openrouter.ai/api/v1",
                        Model = "deepseek/deepseek-v4-flash",
                        ApiKeyEnv = "DEEPSEEK_API_KEY",
                        CompactionRatio = 0.75,
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
                        Name = "openrouter-pro",
                        LocalityRaw = "cloud",
                        Endpoint = "https://openrouter.ai/api/v1",
                        Model = "deepseek/deepseek-v4-pro",
                        ApiKeyEnv = "DEEPSEEK_API_KEY",
                        CompactionRatio = 0.78,
                    },
                },
            },
        },
    };

    private static Availability With(params (string Provider, ProviderAvailability State)[] states) =>
        new(states.ToDictionary(s => s.Provider, s => s.State));

    private static readonly ResolvePolicy CloudOk = new(AllowCloud: true);
    private static readonly ResolvePolicy LocalOnly = new(AllowCloud: false);

    // ---------------------------------------------------------------
    // Preference: local is already paid for.
    // ---------------------------------------------------------------

    [Fact]
    public void SmallFlashPrefersLocalEvenWhenCloudIsAllowed()
    {
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 32_000), CloudOk, Availability.AllFree);

        var bound = Assert.IsType<Resolution.Bound>(r);
        Assert.Equal("local-gpu-1", bound.Provider.Name);
        Assert.Equal(Locality.Local, bound.Provider.Locality);
    }

    [Fact]
    public void FlashSpillsToCloudWhenTheLocalPoolCannotFitTheRequest()
    {
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 32_000), CloudOk,
            With(("local-gpu-1", new ProviderAvailability(FreeTokens: 8_000))));

        var bound = Assert.IsType<Resolution.Bound>(r);
        Assert.Equal("openrouter-flash", bound.Provider.Name);
    }

    // ---------------------------------------------------------------
    // "A cloud may run out of cash on a flash model — then wait for local."
    // ---------------------------------------------------------------

    [Fact]
    public void FlashFallsBackToLocalWhenTheCloudBudgetIsSpent()
    {
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 32_000), CloudOk,
            With(("openrouter-flash", new ProviderAvailability(BlockedReason: "spend budget exhausted"))));

        var bound = Assert.IsType<Resolution.Bound>(r);
        Assert.Equal("local-gpu-1", bound.Provider.Name);
    }

    [Fact]
    public void FlashWaitsRatherThanFailingWhenCloudIsSpentAndLocalIsFull()
    {
        // Both doors shut, but both can reopen — so this is a QUEUE, not a
        // refusal. Getting this wrong in the other direction (refusing) would
        // abort runs that only needed to wait a minute.
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 32_000), CloudOk,
            With(("local-gpu-1", new ProviderAvailability(FreeTokens: 0)),
                 ("openrouter-flash", new ProviderAvailability(BlockedReason: "spend budget exhausted"))));

        var wait = Assert.IsType<Resolution.Wait>(r);
        Assert.Contains("local-gpu-1", wait.WaitingOn);
        Assert.Contains("openrouter-flash", wait.WaitingOn);
    }

    [Fact]
    public void LocalOnlyPolicyWaitsForLocalInsteadOfReachingForCloud()
    {
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 32_000), LocalOnly,
            With(("local-gpu-1", new ProviderAvailability(FreeTokens: 0))));

        var wait = Assert.IsType<Resolution.Wait>(r);
        Assert.Equal(new[] { "local-gpu-1" }, wait.WaitingOn);
    }

    // ---------------------------------------------------------------
    // "But we can't for the pro — those have to wait."
    // The load-bearing rule: pro NEVER resolves to a flash provider.
    // ---------------------------------------------------------------

    [Fact]
    public void ProWaitsForItsOwnProviderAndIsNeverServedByFlash()
    {
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("pro", 32_000), CloudOk,
            With(("openrouter-pro", new ProviderAvailability(BlockedReason: "spend budget exhausted"))));

        var wait = Assert.IsType<Resolution.Wait>(r);
        Assert.Equal(new[] { "openrouter-pro" }, wait.WaitingOn);
        Assert.DoesNotContain("flash", string.Join(",", wait.WaitingOn));
    }

    [Fact]
    public void ProIsDeniedNotSubstitutedWhenCloudIsForbidden()
    {
        // Pro has no local provider at any size, so a local-only policy can
        // never be satisfied. Denied, not Wait: waiting would block forever.
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("pro", 32_000), LocalOnly, Availability.AllFree);

        var denied = Assert.IsType<Resolution.Denied>(r);
        Assert.Contains("cloud is not permitted", denied.Reason);
    }

    [Fact]
    public void NoResolutionOfProEverNamesAFlashProvider()
    {
        // Swept across every world state that matters, because the failure
        // this guards is silent: a pro request quietly answered by flash
        // returns a plausible answer to a different question.
        var flashProviders = new[] { "local-gpu-1", "openrouter-flash" };
        var worlds = new[]
        {
            Availability.AllFree,
            With(("openrouter-pro", new ProviderAvailability(BlockedReason: "spent"))),
            With(("openrouter-pro", new ProviderAvailability(FreeTokens: 0))),
            With(("openrouter-pro", new ProviderAvailability(BlockedReason: "unreachable")),
                 ("local-gpu-1", new ProviderAvailability(FreeTokens: LocalPool))),
        };

        foreach (var policy in new[] { CloudOk, LocalOnly })
        foreach (var world in worlds)
        {
            var r = CapabilityResolver.Resolve(
                Catalogue(), new CapabilityRequest("pro", 32_000), policy, world);

            if (r is Resolution.Bound b)
            {
                Assert.DoesNotContain(b.Provider.Name, flashProviders);
                Assert.Equal("deepseek/deepseek-v4-pro", b.Provider.Model);
            }
            if (r is Resolution.Wait w)
            {
                Assert.All(w.WaitingOn, n => Assert.DoesNotContain(n, flashProviders));
            }
        }
    }

    // ---------------------------------------------------------------
    // Substitutability is a property of (capability, SIZE), not of the
    // capability alone. A large flash request loses its local door.
    // ---------------------------------------------------------------

    [Fact]
    public void LargeContextFlashCannotUseLocalBecauseTheWindowIsTooSmall()
    {
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 100_000), CloudOk, Availability.AllFree);

        var bound = Assert.IsType<Resolution.Bound>(r);
        Assert.Equal("openrouter-flash", bound.Provider.Name);
        Assert.Contains("no local provider can serve", bound.Rationale);
    }

    [Fact]
    public void LargeContextFlashIsDeniedUnderLocalOnlyBecauseWaitingCannotHelp()
    {
        // The local endpoint will never grow a bigger window by waiting, so
        // this is a refusal even though flash "has a local provider".
        var r = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 100_000), LocalOnly, Availability.AllFree);

        var denied = Assert.IsType<Resolution.Denied>(r);
        Assert.Contains("serves at most 65536", denied.Reason);
    }

    [Fact]
    public void FlashIsSubstitutableAtSmallSizesAndNotAtLargeOnes()
    {
        var flash = Catalogue().Find("flash")!;
        Assert.True(flash.IsSubstitutableAt(32_000));
        Assert.False(flash.IsSubstitutableAt(100_000));
    }

    // ---------------------------------------------------------------
    // The threshold binds to the GRANT. This is the rule whose violation
    // kills a run outright rather than degrading it.
    // ---------------------------------------------------------------

    [Fact]
    public void CompactionThresholdIsDerivedFromTheGrantNotTheServerCeiling()
    {
        var small = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 32_000), CloudOk, Availability.AllFree);
        var full = CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", LocalWindow), CloudOk, Availability.AllFree);

        // 0.75 of the GRANT, not a fixed profile constant.
        Assert.Equal(24_000, Assert.IsType<Resolution.Bound>(small).CompactionThreshold);
        // 0.75 of 65536 reproduces the 48000 the profiles hand-tuned.
        Assert.Equal(49_152, Assert.IsType<Resolution.Bound>(full).CompactionThreshold);
    }

    [Fact]
    public void ThresholdAlwaysSitsStrictlyInsideTheGrant()
    {
        // The exact failure from ds-solo-flash.yaml:151 — a trigger at or
        // above the window can never fire, so the run dies on context length
        // instead of compacting.
        foreach (var size in new[] { 4_000, 16_000, 32_000, 48_000, LocalWindow })
        {
            var r = CapabilityResolver.Resolve(
                Catalogue(), new CapabilityRequest("flash", size), CloudOk, Availability.AllFree);
            var b = Assert.IsType<Resolution.Bound>(r);
            Assert.True(b.CompactionThreshold > 0, $"grant {size} produced a non-positive trigger");
            Assert.True(b.CompactionThreshold < b.GrantedTokens,
                $"grant {size} produced trigger {b.CompactionThreshold}, which could never fire");
        }
    }

    [Fact]
    public void AProviderWhoseThresholdCannotBeResolvedSafelyIsNotEligible()
    {
        var cat = Catalogue();
        cat.Find("flash")!.Providers[0].CompactionRatio = 1.5; // outside (0,1)

        var r = CapabilityResolver.Resolve(
            cat, new CapabilityRequest("flash", 32_000), LocalOnly, Availability.AllFree);

        var denied = Assert.IsType<Resolution.Denied>(r);
        Assert.Contains("not strictly between 0 and 1", denied.Reason);
    }

    // ---------------------------------------------------------------
    // Measurement honesty.
    // ---------------------------------------------------------------

    [Fact]
    public void AnUnmeasuredWindowIsExcludedOnlyWhenTheRunDemandsMeasurement()
    {
        var req = new CapabilityRequest("flash", 100_000);

        // Default: unmeasured cloud is allowed to try. Absence of a
        // measurement is not evidence of a small ceiling.
        Assert.IsType<Resolution.Bound>(
            CapabilityResolver.Resolve(Catalogue(), req, CloudOk, Availability.AllFree));

        // Strict: a run that cannot tolerate a guess drops it instead.
        var strict = new ResolvePolicy(AllowCloud: true, RequireMeasuredWindow: true);
        var denied = Assert.IsType<Resolution.Denied>(
            CapabilityResolver.Resolve(Catalogue(), req, strict, Availability.AllFree));
        Assert.Contains("never been measured", denied.Reason);
    }

    [Fact]
    public void UnknownCapabilityIsDeniedAndListsWhatDoesExist()
    {
        var denied = Assert.IsType<Resolution.Denied>(CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("ultra", 1_000), CloudOk, Availability.AllFree));

        Assert.Contains("flash", denied.Reason);
        Assert.Contains("pro", denied.Reason);
    }

    [Fact]
    public void ANonPositiveContextRequestIsRefused()
    {
        Assert.IsType<Resolution.Denied>(CapabilityResolver.Resolve(
            Catalogue(), new CapabilityRequest("flash", 0), CloudOk, Availability.AllFree));
    }
}
