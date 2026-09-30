using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vett.Capacity;

/// <summary>
/// Scheduling priority. Higher wins. Named rather than free-form so the
/// comparison is total and the ordering is reviewable.
/// </summary>
public static class Priority
{
    /// <summary>A human is sitting there waiting (vett-chat).</summary>
    public const int Interactive = 100;

    /// <summary>A normal automated run.</summary>
    public const int Batch = 50;

    /// <summary>Sweeps and backfills. Yields to everything.</summary>
    public const int Background = 10;
}

/// <summary>One live reservation against a provider's pool.</summary>
public sealed record Lease
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("owner")] public string Owner { get; init; } = "";
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("capability")] public string Capability { get; init; } = "";
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("tokens")] public int Tokens { get; init; }
    [JsonPropertyName("compaction_threshold")] public int CompactionThreshold { get; init; }
    [JsonPropertyName("priority")] public int Priority { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Set by the broker when a higher-priority claim needs this lease's
    /// tokens. It is a REQUEST, not a revocation — see
    /// <see cref="CapacityBroker"/> for why nothing here can force a holder
    /// to stop.
    /// </summary>
    [JsonPropertyName("preempt_requested")] public bool PreemptRequested { get; init; }

    [JsonPropertyName("preempt_reason")] public string? PreemptReason { get; init; }
}

/// <summary>Outcome of a claim attempt.</summary>
public abstract record ClaimResult
{
    public sealed record Granted(Lease Lease, Resolution.Bound Binding) : ClaimResult;

    /// <summary>
    /// Not now, but retrying can work. PreemptedLeases lists
    /// leases the broker asked to stand down on this claim's behalf.
    /// </summary>
    public sealed record Queued(
        string Reason,
        IReadOnlyList<string> WaitingOn,
        IReadOnlyList<string> PreemptedLeases) : ClaimResult;

    /// <summary>Retrying cannot work under this policy.</summary>
    public sealed record Refused(string Reason) : ClaimResult;
}

/// <summary>
/// Cross-process reservation ledger for model capacity.
///
/// WHY A LEDGER AND NOT A SEMAPHORE. Every concurrency gate in vett today is
/// a <c>SemaphoreSlim</c> (Coordinator.cs:694, Runner.cs:124,
/// BenchCommand.cs:395), which is in-process only. Two vett runs — say a
/// vett-chat session and a bench sweep — cannot see each other at all, so
/// they oversubscribe the same GPU without either one being able to notice.
/// Coordination therefore has to live OUTSIDE the process, and this is the
/// smallest thing that does: a directory of lease files guarded by a lock
/// file. No daemon to deploy, no port to own, and it survives a client dying
/// because leases expire.
///
/// WHY THE SERVER CANNOT DO THIS FOR US. vLLM runs with
/// <c>--no-scheduler-reserve-full-isl</c>, so it never refuses work: when the
/// KV pool runs dry it PREEMPTS (evict + recompute). Oversubscription shows
/// up as latency collapse rather than an error, so there is no failure to
/// catch and back off from. Worse, /metrics labels every request
/// <c>model_name="aeon-mtp"</c> regardless of which alias the client asked
/// for, so server-side counters cannot attribute usage to a client either.
/// Reservations have to be DECLARED by clients; they cannot be inferred.
///
/// WHAT PREEMPTION HONESTLY MEANS HERE. A run in the middle of an LLM call
/// cannot be paused mid-request. <see cref="Lease.PreemptRequested"/> is a
/// cooperative signal: the broker MARKS the victim's lease, and
/// <see cref="Heartbeat"/> hands that mark back to any holder that looks.
/// Nothing in this class can force a holder to release, and it does not
/// pretend to.
///
/// ⛔ AND AS OF 2026-08-28 NOTHING LOOKS. This doc used to say the holder
/// "observes on its next heartbeat and acts at its next seat admission".
/// The first half is available; the second half is NOT BUILT. The only
/// production caller of <see cref="Heartbeat"/> is the renewal timer in
/// <c>LeasedChatClient</c>, and it DISCARDS the returned lease.
/// <c>PreemptRequested</c> has zero readers outside this class and
/// `vett capacity ls`. CAPTURED IS NOT GATED ON.
///
/// So what priority buys TODAY is: correct victim SELECTION and ordering
/// (unit-tested), plus a STAND DOWN note a human can see in
/// `vett capacity ls`. It does NOT yield a seat on its own. Wiring the
/// consumer needs a policy decision first — whether a marked run should
/// finish its turn and release, abandon, or finish the whole run — and that
/// is a product call, not an implementation detail.
/// </summary>
public sealed class CapacityBroker
{
    private readonly string _root;
    private readonly string _leaseDir;
    private readonly string _lockFile;
    private readonly string _spendFile;
    private readonly string _holderFile;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int, bool> _pidAlive;

