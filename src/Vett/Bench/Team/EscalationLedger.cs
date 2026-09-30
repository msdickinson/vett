using System.Text.Json;
using System.Text.Json.Serialization;
using Vett.Agent;
using Vett.Config;

namespace Vett.Bench.Team;

/// <summary>
/// Which model tier a request actually went to. Derived by the HARNESS
/// from the <c>model</c> field the agent loop stamps on every
/// <c>llm_request</c> event — never from anything the model says about
/// itself, and never by grepping prose.
/// </summary>
public enum ModelTier
{
    /// <summary>Went to the run's base model — the cheap local tier.</summary>
    Base,
    /// <summary>Went to a model that is not the run's base model — the escalation tier.</summary>
    Escalation,
}

/// <summary>
/// Whether the ledger's counts are READABLE, and if not, why not.
///
/// This enum exists so that "zero escalations happened" and "we did not
/// / could not measure escalations" can never collapse into the same
/// output. A bare int cannot express the difference; a bare int is
/// exactly how <c>|measured| = 0</c> gets laundered into
/// <c>measured = 0</c>.
/// </summary>
public enum EscalationLedgerStatus
{
    /// <summary>
    /// The ledger was armed but observed ZERO events of ANY kind. Nothing
    /// reached it. This is not a run with no escalations — it is a run the
    /// ledger never saw. Count withheld.
    /// </summary>
    NoEventsObserved,

    /// <summary>
    /// Events arrived but not one <c>llm_request</c> and not one
    /// <c>iteration_start</c>. The agent loop never ran, or the events
    /// that carry model identity were filtered out upstream. Either way
    /// this is a COULD-NOT-MEASURE, not a measured zero. Count withheld.
    ///
    /// The positive conjunct that makes a later zero meaningful is
    /// <see cref="EscalationLedgerSnapshot.LlmRequestsTotal"/> &gt; 0: a
    /// healthy instrumented run that did real work always has requests.
    /// </summary>
    NoRequestsObserved,

    /// <summary>
    /// The stream is INTERNALLY INCONSISTENT: at least one thread reported
    /// more <c>iteration_start</c> events than <c>llm_request</c> events,
    /// by more than the one in-flight iteration a cancelled run may
    /// legitimately leave behind. The agent loop cannot skip a request
    /// between those two emits (see <see cref="EscalationLedger"/> remarks),
    /// so a gap means requests were dropped before they reached this
    /// ledger. Publishing a count derived from a stream that is known to
    /// be missing entries would rebuild the exact defect this class
    /// exists to prevent. Count withheld.
    /// </summary>
    StreamOmittedRequests,

    /// <summary>
    /// The tier map cannot be applied because model NAMES do not separate
    /// the tiers: either the run's base model name is empty, or the
    /// profile declares two different (provider, endpoint, model)
    /// identities that share a model name. <c>llm_request</c> carries only
    /// the model name, so in that configuration the stream is physically
    /// incapable of telling the tiers apart. Count withheld rather than
    /// guessed.
    /// </summary>
    ModelIdentityAmbiguous,

    /// <summary>
    /// At least one <c>llm_request</c> was observed, per-thread request
    /// and iteration counts agree, and model names separate the tiers.
    /// Counts are readable.
    /// </summary>
    Live,
}

/// <summary>
/// The (provider, endpoint, model) triple that identifies WHICH model a
/// request went to. All three matter for CONFIG: the escalating profile
/// keeps <c>provider: local</c> on both tiers and swaps endpoint+model,
/// so model alone is not a safe key across profiles that reuse a model
/// name behind two endpoints. The event stream only carries the model
/// NAME, which is why <see cref="EscalationLedgerStatus.ModelIdentityAmbiguous"/>
/// exists — it is the guard for exactly that case.
/// </summary>
public sealed record ModelIdentity(string Provider, string Endpoint, string Model)
{
    public static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public bool SameAs(ModelIdentity other) =>
        Cmp.Equals(Provider, other.Provider)
        && Cmp.Equals(Endpoint, other.Endpoint)
        && Cmp.Equals(Model, other.Model);

    public override string ToString() =>
        string.IsNullOrEmpty(Endpoint) ? $"{Provider}/{Model}" : $"{Provider}/{Endpoint}#{Model}";
}

/// <summary>One roster entry: a declared agent (leader or member) and the
/// model identity the PROFILE says it will run on, plus any middleware it
/// is configured with that calls an LLM WITHOUT emitting an event. The
/// roster is computed once, up front, from config — this is the
/// pre-registration of the escalation domain and of the ledger's own
/// blind spots.</summary>
public sealed record DeclaredMemberTier(
    string Member,
    ModelIdentity Identity,
    ModelTier Tier,
    bool IsLeader,
    IReadOnlyList<string> UninstrumentedMiddleware);

