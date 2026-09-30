using System.Threading.Channels;
using Vett.Agent;
using Vett.Live;

namespace Vett.Tests;

/// <summary>
/// Subscriber fan-out for <see cref="LiveSink"/> — the multiplexer behind the
/// SSE endpoint at <c>LiveServer.HandleEventsAsync</c>.
///
/// ⛔ WHY THIS FILE EXISTS. Before 2026-08-27 <c>LiveSink</c> had ZERO test
/// references anywhere in the repo, and <see cref="LiveSink.Unsubscribe"/>
/// ignored its <c>reader</c> parameter entirely:
///
/// <code>
/// _subscribers.RemoveAll(w => { try { w.TryComplete(); return true; } catch { return false; } });
/// </code>
///
/// The predicate returns true for every writer, so ONE client disconnecting
/// completed and dropped EVERY subscriber. LiveServer calls Unsubscribe from
/// the `finally` of each SSE request (LiveServer.cs:112), so with two browser
/// tabs open on a run, closing either one silently killed the other's stream.
///
/// ⭐ THE REASON IT SURVIVED IS THE POINT. Nothing throws, nothing logs, and
/// the run is completely unaffected — Emit is fire-and-forget onto a list that
/// is simply empty afterwards. The observable symptom is "the live view
/// stopped", which reads as a browser, network, or end-of-run event. And it
/// cannot reproduce with fewer than TWO concurrent subscribers, so every
/// single-client use of the feature looked correct.
///
/// So the load-bearing test here is <see cref="Unsubscribing_one_leaves_every_other_subscriber_attached"/>:
/// it is the only one below that fails against the old code. The rest pin the
/// edges around it. A single-subscriber test would have passed against the
/// defect — the same trap as a one-sided pair test.
/// </summary>
public class LiveSinkSubscriberTests
{
    private static Event Ev(string type) =>
        new(type, new Dictionary<string, object?> { ["marker"] = type });

    /// <summary>Drain whatever is queued without blocking.</summary>
    private static List<string> DrainAvailable(ChannelReader<string> r)
    {
        var got = new List<string>();
        while (r.TryRead(out var line)) got.Add(line);
        return got;
    }

    // ---------------------------------------------------------------
    // THE REGRESSION TEST
    // ---------------------------------------------------------------

    /// <summary>
    /// Three subscribers, one leaves. The other two must still be attached AND
    /// must still receive events emitted afterwards.
    ///
    /// Both halves are asserted deliberately. "Still in the list" alone would
    /// pass against a version that removed the right entry but completed the
    /// wrong channel — a completed channel accepts no further writes, so the
    /// count would look right while the stream was dead. Receiving a
    /// POST-unsubscribe event is the conjunct that proves the survivors are
    /// live rather than merely present.
    /// </summary>
    [Fact]
    public void Unsubscribing_one_leaves_every_other_subscriber_attached()
    {
        using var sink = new LiveSink(trajectoryDir: null);

        var a = sink.Subscribe();
        var b = sink.Subscribe();
        var c = sink.Subscribe();
        Assert.Equal(3, sink.SubscriberCount);

        // Everyone is live to begin with — without this the test could pass on
        // a sink that never delivered anything to anyone.
        sink.Emit(Ev("before"));
        Assert.Single(DrainAvailable(a));
        Assert.Single(DrainAvailable(b));
        Assert.Single(DrainAvailable(c));

        sink.Unsubscribe(b);

        Assert.Equal(2, sink.SubscriberCount);

        // ⭐ THE ASSERTION THE OLD CODE FAILED: a and c are still receiving.
        sink.Emit(Ev("after"));

        var aGot = DrainAvailable(a);
        var cGot = DrainAvailable(c);
        Assert.Single(aGot);
        Assert.Contains("after", aGot[0]);
        Assert.Single(cGot);
        Assert.Contains("after", cGot[0]);
    }

    /// <summary>
    /// The other side of the same coin: the subscriber that DID leave must be
    /// completed, so its consumer loop ends instead of hanging.
    /// LiveServer.HandleEventsAsync sits in `await foreach (… ReadAllAsync)`
    /// and only exits when the channel completes.
    /// </summary>
    [Fact]
    public async Task An_unsubscribed_reader_is_completed_so_its_consumer_loop_ends()
    {
        using var sink = new LiveSink(trajectoryDir: null);
        var a = sink.Subscribe();
        var gone = sink.Subscribe();

        sink.Unsubscribe(gone);

        // Completion is the signal LiveServer relies on. If this ever stops
        // being true the SSE handler leaks a request thread per client.
        await gone.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(gone.Completion.IsCompletedSuccessfully);

        // And the survivor is emphatically NOT completed.
        Assert.False(a.Completion.IsCompleted,
            "unsubscribing one subscriber completed another one's channel — this is the "
          + "RemoveAll-returns-true defect back.");
    }

