using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// ⭐ DOES A MEMBER ACTUALLY MODIFY FILES, AND DOES PROMOTION ACTUALLY PROMOTE?
///
/// Mark, 2026-08-27: "have you tested they can modfiy files and do thigns I
/// assume. and work in there own work sapce and promote when it make sense
/// there will be times we wnat teams to share the same space as leader maybe
/// other times its own copy of hte work space."
///
/// Before this file the answer was NO, and two independent audits the same day
/// landed on the same hole from different directions:
///
///   • `DispatchTools.Create` had ZERO references anywhere in tests/. The
///     accept / reject / review surface — the entire promotion mechanism — was
///     exercised only as STRINGS ON AN EVENT STREAM, never as an invoked tool.
///   • Every existing dispatch test uses a FakeSandbox whose FileCreateAsync
///     returns "" and writes nothing. So the suite proved a great deal about
///     the BOOKKEEPING around a member's file edits and precisely nothing about
///     whether a byte ever reached a disk.
///
/// ⛔ THAT SECOND POINT IS THE WHOLE REASON THIS FILE USES A REAL `DirectBash`.
/// A dispatch test on a no-op sandbox is a rig that constructs its own green:
/// capture reports 0 files changed, every "no changes" branch is confirmed, and
/// the run looks healthy — because nothing happened. The isolation claim in
/// particular is UNFALSIFIABLE against a sandbox that cannot write: "the file
/// is not in the leader's tree" is trivially true when the file is nowhere.
/// Every assertion below is anchored on a real file on a real filesystem.
///
/// ⭐ THE THREE ARMS ARE EACH OTHER'S CONTROLS. This is the part that makes the
/// file worth more than the sum of its asserts:
///
///   ISOLATED+ACCEPT   member writes → invisible in leader tree → accept →
///                     VISIBLE. Proves promotion MOVES work.
///   ISOLATED+REJECT   identical up to the decision → reject → STILL INVISIBLE.
///                     Proves the "visible" in arm 1 was caused by the ACCEPT
///                     and not by the member quietly writing through to the
///                     parent all along. Without this arm, arm 1 is equally
///                     consistent with isolation being broken.
///   SHARED            dispatch_worktree:false → member writes → VISIBLE with
///                     NO accept ever called, and no worktree created. Proves
///                     the shared-workspace mode Mark asked about is a real
///                     second mode, not a config key nothing reads.
///
/// Arms 1 and 2 are byte-identical scripts up to the leader's single tool
/// choice, so any difference in outcome is attributable to that choice alone.
///
/// ⛔ EVERY ARM ASSERTS ITS PREMISE BEFORE ITS CONCLUSION. A run where the
/// dispatch never happened would satisfy "the file is not in the leader's tree"
/// perfectly. `AssertDispatched` demands a positive conjunct — a real
/// dispatch_start, a real worktree path where one is expected — so a
/// treatment-never-ran outcome reads as VOID, not as PASS.
///
/// Offline: scripted IChatClient, no LLM, no network, no cost. Needs `git` on
/// PATH, same as the other dispatch suites.
/// </summary>
public class WorkspacePromotionEndToEndTests : IDisposable
{
    private const string LeaderGo = "LEADER-GO-PLEASE-DISPATCH";
    private const string MemberSystem = "MEMBER-SYSTEM-PROMPT-MARKER";
    private const string MemberTask = "MEMBER-WORK-MARKER";
    private const string MemberName = "implementer";

    /// <summary>The file the member creates, and the content that proves it was
    /// THIS member's write that travelled — not an empty file, not a stub the
    /// harness could have produced on its own.</summary>
    private const string WorkFile = "member-work.txt";
    private const string Sentinel = "SENTINEL-WRITTEN-BY-THE-MEMBER-7f3a91";

    private readonly string _repo;
    private readonly string _panelId;

    public WorkspacePromotionEndToEndTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-wpe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        _panelId = "wpe-" + Guid.NewGuid().ToString("N")[..8];

        Git(_repo, "init");
        Git(_repo, "config", "user.email", "test@vett.local");
        Git(_repo, "config", "user.name", "vett-test");
        Git(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
    }