/// <summary>
/// HARNESS-OWNED ESCALATION COUNTER.
///
/// WHY THIS EXISTS
/// ---------------
/// <c>profiles/ds-team-flash-escalate.yaml</c> is a two-tier team: a cheap
/// local base model plus one paid escalation member. Its own header says
/// the harness-owned counter that would make ladder compliance provable
/// "does not exist yet ... until it exists, treat ladder compliance as
/// unproven and audit run.jsonl." Auditing prose is not measuring. With
/// no counter, Flash+Pro is <c>|measured| = 0</c>.
///
/// HOOK POINT: THE EVENT STREAM
/// ----------------------------
/// This ledger is a REDUCER over the run's own structured event stream.
/// It edits no agent-loop code and no coordinator code. The increment
/// comes from <c>llm_request</c>, which <c>AgentLoop</c> emits itself
/// immediately before issuing the call and which already carries
/// <c>["model"] = llm.Model</c> — the same string that goes into
/// <c>ChatOptions.ModelId</c>. So the count cannot drift from what the
/// run actually logged, and it is per-REQUEST, not per-dispatch.
///
/// The same reducer runs on a live <c>OnEvent</c> sink and on a finished
/// <c>_bench-session.jsonl</c> (whose path <c>--json</c> already
/// publishes as <c>session_log_path</c>), so an escalation count can be
/// recovered from runs that are already on disk.
///
/// THE CAVEAT, MEASURED RATHER THAN ASSUMED
/// ----------------------------------------
/// A stream-derived counter is only as complete as the stream. That is
/// not taken on faith here. The stream's completeness was audited
/// against the source and the answer is: it is complete for agent-loop
/// requests, and it is KNOWN-INCOMPLETE at three enumerated sites.
///
///  (1) AGENT-LOOP REQUESTS — COMPLETE, WITH A DETECTOR.
///      <c>AgentLoop</c> emits <c>iteration_start</c> at the top of each
///      iteration and <c>llm_request</c> further down, through the same
///      <c>Emit</c> sink, with no <c>continue</c>/<c>return</c>/
///      <c>throw</c> between them. Therefore, per thread,
///      <c>count(llm_request) == count(iteration_start)</c> exactly —
///      except for at most ONE trailing iteration when a run is
///      cancelled mid-flight. The ledger checks this on every thread. A
///      gap larger than one means requests went missing before they
///      reached the ledger, and the ledger then REFUSES to publish a
///      count (<see cref="EscalationLedgerStatus.StreamOmittedRequests"/>)
///      instead of reporting a silently-low number.
///      LIMIT OF THE DETECTOR: a dropper that removes exactly one request
///      per thread is indistinguishable from a normal cancellation. The
///      raw numbers are published (<c>iteration_starts_total</c>,
///      <c>llm_requests_total</c>, <c>unmatched_iterations</c>) so a
///      reader can apply a stricter rule than the ledger does.
///
///  (2) HTTP RETRIES — COUNTED SEPARATELY, NOT FOLDED IN.
///      One <c>llm_request</c> event can become up to three HTTP attempts:
///      the retry loop in <c>AgentLoop</c> re-issues the call without
///      re-emitting the event. Each failed attempt does emit
///      <c>llm_error</c>, so those extra attempts are visible; they are
///      reported as <c>failed_attempts</c>, never merged into the request
///      count.
///
///  (3) LLM-CALLING MIDDLEWARE — UNINSTRUMENTED, DECLARED FROM CONFIG.
///      <c>llm_summarizing_condenser</c> and <c>replan_checkpoint</c>
///      (CompactionMiddleware) and <c>agent_finished_critic</c>
///      (CriticMiddleware) each call <c>GetResponseAsync</c> and emit NO
///      events at all. They use their host agent's model, so on a paid
///      escalation member they bill the escalation model invisibly. The
///      ledger cannot see those calls — so it reads the profile, works
///      out which agents have them enabled, and DECLARES them as
///      <c>uninstrumented_call_sites</c>. While that list is non-empty the
///      ledger reports <c>request_accounting_complete = false</c>, i.e.
///      its per-model numbers are an explicit LOWER BOUND. An
///      unmeasurable hole is thereby converted into a declared one rather
///      than a silent one.
///
/// ZERO vs UNINSTRUMENTED
/// ----------------------
/// Deliberately kept as FIVE distinguishable outputs, not one integer:
///
///   * no snapshot at all (null on the result / JSON)  → NOT INSTRUMENTED.
///     Nobody armed a ledger for this run. Says nothing about escalation.
///   * status = no_events_observed                     → NOT MEASURED.
///     Armed, but nothing was ever fed to it.
///   * status = no_requests_observed                   → COULD NOT MEASURE.
///     Fed events, but none that carry model identity.
///   * status = live, escalation_tier_configured=false → STRUCTURAL ZERO.
///     Armed, saw traffic, and the profile declares no second tier — zero
///     escalations was the only possible outcome.
///   * status = live, escalation_tier_configured=true,
///     escalation_requests = 0                         → MEASURED ZERO.
///     Escalation was available, the run had LLM traffic, and it never
///     escalated. This is the only one that is a finding.
///
/// plus two refusals — <c>stream_omitted_requests</c> and
/// <c>model_identity_ambiguous</c> — which are integrity failures, not
/// zeros.
///
/// <see cref="EscalationLedgerSnapshot.TryGetEscalationCount"/> is the
/// gate: it returns false in every case except <c>live</c>, so a scorer
/// cannot accidentally read a withheld count as a zero.
/// </summary>
public sealed class EscalationLedger
{
    /// <summary>Event type this ledger publishes itself under.</summary>
    public const string EventType = "escalation_ledger";

    /// <summary>Thread key used for events that carry no <c>thread_id</c>
    /// — i.e. the leader's own loop. <c>TeamCoordinator.TaggedEmit</c>
    /// stamps <c>thread_id</c> on member events only.</summary>
    public const string LeaderThread = "main";

