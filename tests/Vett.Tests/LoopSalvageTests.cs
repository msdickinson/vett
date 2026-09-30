using System.Text.Json;
using Vett.Agent;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>
/// ⛔ LAW 233 (EpicForge batch 9, 2026-09-09 06:30-06:42Z, VETT c676b1f):
/// the law-231 abort fired on the wire exactly as designed -- facts-file
/// creates stopped at ~12.3 K chars with tails at 3-7% (genuine facts files
/// read 19-31%) -- and then EVERY seat re-issued the same looping create,
/// three and four times in a row, until <c>truncated_response_exhausted</c>
/// ended the arm (s9b: 4 aborts, dead at 1,214 s, 0 lines banked). The
/// distinct prefix that arrived before the tail degenerated was thrown away
/// with the loop, so the next attempt was the same attempt.
///
/// <see cref="AgentLoop.SalvageCutCreate"/> now banks the longest leading run
/// of literally-distinct lines whose own tail still deflates honestly, and
/// reports how many repeating lines it dropped; a loop from line one still
/// yields nothing (the law-231 tests in <see cref="StreamedRawArgumentsTests"/>
/// assert that and stay green).
/// </summary>

public sealed class LoopSalvageTests
{
    private static string Create(string body)
        => JsonSerializer.Serialize(new { command = "create", path = "data/facts.js", file_text = body, security_risk = "LOW" });

    /// <summary>Distinct, fact-shaped lines: seeded random words so no deflate
    /// window folds them (a genuine 100-fact file read 19-31% on the wire).</summary>
    private static string[] HonestFacts(int count)
    {
        var rng = new Random(20260909);
        var buf = new byte[21];
        return Enumerable.Range(1, count).Select(i =>
        {
            rng.NextBytes(buf);
            return $"  \"Fact {i}: Saturn {Convert.ToBase64String(buf)} in issue #{rng.Next(1, 900)}.\",";
        }).ToArray();
    }

    private static AgentLoop.SalvagedCreate? Salvage(string raw)
        => AgentLoop.SalvageCutCreate("file_editor", raw, AgentLoop.TailCompressionPct(raw));

    [Fact]
    public void An_honest_prefix_before_a_literal_loop_is_banked_and_the_loop_dropped()
    {
        var honest = HonestFacts(80);
        var body = "export const FACTS = [\n" + string.Join("\n", honest) + "\n"
            + string.Concat(Enumerable.Repeat("  \"Saturn's page is a real Saturn page.\",\n", 400));
        var raw = Create(body);
        var tail = AgentLoop.TailCompressionPct(raw);
        Assert.True(tail is < AgentLoop.RepeatingTailCompressionPct, $"fixture must read as a loop, tail={tail}");

        var s = Salvage(raw);
        Assert.NotNull(s);
        Assert.Equal("data/facts.js", s.Path);
        // The header + 80 facts + the loop's FIRST turn (distinct until it repeats).
        Assert.True(s.Lines is >= 60 and <= 82, $"kept {s.Lines} lines");
        Assert.True(s.LoopedLines >= 399, $"dropped {s.LoopedLines} looped lines");
        Assert.False(s.Complete);
        Assert.Equal(tail, s.TailPct);
        Assert.True(s.Text.Contains(honest[59]), "the 60th fact is inside the banked prefix");
        Assert.False(s.Text.Contains("Saturn's page is a real Saturn page.\",\n  \"Saturn's page"), "no two looped lines survive");
        var keptTail = ArgumentTail.CompressionPct(s.Text);
        Assert.True(keptTail is >= AgentLoop.RepeatingTailCompressionPct, $"the banked text must read honest, tail={keptTail}");
    }

