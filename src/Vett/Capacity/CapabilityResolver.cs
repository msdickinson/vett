namespace Vett.Capacity;

/// <summary>
/// What a seat is asking for: a capability, and how much context it needs.
///
/// The size is not decoration. Local flash is a finite pool of context
/// tokens handed out in chunks, so "a few large-context flash sessions" and
/// "many small ones" are the same resource spent two ways. A request that
/// names only a capability cannot be scheduled against that pool, and a
/// seat count cannot express the trade at all.
/// </summary>
/// <param name="Capability">Name from the catalogue: "flash", "pro".</param>
/// <param name="ContextTokens">Context this seat needs, in tokens.</param>
public sealed record CapabilityRequest(string Capability, int ContextTokens);

/// <summary>Per-run policy: what this run is ALLOWED to do, as opposed to what it needs.</summary>
/// <param name="AllowCloud">
/// Whether this run may spend money at all. False is the "local only, wait
/// if you must" stance.
/// </param>
/// <param name="PreferLocal">
/// Order of preference when both are free. Default true: local is already
/// bought and paid for, so taking a cloud seat while local sits idle is
/// money spent for nothing.
/// </param>
/// <param name="RequireMeasuredWindow">
/// When true, a provider whose served window has never been measured is not
/// eligible. Off by default — but the option has to exist, because
/// "COULD NOT MEASURE" is not "MEASURED", and some runs would rather stop
/// than bind against a guessed ceiling.
/// </param>
public sealed record ResolvePolicy(
    bool AllowCloud,
    bool PreferLocal = true,
    bool RequireMeasuredWindow = false);

/// <summary>
/// What the broker currently knows about one provider. A missing entry means
/// "nothing known against it" — fully free.
/// </summary>
/// <param name="FreeTokens">
/// Tokens still uncommitted in this provider's pool. Null means unbounded,
/// which is the honest description of a cloud endpoint: its limit is the
/// budget, not the cache.
/// </param>
/// <param name="BlockedReason">
/// Non-null when the provider is unusable outright regardless of size —
/// unreachable, or its spend budget is exhausted.
/// </param>
public sealed record ProviderAvailability(int? FreeTokens = null, string? BlockedReason = null);

/// <summary>
/// A snapshot of the world the resolver is scheduling against. Kept separate
/// from the resolver so the resolver stays pure: it decides what SHOULD
/// happen given a world state, it does not go and measure the world.
/// </summary>
public sealed record Availability(IReadOnlyDictionary<string, ProviderAvailability> Providers)
{
    public static readonly Availability AllFree = new(new Dictionary<string, ProviderAvailability>());

    public ProviderAvailability For(string providerName) =>
        Providers.TryGetValue(providerName, out var s) ? s : new ProviderAvailability();
}

/// <summary>Outcome of resolving one capability request.</summary>
public abstract record Resolution
{
    /// <summary>
    /// Go. Bind this provider, grant this much context, compact at this
    /// trigger. The grant is carried explicitly because the trigger is
    /// derived from it — the two must never be separated.
    /// </summary>
    public sealed record Bound(
        CapabilityProvider Provider,
        int GrantedTokens,
        int CompactionThreshold,
        string Rationale) : Resolution;

    /// <summary>
    /// Nothing is free right now, but something will be. The caller should
    /// queue and retry. <paramref name="WaitingOn"/> names the providers
    /// whose release would unblock this, so a wait is never mute.
    /// </summary>
    public sealed record Wait(
        string Capability,
        int ContextTokens,
        IReadOnlyList<string> WaitingOn,
        string Reason) : Resolution;

    /// <summary>
    /// Waiting cannot help: under this policy and catalogue the request can
    /// never be satisfied. Kept distinct from <see cref="Wait"/> because the
    /// correct caller behaviour is the opposite — fail fast rather than
    /// block forever.
    /// </summary>
    public sealed record Denied(string Capability, int ContextTokens, string Reason) : Resolution;
}

