using System.Text.Json.Serialization;

namespace Vett.Runner;

/// <summary>
/// One instance's grading recipe, loaded from an eval-spec JSONL (one object per line).
/// Separate from the run manifest: the manifest says how to SOLVE (problem_statement,
/// image); the eval-spec says how to GRADE (which tests define success + how to run them).
///
/// Field names lock to snake_case to match the generated eval-spec file.
/// </summary>
public sealed class EvalSpec
{
    [JsonPropertyName("instance_id")] public string InstanceId { get; set; } = "";
    /// <summary>Pre-pulled bench-daemon image (repo cloned at RepoDir, deps installed, at base_commit).</summary>
    [JsonPropertyName("image")]        public string Image { get; set; } = "";
    /// <summary>Absolute repo dir inside the image (SWE-Rebench v2 = /&lt;repo-basename&gt;).</summary>
    [JsonPropertyName("repo_dir")]     public string RepoDir { get; set; } = "";
    /// <summary>Exact test command (its output must carry a parseable per-test verdict, e.g. pytest -rA).</summary>
    [JsonPropertyName("test_cmd")]     public string TestCmd { get; set; } = "";
    /// <summary>Gold test patch — always applied so the target tests exist.</summary>
    [JsonPropertyName("test_patch")]   public string TestPatch { get; set; } = "";
    /// <summary>Gold solution patch — used only for the --prove-gold self-check.</summary>
    [JsonPropertyName("gold_patch")]   public string GoldPatch { get; set; } = "";
    /// <summary>Tests that must flip fail→pass. resolved requires ALL of these PASSED.</summary>
    [JsonPropertyName("fail_to_pass")] public List<string> FailToPass { get; set; } = [];
    /// <summary>Regression tests that must stay passing. resolved requires ALL of these PASSED.</summary>
    [JsonPropertyName("pass_to_pass")] public List<string> PassToPass { get; set; } = [];
}