    [Fact]
    public void A_templated_loop_that_never_repeats_a_line_literally_is_trimmed_until_its_tail_reads_honest()
    {
        var honest = HonestFacts(40);
        var templated = Enumerable.Range(1, 400).Select(n => $"  \"Saturn page {n} is a real Saturn page number {n}.\",");
        var body = "export const FACTS = [\n" + string.Join("\n", honest) + "\n" + string.Join("\n", templated) + "\n";
        var raw = Create(body);
        var tail = AgentLoop.TailCompressionPct(raw);
        Assert.True(tail is < AgentLoop.RepeatingTailCompressionPct, $"fixture must read as a loop, tail={tail}");

        var s = Salvage(raw);
        Assert.NotNull(s);
        Assert.True(s.Lines >= AgentLoop.MinSalvagedDistinctLines, $"kept {s.Lines} lines");
        Assert.True(s.Lines < 441, $"kept {s.Lines} lines -- the templated loop was not trimmed");
        Assert.True(s.LoopedLines > 0);
        Assert.True(s.Text.Contains(honest[7]), "the honest facts lead the banked prefix");
        var keptTail = ArgumentTail.CompressionPct(s.Text);
        Assert.True(keptTail is null or >= AgentLoop.RepeatingTailCompressionPct, $"the banked text must read honest, tail={keptTail}");
    }

    [Fact]
    public void A_loop_from_the_first_line_still_yields_nothing()
    {
        var raw = Create(string.Concat(Enumerable.Repeat("Saturn's page is a real Saturn page.\n", 1500)));
        Assert.Null(Salvage(raw));

        // Six distinct lines then the loop: under MinSalvagedDistinctLines, nothing banked.
        var six = Create(string.Join("\n", HonestFacts(6)) + "\n" + string.Concat(Enumerable.Repeat("Saturn's page is a real Saturn page.\n", 1500)));
        Assert.Null(Salvage(six));
    }

    [Fact]
    public void An_honest_cap_cut_is_salvaged_whole_with_no_looped_lines()
    {
        var raw = Create(string.Join("\n", HonestFacts(300)));
        var tail = AgentLoop.TailCompressionPct(raw);
        Assert.True(tail is >= AgentLoop.RepeatingTailCompressionPct, $"fixture must read honest, tail={tail}");
        var s = Salvage(raw);
        Assert.NotNull(s);
        Assert.Equal(0, s.LoopedLines);
        Assert.True(s.Lines >= 299, $"kept {s.Lines}");
    }

    [Fact]
    public void Short_structural_lines_may_recur_inside_the_distinct_prefix()
    {
        var facts = HonestFacts(40);
        var body = string.Join("\n", facts.Select((f, i) => i % 5 == 4 ? f + "\n  ]," + "\n  [" : f)) + "\n";
        var kept = AgentLoop.DistinctPrefix(body);
        Assert.NotNull(kept);
        Assert.Equal(body, kept);
        Assert.Null(AgentLoop.DistinctPrefix(string.Concat(Enumerable.Repeat("Saturn's page is a real Saturn page.\n", 40))));
    }

    // ⛔ LAW 234 (batch 10, 2026-09-09 07:31-07:37Z, VETT 6c09abd): after the
    // law-233 salvage the seat continued with ONE str_replace / insert of
    // 32-50 K chars, the cap cut it, and `salvaged: null` threw it away whole
    // (s10a 12,288 tokens / 50,040 chars / tail 13%; s10c 8,192 / 32,513 /
    // 10%) -- only `create` was salvageable. The body of an insert or a
    // str_replace banks under the same rules once its anchor has arrived.

    private static string Cut(string json) => json[..^2]; // drop the closing quote + brace: a cap cut inside the body

    [Fact]
    public void A_cut_str_replace_body_is_banked_in_place_of_its_old_text()
    {
        var honest = HonestFacts(60);
        var body = string.Join("\n", honest) + "\n" + string.Concat(Enumerable.Repeat("  \"Saturn's page is a real Saturn page.\",\n", 400));
        var raw = Cut(JsonSerializer.Serialize(new { command = "str_replace", path = "src/facts/facts-01.js", old_str = "];", new_str = body }));
        var tail = AgentLoop.TailCompressionPct(raw);
        Assert.True(tail is < AgentLoop.RepeatingTailCompressionPct, $"fixture must read as a loop, tail={tail}");

        var s = AgentLoop.SalvageCutCreate("file_editor", raw, tail, out var reason);
        Assert.NotNull(s);
        Assert.Null(reason);
        Assert.Equal("str_replace", s.Command);
        Assert.Equal("];", s.OldStr);
        Assert.Equal("src/facts/facts-01.js", s.Path);
        Assert.True(s.Lines is >= 40 and <= 62, $"kept {s.Lines}");
        Assert.True(s.LoopedLines >= 399, $"dropped {s.LoopedLines}");
        Assert.True(s.Text.Contains(honest[39]));
    }

