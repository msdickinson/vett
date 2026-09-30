using YamlDotNet.Serialization;

namespace Vett.Capacity;

/// <summary>
/// Where a provider's hardware actually is.
///
/// This exists as its own field because the thing that LOOKS like it
/// answers the question — <c>llm.provider</c> in a profile — does not.
/// <c>provider: local</c> selects the OpenAI-compatible CLIENT CODE PATH;
/// it says nothing about whose machine serves the weights. Measured
/// 2026-08-28: 9 of the 18 shipped profiles declare <c>provider: local</c>
/// while pointing at <c>https://openrouter.ai/api/v1</c>. Any capacity or
/// billing decision keyed on that field is wrong in BOTH directions — it
/// charges paid cloud seats against the local KV budget (starving local
/// sessions that had room) and simultaneously fails to flag them as
/// billable.
/// </summary>
public enum Locality
{
    /// <summary>Served by our own hardware. Costs KV cache, costs no money.</summary>
    Local,

    /// <summary>Served by a paid third party. Costs money, costs no KV cache.</summary>
    Cloud,
}

/// <summary>
/// One concrete way to satisfy a capability: a real endpoint serving a real
/// model, with a hard ceiling on how much context it can give any single
/// caller.
/// </summary>
public sealed class CapabilityProvider
{
    /// <summary>Stable id used in policy, ledgers and refusal messages.</summary>
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";

    [YamlMember(Alias = "locality")] public string LocalityRaw { get; set; } = "";

    [YamlIgnore]
    public Locality Locality =>
        LocalityRaw.Equals("local", StringComparison.OrdinalIgnoreCase) ? Locality.Local : Locality.Cloud;

    [YamlMember(Alias = "endpoint")] public string Endpoint { get; set; } = "";
    [YamlMember(Alias = "model")] public string Model { get; set; } = "";

    /// <summary>Env var holding the key. The key itself never appears in YAML.</summary>
    [YamlMember(Alias = "api_key_env")] public string ApiKeyEnv { get; set; } = "";

    /// <summary>
    /// Name of the spend budget this provider draws on. Empty means "its own",
    /// so budgets are SEPARATE by default and sharing is an explicit act: two
    /// providers share a wallet exactly when they name the same budget.
    ///
    /// That default is the safe one. If two providers accidentally shared a
    /// budget, exhausting one would silently stop the other; if two
    /// accidentally split one, the worst case is that each is tracked on its
    /// own, which is what an operator who wrote nothing would expect.
    /// </summary>
    [YamlMember(Alias = "budget")] public string BudgetRaw { get; set; } = "";

    [YamlIgnore]
    public string BudgetName => string.IsNullOrWhiteSpace(BudgetRaw) ? Name : BudgetRaw;

    /// <summary>
    /// The server's hard per-request context ceiling, if it is KNOWN. This
    /// is the wall an individual request hits (vLLM's <c>max_model_len</c>),
    /// NOT the size of the pool — see <see cref="PoolTokens"/> for that.
    ///
    /// Null means nobody has measured it, which is not "zero" and not
    /// "unlimited". <c>capacity probe</c> fills it in from the endpoint's own
    /// /v1/models, and callers that cannot tolerate a guess can demand a
    /// measured value.
    ///
    /// Deliberately nullable: vett-chat previously rendered a context gauge
    /// against a 131072 denominator taken from a package.json default while
    /// the engine actually served 65536 — wrong by exactly 2x, so the gauge
    /// read half-full at the point vLLM starts returning HTTP 400. A guessed
    /// denominator must never be indistinguishable from a probed one.
    /// </summary>
    [YamlMember(Alias = "served_window_tokens")] public int? ServedWindowTokens { get; set; }

    /// <summary>
    /// Total context this provider may hand out across ALL concurrent
    /// callers — the thing being rationed.
    ///
    /// For a local vLLM engine this is the KV cache pool (measured 629,666
    /// tokens on gpu-1, 2026-08-28) and it is a genuinely finite resource:
    /// the sum of every live reservation has to fit inside it. That is why
    /// the allocation unit here is TOKENS and not seats — "a few large
    /// flash sessions" and "many small flash sessions" draw on the very
    /// same pool, and a seat count cannot express the trade.
    ///
    /// Null for cloud providers, where the constraint is money rather than
    /// cache and the pool is effectively unbounded.
    /// </summary>
    [YamlMember(Alias = "pool_tokens")] public int? PoolTokens { get; set; }

    /// <summary>
    /// Fraction of a GRANTED window at which compaction should trigger.
    /// Applied to what the caller was actually granted, never to the
    /// server's ceiling — a seat granted 32k must compact inside 32k even
    /// though the endpoint could serve 65536.
    /// </summary>
    [YamlMember(Alias = "compaction_ratio")] public double? CompactionRatio { get; set; }

