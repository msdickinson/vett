using System.Text.Json;
using Vett.Config;
using Microsoft.Extensions.AI;
using Vett.Llm;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Agent;

/// <summary>
/// Runs a team: one leader agent with delegation tools,
/// N member agents spawned on demand via goroutine-equivalent Tasks.
/// </summary>
public static class TeamCoordinator
{
    /// <summary>
    /// How many times a team may nest inside another team.
    ///
    /// ⛔ THIS IS NOT COUNTED IN THE UNITS PEOPLE THINK IN. It counts NESTED
    /// TEAM LEVELS, not agents in the chain, and the top-level team is level
    /// ZERO. The agent chain is always TWO longer than this number, because a
    /// team contributes a leader AND its plain members:
    ///
    ///     MaxDispatchDepth = 0  ->  leader, member                  (2 deep)
    ///     MaxDispatchDepth = 1  ->  manager, lead, worker           (3 deep)
    ///     MaxDispatchDepth = 2  ->  manager, lead, worker, sub-worker (4 deep)
    ///
    /// Set to 2 on Mark's explicit call (2026-08-26): "I dont really want
    /// Manger, Team lead, worker... to go DEEPR though. so 3 still maybe... a
    /// 4th. worker and sub worker task if really needed to save context or
    /// somthing but that would max out. if you mean 5 depth 4 depth makes mose
    /// sense max."
    ///
    /// It was 5, which permitted a SEVEN-agent chain — a shape nobody asked
    /// for, nobody has ever run, and which multiplies cost silently: every
    /// extra level is another full leader loop between the work and the person
    /// who wanted it.
    ///
    /// ⚠ ENFORCEMENT IS BY THROW, AFTER THE FACT. A leader is never told its
    /// remaining depth; it discovers the cap by having a dispatch fail.
    ///
    /// ⛔ DEPTH AND WIDTH ARE CAPPED DIFFERENTLY, ON PURPOSE. Depth throws: it is
    /// decided statically before any work happens and nothing can rescue it.
    /// Width REFUSES — see `TaskBoard.MaxConcurrentDispatches`, added 2026-08-27
    /// and off by default — because "you are at your ceiling, wait for a slot"
    /// is a moment-in-time state the leader can actually act on. That refusal is
    /// also the only thing in VETT that tells a manager its budget at all.
    /// </summary>
    internal const int MaxDispatchDepth = 2;

    /// <summary>
    /// TIER 3 (2026-07-13): run a member that is ITSELF A TEAM.
    ///
    /// When <see cref="MemberConfig.Team"/> is set, the member is not a lone
    /// agent loop — it is a sub-leader with its own members. We synthesize a
    /// sub-<see cref="Profile"/> from the member's own config (its llm, tools,
    /// middleware, compaction, iteration budget) and recurse into
    /// <see cref="RunAsync"/> at depth+1.
    ///
    /// The sub-team is handed the SAME sandbox the member would have gotten —
    /// which, when dispatch_worktree is on, is the parent dispatch's ISOLATED
    /// WORKTREE. So the entire sub-team's work accumulates into one worktree
    /// and surfaces to the top leader as a SINGLE diff: it accepts or rejects
    /// the whole feature, exactly as if one very capable member had done it.
    ///
    /// depth+1 finally makes MaxDispatchDepth mean something — before nesting
    /// existed, `depth` was never incremented and the guard was dead code.
    /// </summary>
    /// <summary>
    /// Nesting depth, flowed across the async call chain. AsyncLocal (not a
    /// parameter) because the nested team is driven through
    /// RunInteractiveAsync, which has no depth parameter to thread.
    /// </summary>
    private static readonly AsyncLocal<int> _nestDepth = new();

    /// <summary>
    /// The leader's middleware list: its OWN <c>team.leader.middleware</c> when it
    /// declares one, otherwise the profile-level list.
    ///
    /// ⛔ THE DEFECT THIS CLOSES (2026-08-26). `team.leader.middleware` is a real
    /// field — <see cref="Config.MemberConfig.Middleware"/>, bound from YAML by the
    /// same deserialiser as every other member field — and at depth 0 it had NO
    /// READER. Both leader call sites passed <c>profile.Middleware</c> literally,
    /// so a profile that gave its leader its own chain got the profile's instead.
    /// Nothing errored and nothing logged: the config parsed, validated, and was
    /// then discarded, which reads exactly like config that works.
    ///
    /// Two things made it worth fixing rather than documenting:
    ///
    ///   • EVERY OTHER LEADER FIELD ALREADY FELL THROUGH THIS WAY —
    ///     SystemPrompt, Llm, Temperature, TopP, MaxIterations and Compaction all
    ///     read `team.Leader.X ?? profile.X` within a few lines of the two sites.
    ///     Middleware alone did not, which makes it an oversight rather than a
    ///     deliberate asymmetry.
    ///   • DEPTH ≥ 1 LEADERS ALREADY HONOURED IT. RunNestedTeamAsync synthesises a
    ///     sub-profile with `Middleware = m.Middleware.Count > 0 ? m.Middleware :
    ///     parentProfile.Middleware` (below), so the same YAML behaved differently
    ///     depending on how deep the leader sat in the tree.
    ///
    /// Fall-through, not merge: an explicit list REPLACES the profile's, matching
    /// how members and the nested synthesis already behave. MiddlewareResolver
    /// force-prepends submit_detector when unlisted, so naming a list can never
    /// drop the completion signal.
    ///
    /// ⚠ <see cref="Vett.Bench.Team.EscalationLedger"/> deliberately mirrors this
    /// resolution to declare each role's blind spots. The two MUST move together —
    /// a ledger reading the other list would report the wrong blind spots, which is
    /// worse than reporting none.
    /// </summary>
    internal static List<string> LeaderMiddleware(Config.TeamConfig team, Profile profile) =>
        team.Leader.Middleware.Count > 0 ? team.Leader.Middleware : profile.Middleware;

    /// <summary>
    /// The leader's compaction block, falling through to the profile's.
    ///
    /// ⭐ EXTRACTED 2026-08-27 AS A SEAM, NOT A FIX. Unlike
    /// <see cref="LeaderMiddleware"/> this rule was already CORRECT inline —
    /// both leader sites read <c>team.Leader.Compaction ?? profile.Compaction</c>.
    /// What it lacked was any test at all, and it sat as TWO COPIES of the same
    /// expression ~350 lines apart, which is the exact shape that lets one site
    /// get "simplified" to <c>profile.Compaction</c> while the other keeps
    /// working. One expression, one reader, one test.
    ///
    /// ⛔ WHY AN UNTESTED CORRECT RULE WAS STILL WORTH CLOSING. This field is the
    /// lever for giving a team lead a bigger context window than its members.
    /// If it silently stopped being read, the symptom is: we set 240000, the
    /// lead still compacts at 48000, and NOTHING ERRORS — the block parses,
    /// validates, and is discarded. The conclusion a person draws is "a bigger
    /// window didn't help", and a working option gets abandoned for the wrong
    /// reason. There is no error message to search for and no artifact that
    /// records which threshold was used, so the defect is only reachable by a
    /// test that reads the resolution directly.
    ///
    /// ⚠ <see cref="Vett.Config.EndpointProbe"/> is a SECOND, INDEPENDENT reader
    /// of this same field (EndpointProbe.cs:137 visits the leader seat with
    /// <c>team.Leader.Compaction</c>; :240 applies the same fall-through). It
    /// decides whether to warn that a compaction trigger can never fire. The two
    /// MUST agree — a probe reading the other block warns about a seat that does
    /// not exist, or stays silent about one that is genuinely misconfigured.
    /// LeaderCompactionResolutionTests pins them to the same answer.
    ///
    /// Fall-through is whole-block, not per-field, matching members
    /// (<c>m.Compaction ?? profile.Compaction</c>) — a leader that names a
    /// compaction block gets ONLY what that block says, with
    /// <see cref="Config.CompactionConfig"/>'s own defaults filling the rest.
    /// </summary>
    internal static Config.CompactionConfig? LeaderCompaction(Config.TeamConfig team, Profile profile) =>
        team.Leader.Compaction ?? profile.Compaction;

    /// <summary>
    /// Dispatch-namespace id for a NESTED team, given the parent's session id
    /// and the task id of the dispatch that spawned it.
    ///
    /// ⭐ THIS IS AN EXTRACTED SEAM, NOT A HELPER FOR CONVENIENCE. It has
    /// exactly ONE production call site (RunNestedTeamAsync, below). The rule
    /// was previously inline, which meant the only way to prove it was to pay
    /// for a live nested run — and the defect it fixes READ GREEN when it was
    /// live, so "we ran it and it passed" was never going to be evidence.
    /// Keep it single-call-site: a second copy of this expression anywhere
    /// re-opens the drift the extraction closes.
    ///
    /// ⚠ THE ENCODING IS SUFFICIENT, NOT INJECTIVE. '-' is a legal character
    /// in both inputs and is not escaped, so distinct (sessionId, parentTaskId)
    /// pairs CAN fold to one id — e.g. ("s", "a-1") and ("s-a", "1"). That is
    /// unreachable with real rosters (see DispatchNamespaceCollisionTests,
    /// which constructs the ambiguity explicitly so nobody later mistakes
    /// "fixes the observed collision" for "ids are unique"). If member names
    /// ever become user-supplied, escape the separator before relying on this.
    /// </summary>
    internal static string DeriveSubSessionId(string sessionId, string parentTaskId) =>
        string.IsNullOrEmpty(parentTaskId) ? sessionId : $"{sessionId}-{parentTaskId}";

    /// <summary>
    /// ⭐ THE DEPTH CEILING, ENFORCED — the one place that decides.
    ///
    /// Extracted 2026-08-26 for two reasons, both real:
    ///
    /// 1. AN INVARIANT IS ONLY AS REAL AS ITS FAILURE TEST.
    ///    <see cref="MaxDispatchDepth"/> had a change-detector test pinning it
    ///    to 2, and NOTHING anywhere proved the guard actually refuses a
    ///    too-deep dispatch. The constant could stay 2 while the `if` was
    ///    deleted and the suite would not have noticed. A cap is a claim about
    ///    what gets REFUSED, so the refusal is what has to be tested — the
    ///    guard needed to be reachable from a test to make that possible.
    ///
    /// 2. A DUPLICATED MESSAGE DRIFTS SILENTLY. The identical four-line throw
    ///    was written out at BOTH enforcement sites (the nested-team entry and
    ///    RunAsync). Two copies of a sentence that quotes two derived numbers
    ///    is a diff waiting to half-land: change the cap, update one, and the
    ///    other keeps telling operators the old chain length. One function,
    ///    one sentence.
    ///
    /// Deliberately takes the ALREADY-INCREMENTED depth, matching both call
    /// sites, so the off-by-one lives in exactly one place too.
    /// </summary>
    /// <param name="depth">The depth the dispatch WOULD run at.</param>
    /// <exception cref="InvalidOperationException">If that exceeds the cap.</exception>
    internal static void EnforceDispatchDepth(int depth)
    {
        if (depth <= MaxDispatchDepth) return;
        throw new InvalidOperationException(
            $"Sub-agent dispatch depth exceeded: a team may nest {MaxDispatchDepth} level(s) "
            + $"deep, i.e. an agent chain at most {MaxDispatchDepth + 2} long "
            + "(manager, lead, worker, sub-worker). Possible infinite delegation loop.");
    }

    /// <summary>
    /// ⭐ THE SAME CEILING, ANSWERED FROM THE YAML — before a token is spent.
    ///
    /// <see cref="EnforceDispatchDepth"/> is a RUNTIME guard: it fires when a
    /// dispatch is already in flight, which means an over-deep profile spends
    /// real money getting to its own rejection. But nesting is declared
    /// STATICALLY — <see cref="Config.MemberConfig.Team"/> is a recursive type,
    /// so the depth a profile will reach is a property of the file and is
    /// knowable offline.
    ///
    /// Measured 2026-08-26: `vett validate` gave a profile declaring five agent
    /// levels a clean ✓ and `0 error(s), 0 warning(s)`, rc=0. Everything
    /// validate checks — tools, schemas, prompts, ranges, unknown keys, seat
    /// completeness, endpoint liveness — was genuinely fine. The topology was
    /// simply un-runnable and nothing looked. Same false-green class validate
    /// already documents for the 31 suites, one axis over.
    ///
    /// ⛔ MEMBERS ONLY, DELIBERATELY. The runtime nests on `m.Team` inside the
    /// MEMBERS loop (see the two `m.Team is not null` sites); a `team:` written
    /// under a LEADER is never dispatched. SimpleCommands.CollectTeam does walk
    /// leaders, and is right to — over-inclusion only over-reports endpoints
    /// there. Here over-inclusion would REFUSE a profile that runs fine, so the
    /// walk must match what actually dispatches, not what merely parses.
    ///
    /// Cycle-safe by construction rather than by a visited-set: YamlDotNet
    /// resolves anchors/aliases, so a hand-written `&amp;a`/`*a` can produce a
    /// cyclic graph, and an unbounded walk would HANG the validator on exactly
    /// the malformed profile it exists to diagnose. Returning as soon as the
    /// cap is exceeded bounds recursion at <see cref="MaxDispatchDepth"/>+1
    /// frames, and a cycle always exceeds the cap, so it always terminates.
    /// </summary>
    /// <param name="team">The profile's top-level team, or null for a solo profile.</param>
    /// <returns>
    /// Nesting levels the YAML declares: 0 for a flat team (leader + members),
    /// 1 for manager→lead→worker, and so on. Saturates just past the cap —
    /// this answers "is it too deep?", not "how absurdly deep is it?".
    /// </returns>
    /// <summary>
    /// The width ceiling for one team node, or a throw if the node never
    /// declared one.
    ///
    /// ⛔ FAILS CLOSED, AND `?? 0` WOULD HAVE FAILED OPEN. `0` is this system's
    /// spelling of UNLIMITED, so coalescing an absent value to 0 would quietly
    /// reinstate the exact default the requirement exists to abolish — on every
    /// path that reaches a board WITHOUT going through
    /// <c>Yaml.ValidateProfileForRun</c>. `TeamCoordinator.RunAsync` is a public
    /// entry point and does not, so that path is not hypothetical.
    ///
    /// This is a BACKSTOP, not the primary check: the primary check is in
    /// ValidateProfileForRun, which reports every offending node at once and
    /// names them. This one fires per-board, is reached only if that check was
    /// bypassed, and exists because the guard nearest the danger is the one that
    /// cannot be routed around.
    /// </summary>
    internal static int RequiredWidth(Config.TeamConfig team) =>
        team.MaxConcurrentDispatches ?? throw new InvalidOperationException(
            "team.max_concurrent_dispatches is required on every team node and is absent. "
            + "An absent ceiling means UNLIMITED fan-out, which is never assumed implicitly. "
            + "Add `max_concurrent_dispatches: <n>` to this team node (or `0` to be "
            + "deliberately uncapped). Run `vett validate` to see every node that is missing it.");

    /// <summary>Floor under the derived stall timeout, in seconds. A legitimate
    /// `dotnet test` on a big solution runs 4-8 min under dispatch contention
    /// and emits nothing while it does, so the watchdog must clear that even
    /// for a profile whose LLM budget is small.</summary>
    internal const int StallTimeoutFloorSec = 1200;