    /// <summary>
    /// Middleware that issues its own <c>GetResponseAsync</c> call and
    /// emits NO event, so its usage is invisible to any stream-derived
    /// counter. Verified against MiddlewareResolver's LLM-backed
    /// registrations. <c>milestone_checkpoint</c> is deliberately absent:
    /// it receives only the model NAME, not a client, so it cannot call
    /// the LLM.
    /// </summary>
    public static readonly IReadOnlyList<string> UninstrumentedLlmMiddleware =
        ["llm_summarizing_condenser", "replan_checkpoint", "agent_finished_critic"];

    private readonly object _lock = new();
    private readonly Dictionary<string, DeclaredMemberTier> _roster;

    // Per-model tallies, keyed by the model NAME the stream reported.
    private readonly Dictionary<string, ModelStat> _stats = new(StringComparer.OrdinalIgnoreCase);
    // (thread, iteration) -> the request that key opened. Used to pair
    // llm_response tokens back to the model that was actually asked.
    private readonly Dictionary<(string Thread, int Iteration), PendingRequest> _pending = new();
    private readonly Dictionary<string, int> _iterationStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _requestsByThread = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastModelByThread = new(StringComparer.Ordinal);

    private int _eventsObserved;
    private int _orphanResponses;
    private int _collidingRequestKeys;
    private int _malformedLogLines;
    private readonly List<string> _notes = new();

    /// <summary>The run's base (provider, endpoint, model). Any request to
    /// a model that is not <c>BaseIdentity.Model</c> is, by definition,
    /// the escalation tier.</summary>
    public ModelIdentity BaseIdentity { get; }

