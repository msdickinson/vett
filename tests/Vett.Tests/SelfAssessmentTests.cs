using System.Text.Json;
using Vett.Bench.Team;

namespace Vett.Tests;

// Tier 1B Q4 — locks the parse + serialization shape for the
// self-assessment block. Called directly (the parser is internal, and
// Vett.Tests has InternalsVisibleTo) rather than by reflection: the old
// reflective binding meant a signature change surfaced as 8 runtime
// TargetParameterCountExceptions instead of a compile error.
public class SelfAssessmentTests
{
    private static (int? confidence, bool? predicted, string? reasoning) Parse(string text, bool captured = true)
    {
        var sa = Harness.ParseSelfAssessment(text, captured, captured ? "answered" : "refused_empty");
        return (sa.Confidence, sa.PredictedPass, sa.Reasoning);
    }

    [Fact]
    public void Parser_HappyPath_ExtractsBothFields()
    {
        var (conf, pred, reason) = Parse("CONFIDENCE: 87\nREASON: All assertions covered; tests pass cleanly.");
        Assert.Equal(87, conf);
        Assert.True(pred);
        Assert.Equal("All assertions covered; tests pass cleanly.", reason);
    }

    [Fact]
    public void Parser_LowConfidence_PredictsFail()
    {
        var (conf, pred, _) = Parse("CONFIDENCE: 30\nREASON: I'm not sure the edge case is handled.");
        Assert.Equal(30, conf);
        Assert.False(pred);
    }

    [Fact]
    public void Parser_Boundary_50_PredictsPass()
    {
        // 50 is the threshold; the spec says >= 50 → predicted_pass = true
        var (conf, pred, _) = Parse("CONFIDENCE: 50\nREASON: borderline.");
        Assert.Equal(50, conf);
        Assert.True(pred);
    }

    [Fact]
    public void Parser_CaseInsensitive_AndWhitespaceTolerant()
    {
        var (conf, pred, reason) = Parse("confidence :  72  \n  reason  : Works, edge cases covered. ");
        Assert.Equal(72, conf);
        Assert.True(pred);
        Assert.Equal("Works, edge cases covered.", reason);
    }

    [Fact]
    public void Parser_PrefersLastMatch_WhenThinkingThenAnswer()
    {
        var text = """
            Let me think. CONFIDENCE: 60 maybe.
            Actually re-reading the spec...

            CONFIDENCE: 85
            REASON: The implementation handles the null case explicitly.
            """;
        var (conf, _, reason) = Parse(text);
        Assert.Equal(85, conf);
        Assert.Equal("The implementation handles the null case explicitly.", reason);
    }

    [Fact]
    public void Parser_Garbage_ReturnsNulls_ButRawTextPreserved()
    {
        var text = "I think it probably worked. Not sure though.";
        var sa = Harness.ParseSelfAssessment(text, true, "answered");
        Assert.Null(sa.Confidence);
        Assert.Null(sa.PredictedPass);
        Assert.Null(sa.Reasoning);
        Assert.True(sa.Captured);
        Assert.Equal(text, sa.RawText);
    }

    [Fact]
    public void Parser_OutOfRange_Ignored()
    {
        var (conf, pred, _) = Parse("CONFIDENCE: 150\nREASON: silly.");
        Assert.Null(conf);
        Assert.Null(pred);
    }

    [Fact]
    public void Parser_NotCaptured_FlagSurfaced()
    {
        var sa = Harness.ParseSelfAssessment("", false, "refused_empty");
        Assert.False(sa.Captured);
        Assert.Null(sa.Confidence);
        Assert.Null(sa.RawText);
    }

    [Fact]
    public void WireFormat_SnakeCase_AllFields()
    {
        var run = new TeamBenchJsonRun {
            InstanceId = "demo",
            RunIndex = 0,
            Pass = true,
            WallClockSeconds = 12.3,
            LeaderIterations = 5,
            MemberIterations = new() { ["implementer-1"] = 4 },
            Assertions = new(),
            Taxonomy = null,
            SelfAssessment = new TeamBenchRunSelfAssessment {
                Captured = true,
                Confidence = 87,
                PredictedPass = true,
                Reasoning = "Tests all green; null path covered.",
                RawText = "CONFIDENCE: 87\nREASON: Tests all green; null path covered.",
            },
        };
        var json = JsonSerializer.Serialize(run);

        Assert.Contains("\"self_assessment\":", json);
        Assert.Contains("\"captured\":true", json);
        Assert.Contains("\"confidence\":87", json);
        Assert.Contains("\"predicted_pass\":true", json);
        Assert.Contains("\"reasoning\":", json);
        Assert.Contains("\"raw_text\":", json);
    }

    [Fact]
    public void WireFormat_NullSelfAssessment_Omitted_Or_Null()
    {
        var run = new TeamBenchJsonRun { InstanceId = "demo", Pass = true };
        var json = JsonSerializer.Serialize(run);
        // Field present but null — consumers should treat as "not captured"
        Assert.Contains("\"self_assessment\":null", json);
    }
}