    /// <summary>How long a member may emit NOTHING before the watchdog reaps it.
    ///
    /// Derived, not constant, because the quantity it has to outlast is set by
    /// the profile: a member parked inside the SDK's retry stack is silent for
    /// <see cref="Llm.ChatClientFactory.EffectiveRetryBudgetSeconds"/> and is
    /// NOT wedged. Reaping it destroys real work (Shakedown #52).
    ///
    /// Two deliberate choices:
    /// - MAX over profile-level and member-level config rather than replicating
    ///   the merge rules. Over-estimating only delays reaping a genuinely wedged
    ///   member; under-estimating kills a live one. The costs are not symmetric,
    ///   so the tie goes to the larger budget.
    /// - x1.25 margin on top, so a profile sitting exactly at the boundary
    ///   (coding*/spec-* land on 1200s, dead level with the old constant) is not
    ///   decided by scheduling jitter.</summary>
    internal static int StallTimeoutFor(Config.LlmConfig profileLlm, Config.LlmConfig? memberOverride)
    {
        var budget = Llm.ChatClientFactory.EffectiveRetryBudgetSeconds(profileLlm);
        if (memberOverride is not null)
            budget = Math.Max(budget, Llm.ChatClientFactory.EffectiveRetryBudgetSeconds(memberOverride));
        return Math.Max(StallTimeoutFloorSec, (int)(budget * 1.25));
    }

    /// <summary>Give a watchdog-killed member ONE label regardless of which way
    /// the cancellation unwound it.
    ///
    /// ⛔ THE UNWIND PATH IS A RACE, SO THE LABEL WAS TOO. Cancelling a member
    /// resolves two ways:
    ///   ABSORBED — AgentLoop's own `catch (OperationCanceledException) when
    ///     (ct.IsCancellationRequested)` (AgentLoop.cs:430) returns NORMALLY with
    ///     StopReason "cancelled" and REAL counters.
    ///   ESCAPED  — the bare `ct.ThrowIfCancellationRequested()` at
    ///     AgentLoop.cs:306 sits OUTSIDE that try, so the exception propagates to
    ///     Coordinator's synthesizing catch: StopReason "stalled", counters lost.
    /// The same event therefore arrived under two labels, and the common one
    /// ("cancelled") is indistinguishable from a user calling cancel_task. The
    /// diagnosis lived only in the rarer branch.
    ///
    /// The watchdog is the authority on whether a stall happened, so the verdict
    /// keys on <paramref name="watchdogFired"/> and never on the StopReason the
    /// unwind happened to produce.
    ///
    /// Extracted from RunMemberFull so it can be tested: the stall timeout has a
    /// 1200s floor by design, so no test can drive the inline path without either
    /// waiting 20 minutes or configuring a timeout that never ships.
    ///
    /// <paramref name="outerCancelled"/> wins over the watchdog: a user hitting
    /// Stop at the same moment is a cancellation, not a stall, and mislabelling it
    /// would invent stalls in the one situation where they are expected.</summary>
    internal static AgentResult ApplyStallVerdict(
        AgentResult result, bool watchdogFired, bool outerCancelled,
        string memberName, int stallTimeoutSec)
    {
        // Already "stalled" = the escaped path, which wrote the note itself.
        // Re-entering would append a duplicate.
        if (!watchdogFired || outerCancelled || result.StopReason == "stalled")
            return result;

        var note = Chat.Assistant(
            $"[Member {memberName} stalled — no events for {stallTimeoutSec}s. Run was force-cancelled. "
            + "Try a smaller, more targeted task or ask the user for guidance.]");

        return new AgentResult
        {
            Messages = new List<ChatMessage>(result.Messages) { note },
            StopReason = "stalled",
            // ⭐ KEEP THE ABSORBED PATH'S COUNTERS. This is the branch that
            // actually measured them; overwriting with zeros "for consistency"
            // with the escaped path would throw away the only real numbers a
            // stall ever produces.
            Iterations = result.Iterations,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            CountersMeasured = result.CountersMeasured,
            DispatchDiff = result.DispatchDiff,
            DispatchDiffStat = result.DispatchDiffStat,
            DispatchFilesChanged = result.DispatchFilesChanged,
            DispatchWorktreePath = result.DispatchWorktreePath,
            DispatchBranch = result.DispatchBranch,
            SelfAssessment = result.SelfAssessment,
            SelfAssessmentNotes = result.SelfAssessmentNotes,
        };
    }

    internal static int DeclaredNestingDepth(Config.TeamConfig? team) => WalkDeclared(team, 0);

    private static int WalkDeclared(Config.TeamConfig? team, int depth)
    {
        // Past the ceiling the exact figure buys nothing, and stopping here is
        // what makes a cyclic profile terminate instead of hanging.
        if (team is null || depth > MaxDispatchDepth) return depth;

        var deepest = depth;
        foreach (var m in team.Members ?? [])
        {
            if (m?.Team is null) continue;
            var d = WalkDeclared(m.Team, depth + 1);
            if (d > deepest) deepest = d;
            if (deepest > MaxDispatchDepth) return deepest;
        }
        return deepest;
    }

    /// <summary>
    /// ⭐ THE COUNTER THE CAP READS — the other half of the invariant.
    ///
    /// <see cref="EnforceDispatchDepth"/> proves a REFUSAL given a depth. It
    /// says nothing about whether `depth` ever actually GROWS. Until
    /// 2026-08-26 that arithmetic was three inline statements with no test:
    /// change the increment to a constant, and the cap becomes decorative
    /// while the entire suite — including the refusal tests — stays green.
    /// A guard and the number it guards are two separate claims.
    ///
    /// Extracted so both are reachable from a test, and so the increment
    /// exists exactly once.
    /// </summary>
    internal static int NextDispatchDepth() => _nestDepth.Value + 1;

    /// <summary>
    /// Enters a nested dispatch at <paramref name="depth"/>, restoring the
    /// ambient depth when disposed.
    ///
    /// ⛔ RESTORES THE CAPTURED PREVIOUS VALUE, NOT <c>depth - 1</c>. The old
    /// inline form assumed depth was always the ambient value plus one, which
    /// is true today only because <see cref="NextDispatchDepth"/> is the sole
    /// producer. Restoring what was actually there cannot drift if that ever
    /// stops being true, and it makes the save/restore pair symmetric enough
    /// to test directly.
    ///
    /// The AsyncLocal matters for the CONCURRENT case: `assign_async` fires
    /// sibling dispatches through Task.Run, so each sibling gets its own copy
    /// of the execution context and cannot see another's depth. The restore is
    /// still load-bearing for the sequential case, where a nested run is
    /// awaited directly on the leader's own context.
    /// </summary>
    internal static IDisposable EnterDispatchDepth(int depth)
    {
        var previous = _nestDepth.Value;
        _nestDepth.Value = depth;
        return new DepthScope(previous);
    }

    private sealed class DepthScope : IDisposable
    {
        private readonly int _previous;
        private bool _disposed;

        internal DepthScope(int previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _nestDepth.Value = _previous;
        }
    }

    private static async Task<AgentResult> RunNestedTeamAsync(
        Profile parentProfile, Config.MemberConfig m,
        IChatClient memberClient, string memberModel,
        ISandbox sandbox, string sessionId, string parentTaskId, string task,
        Action<Event>? onEvent, CancellationToken ct,
        Dictionary<string, ToolFn> extraTools,
        Dictionary<string, JsonElement> extraSchemas,
        Dictionary<string, MiddlewareFn> extraMiddlewares,
        string cwd)
    {
        var depth = NextDispatchDepth();
        EnforceDispatchDepth(depth);
        var subProfile = new Profile
        {
            Sandbox = parentProfile.Sandbox,
            // Merged so the sub-team's own members can still inherit/override
            // from THIS member's llm (e.g. a Pro feature-lead whose
            // implementer overrides down to local Flash).
            Llm = Llm.ChatClientFactory.Merge(parentProfile.Llm, m.Llm),
            SystemPrompt = parentProfile.SystemPrompt,
            // The sub-LEADER's toolset. Same fall-through rule as the top
            // level: profile.Tools IS the leader's list (see LeaderWriteGuard).
            Tools = m.Tools.Count > 0 ? m.Tools : parentProfile.Tools,
            Middleware = m.Middleware.Count > 0 ? m.Middleware : parentProfile.Middleware,
            Compaction = m.Compaction ?? parentProfile.Compaction,
            MaxIterations = m.MaxIterations > 0 ? m.MaxIterations : parentProfile.MaxIterations,
            TimeoutMinutes = parentProfile.TimeoutMinutes,
            Team = m.Team,
            // A sub-team is still governed by the parent's profile FILE;
            // carrying the path keeps run artifacts able to name the ruler
            // at every nesting depth.
            SourcePath = parentProfile.SourcePath,
        };

        // Drive the sub-team through RunInteractiveAsync — the SAME entry point
        // `vett chat` and `team-bench` use. The one-shot RunAsync overload has
        // NO dispatch worktrees, no accept/reject tools, no write-guard and no
        // best-of; a sub-team run through it would silently be a lesser team
        // (its implementer would edit the feature worktree directly, with no
        // review gate). Feed the task as a single user turn, then close the
        // channel so the sub-leader finishes and returns instead of waiting for
        // more input — exactly how Bench/Team/Harness.cs drives it.
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync(task, ct);
        chan.Writer.Complete();

        // ⛔ THE SUB-TEAM GETS ITS OWN DISPATCH NAMESPACE.
        //
        // Dispatch worktrees live at ~/.vett/dispatches/<panelId>/<taskId>
        // and branch as vett/dispatch/<panelId>-<taskId>
        // (DispatchWorktreeManager.cs:17-19). panelId IS this sessionId.
        //
        // Task ids are minted PER TaskBoard (TaskBoard.cs:173-176,
        // `_nextIdByMember`) and every RunAsync/RunInteractiveAsync builds a
        // FRESH board — so each sub-team's numbering restarts at 1. Passing
        // the parent's sessionId straight down therefore gave two SIBLING
        // sub-teams the SAME <panelId>/<taskId>, i.e. one directory and one
        // branch name for two concurrent dispatches.
        //
        // MEASURED 2026-08-26 (team-manager-tier3, mgr2): feature-lead-a and
        // feature-lead-b both minted `implementer-1` into
        //   ~/.vett/dispatches/team-bench-mgr2-.../implementer-1
        // and were BOTH LIVE IN IT for 18.1s (01:53:57.699-01:54:25.835),
        // each running its own edits and `dotnet build`. Worse,
        // PreCleanByTaskIdAsync (below, ~:518) fires before Create, so the
        // second arrival actively cleans the first one's live worktree.
        //
        // ⛔ AND THE SUITE PASSED. Assertions score the top-level workspace,
        // which is downstream of accept_dispatch, so a trampled sub-team
        // worktree is invisible to every file assertion. This corrupts
        // SILENTLY and reads GREEN — it can only be seen in the event
        // stream, as two live intervals over one path.
        //
        // Qualifying panelId with the parent dispatch's task id makes the
        // namespace unique per nesting path:
        //   team-bench-mgr2-...-feature-lead-a-1/implementer-1
        //   team-bench-mgr2-...-feature-lead-b-1/implementer-1
        // SanitizeId permits '-', so this survives the path/branch sanitizer
        // unchanged. Top-level runs are untouched: they never come through
        // here, so their panelId keeps its existing shape.
        var subSessionId = DeriveSubSessionId(sessionId, parentTaskId);

        using (EnterDispatchDepth(depth))
        {
            return await RunInteractiveAsync(
                subProfile, memberClient, memberModel, sandbox, subSessionId,
                chan.Reader,
                onAssistantText: null, onWaitingForInput: null, onEvent: onEvent,
                seedHistory: null, turnInterrupt: null, compactRequest: null,
                cwd: cwd, ct: ct,
                extraTools: extraTools, extraSchemas: extraSchemas,
                extraMiddlewares: extraMiddlewares);
        }
    }

    public static Task<AgentResult> RunAsync(
        Profile profile, IChatClient client, string model,
        ISandbox sandbox, string sessionId,
        string userMessage, Action<Event>? onEvent, CancellationToken ct,
        Dictionary<string, ToolFn>? extraTools = null,
        Dictionary<string, JsonElement>? extraSchemas = null,
        Dictionary<string, MiddlewareFn>? extraMiddlewares = null)
        => RunAsync(profile, client, model, sandbox, sessionId, userMessage, onEvent, ct,
            extraTools ?? new(), extraSchemas ?? new(), extraMiddlewares ?? new(), depth: 0);

    /// <summary>
    /// Interactive variant for chat sessions: the leader runs through
    /// AgentLoop.RunInteractiveAsync so the user can keep typing turn
    /// after turn. Member dispatches still run as one-shot AgentLoop.RunAsync
    /// per assign_task call; their events are tagged with `thread_id`
    /// (the member's name) so the extension can group them into
    /// per-thread tabs in the chat panel.
    ///
    /// <paramref name="cwd"/> is used to load VETT.md project
    /// instructions and prepend them to BOTH the leader's system prompt
    /// and every member's system prompt — without it, sub-agents lose
    /// project context that the user expects them to have.
    /// </summary>
    public static Task<AgentResult> RunInteractiveAsync(
        Profile profile, IChatClient client, string model,
        ISandbox sandbox, string sessionId,
        System.Threading.Channels.ChannelReader<string> userInput,
        Action<string>? onAssistantText,
        Action? onWaitingForInput,
        Action<Event>? onEvent,
        IReadOnlyList<ChatMessage>? seedHistory,
        TurnInterrupt? turnInterrupt,
        CompactRequest? compactRequest,
        string cwd,
        CancellationToken ct,
        Dictionary<string, ToolFn>? extraTools = null,
        Dictionary<string, JsonElement>? extraSchemas = null,
        Dictionary<string, MiddlewareFn>? extraMiddlewares = null,
        Vett.Tools.PermissionGate? leaderPermissionGate = null,
        bool planMode = false,
        Vett.Tools.PlanModeUnlockService? planUnlockService = null,
        // True only from `vett chat`, where a person is reading the leader's
        // output. The bench harness drives this same entry point with a channel
        // nobody types into, so it must default to false — see
        // AgentEnvironment.HumanAtTheKeyboard.
        bool humanAtTheKeyboard = false,
        // Opt back in to "the leader calling finish ends the whole chat".
        // Ignored unless humanAtTheKeyboard; see AgentEnvironment.
        bool agentStopEndsSession = false)
        => RunInteractiveImpl(profile, client, model, sandbox, sessionId,
            userInput, onAssistantText, onWaitingForInput, onEvent,
            seedHistory, turnInterrupt, compactRequest, cwd, ct,
            extraTools ?? new(), extraSchemas ?? new(), extraMiddlewares ?? new(),
            leaderPermissionGate, planMode, planUnlockService, humanAtTheKeyboard,
            agentStopEndsSession);