    private EscalationLedger(ModelIdentity baseIdentity, IEnumerable<DeclaredMemberTier> roster)
    {
        BaseIdentity = baseIdentity;
        _roster = roster.ToDictionary(r => r.Member, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build the ledger from the profile plus the run's EFFECTIVE base
    /// endpoint/model. Those two are passed in rather than read off
    /// <c>profile.Llm</c> because <c>team-bench</c> accepts
    /// <c>--endpoint</c> / <c>--model</c> overrides, and the base tier is
    /// whatever the run actually used — <c>Harness.RunInstanceAsync</c>
    /// hands the same pair to <c>ChatClientFactory.Create</c>.
    ///
    /// A profile with no <c>team:</c> block yields a roster holding just
    /// the base tier; the resulting ledger reports
    /// <c>escalation_tier_configured = false</c>, which is what makes a
    /// later zero a STRUCTURAL zero rather than a finding.
    /// </summary>
    public static EscalationLedger FromProfile(Profile profile, string baseEndpoint, string baseModel)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var baseIdentity = new ModelIdentity(profile.Llm.Provider, baseEndpoint ?? "", baseModel ?? "");

        var roster = new List<DeclaredMemberTier>();
        var team = profile.Team;
        if (team is not null)
        {
            if (!string.IsNullOrEmpty(team.Leader.Name))
            {
                // ⚠ MIRRORS THE COORDINATOR BY CALLING IT, NOT BY RESTATING IT.
                // This used to hardcode `profile.Middleware` with a comment
                // explaining that the leader ignores its own block — which was
                // true, and was itself the defect (Coordinator.LeaderMiddleware,
                // 2026-08-26). A duplicated resolution rule drifts silently the
                // moment one copy is fixed, and a ledger reading the wrong list
                // declares the wrong blind spots — worse than declaring none.
                // Call the single source of truth so the two cannot diverge.
                roster.Add(Declare(team.Leader, Vett.Agent.TeamCoordinator.LeaderMiddleware(team, profile),
                    baseIdentity, isLeader: true));
            }

            foreach (var m in team.Members)
            {
                if (string.IsNullOrEmpty(m.Name)) continue;
                roster.Add(Declare(m, m.Middleware, baseIdentity, isLeader: false));
            }
        }

        return new EscalationLedger(baseIdentity, roster);
    }

    /// <summary>
    /// Minimal ledger for callers that have no profile in hand: the tier
    /// rule needs only the base model name. The roster is empty, so
    /// <c>escalation_tier_configured</c> is reported as false and any
    /// escalation actually seen in the stream lands in
    /// <c>undeclared_escalation_models</c> — which is the honest result:
    /// escalation happened and nothing had declared it in advance.
    /// </summary>
    public static EscalationLedger ForBaseModel(string baseModel) =>
        new(new ModelIdentity("", "", baseModel ?? ""), []);

    /// <summary>
    /// Resolve one agent's declared identity and blind spots. Mirrors
    /// <c>ChatClientFactory.Merge</c> field-for-field on the fields that
    /// identify a client: an empty string or an unset provider means
    /// INHERIT, never "override with empty". Getting this wrong in either
    /// direction is the whole ballgame — inheriting too eagerly hides an
    /// escalation, overriding too eagerly invents one.
    /// </summary>
    private static DeclaredMemberTier Declare(
        MemberConfig m, List<string> middlewareNames, ModelIdentity baseIdentity, bool isLeader)
    {
        var llm = m.Llm;
        var provider = llm is not null && llm.HasExplicitProvider ? llm.Provider : baseIdentity.Provider;
        var endpoint = llm is not null && !string.IsNullOrEmpty(llm.Endpoint) ? llm.Endpoint : baseIdentity.Endpoint;
        var model = llm is not null && !string.IsNullOrEmpty(llm.Model) ? llm.Model : baseIdentity.Model;

        var identity = new ModelIdentity(provider, endpoint, model);
        var tier = identity.SameAs(baseIdentity) ? ModelTier.Base : ModelTier.Escalation;

        // An EMPTY middleware list falls back to Builtins.DefaultMiddleware()
        // — submit-detector, output-truncation, stuck-detector — none of
        // which call an LLM. So an unset list is a genuine zero here, not
        // an unknown.
        var blind = (middlewareNames ?? [])
            .Where(n => UninstrumentedLlmMiddleware.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return new DeclaredMemberTier(m.Name, identity, tier, isLeader, blind);
    }

    /// <summary>The escalation domain, declared IN ADVANCE from config —
    /// before any run happens. Pre-registering it is what stops the tier
    /// set from being chosen after the numbers are in.</summary>
    public IReadOnlyList<DeclaredMemberTier> Roster =>
        _roster.Values.OrderBy(r => r.Member, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Feed the ledger one event. Only four types matter; every other type
    /// is ignored (but still counted as "an event arrived", which is what
    /// separates <c>no_events_observed</c> from <c>no_requests_observed</c>).
    /// Thread-safe: dispatched members emit from background tasks.
    /// </summary>
    public void Observe(Event ev)
    {
        if (ev is null) return;

        lock (_lock)
        {
            _eventsObserved++;
            var thread = Str(ev.Data, "thread_id") ?? LeaderThread;

            switch (ev.Type)
            {
                case "iteration_start":
                    _iterationStarts[thread] = _iterationStarts.GetValueOrDefault(thread) + 1;
                    break;

                case "llm_request":
                    RecordRequest(thread, Num(ev.Data, "iteration"), Str(ev.Data, "model"));
                    break;

                case "llm_response":
                    RecordResponse(thread, Num(ev.Data, "iteration"),
                        Num(ev.Data, "input_tokens"), Num(ev.Data, "output_tokens"));
                    break;

                case "llm_error":
                    // One extra HTTP attempt that no llm_request event
                    // represents. It carries neither model nor iteration,
                    // so it attributes to the last model this thread asked.
                    var model = _lastModelByThread.GetValueOrDefault(thread);
                    if (model is null)
                    {
                        Note($"llm_error on thread '{thread}' before any llm_request — attempt not attributable to a model");
                    }
                    else
                    {
                        Stat(model).FailedAttempts++;
                    }
                    break;
            }
        }
    }

    /// <summary>Feed a whole sequence — a replayed session log, or a
    /// buffered stream.</summary>
    public void ObserveAll(IEnumerable<Event> events)
    {
        if (events is null) return;
        foreach (var e in events) Observe(e);
    }

    private void RecordRequest(string thread, long? iteration, string? model)
    {
        // A request with no model field cannot be attributed to a tier.
        // Count it, name it, and let it poison request_accounting_complete
        // rather than silently dropping it out of the denominator.
        var key = model;
        if (string.IsNullOrEmpty(key))
        {
            key = "(unknown)";
            Note($"llm_request on thread '{thread}' carried no model field");
        }

        _requestsByThread[thread] = _requestsByThread.GetValueOrDefault(thread) + 1;
        _lastModelByThread[thread] = key;
        Stat(key).Requests++;

        var iter = (int)(iteration ?? -1);
        var pk = (thread, iter);
        if (_pending.TryGetValue(pk, out var existing))
        {
            // Two requests on the same (thread, iteration). This happens
            // when a nested sub-team's members are all tagged with the
            // PARENT dispatch's thread_id and interleave. It does not
            // disturb the REQUEST count; it does make token pairing
            // ambiguous, so say so instead of quietly mis-attributing.
            _collidingRequestKeys++;
            if (!ModelIdentity.Cmp.Equals(existing.Model, key))
                Note($"thread '{thread}' iteration {iter} issued requests to both '{existing.Model}' and '{key}' — token pairing on that key is ambiguous");
            existing.Outstanding++;
        }
        else
        {
            _pending[pk] = new PendingRequest(key);
        }
    }

    private void RecordResponse(string thread, long? iteration, long? input, long? output)
    {
        var iter = (int)(iteration ?? -1);
        if (!_pending.TryGetValue((thread, iter), out var pending))
        {
            // A response with no matching request means the request event
            // is MISSING from the stream — the precise failure mode a
            // stream-derived counter must never paper over.
            _orphanResponses++;
            Note($"llm_response on thread '{thread}' iteration {iter} has no matching llm_request");
            return;
        }

        pending.Responded++;
        if (pending.Responded >= pending.Outstanding) _pending.Remove((thread, iter));

        var s = Stat(pending.Model);
        if (input is null && output is null)
        {
            // No usage numbers at all. UNKNOWN is not zero: adding a
            // synthesized 0 would turn a could-not-measure into a
            // measured zero inside the token totals.
            s.ResponsesWithoutTokens++;
            return;
        }

        s.InputTokens += input ?? 0;
        s.OutputTokens += output ?? 0;
        s.ResponsesWithTokens++;
        if (input is null || output is null)
        {
            s.ResponsesWithPartialTokens++;
            Note($"llm_response on thread '{thread}' iteration {iter} reported only one of input/output tokens");
        }
    }

    private ModelStat Stat(string model)
    {
        if (!_stats.TryGetValue(model, out var s))
        {
            s = new ModelStat(model);
            _stats[model] = s;
        }
        return s;
    }

    private void Note(string msg)
    {
        // Bounded: a pathological stream must not turn the snapshot into
        // an unbounded string. The count is still exact.
        if (_notes.Count < 50) _notes.Add(msg);
    }

    /// <summary>
    /// Replay a finished run's <c>_bench-session.jsonl</c>. The path is
    /// already published on the <c>--json</c> summary as
    /// <c>session_log_path</c>, so escalation counts can be recovered
    /// from runs that finished before this ledger existed.
    ///
    /// Malformed lines are COUNTED, not skipped silently: a truncated log
    /// is itself a way for requests to go missing, and
    /// <c>malformed_log_lines</c> is published so the reader sees the
    /// discard count.
    /// </summary>
    public void ObserveSessionLog(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Event? ev = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "" : "";
                var data = new Dictionary<string, object?>();
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in d.EnumerateObject()) data[p.Name] = p.Value.Clone();
                }
                if (type.Length == 0) throw new JsonException("no type");
                ev = new Event(type, data);
            }
            catch (JsonException)
            {
                lock (_lock) { _malformedLogLines++; }
            }
            if (ev is not null) Observe(ev);
        }
    }

    /// <summary>Convenience: build from profile and immediately replay a
    /// session log.</summary>
    public static EscalationLedger FromSessionLog(
        Profile profile, string baseEndpoint, string baseModel, string sessionLogPath)
    {
        var l = FromProfile(profile, baseEndpoint, baseModel);
        l.ObserveSessionLog(sessionLogPath);
        return l;
    }

    /// <summary>Freeze the current counts into a publishable, JSON-shaped
    /// snapshot. Safe to call at any time.</summary>
    public EscalationLedgerSnapshot Snapshot()
    {
        lock (_lock)
        {
            var declaredModels = _roster.Values
                .Select(r => r.Identity.Model)
                .Where(m => !string.IsNullOrEmpty(m))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var escalationMembers = _roster.Values
                .Where(r => r.Tier == ModelTier.Escalation && !r.IsLeader)
                .Select(r => r.Member)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            var blindSpots = _roster.Values
                .SelectMany(r => r.UninstrumentedMiddleware.Select(mw => $"{r.Member}:{mw} ({r.Identity.Model})"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            // --- integrity: does the stream account for every iteration? ---
            var threads = _iterationStarts.Keys.Union(_requestsByThread.Keys, StringComparer.Ordinal).ToList();
            int unmatched = 0, droppedThreads = 0;
            foreach (var t in threads)
            {
                var delta = _iterationStarts.GetValueOrDefault(t) - _requestsByThread.GetValueOrDefault(t);
                if (delta <= 0) continue;
                unmatched += delta;
                // delta == 1 is the legitimate case: the run was cancelled
                // between iteration_start and llm_request. Anything more
                // means requests are missing.
                if (delta > 1) droppedThreads++;
            }

            // --- integrity: do model NAMES separate the declared tiers? ---
            var ambiguous = _roster.Values
                .GroupBy(r => r.Identity.Model, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(r => r.Identity).Distinct().Count() > 1)
                .Select(g => g.Key)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            var requestsTotal = _stats.Values.Sum(s => s.Requests);
            var iterationStartsTotal = _iterationStarts.Values.Sum();

            var status =
                _eventsObserved == 0 ? EscalationLedgerStatus.NoEventsObserved
                : string.IsNullOrEmpty(BaseIdentity.Model) || ambiguous.Count > 0
                    ? EscalationLedgerStatus.ModelIdentityAmbiguous
                : droppedThreads > 0 || _orphanResponses > 0
                    ? EscalationLedgerStatus.StreamOmittedRequests
                : requestsTotal == 0
                    ? EscalationLedgerStatus.NoRequestsObserved
                : EscalationLedgerStatus.Live;

            var detail = status switch
            {
                EscalationLedgerStatus.NoEventsObserved =>
                    "ledger was constructed but no event ever reached it",
                EscalationLedgerStatus.NoRequestsObserved =>
                    $"{_eventsObserved} events seen but zero llm_request — the agent loop's model identity never reached the ledger",
                EscalationLedgerStatus.StreamOmittedRequests =>
                    $"{droppedThreads} thread(s) reported more iteration_start than llm_request by >1; {_orphanResponses} llm_response had no matching llm_request",
                EscalationLedgerStatus.ModelIdentityAmbiguous =>
                    string.IsNullOrEmpty(BaseIdentity.Model)
                        ? "the run's base model name is empty, so no request can be classified"
                        : $"model name(s) [{string.Join(", ", ambiguous)}] are shared by two different (provider, endpoint, model) identities; llm_request carries only the name",
                _ => null,
            };

            var perModel = _stats.Values
                .OrderByDescending(s => s.Requests)
                .ThenBy(s => s.Model, StringComparer.Ordinal)
                .Select(s => new EscalationModelUsage
                {
                    Model = s.Model,
                    Tier = TierWire(TierOf(s.Model)),
                    Declared = declaredModels.Contains(s.Model),
                    Requests = s.Requests,
                    FailedAttempts = s.FailedAttempts,
                    InputTokens = s.InputTokens,
                    OutputTokens = s.OutputTokens,
                    ResponsesWithTokens = s.ResponsesWithTokens,
                    ResponsesWithoutTokens = s.ResponsesWithoutTokens,
                    ResponsesWithPartialTokens = s.ResponsesWithPartialTokens,
                })
                .ToList();

            var undeclaredEscalation = perModel
                .Where(m => m.Tier == "escalation" && !m.Declared)
                .Select(m => m.Model)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            var leader = _roster.Values.FirstOrDefault(r => r.IsLeader);

            return new EscalationLedgerSnapshot
            {
                Status = StatusWire(status),
                StatusDetail = detail,
                BaseModel = BaseIdentity.Model,
                BaseIdentity = BaseIdentity.ToString(),
                LeaderTier = leader is null ? null : TierWire(leader.Tier),
                EscalationTierConfigured = escalationMembers.Count > 0,
                EscalationTierMembers = escalationMembers,

                EventsObserved = _eventsObserved,
                ThreadsObserved = threads.Count,
                IterationStartsTotal = iterationStartsTotal,
                LlmRequestsTotal = requestsTotal,
                BaseRequests = perModel.Where(m => m.Tier == "base").Sum(m => m.Requests),
                EscalationRequests = perModel.Where(m => m.Tier == "escalation").Sum(m => m.Requests),
                UndeclaredEscalationModels = undeclaredEscalation,

                UnmatchedIterations = unmatched,
                ThreadsWithDroppedRequests = droppedThreads,
                OrphanResponses = _orphanResponses,
                CollidingRequestKeys = _collidingRequestKeys,
                MalformedLogLines = _malformedLogLines,
                FailedAttemptsTotal = _stats.Values.Sum(s => s.FailedAttempts),

                UninstrumentedCallSites = blindSpots,
                Notes = _notes.ToList(),
                PerModel = perModel,
            };
        }
    }

    private ModelTier TierOf(string model) =>
        ModelIdentity.Cmp.Equals(model, BaseIdentity.Model) ? ModelTier.Base : ModelTier.Escalation;

    /// <summary>Publish the ledger onto an event stream. The bench harness
    /// mirrors every event into <c>_bench-session.jsonl</c>, so emitting
    /// this puts the counts in the structured per-run log.</summary>
    public Event ToEvent()
    {
        var s = Snapshot();
        return new Event(EventType, new Dictionary<string, object?>
        {
            ["thread_id"] = LeaderThread,
            ["instrumented"] = true,
            ["status"] = s.Status,
            ["status_detail"] = s.StatusDetail,
            ["base_model"] = s.BaseModel,
            ["base_identity"] = s.BaseIdentity,
            ["leader_tier"] = s.LeaderTier,
            ["escalation_tier_configured"] = s.EscalationTierConfigured,
            ["escalation_tier_members"] = s.EscalationTierMembers,
            ["events_observed"] = s.EventsObserved,
            ["threads_observed"] = s.ThreadsObserved,
            ["iteration_starts_total"] = s.IterationStartsTotal,
            ["llm_requests_total"] = s.LlmRequestsTotal,
            ["base_requests"] = s.BaseRequests,
            ["escalation_requests"] = s.EscalationRequests,
            ["undeclared_escalation_models"] = s.UndeclaredEscalationModels,
            ["unmatched_iterations"] = s.UnmatchedIterations,
            ["threads_with_dropped_requests"] = s.ThreadsWithDroppedRequests,
            ["orphan_responses"] = s.OrphanResponses,
            ["colliding_request_keys"] = s.CollidingRequestKeys,
            ["malformed_log_lines"] = s.MalformedLogLines,
            ["failed_attempts_total"] = s.FailedAttemptsTotal,
            ["uninstrumented_call_sites"] = s.UninstrumentedCallSites,
            ["request_accounting_complete"] = s.RequestAccountingComplete,
            ["token_attribution_complete"] = s.TokenAttributionComplete,
            ["notes"] = s.Notes,
            ["per_model"] = s.PerModel,
        });
    }

    internal static string StatusWire(EscalationLedgerStatus s) => s switch
    {
        EscalationLedgerStatus.Live => "live",
        EscalationLedgerStatus.StreamOmittedRequests => "stream_omitted_requests",
        EscalationLedgerStatus.ModelIdentityAmbiguous => "model_identity_ambiguous",
        EscalationLedgerStatus.NoRequestsObserved => "no_requests_observed",
        _ => "no_events_observed",
    };

    internal static string TierWire(ModelTier t) => t == ModelTier.Escalation ? "escalation" : "base";

    private sealed class PendingRequest(string model)
    {
        public string Model { get; } = model;
        public int Outstanding = 1;
        public int Responded;
    }

    private sealed class ModelStat(string model)
    {
        public string Model { get; } = model;
        public int Requests;
        public int FailedAttempts;
        public long InputTokens;
        public long OutputTokens;
        public int ResponsesWithTokens;
        public int ResponsesWithoutTokens;
        public int ResponsesWithPartialTokens;
    }

    private static string? Str(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return s;
        if (v is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : null;
        return v.ToString();
    }

    private static long? Num(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return null;
        switch (v)
        {
            case int i: return i;
            case long l: return l;
            case double dd: return (long)dd;
            case JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var jl): return jl;
            default:
                return long.TryParse(v.ToString(), out var parsed) ? parsed : null;
        }
    }
}

/// <summary>Per-model attribution. The FLAG-OFF RULE in
/// ds-team-flash-escalate.yaml exists because escalation swaps model
/// identity mid-instance and destroys attribution — this is the table
/// that restores it.</summary>
public sealed class EscalationModelUsage
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";

    /// <summary><c>base</c> or <c>escalation</c>, decided by comparing
    /// against the run's base model.</summary>
    [JsonPropertyName("tier")] public string Tier { get; set; } = "base";

    /// <summary>Was this model named by the profile? False on an
    /// escalation-tier row means the run went somewhere nothing declared
    /// in advance — a finding, not an error.</summary>
    [JsonPropertyName("declared")] public bool Declared { get; set; }

    /// <summary>Logical LLM requests: one per <c>llm_request</c> event.</summary>
    [JsonPropertyName("requests")] public int Requests { get; set; }

    /// <summary>Extra HTTP attempts that no <c>llm_request</c> event
    /// represents, recovered from <c>llm_error</c>. Kept separate from
    /// <see cref="Requests"/> on purpose: they are attempts, not
    /// requests.</summary>
    [JsonPropertyName("failed_attempts")] public int FailedAttempts { get; set; }

    [JsonPropertyName("input_tokens")] public long InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public long OutputTokens { get; set; }

    /// <summary>Responses that contributed real numbers to the totals.</summary>
    [JsonPropertyName("responses_with_tokens")] public int ResponsesWithTokens { get; set; }

    /// <summary>Responses that contributed nothing because usage was
    /// absent. The token totals are a LOWER BOUND whenever this is
    /// non-zero.</summary>
    [JsonPropertyName("responses_without_tokens")] public int ResponsesWithoutTokens { get; set; }

    /// <summary>Responses that reported only one side of the usage pair.</summary>
    [JsonPropertyName("responses_with_partial_tokens")] public int ResponsesWithPartialTokens { get; set; }

    /// <summary>Requests that never produced a response event. Non-zero is
    /// normal on a cancelled run (one per live thread) and suspicious
    /// otherwise.</summary>
    [JsonPropertyName("requests_without_response")]
    public int RequestsWithoutResponse => Math.Max(0, Requests - ResponsesWithTokens - ResponsesWithoutTokens);
}

