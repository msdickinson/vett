using Microsoft.Extensions.AI;
using Vett.Capacity;
using Vett.Config;

namespace Vett.Llm;

/// <summary>
/// The seam between a profile's <c>llm:</c> block and the shared capacity
/// ledger.
///
/// WHY THIS FILE EXISTS. Before it, every constraint vett had was
/// in-process: a SemaphoreSlim in Coordinator, another in Runner, another in
/// BenchCommand. Two vett processes could not see each other at all, so a
/// `vett chat` session and a `vett run` sweep each believed they had the
/// whole DGX. The broker fixed that on disk; this file is what actually
/// routes traffic through it. Both entry points build their clients through
/// <see cref="ChatClientFactory.Create"/>, so binding here — and only here —
/// is what makes the pool shared rather than per-process.
///
/// WHAT IT DELIBERATELY DOES NOT DO. No network. Claiming a lease must not
/// depend on a reachable endpoint, or a busy DGX would turn every claim into
/// a timeout. Live measurement of served windows belongs to
/// `vett capacity probe`, which writes what it learns into the catalogue;
/// this path reads what is already known.
/// </summary>
public static class CapacityBinding
{
    /// <summary>
    /// Overridable so tests can point at a scratch ledger instead of the
    /// real <c>~/.vett/capacity</c>. Null = use the ambient one.
    /// </summary>
    public static CapacityBroker? BrokerOverride { get; set; }

    /// <summary>Overridable so tests can supply a catalogue inline.</summary>
    public static CapabilityCatalogue? CatalogueOverride { get; set; }

    /// <summary>
    /// Whether cloud providers may be bound. Mirrors
    /// <see cref="ResolvePolicy.AllowCloud"/>; defaults to true because the
    /// catalogue's own budgets are the thing meant to stop cloud spend, not
    /// a blanket switch. A run that wants "local or nothing" sets this false.
    /// </summary>
    public static bool AllowCloud { get; set; } = true;

    /// <summary>
    /// Priority claimed when a config names a capability but no priority.
    /// `vett chat` raises this to Interactive at startup: a human waiting at
    /// a prompt outranks a queued batch run.
    /// </summary>
    public static int DefaultPriority { get; set; } = Priority.Batch;

    private static CapacityBroker Broker() => BrokerOverride ?? new CapacityBroker();

    /// <summary>Map the YAML word onto the broker's numeric priority.</summary>
    public static int PriorityOf(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "" => DefaultPriority,
        "interactive" => Priority.Interactive,
        "batch" => Priority.Batch,
        "background" => Priority.Background,
        var other => throw new VettException(
            $"unknown llm priority \"{other}\". Use interactive, batch, or background."),
    };

    /// <summary>
    /// A granted claim: the lease that reserves it, plus the provider that
    /// won. Carried together on purpose — a provider without its lease is a
    /// reservation nobody is holding, and a lease without its provider names
    /// no endpoint.
    /// </summary>
    public sealed record Binding(Lease Lease, CapabilityProvider Provider, Resolution.Bound Resolved);

