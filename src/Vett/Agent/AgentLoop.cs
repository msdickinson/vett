using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Vett.Llm;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Agent;

/// <summary>A thing that happened during the loop. Caller decides what to do with it.</summary>
public sealed record Event(string Type, Dictionary<string, object?> Data);

/// <summary>What the loop returns when it finishes.</summary>
public sealed class AgentResult
{
    public List<ChatMessage> Messages { get; init; } = [];
    public string StopReason { get; init; } = "";
    public int Iterations { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    /// <summary>False when the three counters above are NOT measurements.
    ///
    /// ⛔ COULD-NOT-MEASURE IS NOT MEASURED-ZERO. The counters are plain
    /// `int`, so a run that was killed before the loop could report them has
    /// no value in the domain meaning "unknown" — it can only say 0, which is
    /// also what a member that ran and did nothing says. This flag is the
    /// discriminator, so consumers can publish null instead of a fabricated
    /// zero and can leave the value out of sums.
    ///
    /// Default TRUE: every normal path through Finalize() really did measure,
    /// so only the synthesizing catch in Coordinator.RunMemberFull sets false.
    /// Same law `files_changed` already follows (Coordinator.cs:1100-1110).</summary>
    public bool CountersMeasured { get; init; } = true;

    /// <summary>
    /// The stop reasons that mean the loop FINISHED ON PURPOSE.
    ///
    /// ⛔ ENUMERATED FROM THE REAL <c>Finalize(state, …)</c> CALL SITES, NOT
    /// GUESSED — a predicate over a guessed vocabulary returns a confident
    /// zero. Everything else AgentLoop can finalize with is a stop the caller
    /// has to be TOLD about: the caps (<c>max_iterations</c>), the stuck
    /// detector (<c>stuck:*</c>), the error exits (<c>llm_error</c>,
    /// <c>empty_response</c>, <c>empty_response_exhausted</c>,
    /// <c>malformed_tool_call</c>), the external ones (<c>cancelled</c>,
    /// <c>user_closed</c>, <c>user_interrupted</c>) and the coordinator’s own
    /// synthesized <c>stalled</c>.
    ///
    /// ⚠ THIS LIVES ON THE RESULT TYPE ON PURPOSE. It was previously private
    /// to the bench harness, so the DISPATCH path had no notion of a non-clean
    /// member at all — see <c>Coordinator.RunMember</c>. Two consumers deciding
    /// "did this finish?" from two copies of a word list is exactly how they
    /// drift; <c>Harness.CleanLeaderStopReasons</c> now points here.
    /// </summary>
    public static readonly IReadOnlySet<string> CleanStopReasons =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "finish_tool",          // BuiltinMiddleware.cs:16 — declare_done / finish
            "sequential_complete",  // Coordinator.cs — every member ran
            "completed",            // AgentLoop.cs — model stopped calling tools
        };

    /// <summary>True when <see cref="StopReason"/> is one of
    /// <see cref="CleanStopReasons"/>. An empty stop reason is NOT clean:
    /// a result that never reached Finalize has not finished on purpose.</summary>
    public bool StoppedCleanly => CleanStopReasons.Contains(StopReason);

    /// <summary>
    /// The agent's FINAL WORD — the prose of its last assistant turn, or, when
    /// that turn was a bare tool call, the summary it SUBMITTED in it.
    ///
    /// ⛔ DELIBERATELY DOES NOT LOOK PAST THE LAST TURN, and that is the whole
    /// difference between this and <c>Coordinator.ExtractFinalAssistantText</c>,
    /// which does look back because a leader wants its member's best AVAILABLE
    /// report. This one feeds ABSTAIN classification
    /// (<c>ResolutionClassifier.cs:39</c>), where reaching further back would
    /// match a mid-run "I can't do X yet" that the agent then went on to solve
    /// and label a PASS as a refusal. A refusal can only be the LAST thing said.
    /// The two are separate functions on purpose; what they share — the marker
    /// — is already one constant (<c>Builtins.SubmitMarker</c>), so there is no
    /// vocabulary here to drift.
    ///
    /// MEASURED 2026-08-28: <c>finish</c>'s <c>message</c> argument is exactly
    /// where a model writes "I cannot solve this" (<c>BuiltinTools.cs:178</c>),
    /// and <c>Runner</c> read only TextContent — so such a run arrived at the
    /// classifier with a null final message, missed ABSTAIN, and fell through to
    /// FALSE_CONFIDENCE. A refusal was counted as a confident wrong answer,
    /// moving a run between the two buckets that get read.
    ///
    /// ⚠ <c>finish</c> DEFAULTS its message to "Task completed." when the model
    /// passes none, so a bare call yields TOOL-supplied text, not the model's.
    /// Harmless for ABSTAIN (it cannot match the refusal regex) but it is NOT
    /// evidence that the agent claimed anything.
    ///
    /// Returns null, never "", so a caller's <c>is not null</c> test keeps
    /// meaning "the agent said something".
    /// </summary>
    public string? FinalTurnText()
    {
        int lastAsst = -1;
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].IsRole(ChatRole.Assistant)) { lastAsst = i; break; }
        }
        if (lastAsst < 0) return null;

        var text = Messages[lastAsst].GetText();
        if (!string.IsNullOrEmpty(text)) return text;

        // Only the results of THAT turn's calls. Anything at or before
        // lastAsst belongs to an EARLIER turn, and a summary the agent
        // submitted and then continued past is not its final word.
        for (int i = Messages.Count - 1; i > lastAsst; i--)
        {
            if (!Messages[i].IsRole(ChatRole.Tool)) continue;
            var toolText = Messages[i].GetText();
            if (!string.IsNullOrEmpty(toolText)
                && toolText.StartsWith(Builtins.SubmitMarker, StringComparison.Ordinal))
                return toolText[Builtins.SubmitMarker.Length..];
        }

        return null;
    }

    // ----- Per-dispatch metadata (only set when team.dispatch_worktree=true) -----
    // The coordinator captures these from the dispatch worktree after the
    // member finishes; LeaderTools surfaces them through assign_task /
    // assign_async return values so the leader can decide accept vs reject.
    // Null on non-worktree dispatches and on top-level (single-agent) runs.

    /// <summary>Captured `git diff --cached --binary` blob (or null if no
    /// changes / not a worktree dispatch). Replayed by accept_dispatch.</summary>
    public string? DispatchDiff { get; init; }

    /// <summary>Human-readable `git diff --stat` for the diff. Goes into the
    /// dispatch_end event payload so the chat UI can show "14 files +423/-12"
    /// in the dispatch card without parsing the full patch.</summary>
    public string? DispatchDiffStat { get; init; }

    /// <summary>Number of files changed. Quick scrutiny signal for the leader
    /// (small change + confident self-assessment → likely safe to accept).</summary>
    public int DispatchFilesChanged { get; init; }

    /// <summary>Absolute path of the dispatch worktree on disk. Stays valid
    /// until accept_dispatch / reject_dispatch tears it down.</summary>
    public string? DispatchWorktreePath { get; init; }

    /// <summary>Branch name of the dispatch worktree (vett/dispatch/...).</summary>
    public string? DispatchBranch { get; init; }

    /// <summary>One of "confident" | "partial" | "uncertain" | "unknown",
    /// parsed from a "SELF-ASSESSMENT:" prefix in the implementer's final
    /// message. "unknown" means the prefix was missing.</summary>
    public string SelfAssessment { get; init; } = "unknown";

    /// <summary>Free-form notes after the SELF-ASSESSMENT line, parsed from
    /// the matching "NOTES:" block. Empty when assessment is unknown.</summary>
    public string SelfAssessmentNotes { get; init; } = "";
}

// --- Three parameter groups ---

public sealed record LlmSettings(IChatClient Client, string Model, double Temperature = 1.0, double? TopP = null, int? MaxOutputTokens = null,
    double? PresencePenalty = null, double? FrequencyPenalty = null);
public sealed record AgentCapabilities(Dictionary<string, ToolFn> Tools, List<JsonElement> ToolSchemas, List<MiddlewareFn> Middlewares);
public sealed record AgentEnvironment(
    ISandbox Sandbox,
    string SessionId,
    int MaxIterations = 100,
    Action<Event>? OnEvent = null,
    /// <summary>
    /// Optional hook the loop calls right before each iteration's LLM
    /// request, with the live AgentState so the hook can mutate
    /// state.Messages (typically by injecting synthesized "[task X
    /// completed: …]" user messages drained from a TaskBoard). Used by
    /// the chat coordinator to deliver async task results without the
    /// leader having to poll. Hook runs synchronously in the loop's
    /// turn — keep it fast.
    /// </summary>
    Func<AgentState, CancellationToken, Task>? PreIteration = null,
    /// <summary>
    /// Optional channel of mid-flight user messages to inject into the
    /// running loop. The loop drains all pending messages at each
    /// iteration boundary and appends them as user turns, so the
    /// agent picks them up on the next LLM call without losing any
    /// of its prior context (tool calls, partial conclusions, etc).
    /// Used by the team coordinator's `inject_into_task` so the leader
    /// can redirect a busy member without cancelling and restarting.
    /// Null for top-level / non-injectable runs.
    /// </summary>
    System.Threading.Channels.ChannelReader<string>? InjectedMessages = null,
    /// <summary>
    /// Optional wake signal that breaks the loop's "waiting for user
    /// input" wait WITHOUT consuming a user message. Used by the
    /// chat coordinator so background-task completions and
    /// `report_progress` calls cause the leader to iterate
    /// immediately instead of sitting idle until the user types.
    /// Drained on each fire (coalescing) — multiple wakes between
    /// iterations only kick the loop once.
    /// </summary>
    System.Threading.Channels.ChannelReader<bool>? WakeSignal = null,
    /// <summary>
    /// Auto-lint shell command. Run via the sandbox's bash after any
    /// iteration where the agent successfully wrote a file via
    /// file_editor. Non-zero exit → stdout/stderr appended to the
    /// conversation as a &lt;lint_feedback&gt; user-style turn so the
    /// agent self-corrects on the next LLM call. Null/empty = disabled.
    /// Sourced from <see cref="Vett.Config.Profile.LintCmd"/>.
    /// </summary>
    string? AutoLintCmd = null,
    /// <summary>
    /// Auto-test shell command — same shape as <see cref="AutoLintCmd"/>.
    /// Sourced from <see cref="Vett.Config.Profile.TestCmd"/>.
    /// </summary>
    string? AutoTestCmd = null,
    /// <summary>
    /// Optional pause request — when set, the loop checks at each
    /// iteration boundary and pauses BEFORE the next LLM call. While
    /// paused, the loop waits on the same user-input channel as
    /// "waiting for input" but emits a `paused` event so the chat UI
    /// can show a Resume button. Resume is signaled by the runner
    /// clearing the request OR by a fresh user message arriving.
    /// </summary>
    PauseRequest? PauseRequest = null,
    /// <summary>
    /// Optional permission gate — when set, every tool call is
    /// classified into a <see cref="Vett.Tools.PermissionKind"/> and
    /// gated according to the configured rules: Auto invokes
    /// directly, Ask emits a `permission_request` event and awaits
    /// the user's `permission_response` via stdin, Deny synthesizes
    /// an error observation back to the agent without invoking the
    /// tool. Null = no gating (benchmarks, unconfigured profiles).
    /// </summary>
    Vett.Tools.PermissionGate? PermissionGate = null,
    /// <summary>
    /// Optional reference to the active profile, threaded through so
    /// <see cref="Vett.Plugin.LifecycleHooks"/> can read the
    /// <c>hooks:</c> block at PreToolUse / PostToolUse time. Null = no
    /// hook plumbing (benchmarks, unconfigured profiles, RunAsync
    /// non-chat callers). When non-null AND the profile carries a
    /// non-empty <c>pre_tool_use</c> list, the dispatch path runs
    /// hooks BEFORE the permission gate so a hook's deny / modify is
    /// authoritative over the user prompt.
    /// </summary>
    Vett.Config.Profile? Profile = null,
    /// <summary>
    /// True only when a PERSON is reading the output and can type a reply.
    ///
    /// This is NOT the same question as "is there a userInput channel". A team
    /// leader and the bench harness are both driven through
    /// <see cref="AgentLoop.RunInteractiveAsync"/> with a channel, but nobody is
    /// watching them. That distinction decides what the runaway cap does when it
    /// trips: with a human present the loop PARKS (stop spending, explain, wait),
    /// because terminating would throw away a live conversation the person was in
    /// the middle of. With no human, parking is strictly worse than the kill — the
    /// run would hang forever on input that is never coming — so the loop keeps
    /// the old behaviour and terminates with stop_reason "max_iterations", which
    /// is what the bench harness and the coordinator reconcile on.
    ///
    /// Defaults to false so every caller that has not thought about it gets the
    /// pre-existing terminate semantics. Only the chat entry points set it.
    /// </summary>
    bool HumanAtTheKeyboard = false,
    /// <summary>
    /// Escape hatch: let an AGENT-SIDE stop end the whole chat session, the way
    /// it did before 2026-08-29.
    ///
    /// The default is false, which means: in `vett chat`, the agent calling
    /// `finish` (or tripping a stuck detector) ENDS THE TURN, NOT THE
    /// CONVERSATION. Mark, 2026-08-29, after watching a session close itself at
    /// iteration 130: "i dont love it ended conv before i had a chance to
    /// review it" ... "maybe when its vett chat it never does maybe reg vett
    /// can. as its fully automatoous". That is the split this encodes: the
    /// autonomous path is untouched, because there the whole run genuinely IS
    /// one turn and the bench harness reconciles on the stop_reason.
    ///
    /// THIS ONLY DOES ANYTHING WHERE <see cref="HumanAtTheKeyboard"/> IS TRUE.
    /// With nobody typing there is no one to hand control back to, so the loop
    /// terminates regardless of what this says. Parking there would hang the
    /// run forever on input that is never coming.
    ///
    /// Kept because ending really is right sometimes ("there may be times its
    /// good it can"): a scripted `vett chat --stdio` driven by another program
    /// wants the process to exit when the agent declares done. Opt in with
    /// `--finish-ends-session`.
    /// </summary>
    bool AgentStopEndsSession = false);

