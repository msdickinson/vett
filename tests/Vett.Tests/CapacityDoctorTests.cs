using Vett.Capacity;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// The "nobody released it" case: a way to SEE who holds the ledger, and a way
/// to act when the answer is "a process that is gone".
///
/// The honest shape of this problem on Windows is worth stating up front,
/// because it decides what these tests can assert at all:
///
///  * A process that DIES holding the lock releases it automatically — the OS
///    closes the handle. There is nothing to break. What it leaves behind is a
///    stale STAMP, which makes a healthy ledger look stuck.
///  * A process that is ALIVE but wedged cannot have its lock taken by anyone.
///    No file operation can do it. The only remedy is stopping that pid, which
///    is a human decision about a specific tracked process.
///
/// So the deliverable is diagnosis plus the one safe repair, and the tests
/// below pin exactly that — including that the tool refuses to claim a power it
/// does not have.
/// </summary>
public class CapacityDoctorTests : IDisposable
{
    private readonly string _dir;

    public CapacityDoctorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-doctor-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const int DeadPid = 999_999;

    private CapacityBroker Broker(Func<DateTimeOffset>? now = null) =>
        new(_dir, now, pid => pid != DeadPid);

    private string StampPath => Path.Combine(_dir, ".lock.holder");

    private void WriteGhostStamp() => File.WriteAllText(StampPath,
        "{\"pid\":" + DeadPid + ",\"owner\":\"ghost\",\"machine\":\"runner\","
        + "\"acquired_at\":\"2026-08-28T00:00:00+00:00\"}");

    private static CapabilityCatalogue Catalogue() => new()
    {
        Capabilities =
        {
            new Capability
            {
                Name = "flash",
                Providers =
                {
                    new CapabilityProvider
                    {
                        Name = "local-gpu-1", LocalityRaw = "local",
                        Endpoint = "http://gpu-1:8000/v1", Model = "deepseek-v4-flash",
                        ServedWindowTokens = 65_536, PoolTokens = 100_000, CompactionRatio = 0.75,
                    },
                },
            },
        },
    };

    private static readonly ResolvePolicy LocalOnly = new(AllowCloud: false);

    // ---------------------------------------------------------------
    // Who holds it.
    // ---------------------------------------------------------------

    [Fact]
    public void WhileHeldTheLedgerNamesItsHolder()
    {
        var broker = Broker();
        using (broker.AcquireLock())
        {
            var status = broker.Inspect();

            Assert.True(status.LockHeld);
            Assert.NotNull(status.Holder);
            Assert.Equal(Environment.ProcessId, status.Holder!.Pid);
            Assert.True(status.HolderAlive);
        }
    }

    [Fact]
    public void ReleasingTheLockAlsoClearsItsStamp()
    {
        var broker = Broker();
        using (broker.AcquireLock()) { }

        var status = broker.Inspect();
        Assert.False(status.LockHeld);
        Assert.Null(status.Holder);
    }

    [Fact]
    public void AStampLeftByADeadProcessIsReportedAsStaleNotAsAHolder()
    {
        // The case that makes a healthy ledger look stuck. The OS already
        // released the handle when the process died; only the stamp remains.
        WriteGhostStamp();

        var status = Broker().Inspect();

        Assert.False(status.LockHeld);
        Assert.Equal(DeadPid, status.Holder!.Pid);
        Assert.False(status.HolderAlive);
        Assert.Contains(status.Notes, n => n.Contains("stale") && n.Contains("Nothing is stuck"));
    }

    [Fact]
    public void InspectingDoesNotChangeAnything()
    {
        // A diagnostic that mutates cannot be run twice and believed. This
        // caught a real defect: Inspect originally called Live(), which takes
        // the stamping lock — destroying the stale stamp it was reporting — and
        // sweeps expired leases off disk on the way past.
        WriteGhostStamp();
        var broker = Broker();
        broker.Claim(Catalogue(), new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "run");

        // Claim() takes and releases the stamping lock, so re-ghost afterwards.
        WriteGhostStamp();

        var first = broker.Inspect();
        var second = broker.Inspect();

        Assert.True(File.Exists(StampPath));
        Assert.Equal(first.Holder!.Pid, second.Holder!.Pid);
        Assert.Equal(first.Leases.Count, second.Leases.Count);
        Assert.False(second.LockHeld);
    }

