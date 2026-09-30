using System.ClientModel.Primitives;
using System.Reflection;
using Vett.Config;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>
/// ⛔⛔ THE SILENCE WINDOW IS A JOINT CLAIM ACROSS TWO FILES, AND IT WAS FALSE.
///
/// <see cref="ChatClientFactory.EffectiveRetryBudgetSeconds"/> tells the member
/// watchdog how long one <c>GetResponseAsync</c> may stay silent:
/// (num_retries + 1) x request_timeout_seconds. Every profile's watchdog is
/// sized off it.
///
/// It was wrong until 2026-09-01, and wrong in the direction that reaps live
/// members. Not by arithmetic — <c>num_retries</c> never reached the wire.
/// System.ClientModel installs its own ClientRetryPolicy (3 retries = 4
/// attempts) by default, it sits beneath the PerCall policy vett adds, and it
/// swallowed the timeout first. MEASURED at a socket that accepts and never
/// answers (EpicForge's retry_budget_probe.js (not in this repo)), num_retries 0 / 2
/// / 5: FOUR attempts in all three arms, 5.0s apart, error at 20.0s. Three
/// settings, one behaviour.
///
/// So these tests guard the PAIRING, not the arithmetic. The arithmetic was
/// never in doubt; what failed is that one half of it was decorative.
/// </summary>
public class RetryBudgetTests
{
    // The probe above measures the real thing, over a real socket, and is the
    // evidence. It needs a listener and a spawned process, so it does not belong
    // in this suite. What belongs here is the invariant that makes the probe's
    // result GENERALISE: whatever number the budget formula reads, the SDK
    // policy is constructed from that same number.
    private static int MaxRetriesOf(PipelinePolicy policy)
    {
        // ClientRetryPolicy keeps its limit private. Reflect for it, and FAIL
        // LOUDLY if the shape moves — a probe that cannot find what it is
        // looking for must not report success. A silent skip here would restore
        // exactly the vacuous green this test exists to prevent.
        var t = policy.GetType();
        for (var cur = t; cur is not null; cur = cur.BaseType)
        {
            foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (f.FieldType != typeof(int)) continue;
                if (!f.Name.Contains("axRetries", StringComparison.Ordinal)) continue;
                return (int)(f.GetValue(policy) ?? -1);
            }
        }

        throw new InvalidOperationException(
            $"Could not read a max-retries field off {t.FullName}. The SDK's shape changed. " +
            "Do NOT relax this test — re-run EpicForge's retry_budget_probe.js (not in this repo), " +
            "which measures the attempt count at the socket and does not depend on this reflection.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(7)]
    public void NumRetriesReachesTheSdkRetryPolicy(int retries)
    {
        var opts = ChatClientFactory.BuildOptions("http://127.0.0.1:1/v1", null, 30, retries);

        Assert.NotNull(opts.RetryPolicy);
        Assert.Equal(retries, MaxRetriesOf(opts.RetryPolicy!));
    }

    [Fact]
    public void TheDefaultIsNotLeftToTheSdk()
    {
        // The regression in one line. Before the fix opts.RetryPolicy was null,
        // the SDK supplied its own 3-retry policy, and the profile's number was
        // ignored. Null here means the knob is decorative again.
        var opts = ChatClientFactory.BuildOptions("http://127.0.0.1:1/v1");
        Assert.NotNull(opts.RetryPolicy);
    }

    [Theory]
    [InlineData(0, 180, 180)]
    [InlineData(2, 180, 540)]
    [InlineData(2, 900, 2700)]
    [InlineData(5, 300, 1800)]
    public void TheBudgetIsTheAttemptCountTimesTheTimeout(int retries, int timeout, int expected)
    {
        var cfg = new LlmConfig { NumRetries = retries, RequestTimeoutSeconds = timeout };
        Assert.Equal(expected, ChatClientFactory.EffectiveRetryBudgetSeconds(cfg));
    }

    [Fact]
    public void TheBudgetAndThePolicyAgreeOnTheSAMEConfig()
    {
        // ⭐ THE ONE THAT MATTERS. Two numbers derived from one config must be
        // the same number. Asserting each against a literal, in separate tests,
        // is exactly how they drifted apart for vett's whole life: both halves
        // were individually "correct" and jointly false.
        foreach (var retries in new[] { 0, 1, 2, 3, 5 })
        {
            const int timeout = 240;
            var cfg = new LlmConfig { NumRetries = retries, RequestTimeoutSeconds = timeout };
            var budget = ChatClientFactory.EffectiveRetryBudgetSeconds(cfg);
            var attemptsAssumedByBudget = budget / timeout;

            var opts = ChatClientFactory.BuildOptions("http://127.0.0.1:1/v1", null, timeout, retries);
            var attemptsTheSdkWillMake = MaxRetriesOf(opts.RetryPolicy!) + 1;

            Assert.Equal(attemptsAssumedByBudget, attemptsTheSdkWillMake);
        }
    }

    [Fact]
    public void AnUnsetNumRetriesUsesTheSameFallbackOnBothSides()
    {
        // A profile that omits num_retries must not get one count from the
        // formula and another from the wire. `null` is the case that hid the
        // original defect on the profiles that never set the field.
        var cfg = new LlmConfig { RequestTimeoutSeconds = 240 };
        var attemptsAssumedByBudget = ChatClientFactory.EffectiveRetryBudgetSeconds(cfg) / 240;

        var opts = ChatClientFactory.BuildOptions(
            "http://127.0.0.1:1/v1", null, 240, ChatClientFactory.DefaultNumRetries);

        Assert.Equal(attemptsAssumedByBudget, MaxRetriesOf(opts.RetryPolicy!) + 1);
    }
}