    /// <summary>
    /// Interactive team coordinator. Same setup as <see cref="RunAsync"/>
    /// — leader, members, leader-tools wiring — but the leader runs
    /// through <see cref="AgentLoop.RunInteractiveAsync"/> so user
    /// input keeps flowing turn after turn. Member events are tagged
    /// with `thread_id` (member name) and bracketed by
    /// <c>dispatch_start</c> / <c>dispatch_end</c> events the chat UI
    /// uses to render per-thread tabs.
    /// </summary>
    private static async Task<AgentResult> RunInteractiveImpl(
        Profile profile, IChatClient client, string model,
        ISandbox sandbox, string sessionId,
        System.Threading.Channels.ChannelReader<string> userInput,
        Action<string>? onAssistantText,
        Action? onWaitingForInput,
        Action<Event>? onEvent,
        IReadOnlyList<ChatMessage>? seedHistory,
        TurnInterrupt? turnInterrupt,
        CompactRequest? compactRequest,
        string cwd,
        CancellationToken ct,
        Dictionary<string, ToolFn> extraTools,
        Dictionary<string, JsonElement> extraSchemas,
        Dictionary<string, MiddlewareFn> extraMiddlewares,
        Vett.Tools.PermissionGate? leaderPermissionGate,
        bool planMode,
        Vett.Tools.PlanModeUnlockService? planUnlockService,
        bool humanAtTheKeyboard,
        bool agentStopEndsSession)
    {
        var team = profile.Team ?? throw new InvalidOperationException("No team config");
        var _planUnlockService = planUnlockService;
        // Load VETT.md once and prepend to every agent's system prompt
        // so the leader and all members share the same project context.
        var projectInstructions = ProjectInstructions.Load(cwd);
        string ApplyInstructions(string basePrompt)
        {
            if (string.IsNullOrEmpty(projectInstructions)) return basePrompt;
            return
                "<project_instructions>\n" +
                projectInstructions + "\n" +
                "</project_instructions>\n\n" +
                basePrompt;
        }
        var board = new TaskBoard
        {
            // Auto-inject is per-profile (typically chat profiles only);
            // bench profiles leave it false and the leader polls
            // explicitly. Toggling at construction time keeps the
            // Complete/Fail hot path branch-free for bench runs.
            AutoInjectCompletions = team.AutoInjectAsyncResults,
            // Width ceiling — REQUIRED on every team node since 2026-08-27.
            MaxConcurrentDispatches = RequiredWidth(team),
        };
        var members = team.Members.ToDictionary(m => m.Name);
        var memberClientCache = new Dictionary<string, IChatClient>();
        // Per-task message-history cache. Keyed by task_id so parallel
        // dispatches to the same member each store their own state.
        // continue_task reads from this to resume a member's prior
        // conversation; otherwise the cache is just a side effect.
        var memberStateCache = new Dictionary<string, (string MemberName, List<ChatMessage> Messages)>();

        // Per-dispatch worktree state. Created lazily on first dispatch
        // when team.dispatch_worktree=true AND cwd is a git repo. Failures
        // (not a repo, git missing, sandbox doesn't support WithCwd) fall
        // back transparently to in-place dispatch with a one-time warning
        // emitted on the first failure so the user knows isolation is off.
        var dispatchEnabled = team.DispatchWorktree;
        var dispatchManager = dispatchEnabled
            ? new DispatchWorktreeManager(cwd, sessionId)
            : null;

        // Startup auto-prune: anything under ~/.vett/dispatches/ older
        // than dispatch_max_age_days gets removed. Cheap (one stat per
        // dir) and bounded — prevents abandoned worktrees from a crashed
        // vett run growing forever. Only runs when worktrees are enabled
        // for this profile, since other profiles never write to the dir.
        if (dispatchManager is not null && team.DispatchMaxAgeDays > 0)
        {
            try
            {
                var removed = DispatchWorktreeManager.AutoPrune(TimeSpan.FromDays(team.DispatchMaxAgeDays));
                if (removed > 0)
                {
                    onEvent?.Invoke(new Event("dispatch_prune", new()
                    {
                        ["thread_id"] = "main",
                        ["removed"] = removed,
                        ["max_age_days"] = team.DispatchMaxAgeDays,
                    }));
                }
            }
            catch { /* prune is best-effort; failure shouldn't block chat startup */ }
        }
        var dispatchFallbackWarned = false;
        // Registry of dispatches still pending leader review. Keyed by
        // taskId. accept_dispatch / reject_dispatch tools mutate via
        // closures in LeaderTools below.
        var pendingDispatches = new Dictionary<string, PendingDispatch>();
        var pendingDispatchesLock = new object();
        // ⛔ SERIALIZES `git apply` AGAINST THE SHARED PARENT WORKTREE.
        //
        // pendingDispatchesLock protects the REGISTRY, and it is correctly
        // released before the apply — you cannot hold a `lock` across an await.
        // But that leaves the apply itself unguarded, and the apply is the part
        // that races: DispatchWorktreeManager holds NO lock of its own (checked
        // — no lock/SemaphoreSlim/Monitor/Interlocked anywhere in the file), and
        // ApplyAsync is a two-phase `git apply --check` then `git apply` against
        // ONE parent tree, with its own comment conceding "a race against parent
        // file changes between check and apply could still fail".
        //
        // Reachable today: AgentLoop dispatches multiple tool calls from a
        // single assistant message CONCURRENTLY via Task.WhenAll
        // (AgentLoop.cs:1124-1148), so a leader that emits two accept_dispatch
        // calls in one turn runs two `git apply` invocations against the same
        // worktree at the same time. Best case they collide on git's index.lock
        // and one fails with an error naming a lock file rather than a conflict;
        // worse case both pass --check against the pre-apply tree and the second
        // applies onto a tree that moved underneath it.
        //
        // A SemaphoreSlim, not a lock: the guarded region is async.
        // Passed EXPLICITLY to both consumers rather than defaulted, so a third
        // promotion path cannot be added without confronting this parameter.
        var promotionGate = new SemaphoreSlim(1, 1);
        // task_id -> the reason CaptureAsync could not be performed.
        //
        // COULD-NOT-MEASURE IS NOT MEASURED-ZERO. A failed capture produces a
        // synthetic DispatchCapture with FilesChanged=0, which is byte-identical
        // to a member that genuinely changed nothing. AgentResult has no field
        // to carry the difference (it lives in AgentLoop.cs and is shared with
        // the non-team paths), so the coordinator keeps its own record and
        // RunMember reads it back — otherwise the leader is told "DISPATCH
        // COMPLETED — NO CHANGES" about a dispatch nobody measured.
        var captureFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        var captureFailuresLock = new object();

        async Task<AgentResult> RunMemberFull(string taskId, string name, string task, CancellationToken mct)
        {
            if (!members.TryGetValue(name, out var m))
                throw new InvalidOperationException($"Unknown member \"{name}\"");

            // Defense-in-depth: strip the parent workspace root from the task
            // string before passing it to the member. Even with the leader
            // system prompt forbidding absolute paths in task descriptions,
            // a model that ignores the rule used to leak `c:\Users\dev\TEST\`
            // into the implementer's user message — the implementer would
            // then dutifully use that absolute path instead of its own
            // {working_dir}, write outside the worktree, and the dispatch
            // would capture no diff. Stripping all common spellings of the
            // workspace root here prevents that regardless of leader behavior.
            task = StripWorkspaceRoot(task, cwd);

            var tools = Builtins.All();
            foreach (var (k, v) in extraTools) tools[k] = v;

            var filtered = m.Tools.Count > 0
                ? m.Tools.Where(tools.ContainsKey).ToDictionary(k => k, k => tools[k])
                : tools;
            // Always include plugin / MCP tools — the profile's m.Tools list
            // whitelists BUILT-IN tools per-role; plugin tools (namespaced
            // like `mcp__analyzer__*`) are always additive if the plugin is
            // loaded. Without this the analyzer MCP server registered via
            // `mcp_servers:` would be callable by name but never advertised
            // to the member's tool set.
            if (m.Tools.Count > 0)
            {
                foreach (var (k, v) in extraTools)
                    filtered[k] = v;
            }

            var schemas = m.Tools.Count > 0
                ? LoadSchemas(m.Tools, extraSchemas)
                : LoadSchemas(profile.Tools, extraSchemas);

            // Member coordination tool: report_progress lets the member
            // stream findings back to the leader mid-run. The message
            // gets queued in TaskBoard and drained alongside completions
            // at the leader's next iteration boundary. Always available
            // when auto-inject is on (chat mode); a no-op in bench mode
            // because the queue is dropped silently.
            if (team.AutoInjectAsyncResults)
            {
                filtered["report_progress"] = (a, _, _, _) =>
                {
                    var msg = a.TryGetValue("message", out var mv) ? mv?.ToString() ?? "" : "";
                    if (mv is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
                        msg = je.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(msg))
                        return Task.FromResult("Error: message required");
                    board.QueueProgress(taskId, msg);
                    return Task.FromResult("Progress reported to leader. Continue your task — leader will relay this to the user on its next turn.");
                };
                schemas.Add(System.Text.Json.JsonDocument.Parse(
                    """{"type":"function","function":{"name":"report_progress","description":"Stream a progress update back to the leader mid-task. Use this to surface findings, partial results, or status as you discover them. The leader sees the message at its next turn and can relay it to the user. Does NOT end your task — keep working until you have a final answer or hit your iteration limit.","parameters":{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}}}""")
                    .RootElement.Clone());
            }

            var prompt = ApplyInstructions(!string.IsNullOrEmpty(m.SystemPrompt)
                ? m.SystemPrompt
                : "Complete the task.");

            IChatClient memberClient;
            string memberModel;
            lock (memberClientCache)
            {
                if (memberClientCache.TryGetValue(name, out var cached))
                {
                    memberClient = cached;
                    memberModel = !string.IsNullOrEmpty(m.Llm?.Model) ? m.Llm.Model : model;
                }
                else
                {
                    var resolved = ResolveClient(profile.Llm, client, model, m.Llm);
                    memberClient = resolved.Client;
                    memberModel = resolved.Model;
                    memberClientCache[name] = memberClient;
                }
            }

            var memberLlm = new LlmSettings(memberClient, memberModel,
                m.Llm?.Temperature ?? profile.Llm.Temperature ?? 1.0,
                m.Llm?.TopP ?? profile.Llm.TopP,
                m.Llm?.MaxOutputTokens ?? profile.Llm.MaxOutputTokens,
                m.Llm?.PresencePenalty ?? profile.Llm.PresencePenalty,
                m.Llm?.FrequencyPenalty ?? profile.Llm.FrequencyPenalty);
            var memberCaps = new AgentCapabilities(filtered, schemas, MiddlewareResolver.ResolveOrDefault(m.Middleware, extraMiddlewares, memberLlm, m.Compaction ?? profile.Compaction));

            // Stall watchdog: if the member emits zero events for
            // StallTimeoutSec seconds, we force-cancel its run via a
            // dedicated CTS linked to the parent token. This catches
            // failure modes the per-tool timeouts don't: an LLM
            // endpoint that goes dark mid-stream, a non-bash tool that
            // deadlocks, the loop wedged in some unexpected state.
            //
            // The threshold must OUTLAST the LLM layer's own recovery, or the
            // watchdog reaps live work. Shakedown #52: two members killed as
            // "stalled" while simply awaiting a slow response, one of them 10
            // iterations into real work. It also has to clear a legitimate
            // `dotnet test` on a big solution (4-8 min under dispatch
            // contention), which emits nothing while it runs.
            //
            // ⛔ RETRACTED: "1200s, not 360 ... request_timeout_seconds is 900
            // in the deepseek profiles — a slow/hung completion self-heals at
            // 900s (timeout -> retry -> event)".
            //
            // That sentence measured the wrong quantity, so the 360 -> 1200
            // bump was computed against a number that was never the bound.
            // A retry does NOT emit an event: RequestRetryPolicy
            // (ChatClientFactory.cs:368+) retries INSIDE the SDK pipeline,
            // underneath the single `GetResponseAsync` the loop is awaiting.
            // Nothing reaches TaggedEmit until the whole retry stack resolves,
            // so the true silent window is
            //     (num_retries + 1) x request_timeout_seconds
            // not one timeout. ENUMERATED across all 18 shipped profiles (not
            // inferred — the remembered inputs were wrong for two of the three
            // families, and one of them still produced the right total):
            //     ds-*            900s x (2+1) = 2700s  (1200 reaps live work)
            //     coding*/spec-*  300s x (3+1) = 1200s  (EXACT TIE — coin flip)
            //     openhands       300s x (5+1) = 1800s
            //     unset           300s x (5+1) = 1800s  (ChatClientFactory:111)
            // Every family was at or past the constant, so Shakedown #52's bug
            // class was still live behind a comment that said it was fixed.
            //
            // Now derived from the same seam the client is built from, with a
            // 25% margin and the old 1200s as a floor, so a profile that
            // lowers its timeouts cannot lower the watchdog below what a big
            // `dotnet test` needs. StallTimeoutFor is at the bottom of this file.
            var stallTimeoutSec = StallTimeoutFor(profile.Llm, m.Llm);
            var lastEventAt = DateTime.UtcNow;

            // Set by the watchdog BEFORE it cancels; read after the try/finally
            // below. The watchdog is the only authority on "was this a stall" —
            // see the relabel block after the catches for why the StopReason
            // alone cannot answer that. Interlocked because it crosses threads;
            // the `await watchdogTask` in the finally is the happens-before edge
            // that makes the read below safe.
            var watchdogFired = 0;

            // Tag every event from this member with thread_id so the UI
            // can route into the right tab. We also use this hook to
            // refresh the watchdog timer — every event the member emits
            // is "progress" and resets the stall counter. We clone the
            // data dict so nothing leaks back into the caller's
            // structures.
            void TaggedEmit(Event e)
            {
                lastEventAt = DateTime.UtcNow;
                // thread_id = task_id makes each parallel dispatch a
                // distinct UI card even when two are running for the
                // same member. We also include `member` for display.
                //
                // ⛔ `member` IS SET ONLY IF THE EVENT DOES NOT ALREADY CARRY ONE.
                //
                // This tags events from the agent running THIS dispatch, which
                // emits them untagged. But a member with its own `team:` is a
                // whole sub-team (RunNestedTeamAsync), and every event from
                // inside it — including the sub-team's OWN dispatch_end for its
                // OWN members — bubbles through here already correctly named.
                //
                // MEASURED 2026-08-28 (tier-2 arm, DispatchFailOpenTests): an
                // unconditional stamp rewrote the sub-member's dispatch_end from
                // `member: sub-worker` to `member: worker`. Both dispatch_end
                // events in the run then carried the SAME name, for two
                // different agents: anything counting dispatches per member
                // double-counted the outer one and could never observe the inner
                // one at all. The event stream is the only surface on which a
                // trampled sub-team worktree is visible (see the sub-session-id
                // note in RunNestedTeamAsync), so it has to stay honest about
                // WHICH agent each row belongs to.
                //
                // thread_id is still rewritten unconditionally, and must be: a
                // sub-leader's own events arrive tagged `main` (the leader
                // thread id), which would collide with the PARENT leader's
                // thread and merge two agents' iterations into one card.
                var data = new Dictionary<string, object?>(e.Data)
                {
                    ["thread_id"] = taskId,
                };
                if (!data.TryGetValue("member", out var existing)
                    || existing is not string s || string.IsNullOrEmpty(s))
                    data["member"] = name;
                onEvent?.Invoke(new Event(e.Type, data));
            }
            // Per-task injection channel: lets the leader push new user
            // messages into THIS member's conversation mid-run via
            // inject_into_task. Bounded to 32 to prevent runaway leader
            // loops from blowing up memory; in practice you'd never
            // inject more than a handful per task.
            var injectChan = System.Threading.Channels.Channel.CreateBounded<string>(32);
            board.RegisterInjector(taskId, injectChan.Writer);

            // Per-dispatch worktree. Provisioned only when enabled in the
            // profile AND we can actually use it (parent is a git repo,
            // sandbox supports WithCwd, git on PATH). Any failure falls
            // back to the shared sandbox with a one-time warning to the
            // user — the dispatch still runs, just without isolation.
            //
            // After the first failure we set dispatchFallbackWarned and
            // SKIP subsequent CreateAsync attempts entirely. Without this
            // short-circuit, every dispatch in a non-git workspace would
            // pay the cost of starting + failing 1-3 git subprocesses
            // (~10-50ms wasted per dispatch).
            DispatchWorktree? worktree = null;
            ISandbox memberSandbox = sandbox;
            if (dispatchManager is not null && !dispatchFallbackWarned)
            {
                try
                {
                    worktree = await dispatchManager.CreateAsync(taskId, mct, diag =>
                        onEvent?.Invoke(new Event("dispatch_worktree_recovered", new()
                        {
                            ["thread_id"] = "main",
                            ["task_id"] = taskId,
                            ["detail"] = diag,
                        })));
                    // WithDispatchWorktree (vs plain WithCwd) installs a
                    // workspace boundary so absolute paths to the parent
                    // workspace get rewritten into the worktree. Without it
                    // an agent that uses an absolute parent path silently
                    // bypasses isolation and writes into the user's actual
                    // workspace — see Sandbox/DirectBash.ResolvePath for
                    // the policy.
                    // worktree.Cwd (not .Path): the worktree root is the repo
                    // toplevel, but the leader's cwd may be a subdirectory of
                    // it. Cwd re-applies that offset so relative paths mean
                    // the same thing to the member as to the leader.
                    memberSandbox = sandbox.WithDispatchWorktree(worktree.Cwd, cwd);
                }
                catch (Exception ex) when (ex is WorktreeNotSupportedException or NotSupportedException)
                {
                    // Latch ONLY when the cause cannot change for the life of
                    // this session. A NotSupportedException means the sandbox
                    // itself can't host a worktree, which is structural; a
                    // WorktreeNotSupportedException latches only if it says so.
                    //
                    // This used to latch unconditionally, which meant a single
                    // RECOVERABLE failure (a stale directory git couldn't
                    // delete) silently stripped isolation from every remaining
                    // dispatch in the run. armD-run4 lost 12 of 14 dispatches
                    // that way and surfaced it only as a 7200s timeout — the
                    // run looked like a model/cap result when it was a fault.
                    var permanent = ex is not WorktreeNotSupportedException w || w.IsPermanent;
                    if (permanent) dispatchFallbackWarned = true;

                    onEvent?.Invoke(new Event("dispatch_worktree_fallback", new()
                    {
                        ["thread_id"] = "main",
                        ["task_id"] = taskId,
                        ["reason"] = ex.Message,
                        ["permanent"] = permanent,
                        ["effect"] = permanent
                            ? "Per-task worktrees disabled FOR THE REST OF THIS CHAT SESSION — dispatches will share the chat panel's working directory. To re-enable worktrees: fix the underlying issue (initialize a git repo, make at least one commit, etc.), then RESTART THE CHAT PANEL. The session-level latch avoids re-running git on every dispatch in unfixable cases. To suppress this entirely, set team.dispatch_worktree=false in the profile."
                            : "THIS DISPATCH ONLY ran without worktree isolation; its diff was NOT captured. Worktrees remain ENABLED and the next dispatch will retry. Treat this dispatch's result as unmeasured rather than as a no-op.",
                    }));
                    // CreateAsync may have succeeded before WithCwd threw
                    // (e.g., RpcClient sandbox throws NotSupported AFTER
                    // we already provisioned the worktree). Discard
                    // best-effort so we don't orphan the directory.
                    if (worktree is not null)
                    {
                        try { await dispatchManager.DiscardAsync(worktree, CancellationToken.None); } catch { }
                    }
                    worktree = null;
                    memberSandbox = sandbox;
                }
                catch (OperationCanceledException) when (mct.IsCancellationRequested)
                {
                    // Cancellation mid-CreateAsync — the OCE escapes past
                    // the WorktreeNotSupportedException catch above and
                    // would propagate up untouched, leaving any partial
                    // worktree state (dir / branch / .git/worktrees
                    // metadata) orphaned. Best-effort cleanup BY TASK ID
                    // (we don't have a DispatchWorktree handle since
                    // CreateAsync didn't return) using the same id
                    // sanitization rules CreateAsync uses internally.
                    try { await dispatchManager.PreCleanByTaskIdAsync(taskId, CancellationToken.None); } catch { }
                    throw;
                }
            }

            var memberEnv = new AgentEnvironment(memberSandbox, sessionId,
                m.MaxIterations > 0 ? m.MaxIterations : 100, TaggedEmit,
                InjectedMessages: injectChan.Reader);

            using var memberCts = CancellationTokenSource.CreateLinkedTokenSource(mct);
            var watchdogTask = Task.Run(async () =>
            {
                while (!memberCts.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(5000, memberCts.Token); }
                    catch { return; }
                    var idle = (DateTime.UtcNow - lastEventAt).TotalSeconds;
                    if (idle > stallTimeoutSec)
                    {
                        // Record the verdict BEFORE cancelling. The relabel
                        // block after the catches keys off this flag, not off
                        // the StopReason the unwind happens to produce.
                        Interlocked.Exchange(ref watchdogFired, 1);
                        // Surface the stall on the member's thread so
                        // the user sees what happened in the dispatch
                        // card, then trip the CTS so RunAsync unwinds.
                        TaggedEmit(new Event("error", new()
                        {
                            ["message"] = $"Member \"{name}\" stalled — no events for {idle:F0}s (limit {stallTimeoutSec}s); cancelling.",
                        }));
                        try { memberCts.Cancel(); } catch { }
                        return;
                    }
                }
            });

            // ⛔ CLOSE THE BRACKET ON EVERY EXIT, INCLUDING THE THROWING ONES.
            //
            // `dispatch_start` below increments the harness's
            // `dispatchesInFlight` (Harness.cs:527-530). Leaving on an
            // exception without a matching `dispatch_end` strands that counter
            // above zero FOREVER, and the settle loop's `if (inFlight > 0)
            // continue` (Harness.cs:719) can then never be satisfied. The run
            // cannot settle, burns to the wall clock, and is classified
            // `wall_clock_timeout` — a HARNESS fault wearing a MODEL fault's
            // label. Two members in flight when the token trips is enough to
            // make the whole instance unsettleable.
            //
            // ⚠ THIS EVENT IS SYNTHETIC AND MUST NOT LOOK LIKE A CLEAN RUN.
            // Trading a stuck counter for a fabricated completed dispatch would
            // be a worse bug than the one it fixes, so:
            //   - `dispatch_aborted: true` is the unambiguous discriminator
            //   - `iterations`/token counts are NULL — COULD NOT MEASURE, never
            //     0. This is the same law `files_changed` already follows on the
            //     normal path below: a dispatch killed mid-flight must not
            //     report the identical value as one that ran and did nothing.
            //   - `pending_review: false` — nothing was captured, so there is
            //     nothing to accept or reject. Emitting true here would strand
            //     `pendingReviews` instead: that MOVES the deadlock rather than
            //     removing it.
            void EmitAbortedDispatchEnd(string stopReason) =>
                onEvent?.Invoke(new Event("dispatch_end", new()
                {
                    ["thread_id"] = "main",
                    ["member"] = name,
                    ["task_id"] = taskId,
                    ["iterations"] = null,
                    ["stop_reason"] = stopReason,
                    ["input_tokens"] = null,
                    ["output_tokens"] = null,
                    ["diff_summary"] = null,
                    ["files_changed"] = null,
                    ["capture_ran"] = false,
                    ["capture_failed"] = false,
                    ["capture_error"] = null,
                    ["worktree_retained"] = false,
                    ["worktree_retained_reason"] = null,
                    ["self_assessment"] = null,
                    ["self_assessment_notes"] = null,
                    ["pending_review"] = false,
                    ["dispatch_aborted"] = true,
                }));

            // Bracket the member's run with dispatch_start / dispatch_end
            // so the UI knows when a sub-thread spawned and finished.
            // Both events live in the parent's stream (no thread_id) so
            // they can render as cross-thread links from the leader's
            // transcript into the member's tab.
            onEvent?.Invoke(new Event("dispatch_start", new()
            {
                ["thread_id"] = "main",
                ["member"] = name,
                ["task"] = task,
                ["task_id"] = taskId,
                ["worktree_path"] = worktree?.Path,
            }));

            AgentResult result;
            try
            {
                // TIER 3: a member with its own `team:` runs as a SUB-TEAM inside
                // this dispatch's worktree, so the whole feature lands as one
                // diff for the parent leader to accept or reject.
                result = m.Team is not null
                    ? await RunNestedTeamAsync(profile, m, memberLlm.Client, memberLlm.Model,
                        memberEnv.Sandbox, sessionId, taskId, task, TaggedEmit, memberCts.Token,
                        extraTools, extraSchemas, extraMiddlewares, cwd)
                    : await AgentLoop.RunAsync(memberLlm, memberCaps, memberEnv, prompt, task, memberCts.Token);
            }
            catch (OperationCanceledException) when (mct.IsCancellationRequested)
            {
                // Outer cancellation (user hit Stop, chat panel closed,
                // CTS torn down at the leader level). The worktree was
                // already provisioned but we'll never reach the capture
                // path. Best-effort discard so we don't accumulate
                // orphans waiting for auto-prune to reap them in 7 days.
                if (worktree is not null && dispatchManager is not null)
                {
                    try { await dispatchManager.DiscardAsync(worktree, CancellationToken.None); } catch { }
                }

                EmitAbortedDispatchEnd("outer_cancelled");
                throw;
            }
            catch (OperationCanceledException) when (memberCts.IsCancellationRequested && !mct.IsCancellationRequested)
            {
                // Watchdog fired AND the cancellation ESCAPED the agent loop
                // rather than being absorbed by it — synthesize a result so the
                // leader's assign_task call returns with a real observation
                // instead of the OCE propagating up and killing the whole chat.
                //
                // ⛔ THE COUNTERS BELOW ARE NOT MEASUREMENTS AND MUST NOT BE
                // WRITTEN AS ZERO. AgentLoop threw, so its iteration and token
                // state died with it — this catch has no way to learn them. The
                // fields are non-nullable `int` and can only hold 0, which is
                // exactly what a member that ran and did nothing also reports.
                // CountersMeasured=false is the discriminator; the dispatch_end
                // emit publishes null on the strength of it, and the sequential
                // totals skip it. Leaving the three assignments off entirely
                // (rather than writing `= 0`) keeps the code from ASSERTING a
                // value it does not have.
                result = new AgentResult
                {
                    Messages = new List<ChatMessage>
                    {
                        Chat.Assistant($"[Member {name} stalled — no events for {stallTimeoutSec}s. Run was force-cancelled. Try a smaller, more targeted task or ask the user for guidance.]"),
                    },
                    StopReason = "stalled",
                    CountersMeasured = false,
                };
            }
            catch (Exception)
            {
                // ⚠ WIDER THAN THE FINDING SAID. The register described this as
                // the outer-CANCELLATION path, but the two catches above are
                // both OperationCanceledException-specific: ANY other throw out
                // of the agent loop (an HTTP failure, a tool-layer bug, an OOM
                // in a nested sub-team) escapes past `dispatch_start` the same
                // way and strands the same counter. Cancellation was the
                // observed instance, not the boundary of the defect.
                EmitAbortedDispatchEnd("dispatch_faulted");
                throw;
            }
            finally
            {
                try { memberCts.Cancel(); } catch { }
                try { await watchdogTask; } catch { /* watchdog cleanup is best-effort */ }
                board.UnregisterInjector(taskId);
            }

            // ⛔ A STALL MUST BE DIAGNOSABLE NO MATTER WHERE THE CANCEL LANDED.
            //
            // The watchdog's Cancel() unwinds the member two different ways and
            // which one you get is a RACE on where the token is observed:
            //   ABSORBED — AgentLoop's own `catch (OperationCanceledException)
            //     when (ct.IsCancellationRequested)` (AgentLoop.cs:430) returns
            //     NORMALLY with StopReason "cancelled" and REAL counters. No
            //     exception ever reaches the catches above.
            //   ESCAPED  — the bare `ct.ThrowIfCancellationRequested()` at
            //     AgentLoop.cs:306 sits OUTSIDE that try, so the OCE propagates
            //     into the synthesizing catch above: StopReason "stalled", but
            //     the counters are unmeasurable.
            //
            // So the SAME event was reported under TWO labels, and the label you
            // get most of the time — "cancelled" — is indistinguishable from a
            // user calling cancel_task. The diagnosis lived only in the RARER
            // branch. Anyone reading stop_reason to count stalls was reading a
            // number set by a race.
            //
            // The watchdog is the authority on whether a stall happened, so key
            // on that and relabel here. Deliberately KEEP the absorbed path's
            // counters: a real measurement beats a synthetic zero, and this is
            // the branch that actually has them.
            //
            // The `!mct.IsCancellationRequested` guard keeps a genuine outer
            // cancellation racing the watchdog from being mislabelled a stall.
            result = ApplyStallVerdict(result, Volatile.Read(ref watchdogFired) == 1,
                                      mct.IsCancellationRequested, name, stallTimeoutSec);

            // Cache the member's full message history so continue_task
            // can resume this exact instance later. We do this BEFORE
            // dispatch_end so the leader's reaction to the result has
            // a valid task_id to follow up against.
            lock (memberStateCache)
            {
                memberStateCache[taskId] = (name, new List<ChatMessage>(result.Messages));
            }

            // Capture diff + parse self-assessment + register pending
            // dispatch, but ONLY if we actually got a worktree. Without
            // a worktree, none of these fields are meaningful and the
            // result flows through unchanged (matching pre-feature behavior).
            DispatchCapture? capture = null;
            string assessment = "unknown";
            string assessmentNotes = "";
            string? captureError = null;
            string? worktreeRetainedReason = null;
            if (worktree is not null && dispatchManager is not null)
            {
                try
                {
                    capture = await dispatchManager.CaptureAsync(worktree, mct);
                }
                catch (Exception ex)
                {
                    // Capture should rarely fail (it's just `git diff`),
                    // but if it does we don't want to lose the dispatch
                    // result entirely. Record a synthetic capture so the
                    // rest of the path has something to read, and remember
                    // that the zero in it is a COULD-NOT-MEASURE, not a
                    // measured zero — the retention branch below and
                    // RunMember both key off `captureError`.
                    captureError = ex.Message;
                    onEvent?.Invoke(new Event("dispatch_capture_failed", new()
                    {
                        ["thread_id"] = "main",
                        ["task_id"] = taskId,
                        ["error"] = ex.Message,
                    }));
                    capture = new DispatchCapture(false, null, $"(capture failed: {ex.Message})", 0, []);
                    lock (captureFailuresLock) captureFailures[taskId] = ex.Message;
                }

                var finalText = ExtractFinalAssistantText(result);
                (assessment, assessmentNotes) = ParseSelfAssessment(finalText);

                // Only register a pending dispatch if there are real changes.
                // Otherwise there's nothing to accept/reject and the leader
                // can move on without ceremony.
                if (capture.HasChanges)
                {
                    lock (pendingDispatchesLock)
                    {
                        pendingDispatches[taskId] = new PendingDispatch(
                            TaskId: taskId,
                            Member: name,
                            Worktree: worktree,
                            Capture: capture,
                            Manager: dispatchManager,
                            CreatedAt: DateTime.UtcNow,
                            SelfAssessment: assessment,
                            AssessmentNotes: assessmentNotes,
                            Task: task);
                    }
                }
                else if (captureError is not null)
                {
                    // RETAIN — THE WORKTREE IS THE EVIDENCE.
                    //
                    // The old code fell through to the discard below, because a
                    // failed capture and a clean no-op both arrive here as
                    // HasChanges=false. That made the ONE case where the
                    // worktree is the last surviving copy of the member's work
                    // the case that deleted it: git couldn't be read, so we
                    // threw away the thing that would have told anyone what
                    // happened. DispatchWorktreeManager.CaptureAsync was fixed
                    // (2026-08-24) to THROW rather than emit a silent zero, and
                    // its comment names this site as the other half.
                    //
                    // Nothing is registered as a pending dispatch: there is no
                    // diff to accept, and offering accept_dispatch on an
                    // unreadable worktree would just move the failure. The
                    // directory is left for a human, and the existing startup
                    // auto-prune (dispatch_max_age_days, default 7) still bounds
                    // it — retention here is not an unbounded leak.
                    worktreeRetainedReason = "capture_failed";
                    onEvent?.Invoke(new Event("dispatch_worktree_retained", new()
                    {
                        ["thread_id"] = "main",
                        ["member"] = name,
                        ["task_id"] = taskId,
                        ["worktree_path"] = worktree.Path,
                        ["branch"] = worktree.Branch,
                        // Explicit reason so a reader can tell THIS retention
                        // apart from a policy retention (dispatch_retention /
                        // --keep-all-worktrees, applied in DispatchTools on the
                        // accept/reject path). "kept on disk" alone is ambiguous
                        // and the ambiguity is the whole defect.
                        ["reason"] = "capture_failed",
                        ["error"] = captureError,
                        ["detail"] = $"The dispatch's changes could NOT be measured, so the worktree was KEPT at {worktree.Path} " +
                                     "instead of discarded — it is the only surviving record of what the member did. " +
                                     "This is NOT the same as a dispatch that changed nothing. Inspect it manually; " +
                                     "it is subject to the normal dispatch_max_age_days auto-prune.",
                    }));
                }
                else
                {
                    // No changes — clean up the worktree right away. The
                    // member ran but produced nothing; no review needed.
                    // Reached ONLY when capture actually succeeded and
                    // measured zero (see the branch above).
                    try { await dispatchManager.DiscardAsync(worktree, CancellationToken.None); } catch { }
                }
            }

            onEvent?.Invoke(new Event("dispatch_end", new()
            {
                ["thread_id"] = "main",
                ["member"] = name,
                ["task_id"] = taskId,
                // null — NOT 0 — when the run was killed before it could report
                // them. Same law as `files_changed` below, and for the same
                // reason: a stalled dispatch that emitted `iterations: 0` was
                // indistinguishable from one that ran a single clean iteration
                // and stopped. See CountersMeasured on AgentResult.
                ["iterations"] = result.CountersMeasured ? result.Iterations : null,
                ["stop_reason"] = result.StopReason,
                // V2: per-member token usage so the worker's $-ledger can
                // attribute cost to each dispatch, not just the leader.
                // A fabricated 0 here would under-report SPEND, which is the
                // direction that silently flatters the ledger.
                ["input_tokens"] = result.CountersMeasured ? result.InputTokens : null,
                ["output_tokens"] = result.CountersMeasured ? result.OutputTokens : null,
                ["counters_measured"] = result.CountersMeasured,
                ["diff_summary"] = capture?.DiffStat,
                // null — NOT 0 — when no capture ran. `?? 0` here used to
                // report COULD-NOT-MEASURE as MEASURED-ZERO: a dispatch that
                // never got a worktree emitted the identical `files_changed: 0`
                // as a dispatch that ran cleanly and changed nothing. That
                // made 12 lost dispatches in armD-run4 read as 12 clean no-ops.
                // AssertionEngine.FieldInt already maps null -> null and its
                // files_changed_min caller treats null as "no match", so the
                // assertion semantics are unchanged; only the honesty improves.
                // `capture_ran` gives consumers an unambiguous discriminator
                // without having to infer it from worktree_path.
                ["files_changed"] = capture?.FilesChanged,
                ["capture_ran"] = capture is not null,
                // capture_ran says a capture was ATTEMPTED; capture_failed says
                // it did not produce a measurement. Both are needed: without
                // the second, `files_changed: 0` with `capture_ran: true` still
                // reads as a clean no-op when it is really an unread worktree.
                ["capture_failed"] = captureError is not null,
                ["capture_error"] = captureError,
                // Whether the worktree survived this dispatch, and WHY. A later
                // reader must be able to separate "retained because capture
                // failed" (evidence) from a policy retention on the
                // accept/reject path (dispatch_retention / --keep-all-worktrees).
                ["worktree_retained"] = worktreeRetainedReason is not null,
                ["worktree_retained_reason"] = worktreeRetainedReason,
                ["self_assessment"] = assessment,
                ["self_assessment_notes"] = assessmentNotes,
                ["pending_review"] = capture?.HasChanges == true,
                ["worktree_path"] = worktree?.Path,
            }));

            // Augment the AgentResult with dispatch metadata. The leader
            // tools (accept_dispatch / reject_dispatch) read pendingDispatches
            // by task_id; the metadata here is for reporting/observability.
            return new AgentResult
            {
                Messages = result.Messages,
                StopReason = result.StopReason,
                Iterations = result.Iterations,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                // Must be carried, not defaulted: this is a NEW AgentResult, and
                // the init-only default is `true`. Dropping it here would
                // re-fabricate the measured-zero one layer up.
                CountersMeasured = result.CountersMeasured,
                DispatchDiff = capture?.Diff,
                DispatchDiffStat = capture?.DiffStat,
                DispatchFilesChanged = capture?.FilesChanged ?? 0,
                DispatchWorktreePath = worktree?.Path,
                DispatchBranch = worktree?.Branch,
                SelfAssessment = assessment,
                SelfAssessmentNotes = assessmentNotes,
            };
        }

