using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Vett.Agent;
using Vett.Cli;
using Vett.Config;
using Vett.Llm;
using Vett.Mcp;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Bench.Team;

/// <summary>
/// Runs one team-mode benchmark instance end-to-end:
///   1. Provisions a clean workspace per <see cref="TeamBenchInstance.Workspace"/>.
///   2. Builds a chat client + DirectBash sandbox + event capture channel.
///   3. Drives <see cref="TeamCoordinator.RunInteractiveAsync"/> with the
///      instance's prompt as a single user message.
///   4. Waits for the loop to settle (quiet period after the leader signals
///      <c>user_input_needed</c>) or hits the per-instance timeout.
///   5. Evaluates the assertions deterministically.
///
/// No LLM judge anywhere. Every check is structural (event field, tool
/// arg shape, file content regex, iteration counter).
/// </summary>
public static class Harness
{
    /// <summary>How long the loop must stay quiet (no events) after the
    /// run looks settled before we close the input channel. Generous
    /// because a single LLM call on aeon-mtp can take 10-15 seconds —
    /// during the call the event stream is silent.</summary>
    private const int QuietPeriodSeconds = 20;

    /// <summary>Once accept_dispatch / reject_dispatch has fired AND the
    /// leader has reached user_input_needed, the run is operationally
    /// done — what comes after is the leader narrating to the user.
    /// Short-circuit after this much quiet so verbose final-message
    /// chains don't blow up wall-clock. Independent of QuietPeriodSeconds.</summary>
    private const int PostSettlementQuietSeconds = 5;

    /// <summary>Hard cap on cleanup wait after we close the input
    /// channel. After this we force-cancel and abandon — better to
    /// publish results than block forever.</summary>
    private const int CleanupTimeoutSeconds = 8;

    /// <summary>Tier 1B Q4 — hard cap on the post-settle self-assessment
    /// turn. The follow-up is one chat turn (no dispatches, no tools);
    /// 60s is generous for the model to produce a confidence + reason.</summary>
    private const int SelfAssessmentTimeoutSeconds = 60;

    /// <summary>Quiet period after the self-assessment input_needed
    /// fires. Shorter than the main run's settle because the follow-up
    /// is a single-turn response — no dispatched member to wait on.</summary>
    private const int SelfAssessmentQuietSeconds = 4;

    /// <summary>
    /// The Tier 1B Q4 follow-up question. ONE literal, shared by both ways of
    /// asking it (the live-leader channel push and the direct call made when the
    /// leader's loop has already returned). Two copies of a prompt are two
    /// instruments wearing one name: the answers would be pooled under a single
    /// column while the question drifted apart.
    /// </summary>
    internal const string SelfAssessmentPrompt =
        "SELF-ASSESSMENT REQUIRED. Do not narrate. Do not summarize what you did. Do not say anything else. Reply with EXACTLY these two lines and nothing else:\n\n" +
        "CONFIDENCE: <integer 0-100>\n" +
        "REASON: <one sentence about whether your work passed the tests>\n\n" +
        "Example response (and exactly this format — no preamble):\n" +
        "CONFIDENCE: 80\n" +
        "REASON: The fix is minimal and the existing tests cover the change.";

    /// <summary>
    /// Ask the self-assessment question when the leader's agent loop has ALREADY
    /// RETURNED — the normal healthy exit since `finish` became a real terminator.
    ///
    /// The channel-push path cannot work here: nothing is reading the channel, so
    /// the write lands in a buffer and the wait for a fresh user_input_needed
    /// never ends. What IS still available is the leader's full conversation
    /// (<see cref="AgentResult.Messages"/>), so ask the same question the same way
    /// the loop would have: append it as a user turn and take ONE completion.
    ///
    /// Deliberately NOT an agent loop — no tools are offered, so this cannot
    /// edit the workspace, cannot dispatch, and cannot change the graded state.
    /// It is a question, and it is scored as one.
    ///
    /// ⛔ THE MODEL MUST BE THE LEADER'S. `ds-team-lead-pro` binds the leader to a
    /// different model than the profile's base; asking the base model and filing
    /// the answer under the leader's row would attribute one model's calibration
    /// to another. Resolution goes through TeamCoordinator.ResolveClient — the same
    /// function the leader itself was built with — rather than a second copy of
    /// the rule here.
    /// </summary>
    // internal, not private, so the capture-outcome tests can drive the three
    // branches (answered / refused_empty / ask_failed) against a stub client.
    // Widened rather than re-implemented in the test: a second copy of this
    // logic would be two instruments wearing one name.
    internal static async Task<TeamBenchSelfAssessment> AskSelfAssessmentDirectAsync(
        Profile profile,
        IChatClient baseClient,
        string baseModel,
        IReadOnlyList<ChatMessage> leaderHistory,
        ILogger logger,
        string instanceId,
        CancellationToken ct)
    {
        var leaderLlmConfig = profile.Team?.Leader?.Llm;
        IChatClient client = baseClient;
        string model = baseModel;
        var ownsClient = false;

        try
        {
            // INSIDE the try, deliberately. When the leader overrides any
            // client-shaping field, ResolveClient CONSTRUCTS a new client, and
            // construction throws on an incomplete config (no endpoint for a
            // local provider, a missing api-key env var). Sitting above the
            // try, that throw escaped this method entirely — so an OPTIONAL
            // end-of-run diagnostic could take down a run whose work was
            // already finished and whose assertions had already passed.
            // A failed question is not a failed run; that principle has to
            // cover failing to BUILD the questioner too, not just the call.
            (client, model) = TeamCoordinator.ResolveClient(
                profile.Llm, baseClient, baseModel, leaderLlmConfig);
            ownsClient = !ReferenceEquals(client, baseClient);

            var req = new List<ChatMessage>(leaderHistory) { Chat.User(SelfAssessmentPrompt) };
            var opts = new ChatOptions
            {
                ModelId = model,
                Temperature = (float)(leaderLlmConfig?.Temperature ?? profile.Llm.Temperature ?? 1.0),
                TopP = (leaderLlmConfig?.TopP ?? profile.Llm.TopP) is { } tp ? (float)tp : null,
            };

            // Bounded by the SAME budget the channel path used, so the two ways
            // of asking cannot differ in how long a run is allowed to spend
            // answering. Linked to `ct` so Ctrl+C still wins.
            using var askCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            askCts.CancelAfter(TimeSpan.FromSeconds(SelfAssessmentTimeoutSeconds));

            var resp = await client.GetResponseAsync(req, opts, askCts.Token);
            var text = resp.Messages.Count > 0 ? resp.Messages[^1].GetText().Trim() : "";

            // `captured` means A REPLY CAME BACK, not that it parsed. An empty
            // completion is the model declining to answer, which is exactly the
            // state Captured=false exists to name — and is distinguishable from
            // "answered, but not in the requested format" (Captured=true with a
            // null Confidence and the RawText kept for diagnosis).
            var gotText = !string.IsNullOrWhiteSpace(text);
            return ParseSelfAssessment(
                text, captured: gotText, outcome: gotText ? "answered" : "refused_empty");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed question is not a failed run. Say so out loud, though —
            // a silent Captured=false here is indistinguishable from a model
            // that refused, and the analyzer would pool the two.
            logger.LogWarning(
                "[{Id}] self-assessment follow-up call failed ({Type}: {Message}); recording captured=false.",
                instanceId, ex.GetType().Name, ex.Message);
            // ask_failed, NOT refused_empty. The call never reached the model,
            // so this says nothing about the model's calibration — and it is
            // most likely on long runs, where the seeded history is largest.
            return new TeamBenchSelfAssessment { Captured = false, CaptureOutcome = "ask_failed" };
        }
        finally
        {
            if (ownsClient && client is IDisposable d) d.Dispose();
        }
    }

