using System.Text.Json;

namespace Vett.Config;

/// <summary>
/// One (endpoint, model) pair a profile will actually talk to, with the
/// seat that asks for it. <paramref name="Path"/> is the seat's dotted path
/// — <c>llm</c> for the leader, <c>team.members[1].llm</c> for a member — so
/// a failure names the seat, not just the profile.
/// </summary>
public sealed record EndpointTarget(
    string Path,
    string Endpoint,
    string Model,
    string ApiKeyEnv,
    int ThresholdTokens)
{
    /// <summary>
    /// What one catalogue fetch covers. Keyed on (endpoint, credential) and
    /// deliberately NOT on model: <c>GET /models</c> returns the WHOLE
    /// catalogue, so every model on a host is answered by a single response.
    ///
    /// ⛔ Keying this on the model too is not merely wasteful — OpenRouter's
    /// catalogue is ~688KB, and re-pulling it per model pushed later probes
    /// past their deadline and reported a live host as DEAD. Model and
    /// threshold are per-seat comparisons made against the one cached
    /// catalogue this key resolves to.
    /// </summary>
    public (string, string) CatalogueKey => (Endpoint.TrimEnd('/'), ApiKeyEnv);
}

/// <summary>Severity of one probe result. Mirrors <c>vett validate</c>'s
/// existing vocabulary so the two report through the same counters.</summary>
public enum ProbeLevel { Ok, Warn, Error }

public sealed record ProbeVerdict(string Path, ProbeLevel Level, string Message);

/// <summary>
/// A seat whose LLM config is incomplete on the profile alone.
/// <paramref name="Missing"/> names the fields — "endpoint", "model", or
/// "endpoint and model" — so the message says what to add, not just that
/// something is absent. See <see cref="EndpointProbe.IncompleteSeatsOf"/>.
/// </summary>
public sealed record SeatGap(string Path, string Missing);

/// <summary>
/// A seat whose EFFECTIVE llm config sets no <c>max_output_tokens</c>, so a
/// single generation has no ceiling of its own. See
/// <see cref="EndpointProbe.UnboundedOutputSeatsOf"/>.
/// </summary>
public sealed record UnboundedSeat(string Path, int RequestTimeoutSeconds, int NumRetries);

/// <summary>
/// A profile whose whole-run cap (<c>timeout_minutes</c>) is shorter than ONE
/// seat's retry budget, so the run is killed part-way through a retry stack it
/// was configured to complete. See
/// <see cref="EndpointProbe.RunCapShorterThanRetryStackOf"/>.
/// </summary>
public sealed record RunCapGap(string Path, int RunCapSeconds, int RetryBudgetSeconds);

/// <summary>
/// A seat that authored an <c>llm.priority</c> word the runtime will
/// reject. <paramref name="Message"/> is <b>the runtime’s own message</b>,
/// taken from the exception <c>CapacityBinding.PriorityOf</c> throws — not a
/// restatement of it. A duplicated vocabulary drifts silently, and a
/// validator that disagreed with the runtime about which words are legal
/// would be worse than no validator at all.
/// See <see cref="EndpointProbe.BadPrioritiesOf"/>.
/// </summary>
public sealed record PriorityGap(string Path, string Word, string Message);

/// <summary>
/// Answers the question <c>vett validate</c> structurally could not:
/// <b>will this profile's seats actually reach a server that serves the
/// model they name?</b>
///
/// ⛔ WHY THIS EXISTS. The schema validator checks SHAPE. A profile naming a
/// host that has been switched off, or a model the server does not serve,
/// parses perfectly and reports a clean tick. That is not a hypothetical:
/// 31 suites pointed at <c>old-gpu-a</c> for weeks, passing validation
/// green the whole time, because nothing ever asked the host a question.
/// A green from a shape checker is not a statement about liveness.
///
/// ⚠ WHAT THIS IS NOT. A reachable endpoint serving the right model is a
/// NECESSARY condition, not a sufficient one — it says the seat can connect,
/// never that the run will succeed. Report it as "reachable", never as
/// "works".
///
/// ⚠ NETWORK, AND ONLY GET. Every call here is <c>GET {endpoint}/models</c>,
/// which is a catalogue read: no prompt, no completion, no token spend on any
/// metered provider. It still touches the network, so it is opt-in behind
/// <c>--check-endpoints</c> and never runs as part of a plain validate.
/// </summary>
public static class EndpointProbe
{
    /// <summary>
    /// Every distinct seat in a profile, with LLM settings resolved the way
    /// the runtime resolves them: a member with no <c>llm:</c> block inherits
    /// the profile's, and likewise for <c>compaction:</c>. Resolving
    /// inheritance here is the whole point — an unresolved view would report
    /// a member as "no endpoint" when at run time it very much has one.
    /// </summary>
    public static List<EndpointTarget> TargetsOf(Profile profile)
    {
        var targets = new List<EndpointTarget>();
        Collect(profile.Llm, profile.Compaction, "llm", profile.Llm, profile.Compaction, targets);
        WalkTeam(profile.Team, "team", profile.Llm, profile.Compaction,
            (llm, comp, path, inhLlm, inhComp) => Collect(llm, comp, path, inhLlm, inhComp, targets));
        return targets;
    }