    /// <summary>
    /// Claim capacity for a config that names a capability.
    ///
    /// FAILS CLOSED, three ways, each with a different correct response:
    /// an unknown capability or an impossible request is a
    /// <see cref="VettException"/> (fix the config); a Refused claim is a
    /// <see cref="VettException"/> (the budget said no — retrying is futile);
    /// a Queued claim is also a <see cref="VettException"/>, but one that
    /// names who to wait on, because this synchronous factory has nowhere to
    /// park. Blocking inside client construction would deadlock a leader
    /// holding a lease while it waits for one.
    /// </summary>
    public static Binding Claim(LlmConfig config, string owner)
    {
        if (string.IsNullOrWhiteSpace(config.Capability))
            throw new VettException("CapacityBinding.Claim called for a config with no capability");

        if (config.ContextTokens <= 0)
        {
            throw new VettException(
                $"llm.capability is \"{config.Capability}\" but llm.context_tokens is "
                + $"{config.ContextTokens}. A capability request has to say how large a window it "
                + "needs — set context_tokens (e.g. 60000) alongside capability.");
        }

        var catalogue = CatalogueOverride;
        string? source = null;
        if (catalogue is null)
        {
            var (loaded, path) = CatalogueLoader.Resolve();
            if (loaded is null)
            {
                throw new VettException(
                    $"llm.capability is \"{config.Capability}\" but no capability catalogue was found. "
                    + $"Looked in: {string.Join(", ", CatalogueLoader.SearchDirs())}. "
                    + "Run from a directory with capabilities/default.yaml, or clear llm.capability "
                    + "and set endpoint/model directly.");
            }
            catalogue = loaded;
            source = path;
        }

        var broker = Broker();
        var request = new CapabilityRequest(config.Capability, config.ContextTokens);
        var policy = new ResolvePolicy(AllowCloud: AllowCloud);
        var priority = PriorityOf(config.Priority);

        var result = broker.Claim(catalogue, request, policy, priority, owner);

        switch (result)
        {
            case ClaimResult.Granted g:
                return new Binding(g.Lease, g.Binding.Provider, g.Binding);

            case ClaimResult.Queued q:
                throw new VettException(
                    $"capacity for \"{config.Capability}\" ({config.ContextTokens} tokens) is not free: "
                    + $"{q.Reason}. Waiting on: {string.Join(", ", q.WaitingOn)}. "
                    + "`vett capacity ls` shows who holds it; `vett capacity release --stale` "
                    + "clears leases whose holder is gone."
                    + (source is null ? "" : $" (catalogue: {source})"));

            case ClaimResult.Refused r:
                throw new VettException(
                    $"capacity for \"{config.Capability}\" ({config.ContextTokens} tokens) refused: "
                    + r.Reason
                    + (source is null ? "" : $" (catalogue: {source})"));

            default:
                throw new VettException($"unhandled claim result {result.GetType().Name}");
        }
    }

    /// <summary>
    /// Project a bound provider back onto an LlmConfig, so the rest of the
    /// factory can build a client from it without knowing capacity exists.
    ///
    /// The provider WINS over the block's own endpoint/model/api_key_env.
    /// That is the point: once a config says `capability: flash`, the
    /// catalogue owns where flash lives, and leaving a stale endpoint in the
    /// profile must not quietly re-route the traffic.
    /// </summary>
    public static LlmConfig Project(LlmConfig config, CapabilityProvider provider)
    {
        var projected = Clone(config);
        projected.Endpoint = provider.Endpoint;
        projected.Model = provider.Model;
        projected.ApiKeyEnv = provider.ApiKeyEnv;
        // A capability's providers are all OpenAI-compatible surfaces — that
        // is what the catalogue records (endpoint + model + key env). Keeping
        // the block's own provider word would let `provider: openai` drag in
        // the SDK's base URL and discard the endpoint the resolver just chose.
        projected.Provider = "local";
        // Fallbacks are the profile's own failover list, aimed at the
        // profile's own endpoints. The resolver has already chosen among the
        // catalogue's providers for this capability, and a second, unmanaged
        // failover list would spend capacity nobody leased.
        projected.Fallbacks = null;
        return projected;
    }

    private static LlmConfig Clone(LlmConfig c) => new()
    {
        Provider = c.HasExplicitProvider ? c.Provider : "",
        Endpoint = c.Endpoint,
        Model = c.Model,
        ApiKeyEnv = c.ApiKeyEnv,
        Temperature = c.Temperature,
        TopP = c.TopP,
        RequestTimeoutSeconds = c.RequestTimeoutSeconds,
        NumRetries = c.NumRetries,
        // Both read by CreateSingle / the per-request bound; a projection that
        // dropped them would silently turn `stream: false` back on (law 135)
        // and hand a leased seat an unbounded generation.
        MaxOutputTokens = c.MaxOutputTokens,
        Stream = c.Stream,
        EnableThinking = c.EnableThinking,
        Fallbacks = c.Fallbacks,
        Capability = c.Capability,
        Priority = c.Priority,
        ContextTokens = c.ContextTokens,
    };
}

