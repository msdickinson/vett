using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;
using Vett.Config;

namespace Vett.Llm;

/// <summary>
/// Creates an IChatClient from profile LLM config. Supports local (OpenAI-compatible),
/// OpenAI, Anthropic, Azure, Google — each resolved from the provider field.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>
    /// Create a chat client from LLM config. Falls back to CLI flags/env vars
    /// for endpoint, model, and api key if not set in config.
    /// </summary>
    public static IChatClient Create(LlmConfig config, string? endpointOverride = null, string? modelOverride = null, string? apiKeyOverride = null)
    {
        // ---- The shared-constraint path. -------------------------------
        // A config that names a capability does not choose its own endpoint;
        // the capability catalogue does, through a lease taken against the
        // on-disk ledger every vett process contends on. Everything below
        // this block is the pre-capacity path, entered whenever `capability:`
        // is empty — which is every profile shipped today, so this changes no
        // existing binding.
        if (!string.IsNullOrWhiteSpace(config.Capability))
            return CreateLeased(config, endpointOverride, modelOverride, apiKeyOverride);

        // Primary endpoint (CLI overrides apply only to the primary).
        var primary = CreateSingle(config, endpointOverride, modelOverride, apiKeyOverride);

        // No fallbacks configured → bare client, byte-for-byte the pre-V1 path.
        if (config.Fallbacks is not { Count: > 0 } fallbacks)
            return primary;

        // Build the ordered failover list. Each fallback is a standalone
        // config (own endpoint/model/auth); nested fallbacks are ignored.
        var endpoints = new List<(IChatClient, string)>
        {
            (primary, LabelOf(config, endpointOverride, modelOverride)),
        };
        foreach (var fb in fallbacks)
            endpoints.Add((CreateSingle(fb), LabelOf(fb, null, null)));

        return new FailoverChatClient(endpoints);
    }

    /// <summary>
    /// Build a client for a config that names a capability: claim a lease,
    /// bind the provider the resolver chose, and hand back a client that
    /// holds that lease until it is disposed.
    ///
    /// AN EXPLICIT OVERRIDE BYPASSES CAPACITY, LOUDLY. If a human passed
    /// --endpoint or --model, honouring the capability would bill a lease
    /// against a provider the traffic is not going to. Rather than mis-attribute
    /// spend, the override wins and the capability is skipped — but it says
    /// so on stderr, because silently un-governing a budget is exactly the
    /// failure this system exists to prevent.
    /// </summary>
    private static IChatClient CreateLeased(
        LlmConfig config, string? endpointOverride, string? modelOverride, string? apiKeyOverride)
    {
        if (!string.IsNullOrEmpty(endpointOverride) || !string.IsNullOrEmpty(modelOverride))
        {
            Console.Error.WriteLine(
                $"warning: llm.capability \"{config.Capability}\" ignored — an explicit "
                + "--endpoint/--model override wins, and this request is NOT counted against "
                + "any capacity pool or budget.");
            return CreateSingle(config, endpointOverride, modelOverride, apiKeyOverride);
        }

        // ONE LEASE PER SEAT CLASS PER PROCESS, and this memo is what enforces
        // it. Nothing in vett disposes an IChatClient (checked 2026-08-28:
        // there is no `client.Dispose()` on any agent path, and Coordinator
        // says so in a comment at line ~2150), while ResolveClient is called
        // PER DISPATCH at three sites. A claim on every call would therefore
        // reserve tokens that are never given back, drain the pool with
        // phantom holders, and start Refusing real work -- a gate failing
        // CLOSED on a lie, which is worse than not gating at all.
        //
        // STATE PLAINLY WHAT THE POOL NUMBER THEN MEANS: a held reservation is
        // "this process has a live seat of this shape", NOT "this many
        // requests are in flight". Two concurrent dispatches of the SAME seat
        // class share one reservation. Per-request accounting needs disposal
        // plumbed through the agent loop; until it is, claiming per call would
        // make the ledger read confidently and wrongly.
        var key = CapacityKey(config, apiKeyOverride);
        return LeasedClients.GetOrAdd(key, _ =>
        {
            var owner = $"{Environment.ProcessId}:{AppDomain.CurrentDomain.FriendlyName}";
            var binding = CapacityBinding.Claim(config, owner);

            // Build against the PROVIDER, not the block. If anything below
            // throws, give the lease straight back: a reservation held by a
            // client that was never constructed is a pure leak, and the TTL
            // would keep it for minutes.
            var broker = CapacityBinding.BrokerOverride ?? new Vett.Capacity.CapacityBroker();
            try
            {
                var inner = CreateSingle(CapacityBinding.Project(config, binding.Provider), null, null, apiKeyOverride);
                return new LeasedChatClient(
                    inner, broker, binding.Lease.Id, Vett.Capacity.CapacityBroker.DefaultLeaseTtl,
                    binding.Provider.Model, binding.Provider.Endpoint);
            }
            catch
            {
                try { broker.Release(binding.Lease.Id); } catch { /* TTL is the backstop */ }
                throw;
            }
        });
    }

    /// <summary>
    /// True when this config gets its endpoint/model from the capability
    /// catalogue instead of from its own fields.
    ///
    /// EVERY endpoint/model guard in the CLI has to consult this. Those guards
    /// run BEFORE the factory, so one that does not know about capacity
    /// rejects a perfectly valid capability profile with "endpoint and model
    /// are required" and the user never reaches the layer that would have
    /// served them -- the same defect DefaultEndpointFor was added to fix, in
    /// the same four commands. It is a method here, not a condition repeated
    /// four times, because a rule written four times drifts.
    /// </summary>
    public static bool BindsThroughCapacity(LlmConfig? c)
        => !string.IsNullOrWhiteSpace(c?.Capability);

    /// <summary>
    /// Fold a profile's own <c>llm.endpoint</c> / <c>llm.model</c> in UNDER a
    /// flag/env override -- except on a capability profile, where the
    /// catalogue owns both and the profile's fields must not be consulted.
    ///
    /// WHY THIS EXISTS, AND WHAT IT COST TO FIND. Every command resolves
    /// endpoint as flag -> env -> profile and hands the FOLDED string to
    /// Create() as `endpointOverride`. The factory's bypass rule ("an explicit
    /// --endpoint wins over a capability, loudly") is written for a HUMAN's
    /// flag -- but by the time it runs, a profile's own stale endpoint is
    /// indistinguishable from one. So a capability profile that also carried
    /// an endpoint bypassed capacity on EVERY run, warned about an override
    /// nobody passed, and routed traffic to the very field
    /// CapacityBinding.Project exists to overrule. Measured, not reasoned:
    /// check C2 of PREREG-2026-08-28 failed exactly this way -- the request
    /// went to the deliberately-unroutable endpoint in the profile.
    ///
    /// The fix has to live where the folding happens, because that is where
    /// the provenance still exists. One method rather than a condition
    /// repeated at six call sites, for the same reason as
    /// <see cref="BindsThroughCapacity"/>: a rule written six times drifts.
    /// </summary>
    public static string FoldProfileValue(LlmConfig? config, string? fromFlagOrEnv, string? profileValue)
        => !string.IsNullOrEmpty(fromFlagOrEnv) ? fromFlagOrEnv!
            : BindsThroughCapacity(config) ? ""
            : profileValue ?? "";

    /// <summary>
    /// Ask a freshly-built client which model it actually bound, when the
    /// caller does not know. See <see cref="LeasedChatClient.Model"/> for why
    /// this join is necessary rather than cosmetic: the empty string a
    /// capability profile legitimately produces becomes
    /// <c>ChatOptions.ModelId = ""</c> two layers down, and the request fails.
    ///
    /// Returns the caller's value untouched whenever it has one, so this is a
    /// no-op on every non-capability path.
    /// </summary>
    public static string EffectiveModel(IChatClient client, string? model)
        => !string.IsNullOrEmpty(model) ? model!
            : client.GetService(typeof(LeasedChatClient)) is LeasedChatClient leased ? leased.Model
            : model ?? "";

    /// <summary>
    /// Leased clients, one per seat class, for the life of the process. See
    /// CreateLeased for why this memo is load-bearing rather than an
    /// optimisation.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IChatClient> LeasedClients = new();

    /// <summary>
    /// Identity of a seat class: everything that would make two leases
    /// legitimately different. Capability and context_tokens because they ARE
    /// the request; priority because it changes who gets preempted; the
    /// client-baked fields because a different value needs a different client
    /// regardless of capacity.
    /// </summary>
    private static string CapacityKey(LlmConfig c, string? apiKeyOverride)
        => string.Join("",
            c.Capability.Trim().ToLowerInvariant(),
            c.ContextTokens.ToString(),
            c.Priority.Trim().ToLowerInvariant(),
            c.EnableThinking?.ToString() ?? "-",
            (c.RequestTimeoutSeconds ?? DefaultRequestTimeoutSeconds).ToString(),
            (c.NumRetries ?? DefaultNumRetries).ToString(),
            (c.Stream ?? true) ? "stream" : "buffered",
            string.IsNullOrEmpty(apiKeyOverride) ? "-" : "override");

    static ChatClientFactory()
    {
        // Give leases back at exit instead of leaving them to time out. The
        // TTL is the backstop for a process that is KILLED; an orderly exit
        // that still parks a reservation for five minutes makes back-to-back
        // runs contend with their own ghost.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseAllLeases();
    }

    /// <summary>
    /// Dispose every leased client, releasing its lease. Idempotent, and safe
    /// to call from tests to return the ledger to a known state.
    /// </summary>
    public static void ReleaseAllLeases()
    {
        foreach (var key in LeasedClients.Keys)
        {
            if (LeasedClients.TryRemove(key, out var c))
            {
                try { c.Dispose(); } catch { /* never throw out of teardown */ }
            }
        }
    }

    private static string LabelOf(LlmConfig c, string? endpointOverride, string? modelOverride)
    {
        var ep = !string.IsNullOrEmpty(endpointOverride) ? endpointOverride : c.Endpoint;
        var m = !string.IsNullOrEmpty(modelOverride) ? modelOverride : c.Model;
        return string.IsNullOrEmpty(ep) ? m : $"{ep} ({m})";
    }

    /// <summary>
    /// The base URL a provider falls back to when no rung (CLI / env / profile)
    /// supplies one. Null means the provider has NO built-in default and an
    /// explicit endpoint is genuinely required.
    ///
    /// ⛔ THIS EXISTS SO THE CLI GUARDS AND THIS FACTORY CANNOT DRIFT APART.
    /// Until 2026-08-26 every command guard demanded a non-empty endpoint
    /// regardless of provider, which made the defaults below UNREACHABLE — the
    /// same dead-fallback shape as BenchCommand's old `clients.Count == 0`
    /// branch. Measured consequence: `openai-example` and `anthropic-example`
    /// — both SHIPPED profiles — exited 2 with "endpoint and model are
    /// required", as did every cloud profile written by the vett-chat
    /// onboarding flow (`profileWriter.renderCloudProfileYaml` emits
    /// `api_key_env` but no endpoint, by design, because the provider knows
    /// its own URL). A guard must not reject input the layer beneath it can
    /// serve.
    /// </summary>
    public static string? DefaultEndpointFor(string? provider) => (provider ?? "").ToLowerInvariant() switch
    {
        "openai" => "https://api.openai.com/v1",
        "anthropic" => "https://api.anthropic.com/v1",
        // ⚠ /v1beta/openai, NOT /v1beta. Every one of these is consumed by
        // CreateOpenAICompatible, which appends /chat/completions, and Google's
        // OpenAI-compatible surface is documented as living under
        // /v1beta/openai/ — so the old /v1beta default resolved to
        // /v1beta/chat/completions. Two sources in this repo disagreed: this
        // line said /v1beta, while vett-chat's cloudOnboarding.ts posts its
        // live key test to /v1beta/openai/chat/completions. The extension's URL
        // is the one that has actually been exercised against Google, so it
        // wins.
        //
        // ⛔ THIS CHOICE IS REASONED, NOT MEASURED — and the obvious probe is
        // SATURATED. Measured 2026-08-26 with a syntactically-valid but fake
        // key, twice each:
        //     POST /v1beta/openai/chat/completions -> 400 "Please pass a valid API key"
        //     POST /v1beta/chat/completions        -> 400 "Please pass a valid API key"
        // Byte-identical. Google checks the key at the edge BEFORE it routes the
        // path, so a bad-key probe has ZERO POWER to tell a real route from a
        // nonexistent one — its null looks confident and means nothing. An
        // earlier draft of this comment asserted /v1beta "is not a Google route
        // at all"; that is documentation, not something this repo measured, and
        // it is retracted as a measured claim. Settling it needs a VALID Google
        // key, which nothing here has. If you get one, re-run both routes and
        // replace this block with the result.
        "google" => "https://generativelanguage.googleapis.com/v1beta/openai",
        // local / azure / "" own no default: pointing them somewhere is the
        // whole point, so a missing endpoint stays a hard config error.
        _ => null,
    };

    // The two fallbacks used when a profile leaves the fields unset. Named
    // constants rather than inline literals because the stall watchdog has to
    // reason about the SAME numbers, and a duplicated value drifts silently.
    internal const int DefaultRequestTimeoutSeconds = 300;
    internal const int DefaultNumRetries = 5;

    /// <summary>Longest a single <c>GetResponseAsync</c> can stay silent before
    /// it must either return or throw — i.e. the worst-case wall time of the
    /// whole retry stack, not of one attempt.
    ///
    /// ⚠ THIS IS THE QUANTITY A SILENCE-BASED WATCHDOG MUST CLEAR. Retries
    /// happen INSIDE the SDK pipeline, beneath the single await the agent loop
    /// is parked on, so they emit no events. An observer watching for silence
    /// sees one uninterrupted gap of (retries + 1) x timeout, NOT a gap of one
    /// timeout punctuated by retry activity. Anything that sizes itself against
    /// a single request_timeout_seconds is off by a factor of (retries + 1).
    ///
    /// ⛔ THIS FORMULA WAS FALSE FOR THE WHOLE OF vett's LIFE UNTIL 2026-09-01,
    /// AND IT WAS FALSE IN THE DANGEROUS DIRECTION -- it UNDER-counted, so the
    /// watchdog was sized under the stack it must outlast. The cause was not
    /// arithmetic: `num_retries` never reached the wire at all, because
    /// System.ClientModel installs a default ClientRetryPolicy (3 retries = 4
    /// attempts) that ran instead. Measured at a black-hole socket, three arms,
    /// num_retries 0 / 2 / 5: FOUR attempts every time, error at exactly
    /// 4 x timeout. See EpicForge's retry_budget_probe.js (not in this repo).
    ///
    /// ⛔ SO THIS METHOD IS ONLY TRUE WHILE BuildOptions SETS opts.RetryPolicy
    /// FROM THE SAME num_retries. They are one claim in two files. Delete that
    /// line and this one silently starts lying again, in the direction that
    /// reaps live members. RetryBudgetTests asserts the pairing; removing the
    /// line fails 8 of its 12 (verified by doing it, 2026-09-01).
    ///
    /// ⚠ THIS EXCLUDES BACKOFF, AND SAYS SO RATHER THAN ROUNDING IT AWAY. The
    /// SDK sleeps between attempts, measured at 0.8s doubling per retry, so the
    /// real window is (retries + 1) x timeout + 0.8 x (2^retries - 1): at
    /// num_retries 5 the probe took 54.9s where this returns 30. The absolute
    /// term is bounded and small (24.8s at 5 retries) and the caller's x1.25
    /// covers it for any timeout above ~20s -- but a caller that ever drops
    /// that multiplier must add the backoff term back.</summary>
    public static int EffectiveRetryBudgetSeconds(LlmConfig config)
        => ((config.NumRetries ?? DefaultNumRetries) + 1)
           * (config.RequestTimeoutSeconds ?? DefaultRequestTimeoutSeconds);

    /// <summary>Build ONE client for a single config (no failover wrapping).</summary>
    private static IChatClient CreateSingle(LlmConfig config, string? endpointOverride = null, string? modelOverride = null, string? apiKeyOverride = null)
    {
        var provider = config.Provider.ToLowerInvariant();
        var endpoint = !string.IsNullOrEmpty(endpointOverride) ? endpointOverride : config.Endpoint;
        var model = !string.IsNullOrEmpty(modelOverride) ? modelOverride : config.Model;
        var apiKey = !string.IsNullOrEmpty(apiKeyOverride)
            ? apiKeyOverride
            : !string.IsNullOrEmpty(config.ApiKeyEnv)
                ? Environment.GetEnvironmentVariable(config.ApiKeyEnv) ?? ""
                : "";
        var timeout = config.RequestTimeoutSeconds ?? DefaultRequestTimeoutSeconds;
        var retries = config.NumRetries ?? DefaultNumRetries;

        var client = provider switch
        {
            "local" or "" => CreateOpenAICompatible(endpoint, model, apiKey, config.EnableThinking, timeout, retries),
            // An explicit endpoint now WINS for `openai` too. CreateOpenAI
            // builds `new OpenAIClient(apiKey)`, which carries the SDK's own
            // base URL and silently DISCARDED whatever the profile said — so a
            // `provider: openai` profile aimed at a gateway or proxy sent its
            // traffic to api.openai.com without a word. No profile in either
            // store sets provider:openai WITH an endpoint (checked 2026-08-26,
            // both stores), so honouring it changes no existing binding.
            "openai" => string.IsNullOrEmpty(endpoint)
                ? CreateOpenAI(model, apiKey)
                : CreateOpenAICompatible(endpoint, model, apiKey, config.EnableThinking, timeout, retries),
            "azure" => CreateAzure(endpoint, model, apiKey, config.EnableThinking, timeout, retries),
            "anthropic" or "google" => CreateOpenAICompatible(
                !string.IsNullOrEmpty(endpoint) ? endpoint : DefaultEndpointFor(provider)!,
                model, apiKey, config.EnableThinking, timeout, retries),
            _ => throw new VettException($"Unknown LLM provider: \"{provider}\". Supported: local, openai, anthropic, azure, google"),
        };

        // Law 135: stream by default so `timeout` bounds SILENCE, not the whole
        // generation. Sits inside FailoverChatClient (built by Create) so an
        // idle timeout still hops endpoints; `stream: false` in the profile
        // returns the buffered client byte-for-byte.
        return (config.Stream ?? true)
            ? new SilenceBoundedChatClient(client, TimeSpan.FromSeconds(timeout))
            : client;
    }

    /// <summary>
    /// Merge a member's LLM config with the profile's base config.
    /// Member values override profile values where set.
    /// </summary>
    public static LlmConfig Merge(LlmConfig baseConfig, LlmConfig? memberOverride)
    {
        if (memberOverride is null)
            return baseConfig;

        // ⛔ A CREDENTIAL IS SCOPED TO THE HOST IT WAS ISSUED FOR (2026-08-26).
        // This used to inherit api_key_env unconditionally. In ds-team-lead-pro
        // the base llm: is OpenRouter with api_key_env: DEEPSEEK_API_KEY, and
        // the implementer/researcher seats override provider+endpoint to the
        // LAN vLLM at gpu-1:8000. They inherited the key, so the real
        // OpenRouter token went out as an `Authorization: Bearer` header over
        // PLAIN HTTP to a LAN box.
        //
        // That profile's own comment (ds-team-lead-pro.yaml:384-385) says
        // "NO api_key_env: the vLLM server is open on the LAN" — the code was
        // contradicting the stated intent at the very seat it applied to.
        //
        // ⚠ NOTHING WOULD EVER HAVE CAUGHT IT AT RUNTIME. vLLM ignores auth and
        // answers 200 with or without a bearer token, so the leak produced no
        // failing observation anywhere. It failed SILENTLY-WELL.
        //
        // Destination = the resolved endpoint, or `provider:<name>` when the
        // endpoint is blank — otherwise an SDK-default `openai` seat and a
        // `local` seat both look like "" and would compare equal despite being
        // different hosts.
        //
        // TRADE-OFF, STATED: a seat deliberately repointed to a DIFFERENT host
        // of the same vendor (a regional mirror, a proxy) must now restate
        // api_key_env. That is one line of YAML and it fails LOUDLY with a 401.
        // The old behaviour failed silently by exfiltrating a credential. For a
        // credential, fail-closed is the correct default.
        var mergedProvider = memberOverride.HasExplicitProvider
            ? memberOverride.Provider : baseConfig.Provider;
        var mergedEndpoint = !string.IsNullOrEmpty(memberOverride.Endpoint)
            ? memberOverride.Endpoint : baseConfig.Endpoint;

        static string Destination(string provider, string endpoint)
        {
            var resolved = string.IsNullOrEmpty(endpoint)
                ? DefaultEndpointFor(provider) ?? ""
                : endpoint;
            return string.IsNullOrEmpty(resolved)
                ? "provider:" + (provider ?? "").ToLowerInvariant()
                : resolved.TrimEnd('/').ToLowerInvariant();
        }

        var sameHost = Destination(mergedProvider, mergedEndpoint)
                    == Destination(baseConfig.Provider, baseConfig.Endpoint);

        return new LlmConfig
        {
            // HasExplicitProvider, not a `!= "local"` sentinel: "local" is a
            // VALID provider, so the sentinel made an explicitly-written
            // `provider: local` on a member indistinguishable from an unset
            // one and silently inherited the base's provider instead. Under a
            // cloud base (provider: openai) that sent the member's traffic to
            // the cloud — and because CreateOpenAI ignores `endpoint`, its
            // local endpoint was dropped too. Same defect the nullable
            // Temperature / TopP / RequestTimeoutSeconds / NumRetries fields
            // below were converted to fix.
            // Computed above, not repeated here: sameHost is derived from these
            // same two values, and a rule written twice drifts silently.
            Provider = mergedProvider,
            Endpoint = mergedEndpoint,
            Model = !string.IsNullOrEmpty(memberOverride.Model)
                ? memberOverride.Model : baseConfig.Model,
            // Explicit member key ALWAYS wins — that is the author's informed
            // choice and is what keeps ds-team-flash-escalate's implementer-pro
            // seat (local base, cloud seat, own api_key_env) working. An
            // INHERITED key travels only within the base's own host.
            ApiKeyEnv = !string.IsNullOrEmpty(memberOverride.ApiKeyEnv)
                ? memberOverride.ApiKeyEnv
                : sameHost ? baseConfig.ApiKeyEnv : "",
            // null = "not set in YAML" so member values inherit cleanly. The previous
            // sentinel-value approach (e.g. != 1.0) silently dropped explicit overrides
            // when the user happened to set temperature to exactly 1.0.
            Temperature = memberOverride.Temperature ?? baseConfig.Temperature,
            TopP = memberOverride.TopP ?? baseConfig.TopP,
            EnableThinking = memberOverride.EnableThinking ?? baseConfig.EnableThinking,
            // Nullable now, so "unset" inherits cleanly instead of the old
            // `> 0` sentinel that made an unset member silently win with the
            // type default (dropping the base's timeout / retries).
            RequestTimeoutSeconds = memberOverride.RequestTimeoutSeconds ?? baseConfig.RequestTimeoutSeconds,
            NumRetries = memberOverride.NumRetries ?? baseConfig.NumRetries,
            // WAS MISSING ENTIRELY until 2026-09-01, so every merge returned a
            // null bound however the base was written. It is the ONE field of
            // LlmConfig's fourteen that this method never named, and nothing
            // failed: the five Coordinator seats read the bound through their
            // own hand-written `m.Llm?.MaxOutputTokens ?? profile.Llm.…`
            // fallback and were correct without it.
            //
            // The one consumer that could not was RunNestedTeamAsync, which
            // builds a NESTED sub-team's profile as `Llm = Merge(parent.Llm,
            // m.Llm)` and hands it down. A nested team under a profile that set
            // `max_output_tokens: 4096` therefore generated with NO ceiling —
            // and only when the sub-team's own member carried an `llm:` block,
            // because a null override short-circuits this method and returns
            // the base untouched. Unbounded generation under a per-attempt
            // timeout is the failure that reads as a hang, not as a config bug.
            //
            // LlmConfigMergeCoverageTests spans the type by reflection so a
            // fifteenth field cannot repeat this.
            MaxOutputTokens = memberOverride.MaxOutputTokens ?? baseConfig.MaxOutputTokens,
            Stream = memberOverride.Stream ?? baseConfig.Stream,
            Fallbacks = memberOverride.Fallbacks ?? baseConfig.Fallbacks,
            // A seat may name its own capability / priority; unset inherits.
            // Same nullable-vs-sentinel reasoning as the fields above, except
            // the "unset" marker here is the empty string, because the type is
            // a string and "" is not a valid capability name.
            Capability = !string.IsNullOrEmpty(memberOverride.Capability)
                ? memberOverride.Capability : baseConfig.Capability,
            Priority = !string.IsNullOrEmpty(memberOverride.Priority)
                ? memberOverride.Priority : baseConfig.Priority,
            ContextTokens = memberOverride.ContextTokens > 0
                ? memberOverride.ContextTokens : baseConfig.ContextTokens,
            // 2026-09-11: fields FIFTEEN and SIXTEEN, and they repeated field
            // fourteen's story exactly — added to LlmConfig, wired through the
            // Coordinator's hand-written `m.Llm?.X ?? profile.Llm.X` fallback
            // at five sites, and never named here. The reflection gate above
            // caught it, which is the only reason this comment exists rather
            // than another silent nested-team defect. null is meaningful: it
            // means "omit the parameter and let the provider default" (vLLM: 0),
            // so it must not be manufactured into a number by the merge.
            PresencePenalty = memberOverride.PresencePenalty ?? baseConfig.PresencePenalty,
            FrequencyPenalty = memberOverride.FrequencyPenalty ?? baseConfig.FrequencyPenalty,
        };
    }

    private static IChatClient CreateOpenAICompatible(string endpoint, string model, string apiKey, bool? enableThinking = null, int requestTimeoutSeconds = 300, int numRetries = 5)
    {
        if (string.IsNullOrEmpty(endpoint))
            throw new VettException("LLM endpoint is required for local/compatible provider");

        var key = string.IsNullOrEmpty(apiKey) ? "none" : apiKey;
        return new OpenAIClient(
            new ApiKeyCredential(key),
            BuildOptions(endpoint, enableThinking, requestTimeoutSeconds, numRetries))
            .GetChatClient(model).AsIChatClient();
    }

    /// <summary>
    /// Build OpenAIClientOptions with failover retry logic and timeout config.
    /// Changes from the SDK defaults:
    ///   - NetworkTimeout honored from config (default 300s, can be overridden).
    ///   - Adds RequestRetryPolicy for transient failures (timeouts, 5xx, etc).
    ///   - WireParityPolicy handles vLLM/Qwen compatibility (tool schemas, enable_thinking).
    /// </summary>
    // internal, not private: RetryBudgetTests reflects on the options this
    // returns to prove num_retries actually reaches the SDK retry policy.
    internal static OpenAIClientOptions BuildOptions(string endpoint, bool? enableThinking = null, int requestTimeoutSeconds = 300, int numRetries = 5)
    {
        var opts = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(requestTimeoutSeconds),
        };
        // ⛔⛔ WITHOUT THIS LINE `num_retries` IS INERT. MEASURED, not read off
        // the source: System.ClientModel installs its OWN ClientRetryPolicy by
        // default (3 retries = 4 attempts), it sits BENEATH the PerCall policy
        // added below, and it swallows the timeout before the outer policy ever
        // sees it. EpicForge's retry_budget_probe.js (not in this repo) points vett at
        // a socket that accepts and never answers and counts the attempts the
        // SERVER received -- num_retries 0, 2 and 5 all produced exactly FOUR
        // attempts, 5.0s apart, with the error arriving at 20.0s in all three
        // arms. Three settings, one behaviour: the knob was decorative.
        //
        // That is not only a dead config field. EffectiveRetryBudgetSeconds
        // sizes the member watchdog as (num_retries + 1) x timeout, so on every
        // profile where that lands under 4 x timeout the watchdog was set to
        // fire BEFORE the retry stack it exists to outlast -- 13 of the 21
        // profiles in profiles/, ds-team-flash and ds-solo-flash among them.
        // Binding the knob here makes the formula true by construction rather
        // than by luck, and hands the silence window back to the profile.
        opts.RetryPolicy = new ClientRetryPolicy(numRetries);
        opts.AddPolicy(new RequestRetryPolicy(numRetries), PipelinePosition.PerCall);
        opts.AddPolicy(new WireParityPolicy(enableThinking), PipelinePosition.PerCall);
        return opts;
    }

    private static IChatClient CreateOpenAI(string model, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            throw new VettException("API key required for OpenAI (set api_key_env in profile)");

        return new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient();
    }

    private static IChatClient CreateAzure(string endpoint, string model, string apiKey, bool? enableThinking = null, int requestTimeoutSeconds = 300, int numRetries = 5)
    {
        if (string.IsNullOrEmpty(endpoint))
            throw new VettException("Endpoint required for Azure OpenAI");
        if (string.IsNullOrEmpty(apiKey))
            throw new VettException("API key required for Azure OpenAI");

        return new OpenAIClient(
            new ApiKeyCredential(apiKey),
            BuildOptions(endpoint, enableThinking, requestTimeoutSeconds, numRetries))
            .GetChatClient(model).AsIChatClient();
    }
}