    /// <summary>Parse the agent's follow-up text for `CONFIDENCE: N`
    /// and `REASON: <sentence>`. Tolerant: case-insensitive, allows
    /// whitespace, picks the LAST match in case the agent emits its
    /// thinking-out-loud and then the formatted answer. Null fields
    /// when not found.</summary>
    // internal so the parser tests bind at COMPILE time. They used to reach
    // it by reflection "so we don't have to widen accessibility"; the cost was
    // that adding a parameter broke 8 tests at RUN time with a
    // TargetParameterCountException instead of failing the build. A test that
    // binds by string name cannot be type-checked against the thing it tests.
    internal static TeamBenchSelfAssessment ParseSelfAssessment(string text, bool captured, string outcome)
    {
        var sa = new TeamBenchSelfAssessment { Captured = captured, CaptureOutcome = outcome };
        if (string.IsNullOrWhiteSpace(text)) return sa;
        sa.RawText = text.Length > 2000 ? text[..2000] : text;

        var confMatches = System.Text.RegularExpressions.Regex.Matches(text,
            @"CONFIDENCE\s*:\s*(\d{1,3})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (confMatches.Count > 0)
        {
            var last = confMatches[^1];
            if (int.TryParse(last.Groups[1].Value, out var c) && c >= 0 && c <= 100)
            {
                sa.Confidence = c;
                sa.PredictedPass = c >= 50;
            }
        }

        var reasonMatches = System.Text.RegularExpressions.Regex.Matches(text,
            @"REASON\s*:\s*(.+?)(?:\r?\n|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (reasonMatches.Count > 0)
        {
            var last = reasonMatches[^1];
            var reason = last.Groups[1].Value.Trim();
            if (reason.Length > 0) sa.Reasoning = reason.Length > 500 ? reason[..500] : reason;
        }

        return sa;
    }

    /// <summary>
    /// Classify why the settle loop stopped, from the three facts the
    /// harness can actually observe. Pure and total so the mapping has a
    /// failure test — an invariant is only as real as its failure test,
    /// and this one exists specifically because the three not-settled
    /// states were previously collapsed into one string.
    ///
    /// Precedence is deliberate: a FAULT outranks EXHAUSTION outranks the
    /// wall clock, because each earlier cause makes the later observation
    /// a bystander. A faulted leader is also an ended leader; an ended
    /// leader also eventually hits the clock. Reporting the outermost
    /// symptom is exactly the mis-labelling this function exists to stop.
    /// </summary>
    /// <param name="settled">The settle loop took its normal exit.</param>
    /// <param name="leaderEnded">The leader's agent-loop task has completed
    /// (ran to completion, faulted, or was cancelled).</param>
    /// <param name="leaderFaulted">That task completed by throwing.</param>
    /// <param name="leaderStopReason">The leader loop's OWN
    /// <see cref="AgentResult.StopReason"/>, when it completed successfully;
    /// null when it faulted or is still running. Distinguishes a leader that
    /// ENDED ON PURPOSE from one that ran out of iterations — see
    /// <see cref="CleanLeaderStopReasons"/>.</param>
    /// <param name="harnessCancelled">The settle loop was cut short by the
    /// OUTER cancellation token — the harness stopped the run; the run did not
    /// stop itself. Everything the leader does after that point is a
    /// consequence of our own cancel, so this outranks
    /// <paramref name="leaderFaulted"/> (but not a clean
    /// <paramref name="leaderStopReason"/>, which a cancel cannot produce).
    /// A run classified <c>harness_cancelled</c> is VOID, not FAILED.</param>
    public static string ClassifyStop(
        bool settled, bool leaderEnded, bool leaderFaulted, string? leaderStopReason = null,
        bool harnessCancelled = false)
    {
        // Settled wins outright: the run reached its normal exit, and what
        // the leader task is doing during the cleanup handshake is not a
        // verdict about the run.
        if (settled) return "settled";

        // ⭐ THE SAME COLLAPSE AGAIN, ONE LEVEL FURTHER OUT.
        //
        // When the settle loop is cut short by the OUTER ct, everything that
        // happens next is a CONSEQUENCE of that cancellation, not evidence
        // about the run: cleanup cancels runCts, runCts kills a leader that
        // was healthy and mid-iteration, and the leader dutifully emits
        // `cancelled reason=external`. Read from the outside, that looks
        // exactly like a leader that died on its own.
        //
        // This branch sits ABOVE leaderFaulted for that reason. A leader we
        // cancelled ourselves is not a crashed leader, and labelling it one
        // blames the model for the harness's exit.
        //
        // MEASURED 2026-08-26 (team-fanout-tier2/fan5, ds-team-lead-pro): the
        // run ended at 340.2s against a 1200s budget. The settle loop exited
        // with quiet ≈ 0.66s — below BOTH quiet thresholds, so the normal
        // `break` at the bottom of the loop was NOT the exit taken. 8.67s
        // later — CleanupTimeoutSeconds is 8 — the leader emitted `cancelled`
        // with reason=external at iteration 14, and that was the last record
        // in the log.
        //
        // Without this branch that run falls through to `wall_clock_timeout`
        // and gets the error string "timeout after 1200s without settle" — a
        // sentence naming a duration that DEMONSTRABLY DID NOT ELAPSE. Worse,
        // `wall_clock_timeout` is the one label that invites raising the cap,
        // and raising a cap to re-score a run is barred. A wrong label here
        // does not just misinform, it points at a forbidden action.
        //
        // This does NOT diagnose WHY the ct fired. Two known causes: the user
        // pressed Ctrl+C, and System.CommandLine's CLI ct firing spuriously
        // mid-action (verified 2026-05-08; the workarounds at the `runCts`
        // construction and the MCP connect both exist for it, and the settle
        // loop was the one path left exposed). Both are the harness stopping
        // the run, and NEITHER is a measurement of the model — which is the
        // distinction this label has to carry. A run wearing it is VOID, not
        // FAILED, and must not be pooled with model results.
        // ⭐ THE SAME COLLAPSE THIS FUNCTION EXISTS TO END, ONE LEVEL DOWN.
        //
        // `leaderEnded` is "the task object completed" — it says nothing
        // about WHY. Before 2026-08-25 every ended-and-not-settled leader
        // was called `leader_loop_exhausted`, which was safe only while the
        // leader had no way to end deliberately: the completion marker was
        // inert (submit_detector was missing from every profile's resolved
        // chain), so the loop really could only stop by running out of road.
        //
        // Wiring submit_detector made deliberate termination the NORMAL
        // path, and this line immediately began reporting healthy runs as
        // exhausted. Measured on e1-bump-csproj-version: a PASS in 7 leader
        // iterations, against a cap of 200, labelled `leader_loop_exhausted`.
        // An iteration count nowhere near the cap is the tell — but the
        // label is what a reader sees, and readers read rows.
        //
        // This sits ABOVE harness_cancelled while leaderFaulted sits BELOW it,
        // and the asymmetry is the whole point. A CANCELLATION CANNOT
        // MANUFACTURE A CLEAN STOP REASON: cancelling a leader yields
        // `cancelled`, never finish_tool / sequential_complete / completed. So
        // one of those on the record is positive evidence that the leader had
        // already finished its work before the ct fired, and voiding that run
        // would throw away a completed one. A FAULT, by contrast, is exactly
        // what our own cancellation produces — which is why it yields and this
        // does not.
        //
        // ⛔ THE `!leaderFaulted` GUARD IS NOT DECORATION — IT BREAKS A CYCLE.
        // Three orderings are each independently right: a clean finish beats a
        // cancel (a cancel can't fabricate one), a cancel beats a fault (a
        // cancel DOES fabricate those), and a fault beats a clean finish (pinned
        // since 2026-08-25 so a crash can never wear the healthiest string).
        // Together they are circular. The cycle is only reachable on an input
        // that cannot occur — leaderStopReason is read off the task ONLY when
        // IsCompletedSuccessfully, so a faulted leader always arrives here with
        // null — but the classifier is total, and moving this branch up without
        // the guard silently flipped the pinned fault case. Excluding the
        // impossible input here is what lets all three real rules hold at once.
        if (leaderEnded && !leaderFaulted && leaderStopReason is not null
            && CleanLeaderStopReasons.Contains(leaderStopReason))
            return "leader_finished";

        if (harnessCancelled) return "harness_cancelled";

        if (leaderFaulted) return "leader_faulted";

        if (leaderEnded) return "leader_loop_exhausted";
        return "wall_clock_timeout";
    }

    /// <summary>
    /// Stop reasons that mean the leader's loop ended BECAUSE IT DECIDED TO,
    /// as opposed to hitting a limit or breaking. Enumerated from the actual
    /// <c>Finalize(state, …)</c> call sites rather than guessed — a predicate
    /// over a guessed vocabulary returns a confident zero.
    ///
    /// Deliberately does NOT include <c>stuck:monologue</c> /
    /// <c>stuck:action_error_loop</c> (the stuck detector overruling the
    /// agent), <c>max_iterations</c> (the cap), the error reasons
    /// (<c>llm_error</c>, <c>empty_response</c>, <c>empty_response_exhausted</c>,
    /// <c>malformed_tool_call</c>), or the external ones (<c>cancelled</c>,
    /// <c>user_closed</c>, <c>user_interrupted</c>). Those all still classify
    /// as <c>leader_loop_exhausted</c>, which keeps their existing meaning:
    /// the loop stopped for a reason the run should be judged on.
    /// </summary>
    /// ⚠ THE MEMBERS MOVED TO <see cref="AgentResult.CleanStopReasons"/> and
    /// this is now a POINTER, not a second copy. The dispatch path needs the
    /// same judgement (a member that ended on <c>llm_error</c> must not be
    /// relayed to its leader as done), and two copies of a word list is how
    /// the bench and the runtime come to disagree about what "finished" means.
    public static IReadOnlySet<string> CleanLeaderStopReasons => AgentResult.CleanStopReasons;

    /// <summary>
    /// True when the leader's loop RAN TO COMPLETION and said so with one of
    /// <see cref="CleanLeaderStopReasons"/>. Faulted and cancelled tasks are
    /// excluded by <c>IsCompletedSuccessfully</c>, so a crash can never be
    /// read as a clean finish.
    /// </summary>
    public static bool LeaderFinishedCleanly(Task<AgentResult>? runTask)
        => runTask is { IsCompletedSuccessfully: true }
           && CleanLeaderStopReasons.Contains(runTask.Result.StopReason);

    /// <summary>The thread id the leader's (or the solo agent's) iterations
    /// are counted under. This is the value <c>TeamCoordinator</c> stamps on
    /// untagged leader events (Coordinator.cs:1046) and the one
    /// <see cref="TeamBenchResult.LeaderIterations"/> is built from.</summary>
    public const string LeaderThreadId = "main";

    /// <summary>
    /// Which iteration counter an event belongs to: <see cref="LeaderThreadId"/>
    /// for the leader (or the sole agent on the solo path), the member's
    /// thread id for a dispatched member, and null when the event is not an
    /// <c>iteration_start</c> at all.
    ///
    /// WHY AN UNTAGGED EVENT IS THE LEADER: only the TEAM path tags events.
    /// TeamCoordinator wraps leader events with thread_id="main"
    /// (Coordinator.cs:1043-1049) and member events with the task id
    /// (Coordinator.cs:361-372). The SOLO path in
    /// <see cref="RunInstanceAsync"/> hands <c>OnEvent</c> straight to
    /// <see cref="Vett.Agent.AgentEnvironment"/>, and AgentLoop's own Emit
    /// (AgentLoop.cs:293-294) adds no thread_id, so its iteration_start
    /// (AgentLoop.cs:489) carries only <c>{"iteration": N}</c>.
    ///
    /// Before 2026-08-24 the untagged case matched NEITHER branch and was
    /// counted nowhere. Every solo team-bench run therefore published
    /// <c>leader_iterations: 0</c> — a COULD-NOT-MEASURE laundered into a
    /// MEASURED-ZERO — and every <c>budget: leader_iters_max</c> assertion
    /// on a solo suite passed vacuously, because AssertionEngine.EvalBudget
    /// (AssertionEngine.cs:382-383) only fails when the count EXCEEDS the
    /// cap and 0 never exceeds one. suites/spec-authoring-tier0.yaml,
    /// -tier0-5.yaml and -tier0-oneshot.yaml all run solo profiles
    /// (spec-authoring-solo / spec-authoring-oneshot, neither of which has a
    /// <c>team:</c> block) and all carry exactly that assertion.
    ///
    /// Pure and total so the mapping has a failure test — same reasoning as
    /// <see cref="ClassifyStop"/>: an invariant is only as real as its
    /// failure test.
    /// </summary>
    public static string? IterationThreadId(Event ev)
    {
        if (ev.Type != "iteration_start") return null;
        if (ev.Data.TryGetValue("thread_id", out var tid) && tid is string s && s.Length > 0)
            return s;
        return LeaderThreadId;
    }

    /// <summary>
    /// Stable, greppable token stamped into <see cref="TeamBenchResult.Error"/>
    /// (and therefore into the published <c>error</c> field of the JSON wire
    /// format, JsonWire.cs) whenever the iteration counts alongside it are
    /// LOWER BOUNDS rather than measurements.
    ///
    /// It exists because the types cannot say this themselves:
    /// <see cref="TeamBenchResult.LeaderIterations"/> and
    /// <c>TeamBenchJsonRun.leader_iterations</c> are both plain <c>int</c>,
    /// and <see cref="TeamBenchResult.MemberIterations"/> is a plain
    /// (non-nullable) dictionary. There is no value in either domain that
    /// means "not measured" — 0 and {} are the only things an aborted run
    /// can put there, and they are indistinguishable from a run that
    /// genuinely did nothing. Widening those to <c>int?</c> / adding an
    /// explicit partial flag is the real fix and is a Models.cs +
    /// JsonWire.cs change; until then this marker is the discriminator, and
    /// it rides the one published field this file owns.
    ///
    /// Consumers: slice on <c>error?.Contains(PartialCountsMarker)</c>
    /// BEFORE pooling any iteration count. A run carrying this marker must
    /// never be pooled with completed runs, and its 0 must never be read as
    /// "did nothing."
    /// </summary>
    public const string PartialCountsMarker = "ITERATION_COUNTS_PARTIAL";

    /// <summary>
    /// Copy the live iteration counters onto the result, and — when the run
    /// did NOT reach the point where the counters are final — say so in the
    /// published <see cref="TeamBenchResult.Error"/> string.
    ///
    /// WHY THIS IS A NAMED, PURE FUNCTION rather than three lines inlined in
    /// <see cref="RunInstanceAsync"/>: the previous code assigned the
    /// counters as the LAST statements of the try block, so any throw
    /// between workspace creation and the assertion call discarded them and
    /// the result published <c>leader_iterations: 0</c>. A run that did 40
    /// real iterations and then threw contributed 0 to every total while
    /// LOOKING like an exact count — the same COULD-NOT-MEASURE-laundered-
    /// into-a-MEASURED-ZERO defect that <see cref="IterationThreadId"/>
    /// documents for the solo path. Moving the assignment into a
    /// <c>finally</c> stops the loss but does NOT fix the lie: a finally
    /// that writes 0 still publishes a number that claims to be a
    /// measurement. Both halves have to move together, so both live here,
    /// behind one call, with a failure test.
    ///
    /// Pure and total — same reasoning as <see cref="ClassifyStop"/> and
    /// <see cref="IterationThreadId"/>: an invariant is only as real as its
    /// failure test, and a fact recorded in a <c>finally</c> is exactly the
    /// kind that never gets exercised by the happy path.
    /// </summary>
    /// <param name="result">Result being published. Mutated in place.</param>
    /// <param name="leaderIterations">Leader/solo count observed so far.</param>
    /// <param name="memberIterations">Per-member counts observed so far.
    /// Copied defensively; null is treated as "none observed".</param>
    /// <param name="complete">True only when the run reached the point where
    /// the counters stop moving (the agent loop was given its full settle +
    /// cleanup handshake). False on every abort path.</param>
    public static void PublishIterationCounts(
        TeamBenchResult result,
        int leaderIterations,
        IReadOnlyDictionary<string, int>? memberIterations,
        bool complete)
    {
        result.LeaderIterations = leaderIterations;
        result.MemberIterations = memberIterations is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(memberIterations, StringComparer.Ordinal);

        if (complete) return;

        var members = result.MemberIterations.Count == 0
            ? "none observed"
            : string.Join(", ", result.MemberIterations
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}>={kv.Value}"));

        var note = $"{PartialCountsMarker}: the run aborted before the iteration counters "
                 + $"were final, so leader_iterations>={leaderIterations} and "
                 + $"member_iterations[{members}] are LOWER BOUNDS, not measurements. "
                 + "A 0 here means COULD-NOT-MEASURE, not measured-zero. Do not pool "
                 + "these counts with completed runs and do not evaluate a budget "
                 + "assertion against them.";

        result.Error = string.IsNullOrEmpty(result.Error) ? note : result.Error + " | " + note;
    }

    /// <summary>
    /// Did this event represent a review that ACTUALLY HAPPENED — an
    /// `accept_dispatch` / `reject_dispatch` call that RETURNED SUCCESSFULLY?
    ///
    /// ⛔ WHY `success` IS NOT OPTIONAL HERE. The settle gate decrements
    /// `pendingReviews` on every `tool_call_end` naming one of those two
    /// tools, reading neither `success` nor any task correlation — while
    /// `AgentLoop` emits `tool_call_end` with `["success"] = false` on a
    /// tool exception, on a soft failure (an "Error:" result), and on both
    /// hook-denial paths. The disambiguating key is PRESENT IN THE VERY
    /// DICTIONARY BEING READ AND IGNORED. The increment side is guarded (it
    /// requires `pending_review == true`); only the decrement was not.
    ///
    /// Failure scenario: the leader calls `accept_dispatch` with a stale
    /// task_id, the tool throws, `pendingReviews` goes to -1, a second
    /// implementer dispatch brings it back to 0, and the gate
    /// `if (pending > 0) continue;` passes with one genuinely unreviewed
    /// worktree outstanding. The harness then scores a workspace missing
    /// that member's work.
    ///
    /// The same key was ignored three lines below, where `anyReviewSettled`
    /// flips the quiet threshold from 20s to 5s: a FAILED review call cut
    /// the settle window by 4x as though work had been reviewed. Both sites
    /// now ask this one question.
    ///
    /// FAIL DIRECTION, deliberately chosen: a review whose success cannot be
    /// established does NOT count. The gate then holds and the run burns its
    /// wall clock — a visible timeout — instead of settling early on a
    /// workspace missing a member's work, which is a silent wrong verdict.
    /// All four `tool_call_end` emit sites in AgentLoop carry `success`
    /// (:1469, :1516, :1588, :1608) and RunCommand.cs:195 already reads it,
    /// so an absent key means a NEW emitter that has not been audited — the
    /// one case where holding is exactly right.
    /// </summary>
    public static bool IsSuccessfulReview(Event ev)
        => ev.Type == "tool_call_end"
        && ev.Data.TryGetValue("tool_name", out var tn)
        && tn is string name
        && (name == "accept_dispatch" || name == "reject_dispatch")
        && ev.Data.TryGetValue("success", out var ok)
        && ok is true;

    /// <summary>
    /// Retire one outstanding review, CLAMPED AT ZERO. Returns true if a
    /// pending review was actually retired, false if there was none to retire.
    ///
    /// WHY THE CLAMP IS SEPARATE FROM THE SUCCESS GATE. Gating on `success`
    /// stops the FAILED-call route to a negative counter; it does not stop
    /// every route, because the counter carries NO TASK CORRELATION. Two
    /// successful `accept_dispatch` calls naming the same task — a leader
    /// retrying after a response it did not see land, say — both decrement,
    /// and the second one has no pending review to retire.
    ///
    /// Below zero the counter stops meaning "nothing outstanding" and starts
    /// meaning CREDIT AGAINST A FUTURE DISPATCH: the next real dispatch
    /// increments -1 to 0, and the settle gate `if (pending > 0) continue;`
    /// reads "nothing outstanding" over a worktree nobody reviewed. The
    /// clamp costs nothing when the counter is honest and removes that state
    /// entirely when it is not.
    ///
    /// SAFE BECAUSE THE INCREMENT STRICTLY PRECEDES THE DECREMENT, verified
    /// in code rather than assumed: Coordinator.cs:695-735 fires the
    /// `dispatch_end` event carrying `pending_review` BEFORE :740-754 returns
    /// the AgentResult that lets the leader see the dispatch at all, so the
    /// leader cannot review a dispatch the counter has not yet counted. The
    /// clamp therefore cannot strand a legitimate decrement.
    /// </summary>
    public static bool DecrementPendingReviews(ref int pendingReviews)
    {
        while (true)
        {
            var cur = Volatile.Read(ref pendingReviews);
            if (cur <= 0) return false;
            if (Interlocked.CompareExchange(ref pendingReviews, cur - 1, cur) == cur) return true;
        }
    }

    /// <summary>
    /// Wait for the agent loop to finish after the input channel is closed,
    /// and report WHETHER IT ACTUALLY FINISHED.
    ///
    /// THE DISTINCTION THIS EXISTS TO MAKE: "the task finished" and "we
    /// stopped waiting for the task" are not the same event, and only the
    /// first one stops the iteration counters. <c>WaitAsync</c> timing out
    /// does not cancel, kill or even inconvenience <paramref name="loop"/> —
    /// it keeps executing, <c>OnEvent</c> keeps firing, and
    /// <c>leaderIters</c>/<c>memberIters</c> keep moving WHILE THE CALLER
    /// READS THEM. Anything published from that state is a lower bound.
    ///
    /// ⛔ Before 2026-08-24 the caller set <c>countersComplete = true</c>
    /// unconditionally after this wait — the comment on that very line named
    /// the abandoned case ("or abandoned with a cleanup warning already
    /// recorded above") and then treated it as final anyway. So on the one
    /// path where <see cref="PublishIterationCounts"/>'s whole apparatus was
    /// needed, <see cref="PartialCountsMarker"/> could never fire: a
    /// COULD-NOT-MEASURE was published wearing the face of a measurement,
    /// which is the exact defect that apparatus was built to prevent.
    ///
    /// The three non-timeout outcomes all mean the task REACHED AN END —
    /// cancellation observed, or a fault — so the counters are final even
    /// though the run itself went badly. Only the timeout is unmeasured.
    ///
    /// ⚠ KNOWN PROOF GAP, MEASURED rather than estimated. Two halves:
    ///
    ///   THE CLASSIFICATION below is proven two-sided — reverting the timeout
    ///   branch to <c>(true, ...)</c> turns 3 named tests red
    ///   (HarnessPartialCountsTests, 2026-08-24).
    ///
    ///   THE WIRING of the abandoned half was recorded here as "NOT proven and
    ///   currently CANNOT BE". ⛔ **BOTH HALVES OF THAT ARE NOW RETRACTED
    ///   (2026-08-27).** What stands: reverting the caller to the old
    ///   unconditional <c>countersComplete = true</c> produced ZERO red, so the
    ///   SUITE still does not pin the wiring. What falls is the reason given —
    ///   the claim that the branch is unreachable through
    ///   <see cref="RunInstanceAsync"/> because "every blocking path in the
    ///   real loop honours the token".
    ///
    ///   IT FIRES. Measured 2026-08-27: 1 full-suite run in 20 (default
    ///   verbosity, all 1221 tests), HarnessPartialCountsTests
    ///   .Counters_survive_a_mid_run_abort went red on the marker, and the test
    ///   took 10s — exactly <see cref="CleanupTimeoutSeconds"/>+2, i.e. the
    ///   TimeoutException branch below and nothing else. Honouring the token is
    ///   not sufficient: under a loaded thread pool the loop can fail to be
    ///   SCHEDULED to observe it inside the grace period. The branch is a
    ///   CONTENTION outcome, not a misbehaving-loop outcome, and it is reachable
    ///   on ordinary hardware.
    ///
    /// ⛔ THE "NEVER OBSERVED" ARGUMENT WAS CIRCULAR, and this is the part worth
    /// remembering. The old text ended: "No artifact in the repo has ever
    /// carried the 'did not unwind' string, so this is a defensive branch."
    /// The caller wrote this message with <c>result.Error ??= unwindError</c>,
    /// which discards it whenever the run already recorded an error — and the
    /// cancellation path ALWAYS has. So no artifact could ever carry the string,
    /// whether or not the branch fired. The absence measured the recording line,
    /// not the branch: a COULD-NOT-RECORD read as a NEVER-HAPPENED. The caller
    /// now appends (see the note at the call site).
    ///
    /// The SETTLED half of the wiring IS covered (HarnessStopReasonWiringTests
    /// catches an inverted or hard-coded-false flag).
    /// </summary>
    /// <param name="loop">The agent-loop task, already signalled to stop.</param>
    /// <param name="cleanupSeconds">Grace period; the wait allows this +2s.</param>
    /// <returns><c>Unwound</c> false ONLY when the task was abandoned still
    /// running. <c>Error</c> is the message to record, or null.</returns>
    public static async Task<(bool Unwound, string? Error)> AwaitLoopUnwindAsync(
        Task loop, int cleanupSeconds)
    {
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(cleanupSeconds + 2), CancellationToken.None);
            return (true, null);
        }
        catch (TimeoutException)
        {
            // ABANDONED, NOT UNWOUND. The task is still running right now.
            return (false, $"agent loop did not unwind within {cleanupSeconds}s");
        }
        catch (OperationCanceledException)
        {
            // Expected: the run's CTS was cancelled and the loop OBSERVED it,
            // so the task has completed and the counters have stopped.
            return (true, null);
        }
        catch (Exception ex)
        {
            // The awaited task faulted, i.e. it ran to an end (badly). The
            // counters are final; the error is about the run's outcome, not
            // about our ability to measure it.
            return (true, $"agent loop threw: {ex.Message}");
        }
    }