    /// <summary>How long a lease survives without a heartbeat.</summary>
    public static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <param name="root">
    /// Ledger directory. Defaults to VETT_CAPACITY_DIR, else ~/.vett/capacity.
    /// </param>
    /// <param name="now">Clock seam, so expiry is testable without sleeping.</param>
    /// <param name="pidAlive">
    /// Liveness seam. Default asks the OS. Injectable because "the process
    /// that held this lease is gone" is exactly the case that is impossible
    /// to arrange reliably in a test otherwise.
    /// </param>
    public CapacityBroker(
        string? root = null,
        Func<DateTimeOffset>? now = null,
        Func<int, bool>? pidAlive = null)
    {
        _root = root
                ?? Environment.GetEnvironmentVariable("VETT_CAPACITY_DIR")
                ?? Path.Combine(
                    Environment.GetEnvironmentVariable("HOME")
                        ?? Environment.GetEnvironmentVariable("USERPROFILE")
                        ?? ".",
                    ".vett", "capacity");

        _leaseDir = Path.Combine(_root, "leases");
        _lockFile = Path.Combine(_root, ".lock");
        _spendFile = Path.Combine(_root, "spend.json");
        _holderFile = Path.Combine(_root, ".lock.holder");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pidAlive = pidAlive ?? DefaultPidAlive;

        Directory.CreateDirectory(_leaseDir);
    }

    /// <summary>Ledger directory, so operators can be told where to look.</summary>
    public string Root => _root;

    private static bool DefaultPidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempt to reserve capacity. Atomic with respect to other processes:
    /// the read-decide-write sequence happens under an exclusive file lock,
    /// so two simultaneous claimants cannot both see the same free tokens
    /// and both take them.
    /// </summary>
    public ClaimResult Claim(
        CapabilityCatalogue catalogue,
        CapabilityRequest request,
        ResolvePolicy policy,
        int priority,
        string owner,
        TimeSpan? ttl = null)
    {
        using var _ = AcquireLock();

        var live = ReadLiveLeases();
        var availability = BuildAvailability(catalogue, live);
        var resolution = CapabilityResolver.Resolve(catalogue, request, policy, availability);

        switch (resolution)
        {
            case Resolution.Bound bound:
            {
                var lease = new Lease
                {
                    Id = Guid.NewGuid().ToString("n")[..12],
                    Owner = owner,
                    Pid = Environment.ProcessId,
                    Capability = request.Capability,
                    Provider = bound.Provider.Name,
                    Tokens = bound.GrantedTokens,
                    CompactionThreshold = bound.CompactionThreshold,
                    Priority = priority,
                    CreatedAt = _now(),
                    ExpiresAt = _now() + (ttl ?? DefaultLeaseTtl),
                };
                WriteLease(lease);
                return new ClaimResult.Granted(lease, bound);
            }

            case Resolution.Denied denied:
                return new ClaimResult.Refused(denied.Reason);

            case Resolution.Wait wait:
            {
                // Only now consider taking capacity from someone else. Asking
                // first and preempting second matters: a claim that fits in
                // free space must never disturb a running job.
                var preempted = RequestPreemption(live, wait, request, priority, owner);
                return new ClaimResult.Queued(wait.Reason, wait.WaitingOn, preempted);
            }

            default:
                return new ClaimResult.Refused($"unhandled resolution {resolution.GetType().Name}");
        }
    }