/// <summary>
/// Turns "I need 32k of flash" into "use this endpoint, with this grant and
/// this compaction trigger" — or into an explicit wait or refusal.
///
/// Deliberately pure: no clock, no network, no file system. Everything it
/// needs about the outside world arrives in <see cref="Availability"/>. That
/// is what makes the interesting cases — cloud budget spent, local pool
/// exhausted, pro unavailable — testable without standing any of it up.
///
/// The rule that must never bend: a request for capability X resolves to a
/// provider OF X, or it does not resolve. There is no path in here that
/// answers a pro request with a flash seat. Substitution happens BETWEEN
/// PROVIDERS OF THE SAME CAPABILITY (local flash for cloud flash) and
/// nowhere else.
/// </summary>
public static class CapabilityResolver
{
    public static Resolution Resolve(
        CapabilityCatalogue catalogue,
        CapabilityRequest request,
        ResolvePolicy policy,
        Availability availability)
    {
        var cap = catalogue.Find(request.Capability);
        if (cap is null)
        {
            return new Resolution.Denied(request.Capability, request.ContextTokens,
                $"no capability named '{request.Capability}' in the catalogue "
                + $"(known: {string.Join(", ", catalogue.Capabilities.Select(c => c.Name))})");
        }

        if (request.ContextTokens <= 0)
        {
            return new Resolution.Denied(cap.Name, request.ContextTokens,
                $"a request for {request.ContextTokens} tokens of context is not a request for a window");
        }

        if (cap.Providers.Count == 0)
        {
            return new Resolution.Denied(cap.Name, request.ContextTokens,
                $"capability '{cap.Name}' declares no providers, so nothing can serve it");
        }

        // Local first by default: it is already bought and paid for, and every
        // cloud seat taken while local sits idle is money spent for nothing.
        var ordered = policy.PreferLocal
            ? cap.Providers.OrderBy(p => p.Locality == Locality.Local ? 0 : 1).ToList()
            : cap.Providers.ToList();

        var waitingOn = new List<string>();
        var waitDetail = new List<string>();
        var permanentProblems = new List<string>();

        foreach (var p in ordered)
        {
            // --- Permanent exclusions: waiting will never change these. ---

            if (p.Locality == Locality.Cloud && !policy.AllowCloud)
            {
                permanentProblems.Add($"{p.Name}: cloud is not permitted by this run's policy");
                continue;
            }

            // Window, not pool: can this endpoint serve a SINGLE request this
            // large at all? This is where a big flash request loses its local
            // door while a small one keeps it.
            if (!p.CanServeWindow(request.ContextTokens))
            {
                permanentProblems.Add(
                    $"{p.Name}: serves at most {p.ServedWindowTokens} tokens per request, "
                    + $"below the {request.ContextTokens} asked for");
                continue;
            }

            if (policy.RequireMeasuredWindow && p.ServedWindowTokens is null)
            {
                permanentProblems.Add(
                    $"{p.Name}: served window has never been measured and this run requires a measured window");
                continue;
            }

            // A provider whose trigger cannot be resolved safely is not a
            // provider. Binding it would produce a run that dies on context
            // length rather than compacting, which is worse than waiting.
            var threshold = p.ResolveThreshold(request.ContextTokens, out var thresholdProblem);
            if (threshold is not int t)
            {
                permanentProblems.Add($"{p.Name}: {thresholdProblem}");
                continue;
            }

            // --- Transient exclusions: it exists and is allowed, just busy. ---

            var state = availability.For(p.Name);

            if (state.BlockedReason is string blocked)
            {
                waitingOn.Add(p.Name);
                waitDetail.Add($"{p.Name} ({blocked})");
                continue;
            }

            if (state.FreeTokens is int free && free < request.ContextTokens)
            {
                waitingOn.Add(p.Name);
                waitDetail.Add($"{p.Name} ({free} of {request.ContextTokens} tokens free)");
                continue;
            }

            var rationale = p.Locality == Locality.Local
                ? $"local provider '{p.Name}' has room for {request.ContextTokens} tokens; no spend required"
                : $"cloud provider '{p.Name}' selected (policy permits cloud"
                  + (cap.HasLocalAt(request.ContextTokens)
                        ? "; local was unavailable)"
                        : cap.HasLocal
                            ? $"; no local provider can serve a {request.ContextTokens}-token window)"
                            : "; this capability has no local provider)");

            return new Resolution.Bound(p, request.ContextTokens, t, rationale);
        }

        // Nothing bound. The distinction below is the whole point of tracking
        // substitutability per size: for a small flash request a shortage is a
        // QUEUE, because another door exists. For pro — or for flash at a size
        // only cloud can serve — a shortage with cloud denied is a REFUSAL,
        // because there is no second door and no amount of waiting opens the
        // first one.
        if (waitingOn.Count > 0)
        {
            var note = cap.IsSubstitutableAt(request.ContextTokens)
                ? $"'{cap.Name}' has {cap.Providers.Count(p => p.CanServeWindow(request.ContextTokens))} "
                  + $"providers able to serve {request.ContextTokens} tokens; "
                  + "the run waits for whichever frees first"
                : $"'{cap.Name}' has exactly one provider able to serve {request.ContextTokens} tokens, "
                  + "so it must wait for that one — it is NOT substituted with a different capability";

            return new Resolution.Wait(cap.Name, request.ContextTokens, waitingOn,
                $"all eligible providers are busy: {string.Join("; ", waitDetail)}. {note}");
        }

        return new Resolution.Denied(cap.Name, request.ContextTokens,
            $"no eligible provider for '{cap.Name}' at {request.ContextTokens} tokens, and waiting cannot help: "
            + string.Join("; ", permanentProblems));
    }
}