        async Task<string> RunMember(string taskId, string name, string task, CancellationToken mct)
        {
            var result = await RunMemberFull(taskId, name, task, mct);
            var text = ExtractFinalAssistantText(result);

            // ⛔ A MEMBER THAT ENDED BADLY MUST NOT REACH THE LEADER AS "done".
            //
            // MEASURED, not reasoned (fault-injection run, 2026-08-28): a member
            // whose LLM call failed to the retry cap finalized with stop_reason
            // `llm_error` and ZERO tokens. This wrapper discarded StopReason,
            // returned "", and assign_task relayed "[worker-1 — worker done]".
            // The leader read "done", had no content, and INVENTED one. rc=0.
            //
            // The two shapes are genuinely different, so they are handled
            // differently rather than collapsed:
            //
            //   NOTHING TO SHOW — no text, no diff, no capture evidence. There is
            //   no result to relay, so THROW: assign_task’s catch already turns
            //   that into board.Fail + "[id — member FAILED]", the shipped path
            //   for a failed dispatch. The board ends up honest too, which a
            //   returned string cannot achieve — assign_task calls
            //   board.Complete on every non-throwing return.
            //
            //   SOMETHING TO SHOW — partial text, files changed, or a capture
            //   failure. RELAY IT, banner first. A member that hit max_iterations
            //   after real work still has work worth reading; dropping it would
            //   trade this fail-open for a fail-closed that destroys evidence.
            if (!result.StoppedCleanly)
            {
                string? stopCaptureError;
                lock (captureFailuresLock) captureFailures.TryGetValue(taskId, out stopCaptureError);
                if (string.IsNullOrWhiteSpace(text)
                    && result.DispatchFilesChanged == 0
                    && stopCaptureError is null)
                    throw new DispatchFailedException(taskId, name, result.StopReason, result.Iterations);
                text = DispatchNotCleanBanner(taskId, result) + text;
            }

            // Four terminal states for a worktree-mode dispatch:
            //   1. files_changed > 0  → PENDING REVIEW block; leader must accept/reject.
            //   2. capture FAILED     → could-not-measure; worktree retained as evidence.
            //   3. files_changed == 0 → "ran clean, nothing to accept" — tell the leader
            //      explicitly so it doesn't follow stale instructions and call
            //      accept_dispatch on an empty registry.
            //   4. no worktree at all → fallback or non-team dispatch; original behavior.
            // The DON'T-ACCEPT note in case (3) matters because team profiles
            // tell the leader to "review and accept_dispatch" by default; without
            // an explicit nothing-to-accept signal it'll dutifully call the tool
            // and hit "no pending dispatch" — confusing the user.
            //
            // Case (2) has to be split OUT of case (3): both carry
            // files_changed == 0, and collapsing them told the leader "DISPATCH
            // COMPLETED — NO CHANGES ... just relay the member's result and move
            // on" about a dispatch nobody was able to measure.
            if (string.IsNullOrEmpty(result.DispatchWorktreePath))
                return text;

            string? captureError;
            lock (captureFailuresLock) captureFailures.TryGetValue(taskId, out captureError);
            if (captureError is not null)
            {
                var failSb = new System.Text.StringBuilder(text);
                if (failSb.Length > 0) failSb.AppendLine().AppendLine();
                failSb.Append("---\n");
                failSb.Append($"DISPATCH CAPTURE FAILED (task_id: {taskId})\n");
                failSb.Append($"  error: {captureError}\n");
                failSb.Append($"  worktree: {result.DispatchWorktreePath}  (RETAINED — not discarded)\n");
                failSb.Append($"  self_assessment: {result.SelfAssessment}\n");
                if (!string.IsNullOrWhiteSpace(result.SelfAssessmentNotes))
                    failSb.Append($"  notes: {result.SelfAssessmentNotes}\n");
                failSb.Append("The member's changes could NOT be MEASURED. This is NOT 'no changes' — treat it as UNMEASURED. ");
                failSb.Append("There is no diff to accept, so accept_dispatch has nothing registered for this task_id; ");
                failSb.Append("the worktree above was kept on disk so the work can be inspected or recovered by hand. ");
                failSb.Append("Do not report this dispatch as a clean no-op — say the capture failed and surface the worktree path.");
                return failSb.ToString();
            }

            if (result.DispatchFilesChanged > 0)
            {
                var sb = new System.Text.StringBuilder(text);
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append("---\n");
                sb.Append($"PENDING REVIEW (task_id: {taskId})\n");
                sb.Append($"  diff: {result.DispatchDiffStat ?? "(none)"}\n");
                sb.Append($"  files_changed: {result.DispatchFilesChanged}\n");
                sb.Append($"  self_assessment: {result.SelfAssessment}\n");
                if (!string.IsNullOrWhiteSpace(result.SelfAssessmentNotes))
                    sb.Append($"  notes: {result.SelfAssessmentNotes}\n");
                sb.Append($"  worktree: {result.DispatchWorktreePath}\n");
                sb.Append("Decide: accept_dispatch(\"" + taskId + "\") or reject_dispatch(\"" + taskId + "\", \"<reason>\").");
                return sb.ToString();
            }

            var emptySb = new System.Text.StringBuilder(text);
            if (emptySb.Length > 0) emptySb.AppendLine().AppendLine();
            emptySb.Append("---\n");
            emptySb.Append($"DISPATCH COMPLETED — NO CHANGES (task_id: {taskId})\n");
            emptySb.Append($"  self_assessment: {result.SelfAssessment}\n");
            if (!string.IsNullOrWhiteSpace(result.SelfAssessmentNotes))
                emptySb.Append($"  notes: {result.SelfAssessmentNotes}\n");
            emptySb.Append("Do NOT call accept_dispatch — the worktree captured no diff and the registry is empty for this task_id. ");
            emptySb.Append("If the member reported writing files, those writes either landed in scratch space (e.g. /tmp) or — if the workspace state changed unexpectedly — indicate a bug in path isolation worth flagging. ");
            emptySb.Append("Just relay the member's result to the user and move on.");
            return emptySb.ToString();
        }