/// <summary>
/// Publishable snapshot of the escalation ledger. Reaches the wire as an
/// OBJECT, not an int: the surrounding fields are what keep a withheld
/// count from reading as a zero.
///
/// A NULL snapshot on the enclosing run means NOT INSTRUMENTED and must
/// never be rendered as zeros.
/// </summary>
public sealed class EscalationLedgerSnapshot
{
    /// <summary>Always true when the object exists. Present so a consumer
    /// that flattens the object still carries the discriminator; absence
    /// of the whole object is the "not instrumented" signal.</summary>
    [JsonPropertyName("instrumented")] public bool Instrumented { get; set; } = true;

    /// <summary><c>live</c> | <c>no_events_observed</c> |
    /// <c>no_requests_observed</c> | <c>stream_omitted_requests</c> |
    /// <c>model_identity_ambiguous</c>. Only <c>live</c> makes the counts
    /// readable — see <see cref="TryGetEscalationCount"/>.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "no_events_observed";

    /// <summary>Why the status is not <c>live</c>. Null when nothing is
    /// wrong.</summary>
    [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }

    /// <summary>The model name every request is compared against. Published
    /// so a reader can audit the tier rule instead of taking it on
    /// faith.</summary>
    [JsonPropertyName("base_model")] public string BaseModel { get; set; } = "";

