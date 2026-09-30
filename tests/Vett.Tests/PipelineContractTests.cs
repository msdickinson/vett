using System.Text.Json;
using Vett.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Tests;

// Tier 1A scaffolding. Locks the phase-output JSON wire format so
// downstream consumers (event-stream subscribers, AI Timeline,
// bench-report HTML, future Pattern Miner) can rely on a stable
// snake_case shape. Mirrors the convention in
// RunSummaryWireFormatTests + TeamBenchJsonWireFormatTests.
//
// Tier 1B work will wire these contracts into Coordinator; until then
// these tests just lock the SHAPE so the contract stays stable when
// the wiring lands.
public class PipelineContractTests
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static string PipelinesDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(PipelineContractTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", "..", "pipelines"));
    }

    [Fact]
    public void Pipeline_YAML_RoundTrips_CodingV1()
    {
        var path = Path.Combine(PipelinesDir(), "coding-v1.yaml");
        Assert.True(File.Exists(path), $"bundled pipeline missing: {path}");

        var pipeline = Yaml.Deserialize<PipelineDefinition>(File.ReadAllText(path));

        Assert.Equal("coding-v1", pipeline.Name);
        Assert.Equal(1, pipeline.SchemaVersion);
        Assert.Equal(3, pipeline.Phases.Count);

        Assert.Equal("plan", pipeline.Phases[0].Name);
        Assert.Equal(PhaseKind.Planner, pipeline.Phases[0].Kind);
        Assert.False(pipeline.Phases[0].FreshContext);

        Assert.Equal("implement", pipeline.Phases[1].Name);
        Assert.Equal(PhaseKind.Implementer, pipeline.Phases[1].Kind);

        Assert.Equal("review", pipeline.Phases[2].Name);
        Assert.Equal(PhaseKind.Reviewer, pipeline.Phases[2].Kind);
        // Reviewer MUST have fresh_context=true — roadmap Tier 1.5
        Assert.True(pipeline.Phases[2].FreshContext);
    }

    [Fact]
    public void PhasePlannerOutput_SerializesWith_SnakeCase()
    {
        var p = new PhasePlannerOutput {
            Classification = new() { ["complexity_tier"] = "2", ["ambiguity_level"] = "low" },
            Plan = ["Read X", "Edit Y", "Test Z"],
            Confidence = new() { Correctness = 0.9, Completeness = 0.85, EdgeCases = 0.7, Performance = 0.95 },
            SimilarPastTasks = ["task-abc"],
            Decomposition = null,
        };
        var json = JsonSerializer.Serialize(p);
        Assert.Contains("\"schema_version\":1", json);
        Assert.Contains("\"kind\":\"planner\"", json);
        Assert.Contains("\"classification\":", json);
        Assert.Contains("\"plan\":", json);
        Assert.Contains("\"confidence\":", json);
        Assert.Contains("\"correctness\":", json);
        Assert.Contains("\"completeness\":", json);
        Assert.Contains("\"edge_cases\":", json);
        Assert.Contains("\"performance\":", json);
        Assert.Contains("\"similar_past_tasks\":", json);
        Assert.Contains("\"decomposition\":", json);
    }

    [Fact]
    public void PhaseImplementerOutput_SerializesWith_SnakeCase()
    {
        var p = new PhaseImplementerOutput {
            Artifacts = ["src/Foo.cs", "tests/FooTests.cs"],
            Diff = "diff --git a/src/Foo.cs b/src/Foo.cs\n+pass",
            Confidence = new() { Correctness = 0.8, Completeness = 0.8, EdgeCases = 0.6, Performance = 0.9 },
            SelfConcerns = [
                new() { Severity = "med", Category = "edge_cases", Description = "Null input not covered" }
            ],
        };
        var json = JsonSerializer.Serialize(p);
        Assert.Contains("\"schema_version\":1", json);
        Assert.Contains("\"kind\":\"implementer\"", json);
        Assert.Contains("\"artifacts\":", json);
        Assert.Contains("\"diff\":", json);
        Assert.Contains("\"confidence\":", json);
        Assert.Contains("\"self_concerns\":", json);
        Assert.Contains("\"severity\":\"med\"", json);
        Assert.Contains("\"category\":\"edge_cases\"", json);
        Assert.Contains("\"description\":", json);
    }

    [Fact]
    public void PhaseReviewerOutput_SerializesWith_SnakeCase()
    {
        var p = new PhaseReviewerOutput {
            Confidence = new() { Correctness = 0.95, Completeness = 0.9, EdgeCases = 0.85, Performance = 0.9 },
            Concerns = [
                new() { Severity = "blocker", Category = "correctness", Description = "Returns wrong type on empty" },
                new() { Severity = "low",     Category = "style",       Description = "Variable naming inconsistent" },
            ],
            Blocking = true,
        };
        var json = JsonSerializer.Serialize(p);
        Assert.Contains("\"schema_version\":1", json);
        Assert.Contains("\"kind\":\"reviewer\"", json);
        Assert.Contains("\"concerns\":", json);
        Assert.Contains("\"blocking\":true", json);
        Assert.Contains("\"severity\":\"blocker\"", json);
    }

    [Fact]
    public void PhaseAggregatorOutput_SerializesWith_SnakeCase()
    {
        var p = new PhaseAggregatorOutput {
            Chosen = "candidate-2",
            Rationale = "Most complete diff; aligned with planner's plan",
            Others = ["candidate-1", "candidate-3"],
        };
        var json = JsonSerializer.Serialize(p);
        Assert.Contains("\"schema_version\":1", json);
        Assert.Contains("\"kind\":\"aggregator\"", json);
        Assert.Contains("\"chosen\":\"candidate-2\"", json);
        Assert.Contains("\"rationale\":", json);
        Assert.Contains("\"others\":", json);
    }
}
