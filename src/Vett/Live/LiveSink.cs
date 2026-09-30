using System.Text.Json;
using System.Threading.Channels;
using Vett.Agent;

namespace Vett.Live;

/// <summary>
/// Optional event-stream sink. Receives Events from the agent loop / runner
/// during a `vett run` and fans them out to:
///
///   (a) per-instance JSONL files at &lt;output&gt;/instances/&lt;instance_id&gt;/events.jsonl
///       and a top-level &lt;output&gt;/events.jsonl (run-level events) — only if
///       trajectoryDir is set.
///
///   (b) any number of in-process subscribers (the SSE endpoint hands them to
///       HTTP clients) — always available, cheap when nobody is listening.
///
/// Construction is opt-in: RunCommand only creates one if --trajectory-dir
/// or --live-port was passed. When both are off, the legacy in-memory
/// callback is used and nothing extra runs.
/// </summary>
public sealed class LiveSink : IDisposable
{
    private readonly string? _trajectoryDir;
    private readonly object _fileLock = new();

    /// <summary>
    /// Live subscribers, keyed by the READER we handed the caller.
    ///
    /// ⛔ 2026-08-27: this used to be a bare <c>List&lt;ChannelWriter&lt;string&gt;&gt;</c>,
    /// which gave <see cref="Unsubscribe"/> nothing to match on — see the note
    /// there. The reader is the only handle a caller ever holds, so it has to be
    /// the key. Reference identity is the right comparison: each
    /// <see cref="Subscribe"/> mints a fresh channel, so two distinct
    /// subscriptions can never compare equal, and a caller can only ever pass
    /// back an object it was given.
    /// </summary>
    private readonly Dictionary<ChannelReader<string>, ChannelWriter<string>> _subscribers =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _subLock = new();
    private long _seq;

    public LiveSink(string? trajectoryDir)
    {
        _trajectoryDir = trajectoryDir;
        if (!string.IsNullOrEmpty(_trajectoryDir))
            Directory.CreateDirectory(_trajectoryDir);
    }

    /// <summary>Called by Runner / AgentLoop for every Event. Cheap, non-blocking.</summary>
    public void Emit(Event e)
    {
        var seq = Interlocked.Increment(ref _seq);
        var instanceId = e.Data.TryGetValue("instance_id", out var iidObj) ? iidObj?.ToString() : null;
        var envelope = new
        {
            seq,
            ts = DateTime.UtcNow.ToString("O"),
            type = e.Type,
            instance_id = instanceId,
            data = e.Data,
        };
        var line = JsonSerializer.Serialize(envelope);

        // (a) file sink — instance-scoped + run-level
        if (_trajectoryDir is not null)
        {
            try { WriteToFiles(instanceId, line); }
            catch { /* never let an emit failure break the run */ }
        }

        // (b) push to live subscribers (SSE clients). Non-blocking writes; if a
        // subscriber's queue is full, that one slot drops the event. We keep
        // the run going.
        List<ChannelWriter<string>> snap;
        lock (_subLock) snap = [.. _subscribers.Values];
        foreach (var w in snap)
        {
            if (!w.TryWrite(line)) { /* slow consumer drops; that's fine */ }
        }
    }

    private void WriteToFiles(string? instanceId, string line)
    {
        lock (_fileLock)
        {
            // Run-level events.jsonl: every event lands here, ordered.
            File.AppendAllText(Path.Combine(_trajectoryDir!, "events.jsonl"), line + "\n");

            // Per-instance events.jsonl: only events tagged with an instance_id.
            if (!string.IsNullOrEmpty(instanceId))
            {
                var instDir = Path.Combine(_trajectoryDir!, "instances", instanceId);
                Directory.CreateDirectory(instDir);
                File.AppendAllText(Path.Combine(instDir, "events.jsonl"), line + "\n");
            }
        }
    }

    /// <summary>SSE endpoint subscribes via this. Returns a queue of JSON lines.</summary>
    public ChannelReader<string> Subscribe(int capacity = 1024)
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_subLock) _subscribers.Add(ch.Reader, ch.Writer);
        return ch.Reader;
    }

    /// <summary>
    /// Detach ONE subscriber — the one that owns <paramref name="reader"/> —
    /// and complete only its channel. Unknown or already-removed readers are a
    /// no-op, so the double-unsubscribe the old comment worried about is
    /// harmless rather than merely unlikely.
    /// </summary>
    /// <remarks>
    /// ⛔ THE DEFECT THIS REPLACES (2026-08-27). The body was:
    ///
    /// <code>
    /// _subscribers.RemoveAll(w => { try { w.TryComplete(); return true; } catch { return false; } });
    /// </code>
    ///
    /// The <c>reader</c> parameter was never read, and the predicate returns
    /// <c>true</c> for every writer it does not throw on — so this completed and
    /// removed EVERY subscriber. Its own comment called it "liberal but safe";
    /// it is neither. With N live SSE clients, the first one to disconnect
    /// completed the other N-1 channels, their <c>await foreach</c> in
    /// LiveServer.HandleEventsAsync:101 ran to completion, and every remaining
    /// watcher's stream closed. Each of those handlers then hit its own
    /// <c>finally</c> and called Unsubscribe again.
    ///
    /// Nothing errors, no exception is thrown, and the run itself is unaffected
    /// (Emit is fire-and-forget onto zero subscribers), so the symptom reaching
    /// a person is "the live view just stops" — attributable to the browser, the
    /// network, or the run ending. The failure needs TWO concurrent viewers to
    /// appear at all, which is why single-client use looked fine.
    /// </remarks>
    public void Unsubscribe(ChannelReader<string> reader)
    {
        lock (_subLock)
        {
            if (_subscribers.Remove(reader, out var writer))
            {
                try { writer.TryComplete(); } catch { /* already completed */ }
            }
        }
    }

    /// <summary>Number of live subscribers. Exists so a test can assert that
    /// unsubscribing one leaves the others attached — the defect above was
    /// invisible from the outside precisely because nothing exposed this.</summary>
    public int SubscriberCount
    {
        get { lock (_subLock) return _subscribers.Count; }
    }

    public void Dispose()
    {
        lock (_subLock)
        {
            foreach (var w in _subscribers.Values) try { w.TryComplete(); } catch { }
            _subscribers.Clear();
        }
    }
}