    /// <summary>The full base (provider, endpoint, model).</summary>
    [JsonPropertyName("base_identity")] public string BaseIdentity { get; set; } = "";

    /// <summary>Tier the LEADER is configured on. A Pro leader is a cost
    /// that a members-only view would hide.</summary>
    [JsonPropertyName("leader_tier")] public string? LeaderTier { get; set; }

    /// <summary>Did this profile declare ANY member on a non-base model?
    /// False turns a zero escalation count into a STRUCTURAL zero —
    /// escalation was impossible, so its absence is not a finding.</summary>
    [JsonPropertyName("escalation_tier_configured")] public bool EscalationTierConfigured { get; set; }

    /// <summary>The escalation domain, declared from config BEFORE the
    /// run.</summary>
    [JsonPropertyName("escalation_tier_members")] public List<string> EscalationTierMembers { get; set; } = new();

    /// <summary>Every event fed to the ledger, of any type. Zero here means
    /// the instrument was never connected.</summary>
    [JsonPropertyName("events_observed")] public int EventsObserved { get; set; }

    /// <summary>Distinct thread ids seen (leader counts as one).</summary>
    [JsonPropertyName("threads_observed")] public int ThreadsObserved { get; set; }

    /// <summary>Agent-loop iterations observed. The completeness check
    /// compares this against <see cref="LlmRequestsTotal"/> per thread.</summary>
    [JsonPropertyName("iteration_starts_total")] public int IterationStartsTotal { get; set; }