    public static Task<TeamBenchResult> RunInstanceAsync(
        TeamBenchInstance instance,
        Profile profile,
        string endpoint,
        string model,
        string? apiKey,
        ILogger logger,
        CancellationToken ct)
        => RunInstanceAsync(instance, profile, endpoint, model, apiKey, logger, ct, workspaceOverride: null);

    /// <summary>
    /// Overload with <paramref name="workspaceOverride"/> for Q3
    /// decomposition chains. When non-null, the harness adopts that dir
    /// AS-IS instead of creating a fresh workspace; seed files are NOT
    /// re-applied (caller owns the state from prior chain steps).
    /// Workspaces passed this way are NEVER auto-deleted on PASS.
    /// </summary>
    public static async Task<TeamBenchResult> RunInstanceAsync(
        TeamBenchInstance instance,
        Profile profile,
        string endpoint,
        string model,
        string? apiKey,
        ILogger logger,
        CancellationToken ct,
        string? workspaceOverride)
    {
        var sw = Stopwatch.StartNew();
        var result = new TeamBenchResult { InstanceId = instance.Id };
        // ⭐ PROCESS-SCOPED. This is the dispatch worktree PANEL ID and the
        // sandbox session name; it used to be the bare "team-bench-" +
        // instance.Id, which is the SAME STRING for every run of a given suite
        // instance on this machine. Two `vett team-bench` processes therefore
        // aimed at one directory under the per-user ~/.vett/dispatches root —
        // MEASURED 2026-08-26: the width-10 Flash and Pro runs shared 13 panel
        // ids. See RunScope for the full reasoning and why the existing
        // collision-recovery path is a backstop rather than a substitute.
        //
        // ⚠ SCOPE: this separates PROCESSES, not repeats. `--repeat N` still
        // reuses one id across its N sequential runs of an instance, so a later
        // repeat's pre-clean removes an earlier one's worktree — invisible
        // normally, but it means --keep-all-worktrees keeps only the LAST
        // repeat. Unchanged here on purpose; the concurrency defect and the
        // repeat-retention defect are separate claims and shouldn't be bundled.
        var benchSessionId = RunScope.Qualify("team-bench-" + instance.Id);
        var memberIters = new Dictionary<string, int>(StringComparer.Ordinal);
        int leaderIters = 0;
        // Flips true once the agent loop has been given its full settle +
        // cleanup handshake, i.e. once the counters stop moving. Read in the
        // finally to decide whether the counts we publish are measurements or
        // lower bounds. Deliberately NOT set after the assertion call: if
        // AssertionEngine.Evaluate throws, the counters really are final and
        // saying otherwise would be its own mis-label.
        var countersComplete = false;

        string? workspace = null;
        var workspaceAdopted = workspaceOverride is not null;
        // Per-run JSONL log of every event the loop emitted. Lives next
        // to the workspace dir for failure post-mortems — when an
        // assertion fails, the user can read this to see exactly what
        // the model did. Vett's chat-panel writes a similar log
        // automatically for chat sessions; the bench harness has to
        // write its own since it doesn't go through the chat-panel
        // pipeline.
        // ⛔ MUST BE SYNCHRONIZED — OnEvent IS CALLED CONCURRENTLY.
        //
        // This was a plain StreamWriter until 2026-08-26. StreamWriter is
        // documented as NOT thread-safe, and OnEvent below is invoked from
        // every member thread at once: one leader turn issuing N assign_async
        // calls puts N member agent loops on separate threads, all emitting
        // into this one writer with no lock.
        //
        // With a single member the races effectively never fire, which is why
        // this survived 61 runs of the 2026-08-26 sweep — every one of them
        // had a max fan-out of ONE. The first real 5-way fan-out produced a
        // visibly corrupt log: two byte-identical `dispatch_start` lines for
        // implementer-4, sharing a timestamp to the 100ns tick (one dispatch,
        // written twice), while implementer-1 lost BOTH its closing
        // `iteration_end` and its `dispatch_end` — events that are otherwise
        // never separated.
        //
        // THE INSTRUMENT WAS THE THING UNDER TEST. This file is the only
        // post-mortem record a failed bench run leaves, so a fan-out bug and
        // a fan-out LOGGING bug present identically: counts that don't
        // balance. Any conclusion drawn from start/end counts in a log
        // written before this fix is unsafe — dispatch accounting has to be
        // re-measured, not re-read.
        //
        // TextWriter.Synchronized wraps every write in a lock. AutoFlush stays
        // on: a killed run must leave a readable log.
        TextWriter? sessionLog = null;
        string? sessionLogPath = null;
        try
        {
            workspace = workspaceOverride is not null
                ? WorkspaceSetup.Adopt(workspaceOverride)
                : WorkspaceSetup.Create(instance.Workspace, instance.SeedFiles);
            sessionLogPath = Path.Combine(workspace, "_bench-session.jsonl");
            sessionLog = TextWriter.Synchronized(
                new StreamWriter(sessionLogPath, append: false) { AutoFlush = true });
            result.SessionLogPath = sessionLogPath;

            var bash = new DirectBash(workspace);
            var client = ChatClientFactory.Create(profile.Llm, endpoint, model, apiKey);
            // JOIN THE MODEL NAME BACK UP -- THE ONE CLI PATH THAT COULD NOT DO IT
            // ITSELF. The other five commands fold and then read the model off the
            // client they just built; TeamBenchCommand cannot, because the client is
            // built HERE, several frames below where `model` was resolved. So a
            // capability profile reached this line with model = "" (correct -- the
            // profile names no model, the catalogue does), and `new LlmSettings(client,
            // model, ...)` at the bottom of this method put ChatOptions.ModelId = "" on
            // every request. That is the exact failure C2 of PREREG-2026-08-28 hit on
            // its second run ("Empty encoded value"), on a path that prereg never
            // exercised. Found by re-reading the six fold sites afterwards, not by a
            // failing run -- so it is fixed here and pinned by CapacityWiringTests
            // rather than claimed as measured.
            model = ChatClientFactory.EffectiveModel(client, model);

            // Fixed-capacity unbounded channel for the user input — we'll
            // push the prompt once, then close after settling. Bounded
            // would needlessly add backpressure for a one-shot benchmark.
            var inputChan = Channel.CreateUnbounded<string>();

            // Capture every emitted event. ConcurrentQueue because
            // dispatched members emit from background tasks.
            var events = new System.Collections.Concurrent.ConcurrentQueue<Event>();
            var lastEventAt = DateTime.UtcNow;
            var sawInputNeeded = false;
            // Track in-flight dispatches so we don't settle while a
            // member is mid-LLM-call. Each dispatch_start / dispatch_end
            // pair brackets one member run.
            var dispatchesInFlight = 0;
            // Pending reviews — dispatches that ended with pending_review=true
            // (worktree-isolated implementer/researcher with edits) and have
            // NOT yet had accept_dispatch / reject_dispatch fired against them.
            // Only these block settlement; researcher dispatches return with
            // pending_review=false and need no acceptance.
            var pendingReviews = 0;

            void OnEvent(Event ev)
            {
                events.Enqueue(ev);
                lastEventAt = DateTime.UtcNow;

                // Mirror to the per-run JSONL session log for post-mortem.
                if (sessionLog is not null)
                {
                    try
                    {
                        var record = new Dictionary<string, object?>
                        {
                            ["ts"] = DateTime.UtcNow.ToString("o"),
                            ["type"] = ev.Type,
                            ["data"] = ev.Data,
                        };
                        sessionLog.WriteLine(JsonSerializer.Serialize(record));
                    }
                    catch { /* best-effort; don't break the run on a log write error */ }
                }

                var tid = IterationThreadId(ev);
                if (tid is not null)
                {
                    if (tid == LeaderThreadId) Interlocked.Increment(ref leaderIters);
                    else
                    {
                        lock (memberIters)
                        {
                            memberIters.TryGetValue(tid, out var n);
                            memberIters[tid] = n + 1;
                        }
                    }
                }
                else if (ev.Type == "dispatch_start")
                {
                    Interlocked.Increment(ref dispatchesInFlight);
                }
                else if (ev.Type == "dispatch_end")
                {
                    Interlocked.Decrement(ref dispatchesInFlight);
                    // Researcher dispatches return pending_review=false (no
                    // worktree to apply); implementer dispatches return
                    // pending_review=true and require accept/reject before
                    // the leader's turn can be considered finished.
                    if (ev.Data.TryGetValue("pending_review", out var pr)
                        && pr is bool b && b)
                    {
                        Interlocked.Increment(ref pendingReviews);
                    }
                }
                else if (IsSuccessfulReview(ev))
                {
                    DecrementPendingReviews(ref pendingReviews);
                }
            }

            // Collect leader-to-user text (final assistant turns with no
            // tool calls). Used by `assistant_text_contains` assertions
            // to verify the leader actually relayed answers from members.
            var assistantTexts = new List<string>();
            void OnAssistantText(string text)
            {
                if (!string.IsNullOrEmpty(text)) lock (assistantTexts) assistantTexts.Add(text);
            }
            // AgentLoop.RunInteractiveAsync invokes onWaitingForInput TWICE
            // by design: once at session start before any user input
            // arrives, then again after each completed leader turn.
            // Treating both as "leader is done" makes the settle loop
            // fire at t=~20s (during the leader's silent first LLM call,
            // no events updating lastEventAt), which then schedules
            // runCts.CancelAfter and kills the implementer dispatch
            // mid-flight a few seconds later. Skip the first invocation;
            // only the post-turn one is meaningful.
            var inputNeededHits = 0;
            void OnInputNeeded()
            {
                Interlocked.Increment(ref inputNeededHits);
                if (inputNeededHits > 1) sawInputNeeded = true;
                lastEventAt = DateTime.UtcNow;
            }

            // Drive the chat in the background so we can monitor the
            // event stream for settle/timeout from the foreground.
            //
            // NOT linked to the outer ct — System.CommandLine's CLI ct
            // appears to cancel mid-action under some scenarios (verified
            // 2026-05-08: a fresh bench run had its agent loop killed at
            // t=5s with reason=external, before settle even fired). The
            // harness owns this CTS exclusively; we cancel it ourselves
            // during cleanup or when the wall-clock deadline trips. The
            // outer ct is still used for the settle loop's user-cancel
            // path (Ctrl+C still works on the bench command).
            using var runCts = new CancellationTokenSource();

            // MCP server pool. Chat mode wires this at ChatCommand.cs:687-696;
            // team-bench had no equivalent until 2026-07-05, which meant
            // `mcp_servers:` blocks were silently ignored during team-bench
            // runs. That invalidated the analyzer M5 A/B (2026-07-05 finding).
            // Now: connect every configured server before starting the
            // coordinator, merge its tool functions + schemas into the
            // extraTools/extraSchemas dicts, dispose when the run ends.
            using var mcpPool = new McpClientPool(logger);
            var extraTools = new Dictionary<string, ToolFn>(StringComparer.Ordinal);
            var extraSchemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (profile.McpServers is { Count: > 0 })
            {
                try
                {
                    // Use a NEW cts for MCP connect — the outer `ct` sometimes
                    // fires mid-startup under System.CommandLine (see the runCts
                    // rationale further down for the same story), and a canceled
                    // handshake looks like an MCP failure. Give MCP its own
                    // 60s budget scoped only to connect.
                    using var mcpConnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                    // Ensure MCP subprocesses inherit workspace as their cwd.
                    // Process.Start uses Directory.GetCurrentDirectory() when
                    // ProcessStartInfo.WorkingDirectory is unset. Without this,
                    // servers that read files relative to `.` (e.g. spec-diff
                    // reading current_spec.json from the workspace root) fail
                    // because they end up running from vett's launch cwd.
                    // Analyzer sidesteps this because Q1-Q7 tools take explicit
                    // file paths per call; other servers may not.
                    var savedCwd = Directory.GetCurrentDirectory();
                    Directory.SetCurrentDirectory(workspace);
                    try
                    {
                        await mcpPool.ConnectAllAsync(profile.McpServers, mcpConnectCts.Token);
                    }
                    finally
                    {
                        Directory.SetCurrentDirectory(savedCwd);
                    }
                    foreach (var (name, fn) in mcpPool.ToolFunctions)
                        extraTools[name] = fn;
                    foreach (var s in mcpPool.ToolSchemas)
                    {
                        // Tool schemas are named `mcp__<server>__<tool>` inside
                        // the "function.name" field; that name IS the dict key.
                        if (s.TryGetProperty("function", out var fn2)
                            && fn2.TryGetProperty("name", out var n)
                            && n.ValueKind == JsonValueKind.String)
                        {
                            extraSchemas[n.GetString()!] = s;
                        }
                    }
                    Console.Error.WriteLine($"[mcp] connected {mcpPool.ConnectedServerCount} server(s), {extraTools.Count} tools available.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[mcp] connect failed: {ex.GetType().Name}: {ex.Message}");
                    logger.LogWarning("MCP pool connect failed: {Message}. Continuing without analyzer tools.", ex.Message);
                }
            }

            Task<AgentResult> runTask;
            if (profile.Team is not null)
            {
                runTask = TeamCoordinator.RunInteractiveAsync(
                    profile, client, model, bash, benchSessionId,
                    inputChan.Reader,
                    OnAssistantText,
                    OnInputNeeded,
                    OnEvent,
                    seedHistory: null,
                    turnInterrupt: null,
                    compactRequest: null,
                    cwd: workspace,
                    runCts.Token,
                    extraTools: extraTools.Count > 0 ? extraTools : null,
                    extraSchemas: extraSchemas.Count > 0 ? extraSchemas : null);
            }
            else
            {
                // Fallback path for non-team profiles. Merge MCP extraTools
                // + extraSchemas into the single-agent capabilities so
                // mcp_servers work for solo profiles too. Mirrors the
                // pattern in Coordinator.cs:750-751 (team path). Without
                // this, MCP tool schemas were advertised to the LLM but
                // the dispatcher had no handler, so mcp__ calls silently
                // never generated tool_call_start events — the run hung
                // waiting for a response that would never come.
                var soloTools = Builtins.All();
                foreach (var kv in extraTools) soloTools[kv.Key] = kv.Value;
                var soloSchemas = Helpers.LoadSchemas(profile.Tools);
                soloSchemas.AddRange(extraSchemas.Values);

                // ⛔ THIS USED TO BE `new List<MiddlewareFn>()` — a HARDCODED
                // EMPTY CHAIN, which did two wrong things at once:
                //
                //   1. It silently DISCARDED the profile's own `middleware:`
                //      block. A solo profile could declare output_truncation
                //      and observation_elision and get neither, with nothing
                //      logged. Config that is read, parsed, and then dropped
                //      reads exactly like config that works.
                //   2. It left `finish` with NO READER. `finish` returns a
                //      __VETT_SUBMIT__ marker and SubmitDetector is the only
                //      thing that acts on it, so on this path the marker was
                //      an inert string: the agent submitted and the loop kept
                //      going to max_iterations. Same defect as the profile-level
                //      one MiddlewareResolver now force-fixes, just reached
                //      through a different branch — which is why the fix belongs
                //      in the resolver and this call site simply USES it.
                //
                // ResolveOrDefault: an empty/absent list still falls back to
                // Builtins.DefaultMiddleware(), so profiles that declare nothing
                // now get the standard chain rather than nothing at all.
                // Constructed BEFORE caps because the resolver needs it — see
                // the note below. It used to be built after, which is part of
                // why `llm` was never threaded in.
                var llmSettings = new LlmSettings(client, model,
                    profile.Llm.Temperature ?? 1.0,
                    profile.Llm.TopP,
                    profile.Llm.MaxOutputTokens,
                    profile.Llm.PresencePenalty,
                    profile.Llm.FrequencyPenalty);

                // ⛔ 2026-08-26 — `compaction` was passed here but `llm` was
                // not, so `llm_summarizing_condenser` never registered and the
                // name was silently skipped. The threshold was honored while the
                // strategy it was tuning did not exist: a profile could carry a
                // carefully chosen threshold_tokens and compact never. Both
                // ds-solo-* profiles name the condenser as their only strategy.
                //
                // The resolver now degrades a declared compaction strategy to
                // milestone_checkpoint rather than dropping it, so this path was
                // no longer silent even before `llm` was threaded — but passing
                // `llm` is what gets the profile the strategy it actually asked
                // for. agent_finished_critic stays excluded by name, not by
                // starving the whole resolver of `llm`.
                var caps = new AgentCapabilities(
                    soloTools,
                    soloSchemas,
                    MiddlewareResolver.ResolveOrDefault(
                        profile.Middleware, extras: null, llm: llmSettings,
                        compaction: profile.Compaction,
                        onDiagnostic: msg => OnEvent(new Event("middleware_diagnostic", new()
                        {
                            ["instance_id"] = instance.Id,
                            ["message"] = msg,
                        }))));
                var env = new AgentEnvironment(bash, benchSessionId,
                    profile.MaxIterations > 0 ? profile.MaxIterations : 100,
                    OnEvent);
                runTask = AgentLoop.RunInteractiveAsync(
                    llmSettings, caps, env, profile.SystemPrompt,
                    inputChan.Reader, OnAssistantText, OnInputNeeded,
                    seedHistory: null, turnInterrupt: null,
                    compactRequest: null, ct: runCts.Token);
            }

            // Send the prompt as the single user turn.
            await inputChan.Writer.WriteAsync(instance.Prompt, ct);

            // Settle loop: every 500ms check the wall-clock timeout +
            // whether we look "done." A run is done when:
            //   - the leader has signalled user_input_needed (its turn
            //     is complete), AND
            //   - no dispatch is currently in flight (otherwise we'd
            //     abort a member mid-LLM-call), AND
            //   - no pending reviews are outstanding (every dispatch_end
            //     with pending_review=true has been accept/reject'd —
            //     researcher dispatches return pending_review=false and
            //     don't block here), AND
            //   - the event stream has been quiet for QuietPeriodSeconds.
            var deadline = DateTime.UtcNow.AddSeconds(instance.TimeoutSeconds);
            var settled = false;
            var anyReviewSettled = false;  // tracks if accept/reject ever fired, for post-settlement short-circuit
            // Set when the OUTER ct ends this loop, by either of its two exits:
            // the `while` condition observing it, or the delay below throwing.
            // Tracked rather than thrown so the run still reaches the assertion
            // pass — see the tolerant delay.
            var harnessCancelled = false;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                // ⛔ DO NOT LET THIS THROW OUT OF THE LOOP.
                //
                // `Task.Delay(500, ct)` throwing was not merely an untidy exit:
                // it jumped straight past AssertionEngine.Evaluate to the
                // catch-all at the bottom of this method. The run then carried
                // an EMPTY AssertionResults list, and `Pass` requires
                // `Count > 0` — so a cancelled run was scored FAIL with the
                // error "harness error: The operation was canceled.", no matter
                // what the members had actually built in the workspace. The
                // work is still sitting on disk and is still gradeable; the
                // only thing the cancellation removed was our willingness to
                // look at it. That is a COULD-NOT-MEASURE being laundered into
                // a MEASURED-FAIL, and it lands on the model's record.
                //
                // Breaking instead lets the rest of the method run: the
                // classifier gets `harnessCancelled`, the assertions get
                // evaluated against the real workspace, and the run is scored
                // on evidence rather than on the manner of its exit.
                //
                // Ctrl+C stays responsive — this exits the loop on the first
                // tick after cancellation, exactly as the throw did.
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    harnessCancelled = true;
                    break;
                }
                var quiet = (DateTime.UtcNow - lastEventAt).TotalSeconds;

                // ⭐ TWO WAYS A RUN IS OVER; THE HARNESS ONLY KNEW ONE.
                //
                // (1) user_input_needed — the leader emitted a tool-call-less
                //     turn and parked. This was the ONLY exit, and it worked
                //     only BECAUSE the completion marker was inert: the leader
                //     submitted, kept iterating, eventually went quiet, and the
                //     harness read the silence as "done".
                //
                // (2) the leader's loop RETURNED. Wiring submit_detector means
                //     declare_done sets StopLoop and AgentLoop returns at the
                //     stop boundary (AgentLoop.cs:1197) — it never reaches the
                //     onWaitingForInput path at all. So the STRONGER, explicit
                //     completion signal produced NO settle, and the harness sat
                //     out the whole wall clock waiting for a callback that could
                //     no longer arrive. Measured on e1-bump-csproj-version: PASS
                //     in 7 leader iterations, 421s wall, ~360s of it dead wait.
                //
                // Once the leader task is done, NOTHING further can change any
                // of the conditions below except an in-flight dispatch draining,
                // so continuing to wait is futile by construction, not merely
                // slow. `leaderIsDone` is tracked SEPARATELY from `settled` on
                // purpose: a faulted or exhausted leader also stops the wait,
                // but must NOT be relabelled "settled" — that would hide a crash
                // behind the healthiest string in the vocabulary.
                var leaderIsDone = runTask.IsCompleted;
                if (!sawInputNeeded && !leaderIsDone) continue;

                if (Interlocked.CompareExchange(ref dispatchesInFlight, 0, 0) > 0) continue;

                // pendingReviews is decremented ONLY by the leader's own
                // accept_dispatch / reject_dispatch. If the leader's loop has
                // returned, nothing can ever clear it — blocking on it would
                // reproduce the exact dead wait this change removes, just behind
                // a different counter. Unreviewed work is still a real defect;
                // it simply gets caught by grading (the edits aren't in the
                // workspace, so the assertions fail) rather than by stalling.
                if (!leaderIsDone)
                {
                    var pending = Interlocked.CompareExchange(ref pendingReviews, 0, 0);
                    if (pending > 0) continue;
                }
                // Same question as the decrement above: a review that THREW
                // is not a review. Before this, a failed accept_dispatch cut
                // the quiet threshold from 20s to 5s as though the work had
                // been reviewed.
                if (!anyReviewSettled && events.Any(IsSuccessfulReview))
                {
                    anyReviewSettled = true;
                }
                // Two settle thresholds:
                //   - Post-settlement (accept/reject already fired): short
                //     5s window so a verbose leader narrating its final
                //     response doesn't block the bench. The work is done.
                //   - Otherwise (no dispatch, or researcher-only dispatch):
                //     standard 20s window to avoid aborting mid-LLM-call.
                //
                // The 20s window buys ONE thing: not aborting the leader in the
                // middle of an LLM call, during which the event stream is
                // legitimately silent. Once `leaderIsDone` there is no leader
                // call to abort, and `dispatchesInFlight == 0` was already
                // established above, so the only thing 20s of silence can still
                // measure is a member's closing event racing its own decrement.
                // Keep the short drain for that; stop paying the long one for a
                // system that is provably finished. At ~20s x 25 instances the
                // long window was costing a suite ~8 minutes of nothing.
                var threshold = (anyReviewSettled || leaderIsDone)
                    ? PostSettlementQuietSeconds
                    : QuietPeriodSeconds;
                if (quiet < threshold) continue;

                // `settled` means THE CLASSIC EXIT HAPPENED — the leader parked
                // at user_input_needed — not merely "we stopped waiting". When
                // the loop is leaving only because the leader's task returned,
                // this stays false so ClassifyStop can report what the leader
                // actually did (finished / exhausted / faulted). Setting it
                // unconditionally here would map every returned leader,
                // INCLUDING A CRASHED ONE, onto "settled".
                settled = sawInputNeeded;
                break;
            }