        // Resume a previously-completed member's run with a new user
        // message. Pulls the cached message history, hands it back to
        // AgentLoop.ContinueAsync, then updates the cache with the new
        // state so further continuations work too.
        async Task<string> ContinueMember(string taskId, string newMessage, CancellationToken mct)
        {
            (string MemberName, List<ChatMessage> Messages) prior;
            lock (memberStateCache)
            {
                if (!memberStateCache.TryGetValue(taskId, out var cached))
                    throw new InvalidOperationException($"task_id {taskId} has no cached state — was it ever run?");
                prior = cached;
            }
            if (!members.TryGetValue(prior.MemberName, out var m))
                throw new InvalidOperationException($"member {prior.MemberName} no longer in team profile");

            // Same client / capability setup as RunMemberFull. Could
            // be DRY'd up, but keeping inline so the ContinueMember
            // path is readable end-to-end.
            var contTools = Builtins.All();
            foreach (var (k, v) in extraTools) contTools[k] = v;
            var filtered = m.Tools.Count > 0
                ? m.Tools.Where(contTools.ContainsKey).ToDictionary(k => k, k => contTools[k])
                : contTools;
            // Plugin / MCP tools are additive over per-role built-in
            // whitelists (same rationale as RunMemberFull path).
            if (m.Tools.Count > 0)
            {
                foreach (var (k, v) in extraTools)
                    filtered[k] = v;
            }
            var schemas = m.Tools.Count > 0
                ? LoadSchemas(m.Tools, extraSchemas)
                : LoadSchemas(profile.Tools, extraSchemas);
            IChatClient memberClient;
            string memberModel;
            lock (memberClientCache)
            {
                if (memberClientCache.TryGetValue(prior.MemberName, out var cached))
                {
                    memberClient = cached;
                    memberModel = !string.IsNullOrEmpty(m.Llm?.Model) ? m.Llm.Model : model;
                }
                else
                {
                    var resolved = ResolveClient(profile.Llm, client, model, m.Llm);
                    memberClient = resolved.Client;
                    memberModel = resolved.Model;
                    memberClientCache[prior.MemberName] = memberClient;
                }
            }
            // Match RunMemberFull: chat-mode members get report_progress
            // so a continuation can stream updates back too. Without
            // this, continuations had 3 tools and the model literally
            // remarked "report_progress is not available."
            if (team.AutoInjectAsyncResults)
            {
                filtered["report_progress"] = (a, _, _, _) =>
                {
                    var msg = a.TryGetValue("message", out var mv) ? mv?.ToString() ?? "" : "";
                    if (mv is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
                        msg = je.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(msg))
                        return Task.FromResult("Error: message required");
                    board.QueueProgress(taskId, msg);
                    return Task.FromResult("Progress reported to leader. Continue your task — leader will relay this to the user on its next turn.");
                };
                schemas.Add(System.Text.Json.JsonDocument.Parse(
                    """{"type":"function","function":{"name":"report_progress","description":"Stream a progress update back to the leader mid-task. Use this to surface findings, partial results, or status as you discover them. The leader sees the message at its next turn and can relay it to the user. Does NOT end your task — keep working until you have a final answer or hit your iteration limit.","parameters":{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}}}""")
                    .RootElement.Clone());
            }

            var memberLlm = new LlmSettings(memberClient, memberModel,
                m.Llm?.Temperature ?? profile.Llm.Temperature ?? 1.0,
                m.Llm?.TopP ?? profile.Llm.TopP,
                m.Llm?.MaxOutputTokens ?? profile.Llm.MaxOutputTokens,
                m.Llm?.PresencePenalty ?? profile.Llm.PresencePenalty,
                m.Llm?.FrequencyPenalty ?? profile.Llm.FrequencyPenalty);
            var memberCaps = new AgentCapabilities(filtered, schemas, MiddlewareResolver.ResolveOrDefault(m.Middleware, extraMiddlewares, memberLlm, m.Compaction ?? profile.Compaction));

            // Tag continuation events with the original task_id so the
            // existing dispatch card receives them — the user sees the
            // continuation as an extension of the same thread.
            void TaggedEmit(Event e)
            {
                var data = new Dictionary<string, object?>(e.Data)
                {
                    ["thread_id"] = taskId,
                    ["member"] = prior.MemberName,
                };
                onEvent?.Invoke(new Event(e.Type, data));
            }

            // Re-register an injection channel for this continuation so
            // the leader can inject_into_task to redirect it. The
            // original channel was unregistered when the first run
            // ended; without this, mid-continuation injection failed
            // with "no live injection channel".
            var injectChan = System.Threading.Channels.Channel.CreateBounded<string>(32);
            board.RegisterInjector(taskId, injectChan.Writer);

            var memberEnv = new AgentEnvironment(sandbox, sessionId,
                m.MaxIterations > 0 ? m.MaxIterations : 100, TaggedEmit,
                InjectedMessages: injectChan.Reader);

            // Synthetic dispatch_start so the UI shows the resumption
            // (existing card adds a new event entry; if the card was
            // closed, this dispatch_start opens it again).
            onEvent?.Invoke(new Event("dispatch_start", new()
            {
                ["thread_id"] = "main",
                ["member"] = prior.MemberName,
                ["task"] = $"(continuation) {newMessage}",
                ["task_id"] = taskId,
                ["continuation"] = true,
            }));

            var priorState = new AgentState
            {
                Messages = prior.Messages,
                MaxIterations = memberEnv.MaxIterations,
            };
            AgentResult result;
            try
            {
                result = await AgentLoop.ContinueAsync(memberLlm, memberCaps, memberEnv, priorState, newMessage, mct);
            }
            catch (OperationCanceledException)
            {
                // COULD-NOT-MEASURE IS NOT MEASURED-ZERO.
                //
                // ContinueAsync threw before returning a result, so this scope
                // never saw an iteration count or a token total. The previous
                // version published 0/0/0 here, which is not a missing value —
                // it is a CONFIDENT WRONG ONE. A continuation cancelled after
                // real work reported `iterations=0, input_tokens=0` and any
                // consumer summing these got a silent undercount that looks
                // exactly like a cheap run.
                //
                // null says "not measured"; both readers are null-tolerant
                // (EscalationLedger.Num and AssertionEngine.FieldInt both
                // return nullable and handle it), and the ledger's token
                // totals come from `llm_response`, not from here.
                //
                // `counters_measured` is the POSITIVE conjunct: a reader must
                // be able to tell an absent value from an unmeasured one
                // without inferring it from nulls it might not check.
                onEvent?.Invoke(new Event("dispatch_end", new()
                {
                    ["thread_id"] = "main",
                    ["member"] = prior.MemberName,
                    ["task_id"] = taskId,
                    ["iterations"] = null,
                    ["stop_reason"] = "cancelled",
                    ["input_tokens"] = null,
                    ["output_tokens"] = null,
                    ["counters_measured"] = false,
                }));
                throw;
            }
            finally
            {
                board.UnregisterInjector(taskId);
            }

            lock (memberStateCache)
            {
                memberStateCache[taskId] = (prior.MemberName, new List<ChatMessage>(result.Messages));
            }

            onEvent?.Invoke(new Event("dispatch_end", new()
            {
                ["thread_id"] = "main",
                ["member"] = prior.MemberName,
                ["task_id"] = taskId,
                ["iterations"] = result.Iterations,
                ["stop_reason"] = result.StopReason,
                ["input_tokens"] = result.InputTokens,
                ["output_tokens"] = result.OutputTokens,
                // Paired with the `false` on the cancel path above. Emitted on
                // BOTH paths on purpose: a key that only ever appears when the
                // news is bad is indistinguishable, to a reader, from a key the
                // emitter forgot — and that reader then cannot fail closed.
                ["counters_measured"] = true,
            }));

            for (int i = result.Messages.Count - 1; i >= 0; i--)
            {
                if (result.Messages[i].IsRole(ChatRole.Assistant) && !string.IsNullOrEmpty(result.Messages[i].GetText()))
                    return result.Messages[i].GetText();
            }
            return "";
        }