/// <summary>
/// An <see cref="IChatClient"/> that holds a capacity lease for as long as it
/// is alive, and gives it back when disposed.
///
/// WHY A HEARTBEAT AND A TTL, NOT JUST A RELEASE. Mark's actual question:
/// "if a service never releases a lock you may need a way to do that". A
/// process that is killed, panics, or is closed by the OS never runs
/// Dispose, so release-on-dispose alone leaks the reservation forever and
/// the pool shrinks with every crash. So the lease carries a short TTL and
/// this client renews it while it is in use. A holder that stops running
/// stops renewing, its lease expires, and
/// <see cref="CapacityBroker.ReclaimStaleLeases"/> collects it. The manual
/// escape hatch (`vett capacity release`) exists for the case where even
/// that is not enough.
/// </summary>
public sealed class LeasedChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly CapacityBroker _broker;
    private readonly string _leaseId;
    private readonly Timer _heartbeat;
    private int _disposed;

    public LeasedChatClient(IChatClient inner, CapacityBroker broker, string leaseId, TimeSpan ttl, string model, string endpoint)
    {
        _inner = inner;
        _broker = broker;
        _leaseId = leaseId;
        Model = model;
        Endpoint = endpoint;

        // Renew at a third of the TTL, so one missed tick (a long GC pause, a
        // saturated thread pool) does not expire a live lease. Two misses in a
        // row and it expires — which is the intended behaviour for a process
        // that has genuinely stopped making progress.
        var period = TimeSpan.FromMilliseconds(Math.Max(1000, ttl.TotalMilliseconds / 3));
        // ⛔ THE RETURN VALUE IS DISCARDED, AND THAT IS A KNOWN GAP.
        // Heartbeat hands back the live lease, which carries
        // PreemptRequested (set when a higher-priority claim asked this
        // holder to stand down) and returns NULL when the lease is gone --
        // whose own doc says a caller "must treat as 'you no longer hold
        // capacity', not as 'fine'". This caller treats both as fine.
        //
        // Consequences, both real: a preemption request is never observed by
        // the run it targets, so priority does not actually yield a seat; and
        // a lease reclaimed as stale while this process is alive leaves the
        // client happily issuing requests against capacity it no longer holds.
        //
        // Not fixed here because acting on it is a POLICY choice (finish the
        // turn and release? abandon? finish the run?) rather than a wiring
        // detail. Recorded in
        // internal note CAPACITY-WIRING-RESULTS-2026-08-28.md.
        _heartbeat = new Timer(_ =>
        {
            try { _broker.Heartbeat(_leaseId, ttl); }
            catch { /* a failed renewal must not take down the run; the TTL handles it */ }
        }, null, period, period);
    }

    /// <summary>The lease this client is holding. Exposed for assertions.</summary>
    public string LeaseId => _leaseId;

    /// <summary>
    /// The model the capability resolver chose, and the endpoint it lives on.
    ///
    /// THE MODEL NAME TRAVELS ON A SECOND PATH, AND IT HAD TO BE JOINED BACK UP.
    /// Building the client is only half of binding a model: every command also
    /// carries the model as a bare string into <c>LlmSettings</c>, which
    /// AgentLoop puts on <c>ChatOptions.ModelId</c> for each request. For a
    /// capability profile that string is empty by design -- the profile names
    /// no model -- so the request went out with <c>ModelId = ""</c> and the
    /// OpenAI client rejected it with "Empty encoded value". The client knew
    /// the right answer the whole time; nothing asked it. Measured, not
    /// reasoned: this is what C2 of PREREG-2026-08-28 hit on its second run,
    /// after the endpoint half of the same defect was fixed.
    /// </summary>
    public string Model { get; }

    /// <inheritdoc cref="Model"/>
    public string Endpoint { get; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(LeasedChatClient) && serviceKey is null
            ? this
            : _inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // Release exactly once. Double-dispose is legal on IDisposable, and a
        // second Release would return tokens that a LATER claim may already
        // have taken — handing the same capacity out twice.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _heartbeat.Dispose();
        try { _broker.Release(_leaseId); }
        catch { /* the TTL is the backstop; never throw out of Dispose */ }
        _inner.Dispose();
    }
}