/// <summary>
/// Rewrites outgoing chat completion bodies for vLLM compatibility:
///   - drops tool_choice when its value is the default "auto" (matches
///     openhands-sdk wire format; some chat templates render the field
///     differently when present vs absent)
///   - injects chat_template_kwargs.enable_thinking when configured
///     (Qwen3 family chat-template kwarg)
/// Pass-through for any path that isn't a chat-completions request body.
///
/// Historical note: this policy used to ALSO strip
/// `additionalProperties: false` from each tool's parameters (per a
/// "match what openhands-sdk sends" guess). That stripping turned out
/// to actively break aeon-mtp + qwen3_coder tool-call generation —
/// without the field, the model sometimes emits the tool name as plain
/// text (`&lt;file_editor&gt;`, `[view]`, bare `file_editor`) and the
/// parser extracts no tool_call. Diagnosed 2026-05-08 by replaying the
/// post-policy body against vLLM directly: stripping reproduced the
/// failure 100%, putting it back fixed it 100%.
/// </summary>
public sealed class WireParityPolicy(bool? enableThinking = null) : PipelinePolicy
{
    private readonly bool? _enableThinking = enableThinking;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        if (currentIndex < pipeline.Count - 1)
            pipeline[currentIndex + 1].Process(message, pipeline, currentIndex + 1);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        if (currentIndex < pipeline.Count - 1)
            await pipeline[currentIndex + 1].ProcessAsync(message, pipeline, currentIndex + 1);
    }

    private void Rewrite(PipelineMessage message)
    {
        var req = message.Request;
        if (req?.Content is null) return;
        var uri = req.Uri?.AbsolutePath ?? "";
        if (!uri.EndsWith("/chat/completions", StringComparison.Ordinal)) return;

        using var ms = new MemoryStream();
        req.Content.WriteTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) return;

        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch { return; }
        if (root is not JsonObject obj) return;

        bool changed = false;

        if (obj["tool_choice"] is JsonValue tc
            && tc.TryGetValue<string>(out var tcStr)
            && tcStr == "auto")
        {
            obj.Remove("tool_choice");
            changed = true;
        }

        if (_enableThinking.HasValue)
        {
            // Merge into existing chat_template_kwargs without clobbering
            // other keys the caller (or another policy) may have set.
            if (obj["chat_template_kwargs"] is not JsonObject ctk)
            {
                ctk = new JsonObject();
                obj["chat_template_kwargs"] = ctk;
            }
            ctk["enable_thinking"] = _enableThinking.Value;
            changed = true;
        }

        if (!changed) return;

        var newBytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
        req.Content = BinaryContent.Create(BinaryData.FromBytes(newBytes));
    }
}


