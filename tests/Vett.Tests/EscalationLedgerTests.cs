using System.Text.Json;
using Vett.Agent;
using Vett.Bench.Team;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// HARNESS-OWNED ESCALATION COUNTER — proof suite.
///
/// The counter under test (<see cref="EscalationLedger"/>) is a reducer
/// over the run's own event stream: it counts <c>llm_request</c> events
/// and splits them by the <c>model</c> field the agent loop stamps on
/// each one. Nothing here parses prose and nothing trusts a model's
/// self-report.
///
/// These tests are deliberately split into two disjoint families, because
/// they are proving two different things and a single "it counted right"
/// suite cannot separate them:
///
///   COUNT tests   — the number is a live function of the stream.
///   HONESTY tests — a number that is NOT a measurement is WITHHELD, and
///                   the four zero-ish outcomes (not instrumented / no
///                   events / structural zero / measured zero) never
///                   collapse into the same output.
///
/// The disable-and-predict proof exploits that split: killing the counter
/// reddens the COUNT family and leaves the HONESTY family green, while
/// stripping the withholding reddens the HONESTY family and leaves the
/// COUNT family green.
/// </summary>
public class EscalationLedgerTests
{
    // ---------- stream builders ----------

    private static Event Ev(string type, params (string Key, object? Val)[] kv)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        return new Event(type, d);
    }

    /// <summary>An agent-loop iteration boundary. Thread null = the
    /// leader, which emits no thread_id (TeamCoordinator.TaggedEmit only
    /// tags MEMBER events).</summary>
    private static Event IterStart(string? thread, int iter) =>
        thread is null
            ? Ev("iteration_start", ("iteration", iter))
            : Ev("iteration_start", ("iteration", iter), ("thread_id", thread), ("member", thread));

    private static Event Req(string? thread, int iter, string model) =>
        thread is null
            ? Ev("llm_request", ("iteration", iter), ("model", model))
            : Ev("llm_request", ("iteration", iter), ("model", model), ("thread_id", thread), ("member", thread));

    private static Event Resp(string? thread, int iter, long? inTok, long? outTok) =>
        thread is null
            ? Ev("llm_response", ("iteration", iter), ("input_tokens", inTok), ("output_tokens", outTok))
            : Ev("llm_response", ("iteration", iter), ("input_tokens", inTok), ("output_tokens", outTok),
                 ("thread_id", thread), ("member", thread));

    private static Event Err(string? thread, bool willRetry) =>
        thread is null
            ? Ev("llm_error", ("message", "boom"), ("attempt", 0), ("retry_cap", 2), ("will_retry", willRetry))
            : Ev("llm_error", ("message", "boom"), ("attempt", 0), ("retry_cap", 2), ("will_retry", willRetry),
                 ("thread_id", thread), ("member", thread));

    /// <summary>One healthy iteration: start, request, response.</summary>
    private static IEnumerable<Event> Turn(string? thread, int iter, string model, long inTok = 100, long outTok = 20)
    {
        yield return IterStart(thread, iter);
        yield return Req(thread, iter, model);
        yield return Resp(thread, iter, inTok, outTok);
    }

    private const string Flash = "deepseek-v4-flash";
    private const string Pro = "deepseek/deepseek-v4-pro";

    /// <summary>Minimal two-tier team: implementer on the base model,
    /// implementer-pro on a paid endpoint. Parsed from YAML rather than
    /// hand-built so the inherit-vs-override merge rule
    /// (ChatClientFactory.Merge) is exercised for real — an empty string
    /// means INHERIT, and getting that wrong in either direction either
    /// hides an escalation or invents one.</summary>
    private static Profile TwoTierProfile(bool proRunsCondenser = false) => Yaml.ParseProfile($"""
        name: test-escalating
        llm:
          provider: local
          endpoint: http://base:8000/v1
          model: {Flash}
        team:
          leader:
            name: lead
          members:
            - name: implementer
              middleware:
                - output_truncation
            - name: implementer-pro
              middleware:
                - output_truncation{(proRunsCondenser ? "\n        - llm_summarizing_condenser" : "")}
              llm:
                endpoint: https://openrouter.ai/api/v1
                model: {Pro}
                api_key_env: DEEPSEEK_API_KEY
        """);

    /// <summary>Single-tier team: every member inherits the base model, so
    /// escalation is STRUCTURALLY impossible.</summary>
    private static Profile SingleTierProfile() => Yaml.ParseProfile($"""
        name: test-local
        llm:
          provider: local
          endpoint: http://base:8000/v1
          model: {Flash}
        team:
          leader:
            name: lead
          members:
            - name: implementer
            - name: reviewer
        """);

    private static EscalationLedger Fed(Profile p, IEnumerable<Event> events)
    {
        var l = EscalationLedger.FromProfile(p, "http://base:8000/v1", Flash);
        l.ObserveAll(events);
        return l;
    }

    // ==================================================================
    // COUNT FAMILY — the number is a live function of the stream.
    // ==================================================================

    [Fact]
    public void Counts_BaseAndEscalationRequests_Separately()
    {
        var s = Fed(TwoTierProfile(), [
            .. Turn("impl-1", 0, Flash),
            .. Turn("impl-1", 1, Flash),
            .. Turn("pro-1", 0, Pro),
        ]).Snapshot();

        Assert.Equal("live", s.Status);
        Assert.Equal(3, s.LlmRequestsTotal);
        Assert.Equal(2, s.BaseRequests);
        Assert.Equal(1, s.EscalationRequests);
        Assert.True(s.TryGetEscalationCount(out var n));
        Assert.Equal(1, n);
    }

    [Fact]
    public void PerModel_TokensAreAttributedToTheModelThatWasActuallyAsked()
    {
        // llm_response carries tokens but NOT a model, so the ledger must
        // pair them back to the request on the same (thread, iteration).
        // Interleaving two threads on the same iteration number is the
        // case that breaks a naive "last request wins" implementation.
        var s = Fed(TwoTierProfile(), [
            IterStart("impl-1", 0), Req("impl-1", 0, Flash),
            IterStart("pro-1", 0),  Req("pro-1", 0, Pro),
            Resp("pro-1", 0, 900, 90),
            Resp("impl-1", 0, 100, 10),
        ]).Snapshot();

        var flash = s.PerModel.Single(m => m.Model == Flash);
        var pro = s.PerModel.Single(m => m.Model == Pro);

        Assert.Equal(100, flash.InputTokens);
        Assert.Equal(10, flash.OutputTokens);
        Assert.Equal(900, pro.InputTokens);
        Assert.Equal(90, pro.OutputTokens);
        Assert.Equal("base", flash.Tier);
        Assert.Equal("escalation", pro.Tier);
    }

    [Fact]
    public void LeaderEventsWithoutThreadId_AreCounted_OnTheLeaderThread()
    {
        var s = Fed(TwoTierProfile(), [.. Turn(null, 0, Flash), .. Turn(null, 1, Flash)]).Snapshot();

        Assert.Equal("live", s.Status);
        Assert.Equal(2, s.LlmRequestsTotal);
        Assert.Equal(1, s.ThreadsObserved);
    }

    [Fact]
    public void FailedAttempts_AreCountedSeparatelyFromRequests()
    {
        // AgentLoop retries an HTTP failure up to twice WITHOUT re-emitting
        // llm_request, so one request event can be three billed attempts.
        // Folding those into `requests` would silently inflate the request
        // count; dropping them would silently hide real traffic.
        var s = Fed(TwoTierProfile(), [
            IterStart("pro-1", 0),
            Req("pro-1", 0, Pro),
            Err("pro-1", willRetry: true),
            Err("pro-1", willRetry: true),
            Resp("pro-1", 0, 900, 90),
        ]).Snapshot();

        var pro = s.PerModel.Single(m => m.Model == Pro);
        Assert.Equal(1, pro.Requests);
        Assert.Equal(2, pro.FailedAttempts);
        Assert.Equal(2, s.FailedAttemptsTotal);
    }

    [Fact]
    public void UndeclaredEscalationModel_IsCounted_AndFlagged()
    {
        // The run went somewhere the profile never named. That is a
        // finding about the run, not an error in the ledger, so the count
        // stands AND the model is named.
        var s = Fed(TwoTierProfile(), [
            .. Turn("impl-1", 0, Flash),
            .. Turn("mystery", 0, "gpt-9-turbo"),
        ]).Snapshot();

        Assert.Equal("live", s.Status);
        Assert.Equal(1, s.EscalationRequests);
        Assert.Equal(["gpt-9-turbo"], s.UndeclaredEscalationModels);
        Assert.False(s.PerModel.Single(m => m.Model == "gpt-9-turbo").Declared);
    }

    // ==================================================================
    // HONESTY FAMILY — a non-measurement is WITHHELD, and the zero-ish
    // outcomes stay distinguishable.
    // ==================================================================

    [Fact]
    public void NoEventsAtAll_IsNotMeasured_NotZero()
    {
        var s = EscalationLedger.FromProfile(TwoTierProfile(), "http://base:8000/v1", Flash).Snapshot();

        Assert.Equal("no_events_observed", s.Status);
        Assert.False(s.TryGetEscalationCount(out _));
        Assert.Contains("NOT MEASURED", s.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void EventsButNoLlmRequest_IsCouldNotMeasure_NotZero()
    {
        // The instrument was connected and saw traffic, but none of it
        // carried model identity. "Could not measure" — not "measured
        // zero".
        var s = Fed(TwoTierProfile(), [
            Ev("dispatch_start", ("member", "implementer")),
            Ev("tool_call", ("name", "terminal")),
        ]).Snapshot();

        Assert.Equal("no_requests_observed", s.Status);
        Assert.Equal(0, s.LlmRequestsTotal);
        Assert.False(s.TryGetEscalationCount(out _));
        Assert.Contains("NOT MEASURED", s.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void MeasuredZero_IsReadable_AndSaysSo()
    {
        // Escalation WAS available, the run had real LLM traffic, and it
        // never escalated. The only zero that is a finding.
        var s = Fed(TwoTierProfile(), [.. Turn("impl-1", 0, Flash), .. Turn("impl-1", 1, Flash)]).Snapshot();

        Assert.Equal("live", s.Status);
        Assert.True(s.EscalationTierConfigured);
        Assert.True(s.TryGetEscalationCount(out var n));
        Assert.Equal(0, n);
        Assert.Contains("MEASURED ZERO", s.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void StructuralZero_IsReadable_AndDistinguishedFromMeasuredZero()
    {
        // No second tier exists, so zero escalations was the only possible
        // outcome. Reporting this as if it were evidence about the ladder
        // would be the same defect in a different coat.
        var l = EscalationLedger.FromProfile(SingleTierProfile(), "http://base:8000/v1", Flash);
        l.ObserveAll(Turn("impl-1", 0, Flash));
        var s = l.Snapshot();

        Assert.Equal("live", s.Status);
        Assert.False(s.EscalationTierConfigured);
        Assert.True(s.TryGetEscalationCount(out var n));
        Assert.Equal(0, n);
        Assert.Contains("STRUCTURAL ZERO", s.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CORE REQUIREMENT, asserted directly: the four zero-ish outcomes
    /// must not produce the same output. A single int cannot express this
    /// difference — which is why the ledger publishes an object.
    /// </summary>
    [Fact]
    public void FourZeroishOutcomes_ProduceFourDistinctOutputs()
    {
        // 1. NOT INSTRUMENTED — no snapshot exists at all. Modelled here
        //    as the null a consumer would carry on the run summary.
        EscalationLedgerSnapshot? notInstrumented = null;

        // 2. NO EVENTS — armed, nothing reached it.
        var noEvents = EscalationLedger.FromProfile(TwoTierProfile(), "http://base:8000/v1", Flash).Snapshot();

        // 3. STRUCTURAL ZERO — traffic seen, no escalation tier exists.
        var structural = Fed(SingleTierProfile(), Turn("impl-1", 0, Flash)).Snapshot();

        // 4. MEASURED ZERO — traffic seen, escalation tier exists, unused.
        var measured = Fed(TwoTierProfile(), Turn("impl-1", 0, Flash)).Snapshot();

        Assert.Null(notInstrumented);

        var descriptions = new[] { noEvents.Describe(), structural.Describe(), measured.Describe() };
        Assert.Equal(3, descriptions.Distinct(StringComparer.Ordinal).Count());

        // And the machine-readable discriminators disagree too, so a
        // scorer never has to read the prose.
        Assert.False(noEvents.TryGetEscalationCount(out _));
        Assert.True(structural.TryGetEscalationCount(out _));
        Assert.True(measured.TryGetEscalationCount(out _));
        Assert.NotEqual(structural.EscalationTierConfigured, measured.EscalationTierConfigured);
    }

    [Fact]
    public void Describe_NeverRendersAWithheldCountAsANumber()
    {
        foreach (var s in new[]
                 {
                     EscalationLedger.FromProfile(TwoTierProfile(), "http://base:8000/v1", Flash).Snapshot(),
                     Fed(TwoTierProfile(), [Ev("dispatch_start", ("member", "implementer"))]).Snapshot(),
                     Fed(TwoTierProfile(), [
                         IterStart("t", 0), IterStart("t", 1), IterStart("t", 2), IterStart("t", 3),
                         Req("t", 0, Flash),
                     ]).Snapshot(),
                 })
        {
            Assert.False(s.TryGetEscalationCount(out _));
            var d = s.Describe();
            Assert.Contains("NOT MEASURED", d, StringComparison.Ordinal);
            Assert.DoesNotContain("MEASURED ZERO", d, StringComparison.Ordinal);
            Assert.DoesNotContain("requests on the escalation tier", d, StringComparison.Ordinal);
        }
    }

    // ==================================================================
    // FAILURE TESTS — an invariant is only as real as its failure test.
    // Each of these constructs a stream the ledger MUST refuse.
    // ==================================================================

    [Fact]
    public void SilentlyDroppedRequest_IsCaught_ByTheOrphanResponse()
    {
        // Delete one llm_request from an otherwise healthy stream. Its
        // response survives and has nothing to pair with — the direct
        // signature of a request event going missing.
        var s = Fed(TwoTierProfile(), [
            .. Turn("impl-1", 0, Flash),
            IterStart("impl-1", 1), /* llm_request DELETED */ Resp("impl-1", 1, 100, 10),
            .. Turn("impl-1", 2, Flash),
        ]).Snapshot();

        Assert.Equal(1, s.OrphanResponses);
        Assert.Equal("stream_omitted_requests", s.Status);
        Assert.False(s.TryGetEscalationCount(out _));
    }

    [Fact]
    public void MultipleDroppedRequests_ExceedTheCancellationAllowance()
    {
        // Requests dropped with no surviving responses, so the orphan
        // detector cannot see them. The iteration/request ledger still
        // can: four iterations opened, one request logged.
        var s = Fed(TwoTierProfile(), [
            IterStart("impl-1", 0), Req("impl-1", 0, Flash),
            IterStart("impl-1", 1),
            IterStart("impl-1", 2),
            IterStart("impl-1", 3),
        ]).Snapshot();

        Assert.Equal(4, s.IterationStartsTotal);
        Assert.Equal(1, s.LlmRequestsTotal);
        Assert.Equal(3, s.UnmatchedIterations);
        Assert.Equal(1, s.ThreadsWithDroppedRequests);
        Assert.Equal("stream_omitted_requests", s.Status);
        Assert.False(s.TryGetEscalationCount(out _));
    }

    [Fact]
    public void OneTrailingIterationWithoutARequest_IsToleratedAsCancellation()
    {
        // The OTHER side of the same detector. A run cancelled between
        // iteration_start and llm_request legitimately leaves exactly one
        // unmatched iteration per live thread. If the ledger refused here
        // it would report "omitted" on every timed-out run, and a detector
        // that fires on the healthy case is not a detector.
        var s = Fed(TwoTierProfile(), [
            .. Turn("impl-1", 0, Flash),
            .. Turn("impl-1", 1, Flash),
            IterStart("impl-1", 2),
        ]).Snapshot();

        Assert.Equal(1, s.UnmatchedIterations);
        Assert.Equal(0, s.ThreadsWithDroppedRequests);
        Assert.Equal("live", s.Status);
        Assert.True(s.TryGetEscalationCount(out _));
    }

    [Fact]
    public void AmbiguousModelNames_WithholdTheCount()
    {
        // Two tiers behind ONE model name. llm_request carries only the
        // name, so the stream is physically incapable of telling them
        // apart. Guessing would produce a confident, wrong number.
        var p = Yaml.ParseProfile($"""
            name: ambiguous
            llm:
              provider: local
              endpoint: http://base:8000/v1
              model: {Flash}
            team:
              leader:
                name: lead
              members:
                - name: implementer
                - name: implementer-remote
                  llm:
                    endpoint: https://openrouter.ai/api/v1
                    model: {Flash}
            """);

        var s = Fed(p, Turn("impl-1", 0, Flash)).Snapshot();

        Assert.Equal("model_identity_ambiguous", s.Status);
        Assert.False(s.TryGetEscalationCount(out _));
        Assert.Contains(Flash, s.StatusDetail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyBaseModel_WithholdsTheCount()
    {
        var l = EscalationLedger.ForBaseModel("");
        l.ObserveAll(Turn("impl-1", 0, Flash));
        var s = l.Snapshot();

        Assert.Equal("model_identity_ambiguous", s.Status);
        Assert.False(s.TryGetEscalationCount(out _));
    }

    /// <summary>
    /// PROVE THE COUNTER CAN REPORT A WRONG ANSWER. The same stream,
    /// scored against a mis-declared base model, yields the INVERTED tier
    /// split. This is the two-sided evidence that the number is a live
    /// function of its inputs rather than a constant that happens to look
    /// right — and it names the one input a caller can get wrong.
    /// </summary>
    [Fact]
    public void MisdeclaredBaseModel_InvertsTheTierSplit()
    {
        Event[] stream = [
            .. Turn("a", 0, Flash), .. Turn("a", 1, Flash), .. Turn("a", 2, Flash),
            .. Turn("b", 0, Pro),
        ];

        var right = EscalationLedger.ForBaseModel(Flash);
        right.ObserveAll(stream);
        var rs = right.Snapshot();

        var wrong = EscalationLedger.ForBaseModel(Pro);
        wrong.ObserveAll(stream);
        var ws = wrong.Snapshot();

        Assert.Equal(3, rs.BaseRequests);
        Assert.Equal(1, rs.EscalationRequests);

        Assert.Equal(1, ws.BaseRequests);
        Assert.Equal(3, ws.EscalationRequests);

        // Both are "live" — the ledger cannot know which base model the
        // caller meant. base_model is published on the snapshot precisely
        // so a reader can audit the rule instead of taking it on faith.
        Assert.Equal(Flash, rs.BaseModel);
        Assert.Equal(Pro, ws.BaseModel);
    }

    [Fact]
    public void RequestWithoutAModelField_IsNamed_NotSilentlyDropped()
    {
        var s = Fed(TwoTierProfile(), [
            IterStart("impl-1", 0), Ev("llm_request", ("iteration", 0), ("thread_id", "impl-1")),
        ]).Snapshot();

        Assert.Equal(1, s.LlmRequestsTotal);
        Assert.Contains(s.PerModel, m => m.Model == "(unknown)");
        Assert.False(s.RequestAccountingComplete);
        Assert.Contains(s.Notes, n => n.Contains("no model field", StringComparison.Ordinal));
    }

    // ==================================================================
    // COMPLETENESS / LOWER-BOUND DECLARATION
    // ==================================================================

    [Fact]
    public void UninstrumentedMiddleware_IsDeclared_AndMarksAccountingIncomplete()
    {
        // llm_summarizing_condenser calls GetResponseAsync and emits NO
        // event, using its host agent's model. On the PAID member that is
        // invisible spend. The ledger cannot see it — so it declares it,
        // and refuses to call its own numbers complete.
        var s = Fed(TwoTierProfile(proRunsCondenser: true), Turn("pro-1", 0, Pro)).Snapshot();

        Assert.Contains(s.UninstrumentedCallSites,
            c => c.StartsWith("implementer-pro:llm_summarizing_condenser", StringComparison.Ordinal));
        Assert.Contains(Pro, s.UninstrumentedCallSites.Single());
        Assert.False(s.RequestAccountingComplete);
        Assert.False(s.TokenAttributionComplete);
        Assert.Contains("LOWER BOUND", s.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoLlmCallingMiddleware_LeavesAccountingComplete()
    {
        // The other side. An EMPTY middleware list falls back to
        // Builtins.DefaultMiddleware() — submit-detector, output-truncation,
        // stuck-detector — none of which call an LLM, so an unset list is a
        // genuine zero here, not an unknown. If this went false too, the
        // blind-spot flag would be a constant and worth nothing.
        var s = Fed(SingleTierProfile(), Turn("impl-1", 0, Flash)).Snapshot();

        Assert.Empty(s.UninstrumentedCallSites);
        Assert.True(s.RequestAccountingComplete);
        Assert.True(s.TokenAttributionComplete);
        Assert.DoesNotContain("LOWER BOUND", s.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseWithoutTokens_IsUnknown_NotZero()
    {
        var s = Fed(TwoTierProfile(), [
            IterStart("impl-1", 0), Req("impl-1", 0, Flash), Resp("impl-1", 0, null, null),
        ]).Snapshot();

        var flash = s.PerModel.Single(m => m.Model == Flash);
        Assert.Equal(0, flash.ResponsesWithTokens);
        Assert.Equal(1, flash.ResponsesWithoutTokens);
        Assert.Equal(0, flash.InputTokens);
        Assert.False(s.TokenAttributionComplete);   // totals are a lower bound
        Assert.Equal("live", s.Status);              // but the REQUEST count still stands
        Assert.True(s.TryGetEscalationCount(out _));
    }

    [Fact]
    public void RequestWithNoResponse_IsCounted_AsUnanswered()
    {
        var s = Fed(TwoTierProfile(), [
            .. Turn("impl-1", 0, Flash),
            IterStart("impl-1", 1), Req("impl-1", 1, Flash),
        ]).Snapshot();

        var flash = s.PerModel.Single(m => m.Model == Flash);
        Assert.Equal(2, flash.Requests);
        Assert.Equal(1, flash.RequestsWithoutResponse);
        Assert.False(s.TokenAttributionComplete);
    }

    // ==================================================================
    // CONFIG-DERIVED ROSTER (the escalation domain, pre-registered)
    // ==================================================================

    [Fact]
    public void Roster_TreatsInheritedLlmAsBase_AndOverriddenModelAsEscalation()
    {
        var l = EscalationLedger.FromProfile(TwoTierProfile(), "http://base:8000/v1", Flash);
        var byName = l.Roster.ToDictionary(r => r.Member, StringComparer.Ordinal);

        Assert.Equal(ModelTier.Base, byName["lead"].Tier);
        Assert.Equal(ModelTier.Base, byName["implementer"].Tier);
        Assert.Equal(ModelTier.Escalation, byName["implementer-pro"].Tier);
        Assert.Equal(Pro, byName["implementer-pro"].Identity.Model);
        // Provider was NOT set on the member block, so it inherits.
        Assert.Equal("local", byName["implementer-pro"].Identity.Provider);
    }

    [Fact]
    public void CliOverrideOfBaseModel_MovesTheTierBoundary()
    {
        // team-bench takes --model, so the base tier is whatever the RUN
        // used, not whatever the YAML says. Score the same profile against
        // a different base and the roster re-classifies.
        var l = EscalationLedger.FromProfile(TwoTierProfile(), "https://openrouter.ai/api/v1", Pro);
        var byName = l.Roster.ToDictionary(r => r.Member, StringComparer.Ordinal);

        // implementer-pro pins its own endpoint+model, so it stops being an
        // escalation the moment the run's base moves onto that same model.
        Assert.Equal(ModelTier.Base, byName["implementer-pro"].Tier);

        // implementer pins NOTHING, so it inherits whatever the run's base
        // is and is base-tier by construction — under this override it is
        // now silently running on the PAID model. That is a real property
        // of --model overrides, not a ledger artefact: the tier split stays
        // honest, but "base" no longer means "cheap".
        Assert.Equal(ModelTier.Base, byName["implementer"].Tier);
        Assert.Equal(Pro, byName["implementer"].Identity.Model);
        Assert.False(l.Snapshot().EscalationTierConfigured);
    }

    /// <summary>
    /// The real shipped profile this counter was built for. Locks in what
    /// the ledger derives from it BEFORE any run happens — including the
    /// finding that the paid member is configured with an uninstrumented
    /// LLM-calling middleware, so its cost can only ever be a declared
    /// lower bound until that middleware emits events.
    /// </summary>
    [Fact]
    public void RealEscalatingProfile_DeclaresProImplementer_AndItsBlindSpots()
    {
        var path = FindRepoFile(Path.Combine("profiles", "ds-team-flash-escalate.yaml"));
        Assert.True(File.Exists(path), $"profile not found at {path}");

        var profile = Yaml.LoadProfile(path);
        var l = EscalationLedger.FromProfile(profile, profile.Llm.Endpoint, profile.Llm.Model);
        var byName = l.Roster.ToDictionary(r => r.Member, StringComparer.Ordinal);

        Assert.Equal(ModelTier.Escalation, byName["implementer-pro"].Tier);
        Assert.Equal("deepseek/deepseek-v4-pro", byName["implementer-pro"].Identity.Model);
        Assert.Equal(ModelTier.Base, byName["implementer"].Tier);

        var s = l.Snapshot();
        Assert.True(s.EscalationTierConfigured);
        Assert.Equal(["implementer-pro"], s.EscalationTierMembers);

        // The finding: the PAID member runs an LLM-calling middleware that
        // emits no events, so escalation spend is under-counted by an
        // unknown amount until CompactionMiddleware is instrumented.
        Assert.Contains(s.UninstrumentedCallSites,
            c => c.StartsWith("implementer-pro:llm_summarizing_condenser", StringComparison.Ordinal));
        Assert.False(s.RequestAccountingComplete);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return relative;
    }

    // ==================================================================
    // SESSION-LOG REPLAY (the offline path — no contested edit needed)
    // ==================================================================

    [Fact]
    public void SessionLog_Replay_ProducesTheSameCountsAsTheLiveStream()
    {
        Event[] stream = [
            .. Turn("impl-1", 0, Flash),
            .. Turn("impl-1", 1, Flash),
            .. Turn("pro-1", 0, Pro, 900, 90),
        ];

        var path = Path.Combine(Path.GetTempPath(), $"vett-escledger-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path, stream.Select(e => JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                type = e.Type,
                data = e.Data,
            })));

            var offline = EscalationLedger.FromSessionLog(TwoTierProfile(), "http://base:8000/v1", Flash, path)
                                          .Snapshot();
            var live = Fed(TwoTierProfile(), stream).Snapshot();

            Assert.Equal("live", offline.Status);
            Assert.Equal(live.LlmRequestsTotal, offline.LlmRequestsTotal);
            Assert.Equal(live.BaseRequests, offline.BaseRequests);
            Assert.Equal(live.EscalationRequests, offline.EscalationRequests);
            Assert.Equal(1, offline.EscalationRequests);
            Assert.Equal(900, offline.PerModel.Single(m => m.Model == Pro).InputTokens);
            Assert.Equal(0, offline.MalformedLogLines);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MalformedLogLines_ArePublishedAsADiscardCount()
    {
        // A truncated log is itself a way for requests to vanish. Skipping
        // bad lines without publishing how many were skipped would make a
        // half-read log look like a complete one.
        var path = Path.Combine(Path.GetTempPath(), $"vett-escledger-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path, [
                JsonSerializer.Serialize(new { ts = "t", type = "iteration_start", data = new { iteration = 0 } }),
                "{\"ts\":\"t\",\"type\":\"llm_request\",\"data\":{\"iteration\":0,\"mod",  // truncated
                "not json at all",
            ]);

            var s = EscalationLedger.FromSessionLog(TwoTierProfile(), "http://base:8000/v1", Flash, path).Snapshot();

            Assert.Equal(2, s.MalformedLogLines);
            Assert.Equal(1, s.IterationStartsTotal);
            Assert.Equal(0, s.LlmRequestsTotal);
            // One iteration, no request: within the 1-in-flight allowance,
            // so the omission detector stays quiet — but there is no
            // request at all, so the count is still withheld.
            Assert.Equal("no_requests_observed", s.Status);
            Assert.False(s.TryGetEscalationCount(out _));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ToEvent_CarriesTheDiscriminators_NotJustTheNumber()
    {
        var ev = Fed(TwoTierProfile(), Turn("impl-1", 0, Flash)).ToEvent();

        Assert.Equal("escalation_ledger", ev.Type);
        Assert.Equal(true, ev.Data["instrumented"]);
        Assert.Equal("live", ev.Data["status"]);
        Assert.Equal(0, ev.Data["escalation_requests"]);
        Assert.Equal(1, ev.Data["llm_requests_total"]);
        Assert.Equal(true, ev.Data["escalation_tier_configured"]);
        // The liveness conjunct and the blind-spot declaration must travel
        // with the number — a bare count in the stream would be re-readable
        // as a measured zero by anyone who saw only that field.
        Assert.True(ev.Data.ContainsKey("uninstrumented_call_sites"));
        Assert.True(ev.Data.ContainsKey("request_accounting_complete"));
        Assert.True(ev.Data.ContainsKey("iteration_starts_total"));
    }

    [Fact]
    public void SnapshotJson_UsesSnakeCaseWireNames()
    {
        var json = JsonSerializer.Serialize(Fed(TwoTierProfile(), Turn("impl-1", 0, Flash)).Snapshot());

        foreach (var key in new[]
                 {
                     "\"instrumented\"", "\"status\"", "\"base_model\"", "\"llm_requests_total\"",
                     "\"base_requests\"", "\"escalation_requests\"", "\"escalation_tier_configured\"",
                     "\"uninstrumented_call_sites\"", "\"request_accounting_complete\"",
                     "\"token_attribution_complete\"", "\"per_model\"", "\"malformed_log_lines\"",
                 })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }
    }
}