    /// <summary>THE LIVENESS CONJUNCT. Zero means the instrument saw no
    /// LLM traffic, which is not the same as a run with no
    /// escalations.</summary>
    [JsonPropertyName("llm_requests_total")] public int LlmRequestsTotal { get; set; }

    [JsonPropertyName("base_requests")] public int BaseRequests { get; set; }
    [JsonPropertyName("escalation_requests")] public int EscalationRequests { get; set; }

    /// <summary>Models the run escalated to that the profile never
    /// declared. Non-empty means escalation happened outside the
    /// pre-registered domain.</summary>
    [JsonPropertyName("undeclared_escalation_models")]
    public List<string> UndeclaredEscalationModels { get; set; } = new();

    /// <summary>Sum over threads of (iteration_start - llm_request) where
    /// positive. Up to one per live thread is the normal signature of a
    /// cancelled run; more than that on a single thread is a dropped
    /// request.</summary>
    [JsonPropertyName("unmatched_iterations")] public int UnmatchedIterations { get; set; }

    /// <summary>Threads whose iteration/request gap exceeds the one
    /// in-flight iteration a cancellation can explain.</summary>
    [JsonPropertyName("threads_with_dropped_requests")] public int ThreadsWithDroppedRequests { get; set; }

    /// <summary>Responses with no matching request — the direct signature
    /// of a request event going missing.</summary>
    [JsonPropertyName("orphan_responses")] public int OrphanResponses { get; set; }