            // THE LOOP HAS TWO CANCELLATION EXITS AND THE CATCH IS ONLY ONE.
            //
            // `catch (OperationCanceledException)` above covers the case where
            // the token trips DURING the 500ms delay. But the `while` condition
            // also tests `!ct.IsCancellationRequested`, and that exit runs no
            // catch at all: a token tripping while the loop body is between the
            // delay and the next condition check falls out here with the flag
            // still false. The run would then be classified against a stale
            // `harnessCancelled = false` and, being neither settled nor
            // leader-finished, be labelled `wall_clock_timeout` — printing a
            // deadline that had not elapsed and inviting a cap raise, which is
            // the exact mislabelling this whole change exists to remove.
            //
            // `|=` and not `=`: the catch above may already have set it, and a
            // token can be cancelled without this loop being what observed it.
            // Reading the token here is safe precisely because it is monotonic
            // — once requested it never un-requests, so a late read cannot
            // produce a false negative.
            harnessCancelled |= ct.IsCancellationRequested;

            // THE LABEL ON A FAILURE IS ITSELF A MEASUREMENT AND CAN BE WRONG.
            //
            // Measured on armA-run4 (2026-08-24): the leader recorded EXACTLY
            // 200 iterations against a team.leader.max_iterations of EXACTLY
            // 200, having dispatched ONE member across 7261s where this suite
            // normally runs 3-14. Every other run in the 9-run census sat at
            // 21-77. The run was reported as "timeout after 7200s without
            // settle" — the same string a genuinely slow-but-alive run gets.
            //
            // The mechanism: if the leader's agent loop ENDS (iteration cap
            // reached, or it faults) before signalling user_input_needed, then
            // `sawInputNeeded` never goes true, so the settle loop above can
            // never take its exit — it just spins at 500ms until the wall
            // clock expires. A DEAD leader and a BUSY leader produce a
            // byte-identical verdict, and the run enters the cohort as a cap
            // finding when it is actually an exhaustion fault.
            //
            // So ask the leader task directly. This is the liveness conjunct:
            // "did not settle" is not one state, it is three, and they have
            // different owners — the model, the config, and the code.
            var leaderEnded = runTask is not null && runTask.IsCompleted;
            var leaderFaulted = leaderEnded && runTask!.IsFaulted;
            // Only read StopReason off a task that completed SUCCESSFULLY —
            // .Result on a faulted task rethrows, and on a cancelled one throws
            // TaskCanceledException. LeaderFinishedCleanly guards this too; the
            // local exists so the classifier gets the reason even when it is a
            // non-clean one (max_iterations, stuck:*, llm_error…), which is what
            // keeps `leader_loop_exhausted` meaning what it always meant.
            var leaderResult = runTask is { IsCompletedSuccessfully: true } ? runTask.Result : null;
            var leaderStopReason = leaderResult?.StopReason;
            result.StopReason = ClassifyStop(settled, leaderEnded, leaderFaulted, leaderStopReason, harnessCancelled);

