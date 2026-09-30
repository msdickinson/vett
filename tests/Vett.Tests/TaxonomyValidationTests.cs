using Vett.Bench.Team;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Tests;

// Tier 1.14 — locks two invariants:
//   1. Every team-bench instance under suites/ declares a
//      taxonomy block that conforms to defaults/taxonomy.yaml.
//   2. The taxonomy schema itself loads cleanly and has the 9 starting
//      dimensions the roadmap calls out.
//
// Without this test, adding a new instance without a taxonomy block
// would silently produce un-sliceable calibration data — defeating
// Tier 1.16/1.17. The schema is the contract; the test enforces it.
public class TaxonomyValidationTests
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Resolve repo paths relative to the test assembly location. The test
    // assembly lives at tests/Vett.Tests/bin/<cfg>/net10.0/, so
    // walking up five levels (net10.0 → cfg → bin → Vett.Tests → tests)
    // lands at the repo root.
    private static string RepoVettDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(TaxonomyValidationTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    private static string DefaultsTaxonomyPath()
        => Path.Combine(RepoVettDir(), "defaults", "taxonomy.yaml");

    private static string SuitesDir()
        => Path.Combine(RepoVettDir(), "suites");

    [Fact]
    public void Taxonomy_Schema_Loads_With_Nine_Starting_Dimensions()
    {
        var schema = TaxonomySchema.LoadFromFile(DefaultsTaxonomyPath());

        Assert.Equal(1, schema.SchemaVersion);
        Assert.Equal(9, schema.Dimensions.Count);

        var names = schema.Dimensions.Select(d => d.Name).ToHashSet();
        // The 9 starting dimensions per internal note v2-roadmap.md Tier 1.14.
        // Any rename means the roadmap is out of sync — bump schema_version
        // and update the roadmap before changing this assertion.
        Assert.Contains("file_count_bucket", names);
        Assert.Contains("has_network_io", names);
        Assert.Contains("ambiguity_level", names);
        Assert.Contains("complexity_tier", names);
        Assert.Contains("requires_test_writing", names);
        Assert.Contains("touches_state_or_persistence", names);
        Assert.Contains("novel_api_vs_known", names);
        Assert.Contains("requires_cross_file_changes", names);
        Assert.Contains("requires_external_research", names);

        // Every dimension must declare at least 2 allowed values — a
        // 1-value enum can't differentiate anything.
        foreach (var dim in schema.Dimensions)
            Assert.True(dim.Values.Count >= 2, $"dimension '{dim.Name}' has fewer than 2 allowed values");
    }

    [Fact]
    public void Every_TeamBench_Instance_Has_Valid_Taxonomy()
    {
        var schema = TaxonomySchema.LoadFromFile(DefaultsTaxonomyPath());
        var suitesDir = SuitesDir();
        var suiteFiles = Directory.GetFiles(suitesDir, "team-*.yaml");

        Assert.NotEmpty(suiteFiles);

        var allIssues = new List<string>();
        var instancesChecked = 0;

        foreach (var path in suiteFiles)
        {
            var suite = D.Deserialize<TeamBenchSuite>(File.ReadAllText(path));
            foreach (var instance in suite.Instances)
            {
                instancesChecked++;
                foreach (var issue in TaxonomyValidator.Validate(instance, schema))
                    allIssues.Add($"{Path.GetFileName(path)} :: {issue.InstanceId} :: {issue.Message}");
            }
        }

        // Track the count so a future suite-add doesn't accidentally
        // ship without tagging — the count must stay non-zero AND the
        // issues list must stay empty.
        Assert.True(instancesChecked >= 57,
            $"expected at least 57 bench instances across team-*.yaml, found {instancesChecked} — did a suite get deleted, or is the test resolving the wrong dir?");
        Assert.Empty(allIssues);
    }
}