    /// <summary>
    /// Ask lower-priority holders on the contended providers to stand down,
    /// lowest priority first and newest first within a priority, until
    /// enough tokens would be freed to satisfy the request.
    ///
    /// Newest-first within a tier is deliberate: it preserves the work that
    /// has already spent the most, which is both cheaper and fairer than
    /// killing the run that is nearly done.
    ///
    /// Returns the lease ids actually asked to yield. An empty list means
    /// there was nothing outranked to take from — the claim simply waits.
    /// </summary>
    private List<string> RequestPreemption(
        List<Lease> live,
        Resolution.Wait wait,
        CapabilityRequest request,
        int priority,
        string owner)
    {
        var preempted = new List<string>();

        foreach (var providerName in wait.WaitingOn)
        {
            var candidates = live
                .Where(l => l.Provider == providerName)
                .Where(l => l.Priority < priority)
                .Where(l => !l.PreemptRequested)
                .OrderBy(l => l.Priority)
                .ThenByDescending(l => l.CreatedAt)
                .ToList();

            var freed = 0;
            foreach (var victim in candidates)
            {
                if (freed >= request.ContextTokens) break;

                var marked = victim with
                {
                    PreemptRequested = true,
                    PreemptReason =
                        $"'{owner}' (priority {priority}) needs {request.ContextTokens} tokens of "
                        + $"{request.Capability} on {providerName}; this lease is priority {victim.Priority}",
                };
                WriteLease(marked);
                preempted.Add(victim.Id);
                freed += victim.Tokens;
            }
        }

        return preempted;
    }

    /// <summary>
    /// Extend a lease, and report back whether the broker has asked it to
    /// stand down. Returns null when the lease no longer exists — which a
    /// caller must treat as "you no longer hold capacity", not as "fine".
    /// </summary>
    public Lease? Heartbeat(string leaseId, TimeSpan? ttl = null)
    {
        using var _ = AcquireLock();

        var path = LeasePath(leaseId);
        if (!File.Exists(path)) return null;

        var lease = ReadLease(path);
        if (lease is null) return null;

        var renewed = lease with { ExpiresAt = _now() + (ttl ?? DefaultLeaseTtl) };
        WriteLease(renewed);
        return renewed;
    }