    // ---------------------------------------------------------------
    // Breaking it — and refusing to.
    // ---------------------------------------------------------------

    [Fact]
    public void BreakingAFreeLockReportsThatNothingWasStuck()
    {
        var r = Assert.IsType<BreakResult.NothingStuck>(Broker().BreakLock());
        Assert.Contains("Nothing needed breaking", r.Detail);
    }

    [Fact]
    public void BreakingClearsAStaleStampOnlyWhenAsked()
    {
        WriteGhostStamp();
        var broker = Broker();

        // Not asked: reported, not removed. Tidying up state nobody asked about
        // is how evidence disappears before anyone has looked at it.
        var seen = Assert.IsType<BreakResult.NothingStuck>(broker.BreakLock());
        Assert.Contains("stale stamp", seen.Detail);
        Assert.True(File.Exists(StampPath));

        var cleared = Assert.IsType<BreakResult.ClearedStaleStamp>(broker.BreakLock(clearStaleStamp: true));
        Assert.Equal(DeadPid, cleared.Holder.Pid);
        Assert.False(File.Exists(StampPath));
    }

    [Fact]
    public void ALiveHoldersLockIsNotBrokenAndTheToolSaysSoPlainly()
    {
        var broker = Broker();
        using var held = broker.AcquireLock();

        var r = Assert.IsType<BreakResult.CannotBreak>(broker.BreakLock(clearStaleStamp: true));

        Assert.Equal(Environment.ProcessId, r.Holder!.Pid);
        Assert.Contains("cannot be broken", r.Detail);
        Assert.Contains("pid " + Environment.ProcessId, r.Detail);

        // It must also say why this is survivable, or an operator reads it as a
        // dead end and starts killing things.
        Assert.Contains("times out", r.Detail);
    }

    [Fact]
    public void AskingToClearAStampDoesNotDeleteALiveHoldersStamp()
    {
        var broker = Broker();
        using (broker.AcquireLock())
        {
            broker.BreakLock(clearStaleStamp: true);
            Assert.True(File.Exists(StampPath), "the live holder's own stamp was deleted");
        }
    }

    // ---------------------------------------------------------------
    // Leases: the "has not checked in for a long time" half.
    // ---------------------------------------------------------------

    [Fact]
    public void ALeaseWhoseOwnerDiedIsReportedAsReclaimable()
    {
        var granted = Assert.IsType<ClaimResult.Granted>(
            new CapacityBroker(_dir, null, _ => true)
                .Claim(Catalogue(), new CapabilityRequest("flash", 32_000), LocalOnly, Priority.Batch, "ghost-run"));

        // Rewrite the lease so its owner is a pid that no longer exists.
        var path = Path.Combine(_dir, "leases", granted.Lease.Id + ".json");
        var text = File.ReadAllText(path);
        var replaced = text.Replace("\"pid\": " + granted.Lease.Pid, "\"pid\": " + DeadPid);
        Assert.NotEqual(text, replaced); // the rewrite must actually have landed
        File.WriteAllText(path, replaced);

        var status = Broker().Inspect();

        Assert.Single(status.ReclaimableLeases);
        Assert.Empty(status.Leases);
        Assert.Contains(status.Notes, n => n.Contains("is gone") && n.Contains("ghost-run"));
    }

    [Fact]
    public void ALeaseThatStoppedHeartbeatingIsReportedAsReclaimable()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = new CapacityBroker(_dir, clock, _ => true);

        broker.Claim(Catalogue(), new CapabilityRequest("flash", 32_000), LocalOnly, Priority.Batch, "quiet-run");

        now = now.AddMinutes(30); // long past the 5-minute lease TTL

