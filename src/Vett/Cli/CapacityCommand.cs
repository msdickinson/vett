using Microsoft.Extensions.Logging;
using System.CommandLine;
using Vett.Capacity;
using Vett.Config;

namespace Vett.Cli;

/// <summary>
/// Operator surface for the capacity ledger.
///
/// The ledger is deliberately a set of plain files rather than a daemon, so
/// this command is the only thing standing between an operator and a directory
/// of JSON. It answers the three questions that actually get asked when a run
/// stalls: what is holding the pool (<c>ls</c>), what would THIS run get
/// (<c>plan</c>), and is the catalogue still telling the truth about the
/// hardware (<c>probe</c>).
/// </summary>
public static class CapacityCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command("capacity",
            "Inspect and plan against the shared LLM capacity ledger");

        cmd.Add(Ls());
        cmd.Add(Plan());
        cmd.Add(Probe());
        cmd.Add(Release(logger));
        cmd.Add(Spend(logger));
        return cmd;
    }

    private static Option<string> CatalogueOption() =>
        new("--catalogue") { Description = "Capability catalogue name or path (default: 'default' from capabilities/)" };

    private static (CapabilityCatalogue Catalogue, string Path)? LoadOrReport(string? name)
    {
        var (cat, path) = CatalogueLoader.Resolve(name);
        if (cat is null || path is null)
        {
            Console.Error.WriteLine(
                $"no capability catalogue named '{name ?? CatalogueLoader.DefaultName}' found. Searched:");
            foreach (var d in CatalogueLoader.SearchDirs())
                Console.Error.WriteLine($"  {d}");
            return null;
        }
        return (cat, path);
    }

    // -----------------------------------------------------------------
    // ls — who holds what
    // -----------------------------------------------------------------

    private static Command Ls()
    {
        var cmd = new Command("ls", "Show live leases and remaining tokens per provider");
        var catOpt = CatalogueOption();
        cmd.Add(catOpt);

        cmd.SetAction(parse =>
        {
            var loaded = LoadOrReport(parse.GetValue(catOpt));
            if (loaded is not { } l) return 2;

            var broker = new CapacityBroker();
            var leases = broker.Live();
            var avail = broker.BuildAvailability(l.Catalogue, leases);

            Console.WriteLine($"catalogue: {l.Path}");
            Console.WriteLine($"ledger:    {broker.Root}");
            Console.WriteLine();

            Console.WriteLine($"{"CAPABILITY",-12} {"PROVIDER",-18} {"WHERE",-6} {"WINDOW",9} {"POOL",10} {"FREE",10} {"HELD",5}");
            foreach (var cap in l.Catalogue.Capabilities)
            {
                foreach (var p in cap.Providers)
                {
                    var free = avail.For(p.Name).FreeTokens;
                    var held = leases.Count(x => x.Provider.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                    Console.WriteLine(
                        $"{cap.Name,-12} {p.Name,-18} {p.Locality.ToString().ToLowerInvariant(),-6} "
                        + $"{Show(p.ServedWindowTokens),9} {Show(p.PoolTokens),10} {Show(free),10} {held,5}");
                }
            }

            // A cloud provider's limit is money, not tokens, and this command
            // must not imply otherwise by printing a number the broker does
            // not enforce.
            Console.WriteLine();
            Console.WriteLine("'-' means unbounded here: a cloud pool is limited by spend, which this ledger does not track.");

            if (leases.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("no live leases.");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine($"{"LEASE",-10} {"OWNER",-22} {"PID",7} {"PROVIDER",-18} {"TOKENS",8} {"TRIGGER",8} {"PRI",4} {"EXPIRES",9}  NOTE");
            var now = DateTimeOffset.UtcNow;
            foreach (var x in leases.OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt))
            {
                var ttl = (int)(x.ExpiresAt - now).TotalSeconds;
                var note = x.PreemptRequested ? $"STAND DOWN: {x.PreemptReason}" : "";
                Console.WriteLine(
                    $"{Short(x.Id),-10} {Trim(x.Owner, 22),-22} {x.Pid,7} {Trim(x.Provider, 18),-18} "
                    + $"{x.Tokens,8} {x.CompactionThreshold,8} {x.Priority,4} {ttl + "s",9}  {note}");
            }
            return 0;
        });
        return cmd;
    }

    // -----------------------------------------------------------------
    // plan — what would this run get, and why
    // -----------------------------------------------------------------

    private static Command Plan()
    {
        var cmd = new Command("plan",
            "Resolve a capability request against the live ledger WITHOUT claiming anything");

        var catOpt = CatalogueOption();
        var capOpt = new Option<string>("--capability") { Description = "Capability the run needs (e.g. flash, pro)", Required = true };
        var ctxOpt = new Option<int>("--context") { Description = "Context tokens the run wants (default 32000)" };
        ctxOpt.DefaultValueFactory = _ => 32_000;
        var cloudOpt = new Option<bool>("--allow-cloud") { Description = "Permit binding to a paid cloud provider" };
        var measuredOpt = new Option<bool>("--require-measured") { Description = "Refuse providers whose served window has never been measured" };
        cmd.Add(catOpt); cmd.Add(capOpt); cmd.Add(ctxOpt); cmd.Add(cloudOpt); cmd.Add(measuredOpt);

        cmd.SetAction(parse =>
        {
            var loaded = LoadOrReport(parse.GetValue(catOpt));
            if (loaded is not { } l) return 2;

            var want = parse.GetValue(capOpt)!;
            var ctx = parse.GetValue(ctxOpt);
            var policy = new ResolvePolicy(
                AllowCloud: parse.GetValue(cloudOpt),
                RequireMeasuredWindow: parse.GetValue(measuredOpt));

            var broker = new CapacityBroker();
            var avail = broker.BuildAvailability(l.Catalogue, broker.Live());
            var res = CapabilityResolver.Resolve(l.Catalogue, new CapabilityRequest(want, ctx), policy, avail);

            // Substitutability is a property of (capability, SIZE), not of the
            // capability — flash at 32k has two doors, flash at 100k has one —
            // so it is only ever reported against the size that was asked for.
            var cap = l.Catalogue.Find(want);
            if (cap is not null)
            {
                var doors = cap.Providers.Count(p => p.CanServeWindow(ctx));
                Console.WriteLine($"'{want}' at {ctx} tokens: {doors} provider(s) can serve that window"
                    + (cap.IsSubstitutableAt(ctx) ? " — substitutable" : " — NOT substitutable at this size"));
                Console.WriteLine();
            }

            switch (res)
            {
                case Resolution.Bound b:
                    Console.WriteLine($"BOUND    {b.Provider.Name} ({b.Provider.Locality.ToString().ToLowerInvariant()})");
                    Console.WriteLine($"  endpoint            {b.Provider.Endpoint}");
                    Console.WriteLine($"  model               {b.Provider.Model}");
                    Console.WriteLine($"  granted tokens      {b.GrantedTokens}");
                    Console.WriteLine($"  compaction trigger  {b.CompactionThreshold}   (derived from the GRANT, not the server ceiling)");
                    Console.WriteLine($"  why                 {b.Rationale}");
                    return 0;

                case Resolution.Wait w:
                    // Exit 3, distinct from 4: waiting is a transient state a
                    // caller can retry, refusal never is.
                    Console.WriteLine($"WAIT     {w.Reason}");
                    Console.WriteLine($"  waiting on          {string.Join(", ", w.WaitingOn)}");
                    return 3;

                case Resolution.Denied d:
                    Console.WriteLine($"DENIED   {d.Reason}");
                    return 4;
            }
            return 1;
        });
        return cmd;
    }

    // -----------------------------------------------------------------
    // probe — is the catalogue still true?
    // -----------------------------------------------------------------

    private static Command Probe()
    {
        var cmd = new Command("probe",
            "Ask each provider what context window it actually serves, and compare with the catalogue");

        var catOpt = CatalogueOption();
        var timeoutOpt = new Option<int>("--timeout") { Description = "Seconds per endpoint (default 20)" };
        timeoutOpt.DefaultValueFactory = _ => 20;
        cmd.Add(catOpt); cmd.Add(timeoutOpt);

        cmd.SetAction(async (parse, ct) =>
        {
            var loaded = LoadOrReport(parse.GetValue(catOpt));
            if (loaded is not { } l) return 2;

            var timeout = TimeSpan.FromSeconds(parse.GetValue(timeoutOpt));
            var drift = 0;
            var unreachable = 0;

            Console.WriteLine("GET /models only — no completions, no token spend.");
            Console.WriteLine();

            foreach (var cap in l.Catalogue.Capabilities)
            {
                foreach (var p in cap.Providers)
                {
                    var (served, failure) = await EndpointProbe.ServedWindowAsync(
                        p.Endpoint, p.Model, p.ApiKeyEnv, timeout, ct);

                    if (failure is not null)
                    {
                        // Unreachable is NOT drift. A host that did not answer
                        // has told us nothing about its window, and recording
                        // that silence as a changed value is how a catalogue
                        // gets edited to match a network blip.
                        unreachable++;
                        Console.WriteLine($"  ?  {cap.Name}/{p.Name}: {failure}");
                        continue;
                    }

                    if (served is not int measured)
                    {
                        Console.WriteLine($"  ?  {cap.Name}/{p.Name}: serves '{p.Model}' but publishes no window "
                            + "(could not measure — this is not a measurement of zero)");
                        continue;
                    }

                    if (p.ServedWindowTokens is not int declared)
                    {
                        Console.WriteLine($"  +  {cap.Name}/{p.Name}: measured {measured}, catalogue says nothing "
                            + $"— consider adding 'served_window_tokens: {measured}'");
                    }
                    else if (declared != measured)
                    {
                        drift++;
                        Console.WriteLine($"  !  {cap.Name}/{p.Name}: catalogue says {declared}, host serves {measured} "
                            + "— max_model_len is a LAUNCH FLAG, so a restart can move it");
                    }
                    else
                    {
                        Console.WriteLine($"  ok {cap.Name}/{p.Name}: {measured}");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{drift} drifted, {unreachable} unreachable.");
            return drift > 0 ? 5 : 0;
        });
        return cmd;
    }

    // -----------------------------------------------------------------
    // release — the janitor
    // -----------------------------------------------------------------

    private static Command Release(ILogger logger)
    {
        var cmd = new Command("release",
            "Give capacity back: one lease by id, every lease whose holder is gone, "
            + "or a ledger lock stamped by a process that died.");

        // NOT Required any more. --lease was the only way in, which left the
        // case Mark actually asked about ("if a service never releases a lock
        // you may need a way to do that") reachable only by reading lease ids
        // out of `ls` by hand -- and unreachable entirely for a lock, whose
        // holder has no lease id at all.
        var idOpt = new Option<string>("--lease") { Description = "Lease id (the short form printed by 'capacity ls' is accepted)" };
        var staleOpt = new Option<bool>("--stale") { Description = "Reclaim every lease whose holder is dead or has stopped heartbeating" };
        var breakOpt = new Option<bool>("--break-lock") { Description = "Clear a ledger lock stamp left behind by a process that already exited" };
        cmd.Add(idOpt);
        cmd.Add(staleOpt);
        cmd.Add(breakOpt);

        cmd.SetAction(parse =>
        {
            var wanted = parse.GetValue(idOpt);
            var stale = parse.GetValue(staleOpt);
            var breakLock = parse.GetValue(breakOpt);
            var broker = new CapacityBroker();

            // An empty invocation must not read as success. `release` with no
            // arm did nothing and exited 0 -- a janitor that reports "done"
            // without sweeping is worse than one that errors.
            if (string.IsNullOrEmpty(wanted) && !stale && !breakLock)
            {
                Console.Error.WriteLine(
                    "nothing to release: pass --lease <id>, --stale, or --break-lock.");
                return 1;
            }

            var rc = 0;

            if (breakLock)
            {
                // clearStaleStamp: true -- the whole point of asking is that
                // something looks stuck. A live holder is still refused below;
                // this only ever removes a stamp whose process is gone.
                var outcome = broker.BreakLock(clearStaleStamp: true);
                switch (outcome)
                {
                    case BreakResult.NothingStuck n:
                        Console.WriteLine($"lock: {n.Detail}");
                        break;
                    case BreakResult.ClearedStaleStamp c:
                        logger.LogInformation("cleared stale ledger stamp from pid {Pid}", c.Holder.Pid);
                        Console.WriteLine($"lock: {c.Detail}");
                        break;
                    case BreakResult.CannotBreak b:
                        // rc=1: the caller asked for something that did not
                        // happen. Reporting this as success is how a stuck
                        // ledger gets mistaken for a cleared one.
                        Console.Error.WriteLine($"lock: {b.Detail}");
                        if (b.Holder is not null)
                            Console.Error.WriteLine($"      held by pid {b.Holder.Pid} ({b.Holder.Owner}) on {b.Holder.Machine} since {b.Holder.AcquiredAt:HH:mm:ss}");
                        rc = 1;
                        break;
                }
            }

            if (stale)
            {
                var reclaimed = broker.ReclaimStaleLeases();
                if (reclaimed.Count == 0)
                {
                    Console.WriteLine("stale: no leases to reclaim -- every holder is alive and heartbeating.");
                }
                else
                {
                    Console.WriteLine($"stale: reclaimed {reclaimed.Count} lease(s)");
                    var reclaimAt = DateTimeOffset.UtcNow;
                    foreach (var l in reclaimed)
                    {
                        // SAY WHICH OF THE TWO CONDITIONS FIRED. ReclaimStaleLeases
                        // takes a lease when its TTL has passed OR when its holder
                        // process is gone -- and those are different bugs to chase.
                        // This line used to print "expired {ExpiresAt}" for both,
                        // so a lease reclaimed one second after a hard kill was
                        // reported as a TTL expiry, with a timestamp in the FUTURE
                        // and no timezone marker beside local-time log stamps. A
                        // reader would go looking for a heartbeat problem that does
                        // not exist. Observed while running C6 of
                        // PREREG-2026-08-28.
                        var ttlPassed = l.ExpiresAt <= reclaimAt;
                        var why = ttlPassed
                            ? $"TTL expired {l.ExpiresAt.ToLocalTime():HH:mm:ss}"
                            : $"holder gone (TTL had {(l.ExpiresAt - reclaimAt).TotalSeconds:F0}s left)";
                        logger.LogInformation(
                            "reclaimed stale lease {Id} from {Owner} (pid {Pid}): {Why}", l.Id, l.Owner, l.Pid, why);
                        Console.WriteLine(
                            $"  {Short(l.Id)}  {l.Capability,-10} {l.Provider,-18} {l.Tokens,8} tokens  "
                            + $"pid {l.Pid} ({l.Owner}), {why}");
                    }
                }
            }

            if (string.IsNullOrEmpty(wanted))
                return rc;

            var live = broker.Live();

            var matches = live.Where(x => x.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                Console.Error.WriteLine($"no live lease matching '{wanted}'");
                return 1;
            }
            if (matches.Count > 1)
            {
                // Releasing the wrong lease hands a running job's tokens to
                // someone else, so an ambiguous prefix is refused rather than
                // resolved by picking the first.
                Console.Error.WriteLine($"'{wanted}' matches {matches.Count} leases — be more specific:");
                foreach (var m in matches) Console.Error.WriteLine($"  {m.Id}  {m.Owner}");
                return 1;
            }

            broker.Release(matches[0].Id);
            logger.LogInformation("released lease {Id} held by {Owner}", matches[0].Id, matches[0].Owner);
            Console.WriteLine($"released {Short(matches[0].Id)} ({matches[0].Tokens} tokens on {matches[0].Provider})");
            return rc;
        });
        return cmd;
    }

    private static string Show(int? v) => v is int n ? n.ToString() : "-";
    // -----------------------------------------------------------------
    // spend — the per-model dollar budgets
    // -----------------------------------------------------------------

    /// <summary>
    /// Record or show what a budget has actually spent.
    ///
    /// THIS EXISTS BECAUSE THE GATE ALREADY POINTED AT IT. A budget with a
    /// limit_usd and no reading BLOCKS its providers, and the block's message
    /// told the operator to run `vett capacity spend` -- a command that did not
    /// exist, so the only documented way out of a blocked budget was a dead
    /// end.
    ///
    /// --source IS MANDATORY AND IS NOT DECORATION. The number that gates a run
    /// has to come from a billing read. Token counts multiplied by a list price
    /// are NOT billed cost -- cache discounts alone move that figure enough to
    /// flip a budget decision -- so the source is stored beside the number and
    /// travels with it into every refusal message.
    /// </summary>
    private static Command Spend(ILogger logger)
    {
        var cmd = new Command("spend",
            "Record a billing reading against a budget, or show the current readings");

        var catOpt = CatalogueOption();
        var budgetOpt = new Option<string>("--budget") { Description = "Budget name (as written under 'budgets:' in the catalogue)" };
        var usdOpt = new Option<decimal?>("--usd") { Description = "Dollars spent so far against this budget, READ FROM BILLING" };
        var sourceOpt = new Option<string>("--source") { Description = "Where the figure came from, e.g. 'openrouter total_usage 2026-08-28T21:00Z'" };
        cmd.Add(catOpt); cmd.Add(budgetOpt); cmd.Add(usdOpt); cmd.Add(sourceOpt);

        cmd.SetAction(parse =>
        {
            var loaded = LoadOrReport(parse.GetValue(catOpt));
            if (loaded is null) return 1;
            var (catalogue, path) = loaded.Value;

            var broker = new CapacityBroker();
            var budget = parse.GetValue(budgetOpt);
            var usd = parse.GetValue(usdOpt);
            var source = parse.GetValue(sourceOpt);

            // Read-only view when nothing is being written.
            if (string.IsNullOrEmpty(budget) && usd is null && string.IsNullOrEmpty(source))
            {
                var readings = broker.Spend();
                Console.WriteLine($"catalogue: {path}");
                Console.WriteLine($"ledger:    {broker.Root}");
                Console.WriteLine();
                Console.WriteLine($"{"BUDGET",-16} {"LIMIT",10} {"READ",10} {"AS OF",-22} SOURCE");

                foreach (var b in catalogue.Budgets)
                {
                    var r = readings.FirstOrDefault(
                        x => x.Budget.Equals(b.Name, StringComparison.OrdinalIgnoreCase));
                    var limit = b.LimitUsd is decimal l ? $"${l}" : "-";
                    Console.WriteLine(
                        $"{b.Name,-16} {limit,10} "
                        + $"{(r is null ? "(none)" : "$" + r.Usd),10} "
                        + $"{(r is null ? "-" : r.AsOf.ToString("u")),-22} "
                        + (r?.Source ?? "-"));
                }

                // A poison reading matches every budget and would not show up
                // under any single name above. Saying nothing about it would
                // let an unreadable ledger look like a healthy one.
                var poison = readings.FirstOrDefault(x => x.Budget == CapacityBroker.PoisonBudget);
                if (poison is not null)
                {
                    Console.WriteLine();
                    Console.Error.WriteLine(
                        $"the spend ledger is unreadable ({poison.Source}); EVERY budgeted provider is "
                        + "blocked until it is repaired or removed.");
                    return 1;
                }

                if (catalogue.Budgets.Count == 0)
                    Console.WriteLine("(this catalogue declares no budgets, so nothing is capped by spend)");
                return 0;
            }

            // Writing: all three are required together. A partial write is a
            // reading nobody can attribute.
            if (string.IsNullOrEmpty(budget) || usd is null || string.IsNullOrEmpty(source))
            {
                Console.Error.WriteLine(
                    "recording a reading needs all three: --budget <name> --usd <amount> --source <where>. "
                    + "The source is required because an unsourced number cannot gate a run.");
                return 1;
            }

            // Refuse a name the catalogue does not know. A typo would otherwise
            // write a reading that gates NOTHING while looking like it gated
            // something -- and the real budget would stay blocked.
            if (catalogue.FindBudget(budget) is null)
            {
                Console.Error.WriteLine(
                    $"no budget named '{budget}' in {path}. Known: "
                    + (catalogue.Budgets.Count == 0
                        ? "(none)"
                        : string.Join(", ", catalogue.Budgets.Select(b => b.Name))));
                return 1;
            }

            try
            {
                broker.RecordSpend(budget, usd.Value, source);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            logger.LogInformation("recorded ${Usd} against budget {Budget} from {Source}", usd, budget, source);

            var cap = catalogue.FindBudget(budget)!.LimitUsd;
            Console.WriteLine(
                cap is decimal limitUsd
                    ? $"recorded ${usd} of ${limitUsd} against '{budget}' (source: {source})"
                      + (usd.Value >= limitUsd ? " — this budget is now SPENT and its providers are blocked." : "")
                    : $"recorded ${usd} against '{budget}' (no limit_usd, so nothing is capped by it)");
            return 0;
        });
        return cmd;
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