    /// <summary>Give the capacity back. Idempotent.</summary>
    public void Release(string leaseId)
    {
        using var _ = AcquireLock();
        var path = LeasePath(leaseId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Diagnose the ledger without changing it: is the lock held, by whom, for
    /// how long, is that process still alive, and which leases are about to be
    /// reclaimed out from under their holders.
    ///
    /// The stamp is never trusted on its own. A process that dies holding the
    /// lock has its handle released by the OS but leaves its stamp behind, so
    /// the stamp alone would report a holder that holds nothing. This probes
    /// the real lock and uses the stamp only as corroboration.
    ///
    /// Deliberately a SNAPSHOT, not a guarantee: the probe can land in the gap
    /// between two acquisitions, so "not held" means "not held at that
    /// instant". It is a diagnostic and says so rather than implying more.
    /// </summary>
    public LedgerStatus Inspect(TimeSpan? probe = null)
    {
        var holder = ReadHolder();
        var notes = new List<string>();

        bool held;
        try
        {
            using (AcquireLockRaw(probe ?? TimeSpan.FromMilliseconds(250))) { }
            held = false;
        }
        catch (TimeoutException)
        {
            held = true;
        }

        bool? holderAlive = holder is null ? null : _pidAlive(holder.Pid);
        TimeSpan? heldFor = holder is null ? null : _now() - holder.AcquiredAt;

        if (!held && holder is not null)
        {
            notes.Add(
                $"a holder stamp names pid {holder.Pid} but the lock is NOT held. The stamp is stale — "
                + "either that process died (the OS released its handle) or it released between the read "
                + "and the probe. Nothing is stuck.");
        }

        if (held && holderAlive == false)
        {
            notes.Add(
                $"the lock IS held, but its stamp names pid {holder!.Pid}, which is gone. Something is "
                + "holding the ledger without stamping it, so the holder cannot be attributed from here.");
        }

        if (held && holderAlive == true && heldFor > TimeSpan.FromMinutes(1))
        {
            notes.Add(
                $"pid {holder!.Pid} has held the ledger lock for {heldFor!.Value.TotalSeconds:F0}s. Every "
                + "operation here is a short read-modify-write, so this is a wedged process rather than "
                + "slow work. A live holder's lock cannot be taken on Windows — stop that pid to clear it.");
        }

        // Leases past TTL or owned by a dead process are ALREADY invisible to
        // BuildAvailability, so their capacity is being handed out again. That
        // is correct, and it is also exactly the silent step worth surfacing.
        //
        // Read WITHOUT the stamping lock and WITHOUT Live(). Live() takes
        // AcquireLock (which writes then deletes a stamp, destroying the very
        // stale stamp being diagnosed) and sweeps expired leases off disk. Both
        // are mutations, and this method promises not to make any. Lease files
        // are written by atomic rename, so each one reads consistently on its
        // own.
        var now = _now();
        var all = ReadAllLeases();
        var stale = all.Where(l => l.ExpiresAt <= now || !_pidAlive(l.Pid)).ToList();
        var alive = all.Where(l => l.ExpiresAt > now && _pidAlive(l.Pid)).ToList();

        foreach (var l in stale)
        {
            var why = !_pidAlive(l.Pid)
                ? $"owner pid {l.Pid} is gone"
                : $"no heartbeat since {l.ExpiresAt:u}";
            notes.Add($"lease {l.Id[..Math.Min(8, l.Id.Length)]} ({l.Owner}, {l.Tokens} tokens on "
                      + $"{l.Provider}) is reclaimable: {why}.");
        }

        return new LedgerStatus(held, holder, holderAlive, heldFor, alive, stale, notes);
    }

    /// <summary>
    /// Drop leases whose holder is gone or which have stopped heartbeating,
    /// and report exactly what was dropped.
    ///
    /// This mostly makes an EXISTING behaviour visible rather than adding one:
    /// such leases are already skipped when free capacity is computed. The
    /// value is the report — a reclaimed lease means someone's reservation was
    /// taken away, and that should never happen silently.
    /// </summary>
    public IReadOnlyList<Lease> ReclaimStaleLeases()
    {
        using var _ = AcquireLock();

        var now = _now();
        var reclaimed = new List<Lease>();

        foreach (var lease in ReadAllLeases())
        {
            if (lease.ExpiresAt > now && _pidAlive(lease.Pid)) continue;
            var path = LeasePath(lease.Id);
            if (File.Exists(path)) File.Delete(path);
            reclaimed.Add(lease);
        }

        return reclaimed;
    }

    /// <summary>
    /// Deal with a lock nobody is releasing.
    ///
    /// WHAT THIS CANNOT DO. On Windows a handle opened with FileShare.None
    /// cannot be taken, renamed around, or deleted by another process while its
    /// holder lives. There is no honest "force break" against a live holder, so
    /// this does not offer one — it names the pid and stops. Killing that
    /// process is a human decision about a specific tracked pid.
    ///
    /// What it DOES do is clear the one artefact vett owns: a stale holder
    /// stamp left by a process that already died. That is the case which makes
    /// a perfectly healthy ledger LOOK stuck.
    /// </summary>
    public BreakResult BreakLock(bool clearStaleStamp = false)
    {
        var status = Inspect();

        if (!status.LockHeld)
        {
            if (status.Holder is not null && clearStaleStamp)
            {
                try { File.Delete(_holderFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return new BreakResult.ClearedStaleStamp(status.Holder,
                    $"the lock was not held; cleared the stale stamp left by pid {status.Holder.Pid}.");
            }

            return new BreakResult.NothingStuck(
                "the ledger lock is free. Nothing needed breaking."
                + (status.Holder is not null
                    ? $" (A stale stamp from pid {status.Holder.Pid} is on disk; pass --clear-stale-stamp to remove it.)"
                    : ""));
        }

        var who = status.Holder is null
            ? "an unstamped process"
            : $"pid {status.Holder.Pid} on {status.Holder.Machine}"
              + (status.HeldFor is { } h ? $", held for {h.TotalSeconds:F0}s" : "");

        return new BreakResult.CannotBreak(status.Holder,
            $"the ledger lock is held by {who}. A live holder's lock cannot be broken from another "
            + "process — stop that pid and the OS releases it immediately. Nothing is permanently "
            + "blocked meanwhile: every acquisition times out rather than waiting forever, and leases "
            + "expire on their own once heartbeats stop.");
    }

    /// <summary>Every lease on disk, sweeping nothing. For diagnosis only.</summary>
    private IReadOnlyList<Lease> ReadAllLeases()
    {
        var all = new List<Lease>();
        if (!Directory.Exists(_leaseDir)) return all;

        foreach (var file in Directory.EnumerateFiles(_leaseDir, "*.json"))
        {
            if (ReadLease(file) is { } lease) all.Add(lease);
        }
        return all;
    }

    /// <summary>Every lease currently held, after sweeping dead ones.</summary>
    public IReadOnlyList<Lease> Live()
    {
        using var _ = AcquireLock();
        return ReadLiveLeases();
    }

    /// <summary>
    /// Free tokens per provider given what is currently committed. Cloud
    /// providers report null (unbounded): their limit is money, and money is
    /// not tracked here — a token count would silently imply a spend cap
    /// this class does not enforce.
    /// </summary>
    public Availability BuildAvailability(CapabilityCatalogue catalogue, IReadOnlyList<Lease> live)
    {
        var committed = live
            .GroupBy(l => l.Provider)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Tokens));

        var spend = ReadSpend();
        var map = new Dictionary<string, ProviderAvailability>();

        foreach (var provider in catalogue.Capabilities.SelectMany(c => c.Providers))
        {
            if (map.ContainsKey(provider.Name)) continue;

            // Money is checked BEFORE tokens. A provider whose budget is spent
            // is shut regardless of how much context is free on it, and
            // reporting free tokens on a wallet that cannot pay would be a
            // confident, wrong "yes".
            if (BudgetBlock(catalogue, provider, spend) is { } why)
            {
                map[provider.Name] = new ProviderAvailability(BlockedReason: why);
                continue;
            }

            if (provider.PoolTokens is not int pool)
            {
                map[provider.Name] = new ProviderAvailability();
                continue;
            }

            var used = committed.TryGetValue(provider.Name, out var u) ? u : 0;
            map[provider.Name] = new ProviderAvailability(FreeTokens: Math.Max(0, pool - used));
        }

        return new Availability(map);
    }

    /// <summary>
    /// Why this provider's wallet is shut, or null if it is open.
    ///
    /// The gate FAILS CLOSED, but only once an operator has asked for it: a
    /// provider whose budget declares no limit_usd is never blocked here, so a
    /// catalogue that says nothing about money behaves exactly as it did before
    /// budgets existed. Once a limit IS declared, a missing or stale reading
    /// blocks — a spend gate that admits work when it cannot see the balance is
    /// not a spend gate, and a stale reading errs in the permissive direction,
    /// which is the direction that spends money nobody approved.
    ///
    /// Every block here is TRANSIENT: it becomes a Wait, never a Denied. A
    /// budget can be topped up or a fresh reading taken, so refusing outright
    /// would destroy a run over a condition that clears on its own.
    /// </summary>
    private string? BudgetBlock(
        CapabilityCatalogue catalogue, CapabilityProvider provider, IReadOnlyList<SpendRecord> spend)
    {
        var budget = catalogue.FindBudget(provider.BudgetName);
        if (budget?.LimitUsd is not decimal limit) return null;

        var reading = spend.FirstOrDefault(
            r => r.Budget.Equals(budget.Name, StringComparison.OrdinalIgnoreCase)
                 || r.Budget == PoisonBudget);

        if (reading is not null && reading.Budget == PoisonBudget)
        {
            // Distinct from the "no reading" case below, and deliberately so. A
            // mutation test caught the two being indistinguishable: both blocked,
            // so a test asserting only "something blocked" stayed green after the
            // poison record was deleted. They are different situations — one is a
            // budget nobody has read yet, the other is a ledger that has been
            // written and can no longer be parsed — and the second needs fixing.
            return $"budget '{budget.Name}' cannot be evaluated: {reading.Source}. "
                 + $"The spend ledger at {_spendFile} exists but could not be read, which is NOT the same as "
                 + "nothing having been spent, so every budgeted provider is held until it is repaired or removed.";
        }

        if (reading is null)
        {
            return $"budget '{budget.Name}' caps spend at ${limit} but no billing reading has been recorded. "
                 + $"Run: vett capacity spend --budget {budget.Name} --usd <amount> --source <where>. "
                 + "This is never estimated from token counts, because logged tokens times a list price "
                 + "is not billed cost.";
        }

        var age = _now() - reading.AsOf;
        if (age > TimeSpan.FromHours(budget.StaleAfterHours))
        {
            return $"budget '{budget.Name}': the last billing reading (${reading.Usd} from {reading.Source}) "
                 + $"is {age.TotalHours:F1}h old, past this budget's {budget.StaleAfterHours}h staleness "
                 + "limit. A stale reading understates spend, so it is treated as no reading at all.";
        }

        if (reading.Usd >= limit)
        {
            return $"budget '{budget.Name}' is spent: ${reading.Usd} of ${limit} as of "
                 + $"{reading.AsOf:u} ({reading.Source}).";
        }

        return null;
    }

    /// <summary>
    /// Budget name of the poison reading written when the ledger cannot be parsed.
    /// It matches every budget, so one unreadable file holds all of them.
    /// </summary>
    public const string PoisonBudget = "*";

    /// <summary>Every recorded spend reading.</summary>
    public IReadOnlyList<SpendRecord> Spend() => ReadSpend();

    /// <summary>
    /// Record what a budget has actually spent, as read from a billing source.
    ///
    /// The source is required and stored. It is the only thing distinguishing a
    /// figure pulled from a billing endpoint from one somebody inferred, and
    /// only the former may gate a run.
    /// </summary>
    public void RecordSpend(string budget, decimal usd, string source)
    {
        if (string.IsNullOrWhiteSpace(budget))
            throw new ArgumentException("a spend reading must name its budget", nameof(budget));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException(
                "a spend reading must name where the figure came from — an unsourced number cannot gate a run",
                nameof(source));
        if (usd < 0)
            throw new ArgumentException("a spend reading cannot be negative", nameof(usd));

        using var _ = AcquireLock();
        var all = ReadSpend()
            .Where(r => !r.Budget.Equals(budget, StringComparison.OrdinalIgnoreCase))
            .ToList();
        all.Add(new SpendRecord(budget, usd, _now(), source));

        var tmp = _spendFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOpts));
        File.Move(tmp, _spendFile, overwrite: true);
    }