    /// <summary>(thread, iteration) keys that received more than one
    /// request. Token pairing on those keys is ambiguous; request counts
    /// are unaffected.</summary>
    [JsonPropertyName("colliding_request_keys")] public int CollidingRequestKeys { get; set; }

    /// <summary>THE DISCARD COUNT. Session-log lines that would not parse.
    /// A truncated log is itself a way for requests to vanish.</summary>
    [JsonPropertyName("malformed_log_lines")] public int MalformedLogLines { get; set; }

    /// <summary>Failed HTTP attempts recovered from <c>llm_error</c>.</summary>
    [JsonPropertyName("failed_attempts_total")] public int FailedAttemptsTotal { get; set; }

    /// <summary>
    /// THE DECLARED BLIND SPOTS, in the form <c>member:middleware
    /// (model)</c>. These are LLM calls the run makes that emit no event,
    /// so no stream-derived counter can see them. Empty is the healthy
    /// value. Non-empty means the per-model numbers below are an explicit
    /// LOWER BOUND on that model's usage — declared, not hidden.
    /// </summary>
    [JsonPropertyName("uninstrumented_call_sites")]
    public List<string> UninstrumentedCallSites { get; set; } = new();

    /// <summary>Specific anomalies the ledger saw, capped at 50.</summary>
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();

    [JsonPropertyName("per_model")] public List<EscalationModelUsage> PerModel { get; set; } = new();

    /// <summary>True only when NO uninstrumented LLM-calling middleware is
    /// configured anywhere in the roster and no request arrived without a
    /// model. False means the request counts are a LOWER BOUND.</summary>
    [JsonPropertyName("request_accounting_complete")]
    public bool RequestAccountingComplete =>
        UninstrumentedCallSites.Count == 0
        && !PerModel.Any(m => m.Model == "(unknown)");

    /// <summary>True only when every observed request produced a response
    /// carrying both token numbers, no key collided, and no blind spot is
    /// armed. False means the per-model token totals are a LOWER BOUND,
    /// not a measurement.</summary>
    [JsonPropertyName("token_attribution_complete")]
    public bool TokenAttributionComplete =>
        RequestAccountingComplete
        && CollidingRequestKeys == 0
        && PerModel.All(m => m.ResponsesWithoutTokens == 0
                             && m.ResponsesWithPartialTokens == 0
                             && m.RequestsWithoutResponse == 0);

    /// <summary>
    /// THE GATE. Returns the escalation request count only when it is an
    /// actual measurement. Returns false — leaving <paramref name="count"/>
    /// at zero, which the caller must NOT use — for every non-<c>live</c>
    /// status.
    ///
    /// Scorers must call this rather than reading
    /// <see cref="EscalationRequests"/> directly; that field is public for
    /// serialization and post-mortem, not for arithmetic.
    /// </summary>
    public bool TryGetEscalationCount(out int count)
    {
        count = 0;
        if (!string.Equals(Status, "live", StringComparison.Ordinal)) return false;
        count = EscalationRequests;
        return true;
    }

    /// <summary>One-line verdict for logs and reports. Never renders a
    /// withheld count as a number.</summary>
    public string Describe()
    {
        if (!TryGetEscalationCount(out var n))
            return $"escalation: NOT MEASURED ({Status}{(StatusDetail is null ? "" : ": " + StatusDetail)})";

        var bound = RequestAccountingComplete ? "" : " [LOWER BOUND — uninstrumented call sites declared]";
        if (!EscalationTierConfigured && n == 0)
            return $"escalation: 0 of {LlmRequestsTotal} requests — STRUCTURAL ZERO (profile declares no escalation tier){bound}";
        if (n == 0)
            return $"escalation: 0 of {LlmRequestsTotal} requests — MEASURED ZERO (tier available: {string.Join(", ", EscalationTierMembers)}){bound}";
        return $"escalation: {n} of {LlmRequestsTotal} requests on the escalation tier{bound}";
    }
}