            // Record what the label was computed FROM, not just the label.
            // See TeamBenchResult.StopEvidence: `wall_clock_timeout` is printed
            // both by a leader that was genuinely still working and by one that
            // had already stopped but whose Task had not been observed complete
            // at this instant, and after the fact the two are identical.
            result.StopEvidence =
                $"settled={settled.ToString().ToLowerInvariant()} "
                + $"leader_ended={leaderEnded.ToString().ToLowerInvariant()} "
                + $"leader_faulted={leaderFaulted.ToString().ToLowerInvariant()} "
                + $"harness_cancelled={harnessCancelled.ToString().ToLowerInvariant()} "
                + $"leader_stop_reason={leaderStopReason ?? "(none)"}";

            // A leader that ended ON PURPOSE is not a failure and must not get
            // an Error string. Before this, wiring submit_detector made every
            // healthy run take the `leaderEnded` branch below and acquire an
            // error reading "ENDED … without signalling user_input_needed" —
            // a true sentence describing correct behaviour as a fault.
            var leaderFinishedCleanly = LeaderFinishedCleanly(runTask);

            if (!settled && !leaderFinishedCleanly)
            {
                if (harnessCancelled)
                {
                    // Mirrors the classifier's ordering, and for the same
                    // reason: whatever the leader did after we cancelled it is
                    // not a fact about the leader. Naming the ELAPSED time
                    // beside the budget is the load-bearing part — it is what
                    // stops a reader (or me, later) from treating a 340s stop
                    // under a 1200s budget as a cap finding and reaching for
                    // the one remedy that is barred.
                    result.Error = $"run CANCELLED BY THE HARNESS after {(int)sw.Elapsed.TotalSeconds}s " +
                                   $"of a {instance.TimeoutSeconds}s budget — the outer cancellation token " +
                                   "fired, so the run was stopped from the outside rather than finishing, " +
                                   "timing out, or failing. Known causes: Ctrl+C, and System.CommandLine " +
                                   "cancelling mid-action. This run is VOID — do not pool it with model " +
                                   "results, and DO NOT raise the timeout: the budget was never reached. " +
                                   "Assertions below were still evaluated against the real workspace.";
                }
                else if (leaderFaulted)
                {
                    var inner = runTask!.Exception?.GetBaseException();
                    result.Error = $"leader loop FAULTED after {(int)sw.Elapsed.TotalSeconds}s " +
                                   $"without signalling user_input_needed: {inner?.GetType().Name}: {inner?.Message}. " +
                                   "This is a CRASH, not a timeout — do not score it as a cap or model result.";
                }
                else if (leaderEnded)
                {
                    // ⛔ THIS STRING USED TO END "…then the harness waited out the
                    // remaining wall clock ({TimeoutSeconds}s)". That was true of
                    // the code that wrote it and is FALSE of the code that ships
                    // it: the settle loop now exits once the leader's task is
                    // done. Left as-is it would have kept telling every reader
                    // that a fixed dead wait was still happening — a retraction
                    // that doesn't reach the string literal keeps shipping.
                    result.Error = $"leader loop ENDED after {leaderIters} iterations with stop_reason=" +
                                   $"'{leaderStopReason ?? "(none)"}' — not one of the deliberate exits " +
                                   "(finish_tool / sequential_complete / completed). If that iteration " +
                                   "count equals the profile's team.leader.max_iterations, this is " +
                                   "ITERATION EXHAUSTION, not a timeout — the clock is a bystander. " +
                                   "Do not pool it with wall-clock timeouts.";
                }
                else
                {
                    // The honest wall-clock case: the leader is STILL RUNNING
                    // and simply did not finish in time. This is the only one
                    // of the three that the old string described correctly.
                    result.Error = $"timeout after {instance.TimeoutSeconds}s without settle";
                }
                logger.LogWarning("[{Id}] {StopReason}: {Error}", instance.Id, result.StopReason, result.Error);
            }

