using System.Text.Json;
using Vett.Config;
using Vett.Live;

namespace Vett.Tests;

public class PhaseArtifactStoreTests
{
    [Fact]
    public void PathFor_UsesTwoDigitZeroPaddedIndex()
    {
        var p = PhaseArtifactStore.PathFor("/tmp/ws", 1, "plan");
        Assert.EndsWith(Path.Combine(".vet", "phase-01-plan.json"), p);
        Assert.EndsWith(Path.Combine(".vet", "phase-13-review.json"),
            PhaseArtifactStore.PathFor("/tmp/ws", 13, "review"));
    }

    [Fact]
    public void PathFor_SanitizesFunkyPhaseNames()
    {
        var p = PhaseArtifactStore.PathFor("/tmp/ws", 2, "deep think / review");
        Assert.EndsWith(Path.Combine(".vet", "phase-02-deep-think---review.json"), p);
    }

    [Fact]
    public void WriteThenReadRaw_RoundTrips()
    {
        var ws = Path.Combine(Path.GetTempPath(), "vett-artifact-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(ws);
        try
        {
            var planner = new PhasePlannerOutput {
                Plan = ["step 1", "step 2"],
                Confidence = new() { Correctness = 0.8, Completeness = 0.75, EdgeCases = 0.6, Performance = 0.9 },
            };
            var artifact = new PhaseArtifact {
                PhaseIndex = 1,
                PhaseName = "plan",
                Kind = PhaseKind.Planner,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Output = planner,
            };
            PhaseArtifactStore.Write(ws, artifact);

            var raw = PhaseArtifactStore.ReadRaw(ws, 1, "plan");
            Assert.NotNull(raw);
            Assert.Contains("\"plan\"", raw);
            Assert.Contains("\"correctness\"", raw);
            // Snake_case keys preserved through Output object serialization
            Assert.Contains("step 1", raw);
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReadRaw_MissingFile_ReturnsNull()
    {
        var ws = Path.Combine(Path.GetTempPath(), "vett-artifact-test-empty-" + Path.GetRandomFileName());
        Directory.CreateDirectory(ws);
        try
        {
            Assert.Null(PhaseArtifactStore.ReadRaw(ws, 99, "ghost"));
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EventEnvelope_SerializesWith_SnakeCase()
    {
        var e = new EventEnvelope {
            Timestamp = "2026-05-12T08:00:00Z",
            RunId = "run-1",
            PhaseIndex = 2,
            PhaseName = "implement",
            EventType = EventTypes.ArtifactProduced,
            Data = new() { ["files_changed"] = 3, ["diff_lines"] = 47 },
        };
        var json = JsonSerializer.Serialize(e);
        Assert.Contains("\"schema_version\":1", json);
        Assert.Contains("\"ts\":\"2026-05-12T08:00:00Z\"", json);
        Assert.Contains("\"run_id\":\"run-1\"", json);
        Assert.Contains("\"phase_index\":2", json);
        Assert.Contains("\"phase_name\":\"implement\"", json);
        Assert.Contains("\"event_type\":\"artifact_produced\"", json);
        Assert.Contains("\"data\":", json);
    }

    [Fact]
    public void EventTypes_ConstantsMatchExpectedNames()
    {
        // Lock the spelling — consumers key off these strings.
        Assert.Equal("phase_started", EventTypes.PhaseStarted);
        Assert.Equal("phase_completed", EventTypes.PhaseCompleted);
        Assert.Equal("artifact_produced", EventTypes.ArtifactProduced);
        Assert.Equal("concern_raised", EventTypes.ConcernRaised);
        Assert.Equal("confidence_emitted", EventTypes.ConfidenceEmitted);
        Assert.Equal("run_started", EventTypes.RunStarted);
        Assert.Equal("run_completed", EventTypes.RunCompleted);
        Assert.Equal("malformed_tool_call", EventTypes.MalformedToolCall);
    }
}
