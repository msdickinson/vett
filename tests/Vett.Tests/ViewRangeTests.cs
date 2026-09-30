using System.Text.Json;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// file_editor `view_range` — the schema promised it since day one, the
/// implementation never read it. A member viewing a large file with
/// view_range [565, 700] silently got the whole file from line 1, which
/// output-truncation then clipped: the middle of any large file was
/// unreachable. Shakedown #49 implementer-4 requested [565, 700] five
/// times, never saw line 565, hallucinated the method signature there and
/// burned its budget on four str_replace no_match failures. These tests
/// pin the slice semantics the schema documents: 1-based inclusive,
/// [start, -1] = to EOF.
/// </summary>
public class ViewRangeTests
{
    private static string NumberedView(int lines)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= lines; i++) sb.AppendLine($"{i}\tline {i} content");
        return sb.ToString();
    }

    private static Dictionary<string, object?> ArgsWithRange(params int[] range)
    {
        // Arrive as JsonElement in production (OpenAI function-call args).
        var je = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(range));
        return new Dictionary<string, object?> { ["view_range"] = je };
    }

    [Fact]
    public void NoRange_FullView_Unchanged()
    {
        var view = NumberedView(50);
        Assert.Equal(view, Builtins.ApplyViewRange(view, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Range_SlicesInclusive_1Based()
    {
        var result = Builtins.ApplyViewRange(NumberedView(1281), ArgsWithRange(565, 700));
        Assert.Contains("565\tline 565 content", result);
        Assert.Contains("700\tline 700 content", result);
        Assert.DoesNotContain("564\tline 564", result);
        Assert.DoesNotContain("701\tline 701", result);
        Assert.Contains("(showing lines 565-700 of 1281)", result);
    }

    [Fact]
    public void Range_MinusOne_MeansToEof()
    {
        var result = Builtins.ApplyViewRange(NumberedView(30), ArgsWithRange(28, -1));
        Assert.Contains("28\tline 28", result);
        Assert.Contains("30\tline 30", result);
        Assert.DoesNotContain("27\tline 27", result);
        Assert.Contains("(showing lines 28-30 of 30)", result);
    }

    [Fact]
    public void Range_EndPastEof_ClampsQuietly()
    {
        var result = Builtins.ApplyViewRange(NumberedView(20), ArgsWithRange(15, 500));
        Assert.Contains("20\tline 20", result);
        Assert.Contains("(showing lines 15-20 of 20)", result);
    }

    [Fact]
    public void Range_FullyPastEof_ClampsToTail_NotError()
    {
        // Requested window (101 lines) exceeds the 20-line file -> shows the whole
        // file with an explicit note (never a silent dump, never an error).
        var result = Builtins.ApplyViewRange(NumberedView(20), ArgsWithRange(100, 200));
        Assert.DoesNotContain("Error:", result);
        Assert.Contains("beyond the file's 20 lines", result);
        Assert.Contains("20\tline 20 content", result);
    }

    [Fact]
    public void Range_FullyPastEof_ClampsToSameSizeTailWindow()
    {
        // [830,870] (window 41) on a 425-line file -> the LAST 41 lines (385-425),
        // anchored at the real end, plus the true line count. Model stops guessing.
        var result = Builtins.ApplyViewRange(NumberedView(425), ArgsWithRange(830, 870));
        Assert.DoesNotContain("Error:", result);
        Assert.Contains("beyond the file's 425 lines", result);
        Assert.Contains("showing the last 41", result);
        Assert.Contains("425\tline 425 content", result);
        Assert.Contains("385\tline 385 content", result);
        Assert.DoesNotContain("384\tline 384 content", result);
    }

    [Fact]
    public void Range_Malformed_FallsBackToFullView()
    {
        var view = NumberedView(10);
        Assert.Equal(view, Builtins.ApplyViewRange(view, ArgsWithRange(7)));          // 1 element
        Assert.Equal(view, Builtins.ApplyViewRange(view, ArgsWithRange(0, 5)));       // start < 1
        Assert.Equal(view, Builtins.ApplyViewRange(view, ArgsWithRange(9, 3)));       // end < start
        var junk = new Dictionary<string, object?>
        {
            ["view_range"] = JsonSerializer.Deserialize<JsonElement>("\"565-700\""),  // wrong type
        };
        Assert.Equal(view, Builtins.ApplyViewRange(view, junk));
    }

    [Fact]
    public void DirectoryListing_NoLineNumbers_PassesThrough()
    {
        var listing = "src\ntests\nREADME.md\n";
        Assert.Equal(listing, Builtins.ApplyViewRange(listing, ArgsWithRange(2, 3)));
    }

    [Fact]
    public void Range_TabsInsideContent_DoNotConfuseTheParser()
    {
        // File content may itself contain tabs; only the FIRST tab separates
        // the number from the content.
        var view = "1\tcol_a\tcol_b\n2\tx\ty\n3\tz\tw\n";
        var result = Builtins.ApplyViewRange(view, ArgsWithRange(2, 2));
        Assert.Contains("2\tx\ty", result);
        Assert.DoesNotContain("col_a", result);
        Assert.DoesNotContain("3\tz", result);
    }
}
