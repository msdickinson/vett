using System.Reflection;
using System.Text.Json;
using Vett.Cli;
using Vett.Runner;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// <para>
/// <c>BenchCommand.AppendMetrics</c> used to swallow every read failure in a bare
/// <c>catch { }</c>, leaving the accumulator empty and then writing it back — which
/// REPLACED the entire metrics history with a single entry and reported
/// <c>"(total entries: 1)"</c>, indistinguishable from a healthy first run.
/// </para>
/// <para>
/// Two different failures reached that catch and only one of them is corruption:
/// a transient <see cref="IOException"/> (a concurrent process holding the file —
/// routine in this tree, which throws MSB3021/MSB3027 locks regularly) destroyed
/// the history exactly as thoroughly as malformed JSON did. The distinction is the
/// whole point: <b>"could not read" is not "was empty."</b>
/// </para>
/// <para>
/// These tests are two-sided on purpose. Each asserts the new behaviour AND names
/// the old behaviour it forbids, so a regression cannot pass by being merely quiet.
/// </para>
/// <para>
/// <b>MEASURED against the old code (2026-08-24), not assumed.</b> The bare-catch
/// version was restored and this suite re-run: <c>2 of 6 FAILED</c>. Which ones
/// discriminate, and which do not, matters more than the headline:
/// </para>
/// <list type="bullet">
/// <item><description><c>A_corrupt_file_is_MOVED_ASIDE…</c> — <b>FAILS on old</b>
/// ("found 0" sidecars). A genuine two-sided discriminator.</description></item>
/// <item><description><c>A_LOCKED_file_…THROW_and_leaves_the_history_INTACT</c> —
/// <b>FAILS on old</b>, but only on the MESSAGE assertion (old surfaces a raw
/// "process cannot access the file"). Read on.</description></item>
/// <item><description>The two remaining lock tests <b>PASSED on the old code too</b>
/// — vacuously. On Windows a <c>FileShare.None</c> holder blocks the WRITE as well
/// as the read, so the old code could not destroy the file in this fixture either.
/// The history-preservation assertions therefore do NOT carry the proof; the
/// message assertion does.</description></item>
/// </list>
/// <para>
/// ⛔ So the destructive scenario itself — read fails, lock releases, write then
/// SUCCEEDS over an emptied accumulator — is <b>not reproduced here</b>; it is a
/// race this fixture cannot construct deterministically. What is proven is that
/// the two failure modes are now DISTINGUISHED and that a read failure can no
/// longer reach the write at all. Do not cite this suite as proof that the race
/// was reproduced and fixed.
/// </para>
/// </summary>
public sealed class BenchMetricsAppendTests : IDisposable
{
    private readonly string _dir;