    /// <summary>Optional absolute ceiling on the trigger, whatever the ratio says.</summary>
    [YamlMember(Alias = "max_compaction_threshold")] public int? MaxCompactionThreshold { get; set; }

    /// <summary>Ratio used when the provider does not state one.</summary>
    public const double DefaultCompactionRatio = 0.75;

    /// <summary>
    /// Can this provider serve a single request of <paramref name="contextTokens"/>?
    ///
    /// This is the check that makes substitutability contextual rather than
    /// fixed. Local flash serves 65536, cloud flash serves far more; a 32k
    /// request can go either way, while a 100k request of the SAME
    /// capability can only go to cloud. Asking "is flash substitutable?"
    /// without naming a size has no answer.
    ///
    /// An unmeasured window cannot refuse anything — absence of a
    /// measurement is not evidence of a small ceiling — so it passes here
    /// and is reported separately.
    /// </summary>
    public bool CanServeWindow(int contextTokens) =>
        ServedWindowTokens is not int w || contextTokens <= w;

    /// <summary>
    /// The compaction trigger for a seat granted <paramref name="grantedTokens"/>
    /// of context, or null with a reason when no safe trigger exists.
    ///
    /// The trigger is a function of the GRANT, which is the whole reason
    /// dynamic binding is survivable. A static per-profile threshold that
    /// travels onto a smaller endpoint does not degrade — it DIES. From
    /// ds-solo-flash.yaml:151, describing exactly that bug: "Against a 64k
    /// server the old 100000 trigger could NEVER FIRE: vLLM rejects the
    /// request on context length first, so the run dies instead of
    /// compacting."
    /// </summary>
    public int? ResolveThreshold(int grantedTokens, out string? problem)
    {
        problem = null;

        if (grantedTokens <= 0)
        {
            problem = $"provider '{Name}': a grant of {grantedTokens} tokens is not a window";
            return null;
        }

        var ratio = CompactionRatio ?? DefaultCompactionRatio;
        if (ratio <= 0 || ratio >= 1)
        {
            problem = $"provider '{Name}': compaction_ratio {ratio} is not strictly between 0 and 1, "
                    + "so the trigger would sit outside the window it is meant to protect";
            return null;
        }

        var threshold = (int)Math.Round(grantedTokens * ratio);
        if (MaxCompactionThreshold is int cap && cap < threshold)
        {
            threshold = cap;
        }

        // Same invariant EndpointProbe already enforces statically at
        // validate time (EndpointProbe.cs:352 — "this trigger can NEVER
        // fire; compaction is off for this seat"). A warning is the right
        // severity when a human is reading validate output; it is the wrong
        // severity once the endpoint is chosen at RUNTIME, where nobody
        // reads it and the run simply dies mid-flight.
        if (threshold <= 0 || threshold >= grantedTokens)
        {
            problem = $"provider '{Name}': trigger {threshold} does not sit strictly inside a "
                    + $"{grantedTokens}-token grant — it could never fire, and the run would die "
                    + "on context length instead of compacting";
            return null;
        }

        return threshold;
    }
}

/// <summary>
/// A named requirement a profile can state — "I need flash", "I need pro" —
/// together with every concrete provider that can satisfy it.
///
/// The point of the indirection: a profile that hard-codes an endpoint has
/// already made a locality decision at authoring time, months before anyone
/// knows what is free. A profile that states a capability leaves that
/// decision to the moment of the run, where the answer is knowable.
/// </summary>
public sealed class Capability
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";
    [YamlMember(Alias = "description")] public string Description { get; set; } = "";
    [YamlMember(Alias = "providers")] public List<CapabilityProvider> Providers { get; set; } = new();

    /// <summary>
    /// Whether a request of this SIZE has somewhere else to go — the
    /// property that decides what a shortage means.
    ///
    /// Substitutability is a property of (capability, requested context),
    /// never of the capability alone. Measured across the 18 shipped
    /// profiles on 2026-08-28: `deepseek-v4-flash` (local, 65536 window) and
    /// `deepseek/deepseek-v4-flash` (cloud) both exist, so a 32k flash
    /// request has two doors and a shortage is merely a QUEUE. Push the same
    /// capability to 100k and local drops out on window alone, leaving one
    /// door. `deepseek/deepseek-v4-pro` has no local counterpart at any
    /// size, so a pro request that cannot reach cloud has exactly one
    /// correct behaviour: WAIT. Quietly handing it flash would answer a
    /// different question than the one asked, and would do so invisibly.
    /// </summary>
    public bool IsSubstitutableAt(int contextTokens) =>
        Providers.Count(p => p.CanServeWindow(contextTokens)) > 1;

    public bool HasLocalAt(int contextTokens) =>
        Providers.Any(p => p.Locality == Locality.Local && p.CanServeWindow(contextTokens));

    public bool HasLocal => Providers.Any(p => p.Locality == Locality.Local);
    public bool HasCloud => Providers.Any(p => p.Locality == Locality.Cloud);
}

