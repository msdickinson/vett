using System.Collections.Concurrent;
using Vett.Capacity;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// The ledger lock itself, hammered directly.
///
/// WHY NOT THROUGH Claim(). A contended Claim() test is how the underlying
/// defect was first SEEN, but it turned out to be a poor instrument for
/// reproducing it: reinstating the bug left that test green. Claim() does a
/// directory scan, a resolve and a file write inside the lock, so the lock is
/// held for milliseconds and released rarely. The defect lives in the RELEASE
/// path — a handle closed with FileOptions.DeleteOnClose leaves the file
/// delete-pending on Windows, and an opener arriving inside that window is
/// rejected with ERROR_ACCESS_DENIED (surfaced as UnauthorizedAccessException,
/// NOT IOException, so a catch written for IOException lets it escape).
///
/// Hitting it therefore needs many releases per second under contention, which
/// means a tight acquire/release loop and nothing else in the critical section.
/// That is what these tests do.
/// </summary>
public class CapacityLockTests : IDisposable
{
    private readonly string _dir;

    public CapacityLockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-lock-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Every thread acquires and releases as fast as it can for a fixed
    /// duration. Nothing may escape the lock's own retry loop.
    /// </summary>
    [Fact]
    public void HammeringTheLockNeverThrows()
    {
        const int Threads = 16;
        var duration = TimeSpan.FromSeconds(4);

        var errors = new ConcurrentBag<Exception>();
        var acquisitions = 0L;
        using var gate = new Barrier(Threads);

        Parallel.For(0, Threads, i =>
        {
            // A separate broker per thread, because separate PROCESSES are the
            // case the file lock exists for; sharing one instance would let
            // in-process luck stand in for cross-process correctness.
            var broker = new CapacityBroker(_dir, null, _ => true);
            gate.SignalAndWait();

            var stop = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < stop)
            {
                try
                {
                    using (broker.AcquireLock(TimeSpan.FromSeconds(30))) { }
                    Interlocked.Increment(ref acquisitions);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    return;
                }
            }
        });

        // A liveness conjunct: "no exceptions" is also satisfied by a loop that
        // never ran. Threads spinning for 4s must manage far more than this.
        Assert.True(acquisitions > 5_000,
            $"only {acquisitions} acquisitions across {Threads} threads in {duration.TotalSeconds}s — "
            + "too few for this to have been a contended test at all");

        Assert.True(errors.IsEmpty,
            $"{errors.Count} of {acquisitions + errors.Count} acquisitions threw. "
            + $"First: {errors.FirstOrDefault()?.GetType().Name}: {errors.FirstOrDefault()?.Message}");
    }

    /// <summary>
    /// The lock must actually exclude. A retry loop that swallowed the failure
    /// and returned a non-exclusive handle would pass the test above.
    /// </summary>
    [Fact]
    public void TheLockActuallyExcludes()
    {
        const int Threads = 12;
        var duration = TimeSpan.FromSeconds(3);

        var inside = 0;
        var violations = 0;
        var errors = new ConcurrentBag<Exception>();
        using var gate = new Barrier(Threads);

        Parallel.For(0, Threads, i =>
        {
            var broker = new CapacityBroker(_dir, null, _ => true);
            gate.SignalAndWait();

            var stop = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < stop)
            {
                try
                {
                    using (broker.AcquireLock(TimeSpan.FromSeconds(30)))
                    {
                        if (Interlocked.Increment(ref inside) != 1) Interlocked.Increment(ref violations);
                        Thread.SpinWait(200);
                        Interlocked.Decrement(ref inside);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    return;
                }
            }
        });

        Assert.True(errors.IsEmpty, $"threw: {errors.FirstOrDefault()}");
        Assert.Equal(0, violations);
    }

    /// <summary>
    /// A lock nobody can get must not hang forever. Held by a live handle from
    /// this process, the waiter gives up with an explanation that names the
    /// file — that message is the only clue an operator gets.
    /// </summary>
    [Fact]
    public void AnUnobtainableLockTimesOutWithAnActionableMessage()
    {
        var broker = new CapacityBroker(_dir, null, _ => true);

        using var held = broker.AcquireLock();

        var ex = Assert.Throws<TimeoutException>(
            () => broker.AcquireLock(TimeSpan.FromMilliseconds(300)));

        Assert.Contains(".lock", ex.Message);
        Assert.Contains("another vett process", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    /// <summary>
    /// Releasing must not destroy the lock file. Deleting it on close is what
    /// created the delete-pending window in the first place, and the file being
    /// durable is also what lets a waiter block on it rather than racing to
    /// create it.
    /// </summary>
    [Fact]
    public void ReleasingTheLockLeavesTheLockFileInPlace()
    {
        var broker = new CapacityBroker(_dir, null, _ => true);
        using (broker.AcquireLock()) { }

        Assert.True(File.Exists(Path.Combine(_dir, ".lock")),
            "the lock file was removed on release, which reopens the delete-pending race");
    }
}
