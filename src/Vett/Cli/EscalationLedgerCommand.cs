using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vett.Bench.Team;
using Vett.Config;

namespace Vett.Cli;

/// <summary>
/// <c>vett escalation-ledger</c> — the structured-output surface for the
/// harness-owned escalation counter.
///
/// It replays a finished run's <c>_bench-session.jsonl</c> (the path
/// <c>vett team-bench --json</c> already publishes on every run as
/// <c>session_log_path</c>) through <see cref="EscalationLedger"/> and
/// prints how many LLM requests went to the BASE model versus the
/// ESCALATION model, with per-model token totals.
///
/// It reads a log; it never runs an agent and never contacts a model
/// endpoint, so it is safe to point at a run that is still in flight —
/// the result is then a snapshot of the run so far, and the ledger's own
/// completeness fields say so.
///
/// EXIT CODES — the point of the command is that a scorer can branch on
/// these instead of parsing prose:
///   0  status = live. The count is a measurement; read it.
///   2  usage / IO error (no such log, no base model).
///   3  status != live. The count is WITHHELD, deliberately. This is a
///      could-not-measure, and exiting non-zero is what stops it being
///      consumed as a zero.
/// </summary>
public static class EscalationLedgerCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command(
            "escalation-ledger",
            "Count LLM requests per model tier (base vs escalation) from a run's session log");

        var logOpt = new Option<string>("--session-log")
        {
            Description = "Path to a run's _bench-session.jsonl (team-bench --json publishes it as session_log_path)",
            Required = true,
        };
        var profileOpt = new Option<string?>("--profile")
        {
            Description = "Profile name (resolved under ./profiles) or path. Supplies the declared escalation roster and the blind-spot audit",
        };
        var endpointOpt = new Option<string?>("--endpoint")
        {
            Description = "Base endpoint actually used by the run (overrides the profile's)",
        };
        var modelOpt = new Option<string?>("--model")
        {
            Description = "Base model actually used by the run (overrides the profile's). Required when --profile is absent",
        };
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Emit the ledger snapshot as JSON on stdout",
        };

        cmd.Add(logOpt);
        cmd.Add(profileOpt);
        cmd.Add(endpointOpt);
        cmd.Add(modelOpt);
        cmd.Add(jsonOpt);

        cmd.SetAction(pr =>
        {
            var logPath = pr.GetValue(logOpt) ?? "";
            var profileArg = pr.GetValue(profileOpt);
            var endpointArg = pr.GetValue(endpointOpt);
            var modelArg = pr.GetValue(modelOpt);
            var asJson = pr.GetValue(jsonOpt);

            if (!File.Exists(logPath))
            {
                Console.Error.WriteLine($"escalation-ledger: session log not found: {logPath}");
                return 2;
            }

            Profile? profile = null;
            if (!string.IsNullOrEmpty(profileArg))
            {
                var path = ResolveProfilePath(profileArg);
                if (path is null)
                {
                    Console.Error.WriteLine($"escalation-ledger: profile not found: {profileArg}");
                    return 2;
                }
                try
                {
                    profile = Yaml.LoadProfile(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"escalation-ledger: could not load profile {path}: {ex.Message}");
                    return 2;
                }
            }

            var baseModel = modelArg ?? profile?.Llm.Model ?? "";
            var baseEndpoint = endpointArg ?? profile?.Llm.Endpoint ?? "";

            if (string.IsNullOrEmpty(baseModel))
            {
                Console.Error.WriteLine(
                    "escalation-ledger: no base model. Pass --model, or --profile whose llm.model is set. " +
                    "Without it every request is unclassifiable and the ledger would refuse to publish a count anyway.");
                return 2;
            }

            var ledger = profile is null
                ? EscalationLedger.ForBaseModel(baseModel)
                : EscalationLedger.FromProfile(profile, baseEndpoint, baseModel);

            try
            {
                ledger.ObserveSessionLog(logPath);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"escalation-ledger: could not read {logPath}: {ex.Message}");
                return 2;
            }

            var snap = ledger.Snapshot();

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Render(snap, logPath);
            }

            return snap.TryGetEscalationCount(out _) ? 0 : 3;
        });

        return cmd;
    }

    private static string? ResolveProfilePath(string arg)
    {
        if (File.Exists(arg)) return arg;
        var byName = Path.Combine(Directory.GetCurrentDirectory(), "profiles", arg + ".yaml");
        if (File.Exists(byName)) return byName;
        var byNameExt = Path.Combine(Directory.GetCurrentDirectory(), "profiles", arg);
        return File.Exists(byNameExt) ? byNameExt : null;
    }

    private static void Render(EscalationLedgerSnapshot s, string logPath)
    {
        Console.WriteLine($"escalation-ledger  ({logPath})");
        Console.WriteLine($"  status              : {s.Status}");
        if (s.StatusDetail is not null)
            Console.WriteLine($"  status_detail       : {s.StatusDetail}");
        Console.WriteLine($"  base                : {s.BaseIdentity}");
        Console.WriteLine($"  escalation tier     : {(s.EscalationTierConfigured ? string.Join(", ", s.EscalationTierMembers) : "NOT CONFIGURED (a zero here is structural)")}");
        if (s.LeaderTier is not null)
            Console.WriteLine($"  leader tier         : {s.LeaderTier}");
        Console.WriteLine();
        Console.WriteLine($"  events observed     : {s.EventsObserved}  (threads: {s.ThreadsObserved})");
        Console.WriteLine($"  iteration_start     : {s.IterationStartsTotal}");
        Console.WriteLine($"  llm_request         : {s.LlmRequestsTotal}   <- liveness conjunct; 0 means NOT MEASURED");
        Console.WriteLine($"  unmatched iters     : {s.UnmatchedIterations} (threads over the 1-in-flight allowance: {s.ThreadsWithDroppedRequests})");
        Console.WriteLine($"  orphan responses    : {s.OrphanResponses}");
        Console.WriteLine($"  colliding req keys  : {s.CollidingRequestKeys}");
        Console.WriteLine($"  malformed log lines : {s.MalformedLogLines}");
        Console.WriteLine($"  failed attempts     : {s.FailedAttemptsTotal}");
        Console.WriteLine();

        if (s.PerModel.Count > 0)
        {
            Console.WriteLine("  model                          tier        requests  fail   in_tok   out_tok  declared");
            foreach (var m in s.PerModel)
            {
                Console.WriteLine(
                    $"  {Trunc(m.Model, 30),-30} {m.Tier,-10} {m.Requests,9} {m.FailedAttempts,5} {m.InputTokens,8} {m.OutputTokens,9}  {(m.Declared ? "yes" : "NO")}");
            }
            Console.WriteLine();
        }

        if (s.UndeclaredEscalationModels.Count > 0)
        {
            Console.WriteLine($"  !! escalated to models the profile never declared: {string.Join(", ", s.UndeclaredEscalationModels)}");
            Console.WriteLine();
        }

        Console.WriteLine($"  request_accounting_complete : {s.RequestAccountingComplete}");
        Console.WriteLine($"  token_attribution_complete  : {s.TokenAttributionComplete}");
        if (s.UninstrumentedCallSites.Count > 0)
        {
            Console.WriteLine("  DECLARED BLIND SPOTS (LLM calls that emit no event — counts below are a LOWER BOUND):");
            foreach (var b in s.UninstrumentedCallSites) Console.WriteLine($"    - {b}");
        }
        if (s.Notes.Count > 0)
        {
            Console.WriteLine("  notes:");
            foreach (var n in s.Notes) Console.WriteLine($"    - {n}");
        }
        Console.WriteLine();
        Console.WriteLine($"  => {s.Describe()}");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