    public void Dispose()
    {
        try { Git(_repo, "worktree", "prune"); } catch { /* best effort */ }
        try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId), true); } catch { }
        try { Directory.Delete(_repo, true); } catch { }
    }

    // ---- the three arms ---------------------------------------------------

    /// <summary>
    /// OWN WORKSPACE + PROMOTE. The member's write is invisible in the leader's
    /// tree while the dispatch is pending, and `accept_dispatch` is what makes
    /// it appear — with the member's actual bytes.
    /// </summary>
    [Fact]
    public async Task A_member_works_in_its_OWN_worktree_and_accept_dispatch_PROMOTES_the_work()
    {
        var run = await RunTeamAsync(worktree: true, decision: Decision.Accept);

        AssertDispatched(run, expectWorktree: true);

        // ISOLATION, measured mid-run at dispatch_end — after the member wrote,
        // before the leader decided. Both halves matter: the file must EXIST
        // somewhere (else the member never wrote and the second half is vacuous)
        // and must NOT be in the parent.
        Assert.True(run.SnapshotInWorktree,
            "PREMISE NOT MET: the member's file is not in its worktree either, so this run "
            + "measures a member that never wrote — not isolation." + run.Dump());
        Assert.False(run.SnapshotInParent,
            "ISOLATION BROKEN: the member's write was visible in the LEADER's tree while the "
            + "dispatch was still pending review. `dispatch_worktree: true` is not isolating."
            + run.Dump());

        // The leader was actually offered the decision.
        Assert.Contains(run.ToolResults, r => r.Contains("PENDING REVIEW"));
        Assert.True(run.AcceptResult is not null,
            "PREMISE NOT MET: accept_dispatch was never invoked." + run.Dump());
        // The tool must have SUCCEEDED, not merely returned. `accept_dispatch`
        // reports a failed `git apply` as an ordinary string result, so asserting
        // only that it was called would let a conflicted apply read as green.
        Assert.Contains("Applied 1 file(s)", run.AcceptResult!);

        // PROMOTION.
        var promoted = Path.Combine(_repo, WorkFile);
        Assert.True(File.Exists(promoted),
            "accept_dispatch did not promote the member's work into the leader's workspace." + run.Dump());
        Assert.Contains(Sentinel, File.ReadAllText(promoted));
    }

    /// <summary>
    /// OWN WORKSPACE + REJECT. ⭐ THE CONTROL FOR THE TEST ABOVE. Same script,
    /// same member, same write, one different leader tool call — and the work
    /// must NOT land. Without this, "the file appeared after accept" is equally
    /// well explained by the member having written into the parent all along.
    /// </summary>
    [Fact]
    public async Task A_REJECTED_dispatch_leaves_the_leaders_workspace_UNTOUCHED()
    {
        var run = await RunTeamAsync(worktree: true, decision: Decision.Reject);

        AssertDispatched(run, expectWorktree: true);

        // Identical premise to the accept arm: the member really did write, and
        // really was isolated.
        Assert.True(run.SnapshotInWorktree,
            "PREMISE NOT MET: the member never wrote, so a clean parent proves nothing." + run.Dump());
        Assert.False(run.SnapshotInParent, "ISOLATION BROKEN before the decision." + run.Dump());

        Assert.True(run.RejectResult is not null,
            "PREMISE NOT MET: reject_dispatch was never invoked." + run.Dump());
        Assert.Contains("Rejected dispatch", run.RejectResult!);
        // ⭐ REJECT RETAINS THE WORK RATHER THAN DESTROYING IT. That is the
        // behaviour Mark's "promote when it makes sense" implies the other half
        // of: refusing a dispatch must not throw the member's effort away, or a
        // leader's judgement call becomes unrecoverable.
        Assert.Contains("kept", run.RejectResult!);
        Assert.True(Directory.Exists(run.WorktreePath!),
            "A rejected dispatch's worktree was deleted, so the member's work is unrecoverable "
            + "and the leader cannot revisit the decision." + run.Dump());
        Assert.Contains(Sentinel, File.ReadAllText(Path.Combine(run.WorktreePath!, WorkFile)));

        Assert.False(File.Exists(Path.Combine(_repo, WorkFile)),
            "A REJECTED dispatch's work reached the leader's workspace anyway." + run.Dump());

        // And the pre-existing file is untouched — reject must not roll the
        // parent back to something, only leave it alone.
        Assert.Equal("seed\n", File.ReadAllText(Path.Combine(_repo, "seed.txt")).Replace("\r\n", "\n"));
    }

    /// <summary>
    /// SHARED WORKSPACE. `dispatch_worktree: false` — the member edits the
    /// leader's own tree directly, and the write lands with NO promotion step at
    /// all. This is the other mode Mark named ("times we want teams to share the
    /// same space as leader"), and it is the one with no review gate: what the
    /// member writes IS the result.
    /// </summary>
    [Fact]
    public async Task In_SHARED_mode_the_member_edits_the_leaders_own_workspace_with_no_promotion_step()
    {
        var run = await RunTeamAsync(worktree: false, decision: Decision.None);

        AssertDispatched(run, expectWorktree: false);

        // ⭐ THIS ASSERT IS THE POSITIVE CONTROL FOR THE OTHER TWO ARMS.
        // Both isolation arms conclude from `SnapshotInParent == false`, and a
        // parent-side probe that could never read true would give them that for
        // free — wrong path, wrong moment, wrong filename, all indistinguishable
        // from real isolation. Here the SAME probe, at the SAME dispatch_end
        // instant, on the SAME filename, must read TRUE. It is the one place in
        // this file where the instrument itself is measured.
        Assert.True(run.SnapshotInParent,
            "INSTRUMENT NOT LIVE: the parent-side probe never reads true even in shared mode, "
            + "so the isolation arms' `false` proves nothing about isolation." + run.Dump());

        var landed = Path.Combine(_repo, WorkFile);
        Assert.True(File.Exists(landed),
            "In shared mode the member's write did not reach the leader's workspace." + run.Dump());
        Assert.Contains(Sentinel, File.ReadAllText(landed));

        // ⛔ NO REVIEW GATE EXISTS IN THIS MODE — and that is the operationally
        // important half, not a footnote. The leader is never offered a
        // decision, so there is no point at which bad work can be refused.
        Assert.DoesNotContain(run.ToolResults, r => r.Contains("PENDING REVIEW"));
        Assert.Null(run.AcceptResult);
        Assert.Null(run.RejectResult);
    }

    /// <summary>
    /// ⭐ FIVE MEMBERS AT ONCE, MIXED VERDICTS, ONE RUN. Mark: "Be sure to add to
    /// your testing teams where the main leader AI ask 5 or even 10 SUB AIs to do
    /// things."
    ///
    /// This is also the strongest control in the file, because the accepted and
    /// rejected work are measured in the SAME run against the SAME parent tree.
    /// The three arms above infer from a comparison ACROSS runs, which a
    /// per-run environment difference could in principle explain; here three
    /// files must be present and two absent at one instant, so no such
    /// explanation survives.
    ///
    /// It additionally proves the promotion path is correct under CONCURRENCY —
    /// five `git apply` calls into one working tree, from five worktrees created
    /// in parallel. `accept_dispatch` takes no lock, so if that is a race, this
    /// is where it shows.
    /// </summary>
    [Fact]
    public async Task FIVE_concurrent_members_promote_INDEPENDENTLY_and_a_reject_does_not_block_a_peer()
    {
        string[] names = ["w1", "w2", "w3", "w4", "w5"];
        var accepted = new HashSet<string> { "w1", "w2", "w3" };

        var events = new ConcurrentQueue<Event>();
        var profile = new Profile
        {
            Llm = new LlmConfig(),
            Tools = ["file_editor"],
            Team = new TeamConfig
            {
                // Exactly wide enough for all five. A ceiling that refused one
                // would turn a missing file into an ambiguous result, so this
                // number is load-bearing for the assertions below.
                MaxConcurrentDispatches = 5,
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM", MaxIterations = 10 },
                DispatchWorktree = true,
                DispatchMaxAgeDays = 0,
                DispatchRetention = "keep-on-failure",
                AutoInjectAsyncResults = false,
            },
        };
        foreach (var n in names)
        {
            profile.Team.Members.Add(new MemberConfig
            {
                Name = n,
                SystemPrompt = MemberSystem + " I am " + n + ".",
                MaxIterations = 6,
                Tools = ["file_editor"],
            });
        }

        var client = new FanOutScriptClient(names, accepted);
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync(LeaderGo);
        chan.Writer.Complete();

        using var sandbox = new DirectBash(_repo);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await TeamCoordinator.RunInteractiveAsync(
            profile, client, "test-model", sandbox, _panelId,
            chan.Reader,
            onAssistantText: null, onWaitingForInput: null, onEvent: events.Enqueue,
            seedHistory: null, turnInterrupt: null, compactRequest: null,
            cwd: _repo, ct: cts.Token);

        var evs = events.ToList();
        var diag = $"\nstarts={evs.Count(e => e.Type == "dispatch_start")}"
                 + $" ends={evs.Count(e => e.Type == "dispatch_end")}"
                 + $"\ndecisions={string.Join(", ", client.Decisions)}"
                 + $"\nrepo={string.Join(", ", Directory.GetFiles(_repo).Select(Path.GetFileName))}";

        // PREMISE: all five really ran. Four running and one refused would leave
        // a file absent for a reason that has nothing to do with rejection.
        Assert.Equal(5, evs.Count(e => e.Type == "dispatch_start"));
        Assert.Equal(5, evs.Count(e => e.Type == "dispatch_end"));
        Assert.True(client.Decisions.Count == 5,
            "PREMISE NOT MET: the leader did not decide all five dispatches." + diag);

        foreach (var n in names)
        {
            var path = Path.Combine(_repo, $"work-{n}.txt");
            if (accepted.Contains(n))
            {
                Assert.True(File.Exists(path), $"accepted member {n}'s work was not promoted." + diag);
                Assert.Contains(Sentinel + "-" + n, File.ReadAllText(path));
            }
            else
            {
                Assert.False(File.Exists(path), $"REJECTED member {n}'s work was promoted anyway." + diag);
            }
        }
    }

    /// <summary>
    /// ⭐ TWO ACCEPTED MEMBERS EDIT THE SAME FILE. The fan-out arm above uses
    /// five disjoint files, so its five `git apply` calls cannot textually
    /// conflict — it proves promotion scales, not that promotion is SAFE when
    /// two members disagree. At 10–20 concurrent teams, two members touching one
    /// file is not an edge case; it is Tuesday.
    ///
    /// `accept_dispatch` applies with plain `git apply` (not `--3way`) and takes
    /// no lock, and `DispatchApplyConflictException` had zero references in the
    /// entire test suite before this test — so the behaviour here was genuinely
    /// unmeasured.
    ///
    /// ⭐ THE SEQUENCE IS DELIBERATELY SERIAL: dispatch c1, dispatch c2, THEN
    /// accept c1, THEN accept c2. Both worktrees are therefore cut from the same
    /// parent state, and c1's promotion moves the parent out from under c2's
    /// patch — which is exactly the real-world hazard, reproduced without
    /// depending on a race.
    ///
    /// ⚠ THE FIRST VERSION DISPATCHED BOTH CONCURRENTLY AND FLAKED — green 6/6
    /// alone and 14/19 under full-suite parallelism. Concurrency was never this
    /// test's subject (the five-member arm above covers that); it was only a way
    /// to create the conflict, and it made the conflict itself intermittent.
    /// Serialising removes the worktree-creation race AND lets the winner be
    /// named, so the test gained power by losing the concurrency. A flaky test
    /// that asserts a shape is worth less than a deterministic one that asserts
    /// an outcome.
    ///
    /// The failure that matters is the leader being told a promotion succeeded
    /// when the bytes did not land: it would then report completed work that
    /// does not exist. That is what `claimedApplied == actuallyLanded` pins.
    /// </summary>
    [Fact]
    public async Task Two_members_editing_the_SAME_file_cannot_both_report_success_unless_both_landed()
    {
        string[] names = ["c1", "c2"];
        var events = new ConcurrentQueue<Event>();
        var profile = new Profile
        {
            Llm = new LlmConfig(),
            Tools = ["file_editor"],
            Team = new TeamConfig
            {
                MaxConcurrentDispatches = 2,
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM", MaxIterations = 10 },
                DispatchWorktree = true,
                DispatchMaxAgeDays = 0,
                DispatchRetention = "keep-on-failure",
                AutoInjectAsyncResults = false,
            },
        };
        foreach (var n in names)
        {
            profile.Team.Members.Add(new MemberConfig
            {
                Name = n,
                SystemPrompt = MemberSystem + " I am " + n + ".",
                MaxIterations = 6,
                Tools = ["file_editor"],
            });
        }

        // Both accepted, both rewriting the pre-existing seed.txt.
        var client = new ConflictScriptClient();
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync(LeaderGo);
        chan.Writer.Complete();

        using var sandbox = new DirectBash(_repo);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await TeamCoordinator.RunInteractiveAsync(
            profile, client, "test-model", sandbox, _panelId,
            chan.Reader,
            onAssistantText: null, onWaitingForInput: null, onEvent: events.Enqueue,
            seedHistory: null, turnInterrupt: null, compactRequest: null,
            cwd: _repo, ct: cts.Token);

        var final = File.ReadAllText(Path.Combine(_repo, "seed.txt")).Replace("\r\n", "\n");
        var results = client.DecisionResults;
        var diag = $"\nfinal seed.txt = {final.Replace("\n", "\\n")}"
                 + $"\ndecision results:\n  " + string.Join("\n  ", results.Select(kv => $"{kv.Key} => {kv.Value.Replace("\n", " | ")}"));

        Assert.Equal(2, events.Count(e => e.Type == "dispatch_start"));
        Assert.True(results.Count == 2, "PREMISE NOT MET: both dispatches were not decided." + diag);

        // ⭐ THE SAFETY PROPERTY. Count how many accepts CLAIMED to have applied,
        // and how many members' content is actually in the file. They must
        // agree. A claim without the bytes is the leader being lied to.
        var claimedApplied = results.Values.Count(v => v.Contains("Applied"));
        var actuallyLanded = names.Count(n => final.Contains(Sentinel + "-" + n));

        Assert.True(claimedApplied == actuallyLanded,
            $"FALSE SUCCESS: {claimedApplied} accept_dispatch call(s) reported applying, but only "
            + $"{actuallyLanded} member(s)' content is in the file. The leader has been told work "
            + "landed that did not land." + diag);

        // And the file must not be wreckage — whatever won, the result is one
        // member's coherent content, never a half-applied hybrid.
        Assert.DoesNotContain("<<<<<<<", final);

        // MEASURED 2026-08-27: exactly one applies, and because the sequence is
        // deterministic it is always the FIRST — c1 accepted while c2's patch
        // was still cut against the pre-accept parent.
        Assert.Equal(1, claimedApplied);
        Assert.Contains(Sentinel + "-c1", final);
        Assert.DoesNotContain(Sentinel + "-c2", final);

        var loser = results["c2-1"];
        // ⭐ THE REFUSAL MUST BE RECOVERABLE, which is the whole difference
        // between a safe failure and a lost afternoon. VETT does this properly:
        // it names the cause, keeps the worktree, and tells the leader the two
        // ways forward. Losing any of those three would leave a leader holding
        // an error it cannot act on.
        Assert.Contains("patch does not apply", loser);
        Assert.Contains("still at", loser);
        Assert.Contains("re-dispatch", loser);
        Assert.Contains("reject_dispatch", loser);
    }

    /// <summary>
    /// ⛔ TWO PROMOTIONS IN ONE LEADER TURN — the case the serial arm above
    /// deliberately cannot reach.
    ///
    /// The serial test proves the CONFLICT is handled well, but it accepts one
    /// dispatch per turn, so `git apply` is never re-entered concurrently. That
    /// left the actual concurrency hazard unmeasured, and the previous audit said
    /// so in as many words: "10/10 at width 5 is not proof accept_dispatch is
    /// race-free ... those five files are disjoint, so their `git apply` calls
    /// cannot textually conflict."
    ///
    /// Here both accept_dispatch calls arrive in ONE assistant message.
    /// AgentLoop dispatches multiple tool calls from a single message through
    /// Task.WhenAll, so both promotions run against the SAME parent worktree at
    /// the same time — through a DispatchWorktreeManager that holds no lock, via
    /// a two-phase `git apply --check` then `git apply` whose own comment
    /// concedes "a race against parent file changes between check and apply
    /// could still fail."
    ///
    /// ⭐ WHAT THIS ASSERTS IS THE INVARIANT, NOT THE ACCIDENT. It does not name
    /// the winner: under real concurrency the winner is whichever promotion takes
    /// the gate first, and pinning that would be asserting a scheduling detail
    /// and calling it a guarantee. What must hold every time is that exactly one
    /// lands, the claims match the bytes, and the loser gets a refusal it can act
    /// on.
    ///
    /// ⛔ WHAT THE NEGATIVE CONTROL ACTUALLY SAID — READ BEFORE CITING THIS TEST.
    /// With the gate removed and PromotionProbe confirming the promotions still
    /// overlapped, this test came back 0 red in 27 runs. So a green here is NOT
    /// evidence that the gate repairs an observed corruption; the gate is
    /// defence in depth against a TOCTOU window that this construction does not
    /// lose. The reason it survives: `git apply` is invoked plain — no
    /// --index/--cached — so it takes no index.lock, and each process re-reads
    /// the file at apply time, so the loser degrades into an ordinary "patch
    /// does not apply" conflict.
    ///
    /// The index.lock assertion below is therefore a REGRESSION TRIPWIRE, not a
    /// reproduction: it fires if someone switches ApplyAsync to a mode that does
    /// take git's lock (--index, --cached, or an apply-then-commit), at which
    /// point the leader would start getting lock-file errors instead of conflict
    /// errors — un-actionable, and indistinguishable from an infrastructure
    /// fault. An earlier version of this comment called it "the specific
    /// corruption signature"; no such corruption was ever observed. Retracted.
    ///
    /// ⭐ WHAT THIS TEST DOES PROVE, on its own and without the control:
    /// two accept_dispatch calls in one turn overlap for real (probe = 2), and
    /// under that overlap exactly one lands, the claims match the bytes, and the
    /// loser is refused actionably. That was previously untested at all.
    /// </summary>
    [Fact]
    public async Task Two_accepts_in_ONE_turn_promote_one_at_a_time_and_never_corrupt_the_parent()
    {
        string[] names = ["c1", "c2"];
        var events = new ConcurrentQueue<Event>();
        var profile = new Profile
        {
            Llm = new LlmConfig(),
            Tools = ["file_editor"],
            Team = new TeamConfig
            {
                MaxConcurrentDispatches = 2,
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM", MaxIterations = 10 },
                DispatchWorktree = true,
                DispatchMaxAgeDays = 0,
                DispatchRetention = "keep-on-failure",
                AutoInjectAsyncResults = false,
            },
        };
        foreach (var n in names)
        {
            profile.Team.Members.Add(new MemberConfig
            {
                Name = n,
                SystemPrompt = MemberSystem + " I am " + n + ".",
                MaxIterations = 6,
                Tools = ["file_editor"],
            });
        }

        var client = new ConflictScriptClient(concurrentAccepts: true);
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync(LeaderGo);
        chan.Writer.Complete();

        PromotionProbe.Reset();

        using var sandbox = new DirectBash(_repo);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await TeamCoordinator.RunInteractiveAsync(
            profile, client, "test-model", sandbox, _panelId,
            chan.Reader,
            onAssistantText: null, onWaitingForInput: null, onEvent: events.Enqueue,
            seedHistory: null, turnInterrupt: null, compactRequest: null,
            cwd: _repo, ct: cts.Token);

        var final = File.ReadAllText(Path.Combine(_repo, "seed.txt")).Replace("\r\n", "\n");
        var results = client.DecisionResults;
        var diag = $"\nfinal seed.txt = {final.Replace("\n", "\\n")}"
                 + $"\ndecision results:\n  " + string.Join("\n  ", results.Select(kv => $"{kv.Key} => {kv.Value.Replace("\n", " | ")}"));

        // PREMISE FIRST. A run where the two accepts never both happened would
        // satisfy "exactly one landed" trivially — VOID, not PASS.
        Assert.Equal(2, events.Count(e => e.Type == "dispatch_start"));
        Assert.True(results.Count == 2,
            "PREMISE NOT MET: both accept_dispatch calls were not decided, so the "
            + "concurrent promotion under test never occurred." + diag);

        // ⭐ THE PREMISE THIS TEST'S WHOLE POINT RESTS ON, STATED POSITIVELY.
        //
        // "Both accepts were decided" does NOT mean they overlapped — the loop
        // could have run them nose-to-tail and the assertions below would pass
        // for a reason that has nothing to do with the gate. The probe counts
        // arrivals in the promotion region (outside the semaphore), so this is
        // the difference between "two promotions collided and exactly one
        // landed" and "two promotions never met".
        //
        // Healthy NULL is 1 (serialized by the scheduler) — which is a VOID
        // run, not a passing one. 0 would mean nothing promoted at all.
        Assert.True(PromotionProbe.Entries == 2,
            $"PREMISE NOT MET: {PromotionProbe.Entries} promotion(s) entered the region, expected 2." + diag);
        Assert.True(PromotionProbe.MaxOverlap >= 2,
            $"PREMISE NOT MET: peak promotion overlap was {PromotionProbe.MaxOverlap}, so the two "
            + "accepts never actually ran concurrently. This run says NOTHING about the promotion "
            + "gate — it is VOID, not passing." + diag);

        var claimedApplied = results.Values.Count(v => v.Contains("Applied"));
        var actuallyLanded = names.Count(n => final.Contains(Sentinel + "-" + n));

        Assert.True(claimedApplied == actuallyLanded,
            $"FALSE SUCCESS under concurrency: {claimedApplied} accept_dispatch call(s) reported "
            + $"applying, but {actuallyLanded} member(s)' content is in the file." + diag);

        Assert.True(claimedApplied == 1,
            $"Expected exactly one promotion to land, got {claimedApplied}." + diag);

        Assert.DoesNotContain("<<<<<<<", final);

        // The loser must be refused in a way the leader can act on.
        var loser = results.Values.Single(v => !v.Contains("Applied"));
        Assert.DoesNotContain("index.lock", loser);
        Assert.Contains("still at", loser);
        Assert.Contains("re-dispatch", loser);
        Assert.Contains("reject_dispatch", loser);
    }

    // ---- harness ----------------------------------------------------------

    private enum Decision { None, Accept, Reject }

    private sealed record RunOutcome(
        List<Event> Events,
        string? WorktreePath,
        List<string> ToolResults,
        bool SnapshotInParent,
        bool SnapshotInWorktree,
        bool SnapshotTaken,
        string? AcceptResult,
        string? RejectResult)
    {
        /// <summary>Diagnostics for premise failures. A premise assert that
        /// fires without saying WHY leaves you unable to tell "the treatment
        /// never ran" from "the treatment ran and the behaviour is wrong".</summary>
        public string Dump() =>
            $"\nworktree_path={WorktreePath ?? "(null)"}"
            + $"\nsnapshot_taken={SnapshotTaken} in_parent={SnapshotInParent} in_worktree={SnapshotInWorktree}"
            + $"\naccept={AcceptResult ?? "(never called)"}"
            + $"\nreject={RejectResult ?? "(never called)"}"
            + $"\nevents=[{string.Join(", ", Events.Select(e => e.Type))}]"
            + $"\ntool_results=[{string.Join(" ;; ", ToolResults)}]";
    }

    /// <summary>⛔ POSITIVE CONJUNCT. Every claim in this file is of the form
    /// "the file is / is not at path P", and a run where no dispatch ever
    /// happened satisfies half of them for free. This demands evidence the
    /// dispatch really ran, and that its workspace mode is the one the arm
    /// intended, BEFORE any conclusion is drawn.</summary>
    private static void AssertDispatched(RunOutcome run, bool expectWorktree)
    {
        Assert.True(run.Events.Any(e => e.Type == "dispatch_start"),
            "PREMISE NOT MET: no dispatch_start — no member ever ran, so this run says nothing "
            + "about workspaces or promotion." + run.Dump());

        Assert.True(run.SnapshotTaken,
            "PREMISE NOT MET: no dispatch_end, so the mid-run isolation snapshot was never "
            + "taken and its two flags are defaults, not measurements." + run.Dump());

        if (expectWorktree)
        {
            Assert.False(string.IsNullOrEmpty(run.WorktreePath),
                "PREMISE NOT MET: dispatch_worktree:true but no worktree path was reported — "
                + "the run may have silently latched into shared mode, which would make an "
                + "isolation assertion measure the wrong mode entirely." + run.Dump());
            Assert.DoesNotContain(run.Events, e => e.Type == "dispatch_worktree_fallback");
        }
        else
        {
            Assert.True(string.IsNullOrEmpty(run.WorktreePath),
                "PREMISE NOT MET: dispatch_worktree:false but a worktree was created — this arm "
                + "is not measuring shared mode." + run.Dump());
        }
    }

    private async Task<RunOutcome> RunTeamAsync(bool worktree, Decision decision)
    {
        var events = new ConcurrentQueue<Event>();
        string? worktreePath = null;
        bool inParent = false, inWorktree = false, snapshotTaken = false;

        void OnEvent(Event e)
        {
            events.Enqueue(e);

            if (e.Type == "dispatch_start")
            {
                worktreePath = e.Data.TryGetValue("worktree_path", out var p) ? p as string : null;
                return;
            }

            // ⭐ THE ISOLATION MEASUREMENT, and its timing is the whole point.
            // dispatch_end fires after the member finished and after capture,
            // but before the leader's next turn — i.e. before any accept or
            // reject. Sampling here is the only moment at which "isolated" and
            // "promoted" are distinguishable states of the same file. Taken
            // after the run instead, both arms would read identically for the
            // wrong reason.
            if (e.Type != "dispatch_end") return;
            snapshotTaken = true;
            inParent = File.Exists(Path.Combine(_repo, WorkFile));
            inWorktree = worktreePath is not null && File.Exists(Path.Combine(worktreePath, WorkFile));
        }

        var profile = new Profile
        {
            Llm = new LlmConfig(),
            // The member needs file_editor REGISTERED, not merely advertised —
            // Coordinator filters a member's tool dict to its own `tools:` list.
            Tools = ["file_editor"],
            Team = new TeamConfig
            {
                // A real positive ceiling, not the 0 the older fixtures carry:
                // this file is new, so there is no prior measurement whose
                // comparability a cap could break. One member, one slot spare.
                MaxConcurrentDispatches = 2,
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM", MaxIterations = 8 },
                Members =
                {
                    new MemberConfig
                    {
                        Name = MemberName,
                        SystemPrompt = MemberSystem,
                        MaxIterations = 8,
                        Tools = ["file_editor"],
                    },
                },
                DispatchWorktree = worktree,
                // 0 disables the startup auto-prune. It walks the SHARED
                // ~/.vett/dispatches tree and would reap another run's retained
                // worktrees; a unit test must never do that.
                DispatchMaxAgeDays = 0,
                DispatchRetention = "keep-on-failure",
                AutoInjectAsyncResults = false,
            },
        };

        var client = new TeamScriptClient(decision);
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync(LeaderGo);
        chan.Writer.Complete();

        // ⭐ A REAL SANDBOX. Everything in this file rests on this line: the
        // member's file_editor calls reach the actual filesystem, so "the file
        // is there" and "the file is not there" are measurements rather than
        // restatements of a fake's return value.
        using var sandbox = new DirectBash(_repo);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await TeamCoordinator.RunInteractiveAsync(
            profile, client, "test-model", sandbox, _panelId,
            chan.Reader,
            onAssistantText: null, onWaitingForInput: null, onEvent: OnEvent,
            seedHistory: null, turnInterrupt: null, compactRequest: null,
            cwd: _repo, ct: cts.Token);

        return new RunOutcome(
            events.ToList(), worktreePath, client.ToolResults,
            inParent, inWorktree, snapshotTaken,
            client.AcceptResult, client.RejectResult);
    }

    /// <summary>
    /// Drives leader and member off one client. They are told apart by the
    /// member's system-prompt marker, not by call order, so the interleaving
    /// cannot silently mis-script the run.
    ///
    /// The leader's task_id is PARSED OUT of the PENDING REVIEW block rather
    /// than assumed to be "t1". A hard-coded id that stopped matching would make
    /// accept_dispatch fail with "no pending dispatch", and the reject arm would
    /// still pass — the file would be absent for the wrong reason.
    /// </summary>
    private sealed class TeamScriptClient(Decision decision) : IChatClient
    {
        private static readonly Regex TaskIdRe = new(@"PENDING REVIEW \(task_id: (?<id>[^)]+)\)", RegexOptions.Compiled);

        private int _leaderCalls;
        private int _memberCalls;
        private readonly object _lock = new();

        public readonly List<string> ToolResults = [];
        public string? AcceptResult { get; private set; }
        public string? RejectResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var list = messages.ToList();

            if (list.Any(m => (m.Text ?? "").Contains(MemberSystem)))
            {
                int mn;
                lock (_lock) { mn = ++_memberCalls; }
                if (mn == 1)
                {
                    // The real write. Relative path, so it resolves against
                    // whichever cwd the member's sandbox actually has — the
                    // worktree in isolated mode, the leader's repo in shared
                    // mode. That is exactly the behaviour under test, so the
                    // path is deliberately NOT absolute.
                    return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                    {
                        new FunctionCallContent("m-1", "file_editor", new Dictionary<string, object?>
                        {
                            ["command"] = "create",
                            ["path"] = WorkFile,
                            ["file_text"] = Sentinel + "\n",
                        }),
                    }));
                }
                return Reply(new ChatMessage(ChatRole.Assistant,
                    $"Created {WorkFile}. <self_assessment>done</self_assessment>"));
            }

            // Harvest everything the leader was told, and pick the task_id out
            // of it.
            string? taskId = null;
            foreach (var c in list.SelectMany(m => m.Contents))
            {
                if (c is not FunctionResultContent fr) continue;
                var text = fr.Result?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                lock (_lock) { if (!ToolResults.Contains(text)) ToolResults.Add(text); }
                var m2 = TaskIdRe.Match(text);
                if (m2.Success) taskId = m2.Groups["id"].Value;

                // ⛔ KEYED ON THE CALL ID, NOT ON WORDS IN THE RESULT.
                // The first draft of this class detected the decision with
                // `text.Contains("ACCEPTED")`. The real literal is "Accepted
                // dispatch", so the predicate returned a confident zero and both
                // arms reported "accept_dispatch was never invoked" — while the
                // dump showed it had run and had applied the file. A guessed
                // vocabulary fails CLOSED here only because a premise guard was
                // watching; the reject arm in particular would otherwise have
                // gone green for entirely the wrong reason, since its conclusion
                // ("the file is absent") is exactly what a never-invoked tool
                // also produces. call-2 is the decision by construction.
                if (fr.CallId != "call-2") continue;
                if (decision == Decision.Accept) AcceptResult ??= text;
                else if (decision == Decision.Reject) RejectResult ??= text;
            }

            int n;
            lock (_lock) { n = ++_leaderCalls; }

            if (n == 1)
            {
                return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-1", "assign_task", new Dictionary<string, object?>
                    {
                        ["member"] = MemberName,
                        ["task"] = MemberTask,
                    }),
                }));
            }

            if (n == 2 && decision != Decision.None && taskId is not null)
            {
                var (tool, args) = decision == Decision.Accept
                    ? ("accept_dispatch", new Dictionary<string, object?>
                    {
                        ["task_id"] = taskId,
                        ["review"] = "Read the diff: creates " + WorkFile + " with the expected content, no other files touched.",
                    })
                    : ("reject_dispatch", new Dictionary<string, object?>
                    {
                        ["task_id"] = taskId,
                        ["review"] = "Read the diff: the new file is not what this task asked for.",
                        ["reason"] = "not what was asked for",
                    });

                return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-2", tool, args),
                }));
            }

            return Reply(new ChatMessage(ChatRole.Assistant, "Done. Stopping here."));
        }

        private static Task<ChatResponse> Reply(ChatMessage m) =>
            Task.FromResult(new ChatResponse([new ChatMessage(m.Role, m.Contents.ToList())]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Leader dispatches all five in ONE assistant message (so they run
    /// concurrently, not serially), then decides all five in the next.
    ///
    /// Task ids are harvested from the PENDING REVIEW blocks and matched to
    /// members by the `{name}-` prefix — never by the order the results arrive
    /// in, which under concurrency is nondeterministic. Mapping by arrival order
    /// would silently accept w4's work while calling it w1's, and every
    /// assertion would still pass.
    /// </summary>
    private sealed class FanOutScriptClient(string[] names, HashSet<string> accepted) : IChatClient
    {
        private static readonly Regex TaskIdRe = new(@"PENDING REVIEW \(task_id: (?<id>[^)]+)\)", RegexOptions.Compiled);

        private int _leaderCalls;
        private readonly object _lock = new();
        private readonly HashSet<string> _memberSeen = [];
        public readonly List<string> Decisions = [];
        public readonly Dictionary<string, string> DecisionResults = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var list = messages.ToList();
            var sys = list.FirstOrDefault(m => (m.Text ?? "").Contains(MemberSystem))?.Text ?? "";

            if (sys.Length > 0)
            {
                var me = names.First(n => sys.Contains("I am " + n + "."));
                bool first;
                lock (_lock) { first = _memberSeen.Add(me); }
                if (first)
                {
                    return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                    {
                        // Per-member content, so a promoted file can be traced to
                        // the member that actually wrote it.
                        new FunctionCallContent($"m-{me}", "file_editor", new Dictionary<string, object?>
                        {
                            ["command"] = "create",
                            ["path"] = $"work-{me}.txt",
                            ["file_text"] = Sentinel + "-" + me + "\n",
                        }),
                    }));
                }
                return Reply(new ChatMessage(ChatRole.Assistant,
                    $"Wrote work-{me}.txt. <self_assessment>done</self_assessment>"));
            }

            var ids = new List<string>();
            foreach (var c in list.SelectMany(m => m.Contents))
            {
                if (c is not FunctionResultContent fr) continue;
                var text = fr.Result?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                foreach (Match m2 in TaskIdRe.Matches(text)) ids.Add(m2.Groups["id"].Value);
                // Decision OUTCOMES, keyed by call id — what accept/reject
                // actually reported, as distinct from what we asked for.
                if (fr.CallId.StartsWith("d-", StringComparison.Ordinal))
                    lock (_lock) { DecisionResults[fr.CallId[2..]] = text; }
            }

            int n2;
            lock (_lock) { n2 = ++_leaderCalls; }

            if (n2 == 1)
            {
                // All five in one message → AgentLoop dispatches them in
                // parallel. Issuing them across five turns would serialise the
                // run and quietly retire the concurrency claim.
                return Reply(new ChatMessage(ChatRole.Assistant, names.Select(nm =>
                    (AIContent)new FunctionCallContent($"call-{nm}", "assign_task", new Dictionary<string, object?>
                    {
                        ["member"] = nm,
                        ["task"] = $"{MemberTask} for {nm}",
                    })).ToList()));
            }

            if (n2 == 2 && ids.Count > 0)
            {
                var calls = new List<AIContent>();
                foreach (var id in ids.Distinct())
                {
                    var owner = names.FirstOrDefault(nm => id.StartsWith(nm + "-", StringComparison.Ordinal));
                    if (owner is null) continue;
                    lock (_lock) { Decisions.Add($"{id}:{(accepted.Contains(owner) ? "accept" : "reject")}"); }
                    calls.Add(accepted.Contains(owner)
                        ? new FunctionCallContent($"d-{id}", "accept_dispatch", new Dictionary<string, object?>
                        {
                            ["task_id"] = id,
                            ["review"] = $"Reviewed {owner}: adds work-{owner}.txt only.",
                        })
                        : new FunctionCallContent($"d-{id}", "reject_dispatch", new Dictionary<string, object?>
                        {
                            ["task_id"] = id,
                            ["review"] = $"Reviewed {owner}: not what was asked.",
                            ["reason"] = "not what was asked for",
                        }));
                }
                if (calls.Count > 0) return Reply(new ChatMessage(ChatRole.Assistant, calls));
            }

            return Reply(new ChatMessage(ChatRole.Assistant, "All five handled. Stopping."));
        }

        private static Task<ChatResponse> Reply(ChatMessage m) =>
            Task.FromResult(new ChatResponse([new ChatMessage(m.Role, m.Contents.ToList())]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Strictly serial two-member conflict script: assign c1, assign c2, accept
    /// c1, accept c2. One tool call per leader turn, so the ordering is fixed by
    /// construction rather than by timing.
    /// </summary>
    private sealed class ConflictScriptClient(bool concurrentAccepts = false) : IChatClient
    {
        private static readonly Regex TaskIdRe = new(@"PENDING REVIEW \(task_id: (?<id>[^)]+)\)", RegexOptions.Compiled);

        // When true, BOTH accept_dispatch calls are emitted in ONE assistant
        // message. AgentLoop fans multiple tool calls from a single message out
        // through Task.WhenAll, so this is what actually runs two promotions
        // concurrently — the serial arm cannot reach that path at all.
        private readonly bool _concurrentAccepts = concurrentAccepts;

        private int _leaderCalls;
        private readonly object _lock = new();
        private readonly HashSet<string> _memberSeen = [];
        public readonly Dictionary<string, string> DecisionResults = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var list = messages.ToList();
            var sys = list.FirstOrDefault(m => (m.Text ?? "").Contains(MemberSystem))?.Text ?? "";

            if (sys.Length > 0)
            {
                var me = sys.Contains("I am c1.") ? "c1" : "c2";
                bool first;
                lock (_lock) { first = _memberSeen.Add(me); }
                if (first)
                {
                    // str_replace, not create — `create` does NOT overwrite an
                    // existing file, so an earlier draft produced an empty diff
                    // and no PENDING REVIEW was ever offered. Both members
                    // rewrite the SAME line, which is what makes the two patches
                    // genuinely irreconcilable.
                    return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                    {
                        new FunctionCallContent($"m-{me}", "file_editor", new Dictionary<string, object?>
                        {
                            ["command"] = "str_replace",
                            ["path"] = "seed.txt",
                            ["old_str"] = "seed",
                            ["new_str"] = "seed\n" + Sentinel + "-" + me,
                        }),
                    }));
                }
                return Reply(new ChatMessage(ChatRole.Assistant,
                    $"Edited seed.txt. <self_assessment>done</self_assessment>"));
            }

            foreach (var c in list.SelectMany(m => m.Contents))
            {
                if (c is not FunctionResultContent fr) continue;
                var text = fr.Result?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                if (fr.CallId.StartsWith("d-", StringComparison.Ordinal))
                    lock (_lock) { DecisionResults[fr.CallId[2..]] = text; }
            }

            int n;
            lock (_lock) { n = ++_leaderCalls; }

            if (_concurrentAccepts)
            {
                // Turns 1-2 dispatch as usual; turn 3 accepts BOTH at once.
                AIContent? one = n switch
                {
                    1 => new FunctionCallContent("call-c1", "assign_task", new Dictionary<string, object?>
                    { ["member"] = "c1", ["task"] = MemberTask + " for c1" }),
                    2 => new FunctionCallContent("call-c2", "assign_task", new Dictionary<string, object?>
                    { ["member"] = "c2", ["task"] = MemberTask + " for c2" }),
                    _ => null,
                };
                if (one is not null)
                    return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent> { one }));
                if (n > 3)
                    return Reply(new ChatMessage(ChatRole.Assistant, "Both handled. Stopping."));

                // Same premise guard as the serial arm: if either task_id never
                // reached the leader, the concurrency under test never happened
                // and a green here would be measuring nothing.
                var ids = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                    .Select(f => f.Result?.ToString() ?? "")
                    .SelectMany(t => TaskIdRe.Matches(t).Select(mm => mm.Groups["id"].Value))
                    .ToList();
                if (!ids.Contains("c1-1") || !ids.Contains("c2-1"))
                    return Reply(new ChatMessage(ChatRole.Assistant,
                        $"SCRIPT-DESYNC: expected both 'c1-1' and 'c2-1', saw [{string.Join(",", ids)}]."));

                return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("d-c1-1", "accept_dispatch", new Dictionary<string, object?>
                    { ["task_id"] = "c1-1", ["review"] = "c1's edit to seed.txt looks right." }),
                    new FunctionCallContent("d-c2-1", "accept_dispatch", new Dictionary<string, object?>
                    { ["task_id"] = "c2-1", ["review"] = "c2's edit to seed.txt looks right too." }),
                }));
            }

            AIContent? call = n switch
            {
                1 => new FunctionCallContent("call-c1", "assign_task", new Dictionary<string, object?>
                { ["member"] = "c1", ["task"] = MemberTask + " for c1" }),
                2 => new FunctionCallContent("call-c2", "assign_task", new Dictionary<string, object?>
                { ["member"] = "c2", ["task"] = MemberTask + " for c2" }),
                3 => new FunctionCallContent("d-c1-1", "accept_dispatch", new Dictionary<string, object?>
                { ["task_id"] = "c1-1", ["review"] = "c1's edit to seed.txt looks right." }),
                4 => new FunctionCallContent("d-c2-1", "accept_dispatch", new Dictionary<string, object?>
                { ["task_id"] = "c2-1", ["review"] = "c2's edit to seed.txt looks right too." }),
                _ => null,
            };

            // Task ids are `{member}-1` by construction here; assert that rather
            // than assuming it silently, so a change to id formatting surfaces as
            // this guard rather than as a mysterious "no pending dispatch".
            if (n is 3 or 4)
            {
                var want = n == 3 ? "c1-1" : "c2-1";
                var seen = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                    .Select(f => f.Result?.ToString() ?? "")
                    .SelectMany(t => TaskIdRe.Matches(t).Select(mm => mm.Groups["id"].Value))
                    .ToList();
                if (!seen.Contains(want))
                    return Reply(new ChatMessage(ChatRole.Assistant,
                        $"SCRIPT-DESYNC: expected task_id '{want}', saw [{string.Join(",", seen)}]."));
            }

            return call is null
                ? Reply(new ChatMessage(ChatRole.Assistant, "Both handled. Stopping."))
                : Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent> { call }));
        }

        private static Task<ChatResponse> Reply(ChatMessage m) =>
            Task.FromResult(new ChatResponse([new ChatMessage(m.Role, m.Contents.ToList())]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }
}