    public BenchMetricsAppendTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-metrics-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir; best effort */ }
    }

    /// <summary>
    /// Reached by reflection rather than by widening the method or adding
    /// InternalsVisibleTo — this tree has ~15 concurrent writers and the csproj is
    /// shared. The lookup is ASSERTED, not null-checked into a skip: a rename must
    /// fail this suite loudly. A skipped guard reads like a passed one.
    /// </summary>
    private static void AppendMetrics(string path, RunSummary summary)
    {
        var m = typeof(BenchCommand).GetMethod(
            "AppendMetrics", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(m is not null,
            "BenchCommand.AppendMetrics was not found by reflection. If it was renamed or " +
            "its signature changed, FIX THIS TEST — do not delete it. Without it, the " +
            "history-destroying regression it guards has no coverage at all.");

        try { m!.Invoke(null, [path, summary]); }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Unwrap so callers can assert on the real exception type.
            throw tie.InnerException;
        }
    }

    private static RunSummary Summary(string runId) => new()
    {
        RunId = runId,
        Suite = "suite-under-test",
        Profile = "profile-under-test",
        Model = "model-under-test",
    };

    private static List<JsonElement> Read(string path) =>
        JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(path)) ?? [];

    private static string RunIdAt(string path, int i) =>
        Read(path)[i].GetProperty("run_id").GetString()!;

    // ---------------------------------------------------------------- healthy path

    [Fact]
    public void A_missing_file_is_created_with_exactly_one_entry()
    {
        var path = Path.Combine(_dir, "metrics.json");
        AppendMetrics(path, Summary("run-1"));

        Assert.True(File.Exists(path));
        Assert.Single(Read(path));
        Assert.Equal("run-1", RunIdAt(path, 0));
    }

    [Fact]
    public void Successive_runs_ACCUMULATE_rather_than_replace()
    {
        // The healthy null. Without this, "history preserved" tests could all pass
        // against a method that never writes anything at all.
        var path = Path.Combine(_dir, "metrics.json");
        AppendMetrics(path, Summary("run-1"));
        AppendMetrics(path, Summary("run-2"));
        AppendMetrics(path, Summary("run-3"));

        Assert.Equal(3, Read(path).Count);
        Assert.Equal("run-1", RunIdAt(path, 0));
        Assert.Equal("run-3", RunIdAt(path, 2));
    }

    // ---------------------------------------------------- corrupt: preserve, never delete

    [Fact]
    public void A_corrupt_file_is_MOVED_ASIDE_and_its_bytes_survive_verbatim()
    {
        var path = Path.Combine(_dir, "metrics.json");
        const string garbage = "{ this is not valid json at all ][";
        File.WriteAllText(path, garbage);

        AppendMetrics(path, Summary("run-after-corruption"));

        var aside = Directory.GetFiles(_dir, "metrics.json.corrupt-*");
        Assert.True(aside.Length == 1,
            $"Expected the corrupt file to be preserved alongside, found {aside.Length}. " +
            "Mark's standing rule is that nothing is deleted; moving aside is the substitute.");

        // The ORIGINAL BYTES, not a summary of them.
        Assert.Equal(garbage, File.ReadAllText(aside[0]));

        // And the fresh file really did start fresh.
        Assert.Single(Read(path));
        Assert.Equal("run-after-corruption", RunIdAt(path, 0));
    }

    // ------------------------------------------- transient lock: REFUSE, never overwrite

    [Fact]
    public void A_LOCKED_file_makes_the_append_THROW_and_leaves_the_history_INTACT()
    {
        // This is the finding. A lock is not corruption, and the old code could not
        // tell them apart — both landed in `catch { }` and both destroyed the history.
        var path = Path.Combine(_dir, "metrics.json");
        AppendMetrics(path, Summary("run-1"));
        AppendMetrics(path, Summary("run-2"));
        var before = File.ReadAllText(path);

        using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => AppendMetrics(path, Summary("run-3")));

            // The message must say the run was NOT recorded. A silent failure here is
            // how a lower bound gets published as a measurement.
            Assert.Contains("Refusing to write", ex.Message);
            Assert.Contains("NOT recorded", ex.Message);
        }

        // The decisive assertion: two entries, byte-identical, run-3 absent.
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(2, Read(path).Count);
        Assert.DoesNotContain("run-3", File.ReadAllText(path));
    }

    [Fact]
    public void A_lock_does_NOT_leave_a_corrupt_sidecar_behind()
    {
        // Guards the mirror-image mistake: mislabelling a transient lock AS corruption
        // would move a perfectly good history aside and start fresh — data preserved,
        // but the running file silently truncated and the console reporting a fresh
        // start. Refusing is the only correct response to "could not read".
        var path = Path.Combine(_dir, "metrics.json");
        AppendMetrics(path, Summary("run-1"));

        using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => AppendMetrics(path, Summary("run-2")));
        }

        Assert.Empty(Directory.GetFiles(_dir, "metrics.json.corrupt-*"));
        Assert.Single(Read(path));
    }

    [Fact]
    public void The_history_is_still_appendable_AFTER_the_lock_is_released()
    {
        // Proves the refusal is a refusal, not a latch. A guard that permanently
        // disables the thing it guards is its own outage.
        var path = Path.Combine(_dir, "metrics.json");
        AppendMetrics(path, Summary("run-1"));

        using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => AppendMetrics(path, Summary("run-2")));
        }

        AppendMetrics(path, Summary("run-3"));

        Assert.Equal(2, Read(path).Count);
        Assert.Equal("run-1", RunIdAt(path, 0));
        Assert.Equal("run-3", RunIdAt(path, 1));
    }
}