    [Fact]
    public void A_cut_insert_body_is_banked_after_its_line()
    {
        var raw = Cut(JsonSerializer.Serialize(new { command = "insert", path = "src/facts/facts-01.js", insert_line = 17, new_str = string.Join("\n", HonestFacts(300)) }));
        var tail = AgentLoop.TailCompressionPct(raw);
        Assert.True(tail is >= AgentLoop.RepeatingTailCompressionPct, $"fixture must read honest, tail={tail}");

        var s = AgentLoop.SalvageCutCreate("file_editor", raw, tail, out var reason);
        Assert.NotNull(s);
        Assert.Null(reason);
        Assert.Equal("insert", s.Command);
        Assert.Equal(17, s.InsertLine);
        Assert.Equal(0, s.LoopedLines);
        Assert.True(s.Lines >= 299, $"kept {s.Lines}");
        Assert.False(s.Complete);
    }

    [Fact]
    public void An_insert_whose_line_number_had_not_arrived_banks_nothing_and_says_so()
    {
        // insert_line AFTER new_str: cut inside the body, the anchor never came.
        var raw = Cut(JsonSerializer.Serialize(new { command = "insert", path = "a.js", new_str = string.Join("\n", HonestFacts(50)), insert_line = 3 }));
        var cutInBody = raw[..raw.LastIndexOf("insert_line", StringComparison.Ordinal)];
        Assert.Null(AgentLoop.SalvageCutCreate("file_editor", cutInBody, AgentLoop.TailCompressionPct(cutInBody), out var reason));
        Assert.Equal("insert_line_missing", reason);
    }

    [Fact]
    public void A_str_replace_whose_old_text_is_cut_or_empty_banks_nothing_and_says_so()
    {
        var cutInOld = "{\"command\":\"str_replace\",\"path\":\"a.js\",\"old_str\":\"export const FACTS = [\\n  \\\"one\\\",\\n";
        Assert.Null(AgentLoop.SalvageCutCreate("file_editor", cutInOld, null, out var reason));
        Assert.Equal("old_str_incomplete", reason);

        var empty = Cut(JsonSerializer.Serialize(new { command = "str_replace", path = "a.js", old_str = "", new_str = string.Join("\n", HonestFacts(50)) }));
        Assert.Null(AgentLoop.SalvageCutCreate("file_editor", empty, null, out reason));
        Assert.Equal("old_str_incomplete", reason);
    }

    [Fact]
    public void Every_null_salvage_names_its_branch()
    {
        Assert.Null(AgentLoop.SalvageCutCreate("terminal", "{\"command\":\"cat <<EOF\\n", null, out var r1));
        Assert.Equal("not_file_editor", r1);
        Assert.Null(AgentLoop.SalvageCutCreate("file_editor", Cut(JsonSerializer.Serialize(new { command = "view", path = "a.js", view_range = new[] { 1, 2 } })), null, out var r2));
        Assert.Equal("command_not_salvageable", r2);
        Assert.Null(AgentLoop.SalvageCutCreate("file_editor", "{\"command\":\"create\",\"path\":\"a.js\",\"file_text\":\"no newline yet", null, out var r3));
        Assert.Equal("no_complete_line", r3);
        var loop = Create(string.Concat(Enumerable.Repeat("Saturn's page is a real Saturn page.\n", 1500)));
        Assert.Null(AgentLoop.SalvageCutCreate("file_editor", loop, AgentLoop.TailCompressionPct(loop), out var r4));
        Assert.Equal("loop_no_distinct_prefix", r4);
    }

    // ---- LAW 237: a loop whose period hides from the 4 KB tail sample (s10a it.20, 2026-09-09 08:00Z) ----