        var leaderTools = Builtins.All();
        foreach (var (k, v) in extraTools) leaderTools[k] = v;
        foreach (var (k, v) in LeaderTools.Create(board, RunMember, ContinueMember))
            leaderTools[k] = v;

        // team.leader_terminal_readonly: refuse file-writing shell commands so
        // the leader physically cannot bypass assign_task / accept_dispatch.
        // Applied BEFORE the plan-mode wrap so both restrictions compose.
        if (team.LeaderTerminalReadonly)
            Vett.Tools.LeaderWriteGuard.Apply(leaderTools);

        // PLAN MODE wrap is applied AFTER LeaderEmit is defined (further
        // down) — the gate needs an emit callback so it can route its
        // approval request through the leader's thread-tagged event
        // pipeline.

        var leaderSchemas = LoadSchemas(profile.Tools, extraSchemas);
        leaderSchemas.AddRange(LeaderTools.Schemas);

        // Dispatch review tools — only registered when team.dispatch_worktree
        // is enabled. Without the toggle these don't appear in the tool list
        // and the leader keeps its existing pre-feature behavior.
        //
        // In chat mode (auto-inject), list_pending_dispatches is REMOVED
        // because it's a polling temptation — the leader can call it
        // before the dispatch finishes, see "no pending dispatches", and
        // wrongly conclude the work failed. Auto-delivery via
        // <background_task_results> is the only correct signal in chat
        // mode. Bench mode keeps the tool since its leader explicitly
        // polls between iterations.
        if (dispatchManager is not null)
        {
            foreach (var (k, v) in DispatchTools.Create(pendingDispatches, pendingDispatchesLock, promotionGate, dispatchManager, team.DispatchRetention, board))
            {
                if (team.AutoInjectAsyncResults && k == "list_pending_dispatches") continue;
                leaderTools[k] = v;
            }
            foreach (var schema in DispatchTools.Schemas)
            {
                if (team.AutoInjectAsyncResults
                    && schema.TryGetProperty("function", out var fn)
                    && fn.TryGetProperty("name", out var n)
                    && n.GetString() == "list_pending_dispatches")
                {
                    continue;
                }
                leaderSchemas.Add(schema);
            }

            // BEST-OF-N: needs worktrees (each attempt must be isolated), so it
            // is registered inside the dispatchManager branch. The judge and the
            // apply/discard decisions are enforced HERE, in the harness — the
            // leader cannot skip the judge or apply two winners.
            if (team.BestOf is not null)
            {
                // ⛔ BEST-OF IS DELIBERATELY EXEMPT FROM THE WIDTH CEILING, and
                // that exemption is a decision, not an oversight.
                //
                // `TaskBoard.MaxConcurrentDispatches` exists to stop a LEADER
                // from choosing to fan out wider than anyone intended. Best-of's
                // width is not the leader's choice: `n` is written by the profile
                // author, clamped to 2..8, and enforced here in the harness.
                // Capping it would silently run best-of-2 while still calling it
                // best-of-5 — degrading the semantics of the feature (you are now
                // picking the best of a smaller pool) with nothing reported.
                //
                // Two author-declared numbers that disagree is a config problem,
                // so it is surfaced where the author can see it: `validate` warns
                // when `best_of.n` exceeds the ceiling. See ValidateCommand.
                async Task<(string, string)> RunOneCandidate(string mem, string task, CancellationToken bct)
                {
                    var t = board.Create(mem, task);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(bct);
                    board.RegisterCts(t.Id, cts);
                    try
                    {
                        var report = await RunMember(t.Id, mem, task, cts.Token);
                        board.Complete(t.Id, report);
                        return (t.Id, report);
                    }
                    catch (Exception ex) { board.Fail(t.Id, ex.Message); return (t.Id, $"FAILED: {ex.Message}"); }
                    finally { board.UnregisterCts(t.Id); }
                }

                async Task<string> RunJudge(string judgeName, string prompt, CancellationToken jct)
                {
                    var t = board.Create(judgeName, prompt);
                    var r = await RunMember(t.Id, judgeName, prompt, jct);
                    board.Complete(t.Id, r);
                    // The judge is read-only; if it somehow produced a diff, drop it —
                    // a judge must never sneak code into the tree.
                    lock (pendingDispatchesLock) { pendingDispatches.Remove(t.Id); }
                    return r;
                }

                foreach (var (k, v) in BestOfTools.Create(
                             pendingDispatches, pendingDispatchesLock, promotionGate, dispatchManager,
                             team.DispatchRetention, RunOneCandidate, RunJudge,
                             team.BestOf.Judge, team.BestOf.N))
                    leaderTools[k] = v;

                if (team.BestOf.Auto)
                {
                    // TOGGLE, NOT A SUGGESTION. Route assign_task(<candidate>, ...)
                    // straight through best-of-N. Measured: a Pro leader with
                    // assign_best_of advertised AND instructed in its prompt called
                    // it zero times. Offered capabilities get ignored; wired-in ones
                    // get used. The tool schema is NOT advertised in auto mode — the
                    // leader keeps dispatching exactly as it always has and the
                    // N-attempt fan-out happens underneath it.
                    var plainAssign = leaderTools["assign_task"];
                    var bestOf = leaderTools["assign_best_of"];
                    var candidate = team.BestOf.Member;
                    leaderTools["assign_task"] = (a, sbx, sid, act) =>
                    {
                        var mem = a.TryGetValue("member", out var mv) ? mv?.ToString() ?? "" : "";
                        if (mv is JsonElement mje && mje.ValueKind == JsonValueKind.String)
                            mem = mje.GetString() ?? "";
                        return string.Equals(mem, candidate, StringComparison.OrdinalIgnoreCase)
                            ? bestOf(a, sbx, sid, act)
                            : plainAssign(a, sbx, sid, act);
                    };
                    leaderTools.Remove("assign_best_of");
                }
                else
                {
                    leaderSchemas.AddRange(BestOfTools.Schemas);
                }
            }
        }

        // Chat mode: strip the blocking/polling tools so the leader
        // CAN'T freeze the chat. Auto-inject + report_progress cover
        // every legitimate "did the task finish?" use case. Keeping
        // them registered would let the LLM fall back to polling
        // habits learned from generic system prompts.
        if (team.AutoInjectAsyncResults)
        {
            var stripped = new HashSet<string> { "check_task", "check_tasks", "wait_task" };
            foreach (var name in stripped) leaderTools.Remove(name);
            leaderSchemas.RemoveAll(s =>
            {
                try
                {
                    var n = s.GetProperty("function").GetProperty("name").GetString();
                    return n is not null && stripped.Contains(n);
                }
                catch { return false; }
            });
        }

        var leaderPrompt = ApplyInstructions(!string.IsNullOrEmpty(team.Leader.SystemPrompt)
            ? team.Leader.SystemPrompt
            : profile.SystemPrompt);

        var (leaderClient, leaderModel) = ResolveClient(profile.Llm, client, model, team.Leader.Llm);
        var leaderLlm = new LlmSettings(leaderClient, leaderModel,
            team.Leader.Llm?.Temperature ?? profile.Llm.Temperature ?? 1.0,
            team.Leader.Llm?.TopP ?? profile.Llm.TopP,
            team.Leader.Llm?.MaxOutputTokens ?? profile.Llm.MaxOutputTokens,
            team.Leader.Llm?.PresencePenalty ?? profile.Llm.PresencePenalty,
            team.Leader.Llm?.FrequencyPenalty ?? profile.Llm.FrequencyPenalty);
        var leaderCaps = new AgentCapabilities(leaderTools, leaderSchemas,
            MiddlewareResolver.ResolveOrDefault(LeaderMiddleware(team, profile), extraMiddlewares, leaderLlm,
                LeaderCompaction(team, profile)));

        // Leader events default to thread_id="main" so the extension's
        // routing never has a "missing thread" branch.
        void LeaderEmit(Event e)
        {
            if (e.Data.ContainsKey("thread_id")) { onEvent?.Invoke(e); return; }
            var data = new Dictionary<string, object?>(e.Data) { ["thread_id"] = "main" };
            onEvent?.Invoke(new Event(e.Type, data));
        }

        // PLAN MODE: wrap leader's write-capable tools with the
        // per-call unlock gate. Has to happen AFTER LeaderEmit is
        // defined since the gate emits its approval-request events
        // through that pipeline (so the request gets thread-tagged
        // like other leader events). The gate's stdin-side
        // (PostDecision) is hooked from RunStdio via the shared
        // service — see _planUnlockService below.
        if (planMode && _planUnlockService is not null)
        {
            var planGate = new Vett.Tools.PlanModeUnlockGate(
                _planUnlockService,
                (type, data) => LeaderEmit(new Event(type, data)));
            Vett.Cli.Helpers.ApplyPlanModeToolRestrictions(leaderTools, planGate);
        }
        // Pre-iteration hook: drain (a) completed async tasks AND (b)
        // mid-flight progress reports from the board, then prepend them
        // to the leader's outbound messages as a synthesized user turn.
        // The leader sees them as if a third party reported in. No-op
        // when AutoInjectCompletions is off (bench mode).
        // Pending-review nag state (failure mode #29, resilience row 29):
        // when a new user message lands while a dispatch still awaits
        // accept/reject, DeepSeek-class leaders answer the user, never
        // resolve the review, and then falsely report the work as done.
        // The nag injects an unmissable reminder alongside the fresh
        // turn. Capped per task so it can never become loop fuel.
        var nagCounts = new Dictionary<string, int>();
        var lastNagMessageCount = -1;

