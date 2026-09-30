using System.Text.Json.Serialization;

namespace Vett.Live;

// Tier 1.24 scaffolding (v2-roadmap.md). The stable event-stream JSON
// shape for downstream consumers (Pattern Miner, AI Timeline, future
// Vet Chat). Today the session log writes free-form per-event records;
// this envelope formalizes the contract so consumers can subscribe to
// a file-tail / SSE stream and rely on a predictable schema.
//
// Not yet emitted as a stream — Tier 1.25 wires the emitter. Defining
// the envelope here so the wiring can target a stable type.

public sealed class EventEnvelope
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("ts")]             public string Timestamp { get; set; } = "";
    [JsonPropertyName("run_id")]         public string RunId { get; set; } = "";
    [JsonPropertyName("phase_index")]    public int? PhaseIndex { get; set; }
    [JsonPropertyName("phase_name")]     public string? PhaseName { get; set; }
    [JsonPropertyName("event_type")]     public string EventType { get; set; } = "";
    [JsonPropertyName("data")]           public Dictionary<string, object?>? Data { get; set; }
}

/// <summary>
/// Canonical event-type names emitted on the Tier 1.24 stream. Keeping
/// them as constants (not an enum) so adding a new event-type doesn't
/// require recompiling every consumer.
/// </summary>
public static class EventTypes
{
    public const string PhaseStarted     = "phase_started";
    public const string PhaseCompleted   = "phase_completed";
    public const string ArtifactProduced = "artifact_produced";
    public const string ConcernRaised    = "concern_raised";
    public const string ConfidenceEmitted = "confidence_emitted";
    public const string RunStarted       = "run_started";
    public const string RunCompleted     = "run_completed";
    public const string MalformedToolCall = "malformed_tool_call";  // already emitted today
}