    /// <summary>
    /// Events emitted after a subscriber leaves must not reach it. Guards the
    /// opposite over-correction: a fix that stopped completing the channel but
    /// left the writer in the list.
    /// </summary>
    [Fact]
    public void An_unsubscribed_reader_receives_no_further_events()
    {
        using var sink = new LiveSink(trajectoryDir: null);
        var gone = sink.Subscribe();

        sink.Emit(Ev("before"));
        Assert.Single(DrainAvailable(gone));

        sink.Unsubscribe(gone);
        sink.Emit(Ev("after"));

        Assert.Empty(DrainAvailable(gone));
    }

    // ---------------------------------------------------------------
    // EDGES — all of these are paths LiveServer can actually take.
    // ---------------------------------------------------------------

    /// <summary>
    /// The old implementation's comment said the SSE handler "should call
    /// Unsubscribe exactly once". Under the fix that stops being a requirement:
    /// a second call is a no-op instead of a mass disconnect.
    /// </summary>
    [Fact]
    public void Unsubscribing_twice_is_a_no_op_and_does_not_disturb_anyone_else()
    {
        using var sink = new LiveSink(trajectoryDir: null);
        var a = sink.Subscribe();
        var gone = sink.Subscribe();

        sink.Unsubscribe(gone);
        sink.Unsubscribe(gone);   // must not throw, must not touch `a`

        Assert.Equal(1, sink.SubscriberCount);
        sink.Emit(Ev("after"));
        Assert.Single(DrainAvailable(a));
    }

    /// <summary>A reader this sink never issued must be ignored, not treated as
    /// a reason to drop everyone.</summary>
    [Fact]
    public void Unsubscribing_a_foreign_reader_is_ignored()
    {
        using var sink = new LiveSink(trajectoryDir: null);
        var a = sink.Subscribe();

        var foreign = Channel.CreateBounded<string>(4).Reader;
        sink.Unsubscribe(foreign);

        Assert.Equal(1, sink.SubscriberCount);
        sink.Emit(Ev("after"));
        Assert.Single(DrainAvailable(a));
    }

    /// <summary>Every subscription is distinct even though they are created by
    /// the same call with the same arguments — the dictionary keys on reference
    /// identity, and this pins that two Subscribe() calls cannot collide.</summary>
    [Fact]
    public void Two_subscriptions_are_distinct_entries()
    {
        using var sink = new LiveSink(trajectoryDir: null);

        var a = sink.Subscribe();
        var b = sink.Subscribe();

        Assert.NotSame(a, b);
        Assert.Equal(2, sink.SubscriberCount);
    }

    /// <summary>Dispose completes everyone — the run is over, every SSE loop
    /// should end.</summary>
    [Fact]
    public async Task Dispose_completes_every_remaining_subscriber()
    {
        var sink = new LiveSink(trajectoryDir: null);
        var a = sink.Subscribe();
        var b = sink.Subscribe();

        sink.Dispose();

        Assert.Equal(0, sink.SubscriberCount);
        await a.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await b.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Emitting with nobody listening is the common case (the sink is
    /// constructed whenever --trajectory-dir OR --live-port is set) and must be
    /// free of side effects.</summary>
    [Fact]
    public void Emit_with_no_subscribers_does_not_throw()
    {
        using var sink = new LiveSink(trajectoryDir: null);
        sink.Emit(Ev("nobody-home"));
    }

    /// <summary>
    /// A slow consumer must degrade to dropped events for ITSELF only. The
    /// channel is bounded at `capacity` with DropOldest, so overflowing one
    /// subscriber must not block Emit or affect a healthy sibling — Emit is on
    /// the agent loop's path and a blocking write there would stall the run.
    /// </summary>
    [Fact]
    public void A_saturated_subscriber_drops_its_own_events_without_affecting_siblings()
    {
        using var sink = new LiveSink(trajectoryDir: null);

        var slow = sink.Subscribe(capacity: 2);
        var healthy = sink.Subscribe(capacity: 1024);

        for (int i = 0; i < 10; i++) sink.Emit(Ev($"e{i}"));

        // DropOldest: the slow one keeps the LAST 2, not the first 2. Asserting
        // WHICH two also proves the drop policy, not merely that a cap exists.
        var slowGot = DrainAvailable(slow);
        Assert.Equal(2, slowGot.Count);
        Assert.Contains("e8", slowGot[0]);
        Assert.Contains("e9", slowGot[1]);

        // The healthy subscriber is untouched by its neighbour's saturation.
        Assert.Equal(10, DrainAvailable(healthy).Count);
    }
}
