using System.Text.Json;

namespace Vett.Config;

// Tier 1.7 scaffolding (v2-roadmap.md). Each phase in a pipeline writes
// ONE JSON artifact to `.vet/phase-{N}-{name}.json` under the workspace
// root. Workspace files (code, tests) carry forward to the next phase
// naturally via the filesystem; this artifact carries the typed phase
// output (PhaseXxxOutput) plus pipeline metadata so the next phase has
// a stable handoff.
//
// Not yet wired into Coordinator — Tier 1B work. Defining the read/write
// contract here so the Coordinator refactor can target a stable API.

public sealed class PhaseArtifact
{
    public int PhaseIndex { get; init; }
    public string PhaseName { get; init; } = "";
    public PhaseKind Kind { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    /// <summary>The typed output. Concrete type depends on <see cref="Kind"/>:
    /// PhasePlannerOutput / PhaseImplementerOutput / PhaseReviewerOutput /
    /// PhaseAggregatorOutput. Generic phases serialize a free-form dict.</summary>
    public object? Output { get; init; }
}

public static class PhaseArtifactStore
{
    /// <summary>The fixed root dir for phase artifacts inside a workspace.
    /// `.vet/` matches the calibration-DB convention from Tier 6.1.</summary>
    public const string ArtifactDirName = ".vet";

    /// <summary>Compute the artifact file path for a phase. Uses 2-digit
    /// zero-padded index so lexical sort matches chronological order
    /// for the first 100 phases.</summary>
    public static string PathFor(string workspaceRoot, int phaseIndex, string phaseName)
    {
        var dir = Path.Combine(workspaceRoot, ArtifactDirName);
        return Path.Combine(dir, $"phase-{phaseIndex:D2}-{Sanitize(phaseName)}.json");
    }

    public static void Write(string workspaceRoot, PhaseArtifact artifact)
    {
        var dir = Path.Combine(workspaceRoot, ArtifactDirName);
        Directory.CreateDirectory(dir);
        var path = PathFor(workspaceRoot, artifact.PhaseIndex, artifact.PhaseName);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(artifact, opts));
    }

    /// <summary>Read artifact bytes for a previous phase. Returns null
    /// if the file doesn't exist (e.g. first phase has no predecessor).</summary>
    public static string? ReadRaw(string workspaceRoot, int phaseIndex, string phaseName)
    {
        var path = PathFor(workspaceRoot, phaseIndex, phaseName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Strip filesystem-unsafe characters from the phase name —
    /// keeps the artifact path predictable even if a pipeline YAML uses
    /// spaces or unicode in `name:`.</summary>
    private static string Sanitize(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        return sb.ToString();
    }
}