        Task DrainCompletionsHook(AgentState state, CancellationToken hookCt)
        {
            var done = board.DrainPendingDeliveries();
            var progress = board.DrainPendingProgress();
            if (done.Count > 0 || progress.Count > 0)
            {
                var sections = new List<string>();
                foreach (var (taskId, message, member) in progress)
                {
                    sections.Add($"[Progress {taskId} ({member})] {message}");
                }
                foreach (var t in done)
                {
                    if (t.Status == TaskStatus.Failed)
                        sections.Add($"[Background task {t.Id} ({t.Member}) FAILED] {t.Description}\n  Error: {t.Error}");
                    else
                        sections.Add($"[Background task {t.Id} ({t.Member}) completed] {t.Description}\n  Result: {(t.Result.Length > 1500 ? t.Result[..1500] + "..." : t.Result)}");
                }
                var injected =
                    "<background_task_results>\n" +
                    string.Join("\n\n", sections) + "\n" +
                    "</background_task_results>";
                state.Messages.Add(Chat.User(injected));
                // Surface both flavors so the chat UI can render them and
                // resumed sessions replay correctly. Tagged thread_id=main
                // so they show up in the leader's transcript.
                if (done.Count > 0)
                {
                    LeaderEmit(new Event("background_task_completed", new()
                    {
                        ["task_ids"] = done.Select(t => t.Id).ToList(),
                        ["count"] = done.Count,
                    }));
                }
                if (progress.Count > 0)
                {
                    LeaderEmit(new Event("background_task_progress", new()
                    {
                        ["task_ids"] = progress.Select(p => p.TaskId).Distinct().ToList(),
                        ["count"] = progress.Count,
                    }));
                }
            }

            // Pending-review nag: fires only when (a) reviews are pending,
            // (b) the newest message is a user turn (fresh input the model
            // is about to prioritize), (c) that turn isn't itself a nag,
            // and (d) each task has been nagged fewer than 3 times.
            List<string> pendingIds;
            lock (pendingDispatchesLock)
                pendingIds = pendingDispatches.Keys.ToList();
            if (pendingIds.Count > 0 && state.Messages.Count > 0
                && state.Messages.Count != lastNagMessageCount)
            {
                var last = state.Messages[^1];
                var lastText = string.Concat(last.Contents.OfType<TextContent>().Select(t => t.Text));
                bool lastIsUser = last.Role == ChatRole.User;
                bool alreadyNag = lastText.Contains("<pending_review_reminder>");
                var eligible = pendingIds.Where(id => nagCounts.GetValueOrDefault(id) < 3).ToList();
                if (lastIsUser && !alreadyNag && eligible.Count > 0)
                {
                    foreach (var id in eligible)
                        nagCounts[id] = nagCounts.GetValueOrDefault(id) + 1;
                    state.Messages.Add(Chat.User(
                        "<pending_review_reminder>Dispatch(es) awaiting your decision: "
                        + string.Join(", ", eligible)
                        + ". Their work is NOT merged until you accept_dispatch or "
                        + "reject_dispatch. Resolve these with your VERY NEXT tool "
                        + "call, before answering anything else or declaring done."
                        + "</pending_review_reminder>"));
                    lastNagMessageCount = state.Messages.Count;
                    LeaderEmit(new Event("pending_review_nag", new()
                    {
                        ["task_ids"] = eligible,
                        ["count"] = eligible.Count,
                    }));
                }
            }
            return Task.CompletedTask;
        }

