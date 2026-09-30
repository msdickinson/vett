using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Bench.Team;

/// <summary>
/// Tier 1.14 task taxonomy schema. Loaded from
/// <c>defaults/taxonomy.yaml</c> (or any path passed to
/// <see cref="LoadFromFile"/>). The schema declares N dimensions, each
/// with a fixed enum of allowed string values. Every bench instance
/// must carry a <see cref="TeamBenchInstance.Taxonomy"/> dictionary
/// covering exactly these dimensions with values from the allowed set.
/// </summary>
public sealed class TaxonomySchema
{
    [YamlMember(Alias = "schema_version")]
    public int SchemaVersion { get; set; } = 1;

    public string Description { get; set; } = "";

    public List<TaxonomyDimension> Dimensions { get; set; } = new();

    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static TaxonomySchema LoadFromFile(string path)
        => D.Deserialize<TaxonomySchema>(File.ReadAllText(path));

    /// <summary>Look up the bundled taxonomy file relative to the
    /// running tool. Mirrors how suites/profiles are resolved — the
    /// tool's content-included copy ships next to the executable.</summary>
    public static string ResolveBundledPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "defaults", "taxonomy.yaml"),
            Path.Combine(baseDir, "taxonomy.yaml"),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Taxonomy schema not found in: {string.Join(", ", candidates)}");
    }
}

public sealed class TaxonomyDimension
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Values { get; set; } = new();
}

/// <summary>
/// Validates a <see cref="TeamBenchInstance"/>'s taxonomy block against
/// a loaded <see cref="TaxonomySchema"/>. Returns a list of issues —
/// empty list = clean.
/// </summary>
public static class TaxonomyValidator
{
    public sealed record Issue(string InstanceId, string Message);

    public static List<Issue> Validate(TeamBenchInstance instance, TaxonomySchema schema)
    {
        var issues = new List<Issue>();
        if (instance.Taxonomy is null || instance.Taxonomy.Count == 0)
        {
            issues.Add(new(instance.Id, "missing 'taxonomy:' block"));
            return issues;
        }

        var declared = new HashSet<string>(instance.Taxonomy.Keys, StringComparer.Ordinal);

        foreach (var dim in schema.Dimensions)
        {
            if (!instance.Taxonomy.TryGetValue(dim.Name, out var value))
            {
                issues.Add(new(instance.Id, $"missing taxonomy dimension '{dim.Name}'"));
                continue;
            }
            declared.Remove(dim.Name);
            if (!dim.Values.Contains(value, StringComparer.Ordinal))
            {
                issues.Add(new(instance.Id,
                    $"taxonomy.{dim.Name}='{value}' is not in allowed values [{string.Join(", ", dim.Values)}]"));
            }
        }

        foreach (var extra in declared)
            issues.Add(new(instance.Id, $"unknown taxonomy dimension '{extra}' (not in schema v{schema.SchemaVersion})"));

        return issues;
    }
}