        var status = broker.Inspect();
        Assert.Single(status.ReclaimableLeases);
        Assert.Contains(status.Notes, n => n.Contains("no heartbeat") && n.Contains("quiet-run"));
    }

    [Fact]
    public void AHeartbeatKeepsALeaseOutOfTheReclaimPile()
    {
        // The positive half: "reclaim what went quiet" is worthless if it also
        // reclaims what is still talking.
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = new CapacityBroker(_dir, clock, _ => true);

        var g = Assert.IsType<ClaimResult.Granted>(broker.Claim(
            Catalogue(), new CapabilityRequest("flash", 32_000), LocalOnly, Priority.Batch, "chatty"));

        for (var i = 0; i < 10; i++)
        {
            now = now.AddMinutes(3);
            Assert.NotNull(broker.Heartbeat(g.Lease.Id));
        }

        Assert.Empty(broker.Inspect().ReclaimableLeases);
        Assert.Empty(broker.ReclaimStaleLeases());
    }

    [Fact]
    public void ReclaimingReportsExactlyWhatItTookAwayAndSparesLiveLeases()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = new CapacityBroker(_dir, clock, _ => true);
        var cat = Catalogue();

        // Both claimed up front, with different TTLs. Claiming the second one
        // AFTER advancing the clock would not test anything: that claim takes
        // the ledger lock and sweeps the expired lease on the way past, leaving
        // ReclaimStaleLeases with nothing to find. See
        // AnyLedgerOperationAlreadySweepsSilently below.
        broker.Claim(cat, new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "abandoned",
            TimeSpan.FromMinutes(1));
        broker.Claim(cat, new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "healthy",
            TimeSpan.FromMinutes(60));

        now = now.AddMinutes(30);

        var reclaimed = broker.ReclaimStaleLeases();

        // Naming the casualty is the point: a reservation was taken away from
        // someone, and a bare count would not say from whom.
        Assert.Single(reclaimed);
        Assert.Equal("abandoned", reclaimed[0].Owner);
        Assert.Equal("healthy", Assert.Single(broker.Live()).Owner);
    }

    [Fact]
    public void AnyLedgerOperationAlreadySweepsSilently()
    {
        // Worth pinning because it decides what the doctor is FOR. Reclaiming
        // is not a repair that has to be run — every operation that takes the
        // ledger lock already drops dead leases. What was missing is that it
        // happened with no record: a reservation vanished and nobody was told.
        // So the deliverable is the REPORT, and this test is the reason.
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = new CapacityBroker(_dir, clock, _ => true);
        var cat = Catalogue();

        broker.Claim(cat, new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "abandoned");
        now = now.AddMinutes(30);

        // An ordinary unrelated claim, not a repair.
        broker.Claim(cat, new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "unrelated");

        Assert.Empty(broker.ReclaimStaleLeases());
        Assert.Equal("unrelated", Assert.Single(broker.Live()).Owner);
    }

    [Fact]
    public void ReclaimingIsIdempotentAndReportsNothingTheSecondTime()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = new CapacityBroker(_dir, clock, _ => true);

        broker.Claim(Catalogue(), new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "abandoned");
        now = now.AddMinutes(30);

        Assert.Single(broker.ReclaimStaleLeases());
        Assert.Empty(broker.ReclaimStaleLeases());
    }

    [Fact]
    public void ReclaimedTokensGoBackIntoThePool()
    {
        // The whole reason reclaiming matters: an abandoned lease holds capacity
        // nobody is using, and a newcomer is refused because of it.
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = new CapacityBroker(_dir, clock, _ => true);
        var cat = Catalogue();

        for (var i = 0; i < 5; i++)
            broker.Claim(cat, new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "run-" + i);

        // Pool is 100_000 and all of it is now committed.
        Assert.IsType<ClaimResult.Queued>(
            broker.Claim(cat, new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "newcomer"));

        now = now.AddMinutes(30);
        Assert.Equal(5, broker.ReclaimStaleLeases().Count);

        Assert.IsType<ClaimResult.Granted>(
            broker.Claim(cat, new CapabilityRequest("flash", 20_000), LocalOnly, Priority.Batch, "newcomer"));
    }
}