/// <summary>
/// Endpoint failover with exponential backoff retry for transient failures.
/// Retries on: timeouts, 429 (rate limit), 5xx server errors, connection failures.
/// </summary>
public sealed class RequestRetryPolicy(int maxRetries = 5) : PipelinePolicy
{
    private readonly int _maxRetries = maxRetries;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessCore(message, pipeline, currentIndex, 0);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessCoreAsync(message, pipeline, currentIndex, 0);
    }

    private void ProcessCore(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex, int attempt)
    {
        try
        {
            if (currentIndex < pipeline.Count - 1)
                pipeline[currentIndex + 1].Process(message, pipeline, currentIndex + 1);
        }
        catch (HttpRequestException ex) when (ShouldRetry(ex) && attempt < _maxRetries)
        {
            Backoff(attempt);
            ProcessCore(message, pipeline, currentIndex, attempt + 1);
        }
        catch (TaskCanceledException) when (attempt < _maxRetries)
        {
            Backoff(attempt);
            ProcessCore(message, pipeline, currentIndex, attempt + 1);
        }
    }

    private async ValueTask ProcessCoreAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex, int attempt)
    {
        try
        {
            if (currentIndex < pipeline.Count - 1)
                await pipeline[currentIndex + 1].ProcessAsync(message, pipeline, currentIndex + 1);
        }
        catch (HttpRequestException ex) when (ShouldRetry(ex) && attempt < _maxRetries)
        {
            await BackoffAsync(attempt);
            await ProcessCoreAsync(message, pipeline, currentIndex, attempt + 1);
        }
        catch (TaskCanceledException) when (attempt < _maxRetries)
        {
            await BackoffAsync(attempt);
            await ProcessCoreAsync(message, pipeline, currentIndex, attempt + 1);
        }
    }

    private static bool ShouldRetry(HttpRequestException ex)
    {
        // A transport-level failure (connection refused, DNS, socket reset)
        // surfaces as an HttpRequestException with no StatusCode. That is the
        // real "endpoint is down" signal and MUST be retried here — pre-V1 it
        // fell through and stalled the run until the agent-layer catch fired.
        if (ex.StatusCode is null)
            return true;

        return ex.StatusCode is
            System.Net.HttpStatusCode.TooManyRequests or      // 429
            System.Net.HttpStatusCode.InternalServerError or  // 500
            System.Net.HttpStatusCode.BadGateway or           // 502
            System.Net.HttpStatusCode.ServiceUnavailable or   // 503
            System.Net.HttpStatusCode.GatewayTimeout;         // 504
    }

    private static void Backoff(int attempt)
    {
        var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
        Thread.Sleep(delay);
    }

    private static async ValueTask BackoffAsync(int attempt)
    {
        var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
        await Task.Delay(delay);
    }
}

/// <summary>General VETT error — not a plugin, tool, LLM, or sandbox issue.</summary>
public sealed class VettException(string message) : Exception(message);
