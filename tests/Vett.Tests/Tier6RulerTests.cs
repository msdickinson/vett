using Vett.Bench.Team;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Tests;

// Guards the tier-6 ruler (added 2026-08-24).
//
// WHY THIS EXISTS. team-games-tier6.yaml originally asserted ONLY
// `exists: true` on three files. On 2026-08-24 that gate scored two paid
// runs of g1-blocks-2p as PASS; hand re-grading found one of them had 3x
// CS1061 in IntegrationTests.cs, a TypeScript client that failed tsc, and
// a Dockerfile that never built the client. A g3-cycle run cleared all
// three existence assertions while shipping ZERO browser-client code.
// A GREEN RESULT ON AN UNINSPECTED VERIFIER IS NOT EVIDENCE.
//
// WHY IT ASSERTS BINDING, NOT JUST PARSING. TaxonomyValidationTests
// already deserializes every team-*.yaml — but the shared deserializer
// sets IgnoreUnmatchedProperties(), so a MISSPELLED assertion key is
// silently DROPPED rather than throwing. A parse-only test would stay
// green while the gate quietly reverted to existence-only. So this test
// reads the DESERIALIZED objects and requires the specs to be non-null
// with the right values — the positive conjunct, with a known healthy null.
public class Tier6RulerTests
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Same walk-up as TaxonomyValidationTests: bin/<cfg>/net10.0 → the repo root.
    private static string SuitePath()
    {
        var asmDir = Path.GetDirectoryName(typeof(Tier6RulerTests).Assembly.Location)!;
        var vett = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        return Path.Combine(vett, "suites", "team-games-tier6.yaml");
    }

    // Minimum passing-test counts come from each instance's own BUILD.md
    // "Acceptance — ALL must pass" section. They are transcribed, not invented.
    private static readonly Dictionary<string, int> ExpectedMinPassed = new()
    {
        ["g1-blocks-2p"] = 10,      // "dotnet test exits 0 with >=10 simulation tests passing"
        ["g2-panel-match-2p"] = 10, // same
        ["g3-cycle-arena-4p"] = 15,  // ">=15 simulation tests passing"
    };

    [Fact]
    public void Tier6_Gates_On_Build_And_Tests_Not_Just_File_Existence()
    {
        var suite = D.Deserialize<TeamBenchSuite>(File.ReadAllText(SuitePath()));

        Assert.Equal(3, suite.Instances.Count);

        foreach (var inst in suite.Instances)
        {
            var id = inst.Id;

            // --- the compiler gate ---
            var build = inst.Assertions.FirstOrDefault(a =>
                a.RunCommand is not null &&
                a.RunCommand.Command.Contains("dotnet build", StringComparison.OrdinalIgnoreCase));
            Assert.True(build is not null,
                $"{id}: no run_command asserting `dotnet build`. The tier-6 gate must not " +
                $"regress to existence-only — a run can PASS with code that does not compile.");
            Assert.Equal(0, build!.RunCommand!.ExitCode);

            // --- the test gate, including the empty-test-project trap ---
            var test = inst.Assertions.FirstOrDefault(a => a.DotnetTest is not null);
            Assert.True(test is not null,
                $"{id}: no dotnet_test assertion. Without min_passed, a Tests.csproj " +
                $"containing zero test files scores as a delivered test project " +
                $"(g2-panel-match-2p shipped exactly that on 2026-08-24).");
            Assert.Equal(ExpectedMinPassed[id], test!.DotnetTest!.MinPassed);
            Assert.Equal(0, test.DotnetTest.MaxFailed);

            // --- the client gate ---
            var client = inst.Assertions.FirstOrDefault(a =>
                a.RunCommand is not null &&
                a.RunCommand.Command.Contains("npm", StringComparison.OrdinalIgnoreCase));
            Assert.True(client is not null,
                $"{id}: no run_command building the web client. g3-cycle-arena-4p passed " +
                $"all three existence assertions on 2026-08-24 while shipping no client code.");
            Assert.Equal(0, client!.RunCommand!.ExitCode);

            // The existence assertions stay — they are cheap and they localise
            // a failure ("never got there") vs a build break ("got there, broke").
            Assert.Contains(inst.Assertions, a => a.File is not null);
        }
    }

    // Liveness check on the test above: prove the binding assertions can
    // actually FAIL. IgnoreUnmatchedProperties() means a typo'd key
    // deserializes to null rather than throwing, so this pins the healthy
    // null — if this ever goes green, the test above proves nothing.
    [Fact]
    public void Misspelled_Assertion_Key_Deserializes_To_Null_Not_An_Error()
    {
        const string yaml = """
            name: probe
            instances:
              - id: probe-1
                description: probe
                workspace: empty-git-repo
                prompt: probe
                assertions:
                  - run_commandd:
                      command: dotnet build X.slnx
                  - dotnet_testt:
                      min_passed: 10
            """;

        var suite = D.Deserialize<TeamBenchSuite>(yaml);
        var asserts = suite.Instances.Single().Assertions;

        // Both keys are misspelled by one character. Nothing throws, and
        // BOTH specs come back null — which is exactly how a gate silently
        // reverts to existence-only. This is the failure mode the test
        // above is built to catch.
        Assert.All(asserts, a => Assert.Null(a.RunCommand));
        Assert.All(asserts, a => Assert.Null(a.DotnetTest));
    }
}