    private IReadOnlyList<SpendRecord> ReadSpend()
    {
        if (!File.Exists(_spendFile)) return Array.Empty<SpendRecord>();
        try
        {
            return JsonSerializer.Deserialize<List<SpendRecord>>(File.ReadAllText(_spendFile))
                   ?? (IReadOnlyList<SpendRecord>)Array.Empty<SpendRecord>();
        }
        catch (JsonException)
        {
            // An unreadable spend file must not read as "nothing spent". Return
            // a poison reading, matched by every budget, so budgeted providers
            // block rather than silently reverting to unlimited.
            return new[] { new SpendRecord(PoisonBudget, decimal.MaxValue, _now(), "the spend ledger is unreadable") };
        }
    }

    // ---------------- storage ----------------

    private string LeasePath(string id) => Path.Combine(_leaseDir, id + ".json");

    private void WriteLease(Lease lease)
    {
        // Write-then-rename so a reader never observes a half-written lease.
        var final = LeasePath(lease.Id);
        var tmp = final + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(lease, JsonOpts));
        File.Move(tmp, final, overwrite: true);
    }

    private static Lease? ReadLease(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Lease>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every lease that still counts, deleting those that do not.
    ///
    /// Two independent reasons a lease stops counting, and both are needed:
    /// expiry catches a holder that hung without dying, and a dead PID
    /// catches a holder that died without releasing. Relying on expiry alone
    /// would hold a crashed session's tokens hostage for the whole TTL.
    /// </summary>
    private List<Lease> ReadLiveLeases()
    {
        var live = new List<Lease>();
        var now = _now();

        foreach (var path in Directory.EnumerateFiles(_leaseDir, "*.json"))
        {
            var lease = ReadLease(path);
            if (lease is null) { TryDelete(path); continue; }

            var expired = lease.ExpiresAt <= now;
            var ownerGone = !_pidAlive(lease.Pid);

            if (expired || ownerGone) { TryDelete(path); continue; }

            live.Add(lease);
        }

        return live;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* another sweeper won */ }
    }

    /// <summary>
    /// Exclusive cross-process lock over the read-decide-write section.
    /// FileShare.None is the portable primitive here — it behaves the same on
    /// Windows and Linux, unlike advisory locks.
    ///
    /// Two details are load-bearing, and both were found by a contended test
    /// rather than by reading the code:
    ///
    /// 1. NO FileOptions.DeleteOnClose. It looks tidy — the lock file removes
    ///    itself — but on Windows a handle closed with DeleteOnClose leaves the
    ///    file in a DELETE-PENDING state, and an opener arriving in that window
    ///    is rejected with ERROR_ACCESS_DENIED. That is the one moment the lock
    ///    is most likely to be contended, so the tidy version failed exactly
    ///    when it mattered. The file is zero bytes; leaving it costs nothing.
    ///
    /// 2. UnauthorizedAccessException is retried, not propagated. It is not
    ///    only the delete-pending case: a virus scanner or indexer holding the
    ///    file for a few milliseconds surfaces the same way. Neither is a
    ///    permission problem, and neither should abort a claim — but a genuine
    ///    permission failure will still surface, as a TimeoutException naming
    ///    the path, once the deadline passes.
    /// </summary>
    /// <summary>
    /// Take the lock AND record who took it, so a stuck ledger can be
    /// diagnosed by a human instead of guessed at. The stamp is advisory: the
    /// OS handle is the real lock, and the stamp can outlive it if a process
    /// dies (see <see cref="Inspect"/>, which never trusts it alone).
    /// </summary>
    internal IDisposable AcquireLock(TimeSpan? timeout = null)
    {
        var handle = AcquireLockRaw(timeout);
        WriteHolderStamp();
        return new StampedLock(handle, _holderFile);
    }

    /// <summary>
    /// Releases the OS handle and clears the stamp. The stamp goes FIRST: a
    /// stamp with no handle behind it reads as "stale" and is harmless, while
    /// releasing the handle first would let the next holder stamp and then have
    /// this one delete that fresh stamp.
    /// </summary>
    private sealed class StampedLock(IDisposable handle, string holderFile) : IDisposable
    {
        public void Dispose()
        {
            try { if (File.Exists(holderFile)) File.Delete(holderFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            handle.Dispose();
        }
    }

    private void WriteHolderStamp()
    {
        try
        {
            var holder = new LockHolder
            {
                Pid = Environment.ProcessId,
                Owner = Environment.GetEnvironmentVariable("VETT_OWNER") ?? "",
                Machine = Environment.MachineName,
                AcquiredAt = _now(),
            };
            File.WriteAllText(_holderFile, JsonSerializer.Serialize(holder, JsonOpts));
        }
        catch (IOException) { /* observability only; never fail a claim over it */ }
        catch (UnauthorizedAccessException) { }
    }

    private LockHolder? ReadHolder()
    {
        try
        {
            return File.Exists(_holderFile)
                ? JsonSerializer.Deserialize<LockHolder>(File.ReadAllText(_holderFile))
                : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    internal IDisposable AcquireLockRaw(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var delayMs = 5;
        Exception? last = null;

        while (true)
        {
            try
            {
                return new FileStream(_lockFile, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"could not acquire the capacity ledger lock at {_lockFile} within the timeout; "
                        + "another vett process is holding it, or the path is not writable "
                        + $"({last.GetType().Name}: {last.Message})", last);
                }
                Thread.Sleep(delayMs);
                delayMs = Math.Min(100, delayMs * 2);
            }
        }
    }
}
