using System.Text.Json;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// End-to-end smoke checks for the openhands profile after the 2026-04-26
/// fidelity fixes. These mirror what Runner.cs does at instance setup so a
/// regression that makes the profile silently load with empty fields fails
/// here, not in a 4-hour benchmark run.
/// </summary>
public class OpenHandsProfileSmokeTests
{
    private static string ProfilesDir =>
        Path.Combine(AppContext.BaseDirectory, "profiles");
    private static string SchemasDir =>
        Path.Combine(AppContext.BaseDirectory, "schemas");
    private static string TestdataDir =>
        Path.Combine(AppContext.BaseDirectory, "testdata");

    [Fact]
    public void OpenHandsProfile_LoadsAllFileReferences()
    {
        var path = Path.Combine(ProfilesDir, "openhands.yaml");
        Assert.True(File.Exists(path), $"openhands.yaml missing from bin: {path}");

        var profile = Yaml.LoadProfile(path);

        Assert.Equal(500, profile.MaxIterations);
        Assert.Equal(240, profile.TimeoutMinutes);
        Assert.Equal(1.0, profile.Llm.Temperature);
        Assert.Null(profile.Llm.TopP);

        // System prompt loaded from the testdata file. The byte-on-disk count
        // is 12103 (asserted in RefSystemPrompt_DeployedAndUnchanged); after
        // C# decodes UTF-8 to UTF-16, the string.Length code-unit count is
        // 12099 (two non-ASCII chars in <SECURITY> contribute the diff).
        Assert.NotEmpty(profile.SystemPrompt);
        Assert.Contains("OpenHands agent", profile.SystemPrompt);
        Assert.Equal(12099, profile.SystemPrompt.Length);

        // User template loaded from the new file reference (not the inline stub)
        Assert.NotEmpty(profile.UserTemplate);
        Assert.Contains("Phase 1. READING", profile.UserTemplate);
        Assert.Contains("Phase 7. VERIFICATION", profile.UserTemplate);
        Assert.Contains("{working_dir}", profile.UserTemplate);
        Assert.Contains("{problem_statement}", profile.UserTemplate);
        Assert.Contains("{base_commit}", profile.UserTemplate);

        Assert.Equal(new[] { "terminal", "file_editor", "task_tracker", "finish", "think" }, profile.Tools);
        Assert.Equal(new[] { "output_truncation", "submit_detector", "stuck_detector" }, profile.Middleware);
    }

    [Fact]
    public void OpenHandsProfile_UserTemplateRendersForSweBenchInstance()
    {
        var profile = Yaml.LoadProfile(Path.Combine(ProfilesDir, "openhands.yaml"));

        var rendered = profile.UserTemplate
            .Replace("{working_dir}", "/testbed")
            .Replace("{base_commit}", "abc123")
            .Replace("{problem_statement}", "The widget breaks when given an empty list.");

        Assert.DoesNotContain("{working_dir}", rendered);
        Assert.DoesNotContain("{base_commit}", rendered);
        Assert.DoesNotContain("{problem_statement}", rendered);
        Assert.Contains("/testbed", rendered);
        Assert.Contains("abc123", rendered);
        Assert.Contains("The widget breaks", rendered);
    }

    [Fact]
    public void FileEditorSchema_SubstitutesWorkingDirAndParsesAsJson()
    {
        // Mirrors the substitution Runner.cs does at instance setup.
        var schemaPath = Path.Combine(SchemasDir, "file_editor.json");
        Assert.True(File.Exists(schemaPath));

        var raw = File.ReadAllText(schemaPath);
        Assert.Contains("{working_dir}", raw);

        var cwdEscaped = JsonSerializer.Serialize("/testbed").Trim('"');
        var substituted = raw.Replace("{working_dir}", cwdEscaped);

        // Must still parse as valid JSON after substitution
        var doc = JsonDocument.Parse(substituted);
        var desc = doc.RootElement
            .GetProperty("function")
            .GetProperty("description")
            .GetString();

        Assert.NotNull(desc);
        Assert.DoesNotContain("{working_dir}", desc);
        Assert.Contains("Your current working directory is: /testbed", desc!);
    }

    [Fact]
    public void FileEditorSchema_SubstitutionSurvivesPathWithBackslashes()
    {
        // Defensive: confirm a Windows-style or backslash-bearing cwd doesn't
        // corrupt the JSON parse. SWE-bench cwds are always /testbed but a
        // future profile might pin a Windows path or one with quotes.
        var raw = File.ReadAllText(Path.Combine(SchemasDir, "file_editor.json"));
        var weirdCwd = "C:\\path\\with \"quotes\"";
        var cwdEscaped = JsonSerializer.Serialize(weirdCwd).Trim('"');
        var substituted = raw.Replace("{working_dir}", cwdEscaped);

        var doc = JsonDocument.Parse(substituted);
        var desc = doc.RootElement.GetProperty("function").GetProperty("description").GetString();
        Assert.Contains(weirdCwd, desc!);
    }

    [Fact]
    public void RefSystemPrompt_DeployedAndUnchanged()
    {
        // Locked at the byte-identical match against OpenHands SDK 1.14.0.
        // If this fails, either the prompt was edited or the build copy went stale.
        var path = Path.Combine(TestdataDir, "ref-system-prompt.txt");
        Assert.True(File.Exists(path));
        Assert.Equal(12103, new FileInfo(path).Length);
    }

    [Fact]
    public void RefSweBenchUserTemplate_DeployedWithExpectedShape()
    {
        var path = Path.Combine(TestdataDir, "ref-swebench-user-template.txt");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);

        // Quick structural checks. If these break, the template diverged from
        // the OpenHands V1 SWE-bench prompt and we should re-port it.
        Assert.Contains("Phase 1. READING", text);
        Assert.Contains("Phase 2. RUNNING", text);
        Assert.Contains("Phase 7. VERIFICATION", text);
        Assert.Contains("8. FINAL REVIEW", text);
        Assert.Contains("<issue_description>", text);
        Assert.Contains("</issue_description>", text);
    }
}
