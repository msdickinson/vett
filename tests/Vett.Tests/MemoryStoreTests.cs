using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// Covers the persistent-memory store: file layout, MEMORY.md index
/// generation, slug sanitization, and the loader's prompt envelope.
/// Each test runs in a per-test temp directory tree so we don't
/// touch the dev's real .vett/memory/.
/// </summary>
public class MemoryStoreTests : IDisposable
{
    private readonly string _ws;

    public MemoryStoreTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), "vett-mem-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_ws);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { }
    }

    [Fact]
    public void Sanitize_LowercasesAndStripsBadChars()
    {
        Assert.Equal("user_role", MemoryStore.Sanitize("User Role"));
        Assert.Equal("feedback_terse", MemoryStore.Sanitize("feedback/terse!!"));
        Assert.Equal("project_q2", MemoryStore.Sanitize("project Q2"));
        Assert.Equal("foo-bar", MemoryStore.Sanitize("foo-bar"));
        Assert.Equal("", MemoryStore.Sanitize("   "));
        Assert.Equal("", MemoryStore.Sanitize(""));
    }

    [Fact]
    public void Save_WritesTopicFileWithFrontmatter()
    {
        MemoryStore.Save(_ws, "user_role", "user", "Mark is a senior .NET dev", "Body content here.");

        var file = MemoryStore.TopicPath(_ws, "user_role");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("---", content);
        Assert.Contains("name: user_role", content);
        Assert.Contains("type: user", content);
        Assert.Contains("description: Mark is a senior .NET dev", content);
        Assert.Contains("Body content here.", content);
    }

    [Fact]
    public void Save_ReturnsStatusString()
    {
        var status = MemoryStore.Save(_ws, "test_one", "project", "test desc", "body");
        Assert.Contains("test_one", status);
        Assert.Contains("project", status);
    }

    [Fact]
    public void Save_BuildsIndexGroupedByType()
    {
        MemoryStore.Save(_ws, "user_role", "user", "User is a dev", "...");
        MemoryStore.Save(_ws, "feedback_terse", "feedback", "Wants short replies", "...");
        MemoryStore.Save(_ws, "project_q2", "project", "Q2 deadline approaching", "...");

        var index = File.ReadAllText(MemoryStore.IndexPath(_ws));
        Assert.Contains("## User", index);
        Assert.Contains("## Feedback", index);
        Assert.Contains("## Project", index);
        Assert.Contains("user_role.md", index);
        Assert.Contains("feedback_terse.md", index);
        Assert.Contains("project_q2.md", index);
        // Reference section should be absent (no entries).
        Assert.DoesNotContain("## Reference", index);
    }

    [Fact]
    public void Save_OverwritesExistingEntry()
    {
        MemoryStore.Save(_ws, "user_role", "user", "Old desc", "Old body");
        MemoryStore.Save(_ws, "user_role", "user", "New desc", "New body");

        var file = File.ReadAllText(MemoryStore.TopicPath(_ws, "user_role"));
        Assert.Contains("New desc", file);
        Assert.Contains("New body", file);
        Assert.DoesNotContain("Old desc", file);
        Assert.DoesNotContain("Old body", file);

        // Index should still have exactly one entry for user_role (regenerated from disk).
        var index = File.ReadAllText(MemoryStore.IndexPath(_ws));
        var occurrences = index.Split("user_role.md").Length - 1;
        // Each entry shows up twice in the index line: `[user_role.md](user_role.md)`.
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public void Save_NormalizesUnknownTypeToProject()
    {
        MemoryStore.Save(_ws, "bogus_type", "definitely-not-a-type", "desc", "body");

        var file = File.ReadAllText(MemoryStore.TopicPath(_ws, "bogus_type"));
        Assert.Contains("type: project", file);
    }

    [Fact]
    public void Save_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() => MemoryStore.Save(_ws, "   ", "user", "", ""));
    }

    [Fact]
    public void Delete_RemovesTopicAndIndexEntry()
    {
        MemoryStore.Save(_ws, "user_role", "user", "desc", "body");
        Assert.True(File.Exists(MemoryStore.TopicPath(_ws, "user_role")));

        var status = MemoryStore.Delete(_ws, "user_role");
        Assert.Contains("Removed", status);

        Assert.False(File.Exists(MemoryStore.TopicPath(_ws, "user_role")));
        var index = File.ReadAllText(MemoryStore.IndexPath(_ws));
        Assert.DoesNotContain("user_role.md", index);
    }

    [Fact]
    public void Delete_NonExistent_DoesNotThrow()
    {
        var status = MemoryStore.Delete(_ws, "ghost_entry");
        // Idempotent — no exception even when the entry never existed.
        Assert.Contains("did not exist", status);
    }

    [Fact]
    public void LoadIndex_ReturnsEmpty_WhenNoMemory()
    {
        Assert.Equal("", MemoryStore.LoadIndex(_ws));
    }

    [Fact]
    public void LoadIndex_ReturnsContent_AfterSave()
    {
        MemoryStore.Save(_ws, "user_role", "user", "Senior .NET dev", "...");
        var loaded = MemoryStore.LoadIndex(_ws);
        Assert.Contains("user_role.md", loaded);
        Assert.Contains("Senior .NET dev", loaded);
    }

    [Fact]
    public void LoadIndex_TruncatesAtCap()
    {
        // Build an index file with way more lines than the cap, by
        // creating that many memory entries.
        for (int i = 0; i < 250; i++)
        {
            MemoryStore.Save(_ws, $"entry_{i:D4}", "project", $"description {i}", $"body {i}");
        }

        var loaded = MemoryStore.LoadIndex(_ws);
        var lineCount = loaded.Split('\n').Length;
        // We expect at MOST IndexLineCap lines + one truncation marker line.
        Assert.True(lineCount <= MemoryStore.IndexLineCap + 2,
            $"expected ≤ {MemoryStore.IndexLineCap + 2} lines after truncation; got {lineCount}");
        Assert.Contains("truncated", loaded);
    }

    [Fact]
    public void Apply_WrapsInPersistentMemoryBlock()
    {
        MemoryStore.Save(_ws, "user_role", "user", "Senior .NET dev", "...");
        var result = MemoryStore.Apply("BASE-PROMPT", _ws);

        Assert.StartsWith("<persistent_memory>", result);
        Assert.Contains("</persistent_memory>", result);
        Assert.Contains("user_role.md", result);
        Assert.EndsWith("BASE-PROMPT", result);
    }

    [Fact]
    public void Apply_NoMemory_ReturnsBasePromptUnchanged()
    {
        Assert.Equal("BASE-PROMPT", MemoryStore.Apply("BASE-PROMPT", _ws));
    }

    [Fact]
    public void IndexRebuild_DiscoversManuallyDroppedFiles()
    {
        // Save once via the API to bootstrap the directory.
        MemoryStore.Save(_ws, "real_one", "user", "via API", "...");

        // Drop a memory file directly on disk (e.g. someone hand-edits)
        // and trigger a reindex by saving anything.
        var manualPath = Path.Combine(MemoryStore.MemoryDir(_ws), "manual.md");
        File.WriteAllText(manualPath,
            "---\nname: manual\ntype: reference\ndescription: by hand\n---\n\nbody");
        MemoryStore.Save(_ws, "real_two", "feedback", "second", "...");

        var index = File.ReadAllText(MemoryStore.IndexPath(_ws));
        Assert.Contains("manual.md", index);
        Assert.Contains("by hand", index);
    }

    [Fact]
    public void Save_CollidingSlugsOverwrite_DocumentsLossySanitization()
    {
        // Sanitize is lossy: "User Role", "user.role", and "user_role" all
        // sanitize to the same slug "user_role". The store uses the slug as
        // the filename, so the second save overwrites the first — there's
        // no per-display-name disambiguation. Pinning this so an accidental
        // sanitizer change (e.g. preserving casing or adding suffixes on
        // collision) doesn't silently break agents that rely on the
        // current "name picks the same slot" behavior.
        Assert.Equal("user_role", MemoryStore.Sanitize("User Role"));
        Assert.Equal("user_role", MemoryStore.Sanitize("user.role"));
        Assert.Equal("user_role", MemoryStore.Sanitize("user_role"));

        MemoryStore.Save(_ws, "User Role", "user", "first save", "first body");
        MemoryStore.Save(_ws, "user.role", "feedback", "second save", "second body");

        // Only one topic file exists on disk, holding the most-recent values.
        var dir = MemoryStore.MemoryDir(_ws);
        var topicFiles = Directory.GetFiles(dir, "*.md")
            .Select(Path.GetFileName)
            .Where(n => !string.Equals(n, "MEMORY.md", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(topicFiles);
        Assert.Equal("user_role.md", topicFiles[0]);

        var content = File.ReadAllText(MemoryStore.TopicPath(_ws, "user_role"));
        Assert.Contains("type: feedback", content);
        Assert.Contains("second save", content);
        Assert.Contains("second body", content);
        Assert.DoesNotContain("first save", content);
        Assert.DoesNotContain("first body", content);

        // Index regenerates from disk, so it should also have a single
        // entry — under the second save's type heading.
        var index = File.ReadAllText(MemoryStore.IndexPath(_ws));
        var occurrences = index.Split("user_role.md").Length - 1;
        Assert.Equal(2, occurrences);
        Assert.Contains("## Feedback", index);
        Assert.DoesNotContain("## User", index);
    }

    [Fact]
    public void Save_HandlesYamlMetacharsInDescription()
    {
        // Description with `:` would corrupt YAML if not quoted.
        MemoryStore.Save(_ws, "with_colon", "user", "before: after", "body");

        // Re-load via the frontmatter parser to confirm round-trip.
        var (meta, _) = ProjectInstructions.ParseFrontmatter(
            File.ReadAllText(MemoryStore.TopicPath(_ws, "with_colon")));
        // Description should round-trip including the colon. The YAML
        // escape uses double-quotes around the value, which the
        // ParseFrontmatter quote-stripper unwraps.
        Assert.Equal("before: after", meta["description"]);
    }
}
