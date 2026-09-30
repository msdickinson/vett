using YamlDotNet.Serialization;

namespace Vett.Runner;

/// <summary>
/// A bench-spec YAML declares WHAT to run: which manifest, which subset of instances
/// (by language/difficulty/type), and which agent profile drives the agents.
/// This is separate from the agent Profile (LLM config, tools, prompts) — the bench
/// spec is the "dataset selector"; the agent profile is the "solver config".
///
/// Resolved from bench-profiles/ search dirs (workspace → ~/.vett/bench-profiles/ → bundled defaults).
///
/// Example:
/// <code>
/// name: swe-rebench-easy-py
/// manifest: ~/.cache/vett/datasets/swe-rebench-v2.jsonl
/// filter:
///   language: python
///   difficulty: easy
/// agent_profile: openhands
/// working_dir: /testbed
/// </code>
/// </summary>
public sealed class BenchSpec
{
    [YamlMember(Alias = "name")]         public string Name { get; set; } = "";
    [YamlMember(Alias = "description")]  public string Description { get; set; } = "";

    /// <summary>Path to the manifest JSONL. Supports ~/ expansion.</summary>
    [YamlMember(Alias = "manifest")]     public string Manifest { get; set; } = "";

    [YamlMember(Alias = "filter")]       public BenchSpecFilter Filter { get; set; } = new();

    /// <summary>Agent profile name (e.g. "openhands"). Resolved via the same search dirs as vett run --profile.</summary>
    [YamlMember(Alias = "agent_profile")] public string AgentProfile { get; set; } = "";

    /// <summary>Working directory inside the sandbox. Defaults to /testbed.</summary>
    [YamlMember(Alias = "working_dir")] public string WorkingDir { get; set; } = "/testbed";
}

public sealed class BenchSpecFilter
{
    /// <summary>Filter to instances with this language (e.g. "python", "csharp"). Null = all.</summary>
    [YamlMember(Alias = "language")]   public string? Language { get; set; }

    /// <summary>Filter to instances with this difficulty (e.g. "easy", "medium", "hard"). Null = all.</summary>
    [YamlMember(Alias = "difficulty")] public string? Difficulty { get; set; }

    /// <summary>Filter to instances with this type (e.g. "bug", "feat"). Null = all.</summary>
    [YamlMember(Alias = "type")]       public string? Type { get; set; }
}
