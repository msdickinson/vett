using Vett.Bench.Team;

namespace Vett.Tests;

// Locks the new WorkspaceSetup.Adopt semantics added for Q3
// decomposition chains. The adopt path must:
//   - reject null/empty dirs (caller mistake, fail fast)
//   - reject non-existent dirs (caller passed a path that vanished)
//   - return the dir as-is when valid (no copy, no init, no seed)
public class WorkspaceAdoptTests
{
    [Fact]
    public void Adopt_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceSetup.Adopt(""));
        Assert.Throws<ArgumentException>(() => WorkspaceSetup.Adopt("   "));
    }

    [Fact]
    public void Adopt_NonexistentDir_Throws()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), "definitely-does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => WorkspaceSetup.Adopt(fakePath));
    }

    [Fact]
    public void Adopt_ExistingDir_ReturnsItUnchanged()
    {
        var ws = Path.Combine(Path.GetTempPath(), "vett-adopt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        try
        {
            // Drop a sentinel file so we can verify nothing was wiped
            var sentinel = Path.Combine(ws, "carry-over.txt");
            File.WriteAllText(sentinel, "from-prior-step");

            var adopted = WorkspaceSetup.Adopt(ws);
            Assert.Equal(ws, adopted);
            Assert.True(File.Exists(sentinel));
            Assert.Equal("from-prior-step", File.ReadAllText(sentinel));
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }
}