            // Tier 1B Q4 — self-assessment capture. We try this on
            // BOTH settled and timed-out runs. Skipping on timeout
            // produced silent failures: of the 2 A1 minor-edit fails
            // on 2026-05-12, both were timeouts and both had zero
            // self-assessment captured — making failure-flagging
            // recall vacuously undefined instead of measurable.
            //
            // On timeout the agent loop may be unresponsive (mid-tool-
            // call when the deadline fired); pushing the follow-up
            // prompt is best-effort. If the channel rejects, or the
            // agent never signals user_input_needed within the
            // per-self-assessment 60s window, we record
            // Captured=false — but we record an *attempt*, which is
            // visibly different from "we didn't try."
            // ⭐ THE SECOND DEAD WAIT, SAME FAMILY AS THE FIRST — and the one
            // that actually dominated the clock.
            //
            // This block asks the follow-up question by WRITING TO THE LEADER'S
            // INPUT CHANNEL and then waiting for a NEW user_input_needed. Both
            // halves assume the leader's loop is still running and still reading
            // that channel. Once `finish` became a real terminator, the normal
            // healthy run reaches here with the loop already RETURNED: the write
            // succeeds (the channel just buffers it), nothing ever reads it, and
            // the wait below spins the full SelfAssessmentTimeoutSeconds for a
            // signal that cannot arrive.
            //
            // ARITHMETIC, not inference: the finish-path wiring test measured
            // 80.5s against a 60s deadline — 20s of settle quiet plus exactly
            // this 60s. Two futile waits stacked; the settle loop was only the
            // smaller half.
            //
            // Worse than slow, it was SILENT DATA LOSS. Captured=false is what
            // "we asked and got nothing" looks like, so every healthy run would
            // have started reporting the same value that "the model refused to
            // self-assess" reports. COULD NOT MEASURE is not MEASURED ZERO.
            //
            // So branch on whether a reader still exists, and keep the
            // measurement on both live branches:
            //   - leader still running  → the original channel push. Unchanged.
            //   - leader returned WITH a history → ask its own model directly,
            //     one bounded call, no tools. Same question, same model, same
            //     conversation — just not routed through a dead channel.
            //   - leader faulted/cancelled → there is no history and no agent to
            //     ask. Record not-captured IMMEDIATELY rather than waiting 60s
            //     to record the identical thing.
            //
            // ⛔ AND IT IS OPTIONAL, SO IT MUST NOT BE ABLE TO DELETE THE RUN.
            //
            // Two paths in here take the outer `ct`: AskSelfAssessmentDirectAsync
            // deliberately RETHROWS on cancellation (:154), and the settle loop
            // below awaits `Task.Delay(500, ct)`. Either throw lands in the
            // catch-all at the bottom of this method — which sits PAST
            // AssertionEngine.Evaluate. So a cancel arriving during a DIAGNOSTIC
            // WE DO NOT GRADE ON discarded the grading of the work we DO grade
            // on, and the run came back with an empty AssertionResults list,
            // i.e. Pass=false. The file already states the principle six
            // hundred lines up — "a failed question is not a failed run" — but
            // it was only ever applied to BUILDING the questioner, never to
            // being interrupted while waiting on the answer.
            //
            // Catch it here, record the not-captured with its own outcome, and
            // let control reach the assertions. StopReason is deliberately NOT
            // recomputed: it was fixed above, and by then the run's work was
            // already over. A cancel during the post-mortem is not evidence
            // about the run — the same reason `settled` outranks everything in
            // the classifier.
            try
            {
                if (!ct.IsCancellationRequested && leaderEnded)
                {
                    result.SelfAssessment = leaderResult is not null
                        ? await AskSelfAssessmentDirectAsync(
                            profile, client, model, leaderResult.Messages,
                            logger, instance.Id, ct)
                        : new TeamBenchSelfAssessment { Captured = false, CaptureOutcome = "no_leader" };
                }
                else if (!ct.IsCancellationRequested)
                {
                    var preFollowUpHits = Interlocked.CompareExchange(ref inputNeededHits, 0, 0);
                    int preTextCount;
                    lock (assistantTexts) preTextCount = assistantTexts.Count;
                    var followUpPrompt = SelfAssessmentPrompt;

                    // ⭐ SNAPSHOT BEFORE THE PUSH, and it is load-bearing for the
                    // recovery below. `leaderEnded` was computed at :1240; the
                    // leader can finish between there and here. If its loop had
                    // ALREADY returned when we wrote to the channel, then its
                    // final word answers some EARLIER question — it cannot be an
                    // answer to a follow-up it never saw. Reading it as one would
                    // manufacture a confidence number out of an unrelated turn,
                    // which is worse than the missing value this fix recovers.
                    var endedBeforePush = runTask is { IsCompleted: true };
                    bool pushed = false;
                    try
                    {
                        await inputChan.Writer.WriteAsync(followUpPrompt, ct);
                        pushed = true;
                    }
                    catch { /* channel already closed or canceled — skip capture */ }

                    if (pushed)
                    {
                        lastEventAt = DateTime.UtcNow;
                        var selfDeadline = DateTime.UtcNow.AddSeconds(SelfAssessmentTimeoutSeconds);
                        var selfSettled = false;
                        var leaderEndedDuringCapture = false;
                        while (DateTime.UtcNow < selfDeadline && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(500, ct);

                            // ⭐ THE SECOND WAY THE FOLLOW-UP IS OVER — the one this
                            // loop did not know. It is the SAME omission, with the
                            // same consequence, that :1135 already fixed in the
                            // OUTER settle loop; that fix did not reach here.
                            //
                            // A leader answering with declare_done / finish trips
                            // SubmitDetector -> StopLoop, so AgentLoop returns at
                            // the stop boundary (AgentLoop.cs:1197) and NEVER
                            // reaches onWaitingForInput (:1128). inputNeededHits
                            // therefore cannot grow, and the condition below can
                            // never be met. Spinning to the deadline is futile BY
                            // CONSTRUCTION, not merely slow: measured at a full
                            // 60s of dead wait per instance, on the leaders that
                            // obey their prompt most exactly.
                            //
                            // Tracked SEPARATELY from `selfSettled` for the same
                            // reason :1141 tracks leaderIsDone separately: a
                            // FAULTED leader also ends the wait, but must not be
                            // relabelled as having settled — that would hide a
                            // crash behind the healthiest string in the vocabulary.
                            //
                            // ⛔ AND `IsCompletedSuccessfully` IS NOT THAT TEST. It
                            // means THE LOOP RETURNED, not that it succeeded: a loop
                            // that gives up with StopReason "llm_error" returns an
                            // AgentResult perfectly normally, and an earlier draft of
                            // this fix therefore recorded a crashed leader as having
                            // settled — then recovered its PRE-PUSH turn and reported
                            // a confidence number for a question it never saw. Caught
                            // by the self_assessment_capture line below, which read
                            // `leader_stop=llm_error` next to `settled=True`.
                            //
                            // LeaderFinishedCleanly (:370) is the predicate that
                            // already exists for exactly this, and its own comment
                            // says why it must be the only one: "two copies of a word
                            // list is how the bench and the runtime come to disagree
                            // about what 'finished' means."
                            if (runTask is { IsCompleted: true })
                            {
                                leaderEndedDuringCapture = true;
                                selfSettled = LeaderFinishedCleanly(runTask);
                                break;
                            }

                            var hitsNow = Interlocked.CompareExchange(ref inputNeededHits, 0, 0);
                            if (hitsNow <= preFollowUpHits) continue;
                            var quiet = (DateTime.UtcNow - lastEventAt).TotalSeconds;
                            if (quiet < SelfAssessmentQuietSeconds) continue;
                            selfSettled = true;
                            break;
                        }

                        // Pull any new assistant text emitted since the prompt was pushed.
                        string followUpText;
                        lock (assistantTexts)
                        {
                            followUpText = assistantTexts.Count > preTextCount
                                ? string.Join("\n", assistantTexts.Skip(preTextCount))
                                : "";
                        }

                        // ⭐ THE ANSWER CAN BE IN A TOOL CALL, AND THE SINK ABOVE
                        // CANNOT SEE IT. `assistantTexts` is fed by OnAssistantText,
                        // which collects (its own comment, :872) "final assistant
                        // turns with NO TOOL CALLS". Meanwhile SelfAssessmentPrompt
                        // (:66) demands two bare lines of prose while ALL TEN
                        // shipping team profiles forbid exactly that — "never write
                        // CONFIDENCE/REASON as a plain chat message, they go INSIDE
                        // the declare_done summary argument". The leader cannot obey
                        // both, and the one it is more likely to obey is its SYSTEM
                        // prompt. So the better-behaved the leader, the more reliably
                        // its answer was dropped.
                        //
                        // FinalTurnText() is the same shared helper site 3 uses
                        // (AgentLoop.cs:98) rather than a second unwrapper here, so
                        // every reader of a submitted final word agrees on which
                        // message is final and on the one marker constant involved.
                        //
                        // Guarded on IsCompletedSuccessfully: .Result rethrows on a
                        // faulted task and throws on a cancelled one. Guarded on
                        // !endedBeforePush: see the snapshot above.
                        //
                        // ⛔ AND GUARDED ON THE STOP REASON, which the first version of
                        // this fix did not do — caught by the self_assessment_capture
                        // line below on a run where the probe's own stub was serving
                        // malformed JSON. IsCompletedSuccessfully means THE LOOP
                        // RETURNED, not that it succeeded: a loop that gives up with
                        // StopReason "llm_error" returns an AgentResult perfectly
                        // normally. FinalTurnText then hands back the last assistant
                        // turn there IS — which, when the post-push turns all failed,
                        // is the PRE-PUSH one. That reported `answered` carrying text
                        // the leader wrote before it was ever asked the question:
                        // a confidence number manufactured from an unrelated turn,
                        // which is worse than the missing value being recovered.
                        //
                        // "finish_tool" is set by SubmitDetector (BuiltinMiddleware.cs:16)
                        // for the __VETT_SUBMIT__ marker, and it is the ONLY reader of
                        // that marker — so this is precisely "the loop stopped BECAUSE
                        // it submitted", for `finish` and `declare_done` alike, through
                        // one constant rather than two spellings.
                        var recoveredFromSubmit = false;
                        if (string.IsNullOrWhiteSpace(followUpText)
                            && !endedBeforePush
                            && runTask is { IsCompletedSuccessfully: true }
                            && runTask.Result.StopReason == "finish_tool")
                        {
                            var submitted = runTask.Result.FinalTurnText();
                            if (!string.IsNullOrWhiteSpace(submitted))
                            {
                                followUpText = submitted;
                                recoveredFromSubmit = true;
                            }
                        }

                        // A bare capture_outcome says WHAT was decided and nothing
                        // about WHY, which is the difference between a diagnosis and
                        // a label. These are the exact inputs the decision below is
                        // computed from, so a surprising outcome can be attributed
                        // without re-running under a debugger.
                        int postTextCount;
                        lock (assistantTexts) postTextCount = assistantTexts.Count;
                        logger.LogInformation(
                            "[{Id}] self_assessment_capture texts_before={Before} texts_after={After} "
                          + "hits_before={HitsBefore} hits_after={HitsAfter} settled={Settled} "
                          + "leader_ended_during={EndedDuring} ended_before_push={EndedBefore} "
                          + "recovered_from_submit={Recovered} leader_stop={LeaderStop} msgs={Msgs}",
                            instance.Id, preTextCount, postTextCount,
                            preFollowUpHits, Interlocked.CompareExchange(ref inputNeededHits, 0, 0),
                            selfSettled, leaderEndedDuringCapture, endedBeforePush, recoveredFromSubmit,
                            runTask is { IsCompletedSuccessfully: true } ? runTask.Result.StopReason : "<not-completed>",
                            runTask is { IsCompletedSuccessfully: true } ? runTask.Result.Messages.Count : -1);

                        // ⛔ `settle_timeout` NO LONGER ABSORBS A CRASH. capture_outcome
                        // exists precisely so `captured: false` does not pool distinct
                        // states (JsonWire.cs:54); a leader whose loop FAULTED during
                        // the follow-up never had the chance to answer, and reporting
                        // that as a timeout scores a harness fault as model behaviour
                        // — the exact confusion that field was added to prevent.
                        result.SelfAssessment = ParseSelfAssessment(
                            followUpText, selfSettled,
                            outcome: selfSettled
                                ? (string.IsNullOrWhiteSpace(followUpText) ? "refused_empty" : "answered")
                                : leaderEndedDuringCapture ? "leader_ended_unanswered"
                                : "settle_timeout");
                    }
                    else
                    {
                        result.SelfAssessment = new TeamBenchSelfAssessment
                        {
                            Captured = false,
                            CaptureOutcome = "channel_closed",
                        };
                    }
                }
                else
                {
                    // Cancelled BEFORE we could ask. Both guards above already
                    // skipped in this case, and the field was simply left null —
                    // indistinguishable from "this build doesn't capture". Record
                    // the non-attempt explicitly; a null that means three things
                    // is the collapse this whole file is about.
                    result.SelfAssessment = new TeamBenchSelfAssessment
                    {
                        Captured = false,
                        CaptureOutcome = "harness_cancelled",
                    };
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled MID-ASK. `??=` because the direct-ask path can have
                // already assigned before a later await threw — don't overwrite
                // a real capture with a not-captured.
                result.SelfAssessment ??= new TeamBenchSelfAssessment
                {
                    Captured = false,
                    CaptureOutcome = "harness_cancelled",
                };
                logger.LogWarning(
                    "[{Id}] self-assessment cancelled mid-capture; assertions still evaluated",
                    instance.Id);
            }

            // Close the input channel and unwind. Strictly bounded —
            // we'd rather publish a result and surface a cleanup-warning
            // than block forever waiting for the agent loop. The
            // assertion engine doesn't need the loop to fully finish;
            // it works against the captured event list + filesystem
            // state, both of which are valid the moment we settle.
            inputChan.Writer.TryComplete();
            runCts.CancelAfter(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
            // `runTask!` for the same reason :763 and :770 do: it is declared
            // non-nullable and assigned on both branches at :649-693, but the
            // defensive `is not null` test at :762 narrows the flow state to
            // maybe-null for everything after it.
            var (loopUnwound, unwindError) = await AwaitLoopUnwindAsync(runTask!, CleanupTimeoutSeconds);
            // APPEND, don't `??=`.
            //
            // ⛔ 2026-08-27. This was `result.Error ??= unwindError`, which drops
            // the cleanup warning whenever the run already recorded an error —
            // i.e. on exactly the paths where cleanup is most likely to go wrong.
            // The cancellation path always sets Error before reaching here, so on
            // that path the warning was structurally unable to survive.
            //
            // That is what made the doc claim at AwaitLoopUnwindAsync ("no
            // artifact in the repo has ever carried the 'did not unwind' string,
            // so this is a defensive branch") unfalsifiable: the absence was a
            // property of THIS LINE, not evidence about the branch. It was a
            // COULD-NOT-RECORD read as a NEVER-HAPPENED. The branch does fire —
            // measured 2026-08-27, 1 run in 20 of the full suite, and the only
            // surviving trace was the ITERATION_COUNTS_PARTIAL marker that
            // PublishIterationCounts appends (which correctly appends, and is
            // why the marker was visible while its reason was not).
            //
            // Same " | " join as PublishIterationCounts so a record can carry
            // the run's outcome AND every cleanup fact, in the order they
            // occurred.
            if (unwindError is not null)
            {
                result.Error = string.IsNullOrEmpty(result.Error)
                    ? unwindError
                    : result.Error + " | " + unwindError;
            }

            // Complete ONLY when the loop actually FINISHED — not merely when
            // we stopped waiting for it. On the abandoned path the counters
            // are still moving as we read them, so the finally publishes them
            // marked as LOWER BOUNDS instead of measurements. See
            // AwaitLoopUnwindAsync for why the other three outcomes are final.
            countersComplete = loopUnwound;

            var eventList = events.ToList();
            List<string> textsSnapshot;
            lock (assistantTexts) textsSnapshot = assistantTexts.ToList();
            // countersComplete is passed, not re-derived: a budget assertion
            // evaluated against LOWER BOUNDS cannot certify under-budget, and
            // the marker below already says so in the published error. Passing
            // it here is what turns that sentence into an enforced check.
            result.AssertionResults = AssertionEngine.Evaluate(
                instance.Assertions, eventList, workspace, leaderIters, memberIters,
                countersComplete, textsSnapshot);
            // NOTE: LeaderIterations / MemberIterations are NOT assigned here.
            // They are published in the finally below so they survive every
            // throw on the way here — see PublishIterationCounts.
        }
        catch (Exception ex)
        {
            result.Error = $"harness error: {ex.Message}";
            logger.LogError(ex, "[{Id}] harness threw", instance.Id);
        }
        finally
        {
            // Publish the iteration counters HERE, not at the end of the try.
            //
            // Before 2026-08-24 these two assignments were the last statements
            // of the try block. Every throw before them — WorkspaceSetup,
            // StreamWriter, ChatClientFactory, the `ct`-bearing Task.Delay in
            // either settle loop, AssertionEngine.Evaluate — discarded them, and
            // the result went out with leader_iterations: 0. A run that did 40
            // real iterations and then threw contributed 0 to the totals and
            // nothing in the output distinguished it from a run that did
            // nothing: a COULD-NOT-MEASURE laundered into a MEASURED-ZERO,
            // published as a silent lower bound wearing the face of an exact
            // count. WallClock (below) was already done correctly here; the
            // counters simply were not.
            //
            // `countersComplete` is what keeps the finally from telling its own
            // version of the same lie — writing 0 from a finally is no more
            // honest than losing the 40, unless the 0 is labelled.
            //
            // Read both counters the way OnEvent writes them: leaderIters via
            // Interlocked (it is Interlocked.Increment'ed from dispatched member
            // tasks) and memberIters under its lock. The old line copied the
            // dictionary with no lock at all, which could itself throw
            // InvalidOperationException if a member emitted concurrently — and
            // that throw landed on exactly the path that lost the counts.
            Dictionary<string, int> memberSnapshot;
            lock (memberIters) memberSnapshot = new Dictionary<string, int>(memberIters, StringComparer.Ordinal);
            PublishIterationCounts(
                result,
                Interlocked.CompareExchange(ref leaderIters, 0, 0),
                memberSnapshot,
                countersComplete);

            try { sessionLog?.Dispose(); } catch { }
            // Tear down workspace ONLY when we're confident the run is
            // done. Failures keep the dir AND its session log on disk
            // for post-mortem (path is surfaced via TeamBenchResult).
            //
            // `--keep-all-worktrees` (which sets profile.Team.DispatchRetention =
            // "keep-all") ALSO suppresses bench-workspace cleanup — otherwise
            // PASSed runs lose their `_bench-session.jsonl`, defeating the
            // whole point of cross-profile malformed-rate measurement that
            // motivates the flag.
            var keepAll = string.Equals(profile.Team?.DispatchRetention, "keep-all", StringComparison.Ordinal);
            // Solo profiles (no team block) keep workspaces on PASS by default:
            // --keep-all-worktrees only wires through when profile.Team exists
            // (TeamBenchCommand.cs:174), and solo probes exist explicitly to
            // score post-hoc from session logs — auto-deletion defeats the
            // whole purpose. This is a solo-path complement to the 2026-05-11
            // fix that made --keep-all-worktrees preserve team workspaces.
            var isSolo = profile.Team is null;
            // Adopted workspaces are caller-owned; never auto-delete
            // even on PASS — the caller (e.g. decomposition orchestrator)
            // chains subsequent runs against the same dir.
            if (!workspaceAdopted
                && !keepAll
                && !isSolo
                && workspace is not null
                && string.IsNullOrEmpty(result.Error)
                // Use the type's OWN definition of pass. This used to be a
                // second, hand-rolled `AssertionResults.All(r => r.Pass)`,
                // which is VACUOUSLY TRUE on an empty list — so an instance
                // that asserted nothing had its workspace and session log
                // deleted while TeamBenchResult.Pass (which does guard
                // Count > 0) reported it as a FAIL. An empty set is not a
                // falsified set; two definitions of "passed" is how they
                // drift apart.
                && result.Pass)
            {
                try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
            }
            result.WallClock = sw.Elapsed;
        }
        return result;
    }
}
