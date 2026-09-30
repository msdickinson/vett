using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace Vett.Config;

// Tier 1A scaffolding (v2-roadmap.md). These types declare the phase
// pipeline CONTRACTS — what each phase kind outputs, what config it
// carries. They are NOT wired into Coordinator yet; the single-team
// loop still runs as it does today. Tier 1B is when Coordinator gets
// refactored to drive multi-phase pipelines from these definitions.
//
// Naming convention: kebab-case in YAML, snake_case JSON output. The
// JSON shape is the contract enforced when phases serialize their
// artifacts to `.vet/phase-{N}-{name}.json` (Tier 1.7).

/// <summary>
/// The five typed phase kinds in a Vet pipeline. Each kind has a
/// distinct enforced output schema (see <c>Phase*Output</c> types).
/// `Generic` is the only kind that carries no schema — it's the
/// backwards-compatible "today's single-team chat loop" mode.
/// </summary>
public enum PhaseKind
{
    Generic,
    Planner,
    Implementer,
    Reviewer,
    Aggregator,
}

/// <summary>
/// A single phase in a pipeline. `kind` selects the typed output
/// contract; `team` (optional) points at a TeamConfig profile slice
/// for the agents running this phase; `model_override` / `temperature`
/// / `enable_thinking` let each phase route to a different inference
/// setup per Tier 1.8.
/// </summary>
public sealed class PhaseDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "kind")]
    public PhaseKind Kind { get; set; } = PhaseKind.Generic;

    [YamlMember(Alias = "system_prompt")]
    public string? SystemPrompt { get; set; }

    [YamlMember(Alias = "system_prompt_file")]
    public string? SystemPromptFile { get; set; }

    [YamlMember(Alias = "tools")]
    public List<string>? Tools { get; set; }

    /// <summary>Per-phase model override (Tier 1.8). Null inherits from base LLM config.</summary>
    [YamlMember(Alias = "model_override")]
    public string? ModelOverride { get; set; }

    [YamlMember(Alias = "temperature")]
    public double? Temperature { get; set; }

    [YamlMember(Alias = "enable_thinking")]
    public bool? EnableThinking { get; set; }

    /// <summary>Shell command run BEFORE the phase starts (Tier 1.10). Output goes into the phase's context.</summary>
    [YamlMember(Alias = "pre_hook")]
    public string? PreHook { get; set; }

    /// <summary>Shell command run AFTER the phase produces its artifact (Tier 1.10).</summary>
    [YamlMember(Alias = "post_hook")]
    public string? PostHook { get; set; }

    /// <summary>
    /// Whether this phase sees the prior phase's transcript (false) or
    /// only its artifacts (true). Reviewer kinds MUST set this to true
    /// per Tier 1.5 — fresh context is load-bearing for the review to
    /// be independent.
    /// </summary>
    [YamlMember(Alias = "fresh_context")]
    public bool FreshContext { get; set; }
}

/// <summary>
/// The top-level pipeline definition. Loaded from
/// <c>pipelines/*.yaml</c>. Single-phase pipelines are
/// allowed and trivial (default behavior == today's single-team loop
/// wrapped in one `kind: generic` phase).
/// </summary>
public sealed class PipelineDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [YamlMember(Alias = "phases")]
    public List<PhaseDefinition> Phases { get; set; } = new();
}

// =================================================================
// Output schemas — the typed artifact each phase kind must emit.
// Serialization is snake_case JSON (matches the rest of the wire).
// =================================================================

/// <summary>
/// Multi-dimensional confidence shared by Planner / Implementer /
/// Reviewer outputs. Each axis 0.0–1.0. Stored separately from
/// "concerns" so we can compute Brier scores per axis later.
/// </summary>
public sealed class PhaseConfidence
{
    [JsonPropertyName("correctness")]  public double Correctness { get; set; }
    [JsonPropertyName("completeness")] public double Completeness { get; set; }
    [JsonPropertyName("edge_cases")]   public double EdgeCases { get; set; }
    [JsonPropertyName("performance")]  public double Performance { get; set; }
}

public sealed class PhaseConcern
{
    [JsonPropertyName("severity")]    public string Severity { get; set; } = "low";  // low | med | high | blocker
    [JsonPropertyName("category")]    public string Category { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

/// <summary>Output contract for `kind: planner` (Tier 1.3).</summary>
public sealed class PhasePlannerOutput
{
    [JsonPropertyName("schema_version")]      public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("kind")]                public string Kind { get; set; } = "planner";
    [JsonPropertyName("classification")]      public Dictionary<string, string>? Classification { get; set; }
    [JsonPropertyName("plan")]                public List<string> Plan { get; set; } = new();
    [JsonPropertyName("confidence")]          public PhaseConfidence Confidence { get; set; } = new();
    [JsonPropertyName("similar_past_tasks")]  public List<string>? SimilarPastTasks { get; set; }
    [JsonPropertyName("decomposition")]       public List<string>? Decomposition { get; set; }
}

/// <summary>Output contract for `kind: implementer` (Tier 1.4).</summary>
public sealed class PhaseImplementerOutput
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("kind")]           public string Kind { get; set; } = "implementer";
    [JsonPropertyName("artifacts")]      public List<string> Artifacts { get; set; } = new();
    [JsonPropertyName("diff")]           public string? Diff { get; set; }
    [JsonPropertyName("confidence")]     public PhaseConfidence Confidence { get; set; } = new();
    [JsonPropertyName("self_concerns")]  public List<PhaseConcern> SelfConcerns { get; set; } = new();
}

/// <summary>Output contract for `kind: reviewer` (Tier 1.5).</summary>
public sealed class PhaseReviewerOutput
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("kind")]           public string Kind { get; set; } = "reviewer";
    [JsonPropertyName("confidence")]     public PhaseConfidence Confidence { get; set; } = new();
    [JsonPropertyName("concerns")]       public List<PhaseConcern> Concerns { get; set; } = new();
    [JsonPropertyName("blocking")]       public bool Blocking { get; set; }
}

/// <summary>Output contract for `kind: aggregator` (Tier 3.2 — parallel-team aggregation).</summary>
public sealed class PhaseAggregatorOutput
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("kind")]           public string Kind { get; set; } = "aggregator";
    [JsonPropertyName("chosen")]         public string Chosen { get; set; } = "";
    [JsonPropertyName("rationale")]      public string Rationale { get; set; } = "";
    [JsonPropertyName("others")]         public List<string> Others { get; set; } = new();
}