    /// <summary>
    /// Visit every seat below a team — the LEADER and each member, then
    /// recursively any sub-team a member owns — applying the runtime's own
    /// inheritance rules on the way down.
    ///
    /// ⛔ THIS EXISTS BECAUSE THE GATE DID NOT SPAN ITS POPULATION (2026-08-26).
    /// <see cref="TargetsOf"/> and <see cref="IncompleteSeatsOf"/> walked
    /// <c>profile.Llm</c> plus <c>profile.Team.Members</c> and nothing else.
    /// Two whole classes of seat were invisible to <c>vett validate</c>:
    ///
    ///   1. <c>profile.Team.Leader</c> — never collected at all.
    ///      <c>grep -n "Leader" EndpointProbe.cs</c> returned NOTHING.
    ///   2. <c>member.Team</c> — a nested sub-team. Every seat below the first
    ///      nesting level was unreachable, and the coordinator allows more than
    ///      one (<c>TeamCoordinator.MaxDispatchDepth</c> — read the constant,
    ///      it has changed once and restating it here would drift).
    ///
    /// ⚠ IT HAD NOT BITTEN YET, AND THAT IS THE DANGEROUS PART. In every
    /// profile shipping today the leader block sets only temperature/top_p, so
    /// its EFFECTIVE config is field-identical to <c>profile.Llm</c> — which IS
    /// collected, under the path "llm". The leader was covered by COINCIDENCE,
    /// not by construction, and the instrument reported "all seats OK" while
    /// never having looked at it. A gate that passes because of an accident of
    /// the current data is a gate that fails OPEN the moment the data moves.
    /// It moves the instant a leader is given its own endpoint — which is
    /// exactly what a cloud-leader-over-local-workers profile does.
    ///
    /// The two inheritance rules below are MIRRORED FROM THE RUNTIME
    /// (<c>Coordinator.cs:95-108</c>), not invented here:
    ///     Llm        = ChatClientFactory.Merge(parentProfile.Llm, m.Llm)
    ///     Compaction = m.Compaction ?? parentProfile.Compaction
    /// So a nested implementer under a Pro feature-lead inherits the
    /// FEATURE-LEAD's endpoint, not the top-level profile's. Getting that
    /// wrong would make the instrument disagree with the runtime, which is the
    /// one thing this file is not allowed to do.
    ///
    /// No cycle guard: the structure is a tree deserialised from YAML and
    /// cannot be self-referential. Depth is bounded by the document.
    /// </summary>
    private static void WalkTeam(
        TeamConfig? team, string path,
        LlmConfig inheritedLlm, CompactionConfig? inheritedCompaction,
        Action<LlmConfig?, CompactionConfig?, string, LlmConfig, CompactionConfig?> visit)
    {
        if (team is null) return;

        // The leader seat. TeamConfig.Leader is non-nullable (Profile.cs:348,
        // `= new()`), so there is always one to inspect even when the YAML
        // omits the block entirely — in which case it inherits wholesale and
        // resolves identically to "llm", which is correct and harmless.
        var leaderName = string.IsNullOrWhiteSpace(team.Leader.Name) ? "leader" : team.Leader.Name;
        visit(team.Leader.Llm, team.Leader.Compaction,
            $"{path}.leader[{leaderName}].llm", inheritedLlm, inheritedCompaction);

        var members = team.Members;
        if (members is null) return;

        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            // ⚠ PATH STRINGS ARE BYTE-IDENTICAL to the old shape for the
            // non-nested case ("team.members[0:implementer].llm"), so nothing
            // that reads or matches on them changes behaviour. New strings
            // appear ONLY for seats that were previously ABSENT.
            var memberPath = $"{path}.members[{i}:{m.Name}]";
            visit(m.Llm, m.Compaction, $"{memberPath}.llm", inheritedLlm, inheritedCompaction);

            WalkTeam(m.Team, memberPath,
                Llm.ChatClientFactory.Merge(inheritedLlm, m.Llm),   // runtime rule
                m.Compaction ?? inheritedCompaction,                // runtime rule
                visit);
        }
    }

    /// <summary>
    /// Seats that cannot start a session on the profile ALONE, resolved with
    /// the same inheritance rule as <see cref="TargetsOf"/> so the instrument
    /// and the runtime cannot disagree about what a member inherits.
    ///
    /// ⛔ WHY THIS IS SEPARATE FROM PROBING. Whether a profile even NAMES an
    /// endpoint and a model is knowable from the YAML — no network, no
    /// catalogue fetch. It was only ever surfaced under
    /// <c>--check-endpoints</c>, so a plain <c>validate</c> printed a green
    /// tick over a profile that <c>chat</c> refuses to start. Measured on
    /// <c>coding</c> — the VETT Chat extension's DEFAULT profile —
    /// 2026-08-26: <c>validate --profile coding</c> → "✓ coding" /
    /// "0 error(s), 0 warning(s)", while <c>vett chat --profile coding</c>
    /// died with "endpoint and model are required".
    ///
    /// ⚠ NOT A FAILURE VERDICT, DELIBERATELY. endpoint/model may legitimately
    /// arrive at run time from <c>--endpoint</c>/<c>--model</c> or
    /// <c>VETT_LLM_ENDPOINT</c>/<c>VETT_LLM_MODEL</c> — that is how the
    /// benchmark profiles are driven. The finding is "incomplete on its own",
    /// never "broken", so the caller withholds the tick rather than
    /// manufacturing an error.
    ///
    /// <c>endpoint</c> is required only for the <c>local</c>
    /// (OpenAI-compatible) provider; the cloud SDKs carry their own base URL.
    /// <c>model</c> is required by every provider — nothing in the stack
    /// defaults it.
    /// </summary>
    public static List<SeatGap> IncompleteSeatsOf(Profile profile)
    {
        var gaps = new List<SeatGap>();
        Inspect(profile.Llm, "llm", profile.Llm, gaps);
        // Same population as TargetsOf — see WalkTeam. Sharing the walker is
        // deliberate: two hand-rolled traversals would be free to drift, and a
        // startability check that spans FEWER seats than the reachability check
        // is the same fail-open defect in a second place.
        WalkTeam(profile.Team, "team", profile.Llm, profile.Compaction,
            (llm, _, path, inhLlm, _) => Inspect(llm, path, inhLlm, gaps));
        return gaps;
    }

    /// <summary>
    /// Seats whose <c>llm.priority</c> names a word the runtime will refuse.
    ///
    /// ⛔ WHY VALIDATE HAS TO ASK. <c>CapacityBinding.PriorityOf</c> throws on
    /// an unknown word, so a typo FAILS CLOSED — but it fails closed
    /// <b>mid-run</b>, after the profile has already been loaded and a run
    /// started. Fail-late is not fail-open, and this is not a correctness
    /// hole; it is a validator that did not span a vocabulary it could have.
    ///
    /// ⛔ THE VOCABULARY IS NOT RESTATED HERE. This calls
    /// <c>PriorityOf</c> and reports the exception it throws. Writing the
    /// legal words out a second time would let validate and the runtime
    /// drift, and the drift would be invisible: validate would go green on a
    /// word the runtime rejects, or red on one it accepts.
    ///
    /// ⚠ SPANS SEATS, NOT JUST THE ROOT. <c>ChatClientFactory.Merge</c>
    /// carries <c>Priority</c> — "a seat may name its own capability /
    /// priority; unset inherits" — so a member or leader block can author one
    /// and reach <c>PriorityOf</c> at run time. A gate reading only
    /// <c>profile.Llm</c> would pass VACUOUSLY over every seat. It shares
    /// <c>WalkTeam</c> with <see cref="TargetsOf"/> and
    /// <see cref="IncompleteSeatsOf"/> so the three cannot span different
    /// populations.
    ///
    /// ⚠ EACH SEAT’S OWN WORD, NEVER THE INHERITED ONE. Reporting the
    /// EFFECTIVE (post-merge) priority would turn one bad root value into one
    /// error per seat — a count that grows with team size and points at seats
    /// whose YAML is blameless. A seat that authored nothing is not at fault
    /// for what it inherited, and the root is checked once on its own.
    /// </summary>
    public static List<PriorityGap> BadPrioritiesOf(Profile profile)
    {
        var bad = new List<PriorityGap>();
        Check(profile.Llm.Priority, "llm", bad);
        WalkTeam(profile.Team, "team", profile.Llm, profile.Compaction,
            (llm, _, path, _, _) => Check(llm?.Priority, path, bad));
        return bad;
    }

    private static void Check(string? word, string path, List<PriorityGap> into)
    {
        // Empty is not a typo — it is the documented "use the default"
        // marker, and PriorityOf maps it to DefaultPriority. Only a word the
        // author actually wrote can be wrong.
        if (string.IsNullOrWhiteSpace(word)) return;
        try
        {
            Llm.CapacityBinding.PriorityOf(word);
        }
        catch (Llm.VettException e)
        {
            into.Add(new PriorityGap(path, word, e.Message));
        }
    }

    private static void Inspect(LlmConfig? llm, string path, LlmConfig inherited, List<SeatGap> into)
    {
        var effective = Llm.ChatClientFactory.Merge(inherited, llm);

        var missing = new List<string>();
        // Only the OpenAI-compatible "local" provider needs an explicit base
        // URL. Flagging a missing endpoint on `anthropic`/`openai` would be a
        // false positive — their SDKs already know where to go.
        if (effective.Provider == "local" && string.IsNullOrWhiteSpace(effective.Endpoint))
            missing.Add("endpoint");
        if (string.IsNullOrWhiteSpace(effective.Model))
            missing.Add("model");

        if (missing.Count > 0)
            into.Add(new SeatGap(path, string.Join(" and ", missing)));
    }

    private static void Collect(
        LlmConfig? llm, CompactionConfig? compaction, string path,
        LlmConfig inheritedLlm, CompactionConfig? inheritedCompaction,
        List<EndpointTarget> into)
    {
        // ⛔ FIELD-LEVEL MERGE, NOT `llm ?? inherited`. The runtime resolves a
        // member through ChatClientFactory.Merge (Coordinator.cs:63), which
        // inherits per FIELD — so a member whose block sets only
        // `temperature: 0.3` still inherits the profile's endpoint and model.
        //
        // The old null-coalesce let any non-null block win outright, so such a
        // member resolved to a BLANK endpoint and was dropped by the guard
        // below without a word. That is how nearly every team profile in the
        // store — ds-team-flash, ds-team-pro, ds-team-lead-pro,
        // ds-team-flash-escalate, all coding-team*/dsv4-team* — had its member
        // seats skipped while the sweep still printed "(endpoints live)".
        //
        // The old rule was right at both extremes the tests happened to
        // construct (no block at all, or a complete one) and wrong in the
        // middle, which is the only case real profiles use.
        var effective = Llm.ChatClientFactory.Merge(inheritedLlm, llm);
        if (string.IsNullOrWhiteSpace(effective.Endpoint)) return;

        var threshold = (compaction ?? inheritedCompaction)?.ThresholdTokens ?? 0;
        into.Add(new EndpointTarget(
            path, effective.Endpoint, effective.Model, effective.ApiKeyEnv, threshold));
    }

    /// <summary>
    /// The connection and catalogue cache shared across everything one sweep
    /// probes.
    ///
    /// ⛔ WHY THIS IS A TYPE AND NOT A LOCAL. It was a local, and that was a
    /// defect: the caller loops over profiles and probes each one, so a cache
    /// and an <see cref="HttpClient"/> created inside the probe call are
    /// rebuilt PER PROFILE. Deduplication then only ever worked within a
    /// single profile — a 32-profile store meant 32 clients and 32 fetches of
    /// the same catalogue.
    ///
    /// The symptom is a lying instrument, not a slow one: a sweep reported
    /// openrouter.ai unreachable for three profiles while reporting it live
    /// for six others in the same run, all nine naming the identical
    /// (endpoint, credential) pair — each paying its own cold-connection
    /// download of a ~688KB catalogue against an 8s deadline, some losing the
    /// race. A direct curl returned HTTP 200 in 0.5s. A liveness probe whose
    /// false answer is "dead" is worse than no probe at all, because it
    /// condemns working config.
    ///
    /// Making the lifetime an explicit parameter is what stops that
    /// recurring: the caller now has to say how long the sharing lasts, and
    /// the natural place to put it is around the whole loop.
    /// </summary>
    public sealed class ProbeSession : IDisposable
    {
        internal readonly HttpClient Http = new();
        internal readonly Dictionary<(string, string), CatalogueResult> Cache = new();

        /// <summary>Distinct catalogue fetches actually performed. Exposed so a
        /// caller — or a test — can assert the sharing is real rather than
        /// assume it: this is the number that silently equalled the profile
        /// count when the cache was per-profile.</summary>
        public int Fetches { get; internal set; }

        public void Dispose() => Http.Dispose();
    }

    /// <summary>
    /// Probe every distinct (endpoint, credential) once per <paramref
    /// name="session"/> and fan the verdict back out to each seat that asked
    /// for it. Seats sharing a catalogue still get their own model and
    /// threshold comparison, because two seats can name the same server with
    /// different models and different compaction settings.
    ///
    /// ⚠ Failures are cached alongside successes, deliberately: re-asking a
    /// host that just timed out costs another full timeout and cannot produce
    /// better information. The consequence worth knowing is that within one
    /// session a given (endpoint, credential) yields ONE verdict — so if two
    /// seats naming the identical pair ever disagree, the cache was not shared
    /// and the instrument is broken.
    /// </summary>
    public static async Task<List<ProbeVerdict>> ProbeAsync(
        ProbeSession session, IReadOnlyList<EndpointTarget> targets,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var verdicts = new List<ProbeVerdict>();

        foreach (var t in targets)
        {
            if (!session.Cache.TryGetValue(t.CatalogueKey, out var cat))
            {
                cat = await FetchCatalogueAsync(session.Http, t, timeout, ct);
                session.Cache[t.CatalogueKey] = cat;
                session.Fetches++;
            }

            verdicts.Add(Judge(t, cat));
        }

        return verdicts;
    }

    /// <summary>
    /// One-shot convenience for probing a single profile in isolation.
    ///
    /// ⛔ Do NOT call this in a loop — that reintroduces exactly the
    /// per-profile client-and-cache defect <see cref="ProbeSession"/> exists
    /// to prevent. Sweeping several profiles means one session held open
    /// around the whole sweep.
    /// </summary>
    public static async Task<List<ProbeVerdict>> ProbeOnceAsync(
        IReadOnlyList<EndpointTarget> targets, TimeSpan timeout, CancellationToken ct = default)
    {
        using var session = new ProbeSession();
        return await ProbeAsync(session, targets, timeout, ct);
    }

    private static ProbeVerdict Judge(EndpointTarget t, CatalogueResult cat)
    {
        if (cat.Failure is { } why)
            return new ProbeVerdict(t.Path, ProbeLevel.Error, $"{t.Endpoint} — {why}");

        if (!string.IsNullOrWhiteSpace(t.Model) && !cat.ModelIds.Contains(t.Model))
        {
            var available = cat.ModelIds.Count == 0
                ? "server listed no models"
                : $"server serves: {string.Join(", ", cat.ModelIds.Take(6))}";
            return new ProbeVerdict(t.Path, ProbeLevel.Error,
                $"{t.Endpoint} reachable, but model '{t.Model}' is NOT served ({available})");
        }

        var served = cat.Contexts.TryGetValue(t.Model, out var n) ? n : (int?)null;

        // A compaction trigger at or above the served context can never fire
        // before the model's own wall does. The setting is not merely unused —
        // it silently disables compaction for that seat.
        if (served is int len && len > 0 && t.ThresholdTokens >= len)
        {
            return new ProbeVerdict(t.Path, ProbeLevel.Warn,
                $"{t.Model} @ {t.Endpoint} serves {len} tokens, but compaction.threshold_tokens "
                + $"is {t.ThresholdTokens} — this trigger can NEVER fire; compaction is off for this seat");
        }

        var ctxNote = served is int ok ? $", ctx {ok}" : "";
        return new ProbeVerdict(t.Path, ProbeLevel.Ok, $"{t.Model} @ {t.Endpoint}{ctxNote}");
    }


    /// <summary>
    /// One host's catalogue. <paramref name="Contexts"/> holds the served
    /// context length for every model that publishes one — absent entries
    /// mean the server did not say, which is NOT the same as zero and must
    /// never produce a threshold verdict.
    /// </summary>
    internal sealed record CatalogueResult(
        HashSet<string> ModelIds, Dictionary<string, int> Contexts, string? Failure)
    {
        public static CatalogueResult Failed(string why) =>
            new(new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal), why);
    }

    /// <summary>
    /// The served context window for ONE (endpoint, model), or a failure
    /// string. This is the capacity manager's read of a provider: it must know
    /// how big the window actually is before it can hand out a lease against
    /// it, and a CATALOGUE VALUE IS A CLAIM, NOT A MEASUREMENT -- so the answer
    /// comes from the host, not from capabilities/default.yaml.
    ///
    /// Returns (null, why) when the host did not answer, and (null, null) when
    /// it answered but published no window for that model. Those two are
    /// DELIBERATELY DISTINGUISHABLE: "could not measure" is not "measured
    /// zero", and a drift check that treats silence as a changed value is how a
    /// catalogue gets edited to match a network blip.
    ///
    /// GET /models only -- no completions, so this costs no tokens.
    /// </summary>
    public static async Task<(int? Served, string? Failure)> ServedWindowAsync(
        string endpoint, string model, string? apiKeyEnv, TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var target = new EndpointTarget("capacity", endpoint, model, apiKeyEnv ?? "", 0);
        var cat = await FetchCatalogueAsync(http, target, timeout, ct);

        if (cat.Failure is not null) return (null, cat.Failure);
        return cat.Contexts.TryGetValue(model, out var len) ? (len, null) : (null, null);
    }

    private static async Task<CatalogueResult> FetchCatalogueAsync(
        HttpClient http, EndpointTarget t, TimeSpan timeout, CancellationToken ct)
    {
        string? key = null;
        if (!string.IsNullOrWhiteSpace(t.ApiKeyEnv))
        {
            key = Environment.GetEnvironmentVariable(t.ApiKeyEnv);
            if (string.IsNullOrWhiteSpace(key))
                return CatalogueResult.Failed(
                    $"api_key_env '{t.ApiKeyEnv}' is not set in this environment — this seat cannot authenticate");
        }

        var url = t.Endpoint.TrimEnd('/') + "/models";

        // ⛔ A TIMEOUT IS A STATEMENT ABOUT MY DEADLINE, NOT ABOUT THE HOST.
        // Every other failure here is a durable fact the host itself reported
        // — a refused connection, an HTTP 401, unparseable JSON — and caching
        // one is sound. A deadline elapsing is different in kind: it is the
        // one failure whose cause may be entirely on this side, and under a
        // SHARED cache a single transient miss is amplified into a red line on
        // every profile naming that host.
        //
        // Measured 2026-08-25, openrouter.ai/api/v1/models: cold 11.76s, then
        // 0.38s and 0.38s warm — a ~688KB catalogue behind a fresh TLS
        // handshake. One retry rides the connection the failed attempt just
        // opened, so it costs little and removes the cold-start false death. A
        // genuinely unreachable host still settles after two attempts and is
        // then cached like any other durable failure.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (key is not null)
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

                // Per-request deadline, since the client is shared across probes
                // and its own Timeout would apply to the whole sweep.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(timeout);

                using var resp = await http.SendAsync(req, deadline.Token);
                if (!resp.IsSuccessStatusCode)
                    return CatalogueResult.Failed($"GET /models returned HTTP {(int)resp.StatusCode}");

                var body = await resp.Content.ReadAsStringAsync(deadline.Token);
                return ParseCatalogue(body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own deadline elapsed. A caller-driven cancel is a
                // different event and must propagate, not be reported as a
                // dead host.
                if (attempt == 2)
                {
                    return CatalogueResult.Failed(
                        $"no response within {timeout.TotalSeconds:0}s on either of 2 attempts "
                        + "(host down or unreachable?)");
                }
            }
            catch (HttpRequestException e)
            {
                // The host answered — with a refusal. That is durable; do not
                // spend a second attempt on it.
                return CatalogueResult.Failed($"unreachable ({e.Message})");
            }
        }

        // Unreachable: the loop either returns or exhausts both attempts above.
        return CatalogueResult.Failed("probe did not resolve");
    }

    /// <summary>
    /// Pull every model id out of an OpenAI-shaped <c>/models</c> response,
    /// with each model's served context where the server publishes one. vLLM
    /// reports <c>max_model_len</c>; OpenRouter reports <c>context_length</c>;
    /// neither is guaranteed, and a missing value is simply absent rather than
    /// guessed — an unknown context must not manufacture a threshold verdict.
    /// </summary>
    // internal, not private: the served-window join below is the thing that
    // decides whether a grant fits, and it earned direct tests the moment it
    // shipped a crash and an over-promise on the same afternoon.
    internal static CatalogueResult ParseCatalogue(string body)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var contexts = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return CatalogueResult.Failed("GET /models returned no 'data' array");

            foreach (var m in data.EnumerateArray())
            {
                if (!m.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } id) continue;
                ids.Add(id);

                // ⛔ TWO DIFFERENT NUMBERS, AND THE OPTIMISTIC ONE IS LISTED FIRST.
                // OpenRouter publishes `context_length` (the MODEL's advertised
                // ceiling) AND `top_provider.context_length` (what the provider that
                // will actually serve the request offers). MEASURED 2026-08-28 on
                // deepseek/deepseek-v4-flash and -pro: 1048576 vs 1024000 -- a
                // 24576-token gap, and the bigger number is the one listed first.
                //
                // Reading only the first writes that ceiling into the catalogue, and
                // the capacity invariant then HONOURS it: a seat is granted 1048576
                // tokens against an endpoint that serves 1024000. So take the
                // SMALLEST candidate present, not the first one found. An
                // under-promise costs a seat context it might have had; an
                // over-promise is a grant the endpoint refuses at request time --
                // exactly the failure this layer exists to prevent.
                //
                // Found by checking this probe against a raw curl of the same URL,
                // not by a failing run. Verify the tool, not just the artifact.
                int? best = null;
                void Consider(int v) { if (v > 0 && (best is null || v < best)) best = v; }

                // ⛔ TryGetInt32 THROWS on a JSON null -- it returns false only for
                // a number that will not fit. MEASURED: of OpenRouter's 398 listed
                // models, some carry `top_provider: { context_length: null }`, which
                // crashed `vett capacity probe` outright the first time this ran.
                // Every read here is therefore gated on ValueKind first.
                void ConsiderProp(JsonElement parent, string name)
                {
                    if (parent.TryGetProperty(name, out var el)
                        && el.ValueKind == JsonValueKind.Number
                        && el.TryGetInt32(out var v))
                        Consider(v);
                }

                ConsiderProp(m, "max_model_len");
                ConsiderProp(m, "context_length");
                if (m.TryGetProperty("top_provider", out var tp) && tp.ValueKind == JsonValueKind.Object)
                    ConsiderProp(tp, "context_length");

                if (best is { } chosen) contexts[id] = chosen;
            }
        }
        catch (JsonException e)
        {
            return CatalogueResult.Failed($"GET /models returned unparseable JSON ({e.Message})");
        }

        return new CatalogueResult(ids, contexts, null);
    }

    /// <summary>
    /// Seats that can generate without a ceiling.
    ///
    /// ⛔ WHY THIS IS A REAL DEFECT AND NOT A STYLE NOTE. Every seat has a
    /// <c>request_timeout_seconds</c>, which caps how long ONE attempt may
    /// take. Nothing caps how much that attempt tries to produce. With
    /// <c>max_output_tokens</c> unset the request body carries no limit, so the
    /// length of the response — and therefore its duration — is decided by the
    /// model, and whether the call can finish inside its own timeout becomes a
    /// property of the prompt rather than of the configuration. When it does
    /// not finish, the attempt is cancelled and RETRIED, and each retry is a
    /// fresh unbounded generation: the entire retry budget can be spent on a
    /// request that was never going to fit.
    ///
    /// The argument above is structural and needs no experiment. The
    /// experiment agrees anyway: in the EpicForge ladder, four consecutive runs
    /// of the same complex epic with no output bound failed to finish (one hit
    /// a 4,200s hard cap having produced 69,577 output tokens across 134
    /// iterations), and the first run carrying <c>max_output_tokens: 4096</c>
    /// finished in 788s with every one of its 39 requests bounded at the wire.
    /// ⚠ That pair is NOT a controlled comparison — the roster changed too —
    /// so it is corroboration, not the basis.
    ///
    /// ⚠ WARNING, NOT ERROR, and the reason is not timidity. An unbounded seat
    /// is legal and is often what a short single-shot profile wants; the danger
    /// is the COMBINATION with long agentic turns, which the YAML cannot see.
    /// The verdict carries the timeout and retry count so the reader can size
    /// the exposure instead of being told a number this file cannot know.
    ///
    /// ⚠ EFFECTIVE CONFIG, NOT THE SEAT'S OWN BLOCK. <c>max_output_tokens</c>
    /// inherits through <c>ChatClientFactory.Merge</c>, so a member that
    /// authors nothing but sits under a bounded parent IS bounded. Reading each
    /// seat's own block would report every member of a correctly-bounded
    /// profile — the exact false-positive flood that gets a warning ignored.
    /// It shares <see cref="WalkTeam"/> with the other gates so it cannot span
    /// a different population.
    /// </summary>
    public static List<UnboundedSeat> UnboundedOutputSeatsOf(Profile profile)
    {
        var seats = new List<UnboundedSeat>();
        Bound(profile.Llm, "llm", profile.Llm, seats);
        WalkTeam(profile.Team, "team", profile.Llm, profile.Compaction,
            (llm, _, path, inhLlm, _) => Bound(llm, path, inhLlm, seats));
        return seats;
    }

    private static void Bound(LlmConfig? llm, string path, LlmConfig inherited, List<UnboundedSeat> into)
    {
        var effective = Llm.ChatClientFactory.Merge(inherited, llm);
        if (effective.MaxOutputTokens is not null) return;
        into.Add(new UnboundedSeat(
            path,
            effective.RequestTimeoutSeconds ?? Llm.ChatClientFactory.DefaultRequestTimeoutSeconds,
            effective.NumRetries ?? Llm.ChatClientFactory.DefaultNumRetries));
    }

    /// <summary>
    /// Seats whose retry budget outlives the whole run.
    ///
    /// ⛔ A JOINT CLAIM ACROSS TWO KEYS THAT ARE EACH INDIVIDUALLY SENSIBLE.
    /// <c>timeout_minutes</c> caps the entire run. <c>request_timeout_seconds</c>
    /// x (<c>num_retries</c> + 1) is how long ONE call is allowed to keep
    /// trying. When the second exceeds the first, the run is killed part-way
    /// through a retry stack it was configured to finish — and the symptom is
    /// a run that simply stops, with no error, which reads as a hang rather
    /// than as a configuration contradiction.
    ///
    /// ⚠ THE BUDGET IS NOT RESTATED HERE. It comes from
    /// <c>ChatClientFactory.EffectiveRetryBudgetSeconds</c>, the same function
    /// the member watchdog is sized from. That function was WRONG until
    /// 2026-09-01 — <c>num_retries</c> never reached the wire, so the real
    /// stack was four attempts regardless of the setting — which is precisely
    /// why this check calls it rather than recomputing the multiplication: had
    /// this file carried its own copy, fixing the binding would have left this
    /// gate quietly reading the old arithmetic.
    ///
    /// ⚠ WARNING, NOT ERROR. A profile may legitimately intend "cap the run
    /// hard and accept that a stubborn call gets cut off", and the run cap is
    /// also routinely overridden at the command line, so the YAML is not the
    /// last word.
    /// </summary>
    public static List<RunCapGap> RunCapShorterThanRetryStackOf(Profile profile)
    {
        var gaps = new List<RunCapGap>();
        var cap = profile.TimeoutMinutes * 60;
        Cap(profile.Llm, "llm", profile.Llm, cap, gaps);
        WalkTeam(profile.Team, "team", profile.Llm, profile.Compaction,
            (llm, _, path, inhLlm, _) => Cap(llm, path, inhLlm, cap, gaps));
        return gaps;
    }

    private static void Cap(LlmConfig? llm, string path, LlmConfig inherited, int capSeconds, List<RunCapGap> into)
    {
        var effective = Llm.ChatClientFactory.Merge(inherited, llm);
        var budget = Llm.ChatClientFactory.EffectiveRetryBudgetSeconds(effective);
        if (capSeconds <= 0 || budget <= capSeconds) return;
        into.Add(new RunCapGap(path, capSeconds, budget));
    }
}
