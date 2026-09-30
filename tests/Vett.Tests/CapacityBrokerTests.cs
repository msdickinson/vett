using Vett.Capacity;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// The broker's job is the thing vett has never had: coordination BETWEEN
/// processes. Every existing concurrency gate is a SemaphoreSlim, so a
/// vett-chat session and a bench sweep currently oversubscribe the same GPU
/// without either being able to notice. These tests therefore lean on the
/// case that matters — two independent broker instances over one ledger,
/// which is what two independent vett processes look like.
/// </summary>
public class CapacityBrokerTests : IDisposable
{
    private readonly string _dir;

    public CapacityBrokerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-cap-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const int LocalPool = 100_000;
    private const int LocalWindow = 65_536;

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
                        ServedWindowTokens = LocalWindow, PoolTokens = LocalPool, CompactionRatio = 0.75,
                    },
                },
            },
            new Capability
            {
                Name = "pro",
                Providers =
                {
                    new CapabilityProvider
                    {
                        Name = "openrouter-pro", LocalityRaw = "cloud",
                        Endpoint = "https://openrouter.ai/api/v1", Model = "deepseek/deepseek-v4-pro",
                        CompactionRatio = 0.78,
                    },
                },
            },
        },
    };

    private CapacityBroker Broker(Func<DateTimeOffset>? now = null, Func<int, bool>? pidAlive = null) =>
        new(_dir, now, pidAlive ?? (_ => true));

    private static readonly ResolvePolicy LocalOnly = new(AllowCloud: false);
    private static readonly ResolvePolicy CloudOk = new(AllowCloud: true);

    // ---------------------------------------------------------------
    // The core reason this exists: two processes, one pool.
    // ---------------------------------------------------------------

    [Fact]
    public void TwoIndependentBrokersOverOneLedgerSeeEachOthersReservations()
    {
        var a = Broker();
        var b = Broker(); // a different process, as far as the ledger knows

        var first = Assert.IsType<ClaimResult.Granted>(
            a.Claim(Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "run-a"));

        // A grant is capped by the served WINDOW (65536), never the pool, so
        // 100000 - 65536 = 34464 free and a second full-window session cannot fit.
        var second = b.Claim(Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "run-b");

        var queued = Assert.IsType<ClaimResult.Queued>(second);
        Assert.Contains("local-gpu-1", queued.WaitingOn);
        Assert.Equal(LocalWindow, first.Lease.Tokens);
    }

    [Fact]
    public void ReleasingALeaseReturnsItsTokensToThePool()
    {
        var a = Broker();
        var b = Broker();

        var first = Assert.IsType<ClaimResult.Granted>(
            a.Claim(Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "run-a"));

        Assert.IsType<ClaimResult.Queued>(
            b.Claim(Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "run-b"));

        a.Release(first.Lease.Id);

        Assert.IsType<ClaimResult.Granted>(
            b.Claim(Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "run-b"));
    }

    [Fact]
    public void ManySmallGrantsAndFewLargeOnesDrawOnTheSamePool()
    {
        // The trade Mark named: "a few LARGE context flash" versus lots of
        // small. A seat count could not express this; tokens can.
        var broker = Broker();
        var cat = Catalogue();

        // One full-window session takes 65536 of the 100000 pool, leaving no
        // room for a second of the same size...
        Assert.IsType<ClaimResult.Granted>(
            broker.Claim(cat, new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "big-0"));
        Assert.IsType<ClaimResult.Queued>(
            broker.Claim(cat, new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "big-1"));

        // ...but the same remaining space holds several small ones.
        for (var i = 0; i < 4; i++)
        {
            Assert.IsType<ClaimResult.Granted>(
                broker.Claim(cat, new CapabilityRequest("flash", 8_000), LocalOnly, Priority.Batch, $"small-{i}"));
        }
    }

    // ---------------------------------------------------------------
    // Reclamation. A held token that nobody is using is a leak.
    // ---------------------------------------------------------------

    [Fact]
    public void AnExpiredLeaseStopsCountingAgainstThePool()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = Broker(clock);

        Assert.IsType<ClaimResult.Granted>(broker.Claim(
            Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "gone",
            ttl: TimeSpan.FromMinutes(1)));

        now = now.AddMinutes(2); // the holder stopped heartbeating

        Assert.IsType<ClaimResult.Granted>(broker.Claim(
            Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "next"));
    }

    [Fact]
    public void ALeaseHeldByADeadProcessIsReclaimedImmediatelyNotAfterTheTtl()
    {
        // Expiry alone would hold a crashed session's tokens hostage for the
        // whole TTL. These are two independent reasons a lease stops
        // counting, and the pool needs both.
        var live = Broker();
        Assert.IsType<ClaimResult.Granted>(live.Claim(
            Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "crashed",
            ttl: TimeSpan.FromHours(1)));

        var afterCrash = Broker(pidAlive: _ => false);
        Assert.IsType<ClaimResult.Granted>(afterCrash.Claim(
            Catalogue(), new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "next"));
    }

    [Fact]
    public void HeartbeatKeepsALeaseAliveAndReturnsNullOnceItIsGone()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = () => now;
        var broker = Broker(clock);

        var g = Assert.IsType<ClaimResult.Granted>(broker.Claim(
            Catalogue(), new CapabilityRequest("flash", 1_000), LocalOnly, Priority.Batch, "held",
            ttl: TimeSpan.FromMinutes(1)));

        now = now.AddSeconds(30);
        Assert.NotNull(broker.Heartbeat(g.Lease.Id, TimeSpan.FromMinutes(1)));

        broker.Release(g.Lease.Id);
        // "Your lease is gone" must be distinguishable from "fine" — a caller
        // that reads null as success would keep using capacity it no longer holds.
        Assert.Null(broker.Heartbeat(g.Lease.Id));
    }

    // ---------------------------------------------------------------
    // Priority. Interactive work outranks batch.
    // ---------------------------------------------------------------

    [Fact]
    public void AnInteractiveClaimAsksALowerPriorityHolderToStandDown()
    {
        var batch = Broker();
        var chat = Broker();
        var cat = Catalogue();

        var held = Assert.IsType<ClaimResult.Granted>(
            batch.Claim(cat, new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "sweep"));

        var queued = Assert.IsType<ClaimResult.Queued>(
            chat.Claim(cat, new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Interactive, "vett-chat"));

        Assert.Contains(held.Lease.Id, queued.PreemptedLeases);

        // The signal IS AVAILABLE to a holder that checks in -- that is what
        // this asserts, and it is the whole of what the broker promises: a
        // cooperative mark, not a revocation.
        //
        // ⛔ DO NOT READ THIS AS "the holder yields". As of 2026-08-28 NO
        // PRODUCTION CALLER READS THE RESULT -- LeasedChatClient's renewal
        // timer discards it. This test pins that the broker makes the signal
        // reachable; it says nothing about anyone acting on it, and it would
        // stay green if the consumer were never built. See CapacityBroker's
        // class doc.
        var seen = batch.Heartbeat(held.Lease.Id);
        Assert.NotNull(seen);
        Assert.True(seen!.PreemptRequested);
        Assert.Contains("vett-chat", seen.PreemptReason!);
    }

    [Fact]
    public void BatchNeverPreemptsInteractive()
    {
        var chat = Broker();
        var batch = Broker();
        var cat = Catalogue();

        var held = Assert.IsType<ClaimResult.Granted>(
            chat.Claim(cat, new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Interactive, "vett-chat"));

        var queued = Assert.IsType<ClaimResult.Queued>(
            batch.Claim(cat, new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "sweep"));

        Assert.Empty(queued.PreemptedLeases);
        Assert.False(chat.Heartbeat(held.Lease.Id)!.PreemptRequested);
    }

    [Fact]
    public void EqualPriorityDoesNotPreempt()
    {
        // Strictly-greater, not greater-or-equal: peers would otherwise
        // preempt each other in a loop and neither would finish.
        var a = Broker();
        var b = Broker();
        var cat = Catalogue();

        var held = Assert.IsType<ClaimResult.Granted>(
            a.Claim(cat, new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "first"));

        var queued = Assert.IsType<ClaimResult.Queued>(
            b.Claim(cat, new CapabilityRequest("flash", LocalWindow), LocalOnly, Priority.Batch, "second"));

        Assert.Empty(queued.PreemptedLeases);
        Assert.False(a.Heartbeat(held.Lease.Id)!.PreemptRequested);
    }

    [Fact]
    public void AClaimThatFitsInFreeSpaceDisturbsNobody()
    {
        var batch = Broker();
        var chat = Broker();
        var cat = Catalogue();

        var held = Assert.IsType<ClaimResult.Granted>(
            batch.Claim(cat, new CapabilityRequest("flash", 32_000), LocalOnly, Priority.Batch, "sweep"));

        Assert.IsType<ClaimResult.Granted>(
            chat.Claim(cat, new CapabilityRequest("flash", 32_000), LocalOnly, Priority.Interactive, "vett-chat"));

        Assert.False(batch.Heartbeat(held.Lease.Id)!.PreemptRequested);
    }

    [Fact]
    public void PreemptionStopsOnceEnoughTokensWouldBeFreed()
    {
        // Taking more than needed is gratuitous damage: every preempted run
        // pays a prefix recompute when it comes back.
        var batch = Broker();
        var chat = Broker();
        var cat = Catalogue();

        var leases = new List<Lease>();
        for (var i = 0; i < 8; i++)
        {
            leases.Add(Assert.IsType<ClaimResult.Granted>(
                batch.Claim(cat, new CapabilityRequest("flash", 12_000), LocalOnly, Priority.Batch, $"sweep-{i}")).Lease);
        }

        var queued = Assert.IsType<ClaimResult.Queued>(
            chat.Claim(cat, new CapabilityRequest("flash", 12_000), LocalOnly, Priority.Interactive, "vett-chat"));

        // One 12k lease is enough to satisfy a 12k request.
        Assert.Single(queued.PreemptedLeases);
        Assert.Equal(7, leases.Count(l => !batch.Heartbeat(l.Id)!.PreemptRequested));
    }

    // ---------------------------------------------------------------
    // The broker must not launder a refusal into a queue.
    // ---------------------------------------------------------------

    [Fact]
    public void AProRequestUnderALocalOnlyPolicyIsRefusedNotQueued()
    {
        var refused = Assert.IsType<ClaimResult.Refused>(Broker().Claim(
            Catalogue(), new CapabilityRequest("pro", 32_000), LocalOnly, Priority.Batch, "run"));

        Assert.Contains("cloud is not permitted", refused.Reason);
    }

    [Fact]
    public void CloudProvidersReportUnboundedTokensBecauseTheirLimitIsMoney()
    {
        // Reporting a token count for a cloud pool would imply a spend cap
        // this class does not enforce.
        var broker = Broker();
        var availability = broker.BuildAvailability(Catalogue(), Array.Empty<Lease>());

        Assert.Null(availability.For("openrouter-pro").FreeTokens);
        Assert.Equal(LocalPool, availability.For("local-gpu-1").FreeTokens);
    }

    [Fact]
    public void ManyConcurrentClaimsNeverOversubscribeThePool()
    {
        // The race the file lock exists to prevent: two claimants read the same
        // free-token count and both take it.
        //
        // An unsynchronised version of this test had ZERO power — deleting the
        // lock outright left it green, because threads started by Parallel.For
        // drift far enough apart that they serialise by luck. So every thread
        // is held at a barrier and released together, and the whole thing is
        // repeated: a race that fires probabilistically needs more than one
        // trial before its silence means anything.
        const int Racers = 24;
        const int Ask = 12_000;
        const int Trials = 8;

        for (var trial = 0; trial < Trials; trial++)
        {
            var dir = Path.Combine(_dir, $"trial-{trial}");
            Directory.CreateDirectory(dir);

            var cat = Catalogue();
            var granted = new System.Collections.Concurrent.ConcurrentBag<Lease>();
            using var gate = new Barrier(Racers);

            Parallel.For(0, Racers, i =>
            {
                var broker = new CapacityBroker(dir, null, _ => true);
                gate.SignalAndWait();
                var r = broker.Claim(cat, new CapabilityRequest("flash", Ask), LocalOnly, Priority.Batch, $"racer-{i}");
                if (r is ClaimResult.Granted g) granted.Add(g.Lease);
            });

            var total = granted.Sum(l => (long)l.Tokens);
            Assert.True(total <= LocalPool,
                $"trial {trial}: pool is {LocalPool} but {granted.Count} leases totalling {total} were granted");

            // The ledger on disk must agree with what the claimants were told.
            // A broker that answered "granted" but lost the write would leave
            // the pool looking free to the NEXT process.
            var onDisk = new CapacityBroker(dir, null, _ => true).Live();
            Assert.Equal(granted.Count, onDisk.Count);
        }
    }

    [Fact]
    public void ConcurrentClaimsGrantEveryTokenThePoolCanActuallyHold()
    {
        // The other half of the race. "Never oversubscribe" is satisfied
        // trivially by a broker that grants nothing, so pin the floor too:
        // 100000 / 12000 = 8 whole grants, and the lock must not cost any of
        // them by leaving a claimant's write on the floor.
        var cat = Catalogue();
        var granted = new System.Collections.Concurrent.ConcurrentBag<Lease>();
        using var gate = new Barrier(24);

        Parallel.For(0, 24, i =>
        {
            var broker = Broker();
            gate.SignalAndWait();
            var r = broker.Claim(cat, new CapabilityRequest("flash", 12_000), LocalOnly, Priority.Batch, $"racer-{i}");
            if (r is ClaimResult.Granted g) granted.Add(g.Lease);
        });

        Assert.Equal(8, granted.Count);
    }
}