        // Wake channel: TaskBoard fires OnPending whenever a completion
        // or progress report is queued. We push a "true" into this
        // bounded channel; the leader's loop is wired to wake on
        // either a user message OR a wake signal. Without this, the
        // leader sits parked at user_input_needed and never sees
        // progress until the user types.
        var wakeChan = team.AutoInjectAsyncResults
            ? System.Threading.Channels.Channel.CreateBounded<bool>(
                new System.Threading.Channels.BoundedChannelOptions(8) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest })
            : null;
        if (wakeChan is not null)
        {
            board.OnPending += () => wakeChan.Writer.TryWrite(true);
        }

        var leaderEnv = new AgentEnvironment(sandbox, sessionId,
            team.Leader.MaxIterations > 0 ? team.Leader.MaxIterations : 50, LeaderEmit,
            PreIteration: team.AutoInjectAsyncResults ? DrainCompletionsHook : null,
            WakeSignal: wakeChan?.Reader,
            // Thread the leader's permission gate through. Without this,
            // the leader's tool calls bypass gating entirely even when
            // the profile carries a `permissions:` block (gotcha 77).
            // Members deliberately don't get the gate — they spawn in
            // parallel and crosswiring permission cards across threads
            // is messier than letting members run unprompted. v2:
            // per-member gate that funnels through a single UI surface.
            PermissionGate: leaderPermissionGate,
            Profile: profile,
            // Only `vett chat` sets this. A leader driven by the bench harness
            // has a userInput channel too, but nobody types into it, so parking
            // on the runaway cap would hang the run forever instead of
            // terminating with the stop_reason the harness reconciles on.
            HumanAtTheKeyboard: humanAtTheKeyboard,
            // Members are unaffected: they run through AgentLoop.RunAsync with
            // no channel at all, so their finish already ends only their own
            // dispatch. This is the LEADER, the seat the person is reading.
            AgentStopEndsSession: agentStopEndsSession);

        return await AgentLoop.RunInteractiveAsync(
            leaderLlm, leaderCaps, leaderEnv,
            leaderPrompt, userInput, onAssistantText, onWaitingForInput,
            seedHistory, turnInterrupt, compactRequest, ct);
    }

    private static async Task<AgentResult> RunAsync(
        Profile profile, IChatClient client, string model,
        ISandbox sandbox, string sessionId,
        string userMessage, Action<Event>? onEvent, CancellationToken ct,
        Dictionary<string, ToolFn> extraTools,
        Dictionary<string, JsonElement> extraSchemas,
        Dictionary<string, MiddlewareFn> extraMiddlewares,
        int depth)
    {
        EnforceDispatchDepth(depth);

        var team = profile.Team ?? throw new InvalidOperationException("No team config");

        // ⚠ THE ONE-SHOT PATH NEEDS THE CEILING TOO. This board was built bare
        // while RunInteractiveAsync's carried the width cap, so a profile's
        // `max_concurrent_dispatches` would have been honoured on one entry
        // point and silently ignored on the other — the exact "the setting the
        // author wrote never reached the code" class the cap itself guards.
        //
        // ⚠ LATENT, NOT LIVE, as of 2026-08-27: nothing in this repo calls
        // TeamCoordinator.RunAsync — the bench harness (Harness.cs:947) and chat
        // (ChatCommand.cs:1009) both go through RunInteractiveAsync. It is a
        // public entry point, so it is wired anyway rather than left as a trap.
        var board = new TaskBoard { MaxConcurrentDispatches = RequiredWidth(team) };
        var members = team.Members.ToDictionary(m => m.Name);

        // Cache per-member IChatClient so we don't create-and-leak a new
        // OpenAIClient (with its own HttpClient) on every assign_task call.
        // The cache is held for the duration of the team run; the bounded
        // number of distinct clients (one per member with own LLM config)
        // outlives the leader for any pending assign_async work.
        var memberClientCache = new Dictionary<string, IChatClient>();

        // One-shot path: member-state cache exists for symmetry but
        // continue_task is not registered (LeaderTools.Create called
        // with null), so it's effectively unused here.
        async Task<AgentResult> RunMemberFull(string taskId, string name, string task, CancellationToken mct)
        {
            if (!members.TryGetValue(name, out var m))
                throw new InvalidOperationException($"Unknown member \"{name}\"");

            // Defense-in-depth: strip the parent workspace root from the task
            // string before passing it to the member. Even with the leader
            // system prompt forbidding absolute paths in task descriptions,
            // a model that ignores the rule used to leak `c:\Users\dev\TEST\`
            // into the implementer's user message — the implementer would
            // then dutifully use that absolute path instead of its own
            // {working_dir}, write outside the worktree, and the dispatch
            // would capture no diff. Stripping all common spellings of the
            // workspace root here prevents that regardless of leader behavior.
            task = StripWorkspaceRoot(task, sandbox.Cwd);

            // Builtins + plugin tools passed in by the caller. Without merging
            // extras, members in a team profile can't use any plugin/YAML tools.
            var tools = Builtins.All();
            foreach (var (k, v) in extraTools) tools[k] = v;

            var filtered = m.Tools.Count > 0
                ? m.Tools.Where(tools.ContainsKey).ToDictionary(k => k, k => tools[k])
                : tools;
            // Plugin / MCP tools are additive over per-role built-in
            // whitelists (same rationale as team-mode RunMemberFull).
            if (m.Tools.Count > 0)
            {
                foreach (var (k, v) in extraTools)
                    filtered[k] = v;
            }

            var schemas = m.Tools.Count > 0
                ? LoadSchemas(m.Tools, extraSchemas)
                : LoadSchemas(profile.Tools, extraSchemas);

            var prompt = !string.IsNullOrEmpty(m.SystemPrompt)
                ? m.SystemPrompt
                : "Complete the task.";

            IChatClient memberClient;
            string memberModel;
            lock (memberClientCache)
            {
                if (memberClientCache.TryGetValue(name, out var cached))
                {
                    memberClient = cached;
                    memberModel = !string.IsNullOrEmpty(m.Llm?.Model) ? m.Llm.Model : model;
                }
                else
                {
                    var resolved = ResolveClient(profile.Llm, client, model, m.Llm);
                    memberClient = resolved.Client;
                    memberModel = resolved.Model;
                    memberClientCache[name] = memberClient;
                }
            }

            var memberLlm = new LlmSettings(memberClient, memberModel,
                m.Llm?.Temperature ?? profile.Llm.Temperature ?? 1.0,
                m.Llm?.TopP ?? profile.Llm.TopP,
                m.Llm?.MaxOutputTokens ?? profile.Llm.MaxOutputTokens,
                m.Llm?.PresencePenalty ?? profile.Llm.PresencePenalty,
                m.Llm?.FrequencyPenalty ?? profile.Llm.FrequencyPenalty);
            var memberCaps = new AgentCapabilities(filtered, schemas, MiddlewareResolver.ResolveOrDefault(m.Middleware, extraMiddlewares, memberLlm, m.Compaction ?? profile.Compaction));
            var memberEnv = new AgentEnvironment(sandbox, sessionId,
                m.MaxIterations > 0 ? m.MaxIterations : 100, onEvent);

            // TIER 3: a member with its own `team:` runs as a SUB-TEAM (one-shot path).
            if (m.Team is not null)
                return await RunNestedTeamAsync(profile, m, memberLlm.Client, memberLlm.Model,
                    sandbox, sessionId, taskId, task, onEvent, mct,
                    extraTools, extraSchemas, extraMiddlewares, cwd: "");

            return await AgentLoop.RunAsync(memberLlm, memberCaps, memberEnv, prompt, task, mct);
        }

        async Task<string> RunMember(string taskId, string name, string task, CancellationToken mct)
        {
            var result = await RunMemberFull(taskId, name, task, mct);
            // Was an inline copy of ExtractFinalAssistantText’s loop; routed
            // through the helper so chat-mode and team-mode cannot disagree
            // about which message is "final".
            var text = ExtractFinalAssistantText(result);

            // Same fail-open as the team-mode wrapper above, same fix. Chat-mode
            // dispatches carry no worktree metadata, so "nothing to show" is
            // decided on the text alone.
            if (!result.StoppedCleanly)
            {
                if (string.IsNullOrWhiteSpace(text))
                    throw new DispatchFailedException(taskId, name, result.StopReason, result.Iterations);
                return DispatchNotCleanBanner(taskId, result) + text;
            }
            return text;
        }

        // Leader tools = builtins + plugin tools + delegation tools.
        var leaderTools = Builtins.All();
        foreach (var (k, v) in extraTools) leaderTools[k] = v;
        foreach (var (k, v) in LeaderTools.Create(board, RunMember))
            leaderTools[k] = v;

        // team.leader_terminal_readonly: refuse file-writing shell commands so
        // the leader physically cannot bypass assign_task / accept_dispatch.
        if (team.LeaderTerminalReadonly)
            Vett.Tools.LeaderWriteGuard.Apply(leaderTools);

        var leaderSchemas = LoadSchemas(profile.Tools, extraSchemas);
        leaderSchemas.AddRange(LeaderTools.Schemas);

        // Sequential mode (no leader, receives_from chains).
        var sequential = team.Members.Where(m => m.ReceivesFrom is { Count: > 0 }).ToList();
        if (sequential.Count > 0 && string.IsNullOrEmpty(team.Leader.Name))
        {
            var outputs = new Dictionary<string, string>();
            var totalIterations = 0;
            var unmeasuredMembers = 0;
            var totalInput = 0;
            var totalOutput = 0;
            AgentResult? last = null;
            foreach (var m in team.Members)
            {
                var context = m.ReceivesFrom?.Where(outputs.ContainsKey)
                    .Select(n => $"=== Output from {n} ===\n{outputs[n]}") ?? [];
                var msg = string.Join("\n\n", context) + "\n\n" + userMessage;
                // Sequential mode synthesizes a task_id since there's no
                // TaskBoard entry — callers don't expect to continue
                // these, but the per-task plumbing requires a non-empty
                // id.
                last = await RunMemberFull($"seq-{m.Name}", m.Name, msg, ct);
                // Skip members whose counters are COULD-NOT-MEASURE. Adding
                // their synthetic zeros would be arithmetically invisible but
                // semantically wrong: the total would read as a complete sum
                // over every member when one of them was never counted.
                if (last.CountersMeasured)
                {
                    totalIterations += last.Iterations;
                    totalInput += last.InputTokens;
                    totalOutput += last.OutputTokens;
                }
                else
                {
                    unmeasuredMembers++;
                }

                // ⛔ THIS WAS A THIRD COPY OF "WHICH MESSAGE IS FINAL".
                //
                // The helper exists precisely so the paths cannot disagree
                // about that ("so chat-mode and team-mode cannot disagree",
                // at the chat-mode call site) — and then this loop sat here
                // deciding it a third way, so the submit-summary recovery
                // reached two of the three paths and not this one.
                //
                // It matters more here than anywhere else: `outputs` is not a
                // report anyone reads, it is the INPUT CONTEXT handed to the
                // next member in the chain (see the ReceivesFrom join above).
                // A member that answered only by calling `finish` produced ""
                // here, so the downstream member received a section header with
                // nothing under it and carried on — the fail-open's harm
                // again, one layer further out.
                //
                // ⚠ NO SHIPPING PROFILE REACHES THIS. Swept 2026-08-28,
                // re-checked with controls: `receives_from` appears in
                // 0 of the 15 files that DEFINE A TEAM (12 under profiles/,
                // 3 under defaults/profiles/), across all four spellings the
                // binder could accept (receives_from / receivesFrom /
                // ReceivesFrom / receives-from). No test SOURCE names it
                // either; the only tests/ hits are bin/Vett.dll, i.e. the
                // product assembly, not a test.
                //
                // ⛔ DENOMINATOR CORRECTED. The first sweep quoted four dirs
                // -- profiles/, defaults/, bench-profiles/, suites/ (77 yaml
                // files). TWO OF THOSE PASS VACUOUSLY: bench-profiles/ (4) and
                // suites/ (40) hold dataset and instance manifests and define
                // no team under ANY key, so the sweep could not have found a
                // hit there however broken it was. The live denominator is 15,
                // not 77. Same verdict, honest basis.
                //
                // This change is a de-duplication that inherits already-tested
                // behaviour (N1/N4 in the submit-fallback mutation table); it
                // is NOT evidence about a path anyone runs today.
                //
                // ⚠ KNOWN GAP, DELIBERATELY NOT PATCHED HERE: this loop calls
                // RunMemberFull directly, so it never passes through the
                // clean-stop wrapper that RunMember applies. A member that
                // exhausts max_iterations contributes its partial text with no
                // banner, and the aggregate still returns `sequential_complete`
                // -- a reason SIX TESTS assert is CLEAN
                // (HarnessStopReasonTests / DispatchFailOpenTests, all via
                // [InlineData] string membership) while NOT ONE of them
                // executes the path that emits it. The string is pinned as
                // clean; the behaviour behind it is unpinned. That is the gap
                // — which is in CleanStopReasons. Adding a banner here would be
                // new, UNTESTED behaviour on unreachable code; that is worse
                // than leaving it visible. Reported rather than silently
                // patched.
                outputs[m.Name] = ExtractFinalAssistantText(last);
            }
            return new AgentResult
            {
                Messages = last?.Messages ?? [],
                StopReason = "sequential_complete",
                Iterations = totalIterations,
                InputTokens = totalInput,
                OutputTokens = totalOutput,
                // PUBLISH THE DISCARD COUNT. If any member's counters were
                // unmeasurable, these totals are a sum over a SUBSET, and a
                // consumer that cannot tell that would read a partial sum as a
                // complete one. Marking the aggregate unmeasured is the honest
                // summary: the number is still there to look at, but nothing
                // downstream may treat it as the run's full cost.
                CountersMeasured = unmeasuredMembers == 0,
            };
        }

        var leaderPrompt = !string.IsNullOrEmpty(team.Leader.SystemPrompt)
            ? team.Leader.SystemPrompt
            : profile.SystemPrompt;

        var (leaderClient, leaderModel) = ResolveClient(profile.Llm, client, model, team.Leader.Llm);

        var leaderLlm = new LlmSettings(leaderClient, leaderModel,
            team.Leader.Llm?.Temperature ?? profile.Llm.Temperature ?? 1.0,
            team.Leader.Llm?.TopP ?? profile.Llm.TopP,
            team.Leader.Llm?.MaxOutputTokens ?? profile.Llm.MaxOutputTokens,
            team.Leader.Llm?.PresencePenalty ?? profile.Llm.PresencePenalty,
            team.Leader.Llm?.FrequencyPenalty ?? profile.Llm.FrequencyPenalty);
        var leaderCaps = new AgentCapabilities(leaderTools, leaderSchemas,
            MiddlewareResolver.ResolveOrDefault(LeaderMiddleware(team, profile), extraMiddlewares, leaderLlm,
                LeaderCompaction(team, profile)));
        var leaderEnv = new AgentEnvironment(sandbox, sessionId,
            team.Leader.MaxIterations > 0 ? team.Leader.MaxIterations : 50, onEvent);

        // We don't dispose member-override clients on exit. assign_async tasks
        // may still be running with these clients after the leader returns;
        // disposing would break them. The leak is bounded (one client per
        // unique member-with-own-LLM per team run).
        return await AgentLoop.RunAsync(leaderLlm, leaderCaps, leaderEnv, leaderPrompt, userMessage, ct);
    }

    /// <summary>
    /// Decide whether a member needs its own IChatClient. Returns the base client
    /// unless the member overrides a client-affecting field (provider, endpoint,
    /// model, api_key_env). Temperature and top_p are per-call options, not client config.
    ///
    /// INTERNAL, not private, since 2026-08-25: the bench harness has to ask the
    /// LEADER its self-assessment question directly (see Harness's
    /// AskSelfAssessmentDirectAsync), and the leader may be bound to a different
    /// model than the profile's base — `ds-team-lead-pro` is exactly that shape.
    /// Re-deriving this rule over there would put two instruments under one name;
    /// asking the base model and labelling the answer as the leader's would be a
    /// measurement attributed to the wrong subject.
    /// </summary>
    internal static (IChatClient Client, string Model) ResolveClient(
        Config.LlmConfig baseConfig, IChatClient baseClient, string baseModel, Config.LlmConfig? memberConfig)
    {
        if (memberConfig is null)
            return (baseClient, baseModel);

        var clientFieldsDiffer =
            // HasExplicitProvider, not IsNullOrEmpty: Provider never reads empty
            // (it defaults to "local"), so the old guard was always true and the
            // whole conjunct collapsed to "base provider != local". Under a
            // cloud base profile that fired for EVERY member — including one
            // that overrode nothing but temperature — building a redundant
            // client, contradicting this method's own doc comment.
            (memberConfig.HasExplicitProvider && memberConfig.Provider != baseConfig.Provider)
            || (!string.IsNullOrEmpty(memberConfig.Endpoint) && memberConfig.Endpoint != baseConfig.Endpoint)
            || (!string.IsNullOrEmpty(memberConfig.Model) && memberConfig.Model != baseModel)
            || (!string.IsNullOrEmpty(memberConfig.ApiKeyEnv) && memberConfig.ApiKeyEnv != baseConfig.ApiKeyEnv)
            // EnableThinking is baked into the IChatClient at construction (a delegating
            // handler injects chat_template_kwargs.enable_thinking). Per-call override
            // isn't possible — a different value requires a new client. Without this
            // check, a member overriding only enable_thinking gets the base client
            // silently and the override is dropped.
            || (memberConfig.EnableThinking is not null && memberConfig.EnableThinking != baseConfig.EnableThinking)
            // Capability / context_tokens are client-affecting for exactly the
            // same reason endpoint and model are: with a capability set, THEY
            // are what chooses the endpoint, through a lease taken at client
            // construction. Omitting them here would hand a member that says
            // `capability: pro` the base flash client and silently drop the
            // override -- the defect this list was written to prevent, on the
            // newest field in it.
            || (!string.IsNullOrEmpty(memberConfig.Capability) && memberConfig.Capability != baseConfig.Capability)
            || (memberConfig.ContextTokens > 0 && memberConfig.ContextTokens != baseConfig.ContextTokens);

        if (!clientFieldsDiffer)
            return (baseClient, baseModel);

        var merged = ChatClientFactory.Merge(baseConfig, memberConfig);
        var memberClient = ChatClientFactory.Create(merged);

        // ⭐ THE SEAT'S ENDPOINT AND THE SEAT'S MODEL NAME ARRIVE BY TWO
        // DIFFERENT ROUTES, AND INHERITING ONE WITHOUT THE OTHER IS WORSE THAN
        // INHERITING NEITHER.
        //
        // This line used to be `memberConfig.Model ?: baseModel`, which is right
        // for every profile that names its endpoint. It is wrong the moment a
        // seat overrides `capability:`. Take a flash base with a `capability: pro`
        // member: the merged config differs, so a NEW client is built and it takes
        // a PRO lease against the pro provider -- but `resolvedModel` fell through
        // to baseModel and the member then sent the FLASH model id to the PRO
        // endpoint. Not an empty string this time; a confident, wrong one, which
        // is the harder failure to read: on a single-model vLLM it is a 400 that
        // looks like a bad request, and on a router it bills the wrong model and
        // returns an answer.
        //
        // The precedence is the same one the six CLI fold sites use, reusing the
        // same two primitives rather than a fourth copy of the rule: an EXPLICIT
        // per-seat `model:` wins; otherwise, if the merged config binds through a
        // capability, the CATALOGUE owns the name and it is read back off the
        // client that just leased it; otherwise the base model is inherited as
        // before. On a non-capability profile both calls are no-ops and this is
        // byte-for-byte the old behaviour.
        //
        // ⚠ REASONED FROM THE CODE, NOT MEASURED. Found by sweeping the
        // ChatClientFactory.Create call sites after C2 of PREREG-2026-08-28, not
        // by a failing run -- no live team run has yet mixed two capabilities.
        // Pinned by CapacityWiringTests; the live proof is still owed.
        var inherited = ChatClientFactory.FoldProfileValue(merged, memberConfig.Model, baseModel);
        var resolvedModel = ChatClientFactory.EffectiveModel(memberClient, inherited);

        return (memberClient, resolvedModel);
    }

    /// <summary>
    /// The leader-facing block for a dispatch whose member loop did NOT end
    /// on a clean stop reason. Prepended to whatever the member did manage to
    /// say, so the leader reads the warning BEFORE the content and cannot
    /// relay a partial answer as a finished one.
    /// </summary>
    internal static string DispatchNotCleanBanner(string taskId, AgentResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("---\n");
        sb.Append($"DISPATCH DID NOT COMPLETE (task_id: {taskId})\n");
        sb.Append($"  stop_reason: {result.StopReason}\n");
        sb.Append($"  iterations: {result.Iterations}\n");
        sb.Append("The member’s loop STOPPED for the reason above instead of finishing. ");
        sb.Append("Anything below is PARTIAL — it is what the member had produced when the loop ended, ");
        sb.Append("not an answer it stood behind. Do NOT relay it as a completed result: re-assign the ");
        sb.Append("task, or report the stop_reason to the user.\n");
        sb.Append("---\n\n");
        return sb.ToString();
    }

    /// <summary>
    /// Pull the last non-empty assistant message text out of an
    /// AgentResult. Used in two places: RunMember's string-returning
    /// wrapper, and the SELF-ASSESSMENT parser. Centralized so both
    /// agree on what counts as the "final" message.
    /// </summary>
    private static string ExtractFinalAssistantText(AgentResult result)
    {
        for (int i = result.Messages.Count - 1; i >= 0; i--)
        {
            if (result.Messages[i].IsRole(ChatRole.Assistant) && !string.IsNullOrEmpty(result.Messages[i].GetText()))
                return result.Messages[i].GetText();
        }

        // ⛔ AN AGENT THAT ANSWERED ONLY BY SUBMITTING STILL ANSWERED.
        //
        // MEASURED 2026-08-28 (tier-2 arm, DispatchFailOpenTests): a sub-leader
        // finished by calling `declare_done` with a real summary and NO prose in
        // the same turn. Its last ASSISTANT message therefore held one
        // FunctionCallContent and no text, the loop above returned "", and the
        // parent leader was handed:
        //
        //     [worker-1 — worker done]
        //
        // That is the fail-open's exact harm — a leader told "done" with nothing
        // to read, left to invent the content — arriving through the CLEAN door,
        // where the stop-reason guard correctly does not fire because the run
        // really did finish on `finish_tool`.
        //
        // It is NOT nested-only: `finish` is a builtin available to any agent
        // (BuiltinTools.cs:178), and the summary an agent passes to it is the
        // most deliberate statement it makes about its own work — more so than
        // whatever prose it last happened to emit. The submit marker is how
        // SubmitDetector recognises that turn (BuiltinMiddleware.cs:13), so it
        // is also how the summary is recovered here; the marker is stripped
        // because it is transport, not content.
        //
        // Deliberately a FALLBACK, not a precedence change. Assistant prose
        // still wins whenever it exists, so no run that reads correctly today
        // reads differently tomorrow — this can only turn an empty answer into
        // a real one. ParseSelfAssessment reads the same string
        // (Coordinator.cs:1206), so a member that submits its SELF-ASSESSMENT
        // block instead of speaking it is no longer scored "unknown".
        for (int i = result.Messages.Count - 1; i >= 0; i--)
        {
            if (!result.Messages[i].IsRole(ChatRole.Tool)) continue;
            var toolText = result.Messages[i].GetText();
            if (!string.IsNullOrEmpty(toolText)
                && toolText.StartsWith(Builtins.SubmitMarker, StringComparison.Ordinal))
                return toolText[Builtins.SubmitMarker.Length..];
        }

        return "";
    }

    /// <summary>
    /// Parse the implementer's structured "SELF-ASSESSMENT:" prefix from
    /// its final message. Format the implementer's prompt asks for:
    ///   SELF-ASSESSMENT: confident | partial | uncertain
    ///   NOTES: &lt;one paragraph, may span lines until blank line or end&gt;
    ///   &lt;rest of summary…&gt;
    /// Returns ("unknown", "") when the prefix is missing — the leader
    /// treats that as a yellow flag rather than rejecting outright,
    /// because the dispatch may legitimately produce no output (e.g.
    /// stalled / cancelled member).
    /// </summary>
    private static (string Assessment, string Notes) ParseSelfAssessment(string finalText)
    {
        if (string.IsNullOrWhiteSpace(finalText))
            return ("unknown", "");

        var lines = finalText.Split('\n');
        string assessment = "unknown";
        var notesLines = new List<string>();
        bool inNotes = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (assessment == "unknown" && trimmed.StartsWith("SELF-ASSESSMENT:", StringComparison.OrdinalIgnoreCase))
            {
                var v = trimmed.Substring("SELF-ASSESSMENT:".Length).Trim().ToLowerInvariant();
                // Accept the three documented buckets; anything else
                // collapses to "unknown" so the leader doesn't have to
                // handle arbitrary strings.
                assessment = v switch
                {
                    "confident" => "confident",
                    "partial" => "partial",
                    "uncertain" => "uncertain",
                    _ => "unknown",
                };
                inNotes = false;
                continue;
            }

            if (trimmed.StartsWith("NOTES:", StringComparison.OrdinalIgnoreCase))
            {
                var first = trimmed.Substring("NOTES:".Length).TrimStart();
                if (!string.IsNullOrEmpty(first))
                    notesLines.Add(first);
                inNotes = true;
                continue;
            }

            if (inNotes)
            {
                // Notes block ends on the first blank line OR a line that
                // looks like a new section header. Otherwise accumulate.
                if (string.IsNullOrWhiteSpace(line) || trimmed.StartsWith("---") || trimmed.StartsWith("##"))
                {
                    inNotes = false;
                    continue;
                }
                notesLines.Add(line);
            }
        }

        // Trim trailing blank lines and collapse to a single string. Cap
        // at 500 chars so a verbose member doesn't blow up event payloads.
        var notes = string.Join(' ', notesLines).Trim();
        if (notes.Length > 500) notes = notes[..500] + "…";

        return (assessment, notes);
    }

    /// <summary>
    /// Remove every reasonable spelling of <paramref name="workspaceRoot"/>
    /// from a member task description. Models occasionally embed the
    /// absolute parent path in dispatch task strings even with a leader
    /// prompt forbidding it; without this strip the member sees the path,
    /// uses it verbatim, and writes outside its worktree. Handles the
    /// common spelling variations: native and inverted separators,
    /// trailing slash or no slash, case-insensitive on Windows.
    /// </summary>
    private static string StripWorkspaceRoot(string task, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(task) || string.IsNullOrEmpty(workspaceRoot)) return task;
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var trimmed = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var native = trimmed.Replace('/', Path.DirectorySeparatorChar);
        var alt = trimmed.Replace('\\', '/');
        // Order long → short so we don't strip a substring that's a prefix
        // of a longer match before we get to the long one.
        var variants = new[]
        {
            native + Path.DirectorySeparatorChar,
            alt + "/",
            native,
            alt,
        }.Distinct().OrderByDescending(s => s.Length).ToArray();
        var result = task;
        foreach (var v in variants)
        {
            int idx;
            while ((idx = result.IndexOf(v, cmp)) >= 0)
                result = result.Remove(idx, v.Length);
        }
        return result;
    }

    private static List<JsonElement> LoadSchemas(List<string> toolNames, Dictionary<string, JsonElement>? extraSchemas = null)
    {
        var schemas = new List<JsonElement>();
        var already = new HashSet<string>();
        foreach (var name in toolNames)
        {
            already.Add(name);
            if (extraSchemas is not null && extraSchemas.TryGetValue(name, out var pluginSchema))
            {
                schemas.Add(pluginSchema);
                continue;
            }
            var path = Path.Combine(AppContext.BaseDirectory, "schemas", $"{name}.json");
            if (File.Exists(path))
                schemas.Add(JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone());
        }
        // Always advertise extra schemas whose names aren't in the profile's
        // built-in tools: list — these are plugin / MCP tools that don't get
        // whitelisted per-role (their namespaced names, e.g.
        // `mcp__analyzer__q7_flask_endpoints`, aren't known at profile
        // authoring time). Without this, MCP tools registered via
        // `mcp_servers:` would be callable but invisible to the LLM.
        if (extraSchemas is not null)
        {
            foreach (var (name, schema) in extraSchemas)
            {
                if (!already.Contains(name))
                    schemas.Add(schema);
            }
        }
        return schemas;
    }
}