/// <summary>
/// The agent loop. Uses ChatMessage (Microsoft.Extensions.AI) directly.
/// No internal message types, no conversion layer.
/// </summary>
public static class AgentLoop
{
    /// <summary>A JSON object key: <c>"name":</c>. Used by the malformed-tool-call
    /// heuristic to ask whether a JSON-shaped reply uses a registered tool
    /// PARAMETER as a key (a tool call) or only its own vocabulary (an answer).</summary>
    private static readonly System.Text.RegularExpressions.Regex JsonKeyRe =
        new("\"([A-Za-z_][A-Za-z0-9_]*)\"\\s*:", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Accept a user message at a wait boundary and start that turn's runaway
    /// budget from here.
    ///
    /// Every interactive site that takes a user message goes through this. A
    /// site that appends <c>Chat.User(...)</c> directly silently opts out of the
    /// reset, and the symptom of that is invisible until someone has a long
    /// enough conversation to be killed by it.
    /// </summary>
    private static void BeginUserTurn(AgentState state, string message)
    {
        state.Messages.Add(Chat.User(message));
        state.TurnStartIteration = state.Iteration;
    }

    /// <summary>
    /// The system prompt every seat is actually shown: the caller's prompt,
    /// then -- when the profile bounds output -- a paragraph that names the cap
    /// and says what happens at it.
    ///
    /// The number is <see cref="LlmSettings.MaxOutputTokens"/>, the same value
    /// the loop installs on the wire, so it cannot drift from the bound it
    /// describes. No cap, no paragraph: the provider's limit is unknown and no
    /// number beats a wrong one. Idempotent, so a prompt that already carries
    /// this exact notice (a continued conversation, a replay) is returned as-is.
    /// </summary>
    public static string WithOutputCapNotice(string systemPrompt, int? maxOutputTokens)
    {
        if (maxOutputTokens is not int cap || cap <= 0) return systemPrompt;
        var notice = OutputCapNotice(cap);
        if (systemPrompt.Contains(notice, StringComparison.Ordinal)) return systemPrompt;
        return string.IsNullOrEmpty(systemPrompt) ? notice : systemPrompt.TrimEnd() + "\n\n" + notice;
    }

    /// <summary>
    /// The paragraph itself. Measured 2026-09-05 (EpicForge runs 4 and 5): the
    /// seat that writes a file writes it as ONE heredoc inside ONE tool call,
    /// and when that call is longer than the cap it is cut mid-arguments and
    /// the whole reply is lost -- 44-55k characters of a `create` call at a
    /// 12288 cap, 15-17k at 4096. The team brief had disclosed the cap, but
    /// only to the leader; the leader's delegation rewrote the brief and the
    /// member never saw it. This is the one message no delegation rewrites.
    /// The only digits in it are the cap.
    /// </summary>
    private static string OutputCapNotice(int cap) =>
        "OUTPUT CAP: every reply you send is cut off at " + cap + " tokens, and a reply that reaches "
        + "that limit is not finished. If it contains no tool call it is discarded whole; a tool call "
        + "that is still open when the cut lands is lost with its arguments. Make the tool call first "
        + "and keep the prose around it short. When a file, list or answer will not fit in one reply, "
        + "build it across several tool calls -- write the first part, then append the rest -- never in "
        + "one call. This applies to every reply, including the final one.";

    public static async Task<AgentResult> RunAsync(
        LlmSettings llm, AgentCapabilities caps, AgentEnvironment env,
        string systemPrompt, string userMessage,
        CancellationToken ct = default)
    {
        var state = new AgentState
        {
            Messages = [Chat.System(WithOutputCapNotice(systemPrompt, llm.MaxOutputTokens)), Chat.User(userMessage)],
            MaxIterations = env.MaxIterations,
        };
        return await Loop(llm, caps, env, state, null, null, null, null, null, ct);
    }

    public static async Task<AgentResult> RunInteractiveAsync(
        LlmSettings llm, AgentCapabilities caps, AgentEnvironment env,
        string systemPrompt, ChannelReader<string> userInput,
        Action<string>? onAssistantText, Action? onWaitingForInput,
        CancellationToken ct = default)
        => await RunInteractiveAsync(llm, caps, env, systemPrompt, userInput,
            onAssistantText, onWaitingForInput, seedHistory: null, turnInterrupt: null, compactRequest: null, ct);

    /// <summary>
    /// Continue a non-interactive run from a prior <see cref="AgentState"/>.
    /// Used by the team coordinator's <c>continue_task</c> tool: the
    /// member's previous conversation (system, user, assistant, tool
    /// turns) is preserved; the new message is appended as the next
    /// user turn; the loop iterates until the member produces a reply.
    /// Same MaxIterations + StopLoop semantics as RunAsync.
    /// </summary>
    public static async Task<AgentResult> ContinueAsync(
        LlmSettings llm, AgentCapabilities caps, AgentEnvironment env,
        AgentState priorState, string additionalUserMessage,
        CancellationToken ct = default)
    {
        var state = new AgentState
        {
            // Clone the message list so the caller's stored state isn't
            // mutated as the loop advances.
            Messages = new List<ChatMessage>(priorState.Messages) { Chat.User(additionalUserMessage) },
            MaxIterations = env.MaxIterations,
            Iteration = priorState.Iteration,
            TotalInputTokens = priorState.TotalInputTokens,
            TotalOutputTokens = priorState.TotalOutputTokens,
        };
        return await Loop(llm, caps, env, state, null, null, null, null, null, ct);
    }

    /// <summary>
    /// Same as the non-seeded overload but pre-populates the agent's
    /// message history from a past chat (for `vett chat --resume`).
    /// The seed is inserted between the system prompt and the first
    /// new user message, so the agent treats it as prior conversation.
    ///
    /// <paramref name="turnInterrupt"/>, if provided, lets a runner
    /// abort the current turn (LLM call + tool calls) without killing
    /// the loop — that's how "Stop" works in the chat UI. The loop
    /// observes the per-turn token, distinguishes a user-stop from a
    /// session-stop via the parent <paramref name="ct"/>, and continues
    /// to the next user input rather than terminating.
    /// </summary>
    public static async Task<AgentResult> RunInteractiveAsync(
        LlmSettings llm, AgentCapabilities caps, AgentEnvironment env,
        string systemPrompt, ChannelReader<string> userInput,
        Action<string>? onAssistantText, Action? onWaitingForInput,
        IReadOnlyList<ChatMessage>? seedHistory,
        TurnInterrupt? turnInterrupt = null,
        CompactRequest? compactRequest = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { Chat.System(WithOutputCapNotice(systemPrompt, llm.MaxOutputTokens)) };
        if (seedHistory is { Count: > 0 })
            messages.AddRange(seedHistory);

        var state = new AgentState
        {
            Messages = messages,
            MaxIterations = env.MaxIterations,
        };

        onWaitingForInput?.Invoke();
        // First message must come from the user — wake signals before
        // any user input have nothing to deliver against, so they're
        // ignored at session start.
        if (!await userInput.WaitToReadAsync(ct))
            return Finalize(state, "user_closed");
        if (userInput.TryRead(out var first))
            state.Messages.Add(Chat.User(first));

        return await Loop(llm, caps, env, state, userInput, onAssistantText, onWaitingForInput, turnInterrupt, compactRequest, ct);
    }

    /// <summary>
    /// Wait for either a user message OR a wake signal. Returns:
    ///   ("user", msg) — a real user input arrived; msg goes into state.
    ///   ("wake", null) — the board woke us; iterate without a new
    ///                    user message so the PreIteration hook can
    ///                    drain pending completions / progress.
    ///   ("closed", null) — userInput channel closed; finalize.
    /// Wake signals are coalesced (drained) so a burst of completions
    /// only kicks one iteration.
    /// </summary>
    private static async Task<(string Kind, string? Msg)> WaitForUserOrWake(
        ChannelReader<string> userInput,
        ChannelReader<bool>? wake,
        CancellationToken ct)
    {
        if (wake is null)
        {
            if (!await userInput.WaitToReadAsync(ct)) return ("closed", null);
            userInput.TryRead(out var msg);
            return ("user", msg);
        }
        var userTask = userInput.WaitToReadAsync(ct).AsTask();
        var wakeTask = wake.WaitToReadAsync(ct).AsTask();
        var done = await Task.WhenAny(userTask, wakeTask);
        if (done == wakeTask)
        {
            // Coalesce: drop any other queued wakes so we iterate once.
            while (wake.TryRead(out _)) { }
            return ("wake", null);
        }
        if (!await userTask) return ("closed", null);
        userInput.TryRead(out var msg2);
        return ("user", msg2);
    }

    // Tracks how many iterations in a row had a terminal call where every
    // observation was a failure. Recovery fires at the threshold rather than
    // letting StuckDetector trip — the bash session inside the sandbox can
    // die silently (no timeout prefix) and the only signal is N back-to-back
    // failed terminal calls. Was previously 0; the loop only recovered when
    // 2 failures landed in the same iteration, which is rare because the
    // model usually issues one terminal per turn.
    private const int ConsecutiveTerminalFailureThreshold = 2;

    /// <summary>
    /// A cut tool call's raw arguments whose last <see cref="TailSampleChars"/>
    /// characters deflate to under this percentage of their size were a
    /// repetition loop, not content. Distinct prose or JSON lists deflate to
    /// 30–50%; the same line 700 times deflates to ~1%. Calibrated on the
    /// test fixtures in TruncatedToolCallTests; the measured value is
    /// published in `truncated_tool_call.tail_compression_pct` so the live
    /// distribution can move this number if it is wrong.
    /// </summary>
    internal const int RepeatingTailCompressionPct = 12;
    internal const int TailSampleChars = ArgumentTail.SampleChars;

    private static async Task<AgentResult> Loop(
        LlmSettings llm, AgentCapabilities caps, AgentEnvironment env,
        AgentState state,
        ChannelReader<string>? userInput,
        Action<string>? onAssistantText, Action? onWaitingForInput,
        TurnInterrupt? turnInterrupt,
        CompactRequest? compactRequest,
        CancellationToken ct)
    {
        void Emit(string type, Dictionary<string, object?> data)
            => env.OnEvent?.Invoke(new Event(type, data));

        // Iteration boundary. We wrap the body in try/catch so an
        // interactive "stop" can abort a single turn without unwinding
        // the whole loop — the catch checks whether `turnInterrupt`
        // (not the parent ct) was the cancellation source. Without an
        // interrupt, a session-wide ct still terminates everything.
        // failureBox is a single-element mutable counter shared across
        // iterations (RunOneIteration is async and can't take ref int).
        var failureBox = new int[] { 0 };
        var interactive = userInput is not null;
        // The park needs BOTH: a channel to wait on, and someone who will use it.
        var canPark = interactive && env.HumanAtTheKeyboard;

        // The loop condition stays cumulative for non-interactive runs — the
        // bench harness reconciles runs on stop_reason "max_iterations" and
        // that path is unchanged. Interactive runs never fall out of the loop
        // on the cap at all; they park inside it (see below).
        while (canPark || state.Iteration < env.MaxIterations)
        {
            ct.ThrowIfCancellationRequested();

            // Runaway cap, interactive form. Two things differ from the
            // non-interactive path, and both were flaws rather than choices:
            //
            // 1. THE DENOMINATOR. The cap means "this one request will not
            //    stop". Measured from zero against a counter that never resets,
            //    it instead means "this conversation has been going a while",
            //    so a healthy session dies of old age. Measured per turn, it
            //    detects the thing it was built to detect.
            //
            // 2. IT PARKS, IT DOES NOT TERMINATE. Finalize() ends the session
            //    and takes the conversation with it. The state is right here
            //    and so is the human — the honest move is to stop spending
            //    tokens, say plainly what happened, and wait to be told what to
            //    do. The protection is intact: the agent really does stop.
            if (canPark && state.Iteration - state.TurnStartIteration >= env.MaxIterations)
            {
                var spent = state.Iteration - state.TurnStartIteration;
                Emit("runaway_paused", new()
                {
                    ["iteration"] = state.Iteration,
                    ["turn_iterations"] = spent,
                    ["max_iterations"] = env.MaxIterations,
                });

                var notice =
                    $"[Stopped after {spent} steps on this one request — that is the runaway limit "
                    + $"(max_iterations: {env.MaxIterations}). This is a safety stop, not a crash: the "
                    + "session and its full context are still here. Send another message to carry on, "
                    + "or point me somewhere else.]";
                onAssistantText?.Invoke(notice);
                state.Messages.Add(Chat.Assistant(notice));

                onWaitingForInput?.Invoke();
                while (true)
                {
                    var capWait = await WaitForUserOrWake(userInput!, env.WakeSignal, ct);
                    if (capWait.Kind == "closed") return Finalize(state, "user_closed");
                    if (capWait.Kind == "user" && !string.IsNullOrEmpty(capWait.Msg))
                    {
                        BeginUserTurn(state, capWait.Msg!);
                        Emit("resumed", new() { ["iteration"] = state.Iteration, ["with_message"] = true });
                        break;
                    }
                    // A wake alone must NOT hand back a fresh budget. Background
                    // task completions arrive on their own schedule, so resuming
                    // on one would reset the detector forever and there would be
                    // no cap at all. Only a human clears this.
                }
                continue;
            }

            // Pause-mid-loop check. Runs at the iteration boundary
            // (between LLM responses) so an in-flight tool call
            // completes naturally before we freeze. Emits `paused`
            // once, then waits on the same channel as "waiting for
            // input" — either a real user message or an explicit
            // PauseRequest.Resume() will release the wait. A new user
            // message clears the pause flag implicitly (it's the
            // resume signal); explicit Resume + no message leaves the
            // loop going on the next iteration without a fresh turn.
            if (env.PauseRequest is { IsPaused: true } && userInput is not null)
            {
                Emit("paused", new() { ["iteration"] = state.Iteration });
                onWaitingForInput?.Invoke();
                while (env.PauseRequest.IsPaused)
                {
                    var waitResult = await WaitForUserOrWake(userInput, env.WakeSignal, ct);
                    if (waitResult.Kind == "closed") return Finalize(state, "user_closed");
                    if (waitResult.Kind == "user" && !string.IsNullOrEmpty(waitResult.Msg))
                    {
                        // User typed something — clear pause and treat
                        // their text as the next turn. Without this the
                        // message would be dropped on the next loop pass.
                        env.PauseRequest.Resume();
                        BeginUserTurn(state, waitResult.Msg!);
                        Emit("resumed", new() { ["iteration"] = state.Iteration, ["with_message"] = true });
                        break;
                    }
                    // wake-only path: re-check IsPaused; if explicit
                    // Resume() cleared it (chat clicked Resume button),
                    // emit resumed and proceed without a user message.
                    if (!env.PauseRequest.IsPaused)
                    {
                        Emit("resumed", new() { ["iteration"] = state.Iteration, ["with_message"] = false });
                        break;
                    }
                    // still paused, no user input — keep waiting.
                }
            }

            state.Iteration++;
            // Per-iteration token. Falls back to the parent ct when no
            // interrupt is provided so non-interactive callers (`vett
            // run` / benchmarks) keep their existing semantics.
            var iterCt = turnInterrupt is null ? ct : turnInterrupt.NewTurn(ct);

            try
            {
                // Force-compact handling: if the runner asked for a
                // compaction (e.g. user typed /compact), run the
                // milestone checkpoint with a 0-token threshold so it
                // always fires, then emit so the UI can render a marker.
                if (compactRequest?.TakePending() == true)
                {
                    var before = state.Messages.Count;
                    // Honour the profile's compaction block. Passing only
                    // (threshold, model) silently took the METHOD defaults —
                    // see CompactRequest for what that broke.
                    var force = Vett.Tools.CompactionMiddleware.MilestoneCheckpoint(
                        tokenThreshold: 0,
                        keepLastMessages: compactRequest.KeepLastMessages,
                        model: llm.Model,
                        sessionLogDir: compactRequest.SessionLogDir);
                    await force(state, iterCt);

                    // ⛔ EMITTED UNCONDITIONALLY — NOT GATED ON THE COUNT
                    // SHRINKING, unlike the auto path below.
                    //
                    // The auto path infers "a compaction happened" from
                    // Messages.Count dropping, because it cannot tell which
                    // middleware in the chain did what. Here there is nothing
                    // to infer: threshold_tokens is 0, so the checkpoint ALWAYS
                    // fires, and the user explicitly asked for it. Whether it
                    // fired is known, not deduced.
                    //
                    // The old `if (Count < before)` guard was not just
                    // redundant, it was WRONG, and a live probe caught it: a
                    // checkpoint rebuilds the list as
                    //     system + summary + ack + last N
                    // which on a SHORT history is LARGER than what it replaced
                    // (observed: 7 messages in, 8 out, keep-5). The history was
                    // genuinely rewritten to a digest — real content replaced by
                    // a summary — and the guard reported nothing, so the user
                    // pressed /compact and the UI showed silence. Counting
                    // MESSAGES to detect a change that is about CONTENT reads
                    // the wrong quantity; on this path we don't need the proxy.
                    //
                    // `grew` is surfaced rather than hidden: a compaction that
                    // increases the message count is worth seeing, since on a
                    // short history it costs fidelity and buys nothing.
                    Emit("compacted", new()
                    {
                        ["reason"] = "user_requested",
                        ["before"] = before,
                        ["after"] = state.Messages.Count,
                        ["grew"] = state.Messages.Count > before,
                    });
                }

                var result = await RunOneIteration(llm, caps, env, state, userInput,
                    onAssistantText, onWaitingForInput, failureBox, Emit, compactRequest, iterCt);
                if (result is not null) return result;
            }
            catch (OperationCanceledException) when (turnInterrupt is not null && turnInterrupt.IsTurnCancelled(ct))
            {
                // User pressed Stop. Drop whatever partial work this
                // iteration produced, mark it in the conversation, and
                // wait for the next user message. The agent loop stays
                // alive — same UX as Claude Code's stop button.
                Emit("cancelled", new() { ["reason"] = "user_interrupted", ["iteration"] = state.Iteration });
                state.Messages.Add(Chat.Assistant("[Request interrupted by user]"));

                if (userInput is null)
                {
                    return Finalize(state, "user_interrupted");
                }
                onWaitingForInput?.Invoke();
                var waitResult = await WaitForUserOrWake(userInput, env.WakeSignal, ct);
                if (waitResult.Kind == "closed") return Finalize(state, "user_closed");
                if (waitResult.Kind == "user" && !string.IsNullOrEmpty(waitResult.Msg))
                    BeginUserTurn(state, waitResult.Msg!);
                // wake-only path falls through with no user message —
                // PreIteration hook will surface pending deliveries.
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // External cancellation (cancel_task, parent shutdown).
                // Preserve partial state by returning a "cancelled"
                // result instead of propagating — that lets the
                // coordinator cache the messages so continue_task can
                // resume the member with whatever it had so far.
                // Without this, cancelled members had no cached state
                // and any later "tell it to summarize what it found"
                // failed with "no cached state — was it ever run?".
                Emit("cancelled", new() { ["reason"] = "external", ["iteration"] = state.Iteration });
                state.Messages.Add(Chat.Assistant("[Task cancelled — partial state preserved]"));
                return Finalize(state, "cancelled");
            }
        }
        return Finalize(state, "max_iterations");
    }

    /// <summary>
    /// Body of one iteration, extracted so the loop's try/catch around
    /// turn-cancellation has a clean boundary. Returns null when the
    /// iteration completes and the loop should keep going; returns a
    /// concrete <see cref="AgentResult"/> when the loop should
    /// terminate (llm_error, empty_response, user_closed, etc.).
    ///
    /// <paramref name="consecutiveFailureBox"/> is a single-element
    /// array used as a mutable counter — async methods can't take
    /// `ref int` parameters.
    /// </summary>
    private static async Task<AgentResult?> RunOneIteration(
        LlmSettings llm, AgentCapabilities caps, AgentEnvironment env,
        AgentState state,
        ChannelReader<string>? userInput,
        Action<string>? onAssistantText, Action? onWaitingForInput,
        int[] consecutiveFailureBox,
        Action<string, Dictionary<string, object?>> Emit,
        CompactRequest? compactRequest,
        CancellationToken ct)
    {
        // The body that follows is the original loop body — same code,
        // just lifted into its own method so the try/catch in Loop can
        // surround it cleanly. References to `ct` resolve to the
        // per-iteration token. The shared mutable state across
        // iterations (consecutiveTerminalFailures) lives in
        // consecutiveFailureBox[0].
        var consecutiveTerminalFailures = consecutiveFailureBox[0];

            // Pre-iteration hook: lets the coordinator inject pending
            // async-task completions into state.Messages so the leader
            // sees them naturally as part of the next LLM call. Off in
            // bench mode (hook is null), on in chat with auto-inject.
            if (env.PreIteration is not null)
                await env.PreIteration(state, ct);

            // Drain mid-flight user injections into the conversation as
            // user turns. The agent's prior context is preserved — it
            // just sees an additional user message and can decide
            // whether to pivot, integrate, or acknowledge. Wraps each
            // injection in <user_interjection> so the agent reliably
            // notices it as a fresh directive rather than leftover
            // system text.
            if (env.InjectedMessages is not null)
            {
                while (env.InjectedMessages.TryRead(out var injected))
                {
                    state.Messages.Add(Chat.User(
                        $"<user_interjection>\n{injected}\n</user_interjection>"));
                    Emit("user_interjection", new()
                    {
                        ["text"] = injected,
                        ["iteration"] = state.Iteration,
                    });
                }
            }

            // R1 (relay): a wrap-up nudge staged at last iteration's usage
            // capture lands here, where appending a user turn is ordering-
            // safe (same channel as the pre-iteration hook above).
            if (state.PendingContextNudge is not null)
            {
                state.Messages.Add(Chat.User(state.PendingContextNudge));
                Emit("context_nudge", new()
                {
                    ["iteration"] = state.Iteration,
                    ["pct"] = state.LastContextNudgePct,
                });
                state.PendingContextNudge = null;
            }

            Emit("iteration_start", new() { ["iteration"] = state.Iteration });

            // LLM call via IChatClient. Tool schemas must be sent on every
            // request — without them the model has nothing to call, vLLM's
            // qwen3_coder parser stays inactive, and the loop monologues
            // until StuckDetector trips.
            var chatOptions = new ChatOptions
            {
                ModelId = llm.Model,
                Temperature = (float)llm.Temperature,
                TopP = llm.TopP.HasValue ? (float)llm.TopP.Value : null,
                // Anti-repetition penalties. Null leaves them off the wire so the
                // provider applies its default (vLLM: 0). See Profile.PresencePenalty
                // for the measurement that made them a profile setting.
                PresencePenalty = llm.PresencePenalty.HasValue ? (float)llm.PresencePenalty.Value : null,
                FrequencyPenalty = llm.FrequencyPenalty.HasValue ? (float)llm.FrequencyPenalty.Value : null,
                // Null leaves max_tokens off the wire, which is NOT a modest
                // default -- vLLM then permits (max_model_len - prompt), i.e.
                // a call that can outlive its own request timeout several
                // times over. See Profile.MaxOutputTokens for the measurement.
                MaxOutputTokens = llm.MaxOutputTokens,
            };
            if (caps.ToolSchemas.Count > 0)
            {
                // Substitute the `{working_dir}` placeholder in tool
                // schema descriptions (file_editor's "Your current
                // working directory is: {working_dir}" line) with the
                // sandbox's actual cwd so the model has a real path to
                // anchor file operations at. Without this the model
                // sees the literal `{working_dir}` and either copies it
                // verbatim or invents a plausible-looking absolute path
                // (e.g. `/TestRepo/`) — both leave files outside the
                // worktree and bypass dispatch isolation.
                //
                // Empty Cwd (RpcClient/sidecar mode) is a no-op — bench
                // runs already substitute at schema-load time in Runner.
                var workingDir = env.Sandbox.Cwd;
                chatOptions.Tools = caps.ToolSchemas
                    .Select(s => (AITool)RawToolDeclaration.From(SubstituteWorkingDir(s, workingDir)))
                    .ToList();
            }

            // Forced-tool retry: when the previous iteration detected a
            // malformed-tool-call attempt and could identify the tool the
            // model was *trying* to call, set tool_choice to force that
            // SPECIFIC tool. Constrained decoding via xgrammar produces
            // a valid call by construction.
            //
            // Why specific-tool instead of "required":
            // `tool_choice="required"` HANGS on aeon-mtp + qwen3_coder + MTP
            // (verified 2026-05-08 with 60s timeouts via direct curl).
            // vLLM constructs a union grammar over all tools for the
            // "required" path; that interacts badly with MTP draft-token
            // acceptance. The specific-tool path uses a simpler single-
            // tool grammar that MTP handles cleanly (5s, valid output in
            // the same curl test). So we ALWAYS pin to a specific tool
            // when we have a name, never fall back to "required".
            if (!string.IsNullOrEmpty(state.ForceToolNameNextCall) && chatOptions.Tools is { Count: > 0 })
            {
                var forcedName = state.ForceToolNameNextCall;
                state.ForceToolNameNextCall = null;
                // Find the AITool with that name in the freshly-built tools
                // list. If the name doesn't match a registered tool (model
                // hallucinated a tool name), fall through to auto rather
                // than throwing — the system's downstream nudge will catch.
                var matched = chatOptions.Tools.OfType<AIFunction>().FirstOrDefault(t =>
                    string.Equals(t.Name, forcedName, StringComparison.Ordinal));
                if (matched is not null)
                {
                    chatOptions.ToolMode = ChatToolMode.RequireSpecific(forcedName);
                    Emit("forced_tool_choice", new()
                    {
                        ["iteration"] = state.Iteration,
                        ["tool_name"] = forcedName,
                        ["reason"] = "previous_iteration_emitted_malformed_tool_call",
                    });
                }
            }

            // Emit the FULL outbound request so vett-chat's Raw view can
            // show exactly what the LLM saw — system prompt, every prior
            // turn, every tool result, every assistant reply. Big events,
            // but Raw view is opt-in. Logs view ignores the messages
            // field and just shows "→ N msgs sent".
            Emit("llm_request", new()
            {
                ["iteration"] = state.Iteration,
                ["model"] = llm.Model,
                ["temperature"] = llm.Temperature,
                ["top_p"] = llm.TopP,
                ["presence_penalty"] = llm.PresencePenalty,
                ["frequency_penalty"] = llm.FrequencyPenalty,
                // Emitted even when null. A bound you cannot see in the log is
                // a bound you cannot prove was applied, and "unbounded" is
                // exactly the value that needs to be visible.
                ["max_output_tokens"] = llm.MaxOutputTokens,
                ["message_count"] = state.Messages.Count,
                ["tool_count"] = chatOptions.Tools?.Count ?? 0,
                ["messages"] = state.Messages.Select(SerializeMessage).ToList(),
            });

            // Retry transient LLM errors (HTTP 400/429/5xx, socket
            // timeouts) with backoff before giving up. AEON has been
            // observed returning sporadic 400s under sustained load —
            // a fresh request a couple seconds later usually succeeds.
            // Without this retry, a single hiccup stalls a run until
            // the per-instance timeout fires (silent fail).
            //
            // Cap is intentionally small (2 retries = 3 attempts total)
            // so we don't paper over genuinely broken state.
            ChatResponse? resp = null;
            Exception? lastEx = null;
            const int LlmRetryCap = 2;
            int[] backoffSeconds = { 2, 6 };
            for (int attempt = 0; attempt <= LlmRetryCap; attempt++)
            {
                try
                {
                    resp = await llm.Client.GetResponseAsync(state.Messages, chatOptions, ct);
                    lastEx = null;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Emit("llm_error", new()
                    {
                        ["message"] = ex.Message,
                        ["attempt"] = attempt,
                        ["retry_cap"] = LlmRetryCap,
                        ["will_retry"] = attempt < LlmRetryCap,
                    });
                    if (attempt >= LlmRetryCap) break;
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds[attempt]), ct); }
                    catch (OperationCanceledException) { throw; }
                }
            }
            if (resp is null)
            {
                consecutiveFailureBox[0] = consecutiveTerminalFailures;
                return Finalize(state, "llm_error");
            }

            if (resp.Messages.Count == 0)
            {
                consecutiveFailureBox[0] = consecutiveTerminalFailures;
                return Finalize(state, "empty_response");
            }

            var lastMsg = resp.Messages.Last();
            var inputTokens = (int)(resp.Usage?.InputTokenCount ?? 0);
            var outputTokens = (int)(resp.Usage?.OutputTokenCount ?? 0);
            state.TotalInputTokens += inputTokens;
            state.TotalOutputTokens += outputTokens;

            // Anchor compaction on the provider's real count rather than on
            // TokenEstimator, which is ~2.4x low on a real turn because it
            // cannot see the tool schemas re-sent at :491-518. The honest
            // number was already being read here for the relay nudge below and
            // then thrown away; compaction guessed instead, one line later in
            // the call graph. state.Messages has NOT been appended to yet at
            // this point (the next Add is at :758, and the request was built
            // from this exact list — see message_count at :568), so the count
            // captured here is precisely the prompt that was measured.
            if (inputTokens > 0)
            {
                state.LastRealInputTokens = inputTokens;
                state.LastRealInputMessageCount = state.Messages.Count;
            }

            // R1 (relay): surface REAL context fullness. inputTokens is the
            // size of the entire prompt just sent — the honest number
            // (TokenEstimator drifts, iteration count is a bad proxy). The
            // nudge itself is only STAGED here: appending a user message
            // mid-response-processing would land it before lastMsg and
            // corrupt ordering, so it is injected at the top of the next
            // iteration. Off unless the profile carries a relay: block.
            var relayCfg = env.Profile?.Relay;
            if (relayCfg is { ContextWindow: > 0 } && inputTokens > 0)
            {
                var ctxPct = RelayContext.Pct(inputTokens, relayCfg.ContextWindow);
                Emit("context_status", new()
                {
                    ["iteration"] = state.Iteration,
                    ["input_tokens"] = inputTokens,
                    ["context_window"] = relayCfg.ContextWindow,
                    ["pct"] = ctxPct,
                    ["threshold_pct"] = relayCfg.HandoffThresholdPct,
                });
                if (RelayContext.ShouldNudge(ctxPct, relayCfg.HandoffThresholdPct,
                                             state.LastContextNudgePct))
                {
                    state.LastContextNudgePct = ctxPct;
                    state.PendingContextNudge = RelayContext.NudgeText(ctxPct, relayCfg);
                }
            }

            // M.E.AI 10.5 lacks a ReasoningContent type, so providers
            // route the OpenAI `reasoning`/`reasoning_content` field
            // into AdditionalProperties on either the ChatResponse or
            // the message. Aeon's vLLM emits reasoning unconditionally
            // when `--reasoning-parser qwen3` is set; capturing here
            // makes the trace visible in the session log instead of
            // silently dropped. Best-effort: serialize only string-ish
            // values to keep the JSONL line short.
            Dictionary<string, object?>? reasoningProps = null;
            try
            {
                static void CopyStringy(System.Collections.Generic.IDictionary<string, object?>? src, Dictionary<string, object?> dst, string prefix)
                {
                    if (src is null) return;
                    foreach (var (k, v) in src)
                    {
                        if (v is null) continue;
                        var lk = k.ToLowerInvariant();
                        if (lk.Contains("reasoning") || lk.Contains("thinking") || lk.Contains("think"))
                            dst[$"{prefix}{k}"] = v.ToString();
                    }
                }
                var props = new Dictionary<string, object?>();
                CopyStringy(lastMsg.AdditionalProperties, props, "msg.");
                CopyStringy(resp.AdditionalProperties, props, "resp.");
                if (props.Count > 0) reasoningProps = props;
            }
            catch { /* best-effort capture */ }

            Emit("llm_response", new()
            {
                ["iteration"] = state.Iteration,
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
                // WHY THE MODEL STOPPED, which until 2026-09-01 was thrown
                // away. Without it "the model finished" and "the model was cut
                // off mid-sentence" are the same event in the log, and the only
                // way to tell them apart is to notice output_tokens happens to
                // equal the configured bound -- an inference, and one that is
                // unavailable to anything reading a single event. `length` is
                // the value that matters; it is emitted even when null so a
                // provider that reports nothing is distinguishable from one
                // that reported `stop`.
                ["finish_reason"] = resp.FinishReason?.Value,
                // Full assistant message (text + any tool calls) so Raw
                // view shows the model's actual reply, not just token
                // counts. Logs view continues to render the compact
                // "X in / Y out" summary and ignores `content`.
                ["content"] = SerializeMessage(lastMsg),
                ["reasoning"] = reasoningProps,
            });

            var assistantText = lastMsg.GetText();
            var functionCalls = lastMsg.GetToolCalls();

            // ⛔⭐ A CAP HIT CAN ARRIVE AS `finish_reason: tool_calls` WITH A
            // TOOL CALL WHOSE ARGUMENTS WERE CUT.
            //
            // MEASURED 2026-09-05, EpicForge run 5 (deepseek-v4-flash on vLLM),
            // six replies on two vett builds: R-mto0ygih334s replies 18 and 20
            // and R-mto0yl506y14 reply 26 at exactly 4096 output tokens; on
            // the law-136 build R-mto1wbrod2us reply 13, R-mto1wdxw7oyq reply
            // 18 and R-mto1wg3b88oh reply 8 at exactly 12288. Every one ends
            // in a tool call whose `arguments` is null -- the LAST content
            // part, because a cap cuts the tail -- and every one came back
            // `finish_reason: tool_calls`, not `length`. The truncation branch
            // below is gated on `Length && functionCalls.Count == 0` on the
            // 09-01 premise "the provider emits complete tool_calls"; this is
            // the shape that premise did not cover. The cut lands INSIDE the
            // call's JSON, the engine's tool parser gives up, and the adapter
            // hands over a FunctionCallContent with Arguments null and the
            // parse failure in .Exception.
            //
            // WHAT IT COST. The loop ran the call anyway with `{}`: `terminal`
            // answered "Error: command is required", `file_editor` answered
            // "unknown file_editor command" (0 ms), and the model, told nothing
            // about the cap, re-emitted the same ~8k-byte heredoc (identical
            // argument hash in consecutive replies). At ~17 tok/s a 12288-token
            // cut reply is ~700 s of an 1800 s seat: all three law-136 runs
            // spent one such reply and were cancelled at 1180 s with nothing
            // landed.
            //
            // THE RULE: A CALL WHOSE ARGUMENTS NEVER ARRIVED IS NOT RUN, AND
            // THE MODEL IS TOLD WHAT CUT THEM. "At the cap" is the provider's
            // `length` OR the counted output reaching the configured cap --
            // the same field the loop installs on the wire. The cut call is
            // the tail part with no arguments to a tool that needs some (a
            // tool with no required parameters, called empty, is complete).
            // Complete siblings in the same reply still run; the cut call gets
            // a result that says it did not (every call_id gets exactly one
            // result -- the API contract); when the cut call was the ONLY
            // call, the reply is a truncation with nothing usable and takes
            // the recovery path below with a nudge that names the cut.
            //
            // The raw argument bytes are read back from the adapter's
            // RawRepresentation when it carries them, so the notice can say
            // how far the arguments ran -- and whether their tail was
            // REPEATING (a 4 KiB tail that deflates to under 12% of its size
            // is a loop, not content). Reply 8 above generated ~12,250 tokens
            // of arguments for what its own prose called "the first chunk"
            // of a 100-line file; the number decides whether the remedy is
            // "split the write" or "you were looping", and until this event
            // nothing recorded it. NO tool_call_start/end is emitted for the
            // cut call: nothing ran, and an event that said otherwise would
            // make every tool count lie (the repeated_call_break precedent).
            var cutCall = FindCutTailToolCall(lastMsg, functionCalls, resp.FinishReason, outputTokens, llm.MaxOutputTokens, caps.ToolSchemas);
            string? cutRaw = null;
            int? cutTailPct = null;
            SalvagedCreate? salvage = null;
            string? salvageReason = null;
            (string? View, bool Measured) salvagePre = (null, false);
            FunctionCallContent? salvagedCall = null;
            if (cutCall is not null)
            {
                cutRaw = RawArguments(cutCall);
                cutTailPct = TailCompressionPct(cutRaw);
                functionCalls = functionCalls.Where(fc => !ReferenceEquals(fc, cutCall)).ToList();

                // ⭐ A CUT `create` BANKS ITS PREFIX (2026-09-09, EpicForge solo
                // control 5 on deepseek-v4-flash, cap 12288). Five of 37 replies
                // were cut inside a `file_editor create`'s `file_text`; each was
                // 9-11 minutes of generation at ~20 tok/s, and each was thrown
                // away whole -- ~61k of the run's 82k output tokens landed
                // nothing, and the run ended at its 3600 s cap at 151/20 on the
                // ruler. The bytes that DID arrive were real content: complete
                // lines of a file the model was going to have to write again.
                //
                // THE RULE: when the cut call is a `file_editor create` whose
                // `path` arrived complete, whose `file_text` holds at least one
                // complete line, and whose tail is NOT repeating (a loop is not
                // content -- the compression test above decides), the loop
                // rewrites the call as a create of the COMPLETE LINES that
                // arrived and runs THAT through the ordinary tool path. The
                // sandbox refuses `create` on an existing file, so a salvage
                // can never overwrite a finished file with a prefix. The model
                // gets the tool's own result for its call_id plus a notice
                // naming the line the file stops at and telling it to APPEND
                // the rest with `insert`, never to re-create. A `terminal`
                // heredoc is never salvaged: a partial command must not run.
                salvage = SalvageCutCreate(cutCall.Name, cutRaw, cutTailPct, out salvageReason);
                if (salvage is not null)
                {
                    // Law 234: the banked body goes back through the SAME
                    // command the seat used, with the anchor it sent.
                    var args = new Dictionary<string, object?>
                    {
                        ["command"] = salvage.Command,
                        ["path"] = salvage.Path,
                        ["security_risk"] = "LOW",
                    };
                    if (salvage.Command == "create") args["file_text"] = salvage.Text;
                    else args["new_str"] = salvage.Text;
                    if (salvage.Command == "insert") args["insert_line"] = salvage.InsertLine;
                    if (salvage.Command == "str_replace") args["old_str"] = salvage.OldStr;
                    salvagedCall = new FunctionCallContent(cutCall.CallId ?? "", cutCall.Name, args);
                    functionCalls = functionCalls.Append(salvagedCall).ToList();
                    // 2026-09-09 (s12a it.19): the target's bytes BEFORE the
                    // salvaged call run, so "landed" below can be read off the
                    // file and not off a success bit (which a `str_replace`
                    // refusal did not clear).
                    salvagePre = await ViewOrNullAsync(env.Sandbox, env.SessionId, salvage.Path, ct);
                }
                Emit("truncated_tool_call", new()
                {
                    ["salvaged"] = salvage is null ? null : new Dictionary<string, object?>
                    {
                        ["path"] = salvage.Path,
                        ["chars"] = salvage.Text.Length,
                        ["lines"] = salvage.Lines,
                        ["dropped_chars"] = salvage.DroppedChars,
                        ["complete"] = salvage.Complete,
                        ["looped_lines"] = salvage.LoopedLines,
                        ["command"] = salvage.Command,
                        ["loop_signal"] = salvage.LoopSignal,
                        ["duplicate_pct"] = salvage.DuplicatePct,
                    },
                    // Law 234: a null salvage names its branch, and the
                    // head of the bytes says which command was cut.
                    ["salvage_reason"] = salvageReason,
                    ["raw_head"] = cutRaw is null ? null : cutRaw[..Math.Min(200, cutRaw.Length)],
                    ["iteration"] = state.Iteration,
                    ["tool_name"] = cutCall.Name,
                    ["call_id"] = cutCall.CallId ?? "",
                    ["output_tokens"] = outputTokens,
                    ["output_cap"] = llm.MaxOutputTokens,
                    ["finish_reason"] = resp.FinishReason?.ToString(),
                    ["sibling_calls"] = functionCalls.Count,
                    ["preceding_text_chars"] = (assistantText ?? "").Length,
                    ["raw_argument_chars"] = cutRaw?.Length,
                    // The adapter wraps the JSON failure ("Error parsing function
                    // call arguments.") -- the innermost message is the one that
                    // says WHERE the bytes stopped.
                    ["parse_error"] = cutCall.Exception is null ? null : Preview(cutCall.Exception.GetBaseException().Message),
                    ["tail_compression_pct"] = cutTailPct,
                    // The bytes themselves, so a loop can be READ, not
                    // only measured (the ratio alone was the recorder
                    // gap of law 230's calibration).
                    ["raw_tail"] = cutRaw is null ? null : cutRaw[^Math.Min(300, cutRaw.Length)..],
                    // True when SilenceBoundedChatClient cut the stream
                    // itself on a repeating tail (law 231) rather than
                    // the engine's cap; output_tokens is then unreported.
                    ["loop_aborted"] = cutCall.Exception is RepeatingArgumentsException,
                    // Law 232: > 0 when the loop was ACROSS sibling calls
                    // (the series is dropped whole); 0 for a loop inside
                    // one call; null when the engine's cap did the cutting.
                    ["loop_siblings"] = (cutCall.Exception as RepeatingArgumentsException)?.Siblings,
                    // Law 245: > 0 when the series was a literal repeat of
                    // this many distinct calls, whose first copies RAN.
                    ["loop_distinct"] = (cutCall.Exception as RepeatingArgumentsException)?.Distinct,
                    // Law 247: how many leading siblings of the series were
                    // rebuilt and RAN (the paraphrase path keeps the request
                    // now, not only the literal one).
                    ["loop_ran"] = (cutCall.Exception as RepeatingArgumentsException)?.Ran,
                    // Law 249 instrumentation: the 1-based wire index of the
                    // first sibling whose key the harness had already seen
                    // (-1 when none) -- so `loop_ran` can be read against the
                    // harness's own repeat, not an outside ruler's.
                    ["loop_first_repeat"] = (cutCall.Exception as RepeatingArgumentsException)?.FirstRepeat,
                    // Law 247: the head of EVERY sibling in the series, so a
                    // loop across calls can be read call by call (s16f's
                    // 14-call series left a 200-char head and a 300-char tail
                    // and nothing in between).
                    ["sibling_heads"] = cutCall.Exception is RepeatingArgumentsException { Siblings: > 0 } && cutRaw is not null
                        ? cutRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Length > 160 ? l[..160] : l).ToList()
                        : null,
                });
            }

            // Single-turn duplicate fan-out breaker. A model can emit the
            // same (tool, arguments) many times inside ONE assistant turn
            // — observed live: 102 identical `file_editor view Lib/Calc.cs`
            // calls in a single leader turn. The "never repeat a completed
            // action" prompt rule only constrains ACROSS turns (it reasons
            // from results already in context), so it structurally cannot
            // reach this. Collapse here: execute the first of each group,
            // replay its result to the duplicates so every call_id still
            // gets exactly one tool result (the model's API contract).
            List<(string DupId, string WinnerId)> collapsedDups = [];
            if ((env.Profile?.TurnDedupe ?? true) && functionCalls.Count > 1)
            {
                var beforeCollapse = functionCalls;
                (functionCalls, collapsedDups) = CollapseDuplicateToolCalls(functionCalls);
                if (collapsedDups.Count > 0)
                {
                    var dupIds = collapsedDups.Select(d => d.DupId).ToHashSet(StringComparer.Ordinal);

                    Emit("duplicate_tool_calls_collapsed", new()
                    {
                        ["collapsed"] = collapsedDups.Count,
                        ["executed"] = functionCalls.Count,
                        ["iteration"] = state.Iteration,
                        // WHICH tool ran away. Without this the event carries a
                        // number and nothing else, so the only way to learn what
                        // was being repeated is to re-parse the raw assistant
                        // message out of `llm_response` -- which the chat UI does
                        // not retain. A count with no subject cannot be triaged.
                        ["tool_name"] = string.Join(",", beforeCollapse
                            .Where(c => c.CallId is not null && dupIds.Contains(c.CallId))
                            .Select(c => c.Name)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(n => n, StringComparer.Ordinal)),
                    });

                    // COLLAPSING FOR EXECUTION ONLY IS AN AMPLIFIER, NOT A FIX.
                    //
                    // Until 2026-09-01 the collapse governed DISPATCH and nothing
                    // else: `lastMsg` went into `state.Messages` with all N
                    // identical calls still in it, and the replay block below
                    // added one tool result per discarded call_id. So a turn that
                    // emitted 41 copies wrote 41 calls AND 41 results into the
                    // permanent history, and the model read them back on its very
                    // next turn.
                    //
                    // That is a positive feedback loop, and it was measured end to
                    // end on EpicForge E3 run 8 (leader thread, copies of ONE poll
                    // command visible in the outgoing request):
                    //
                    //   iter 12   1 copy    ->  iter 15   4 copies, model emits 41
                    //   iter 16  45 copies  ->  iter 17  74  ->  iter 18 103
                    //   iter 20 143 copies  ->  iter 22 172  -> truncated, exhausted
                    //
                    // Leader input tokens went 11,519 -> 41,004, roughly 30k of it
                    // copies of one `cat src/aggregate.js` poll, and the run DIED
                    // on `truncated_response_exhausted` with the verifier at 13/20.
                    //
                    // The model was not being stupid and it was not disobeying its
                    // prompt. It said so, in its own words, on four consecutive
                    // turns -- "I made a mistake by firing many redundant polling
                    // calls", "I keep polling unnecessarily", "I've been polling
                    // excessively, which is wrong ... let me end the turn and
                    // wait" -- and then emitted 29 more copies each time. A
                    // hundred identical blocks in context is a stronger prior than
                    // any instruction, so NO PROMPT CHANGE CAN REACH THIS. A
                    // prompt-side A/B against it (run 7 vs run 8) moved the
                    // leader's waste 52.1% -> 50.4%: both arms shared this
                    // amplifier, so the experiment had nothing to measure.
                    //
                    // Fix: history keeps only the calls that actually ran. The
                    // duplicates never enter context, so they cannot be imitated,
                    // and -- because their call_ids are gone from the assistant
                    // message -- the provider's one-result-per-call_id contract is
                    // satisfied BY CONSTRUCTION rather than by replaying results
                    // onto ids the model should never have seen again.
                    //
                    // The `llm_response` event above is emitted BEFORE this and is
                    // deliberately left alone: the log must keep what the model
                    // actually produced. Truth in the log, hygiene in the context.
                    lastMsg = PruneCollapsedCalls(lastMsg, dupIds);
                }
            }

            // Empty-content recovery. AEON occasionally returns an
            // assistant message with `Contents.Count == 0` — no text,
            // no tool calls, no reasoning. The diagnostic 2026-05-19
            // root-cause analysis traced every observed `members=[]`
            // leader-bail failure (5 of 5 across A9/A11/A12/monolithic)
            // to this exact pattern: one empty-content response,
            // followed within 1-3 iterations by an unrecoverable
            // OperationCanceledException downstream. Speculation: the
            // empty response is a symptom of AEON hitting an MTP/parser
            // edge case that also corrupts the next request's state,
            // causing the connection to drop. Empirically the
            // cross-correlation is 1:1.
            //
            // Recovery: drop the empty message (don't poison context
            // with a no-op turn — would also tempt the same MTP edge
            // case on the next call), inject a plain user nudge, and
            // continue. Cap retries on consecutive empty responses so
            // a wedged AEON instance can't loop forever.
            const int EmptyResponseRetryCap = 3;
            if (lastMsg.Contents.Count == 0)
            {
                state.EmptyResponseRetries++;
                Emit("empty_response_recovery", new()
                {
                    ["iteration"] = state.Iteration,
                    ["retry_count"] = state.EmptyResponseRetries,
                    ["retry_cap"] = EmptyResponseRetryCap,
                });
                if (state.EmptyResponseRetries > EmptyResponseRetryCap)
                {
                    consecutiveFailureBox[0] = consecutiveTerminalFailures;
                    return Finalize(state, "empty_response_exhausted");
                }
                state.Messages.Add(Chat.User(
                    "Your previous response was empty (no text, no tool calls). " +
                    "This is a transient model glitch — please continue with the task. " +
                    "If you were about to dispatch a sub-agent or call a tool, do it now."));
                consecutiveFailureBox[0] = consecutiveTerminalFailures;
                return null;
            }

            // ⛔ A TRUNCATED RESPONSE IS NOT A FINISHED TURN.
            //
            // MEASURED 2026-09-01, epic-forge E3 run 6 (ef-team-flash,
            // max_output_tokens 4096). The leader's iteration 24 spent the
            // whole budget in a floating-point arithmetic loop --
            // "1.1 + 2.2 = 3.1? No. 1.1 + 2.2 = 3.1? ... Let me just compute:
            // 1.1 + 2.2" -- and was cut off mid-word before it emitted any
            // tool call. Downstream, a tool-call-less turn means "the model is
            // done", so the run finalised. It finalised holding a COMPLETED
            // dispatch (implementer-2-2) that had already written the two
            // modules the epic was missing, in a worktree whose bytes are only
            // promoted by accept_dispatch. The work existed and was thrown
            // away because a half-sentence was read as a final answer.
            //
            // ⛔ AND IT IS THE FIX FOR THE OPPOSITE DEFECT THAT CREATED IT.
            // With no bound, generation ran to ~173k tokens and every attempt
            // died on its 180s timeout (runs 1-4: four consecutive stalls).
            // Adding the bound fixed that and opened this. Across the ladder:
            // 0 responses hit the cap in the four unbounded runs, 6 did in run
            // 6 -- three of them the leader. So the bound stays and the
            // truncation is handled; removing the bound would just restore the
            // worse failure. A correction can over-shoot.
            //
            // ⚠ GATED ON `functionCalls.Count == 0` ON PURPOSE, AND THE
            // MEASUREMENT SAYS SO. Run 6's iterations 16 and 20 ALSO hit 4096
            // and both still carried a function_call -- the provider emits
            // complete tool_calls, so a truncated turn that produced one is
            // perfectly usable and must not be re-rolled. Only the turn where
            // the ramble consumed the entire budget before any call is lost.
            //
            // Precondition verified at the wire before this branch was written,
            // not assumed: the live endpoint returns finish_reason `length` on
            // a capped request and `stop` on an uncapped one. A recovery keyed
            // on a field the provider never sets would be a confident no-op.
            //
            // ⚠ AND A CUT TAIL CALL COUNTS AS "NO CALL". Once the cut call is
            // removed above, a reply whose only call it was has nothing usable
            // in it -- the same lost turn, arriving as `tool_calls` instead of
            // `length`. It takes this path with a nudge that names the cut.
            const int TruncatedResponseRetryCap = 3;
            if ((resp.FinishReason == ChatFinishReason.Length || cutCall is not null) && functionCalls.Count == 0)
            {
                state.TruncatedResponseRetries++;
                // 2026-09-09 (s12b it.24, law 240): the streaming client stops a
                // reply whose TEXT is a repetition loop; it arrives here as a
                // capped no-call reply with the loop's reading attached, and
                // the nudge names the loop instead of the cap.
                var proseLoop = ProseLoopOf(resp);
                // Law 244: count the prose loops in THIS streak; a truncation
                // of any other shape breaks the count without ending the streak.
                state.ConsecutiveProseLoopRecoveries = proseLoop is null ? 0 : state.ConsecutiveProseLoopRecoveries + 1;
                Emit("truncated_response_recovery", new()
                {
                    ["iteration"] = state.Iteration,
                    ["output_tokens"] = outputTokens,
                    ["retry_count"] = state.TruncatedResponseRetries,
                    ["retry_cap"] = TruncatedResponseRetryCap,
                    ["cut_tool_call"] = cutCall?.Name,
                    ["prose_loop_chars"] = proseLoop?.Chars,
                    ["prose_loop_pct"] = proseLoop?.Pct,
                    ["consecutive_prose_loops"] = state.ConsecutiveProseLoopRecoveries,
                });
                if (state.TruncatedResponseRetries > TruncatedResponseRetryCap)
                {
                    // Give up LOUDLY, with a reason of its own. Falling through
                    // to the ordinary no-tool-calls exit would record this as
                    // an ordinary completion, which is the very confusion this
                    // branch exists to end.
                    consecutiveFailureBox[0] = consecutiveTerminalFailures;
                    return Finalize(state, "truncated_response_exhausted");
                }

                // Drop the truncated text rather than keeping it, same as the
                // empty-response path above. The observed content is a
                // degenerate repetition loop; re-feeding it invites the model
                // to continue the loop, and a half-finished sentence is not
                // context worth its tokens.
                //
                // The nudge names the mechanism instead of saying "try again",
                // because the recovery has to change the behaviour that caused
                // the truncation -- a model that rambles identically the second
                // time just burns the cap.
                //
                // And it names the CAP, and covers the reply that WAS the answer.
                // Measured live 2026-09-05 (EpicForge architect, cap 12288, 55k
                // context): the same final plan was re-emitted at exactly 12288
                // tokens three times in a row after three of these nudges. "Tool
                // call FIRST" is no instruction to a reply that was never going
                // to contain one; a re-roll that is not told to be smaller is a
                // replay. The number is the one the loop already holds; when the
                // profile sets none, the provider's limit is unknown and no
                // number is invented.
                // Law 244 (batch 14, 2026-09-09, s14c it.41-44): the prose loop
                // is a property of the CONTEXT, not of one reply. Every batch-14
                // arm that fired law 240 more than twice fired it after ~2,000 s
                // at 50k-78k input tokens -- below the 100k compaction threshold,
                // so nothing ever changed the context between retries -- and
                // s14c's retry_count went 1, 2, 3, 4 on four consecutive
                // iterations into `truncated_response_exhausted` at 3,570 s of
                // a 3,600 s cap. A retry into the context that produced the
                // loop is a replay. At the second consecutive prose loop the
                // history is compacted to the profile's digest FIRST (the same
                // checkpoint `/compact` runs, threshold 0 so it always fires),
                // and the nudge tells the seat so. The retry cap is unchanged:
                // this buys the retry a different context, not more retries.
                var compactedForProseLoop = false;
                if (proseLoop is not null && state.ConsecutiveProseLoopRecoveries >= ProseLoopCompactAfter)
                {
                    var beforeCompact = state.Messages.Count;
                    var compact = Vett.Tools.CompactionMiddleware.MilestoneCheckpoint(
                        tokenThreshold: 0,
                        keepLastMessages: compactRequest?.KeepLastMessages ?? 5,
                        model: llm.Model,
                        sessionLogDir: compactRequest?.SessionLogDir);
                    await compact(state, ct);
                    Emit("compacted", new()
                    {
                        ["reason"] = "prose_loop_retry",
                        ["before"] = beforeCompact,
                        ["after"] = state.Messages.Count,
                        ["grew"] = state.Messages.Count > beforeCompact,
                        ["consecutive_prose_loops"] = state.ConsecutiveProseLoopRecoveries,
                        ["retry_count"] = state.TruncatedResponseRetries,
                    });
                    state.ConsecutiveProseLoopRecoveries = 0;
                    compactedForProseLoop = true;
                }
                var capPhrase = CapPhrase(llm.MaxOutputTokens);
                var fitPhrase = FitPhrase(llm.MaxOutputTokens);
                var nudge = proseLoop is { } pl
                    ? (compactedForProseLoop ? ProseLoopCompactedPreamble : "") + ProseLoopNudge(pl.Chars, pl.Pct)
                    : cutCall is not null
                    ? CutToolCallNudge(cutCall.Name, llm.MaxOutputTokens, outputTokens, cutRaw, cutTailPct, (assistantText ?? "").Length, cutCall.Exception as RepeatingArgumentsException)
                    : "Your previous reply hit " + capPhrase + " and was cut off before it "
                    + "produced a tool call, so none of it was used. Do NOT repeat that reasoning. "
                    + "If you were working towards a tool call, make the tool call FIRST and keep any "
                    + "explanation to one or two sentences; if you were working something out in "
                    + "prose, do it in the terminal instead — that is what it is for. "
                    + "If that reply WAS your answer, the same answer will be cut off again: send a "
                    + "shorter version that fits " + fitPhrase + " — keep every decision, cut the "
                    + "prose around it. A tool call you have ALREADY made, whose result is already in "
                    + "this conversation, must not be made again — use the result you have.";
                state.Messages.Add(Chat.User(nudge));
                consecutiveFailureBox[0] = consecutiveTerminalFailures;
                return null;
            }

            // Detect "model tried to emit a tool call but the wire-format
            // parser couldn't decode it" — the response has zero structured
            // tool_calls but the assistant text is a short stub of
            // tag-shaped junk that's clearly a failed tool-call attempt.
            // Seen on aeon-mtp + qwen3_coder parser at temp 0.3 in three
            // distinct shapes so far:
            //   1. "<tool_call>file_editor:::content>"  qwen3_coder XML
            //      start + garbled mid-call
            //   2. "<file_editor>"                      bare tool-name tag
            //   3. "[view]"                             bracketed shorthand
            // Without this branch, AgentLoop's no-tool-calls early-exit
            // fires "completed" with zero work done and the leader is
            // fooled into thinking the dispatch finished — exactly the
            // failure pattern the README dispatch test reproduced 3×.
            // Instead: drop the malformed message (don't poison context),
            // nudge the model to retry via the structured channel, and
            // continue. Cap retries so a consistently-broken model can't
            // loop forever.
            //
            // Heuristic: short response (<100 tokens) + no tool_calls +
            // EITHER explicit qwen3_coder markers in the body OR the
            // trimmed reply starts with a tag-opener character (`<` or
            // `[`). A real "I'm done" final response is plain prose and
            // doesn't start with a bracket; a real bracket-discussion
            // response wouldn't be 13 tokens. False-positives are
            // recoverable (the nudge re-prompts the model) so we err
            // toward catching more.
            const int MalformedToolCallRetryCap = 3;
            var trimmedText = assistantText?.TrimStart() ?? "";
            var hasToolCallMarkers = assistantText is not null
                && (assistantText.Contains("<tool_call>")
                    || assistantText.Contains("<function=")
                    || assistantText.Contains("</tool_call>"));

            // ── DEEPSEEK DSML CONTROL-TOKEN DRIFT ────────────────────────────
            //
            // MEASURED 2026-08-26 (team-fanout-tier2/fan5, ds-team-flash). The
            // leader emitted FIVE check_task calls as plain text:
            //
            //   "I need to wait for the background tasks to complete. Let me
            //    check again.\n\n<｜DSML｜tool_calls>\n<｜DSML｜invoke
            //    name="check_task">\n<｜DSML｜parameter name="task_id" …
            //
            // NONE of the heuristics above fire on that, and every miss is for
            // a different reason:
            //   • it STARTS WITH PROSE, so startsWithStructuralOpener is false
            //     (trimmedText[0] is 'I', not '<');
            //   • hasToolCallMarkers only knows the qwen3_coder vocabulary
            //     (<tool_call> / <function=), and DSML is neither;
            //   • it is hundreds of tokens, so both the <100-token cap and the
            //     <30-token bare-name heuristic exclude it;
            //   • it doesn't start with a tool or parameter name either.
            //
            // So the run recorded an ordinary prose reply and the suite's
            // `no_event: malformed_tool_call` assertion PASSED — vacuously.
            // The gate was blind to the one drift shape the project's PRIMARY
            // model family actually produces. That is worse than the drift:
            // an unmeasured failure mode reads exactly like a healthy run.
            //
            // U+FF5C (FULLWIDTH VERTICAL LINE) wrapping the literal "DSML" is
            // a DeepSeek control-token spelling that leaked into the content
            // channel. No prose reply contains it, so this is an unambiguous
            // structural signal and skips the length cap — the same standing
            // `<tool_call>`-at-position-0 already gets.
            //
            // ⚠ THIS CHANGES BEHAVIOUR, not just measurement: firing the event
            // also arms the retry counter, so a model that drifts more than
            // MalformedToolCallRetryCap times now ENDS the run instead of
            // limping on. That is what the cap is for, and the recovery below
            // is the good path here — DSML names the intended tool inline, so
            // tool_choice gets pinned to a real function and the constrained
            // decoder produces a valid call. Contrast the prose-then-
            // `<tool_call>` mode documented further down, where recovery does
            // NOT converge and deliberately does not fire.
            const string DsmlMarker = "｜DSML｜";
            var hasDsmlDrift = assistantText is not null
                && assistantText.Contains(DsmlMarker, StringComparison.Ordinal);
            // Cover every "model tried to manually serialize a tool call
            // as plain text" shape we've observed on aeon-mtp:
            //   `<tool_call>...`        qwen3_coder XML start
            //   `<function=...`         qwen3_coder function tag
            //   `<file_editor>`         bare tool-name tag
            //   `[view]`                square-bracket shorthand
            //   `` ```json\n{...} ``    markdown JSON code block
            //   `{"command":...}`       raw JSON
            //   `file_editor`           bare tool name as a word
            //
            // First three groups: structural openers + qwen3_coder tags.
            // Last group: a SHORT response (~<30 tokens) that contains a
            // known tool-name as text but no actual tool_call. That means
            // the model thought "I'll call file_editor" and emitted just
            // the name — the parser had nothing structured to work with.
            //
            // A `{` or a fence at position 0 is ALSO how a short JSON ANSWER
            // starts. Measured live 2026-09-05 (EpicForge's law-44 reader, told
            // "answer with ONE fenced JSON object"): 32 of 84 reader runs had
            // their 52-79-token `{ "claim_holds": ... }` verdict flagged here,
            // nudged, re-emitted and flagged again; 17 ended the run with the
            // answer never delivered. The two shapes differ in VOCABULARY, not
            // in their first character: a botched tool call names a registered
            // tool, or one of its parameters as a JSON key; an answer names
            // neither. So the JSON-shaped openers are gated on that vocabulary
            // once it is built below (structuralOpenerLooksLikeToolCall). `<`
            // and `[` keep their unconditional standing -- no answer to a
            // question opens with a tag.
            var startsWithTagOpener = trimmedText.Length > 0
                && (trimmedText[0] == '<' || trimmedText[0] == '[');
            var startsWithJsonOpener = trimmedText.Length > 0
                && (trimmedText[0] == '{' || trimmedText[0] == '`');
            var startsWithStructuralOpener = startsWithTagOpener || startsWithJsonOpener;
            var looksLikeBareToolNameAttempt = false;
            if (!startsWithStructuralOpener && !hasToolCallMarkers
                && outputTokens > 0 && outputTokens < 30
                && trimmedText.Length > 0)
            {
                // Short reply that just MENTIONS a tool name — model
                // emitted the name without any structured call.
                foreach (var s in caps.ToolSchemas)
                {
                    var inner = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("function", out var fn) ? fn : s;
                    if (inner.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        var name = n.GetString();
                        if (!string.IsNullOrEmpty(name) && assistantText.Contains(name, StringComparison.Ordinal))
                        {
                            looksLikeBareToolNameAttempt = true;
                            break;
                        }
                    }
                }
            }
            // Function-call-as-text shape: the response starts with a
            // known tool name OR a known parameter name immediately
            // followed by space, `(`, `:`, newline, or `{`. Examples on
            // aeon-mtp (each was a real failed dispatch):
            //   `file_editor create file_text="..." path="README.md"`
            //   `terminal(command="ls -la")`
            //   `file_editor\n{"command":"create",...}`
            //   `file_text: # TestRepo\n\n...`            (parameter-name shape)
            //   `path: README.md`                          (parameter-name shape)
            // A real prose reply never starts with a tool/parameter name
            // followed by call-syntax punctuation, so no length cap is
            // needed — the model may emit hundreds of tokens of pseudo-
            // call syntax and we still want to catch it.
            var startsWithToolNameInvocation = false;
            // Capture the tool name the model APPEARS to be trying to
            // call (extracted from any of the malformed shapes). Used
            // to force-pin the next call's tool_choice to that name so
            // vLLM's constrained decoder produces a valid invocation.
            string? apparentToolName = null;
            // Build the set of (toolName, paramNames) once so we can
            // both detect and attribute the apparent tool.
            var toolNamesByLength = new List<string>();        // tool function names
            var paramNamesByLength = new List<string>();        // parameter names (file_text, path, command, …)
            var paramToToolMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in caps.ToolSchemas)
            {
                var inner = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("function", out var fn) ? fn : s;
                string? toolName = null;
                if (inner.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    toolName = n.GetString();
                    if (!string.IsNullOrEmpty(toolName)) toolNamesByLength.Add(toolName);
                }
                if (inner.TryGetProperty("parameters", out var p)
                    && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("properties", out var props)
                    && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in props.EnumerateObject())
                    {
                        paramNamesByLength.Add(prop.Name);
                        if (toolName is not null && !paramToToolMap.ContainsKey(prop.Name))
                            paramToToolMap[prop.Name] = toolName;
                    }
                }
            }
            // Long-first matching so `file_editor` matches before `file`.
            toolNamesByLength.Sort((a, b) => b.Length.CompareTo(a.Length));
            paramNamesByLength.Sort((a, b) => b.Length.CompareTo(a.Length));

            // The vocabulary gate for the JSON-shaped openers (see the note at
            // startsWithJsonOpener): a `{`/fenced reply counts as a tool-call
            // attempt only if it names a registered tool anywhere, or uses a
            // registered parameter as a JSON key. `"claim_holds": true` names
            // neither; `{"command": "ls -la"}` names `command`.
            var namesToolVocabulary = false;
            if (startsWithJsonOpener && assistantText is not null)
            {
                namesToolVocabulary = toolNamesByLength.Any(n => assistantText.Contains(n, StringComparison.Ordinal));
                if (!namesToolVocabulary)
                {
                    foreach (System.Text.RegularExpressions.Match km in JsonKeyRe.Matches(assistantText))
                    {
                        if (paramNamesByLength.Contains(km.Groups[1].Value, StringComparer.Ordinal))
                        {
                            namesToolVocabulary = true;
                            break;
                        }
                    }
                }
            }
            var structuralOpenerLooksLikeToolCall = startsWithTagOpener || (startsWithJsonOpener && namesToolVocabulary);

            if (!hasToolCallMarkers && !startsWithStructuralOpener
                && trimmedText.Length > 0 && functionCalls.Count == 0)
            {
                // Tool-name first (priority over param-name; tool names
                // are stronger evidence of which call the model meant).
                foreach (var name in toolNamesByLength)
                {
                    if (!trimmedText.StartsWith(name, StringComparison.Ordinal)) continue;
                    if (trimmedText.Length == name.Length || IsCallSyntaxBoundary(trimmedText[name.Length]))
                    {
                        startsWithToolNameInvocation = true;
                        apparentToolName = name;
                        break;
                    }
                }
                if (!startsWithToolNameInvocation)
                {
                    foreach (var pn in paramNamesByLength)
                    {
                        if (!trimmedText.StartsWith(pn, StringComparison.Ordinal)) continue;
                        if (trimmedText.Length == pn.Length || IsCallSyntaxBoundary(trimmedText[pn.Length]))
                        {
                            startsWithToolNameInvocation = true;
                            paramToToolMap.TryGetValue(pn, out apparentToolName);
                            break;
                        }
                    }
                }
            }
            // DSML names the intended tool INLINE (`invoke name="check_task"`),
            // so read it from there rather than scanning the whole body for any
            // known tool name the way the generic backfill below does. That
            // backfill picks the LONGEST tool name appearing anywhere in the
            // text, which on a prose-then-DSML reply can easily land on a tool
            // the model merely mentioned. A wrong tool_choice pin is worse than
            // no pin at all: it FORCES the next call to be the wrong function.
            // Only a candidate that is a real registered tool is accepted —
            // pinning tool_choice to a name the server doesn't know would fail
            // the request outright.
            if (apparentToolName is null && hasDsmlDrift && assistantText is not null)
            {
                const string nameAttr = "invoke name=\"";
                var at = assistantText.IndexOf(nameAttr, StringComparison.Ordinal);
                if (at >= 0)
                {
                    var s = at + nameAttr.Length;
                    var e = assistantText.IndexOf('"', s);
                    if (e > s && toolNamesByLength.Contains(assistantText[s..e], StringComparer.Ordinal))
                        apparentToolName = assistantText[s..e];
                }
            }
            // Backfill apparentToolName for the OTHER detection branches
            // when their match implies a specific tool (e.g. `<file_editor>`
            // or `[view]` reveal the intended tool name in the body).
            if (apparentToolName is null && functionCalls.Count == 0
                && (hasToolCallMarkers || structuralOpenerLooksLikeToolCall || looksLikeBareToolNameAttempt))
            {
                foreach (var name in toolNamesByLength)
                {
                    if (assistantText is not null
                        && assistantText.Contains(name, StringComparison.Ordinal))
                    {
                        apparentToolName = name;
                        break;
                    }
                }
            }
            // A response that LITERALLY starts with `<tool_call>` or
            // `<function=` is unambiguous qwen3_coder drift — no real
            // prose reply ever starts that way. Skip the length cap on
            // this case (covers t5/t15 where the entire response is a
            // long XML-shaped pseudo-tool-call). Tried also flagging
            // `<tool_call>` ANYWHERE in the response, but that triggered
            // recovery retries which then induced WORSE output ("garbage"
            // shapes like `<tool_call>\nuser\n<tool_call>` on retry) —
            // for the prose-then-drift mode the recovery loop doesn't
            // converge, so it's better to NOT fire and just let the
            // dispatch end as completed-with-no-work; the leader can
            // re-dispatch a fresh implementer.
            var trimmedStartsWithToolCallTag = trimmedText.StartsWith("<tool_call>", StringComparison.Ordinal)
                || trimmedText.StartsWith("<function=", StringComparison.Ordinal);
            // Length cap applies to the older heuristics that risk false-
            // positives on legitimate prose; structural signals
            // (startsWithToolNameInvocation, trimmedStartsWithToolCallTag)
            // are strong enough to skip the cap.
            var triggered =
                startsWithToolNameInvocation
                || trimmedStartsWithToolCallTag
                || hasDsmlDrift
                || (outputTokens > 0 && outputTokens < 100
                    && (hasToolCallMarkers || structuralOpenerLooksLikeToolCall || looksLikeBareToolNameAttempt));
            if (functionCalls.Count == 0 && triggered)
            {
                state.MalformedToolCallRetries++;
                Emit("malformed_tool_call", new()
                {
                    ["iteration"] = state.Iteration,
                    ["output_tokens"] = outputTokens,
                    ["content_preview"] = assistantText.Length > 200 ? assistantText[..200] : assistantText,
                    ["retry_count"] = state.MalformedToolCallRetries,
                    ["retry_cap"] = MalformedToolCallRetryCap,
                });

                if (state.MalformedToolCallRetries > MalformedToolCallRetryCap)
                {
                    consecutiveFailureBox[0] = consecutiveTerminalFailures;
                    return Finalize(state, "malformed_tool_call");
                }

                // Recovery: drop the malformed assistant message (would
                // pollute context and seed more bad output). If we
                // identified which tool the model was trying to call,
                // pin the NEXT call to that tool via tool_choice — vLLM's
                // constrained decoder will produce a structurally valid
                // invocation by token-by-token enforcement (verified
                // 2026-05-08: specific-tool path takes ~5s vs 60s+ hang
                // on the union-grammar `tool_choice="required"` path on
                // aeon-mtp + MTP). When we couldn't identify a tool name
                // (rare — most failure shapes contain one), fall back to
                // a plain-text nudge so the model at least sees explicit
                // feedback before retrying.
                if (!string.IsNullOrEmpty(apparentToolName))
                {
                    state.ForceToolNameNextCall = apparentToolName;
                }
                else
                {
                    state.Messages.Add(Chat.User(
                        "Your previous response was malformed: you emitted tool-call syntax as plain text in the message body " +
                        "instead of via the structured tool_calls field. Examples of the wrong-shape outputs seen here: " +
                        "`file_editor create file_text=\"...\"`, `<file_editor>{...}`, `<tool_call>...`, `[view]`, " +
                        "`file_text: ...`, ```` ```json\\n{...}` ````, and `<｜DSML｜tool_calls>` / " +
                        "`<｜DSML｜invoke name=\"...\">`. Do NOT write any of those literally — they " +
                        "are syntactically wrong and the parser rejects them. Use the dedicated function-call mechanism " +
                        "your client provides; your response body should be either plain prose OR empty (the tool call " +
                        "lives in a separate channel). Retry the action now."));
                }
                consecutiveFailureBox[0] = consecutiveTerminalFailures;
                return null;
            }

            // ⛔⭐ A REAL REPLY ZEROES THE RECOVERY STREAKS. The three caps
            // above (empty / truncated / malformed, 3 each) were written as
            // CONSECUTIVE counts -- "Cap retries on consecutive empty
            // responses"; "a model that truncates every single time must not
            // loop forever" -- and implemented as LIFETIME counts: until this
            // line there was no `= 0` anywhere in the tree.
            //
            // MEASURED 2026-09-05 (EpicForge E8 `E-pg-e8-flat`, run
            // R-mtnoneab2it5, implementer-1): four truncations spread across a
            // 32-iteration session, never two in a row, twenty-odd healthy tool
            // turns between them, ended the seat `truncated_response_exhausted`
            // at 1104 s -- 50% of the epic's wall clock -- and a second seat
            // then did the whole job. EpicForge law 118 recorded the same
            // mechanism ending an architect at iteration 26 with its verdict
            // half-written. A lifetime budget turns "this model occasionally
            // overruns" into "this seat dies at its fourth overrun, whenever
            // that is", and the longer and more productive the session, the
            // more certain the death.
            //
            // Reaching this line means the turn was a real reply: not empty,
            // not truncated without a call, not a malformed call. Each streak
            // starts over here. A model that fails EVERY time still exhausts in
            // exactly cap+1, as before (TruncatedResponseRecoveryTests pins
            // that count). NoToolEngagementRetries is deliberately NOT reset:
            // it is not a failure streak but the number of times a text-only
            // reply has been asked "are you sure?", and it defines the
            // completed null; LeaderStuckRetries likewise.
            state.TruncatedResponseRetries = 0;
            state.EmptyResponseRetries = 0;
            state.MalformedToolCallRetries = 0;
            state.ConsecutiveProseLoopRecoveries = 0;

            // Add assistant message to history.
            state.Messages.Add(lastMsg);
            state.LastObservations = [];

            // Dispatch tool calls — parallel when multiple.
            if (functionCalls.Count == 1)
            {
                var fc = functionCalls[0];
                var args = ExtractArgs(fc);
                var signature = RepeatSignature(fc.Name, args);

                // ⛔⭐ THE SAME CALL FOR THE SAME ANSWER IS NOT RUN A THIRD TIME.
                // Same E8 seat: 13 tool calls, 7 of them the IDENTICAL 217-byte
                // `terminal` payload ("Write hasBom.js with correct method
                // name"), each returning the same nothing, each followed by a
                // paragraph of narration about trying again. Nothing in the
                // loop interrupts that: StuckDetector counts error TURNS and
                // text-only monologues, and a successful identical write seven
                // times over is neither. The epic-forge runner SAW the streak
                // (its `stuck_seats` metric: tool, argument, byte count) after
                // the fact, and nothing acted on it.
                //
                // The rule: a sole call that repeats the previous sole call
                // byte-for-byte, whose previous two runs returned byte-identical
                // output, is not run. The model gets the output it already has
                // back with a notice that says so, `repeated_call_break`
                // records it (NO tool_call_start/end -- nothing ran, and an
                // event that said otherwise would make every tool count lie),
                // and the fifth identical issue ends the run
                // `repeated_call_exhausted`: a seat that ignores two explicit
                // notices is stuck by any definition, and freeing it is what
                // lets a second seat do the work. That stop is also what keeps
                // the consecutive reset above from letting the E8 shape run to
                // max_iterations: [write, truncate, write, truncate, ...] never
                // exhausts a consecutive truncation count, so the repeat is
                // what has to end it.
                //
                // What is NEVER a repeat: a poll that is still waiting --
                // `check_task` / `wait_task`, or any result carrying
                // Builtins.StillRunningMarker -- (the same benign-wait rule
                // StuckDetector had to learn; breaking it kills every leader
                // that waits on a slow member); a call whose RESULT changes
                // (a progressing poll -- `tail` on a growing log); and anything
                // in a turn that fans out, which resets the streak.
                if (state.IdenticalCallStreak >= RepeatedCallExecuteCap - 1
                    && signature == state.LastSoleCallSignature
                    && !IsRepeatExempt(fc.Name, state.LastSoleCallResult))
                {
                    state.IdenticalCallStreak++;
                    var timesIssued = state.IdenticalCallStreak + 1;
                    var prior = state.LastSoleCallResult ?? "";

                    // ⛔⭐ LAW 248 (EpicForge batches 15-16, 2026-09-09): THE
                    // REFUSAL WAS A REPLAY. s15c, s15g, s16b and s16h all ended
                    // `repeated_call_exhausted` with 15-35 min of cap unspent:
                    // s16b said "I'll write 20 genuinely distinct facts" and
                    // sent a `str_replace` whose `new_str` equalled its
                    // `old_str` -- the same 1,548 bytes -- five times; s16h
                    // wrote the same 475-byte `pieces.js` heredoc five times,
                    // narrating a different next step between them. The notice
                    // told the seat to do something different, and the seat
                    // believed it was: its context held the identical body
                    // four times over and the sampler produced it a fifth.
                    // Law 244's finding for prose loops, at the call site: a
                    // retry into the context that produced the repeat is a
                    // replay. At the FIRST refusal the history is compacted to
                    // the digest (the same checkpoint `/compact` runs), the
                    // pending call is kept so its refusal has a home, and the
                    // notice says so. The exhaustion count is unchanged: this
                    // buys the retry a different context, not more retries --
                    // a seat that repeats twice more INTO THE DIGEST is stuck.
                    var compactedForRepeat = false;
                    if (timesIssued == RepeatedCallExecuteCap + 1 && timesIssued < RepeatedCallExhaustAt)
                    {
                        var beforeCompact = state.Messages.Count;
                        var compact = Vett.Tools.CompactionMiddleware.MilestoneCheckpoint(
                            tokenThreshold: 0,
                            keepLastMessages: compactRequest?.KeepLastMessages ?? 5,
                            model: llm.Model,
                            sessionLogDir: compactRequest?.SessionLogDir);
                        await compact(state, ct);
                        // The checkpoint trims a leading assistant-with-calls
                        // as an orphan; the refused call's message must stay
                        // so the tool result below has a call to answer.
                        if (!ReferenceEquals(state.Messages.LastOrDefault(), lastMsg))
                            state.Messages.Add(lastMsg);
                        // ⛔⭐ LAW 254 (EpicForge batch 21 s21c, 2026-09-09): THE
                        // TAIL WAS THE REPLAY. The checkpoint keeps the last
                        // few messages, and when the repeat is a sole call per
                        // turn those messages ARE the repeated call: s21c's
                        // 65 -> 9 compaction kept its it.28 and it.29 `create`
                        // (4,409 identical bytes each) plus the pending it.30
                        // copy -- three verbatim bodies under a notice saying
                        // the body was gone -- and the seat sent them a 4th
                        // and 5th time into the digest. The digest is the
                        // context now; the body must not survive beside it.
                        // Every kept call with the refused signature keeps its
                        // id and name and loses its string arguments to an
                        // `<omitted: N chars ...>` marker; the disk is the truth.
                        var redacted = RedactRepeatBodies(state.Messages, signature);
                        Emit("compacted", new()
                        {
                            ["reason"] = "repeated_call_retry",
                            ["before"] = beforeCompact,
                            ["after"] = state.Messages.Count,
                            ["grew"] = state.Messages.Count > beforeCompact,
                            ["times_issued"] = timesIssued,
                            ["redacted_bodies"] = redacted,
                        });
                        compactedForRepeat = true;
                    }
                    Emit("repeated_call_break", new()
                    {
                        ["iteration"] = state.Iteration,
                        ["tool_name"] = fc.Name,
                        ["call_id"] = fc.CallId ?? "",
                        ["arguments"] = args,
                        ["times_issued"] = timesIssued,
                        ["exhaust_at"] = RepeatedCallExhaustAt,
                        ["prior_result_preview"] = Preview(prior),
                        ["compacted"] = compactedForRepeat,
                    });
                    var notice = RepeatedCallNotice(fc.Name, timesIssued, prior, compactedForRepeat);
                    state.LastObservations.Add(new Observation
                    {
                        ToolCallId = fc.CallId ?? "", ToolName = fc.Name, Result = notice, Success = false,
                    });
                    state.Messages.Add(Chat.ToolResult(fc.CallId ?? "", notice));
                    if (timesIssued >= RepeatedCallExhaustAt)
                    {
                        consecutiveFailureBox[0] = consecutiveTerminalFailures;
                        return Finalize(state, "repeated_call_exhausted");
                    }
                }
                else
                {
                    var obs = await DispatchAsync(caps.Tools, env.Sandbox, env.SessionId,
                        fc.CallId ?? "", fc.Name, args, Emit, env.PermissionGate, env.Profile, ct);
                    state.IdenticalCallStreak =
                        signature == state.LastSoleCallSignature && obs.Result == state.LastSoleCallResult
                            ? state.IdenticalCallStreak + 1
                            : 0;
                    state.LastSoleCallSignature = signature;
                    state.LastSoleCallResult = obs.Result;
                    state.LastObservations.Add(obs);
                    state.Messages.Add(Chat.ToolResult(fc.CallId ?? "", obs.Result));
                }
            }
            else if (functionCalls.Count > 1)
            {
                // Parallel dispatch — all tool calls run concurrently.
                //
                // 2026-09-09 (s13g it.8, batch 13, VETT da4e8fa): all but the
                // SALVAGED call. The seat's reply 8 carried a complete
                // `create src/facts/facts-01.js` and, cut at the cap, a second
                // create of the SAME path (it had changed its mind mid-reply);
                // the salvage was appended to this list and raced its own
                // sibling -- whichever won, the other was refused
                // `file_exists` -- and `bytes_changed` below, whose before-view
                // was taken at parse time, reported the sibling's write as the
                // salvage's. The siblings run first (they are the seat's own
                // complete calls), the target is viewed, THEN the salvaged
                // call runs alone, so its landing check measures only itself.
                var parallelCalls = salvagedCall is null
                    ? functionCalls
                    : functionCalls.Where(fc => !ReferenceEquals(fc, salvagedCall)).ToList();
                // 2026-09-09 LAW 251 (batch 18 s18a it.18, VETT 6fe9e6b; the
                // same shape on s17b it.52, s17d it.12/22, s17e it.31, s17f
                // it.26, s18h it.8/17): the seat sent `rm -f src/facts/facts-01.js`
                // and, beside it, `create src/facts/facts-01.js`. Through
                // Task.WhenAll the create finished FIRST (0 ms, `file_exists`),
                // the rm landed 52 ms later, and the banked facts file was gone
                // from the tree. The seat wrote the calls in the only order that
                // works; the thread pool picked another. Siblings that touch the
                // workspace run one at a time in wire order. A turn whose
                // siblings are ALL leader tools still fans out -- that
                // concurrency is what "10-20 members under a manager" is made of.
                Observation[] results;
                if (parallelCalls.All(fc => LeaderFanOutTools.Contains(fc.Name)))
                {
                    var tasks = parallelCalls.Select(fc =>
                    {
                        var args = ExtractArgs(fc);
                        return DispatchAsync(caps.Tools, env.Sandbox, env.SessionId,
                            fc.CallId ?? "", fc.Name, args, Emit, env.PermissionGate, env.Profile, ct);
                    }).ToArray();
                    results = await Task.WhenAll(tasks);
                }
                else
                {
                    var ordered = new List<Observation>(parallelCalls.Count);
                    foreach (var fc in parallelCalls)
                        ordered.Add(await DispatchAsync(caps.Tools, env.Sandbox, env.SessionId,
                            fc.CallId ?? "", fc.Name, ExtractArgs(fc), Emit, env.PermissionGate, env.Profile, ct));
                    results = ordered.ToArray();
                }

                foreach (var obs in results)
                {
                    state.LastObservations.Add(obs);
                    state.Messages.Add(Chat.ToolResult(obs.ToolCallId, obs.Result));
                }

                if (salvagedCall is not null && salvage is not null)
                {
                    salvagePre = await ViewOrNullAsync(env.Sandbox, env.SessionId, salvage.Path, ct);
                    var obs = await DispatchAsync(caps.Tools, env.Sandbox, env.SessionId,
                        salvagedCall.CallId ?? "", salvagedCall.Name, ExtractArgs(salvagedCall), Emit, env.PermissionGate, env.Profile, ct);
                    state.LastObservations.Add(obs);
                    state.Messages.Add(Chat.ToolResult(obs.ToolCallId, obs.Result));
                }

                // A fan-out turn is not a sole call; the repeat streak starts over.
                state.LastSoleCallSignature = null;
                state.LastSoleCallResult = null;
                state.IdenticalCallStreak = 0;
            }

            // The cut tail call (see the detection above) is still in
            // `lastMsg` -- the model's message as it sent it, and the complete
            // siblings beside it were real work worth keeping -- so its
            // call_id gets its one result: the truthful one. Nothing ran, so
            // no tool_call_start/end.
            if (cutCall is not null && salvage is null)
            {
                var notice = CutToolCallResult(cutCall.Name, llm.MaxOutputTokens, outputTokens, cutRaw, cutTailPct, cutCall.Exception as RepeatingArgumentsException);
                state.LastObservations.Add(new Observation
                {
                    ToolCallId = cutCall.CallId ?? "", ToolName = cutCall.Name, Result = notice, Success = false,
                });
                state.Messages.Add(Chat.ToolResult(cutCall.CallId ?? "", notice));
            }
            else if (cutCall is not null && salvage is not null)
            {
                // The salvaged call ran above and its call_id already has the
                // tool's own result. This tells the model WHAT was banked and
                // what to do next; without it the model reads "File created"
                // and believes the whole file landed.
                //
                // LAW 236 (s10a it.20, 2026-09-09 08:00Z, VETT 6c09abd): the
                // tool can REFUSE the salvaged call -- a `create` on a path
                // that already existed came back `file_exists` after 12,288
                // tokens / 735 honest lines -- and this notice still said the
                // lines "were written". The seat believed it, then `rm`'d the
                // file and started over from nothing. Read the tool's verdict
                // for the call_id BEFORE speaking: when it refused, bank the
                // text beside its target so nothing generated is lost, and say
                // exactly what landed (nothing) and where the text went.
                var verdict = state.LastObservations.LastOrDefault(o => o.ToolCallId == (cutCall.CallId ?? ""));
                // 2026-09-09 (s12a it.19, batch 12): `verdict.Success` was TRUE
                // for a `str_replace_no_match` -- the sandbox's refusal carried
                // no `Error:` prefix -- and this said `landed:true` over a file
                // whose bytes had not moved. The tool's word is necessary, not
                // sufficient: the target is viewed again and "landed" also
                // requires that its bytes CHANGED. A sandbox that cannot view
                // (bytes_changed null) leaves the verdict to decide: could not
                // measure is not measured unchanged.
                var toolOk = verdict?.Success ?? false;
                var salvagePost = await ViewOrNullAsync(env.Sandbox, env.SessionId, salvage.Path, ct);
                bool? bytesChanged = (salvagePre.Measured && salvagePost.Measured)
                    ? !string.Equals(salvagePre.View, salvagePost.View, StringComparison.Ordinal)
                    : null;
                var landed = toolOk && (bytesChanged ?? true);
                string? sidePath = null;
                if (!landed)
                    sidePath = await BankSalvageAsideAsync(env.Sandbox, env.SessionId, salvage, cutCall.CallId ?? "", ct);
                Emit("truncated_tool_call_salvaged", new()
                {
                    ["iteration"] = state.Iteration,
                    ["call_id"] = cutCall.CallId ?? "",
                    ["path"] = salvage.Path,
                    ["lines"] = salvage.Lines,
                    ["chars"] = salvage.Text.Length,
                    ["dropped_chars"] = salvage.DroppedChars,
                    ["complete"] = salvage.Complete,
                    ["looped_lines"] = salvage.LoopedLines,
                    ["command"] = salvage.Command,
                    ["loop_signal"] = salvage.LoopSignal,
                    ["duplicate_pct"] = salvage.DuplicatePct,
                    ["landed"] = landed,
                    ["side_path"] = sidePath,
                    ["tool_success"] = toolOk,
                    ["bytes_changed"] = bytesChanged,
                    ["tool_error"] = landed ? null : Preview((verdict?.Result ?? "").Length > 0 ? verdict!.Result : "target unchanged after the salvaged call"),
                });
                state.Messages.Add(Chat.User(landed
                    ? SalvageNotice(salvage, llm.MaxOutputTokens, outputTokens, cutCall.Exception is RepeatingArgumentsException, cutRaw?.Length)
                    : SalvageRefusedNotice(salvage, verdict?.Result ?? "", sidePath, llm.MaxOutputTokens, outputTokens)));
            }

            // NO REPLAY. The collapsed duplicates were pruned out of `lastMsg`
            // before it was appended above, so there are no dangling call_ids
            // left to satisfy -- and adding results for them would put the
            // repetition straight back into the context the prune just cleaned.
            // See the amplifier note at the collapse site.

            // Run middleware. Detect auto-compaction by watching the
            // message count shrink — milestone_checkpoint replaces the
            // history when threshold is exceeded. We emit a `compacted`
            // event so the chat UI can render an inline marker; users
            // need to know history was compressed even though the agent
            // continues seamlessly.
            foreach (var mw in caps.Middlewares)
            {
                var beforeMw = state.Messages.Count;
                try { await mw(state, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Emit("error", new() { ["message"] = ex.Message }); }
                if (state.Messages.Count < beforeMw)
                {
                    Emit("compacted", new()
                    {
                        ["reason"] = "auto_threshold",
                        ["before"] = beforeMw,
                        ["after"] = state.Messages.Count,
                    });
                }
            }

            // Auto-lint / auto-test feedback loop. Aider's pattern: run
            // configured lint/test commands after any iteration where
            // the agent successfully wrote a file via file_editor, and
            // pipe non-zero output back as a user-style turn so the
            // model picks it up on the next iteration.
            //
            // We detect "wrote a file" by walking the iteration's
            // function calls for file_editor calls with a non-view
            // command, then checking the matching observations succeeded.
            // No path-glob filter in v1 — if you set the cmd, it runs
            // on every successful write. Add globbing if a user
            // complains about over-eagerness.
            if ((env.AutoLintCmd is { Length: > 0 } || env.AutoTestCmd is { Length: > 0 }) && functionCalls.Count > 0)
            {
                // Tentative write set from args (callId → path).
                var pendingWrites = new Dictionary<string, string>();
                foreach (var fc in functionCalls)
                {
                    if (fc.Name != "file_editor") continue;
                    var args = ExtractArgs(fc);
                    var op = args.TryGetValue("command", out var c) ? c?.ToString() : null;
                    var path = args.TryGetValue("path", out var p) ? p?.ToString() : null;
                    if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(path)) continue;
                    if (op == "view" || op == "undo_edit") continue;
                    pendingWrites[fc.CallId ?? ""] = path!;
                }

                // Filter to the writes that succeeded — failed writes
                // shouldn't trigger lint (the agent already saw the
                // failure as a tool result and will react).
                var successfulWrites = state.LastObservations
                    .Where(o => o.Success && pendingWrites.ContainsKey(o.ToolCallId))
                    .Select(o => pendingWrites[o.ToolCallId])
                    .Distinct()
                    .ToList();

                if (successfulWrites.Count > 0)
                {
                    if (!string.IsNullOrEmpty(env.AutoLintCmd))
                        await RunAutoCheckAsync(env, "lint", env.AutoLintCmd, successfulWrites, state, Emit, ct);
                    if (!string.IsNullOrEmpty(env.AutoTestCmd))
                        await RunAutoCheckAsync(env, "test", env.AutoTestCmd, successfulWrites, state, Emit, ct);
                }
            }

            Emit("iteration_end", new() { ["iteration"] = state.Iteration });

            // Session recovery — tracks consecutive failed-terminal iterations
            // across the loop, not just within one iteration. The bash session
            // inside the docker sandbox can die without a timeout prefix
            // (sidecar reports "bash stdout closed" / "session is dead"); when
            // that happens every subsequent terminal call fails until we
            // re-create the session.
            var terminalObs = state.LastObservations.Where(o => o.ToolName == "terminal").ToList();
            var allTerminalFailed = terminalObs.Count > 0 && terminalObs.All(o => !o.Success);
            var sawTimeout = terminalObs.Any(o => o.Result.StartsWith(Builtins.TimeoutPrefix));

            if (allTerminalFailed)
                consecutiveTerminalFailures++;
            else if (terminalObs.Count > 0)
                consecutiveTerminalFailures = 0;

            if (sawTimeout || consecutiveTerminalFailures >= ConsecutiveTerminalFailureThreshold)
            {
                try
                {
                    // Sidecar's session_create errors with "session already exists"
                    // when the session record is still present after the bash child
                    // died. Destroy first (best-effort), then create. Without the
                    // destroy, recovery can never succeed for a dead-but-not-removed
                    // session and the loop hangs in failure until StuckDetector trips.
                    try { await env.Sandbox.SessionDestroyAsync(env.SessionId, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* not present, fine */ }

                    // Recreate the bash session AT THE WORKSPACE CWD, not at ".".
                    // The "." default lands the recovered session in whatever the
                    // sidecar's process cwd is — typically NOT the dispatch worktree
                    // — which is the root cause of the "implementer drifted into
                    // ~/.vett/dispatches/<somewhere>" failure mode observed on A2
                    // method-tier1 (6 of 8 fails on 2026-05-13). Falling back to "."
                    // only when Cwd is empty (which is the sidecar/RpcClient case).
                    var recoveryCwd = !string.IsNullOrEmpty(env.Sandbox.Cwd) ? env.Sandbox.Cwd : ".";
                    await env.Sandbox.SessionCreateAsync(env.SessionId, recoveryCwd, ct);
                    consecutiveTerminalFailures = 0;
                    Emit("session_recovery", new()
                    {
                        ["iteration"] = state.Iteration,
                        ["reason"] = sawTimeout ? "terminal_timeout" : "consecutive_terminal_failures",
                        ["recovery_cwd"] = recoveryCwd,
                    });

                    // Surface the recovery to the model on the NEXT round-trip.
                    // Tool-result messages for the previous (timed-out) terminal
                    // call have already been appended to state.Messages above, so
                    // we add a fresh user-channel note instead. Without this the
                    // model sees only the prior timeout, retries the same broad
                    // search, and times out again — observed on A2 m2-roman-to-int
                    // r3/r4/r5 (2026-05-13 ladder): same `find /` re-tried after
                    // each recovery, never converged.
                    state.Messages.Add(Chat.User(
                        "[vett] Your bash session was reset (the previous terminal " +
                        "command timed out). Your cwd has been restored to:\n  " +
                        recoveryCwd + "\n\n" +
                        "Do NOT retry the same broad exploration (e.g. `find /`). " +
                        "If you need to locate a file, use file_editor view on a " +
                        "known path — the workspace files are the ones the leader's " +
                        "task referenced. Continue with the actual implementation."
                    ));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Emit("session_recovery_failed", new()
                    {
                        ["iteration"] = state.Iteration,
                        ["error"] = ex.Message,
                    });
                }
            }

            if (state.StopLoop)
            {
                // Set false only by a Stop hook's `deny`, which un-stops the
                // loop entirely. Anything that reaches the bottom of this block
                // is a genuine stop; the question there is whether it ends the
                // TURN or the SESSION, decided below.
                var stopAllowed = true;

                // Stop hook (#20 + v2). v1 ran the hook AFTER the loop
                // returned (side effects only). v2 runs it AT the
                // stop boundary so a `deny` decision can un-stop the
                // loop. Useful for "let the agent finish only if all
                // tests pass" / "block stop_reason=stuck:* and
                // synthesize a hint to the agent." Modify decisions
                // are not honored on Stop — the stop reason is a
                // diagnostic label, not state worth rewriting.
                if (env.Profile?.Hooks?.Stop is { Count: > 0 })
                {
                    var stopEnvelope = System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        stop_reason = state.StopReason,
                        iterations = state.Iteration,
                        session_id = env.SessionId,
                    });
                    var stopDecision = await Vett.Plugin.LifecycleHooks.RunAsync(
                        env.Profile, Vett.Plugin.LifecycleHookEvent.Stop, stopEnvelope, ct);
                    if (stopDecision.Action == "deny")
                    {
                        // Clear stop state so the loop continues. Inject
                        // the reason as a user-style turn so the agent
                        // picks up *why* it can't stop yet on the next
                        // LLM call. Without this, denying-stop produces
                        // an infinite-loop bug — agent finishes, hook
                        // un-stops, agent finishes again.
                        var hint = stopDecision.Reason ?? "Stop blocked by policy hook — keep working.";
                        Emit("stop_blocked", new()
                        {
                            ["original_stop_reason"] = state.StopReason,
                            ["reason"] = hint,
                        });
                        state.StopLoop = false;
                        state.StopReason = "";
                        state.Messages.Add(Chat.User($"<stop_hook_feedback>\n{hint}\n</stop_hook_feedback>"));
                        stopAllowed = false;
                        // Fall through into the next iteration without
                        // returning. Don't reset the iteration counter —
                        // the budget still applies.
                    }
                }

                if (stopAllowed)
                {
                    consecutiveFailureBox[0] = consecutiveTerminalFailures;

                    // THE STOP ENDS THE TURN, NOT THE CONVERSATION.
                    //
                    // Finalize() here was the same defect the runaway cap had
                    // 40 lines up, in the same shape and with the same blast
                    // radius: the agent says "I'm done", and the response is to
                    // tear down the session the person was reading. Mark hit it
                    // live: a chat closed itself on `finish_tool` at iteration
                    // 130 with the review still unread. There is nothing to
                    // salvage in that. The state is right here, the human is
                    // right here, and "I've finished" is precisely the moment
                    // someone wants to look and say "no, also do X".
                    //
                    // THE GUARD IS NOT "interactive". It is a channel AND a
                    // person: the same predicate Loop() calls canPark, spelled
                    // out again here because this body is a lifted helper that
                    // cannot see Loop's locals. A team leader under the bench
                    // harness has the channel and no person; `vett run` has
                    // neither. Both keep the old terminate, deliberately. That
                    // is Mark's own split ("reg vett can, as its fully
                    // automatoous"), and it is also load-bearing: Runner and
                    // the coordinator reconcile runs on these stop reasons, and
                    // a park with nobody typing is an infinite hang, which is
                    // strictly worse than the thing it replaces.
                    //
                    // NO EXCLUSION LIST, AND THAT IS CHECKED, NOT ASSUMED.
                    // Every assignment of StopLoop = true is agent-side:
                    // BuiltinMiddleware's finish_tool detector, its two stuck:*
                    // detectors, and a plugin-supplied reason from Pool. The
                    // user-initiated endings (user_closed, user_interrupted,
                    // cancelled) never travel through here at all; they call
                    // Finalize() directly. So parking on ANY reason that
                    // reaches this line is right, and the stuck:* ones are if
                    // anything the most obvious: "the agent is going in
                    // circles" is an argument FOR fetching the human, not for
                    // destroying what they were reading.
                    if (userInput is null || !env.HumanAtTheKeyboard || env.AgentStopEndsSession)
                        return Finalize(state, state.StopReason);

                    // Remembered before clearing: it is what we tell the user,
                    // and what we finalize with if they close the panel from
                    // here rather than replying.
                    var stoppedBecause = state.StopReason;
                    Emit("agent_stop_parked", new()
                    {
                        ["iteration"] = state.Iteration,
                        ["stop_reason"] = stoppedBecause,
                    });

                    var notice = stoppedBecause switch
                    {
                        "finish_tool" =>
                            "[Finished this request. Nothing is running now, and the session and its "
                            + "full context are still here, so take a look and tell me what to change, "
                            + "or send the next thing.]",
                        "stuck:monologue" =>
                            "[Stopped: I was talking without taking any action, which is a loop worth "
                            + "breaking rather than paying for. Nothing has been lost, so tell me what "
                            + "to do differently.]",
                        "stuck:action_error_loop" =>
                            "[Stopped: the same tool call kept failing and I was making no progress. "
                            + "Nothing has been lost, so tell me what to try instead.]",
                        _ =>
                            $"[Stopped ({stoppedBecause}). Nothing is running now, and the session is "
                            + "still open, so send another message to carry on.]",
                    };
                    onAssistantText?.Invoke(notice);
                    state.Messages.Add(Chat.Assistant(notice));

                    // CLEAR THE STOP STATE OR THIS PARKS FOREVER. StopLoop is
                    // checked every iteration, so leaving it set means the very
                    // next pass re-enters this block and re-parks instead of
                    // running the reply the user just typed.
                    state.StopLoop = false;
                    state.StopReason = "";

                    onWaitingForInput?.Invoke();
                    while (true)
                    {
                        var stopWait = await WaitForUserOrWake(userInput!, env.WakeSignal, ct);
                        // Closing the panel while parked here is the honest end
                        // of the session, and it finalizes with the reason the
                        // agent actually stopped for, not "user_closed". The
                        // agent DID finish; losing that would misreport a clean
                        // run as an abandoned one, and StoppedCleanly reads it.
                        if (stopWait.Kind == "closed")
                        {
                            consecutiveFailureBox[0] = consecutiveTerminalFailures;
                            return Finalize(state, stoppedBecause);
                        }
                        if (stopWait.Kind == "user" && !string.IsNullOrEmpty(stopWait.Msg))
                        {
                            BeginUserTurn(state, stopWait.Msg!);
                            Emit("resumed", new()
                            {
                                ["iteration"] = state.Iteration,
                                ["with_message"] = true,
                                ["after_stop_reason"] = stoppedBecause,
                            });
                            break;
                        }
                        // A wake alone must not resume. Same reasoning as the
                        // runaway park: background deliveries arrive on their
                        // own schedule and would restart a finished agent with
                        // nobody having asked for anything.
                    }
                    // null means "keep looping" to the caller. This helper is
                    // one iteration of Loop's while, so it cannot `continue`
                    // the way the runaway park does.
                    return null;
                }
            }

            // Interactive mode.
            if (userInput is not null)
            {
                if (!string.IsNullOrEmpty(assistantText))
                    onAssistantText?.Invoke(assistantText);

                if (functionCalls.Count == 0)
                {
                    // Leader-stuck-in-exploration detector. Diagnostic
                    // 2026-05-19 found a third systemic failure mode
                    // surviving patches 1 + 2: a team-bench leader does
                    // reasonable file exploration (terminal ls/find,
                    // file_editor view) but never decides to dispatch.
                    // Eventually the leader emits prose-only with no
                    // tool call; interactive mode's
                    // onWaitingForInput-then-wait path lets the harness
                    // settle timer fire, send the self-assessment
                    // prompt, and the run dies with `members=[]` and
                    // tests failing.
                    //
                    // Detect: this agent CAN dispatch (assign_async is
                    // in its tool list) AND has never called
                    // assign_async in this session AND just emitted a
                    // tool-call-less response. That's "leader stuck in
                    // exploration" — nudge once instead of waiting.
                    //
                    // Cap at 1 nudge per session: if it still won't
                    // dispatch after a direct prod, fall through to the
                    // existing wait-for-input path (preserving original
                    // semantics for legitimate "researcher dispatched,
                    // waiting for results" flows).
                    bool canDispatch = caps.Tools.ContainsKey("assign_async");
                    bool everDispatched = state.Messages.Any(m =>
                        m.Contents.Any(c => c is FunctionCallContent fc && fc.Name == "assign_async"));
                    // Profile knob (leader_nudge: false) suppresses the nudge
                    // entirely — in a human-interactive chat there is no
                    // given task, so the injected "call assign_async now"
                    // makes the model invent work (resilience row 26).
                    bool nudgeEnabled = env.Profile?.Team?.LeaderNudge ?? true;
                    if (nudgeEnabled && canDispatch && !everDispatched
                        && state.LeaderStuckRetries < 1
                        && state.Iteration >= 3)
                    {
                        state.LeaderStuckRetries++;
                        Emit("leader_stuck_nudge", new()
                        {
                            ["iteration"] = state.Iteration,
                            ["retry_count"] = state.LeaderStuckRetries,
                        });
                        // lastMsg was already appended unconditionally above
                        // (see "Add assistant message to history"), so a plain
                        // Add() here put the SAME ChatMessage in history twice:
                        // it inflated message_count, re-sent the duplicated
                        // assistant turn on every later round-trip (inflating
                        // TotalInputTokens, which is reported as evidence), and
                        // showed the model its own reply twice. Guard by
                        // reference so the "keep it in history" intent still
                        // holds if a middleware compaction dropped it.
                        if (!state.Messages.Any(m => ReferenceEquals(m, lastMsg)))
                            state.Messages.Add(lastMsg);
                        // Say only what the predicate actually established.
                        // This message used to assert "several iterations on
                        // read-only exploration (view / ls / find)" — but the
                        // predicate above never looks at WHICH tools were
                        // called, only that assign_async was not among them. A
                        // leader that had made zero view/ls/find calls was told
                        // it had spent iterations making them, which is a
                        // fabricated observation the model then has to argue
                        // with. Describe the one thing that is known to be
                        // true: no work has been dispatched.
                        state.Messages.Add(Chat.User(
                            $"You are {state.Iteration} iterations in and haven't " +
                            "dispatched any work yet. The task you were given requires " +
                            "implementation changes that have to be delegated to an " +
                            "implementer. Call `assign_async` now with the task " +
                            "description. If you genuinely believe no work is needed, " +
                            "explain why explicitly in your next turn — don't just stop " +
                            "responding."));
                        consecutiveFailureBox[0] = consecutiveTerminalFailures;
                        return null;
                    }

                    onWaitingForInput?.Invoke();
                    var waitResult = await WaitForUserOrWake(userInput, env.WakeSignal, ct);
                    consecutiveFailureBox[0] = consecutiveTerminalFailures;
                    if (waitResult.Kind == "closed")
                        return Finalize(state, "user_closed");
                    if (waitResult.Kind == "user" && !string.IsNullOrEmpty(waitResult.Msg))
                        BeginUserTurn(state, waitResult.Msg!);
                    // wake-only path returns null — PreIteration hook
                    // surfaces pending deliveries on the next iteration.
                    return null;
                }

                if (userInput.TryRead(out var injected))
                    state.Messages.Add(Chat.User(injected));
            }
            else if (functionCalls.Count == 0)
            {
                // Non-interactive + no tool calls = the model is done.
                // Without this, an LLM that "answered in prose" but
                // didn't fire a tool call would spin the loop until
                // max_iterations — exactly the researcher behavior we
                // saw burning iterations 5-12 on a 198-token monologue
                // when it had already finished its work at iter 4.
                //
                // BUT: legitimate "done" responses come AFTER work. A
                // model that announces "I'll start by viewing..." in
                // iter 1 with no tool call is mid-plan, not finished.
                // Diagnostic 2026-05-19 found a sub-agent (am6 r1
                // patched) and two others said literally "Dispatched
                // the migration to the implementer. Waiting for
                // results." — the sub-agent confused itself for the
                // leader and waited for results from nothing. Harness
                // then closed the dispatch as "completed" with zero
                // work, causing a downstream test fail.
                //
                // Rule: if we're in the first 2 iterations AND there's
                // no prior tool activity in the conversation, treat
                // "no tool call this turn" as "model didn't engage yet"
                // — nudge once. Cap retries so a model that refuses to
                // engage at all can't infinite-loop.
                const int NoToolEngagementRetryCap = 2;
                bool hasPriorToolActivity = state.Messages.Any(m =>
                    m.Contents.Any(c => c is FunctionCallContent || c is FunctionResultContent));
                if (state.Iteration <= 2 && !hasPriorToolActivity
                    && state.NoToolEngagementRetries < NoToolEngagementRetryCap)
                {
                    state.NoToolEngagementRetries++;
                    Emit("no_tool_engagement_nudge", new()
                    {
                        ["iteration"] = state.Iteration,
                        ["retry_count"] = state.NoToolEngagementRetries,
                        ["retry_cap"] = NoToolEngagementRetryCap,
                        ["assistant_text_preview"] = (assistantText ?? "").Length > 200
                            ? assistantText![..200] : (assistantText ?? ""),
                    });
                    // Keep the assistant message in history (it expressed
                    // intent — losing it could confuse the next turn). It is
                    // NOT lost: the unconditional Add above ("Add assistant
                    // message to history") already put it there, so the plain
                    // Add() this comment used to guard was a duplicate — same
                    // ChatMessage twice, inflating message_count and every
                    // later request's input tokens. Guard by reference so the
                    // stated intent still holds if middleware compaction
                    // dropped it.
                    if (!state.Messages.Any(m => ReferenceEquals(m, lastMsg)))
                        state.Messages.Add(lastMsg);
                    state.Messages.Add(Chat.User(
                        "You responded with text but no tool call. If you intend " +
                        "to do work, make the tool call now — don't just announce " +
                        "it. If you're truly finished, make sure your work is " +
                        "actually done first (use file_editor.view or terminal to " +
                        "verify) before finishing without a tool call."));
                    consecutiveFailureBox[0] = consecutiveTerminalFailures;
                    return null;
                }
                consecutiveFailureBox[0] = consecutiveTerminalFailures;
                return Finalize(state, "completed");
            }

            consecutiveFailureBox[0] = consecutiveTerminalFailures;
            return null;
    }

    /// <summary>How many times an identical sole call returning identical
    /// bytes is RUN before the next identical issue is refused. Two: the second
    /// run is what proves the result is stable, one alone proves nothing.</summary>
    private const int RepeatedCallExecuteCap = 2;

    /// <summary>The identical issue -- counting the runs -- that ends the run
    /// `repeated_call_exhausted`. Five: two runs, two refusals ignored, and the
    /// third refusal is the end. See the repeat break at the dispatch site.</summary>
    private const int RepeatedCallExhaustAt = 5;

    /// <summary>Polls that legitimately return the same answer while a member
    /// is still working. Named, not guessed: both are registered by the
    /// dispatch capability and both carry <see cref="Builtins.StillRunningMarker"/>
    /// while waiting; the name check covers a wait whose message varies.</summary>
    private static readonly HashSet<string> RepeatExemptTools =
        new(StringComparer.Ordinal) { "check_task", "wait_task" };

    private static string RepeatSignature(string toolName, Dictionary<string, object?> args)
        => toolName + " " + JsonSerializer.Serialize(args);

    /// <summary>Law 254: after the retry compaction, every kept assistant call
    /// whose signature is the refused one has its string arguments replaced by
    /// an `&lt;omitted: N chars ...&gt;` marker, so the tail holds nothing to
    /// copy. Returns the number of calls redacted. Ids and names stay so the
    /// tool results still have a call to answer.</summary>
    internal static int RedactRepeatBodies(IList<ChatMessage> messages, string signature)
    {
        var redacted = 0;
        foreach (var msg in messages)
        {
            if (msg.Role != ChatRole.Assistant) continue;
            for (var i = 0; i < msg.Contents.Count; i++)
            {
                if (msg.Contents[i] is not FunctionCallContent fc || fc.Arguments is null) continue;
                var dict = fc.Arguments as Dictionary<string, object?> ?? new Dictionary<string, object?>(fc.Arguments);
                if (RepeatSignature(fc.Name, dict) != signature) continue;
                var stripped = new Dictionary<string, object?>(dict.Count);
                foreach (var kv in dict)
                {
                    var s = kv.Value?.ToString();
                    stripped[kv.Key] = s is null
                        ? kv.Value
                        : "<omitted: " + s.Length + " chars -- byte-identical to the call refused as a repeat; it is NOT in your context and must not be sent again>";
                }
                msg.Contents[i] = new FunctionCallContent(fc.CallId, fc.Name, stripped);
                redacted++;
            }
        }
        return redacted;
    }

    private static bool IsRepeatExempt(string toolName, string? lastResult)
        => RepeatExemptTools.Contains(toolName)
           || (lastResult is not null
               && lastResult.Contains(Builtins.StillRunningMarker, StringComparison.Ordinal));

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => n + "th" };

    /// <summary>The tool result the model receives instead of a re-run. It must
    /// say plainly that nothing ran, how many times the model has now asked,
    /// what it already has, and what happens on the next identical issue --
    /// the point is to give the model something to do OTHER than ask again.
    /// It does not start with "Error:" -- StuckDetector's action-error loop
    /// must not be a second executioner for the same turn.</summary>
    private static string RepeatedCallNotice(string toolName, int timesIssued, string priorResult, bool compacted = false)
    {
        var shown = priorResult.Length == 0 ? "(the call produced no output)" : priorResult;
        var next = timesIssued + 1 >= RepeatedCallExhaustAt
            ? "Issuing this same call once more ENDS this session."
            : "It will not be run again; the " + Ordinal(RepeatedCallExhaustAt) + " identical issue ends this session.";
        return "[vett] REPEATED CALL -- NOT RUN. This is the " + Ordinal(timesIssued) + " time in a row you have "
            + "issued this exact `" + toolName + "` call with these exact arguments, and its previous runs "
            + "returned byte-identical output. Running it again cannot produce anything new, so it was not run. "
            + "The output you already have from it is:\n\n" + shown + "\n\n"
            + (compacted ? RepeatedCallCompactedPreamble : "")
            + "Read that output and do something DIFFERENT: change the arguments, act on the result, or finish. "
            + next;
    }

    /// <summary>Law 248: what the seat is told when the first refusal
    /// compacted the history -- the repeated bodies are gone from its
    /// context, the disk is the truth, and the next call must differ in
    /// substance (a `new_str` that adds text the file does not hold, a
    /// different file, a different command), not in wording.</summary>
    internal const string RepeatedCallCompactedPreamble =
        "Because you re-issued it after being told, the conversation above has been compacted to a summary plus "
        + "the most recent messages, so the repeated call no longer sits in your context to be copied; the files on "
        + "disk are unchanged and are the truth -- `view` a file before editing it. Your next call must differ in "
        + "SUBSTANCE, not wording: an edit whose new text is already in the file, or a write of bytes the file "
        + "already holds, is the same call. ";

    /// <summary>Law 251: the only tools whose siblings may run concurrently.
    /// Everything else (terminal, file_editor, MCP, anything unknown) touches
    /// the workspace and runs in wire order. Fail-closed: a new tool is
    /// sequential until it is added here.</summary>
    internal static readonly HashSet<string> LeaderFanOutTools = new(StringComparer.Ordinal)
    {
        "assign_task", "assign_async", "assign_best_of", "cancel_task", "inject_into_task",
        "check_task", "check_tasks", "wait_task", "declare_done",
        "accept_dispatch", "reject_dispatch", "review_dispatch", "list_pending_dispatches",
    };

    private static async Task<Observation> DispatchAsync(
        Dictionary<string, ToolFn> tools, ISandbox sandbox, string sessionId,
        string callId, string toolName, Dictionary<string, object?> args,
        Action<string, Dictionary<string, object?>> emit,
        Vett.Tools.PermissionGate? gate,
        Vett.Config.Profile? profile,
        CancellationToken ct)
    {
        if (!tools.TryGetValue(toolName, out var toolFn))
        {
            // The model really did make this call and really did get an
            // answer, but until this emit the event stream said NOTHING
            // about it — no tool_call_start, no tool_call_end, nothing in
            // _bench-session.jsonl. That silence is what made the `mcp__`
            // schema/handler mismatch so expensive to diagnose (see the
            // post-mortem comment in Bench/Team/Harness.cs): a call that
            // leaves no trace is indistinguishable, in the log, from a call
            // that was never made.
            //
            // Deliberately NOT emitted as a tool_call_start/tool_call_end
            // pair. AssertionEngine.EvalToolCall matches on tool_call_end by
            // tool_name, and a bare `tool_call: X` assertion carries no
            // success filter — synthesising an end event here would turn
            // "the model called a tool that does not exist" from a FAIL into
            // a PASS. A distinct event type records the attempt truthfully
            // without crediting it.
            emit("tool_not_registered", new()
            {
                ["tool_name"] = toolName,
                ["call_id"] = callId,
                ["arguments"] = args,
                ["registered_tools"] = string.Join(",", tools.Keys.OrderBy(k => k, StringComparer.Ordinal)),
            });
            return new Observation { ToolCallId = callId, ToolName = toolName, Result = $"Error: tool \"{toolName}\" not registered" };
        }

        // PreToolUse hooks (#20). Run BEFORE the permission gate so a
        // hook's deny / modify is authoritative — a hook saying "no"
        // shouldn't be re-prompted to the user. Hooks see the args
        // they'll get, can rewrite them via "modify" decision, or
        // synthesize a denial observation back to the agent.
        if (profile?.Hooks is not null)
        {
            var preEnvelope = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                tool_name = toolName,
                arguments = args,
                call_id = callId,
                session_id = sessionId,
            });
            var preDecision = await Vett.Plugin.LifecycleHooks.RunAsync(
                profile, Vett.Plugin.LifecycleHookEvent.PreToolUse, preEnvelope, ct);
            if (preDecision.Action == "deny")
            {
                var deniedReason = preDecision.Reason ?? "denied by hook";
                emit("tool_call_start", new()
                {
                    ["tool_name"] = toolName, ["call_id"] = callId,
                    ["arguments"] = args, ["denied"] = true, ["denied_by"] = "hook",
                });
                var deniedResult = $"Error: tool call to \"{toolName}\" was denied by a pre_tool_use hook: {deniedReason}";
                emit("tool_call_end", new()
                {
                    ["tool_name"] = toolName, ["call_id"] = callId, ["success"] = false,
                    ["duration_ms"] = 0L, ["result_preview"] = deniedResult,
                    ["result"] = deniedResult, ["denied"] = true, ["denied_by"] = "hook",
                });
                return new Observation { ToolCallId = callId, ToolName = toolName, Result = deniedResult };
            }
            if (preDecision.Action == "modify" && preDecision.Modified.HasValue)
            {
                // Pull the modified `arguments` field out of the
                // returned envelope. Tolerant — if the hook returned a
                // bare `{arguments: {...}}` use that; if it returned
                // the full envelope shape, pull arguments from inside.
                if (preDecision.Modified.Value.TryGetProperty("arguments", out var modArgs)
                    && modArgs.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    args = JsonElementToDict(modArgs);
                }
            }
        }

        // Permission gate (chat only). For Auto: pass through. For
        // Ask: emit `permission_request` and block until the user
        // answers. For Deny (either configured or user-chosen):
        // synthesize an error observation that lets the agent see
        // "denied" and decide what to do (often, ask the user
        // directly via assistant text).
        if (gate is not null)
        {
            // Emit a separate "permission_check_start" event so the UI
            // can show a "checking permissions…" state if the gate is
            // taking a while (rare for Auto/Deny, common for Ask).
            // Skipped when result is Auto so we don't spam the timeline.
            var decision = await gate.CheckAsync(toolName, args, ct);
            if (decision == Vett.Tools.PermissionRule.Deny)
            {
                emit("tool_call_start", new()
                {
                    ["tool_name"] = toolName,
                    ["call_id"] = callId,
                    ["arguments"] = args,
                    ["denied"] = true,
                });
                var deniedResult = $"Error: tool call to \"{toolName}\" was denied by the user. Try a different approach, or ask the user what they'd like you to do instead.";
                emit("tool_call_end", new()
                {
                    ["tool_name"] = toolName,
                    ["call_id"] = callId,
                    ["success"] = false,
                    ["duration_ms"] = 0L,
                    ["result_preview"] = deniedResult,
                    ["result"] = deniedResult,
                    ["denied"] = true,
                });
                return new Observation { ToolCallId = callId, ToolName = toolName, Result = deniedResult };
            }
            // Auto path falls through to the normal dispatch below.
        }

        // Include `arguments` so the chat extension's raw view can show
        // what was actually called (file path / shell command). Without
        // this, debugging a hung tool means digging through the JSONL
        // log instead of glancing at the chat panel.
        emit("tool_call_start", new()
        {
            ["tool_name"] = toolName,
            ["call_id"] = callId,
            ["arguments"] = args,
        });
        var start = DateTime.UtcNow;

        try
        {
            var result = await toolFn(args, sandbox, sessionId, ct);
            var durationMs = (long)(DateTime.UtcNow - start).TotalMilliseconds;

            // PostToolUse hooks (#20 + v2). v1 ran for side effects
            // only; v2 honors `modify` to rewrite the observation
            // string the agent sees. `deny` is intentionally not
            // honored on PostToolUse — by the time the tool ran, the
            // side effects already happened; denying retroactively is
            // incoherent. Use PreToolUse for prevention.
            //
            // The hook returns `{action:"modify", modified:{result:"..."}}`
            // and we swap the result string. The original `result`
            // continues to flow through the tool_call_end emit so the
            // chat UI shows what really happened — the hook only
            // rewrites what the AGENT sees on the next turn. This
            // matters: a redaction hook should hide secrets from the
            // model without hiding them from the user.
            if (profile?.Hooks is not null)
            {
                var postEnvelope = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    tool_name = toolName,
                    arguments = args,
                    call_id = callId,
                    session_id = sessionId,
                    result,
                    success = !result.StartsWith("Error:") && !result.StartsWith(Builtins.TimeoutPrefix),
                    duration_ms = durationMs,
                });
                var postDecision = await Vett.Plugin.LifecycleHooks.RunAsync(
                    profile, Vett.Plugin.LifecycleHookEvent.PostToolUse, postEnvelope, ct);
                if (postDecision.Action == "modify"
                    && postDecision.Modified.HasValue
                    && postDecision.Modified.Value.TryGetProperty("result", out var modResult)
                    && modResult.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    result = modResult.GetString() ?? result;
                }
            }

            // Treat tool result as a "soft failure" if it begins with "Error:" so
            // the operator can see what went wrong without scraping the full
            // message stream. The Result string is still the source of truth for
            // the agent loop and middleware; the event flag is diagnostic only.
            var softFailed = result.StartsWith("Error:") || result.StartsWith(Builtins.TimeoutPrefix);
            emit("tool_call_end", new()
            {
                ["tool_name"] = toolName, ["call_id"] = callId, ["success"] = !softFailed,
                ["duration_ms"] = durationMs,
                ["result_preview"] = Preview(result),
                // Full untruncated result for the Raw view in vett-chat.
                // The Logs view continues to use result_preview (200-char
                // head + length tail) to keep the timeline scrollable.
                ["result"] = result,
            });
            return new Observation { ToolCallId = callId, ToolName = toolName, Result = result, Success = !softFailed };
        }
        catch (OperationCanceledException)
        {
            // Don't surface cancellation as a tool error — let it bubble up so
            // the loop terminates cleanly when the caller asked to stop.
            throw;
        }
        catch (Exception ex)
        {
            emit("tool_call_end", new()
            {
                ["tool_name"] = toolName, ["call_id"] = callId, ["success"] = false,
                ["duration_ms"] = (long)(DateTime.UtcNow - start).TotalMilliseconds,
                ["result_preview"] = $"Error: {ex.Message}",
            });
            return new Observation { ToolCallId = callId, ToolName = toolName, Result = $"Error: {ex.Message}" };
        }
    }

    private static string Preview(string s) =>
        s.Length <= 200 ? s : s[..200] + $"…[+{s.Length - 200}]";

    /// <summary>
    /// Convert a JsonElement (object) to the loose Dictionary&lt;string, object?&gt;
    /// shape tools expect for arguments. Used when a PreToolUse hook
    /// rewrites tool inputs via the modify decision. Strings, numbers,
    /// bools, null pass through; nested objects/arrays carry through
    /// as JsonElement (tools that look up nested fields handle either).
    /// </summary>
    private static Dictionary<string, object?> JsonElementToDict(System.Text.Json.JsonElement obj)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                _ => prop.Value, // arrays / nested objects → pass JsonElement through
            };
        }
        return dict;
    }

    /// <summary>
    /// Convert a <see cref="ChatMessage"/> to a plain JSON-serializable
    /// dict for emission to the chat extension. Captures role + every
    /// content block (text / function call / function result) so the
    /// Raw view in vett-chat can show exactly what the LLM saw and
    /// said. Unknown content types degrade to a `{type: ClassName}`
    /// stub rather than crashing the emit.
    /// </summary>
    private static Dictionary<string, object?> SerializeMessage(ChatMessage m)
    {
        var content = new List<Dictionary<string, object?>>();
        foreach (var c in m.Contents)
        {
            switch (c)
            {
                case TextContent tc:
                    content.Add(new() { ["type"] = "text", ["text"] = tc.Text });
                    break;
                case FunctionCallContent fc:
                    content.Add(new()
                    {
                        ["type"] = "function_call",
                        ["call_id"] = fc.CallId,
                        ["name"] = fc.Name,
                        ["arguments"] = fc.Arguments,
                    });
                    break;
                case FunctionResultContent frc:
                    content.Add(new()
                    {
                        ["type"] = "function_result",
                        ["call_id"] = frc.CallId,
                        ["result"] = frc.Result?.ToString() ?? "",
                    });
                    break;
                default:
                    content.Add(new() { ["type"] = c.GetType().Name });
                    break;
            }
        }
        return new()
        {
            ["role"] = m.Role.Value,
            ["content"] = content,
        };
    }

    /// <summary>
    /// Collapse identical (tool name, arguments) calls within one assistant
    /// turn. Returns the calls to actually execute (first of each group, in
    /// original order) plus a duplicate-&gt;winner call-id mapping so the
    /// caller can replay results and keep one tool result per call_id.
    ///
    /// Two calls are "identical" when the tool name matches and the argument
    /// maps are equal — compared key-by-key, order-insensitively, so that
    /// providers that reorder JSON keys don't defeat the match. Calls with a
    /// null/empty CallId are never collapsed (nothing to replay onto).
    /// </summary>
    public static (List<FunctionCallContent> Execute, List<(string DupId, string WinnerId)> Duplicates)
        CollapseDuplicateToolCalls(IReadOnlyList<FunctionCallContent> calls)
    {
        var execute = new List<FunctionCallContent>();
        var dups = new List<(string, string)>();
        var winners = new Dictionary<string, string>(StringComparer.Ordinal); // key -> winner callId

        foreach (var fc in calls)
        {
            var callId = fc.CallId ?? "";
            if (callId.Length == 0) { execute.Add(fc); continue; }

            var key = CanonicalCallKey(fc);
            if (winners.TryGetValue(key, out var winnerId))
                dups.Add((callId, winnerId));
            else
            {
                winners[key] = callId;
                execute.Add(fc);
            }
        }

        return (execute, dups);
    }

    /// <summary>
    /// Drop the collapsed duplicate calls out of an assistant message so the
    /// copy that lands in history contains only what actually ran.
    ///
    /// Everything else about the message is carried across unchanged -- text,
    /// reasoning, ordering, and the additional properties the reasoning
    /// capture reads -- because the point is to remove the repetition, not to
    /// rewrite the turn.
    /// </summary>
    internal static ChatMessage PruneCollapsedCalls(ChatMessage msg, HashSet<string> dupIds)
    {
        var kept = msg.Contents
            .Where(c => c is not FunctionCallContent fc
                        || fc.CallId is null
                        || !dupIds.Contains(fc.CallId))
            .ToList();

        return new ChatMessage(msg.Role, kept)
        {
            AuthorName = msg.AuthorName,
            MessageId = msg.MessageId,
            AdditionalProperties = msg.AdditionalProperties,
        };
    }

    /// <summary>Order-insensitive canonical key for a tool call.</summary>
    private static string CanonicalCallKey(FunctionCallContent fc)
    {
        // Length-prefix every field so the key is unambiguous using only
        // printable characters: {a:"b"} and {ab:""} cannot collide.
        var sb = new System.Text.StringBuilder();
        void Append(string part) => sb.Append(part.Length).Append(':').Append(part).Append('|');

        Append(fc.Name);
        foreach (var kv in ExtractArgs(fc).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Append(kv.Key);
            // A null value gets a length marker Append() can never emit, so
            // it cannot alias the literal string "(null)".
            if (kv.Value is null) sb.Append("-1:|");
            else Append(kv.Value.ToString() ?? "");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The tool call the output cap cut mid-arguments, or null. It is the LAST
    /// content part (a cap cuts the tail), it carries no arguments, the reply
    /// was at the cap (the provider's `length`, or the counted output reaching
    /// the configured cap), and the tool needs arguments -- or the adapter
    /// says the arguments failed to parse, which is never a complete call.
    /// </summary>
    internal static FunctionCallContent? FindCutTailToolCall(
        ChatMessage msg, List<FunctionCallContent> calls, ChatFinishReason? finish,
        int outputTokens, int? cap, List<JsonElement> schemas)
    {
        if (calls.Count == 0) return null;
        var atCap = finish == ChatFinishReason.Length || (cap is int c && c > 0 && outputTokens >= c);
        if (!atCap) return null;
        if (msg.Contents.LastOrDefault() is not FunctionCallContent tail) return null;
        if (!calls.Contains(tail)) return null;
        if (tail.Arguments is { Count: > 0 }) return null;
        if (tail.Exception is null && !ToolRequiresArguments(schemas, tail.Name)) return null;
        return tail;
    }

    /// <summary>Does the tool's schema list any required parameter? Unknown
    /// tool: true -- nothing says an empty call to it is complete.</summary>
    private static bool ToolRequiresArguments(List<JsonElement> schemas, string name)
    {
        foreach (var s in schemas)
        {
            var fn = s;
            if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("function", out var f)) fn = f;
            if (fn.ValueKind != JsonValueKind.Object) continue;
            if (!fn.TryGetProperty("name", out var n) || n.GetString() != name) continue;
            if (fn.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                return req.GetArrayLength() > 0;
            return false;
        }
        return true;
    }

    /// <summary>The argument bytes that DID arrive, when the adapter kept
    /// them: the OpenAI adapter parks the wire tool call in RawRepresentation.</summary>
    private static string? RawArguments(FunctionCallContent fc) => fc.RawRepresentation switch
    {
        OpenAI.Chat.ChatToolCall tc => tc.FunctionArguments?.ToString(),
        string s => s,
        BinaryData bd => bd.ToString(),
        _ => null,
    };

    /// <summary>Deflated size of the raw arguments' tail as a percentage of
    /// its size; null when there is too little to say anything.</summary>
    internal static int? TailCompressionPct(string? raw) => ArgumentTail.CompressionPct(raw);

    /// <summary>Law 240: the streaming client's prose-loop reading, when the
    /// reply carries one.</summary>
    internal static (int Chars, int Pct)? ProseLoopOf(ChatResponse resp)
    {
        if (resp.AdditionalProperties is not { } props) return null;
        if (!props.TryGetValue(SilenceBoundedChatClient.ProseLoopCharsKey, out var c) || c is not int chars) return null;
        if (!props.TryGetValue(SilenceBoundedChatClient.ProseLoopPctKey, out var p) || p is not int pct) return null;
        return (chars, pct);
    }

    /// <summary>Law 244: the second consecutive prose loop in one streak
    /// compacts the history before the retry (see the recovery site).</summary>
    internal const int ProseLoopCompactAfter = 2;

    /// <summary>Law 244: prepended to the prose-loop nudge when the history was
    /// just compacted, so the seat knows the context it sees is a digest and
    /// not the conversation that produced the loop.</summary>
    internal const string ProseLoopCompactedPreamble =
        "Your last two replies BOTH looped, so the conversation above has been compacted to a summary plus the "
        + "most recent messages; the files on disk are unchanged and are the truth -- `view` a file before editing it "
        + "if you are unsure of its current contents. ";

    /// <summary>Law 240: the nudge for a reply whose prose looped. It names
    /// the loop, not the cap (the cap never fired), and asks for the tool
    /// call NOW and small.</summary>
    internal static string ProseLoopNudge(int chars, int pct)
        => "Your previous reply was repeating the same few lines over and over (" + chars
           + " characters; the last " + ArgumentTail.SampleChars + " compressed to " + pct
           + "% of their size), so the harness stopped it before it produced a tool call and NONE of it was used. "
           + "Do NOT pick that reasoning up again and do NOT restate what you were about to do: make the tool call NOW, "
           + "with at most one sentence before it. If that call is a large write, write a SMALL piece (20-40 lines) "
           + "and continue with `insert` in your next reply. A tool call you have ALREADY made, whose result is already "
           + "in this conversation, must not be made again — use the result you have.";

    private static string CapPhrase(int? cap) => cap is int c ? "the output cap of " + c + " tokens" : "the output-length limit";
    private static string FitPhrase(int? cap) => cap is int c ? "well under " + c + " tokens" : "well under the limit";

    private static string CutArgumentsSentence(string? raw, int? tailPct)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = "The cut arguments ran to " + raw.Length + " characters before the cap. ";
        if (tailPct is int p && p < RepeatingTailCompressionPct)
            s += "Their last " + Math.Min(raw.Length, TailSampleChars) + " characters compress to " + p
                + "% of their size: you were REPEATING yourself, not writing new content. Do not retry the same write; "
                + "write far fewer items per call and end the call once they are written. ";
        return s;
    }

    /// <summary>The sentence for a stream THIS harness stopped (laws 231 and
    /// 232): the ruler, not the cap, and no token count -- the engine never
    /// reported one for an aborted turn, so "(0 generated)" would be a lie.</summary>
    private static string AbortedSentence(RepeatingArgumentsException abort, string tool) =>
        abort.Siblings > 0 && abort.Distinct > 0
            // Law 245: the literal repeat -- the first copy of each ran.
            ? "Your previous reply was STOPPED by the harness after " + abort.Siblings + " `" + tool
              + "` calls in a row that were " + (abort.Distinct == 1 ? "the SAME call" : "the same " + abort.Distinct + " calls")
              + " re-issued verbatim, over and over. The first copy of each RAN and its result is in this conversation; "
              + "the repeats were dropped. "
            : abort.Siblings > 0
            ? "Your previous reply was STOPPED by the harness after " + abort.Siblings + " `" + tool
              + "` calls in a row whose arguments repeat each other (the last " + TailSampleChars
              + " characters of them compress to " + abort.TailPct + "% of their size): a loop, not a plan. "
              // Law 247: the leading distinct calls ran; say so, so the seat reads their results instead of re-issuing them.
              + (abort.Ran > 0
                  ? "The first " + (abort.Ran == 1 ? "call" : abort.Ran + " calls") + " of that series RAN and "
                    + (abort.Ran == 1 ? "its result is" : "their results are") + " in this conversation; the rest were dropped. "
                  : "None of them ran. ")
            : "Your previous reply was STOPPED by the harness " + abort.Chars + " characters into the arguments of your `"
              + tool + "` call: their last " + TailSampleChars + " characters compress to " + abort.TailPct
              + "% of their size, so you were REPEATING yourself, not writing new content. ";

    /// <summary>The tool result the cut call receives when complete siblings
    /// ran and the reply stays in history.</summary>
    private static string CutToolCallResult(string tool, int? cap, int outputTokens, string? raw, int? tailPct, RepeatingArgumentsException? abort) =>
        abort is { Distinct: > 0 }
            ? "NOT RUN: this `" + tool + "` call was one more repeat. " + AbortedSentence(abort, tool)
              + "Do NOT issue it again: read the result you already have, and make ONE different call next -- "
              + "a call is made once and its result is then used."
            // Law 247: a member of a paraphrase series, whose leading calls ran.
            : abort is { Siblings: > 0 }
            ? "NOT RUN: this `" + tool + "` call was one more member of that series. " + AbortedSentence(abort, tool)
              + "Do NOT re-issue the series. "
              + (abort.Ran > 0
                  ? "Read the results you already have, and make ONE different call next -- a call is made once and its result is then used."
                  : "If a command's output was too long, ask for LESS of it (a narrower path, a grep, a count) -- never for more of "
                    + "the same. Make ONE different call next, and say in one sentence what its result will decide.")
            : abort is not null
            ? "NOT RUN: this `" + tool + "` call reached the loop with NO arguments. " + AbortedSentence(abort, tool)
              + "Re-issue this call FIRST in your next reply with far less content in it, and end the call once that "
              + "content is written: a write that does not fit in one call is built across several calls (create the "
              + "first part, then str_replace or insert the rest), each " + FitPhrase(cap) + "."
            : "NOT RUN: this `" + tool + "` call reached the loop with NO arguments. Your reply hit " + CapPhrase(cap)
        + " (" + outputTokens + " generated) while the arguments were still being written, so they were cut off and "
        + "nothing ran. " + CutArgumentsSentence(raw, tailPct)
        + "Re-issue this call FIRST in your next reply, with far less content in it: a write that does not fit in one "
        + "call is built across several calls (create the first part, then str_replace or insert the rest), each "
        + FitPhrase(cap) + ".";

    /// <summary>What a cut `file_editor create` yielded: the path, the complete
    /// lines of `file_text` that arrived (or all of it when the string closed
    /// before the cut), how many characters after the last complete line were
    /// dropped, and whether the text was complete.</summary>
    /// <para>Law 233: when the tail was a LOOP, <c>LoopedLines</c> is the
    /// number of complete lines dropped from the end because they repeated
    /// (literally, or by the deflate ruler) what came before; the text kept
    /// is the distinct prefix, and <c>TailPct</c> the ratio that condemned
    /// the whole. Zero / null when the tail was honest.</para>
    /// <para>Law 234: <c>Command</c> is <c>create</c>, <c>insert</c> or
    /// <c>str_replace</c>; <c>InsertLine</c> / <c>OldStr</c> carry the
    /// complete anchor the cut call had already sent.</para>
    internal sealed record SalvagedCreate(string Path, string Text, int Lines, int DroppedChars, bool Complete, int LoopedLines = 0, int? TailPct = null, string Command = "create", int InsertLine = 0, string? OldStr = null, string? LoopSignal = null, int? DuplicatePct = null);

    /// <summary>Fewest lines a loop salvage will bank: under this the
    /// "prefix" is the loop's own first turn, not content.</summary>
    internal const int MinSalvagedDistinctLines = 8;

    /// <summary>Law 237: the whole-body duplicate ruler. A body of at least
    /// <see cref="DuplicateMassMinLines"/> content lines (16+ chars) in which
    /// at least <see cref="DuplicateMassLoopPct"/> percent are literal repeats
    /// of an earlier line is a loop, whatever its tail sample says. The tail
    /// sampler (4,096 chars) reads a loop whose PERIOD is a good fraction of
    /// the sample as nearly honest: s10a it.20 (2026-09-09 08:00Z) cycled 33
    /// facts 22 times to 734 lines and the tail read 13%, one point above the
    /// 12% bound, so all 734 "honest" lines were banked with looped_lines 0.
    /// Genuine test files repeat a few assertion lines; that is a low
    /// percentage, not a mass.</summary>
    internal const int DuplicateMassMinLines = 40;
    internal const int DuplicateMassLoopPct = 50;

    /// <summary>Percent of the body's content lines (16+ chars after trim)
    /// that literally repeat an earlier content line; null when the body has
    /// fewer than <see cref="DuplicateMassMinLines"/> content lines.</summary>
    internal static int? DuplicateLinePct(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var content = 0;
        var dup = 0;
        foreach (var line in text.Split('\n'))
        {
            var key = line.Trim();
            if (key.Length < 16) continue;
            content++;
            if (!seen.Add(key)) dup++;
        }
        if (content < DuplicateMassMinLines) return null;
        return (int)Math.Round(100.0 * dup / content);
    }

    /// <summary>Salvage a cut `file_editor create`: null unless the tool is
    /// file_editor, the command is `create`, `path` arrived as a complete JSON
    /// string, `file_text` opened and holds at least one complete line, and
    /// when the tail is a repetition loop (law 233), only the leading run of
    /// distinct lines is kept -- see <see cref="DistinctPrefix"/> -- and null
    /// when that run is shorter than <see cref="MinSalvagedDistinctLines"/>.</summary>
    internal static SalvagedCreate? SalvageCutCreate(string tool, string? raw, int? tailPct)
        => SalvageCutCreate(tool, raw, tailPct, out _);

    /// <summary>As above, naming WHY nothing was banked (null reason when
    /// something was): a "salvaged: null" with no reason was the recorder
    /// gap of batch 10, where two seats lost 6-10 min per attempt on cut
    /// `str_replace` bodies and the log could not say which branch refused.</summary>
    internal static SalvagedCreate? SalvageCutCreate(string tool, string? raw, int? tailPct, out string? reason)
    {
        reason = null;
        if (tool != "file_editor") { reason = "not_file_editor"; return null; }
        if (string.IsNullOrEmpty(raw)) { reason = "empty_raw"; return null; }
        var looping = tailPct is int p && p < RepeatingTailCompressionPct;
        // ⭐ LAW 234 (EpicForge batch 10, 2026-09-09 07:31-07:37Z, VETT
        // 6c09abd): law 233 banked the distinct prefix of the looping
        // create and told the seat to continue with `insert` or
        // `str_replace` -- and the seat did exactly that, in ONE call of
        // 32-50 K chars, which hit the cap and was thrown away whole
        // because only `create` was salvageable (s10a: 12,288 tokens,
        // 50,040 chars, tail 13%; s10c: 8,192 tokens, 32,513 chars, tail
        // 10%; each 6-10 min, each `salvaged: null`, each a retry). The
        // body of an `insert` or a `str_replace` is the same kind of
        // thing as a `file_text`: complete lines that arrived before the
        // cut, worth banking under the same rules, provided the anchor
        // (`insert_line`, or a complete non-empty `old_str`) arrived.
        var cm = System.Text.RegularExpressions.Regex.Match(raw, @"(?<!\\)""command""\s*:\s*""(create|insert|str_replace)""");
        if (!cm.Success) { reason = "command_not_salvageable"; return null; }
        var command = cm.Groups[1].Value;
        var pm = System.Text.RegularExpressions.Regex.Match(raw, @"(?<!\\)""path""\s*:\s*""((?:[^""\\]|\\.)*)""");
        if (!pm.Success) { reason = "path_missing"; return null; }
        string path;
        try { path = JsonSerializer.Deserialize<string>("\"" + pm.Groups[1].Value + "\"") ?? ""; }
        catch (JsonException) { reason = "path_missing"; return null; }
        if (path.Length == 0) { reason = "path_missing"; return null; }
        var insertLine = 0;
        string? oldStr = null;
        var bodyFrom = 0;
        if (command == "insert")
        {
            // The terminator proves the number arrived whole.
            var im = System.Text.RegularExpressions.Regex.Match(raw, @"(?<!\\)""insert_line""\s*:\s*(\d+)\s*[,}]");
            if (!im.Success || !int.TryParse(im.Groups[1].Value, out insertLine)) { reason = "insert_line_missing"; return null; }
        }
        else if (command == "str_replace")
        {
            var om = System.Text.RegularExpressions.Regex.Match(raw, @"(?<!\\)""old_str""\s*:\s*""");
            if (!om.Success) { reason = "old_str_incomplete"; return null; }
            var (o, oComplete) = DecodeJsonStringPrefix(raw, om.Index + om.Length);
            if (!oComplete || o.Length == 0) { reason = "old_str_incomplete"; return null; }
            oldStr = o;
            bodyFrom = om.Index + om.Length;
        }
        var bodyKey = command == "create" ? "file_text" : "new_str";
        var fm = System.Text.RegularExpressions.Regex.Match(raw[bodyFrom..], @"(?<!\\)""" + bodyKey + @"""\s*:\s*""");
        if (!fm.Success) { reason = "body_missing"; return null; }
        var (text, complete) = DecodeJsonStringPrefix(raw, bodyFrom + fm.Index + fm.Length);
        var dropped = 0;
        if (!complete)
        {
            var nl = text.LastIndexOf('\n');
            if (nl < 0) { reason = "no_complete_line"; return null; }
            dropped = text.Length - (nl + 1);
            text = text[..(nl + 1)];
        }
        if (text.Trim().Length == 0) { reason = "blank"; return null; }
        var loopedLines = 0;
        // LAW 237: the second ruler. The tail sample is one instrument; the
        // whole body's duplicate mass is another that no period can hide from.
        var dupPct = DuplicateLinePct(text);
        var duplicateMass = dupPct is int dp && dp >= DuplicateMassLoopPct;
        string? loopSignal = looping ? "tail" : duplicateMass ? "duplicate_mass" : null;
        if (looping || duplicateMass)
        {
            // ⭐ LAW 233 (EpicForge batch 9, 2026-09-09 06:30-06:40Z, VETT
            // c676b1f): the law-231 abort fired on the wire exactly as
            // designed -- five facts-file creates stopped at 12.3 K chars
            // with tails at 3-7% (genuine facts files read 19-31%) -- and
            // then EVERY seat looped again after the nudge, three in a
            // row, 2.5 min each, one abort short of `truncated_response_
            // exhausted`. The seat cannot write "100 distinct items" in
            // one breath; what it CAN do is the first 60-80, and those
            // arrived, honest and distinct, before the tail degenerated.
            // Throwing them away made the next attempt the same attempt.
            // Bank the distinct prefix, close the reply, and ask for the
            // REST -- a smaller ask than the one that looped.
            var kept = DistinctPrefix(text);
            if (kept is null) { reason = "loop_no_distinct_prefix"; return null; }
            loopedLines = CountLines(text) - CountLines(kept);
            text = kept;
            complete = false;
        }
        return new SalvagedCreate(path, text, CountLines(text), dropped, complete, loopedLines, tailPct, command, insertLine, oldStr, loopSignal, dupPct);
    }

    private static int CountLines(string text) => text.Count(ch => ch == '\n') + (text.EndsWith('\n') ? 0 : 1);

    /// <summary>The longest leading run of lines that (a) never literally
    /// repeats an earlier line of 16+ characters (short structural lines
    /// like <c>],</c> may recur) and (b) whose own tail still deflates at or
    /// above <see cref="RepeatingTailCompressionPct"/> -- a templated loop
    /// that never repeats a line literally ("X is a real name." over forty
    /// names) is trimmed by quarters until its tail reads honest. Null when
    /// fewer than <see cref="MinSalvagedDistinctLines"/> survive.</summary>
    internal static string? DistinctPrefix(string text)
    {
        var lines = text.Split('\n');
        var n = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keep = n;
        for (var i = 0; i < n; i++)
        {
            var key = lines[i].Trim();
            if (key.Length < 16) continue;
            if (!seen.Add(key)) { keep = i; break; }
        }
        while (keep >= MinSalvagedDistinctLines)
        {
            var candidate = string.Join("\n", lines, 0, keep) + "\n";
            var pct = ArgumentTail.CompressionPct(candidate);
            if (pct is null || pct >= RepeatingTailCompressionPct) return candidate;
            keep -= Math.Max(1, keep / 4);
        }
        return null;
    }

    /// <summary>Decode the JSON string starting at <paramref name="start"/>
    /// (just after its opening quote) up to its closing quote or the end of
    /// the data. An incomplete trailing escape is dropped. Returns the text
    /// and whether the closing quote was seen.</summary>
    internal static (string Text, bool Complete) DecodeJsonStringPrefix(string raw, int start)
    {
        var sb = new System.Text.StringBuilder();
        var i = start;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == '"') return (sb.ToString(), true);
            if (c != '\\') { sb.Append(c); i++; continue; }
            if (i + 1 >= raw.Length) break;
            var e = raw[i + 1];
            switch (e)
            {
                case '"': sb.Append('"'); i += 2; break;
                case '\\': sb.Append('\\'); i += 2; break;
                case '/': sb.Append('/'); i += 2; break;
                case 'b': sb.Append('\b'); i += 2; break;
                case 'f': sb.Append('\f'); i += 2; break;
                case 'n': sb.Append('\n'); i += 2; break;
                case 'r': sb.Append('\r'); i += 2; break;
                case 't': sb.Append('\t'); i += 2; break;
                case 'u':
                    if (i + 6 > raw.Length) return (sb.ToString(), false);
                    if (!int.TryParse(raw.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var cp))
                        return (sb.ToString(), false);
                    sb.Append((char)cp); i += 6; break;
                default:
                    // Not a JSON escape: keep the backslash literally and move on.
                    sb.Append(c); i++; break;
            }
        }
        return (sb.ToString(), false);
    }

    /// <summary>Law 236: a refused salvage's text is written beside its
    /// target (`path.salvaged`, then `path.salvaged-&lt;call id tail&gt;`) so
    /// the generated lines survive the refusal. Returns the path written, or
    /// null when neither name could be created.</summary>
    /// <summary>The target's current view for the salvage landing check:
    /// (null, true) when the file does not exist, (view, true) when it does,
    /// (null, false) when the sandbox cannot view at all.</summary>
    internal static async Task<(string? View, bool Measured)> ViewOrNullAsync(ISandbox sandbox, string sessionId, string path, CancellationToken ct)
    {
        try { return (await sandbox.FileViewAsync(sessionId, path, ct), true); }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return (null, true); }
        catch (Exception) { return (null, false); }
    }

    internal static async Task<string?> BankSalvageAsideAsync(ISandbox sandbox, string sessionId, SalvagedCreate s, string callId, CancellationToken ct)
    {
        var tag = callId.Length > 6 ? callId[^6..] : callId;
        foreach (var candidate in new[] { s.Path + ".salvaged", s.Path + ".salvaged-" + tag })
        {
            try { await sandbox.FileCreateAsync(sessionId, candidate, s.Text, ct); return candidate; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* exists, or the sandbox cannot write: try the next name */ }
        }
        return null;
    }

    /// <summary>Law 236: the user-turn notice when the tool refused the
    /// salvaged call. Says what landed (nothing), why, and where the text is.</summary>
    internal static string SalvageRefusedNotice(SalvagedCreate s, string toolResult, string? sidePath, int? cap, int outputTokens)
    {
        var call = "`file_editor " + s.Command + " " + s.Path + "`";
        var err = toolResult.Length > 300 ? toolResult[..300] + "..." : toolResult;
        var kept = sidePath is null
            ? "and they could not be banked anywhere, so they are LOST -- regenerate them in small batches " + FitPhrase(cap) + " each"
            : "so they were saved to `" + sidePath + "` instead: NOTHING you generated is lost. To make them the file, run `terminal` with `mv "
              + sidePath + " " + s.Path + "` (after `rm " + s.Path + "` if you meant to replace it). To add them to the existing file, `view` `"
              + sidePath + "` and `insert` its lines. Do NOT regenerate them";
        return "SALVAGE REFUSED: your " + call + " hit " + CapPhrase(cap) + " (" + outputTokens + " generated). The harness banked the first "
            + s.Lines + " complete lines (" + s.Text.Length + " characters) and re-ran the call with them, but the tool REFUSED it: " + err
            + " -- so NOTHING landed in `" + s.Path + "`, " + kept + ". Then continue with what the task asked for, in small batches "
            + FitPhrase(cap) + " each.";
    }

    /// <summary>The user-turn notice after a salvaged create ran.</summary>
    private static string SalvageNotice(SalvagedCreate s, int? cap, int outputTokens, bool aborted = false, int? rawChars = null)
    {
        var lastLine = s.Text.TrimEnd('\n', '\r');
        var cut = lastLine.LastIndexOf('\n');
        lastLine = cut >= 0 ? lastLine[(cut + 1)..] : lastLine;
        if (lastLine.Length > 120) lastLine = lastLine[..120] + "...";
        // Law 234: the same notice for insert / str_replace bodies.
        var call = "`file_editor " + s.Command + " " + s.Path + "`";
        var body = s.Command == "create" ? "`file_text`" : "`new_str`";
        var landed = s.Command switch
        {
            "insert" => "were inserted into `" + s.Path + "` after line " + s.InsertLine,
            "str_replace" => "were written into `" + s.Path + "` in place of the old text",
            _ => "were written to `" + s.Path + "`",
        };
        // 2026-09-09 (s12a it.19): after a banked create the seat anchored a
        // str_replace on `...loud.",\n];` -- a closing line that was never
        // written -- and the tool said no_match. Say what the tail IS.
        var tailNote = s.Command == "create"
            ? "The file ends EXACTLY at that line: there is NOTHING after it, no closing bracket, brace or `];`, so a "
              + "`str_replace` whose old_str includes a closing line will MISS (str_replace_no_match). `view` the last lines "
              + "before anchoring, or `insert` after line " + s.Lines + ". "
            : "";
        var resume = s.Command == "create"
            ? "continue from line " + (s.Lines + 1) + " with `insert` (or `str_replace` on that last line)"
            : "continue right after that last line with `insert` (or `str_replace` on it)";
        if (s.LoopedLines > 0)
            return "SALVAGED FROM A LOOP: your " + call + " was STOPPED "
                + (aborted
                    ? "by the harness " + (rawChars ?? 0) + " characters in, "
                    : "at " + CapPhrase(cap) + " (" + outputTokens + " generated), ")
                + "because its content had begun REPEATING itself ("
                + (s.LoopSignal == "duplicate_mass"
                    ? s.DuplicatePct + "% of its lines were literal repeats of earlier lines"
                    : "the last " + TailSampleChars + " characters compress to " + s.TailPct + "% of their size")
                + "). The first " + s.Lines
                + " lines, which were still distinct, " + landed + "; the banked text ends at: " + lastLine
                + " -- the " + s.LoopedLines + " repeating lines after them were DROPPED, and nothing past the stop was "
                + "generated. The file is INCOMPLETE. " + tailNote + "Do NOT re-create it and do NOT re-send the " + s.Lines
                + " lines that landed: " + resume
                + ", adding NEW items that differ from every one already in the file, in small batches "
                + FitPhrase(cap) + " each, and stop as soon as the file holds what the task asked for. If you cannot "
                + "think of more distinct items, close the file with what it has rather than repeating.";
        return "SALVAGED: your " + call + " hit " + CapPhrase(cap) + " (" + outputTokens
            + " generated) inside " + body + ". "
            + (s.Complete
                ? "The whole " + body + " had arrived, so " + (s.Command == "create" ? "the file was created in full (" : "it was applied in full (") + s.Lines + " lines); only the "
                  + "arguments after it were cut. "
                : "The first " + s.Lines + " complete lines that arrived (" + s.Text.Length + " characters) " + landed
                  + "; the last line that landed is: " + lastLine + " -- and " + s.DroppedChars
                  + " characters after it, plus everything you had not yet generated, were NOT written. The file is "
                  + "incomplete until you append the rest. ")
            + tailNote + "Do NOT re-create the file and do NOT re-send the part that landed: " + resume
            + ", in pieces " + FitPhrase(cap) + " each.";
    }

    /// <summary>The nudge when the cut call was the reply's only call and the
    /// reply is dropped.</summary>
    private static string CutToolCallNudge(string tool, int? cap, int outputTokens, string? raw, int? tailPct, int proseChars, RepeatingArgumentsException? abort) =>
        abort is not null
            ? AbortedSentence(abort, tool) + "Nothing ran and the reply was discarded. "
              + (abort.Siblings > 0
                  ? "Do NOT re-issue that series. If a command's output was too long, ask for LESS of it (a narrower "
                    + "path, a grep, a count) -- never for more of the same; a listing you have already read once is not "
                    + "read better a second time. Make ONE different tool call next, and say in one sentence what its "
                    + "result will decide. "
                  : "Do not retry the same write: put far fewer items in each call and end the call once they are "
                    + "written; a file that does not fit in one call is built across several calls (create the first "
                    + "part, then str_replace or insert the rest), each " + FitPhrase(cap) + ". ")
              + "A tool call you have ALREADY made, whose result is already in this conversation, must not be made "
              + "again — use the result you have."
            : "Your previous reply hit " + CapPhrase(cap) + " (" + outputTokens + " generated) INSIDE the arguments of your `"
        + tool + "` call: the arguments were cut off, nothing ran, and the reply was discarded. "
        + CutArgumentsSentence(raw, tailPct)
        + (proseChars > 2000
            ? proseChars + " characters of prose came before the call -- do not draft content in prose and then write it; "
              + "write it once, inside the call. "
            : "")
        + "Make the tool call FIRST, and put far less content in each call: a write that does not fit in one call is built "
        + "across several calls (create the first part, then str_replace or insert the rest), each " + FitPhrase(cap) + ". "
        + "A tool call you have ALREADY made, whose result is already in this conversation, must not be made again — use "
        + "the result you have.";

    private static Dictionary<string, object?> ExtractArgs(FunctionCallContent fc)
    {
        var args = new Dictionary<string, object?>();
        if (fc.Arguments is not null)
        {
            foreach (var (k, v) in fc.Arguments)
                args[k] = v;
        }
        return args;
    }

    private static AgentResult Finalize(AgentState state, string reason) => new()
    {
        Messages = state.Messages,
        StopReason = reason,
        Iterations = state.Iteration,
        InputTokens = state.TotalInputTokens,
        OutputTokens = state.TotalOutputTokens,
    };

    /// <summary>
    /// Run a configured lint/test command via the sandbox after the
    /// agent wrote files. On non-zero exit, append the output as a
    /// user-style turn so the model self-corrects on the next iteration.
    /// On success, emit a quiet event but don't pollute the chat — the
    /// agent doesn't need to know lint passed; it just needs to know
    /// when it failed.
    ///
    /// Output is capped at 4KB so a screen-full of lint errors doesn't
    /// blow the context window. Emits an <c>auto_check</c> event with
    /// kind/exit/duration so the chat UI can render a compact marker.
    /// </summary>
    private const int AutoCheckOutputCap = 4096;
    private static async Task RunAutoCheckAsync(
        AgentEnvironment env, string kind, string cmd,
        IReadOnlyList<string> writtenPaths, AgentState state,
        Action<string, Dictionary<string, object?>> Emit,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        Vett.Sandbox.BashResult result;
        try
        {
            result = await env.Sandbox.BashExecAsync(env.SessionId, cmd, timeoutSec: 120, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Emit("auto_check", new()
            {
                ["kind"] = kind,
                ["status"] = "error",
                ["message"] = ex.Message,
                ["iteration"] = state.Iteration,
            });
            return;
        }
        var durMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

        // Treat 0 exit as pass. Some test runners exit non-zero only on
        // genuine failures (xunit, jest, dotnet test) — that's the
        // contract we lean on. If a user's tool returns non-zero on
        // warnings, they need to wrap it.
        if (result.ExitCode == 0)
        {
            Emit("auto_check", new()
            {
                ["kind"] = kind,
                ["status"] = "pass",
                ["exit_code"] = 0,
                ["duration_ms"] = durMs,
                ["iteration"] = state.Iteration,
                ["touched_files"] = writtenPaths.ToList(),
            });
            return;
        }

        // Failure path — pipe the output back into the conversation
        // wrapped in a marker tag so the model recognizes it as
        // automated feedback, not user input. Cap at AutoCheckOutputCap
        // chars so a screen of stack traces doesn't blow the context.
        var output = (result.Stdout ?? string.Empty).Trim();
        var truncated = false;
        if (output.Length > AutoCheckOutputCap)
        {
            output = output.Substring(0, AutoCheckOutputCap) + "\n…[truncated " + (output.Length - AutoCheckOutputCap) + " more chars]";
            truncated = true;
        }
        var label = kind == "lint" ? "lint_feedback" : "test_feedback";
        var feedback =
            $"<{label} cmd=\"{Escape(cmd)}\" exit_code=\"{result.ExitCode}\">\n" +
            output + "\n" +
            $"</{label}>";
        state.Messages.Add(Chat.User(feedback));
        Emit("auto_check", new()
        {
            ["kind"] = kind,
            ["status"] = "fail",
            ["exit_code"] = result.ExitCode,
            ["duration_ms"] = durMs,
            ["iteration"] = state.Iteration,
            ["truncated"] = truncated,
            ["touched_files"] = writtenPaths.ToList(),
        });
    }

    private static string Escape(string s) =>
        s.Replace("\"", "&quot;").Replace("\n", " ").Trim();

    /// <summary>True when <paramref name="ch"/> is a character commonly
    /// found between a tool/parameter name and its arguments in malformed
    /// text-as-tool-call shapes — space, `(`, `:`, newline, `{`, tab,
    /// `=`. Used by the malformed-tool-call detector to recognize
    /// "model wrote a function call as plain text" patterns.</summary>
    private static bool IsCallSyntaxBoundary(char ch)
        => ch == ' ' || ch == '(' || ch == ':' || ch == '\n' || ch == '{' || ch == '\t' || ch == '=';

    /// <summary>
    /// Re-render a tool-schema JsonElement with `{working_dir}` substituted
    /// for the sandbox's actual cwd. No-op when workingDir is empty
    /// (RpcClient mode — Runner.cs handles substitution there) or when
    /// the schema text doesn't contain the placeholder. The cwd is JSON-
    /// escaped before substitution so a backslash or quote in the path
    /// can't corrupt the schema (Windows paths like
    /// <c>c:\Users\dev\.vett\dispatches\...</c> would otherwise produce
    /// invalid JSON when slotted into a string literal).
    /// </summary>
    private static JsonElement SubstituteWorkingDir(JsonElement schema, string workingDir)
    {
        if (string.IsNullOrEmpty(workingDir)) return schema;
        var raw = schema.GetRawText();
        if (!raw.Contains("{working_dir}", StringComparison.Ordinal)) return schema;
        // Trim the surrounding quotes JsonSerializer.Serialize adds — the
        // placeholder already lives inside a JSON string in the schema.
        var cwdEscaped = JsonSerializer.Serialize(workingDir).Trim('"');
        var substituted = raw.Replace("{working_dir}", cwdEscaped);
        try { return JsonDocument.Parse(substituted).RootElement.Clone(); }
        catch { return schema; }
    }
}