/// <summary>
/// A named spend limit, in dollars, that one or more cloud providers draw on.
///
/// ⛔ THE LIMIT IS ENFORCED AGAINST A RECORDED BILLING READ, NEVER AGAINST AN
/// ESTIMATE. Tokens counted locally multiplied by a list price is not billed
/// cost — cache discounts alone break the correspondence, and they break it
/// unevenly, so the error does not even cancel when comparing two runs. This
/// type therefore holds only the LIMIT; the amount spent comes from
/// <see cref="SpendRecord"/>, which carries the source it was read from.
///
/// Budgets are OPT-IN. A provider whose budget declares no limit is unbounded,
/// which is the behaviour of a catalogue that mentions money nowhere. Declaring
/// a limit is what asks for enforcement — and enforcement then FAILS CLOSED,
/// because a spend gate that passes when it cannot see the balance is not a
/// spend gate.
/// </summary>
public sealed class Budget
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";

    /// <summary>Dollar ceiling. Null means "tracked but not capped".</summary>
    [YamlMember(Alias = "limit_usd")] public decimal? LimitUsd { get; set; }

    /// <summary>
    /// Free-text label for what the limit covers ("month", "campaign"). Not
    /// parsed: this class does not roll a budget over on its own, because a
    /// reset it invented would hand out money nobody confirmed was there.
    /// </summary>
    [YamlMember(Alias = "period")] public string Period { get; set; } = "";

    /// <summary>
    /// How old a spend reading may be before this budget stops admitting work.
    /// A stale reading errs in the PERMISSIVE direction — it says $10 while the
    /// real balance is $60 — so it is treated as no reading at all.
    /// </summary>
    [YamlMember(Alias = "stale_after_hours")] public double StaleAfterHours { get; set; } = 24;

    [YamlMember(Alias = "note")] public string Note { get; set; } = "";
}

/// <summary>Top-level document of capabilities.yaml.</summary>
public sealed class CapabilityCatalogue
{
    [YamlMember(Alias = "capabilities")] public List<Capability> Capabilities { get; set; } = new();

    /// <summary>Spend limits. Absent entirely means no budget is enforced.</summary>
    [YamlMember(Alias = "budgets")] public List<Budget> Budgets { get; set; } = new();

    public Capability? Find(string name) =>
        Capabilities.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Budget? FindBudget(string name) =>
        Budgets.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One reading of what a budget has actually spent, and where that number came
/// from. The source is not decoration: it is the difference between a figure
/// read from a billing endpoint and one somebody guessed, and only the former
/// may gate a run.
/// </summary>
public sealed record SpendRecord(string Budget, decimal Usd, DateTimeOffset AsOf, string Source);

/// <summary>
/// Advisory record of who holds the ledger lock. Advisory because the OS
/// handle is the real lock: this file can outlive it when a process dies, and
/// can be absent when something takes the lock without stamping.
/// </summary>
public sealed record LockHolder
{
    [System.Text.Json.Serialization.JsonPropertyName("pid")] public int Pid { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("owner")] public string Owner { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("machine")] public string Machine { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("acquired_at")] public DateTimeOffset AcquiredAt { get; init; }
}

/// <summary>
/// A snapshot of ledger health.
/// </summary>
/// <param name="LockHeld">Held at the instant of the probe. Not a guarantee about any other instant.</param>
/// <param name="Holder">The stamp, if any. May be stale; may be absent while the lock IS held.</param>
/// <param name="HolderAlive">Null when there is no stamp to check — which is NOT the same as "dead".</param>
/// <param name="HeldFor">Age of the stamp, not of the handle.</param>
/// <param name="Leases">Live leases.</param>
/// <param name="ReclaimableLeases">
/// Leases already being ignored when free capacity is computed, because their
/// owner is gone or they stopped heartbeating. Surfaced because their tokens
/// are being handed out again, which is correct but should not be silent.
/// </param>
/// <param name="Notes">Human-readable findings, worst first.</param>
public sealed record LedgerStatus(
    bool LockHeld,
    LockHolder? Holder,
    bool? HolderAlive,
    TimeSpan? HeldFor,
    IReadOnlyList<Lease> Leases,
    IReadOnlyList<Lease> ReclaimableLeases,
    IReadOnlyList<string> Notes);

/// <summary>Outcome of asking to break a stuck ledger lock.</summary>
public abstract record BreakResult
{
    /// <summary>The lock was free. Nothing was wrong.</summary>
    public sealed record NothingStuck(string Detail) : BreakResult;

    /// <summary>A stamp left behind by a dead process was removed. The lock itself was already free.</summary>
    public sealed record ClearedStaleStamp(LockHolder Holder, string Detail) : BreakResult;

    /// <summary>
    /// A live process holds it, and no other process can take that away. The
    /// only honest remedy is stopping the named pid, which is a human decision.
    /// </summary>
    public sealed record CannotBreak(LockHolder? Holder, string Detail) : BreakResult;
}