    /// <summary>s10a it.20: 33 facts cycled ~22 times to 734 lines. The period
    /// (~2 KB) is a good fraction of the 4,096-char sample, so deflate found
    /// little to fold and the tail read 13% -- one point above the 12% bound
    /// -- and all 734 lines were banked as honest with looped_lines 0. The
    /// whole-body duplicate ruler sees 92% literal repeats and banks the
    /// 60 distinct lines instead.</summary>
    [Fact]
    public void ALongPeriodLoop_TheTailSamplerReadsAsHonest_IsCaughtByDuplicateMass()
    {
        var honest = HonestFacts(60);
        var cycles = Enumerable.Range(0, 12).SelectMany(_ => honest);
        var raw = Cut(Create(string.Join("\n", cycles)));
        var tail = AgentLoop.TailCompressionPct(raw);
        Assert.True(tail is >= AgentLoop.RepeatingTailCompressionPct, $"precondition: the tail sampler must read this as honest, tail={tail}");

        var s = AgentLoop.SalvageCutCreate("file_editor", raw, tail, out var reason);
        Assert.NotNull(s);
        Assert.Null(reason);
        Assert.Equal("duplicate_mass", s.LoopSignal);
        Assert.True(s.DuplicatePct is >= 85, $"duplicate_pct={s.DuplicatePct}");
        Assert.InRange(s.Lines, 45, 60);
        Assert.True(s.LoopedLines >= 660, $"looped_lines={s.LoopedLines}");
        Assert.False(s.Complete);
        // Every banked line is one of the 60 distinct facts, and none repeats.
        var banked = s.Text.Split('\n').Where(l => l.Trim().Length >= 16).ToArray();
        Assert.Equal(banked.Length, banked.Distinct().Count());
        Assert.All(banked, l => Assert.Contains(l, honest));
    }

    /// <summary>The tail ruler keeps its name when it is the one that fired.</summary>
    [Fact]
    public void AShortPeriodLoop_StillReportsTheTailSignal()
    {
        var honest = HonestFacts(60);
        var raw = Cut(Create(string.Join("\n", honest) + "\n" + string.Concat(Enumerable.Repeat("  \"Saturn's page is a real Saturn page.\",\n", 400))));
        var tail = AgentLoop.TailCompressionPct(raw);
        Assert.True(tail is < AgentLoop.RepeatingTailCompressionPct, $"fixture must read as a loop, tail={tail}");
        var s = AgentLoop.SalvageCutCreate("file_editor", raw, tail, out _);
        Assert.NotNull(s);
        Assert.Equal("tail", s.LoopSignal);
    }

    /// <summary>Control: 300 honest lines carry no duplicate mass and bank whole.</summary>
    [Fact]
    public void AnHonestBody_HasNoLoopSignal()
    {
        var raw = Cut(Create(string.Join("\n", HonestFacts(300))));
        var s = AgentLoop.SalvageCutCreate("file_editor", raw, AgentLoop.TailCompressionPct(raw), out _);
        Assert.NotNull(s);
        Assert.Null(s.LoopSignal);
        Assert.Equal(0, s.DuplicatePct);
        Assert.Equal(0, s.LoopedLines);
        Assert.InRange(s.Lines, 299, 300);
    }

    /// <summary>Control: a genuine file repeats a few lines (closing brackets,
    /// an assertion) -- 10 repeats among 110 content lines is 9%, not a mass.</summary>
    [Fact]
    public void AFewRepeatedLines_AreNotAMass()
    {
        var lines = HonestFacts(100).Concat(Enumerable.Repeat("  expect(facts.length).toBeGreaterThan(0);", 10));
        var raw = Cut(Create(string.Join("\n", lines)));
        var s = AgentLoop.SalvageCutCreate("file_editor", raw, AgentLoop.TailCompressionPct(raw), out _);
        Assert.NotNull(s);
        Assert.Null(s.LoopSignal);
        Assert.True(s.DuplicatePct is <= 10, $"duplicate_pct={s.DuplicatePct}");
        Assert.InRange(s.Lines, 109, 110);
    }

    /// <summary>The ruler needs a population: under 40 content lines it reads null, never a verdict.</summary>
    [Fact]
    public void DuplicateLinePct_NeedsFortyContentLines()
    {
        Assert.Null(AgentLoop.DuplicateLinePct(string.Join("\n", Enumerable.Repeat("  \"the same fact line, again and again\",", 39))));
        Assert.Equal(98, AgentLoop.DuplicateLinePct(string.Join("\n", Enumerable.Repeat("  \"the same fact line, again and again\",", 40))));
        Assert.Null(AgentLoop.DuplicateLinePct(string.Join("\n", Enumerable.Repeat("short", 500))));
    }
}
