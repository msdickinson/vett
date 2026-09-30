using System.Text.Json;
using Vett.Config;
using Vett.Runner;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Tests;

/// <summary>
/// Tests for BenchSpec loading, manifest filtering, and per-instance image resolution.
/// </summary>
public class BenchSpecTests
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // --- BenchSpec YAML round-trip ---

    [Fact]
    public void BenchSpec_Deserializes_From_Yaml()
    {
        const string yaml = """
            name: swe-rebench-easy-py
            description: Python easy tier
            manifest: ~/.cache/vett/datasets/swe-rebench-v2.jsonl
            filter:
              language: python
              difficulty: easy
              type: bug
            agent_profile: openhands
            working_dir: /testbed
            """;

        var spec = D.Deserialize<BenchSpec>(yaml);

        Assert.Equal("swe-rebench-easy-py", spec.Name);
        Assert.Equal("~/.cache/vett/datasets/swe-rebench-v2.jsonl", spec.Manifest);
        Assert.Equal("python", spec.Filter.Language);
        Assert.Equal("easy", spec.Filter.Difficulty);
        Assert.Equal("bug", spec.Filter.Type);
        Assert.Equal("openhands", spec.AgentProfile);
        Assert.Equal("/testbed", spec.WorkingDir);
    }

    [Fact]
    public void BenchSpec_Partial_Filter_Leaves_Others_Null()
    {
        const string yaml = """
            name: swe-rebench-all-py
            manifest: /tmp/manifest.jsonl
            filter:
              language: python
            agent_profile: openhands
            """;

        var spec = D.Deserialize<BenchSpec>(yaml);
        Assert.Equal("python", spec.Filter.Language);
        Assert.Null(spec.Filter.Difficulty);
        Assert.Null(spec.Filter.Type);
    }

    // --- Instance.LoadJsonl reads new fields ---

    [Fact]
    public void LoadJsonl_Reads_Language_Difficulty_Type_Image()
    {
        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpPath, new[]
            {
                """{"instance_id":"aws__cfn-lint-1","repo":"aws/cfn-lint","language":"python","diff":"easy","type":"bug","image_name":"docker.io/swerebenchv2/aws-cfn-lint:1-abc123","base_commit":"abc","problem_statement":"Fix X"}""",
                """{"instance_id":"microsoft__kiota-2","repo":"microsoft/kiota","language":"csharp","diff":"medium","type":"feat","image_name":"docker.io/swerebenchv2/microsoft-kiota:2-def456","base_commit":"def","problem_statement":"Add Y"}""",
            });

            var instances = BenchmarkRunner.LoadJsonl(tmpPath);

            Assert.Equal(2, instances.Count);

            var py = instances[0];
            Assert.Equal("aws__cfn-lint-1", py.Id);
            Assert.Equal("python", py.Language);
            Assert.Equal("easy", py.Difficulty);
            Assert.Equal("bug", py.InstanceType);
            Assert.Equal("docker.io/swerebenchv2/aws-cfn-lint:1-abc123", py.Image);

            var cs = instances[1];
            Assert.Equal("csharp", cs.Language);
            Assert.Equal("medium", cs.Difficulty);
            Assert.Equal("feat", cs.InstanceType);
        }
        finally { File.Delete(tmpPath); }
    }

    [Fact]
    public void LoadJsonl_Missing_Optional_Fields_Yield_Null()
    {
        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpPath, """{"instance_id":"x","repo":"r","base_commit":"c","problem_statement":"p"}""");
            var instances = BenchmarkRunner.LoadJsonl(tmpPath);
            var inst = instances[0];
            Assert.Null(inst.Language);
            Assert.Null(inst.Difficulty);
            Assert.Null(inst.InstanceType);
            Assert.Null(inst.Image);
        }
        finally { File.Delete(tmpPath); }
    }

    // --- ResolveImage per-instance precedence ---

    [Fact]
    public void ResolveImage_UsesInstanceImage_WhenSet()
    {
        var profile = BuildProfile("docker");
        var suite = new Suite { Rendering = new RenderingConfig { DockerImageTemplate = "template/{repo}:{instance}" } };
        var inst = new Instance { Id = "foo-123", Repo = "org/repo", Image = "docker.io/swerebenchv2/org-repo:123-abcdef" };

        var image = BenchmarkRunner.ResolveImage(profile, suite, inst);

        Assert.Equal("docker.io/swerebenchv2/org-repo:123-abcdef", image);
    }

    [Fact]
    public void ResolveImage_FallsBackToTemplate_WhenInstanceImageNull()
    {
        var profile = BuildProfile("docker");
        var suite = new Suite { Rendering = new RenderingConfig { DockerImageTemplate = "swebench/sweb.eval.x86_64.{repo}_1776_{instance}" } };
        var inst = new Instance { Id = "django__django-12345", Repo = "django/django", Image = null };

        var image = BenchmarkRunner.ResolveImage(profile, suite, inst);

        Assert.Equal("swebench/sweb.eval.x86_64.django_django_1776_django__django-12345", image);
    }

    [Fact]
    public void ResolveImage_ReturnsEmpty_WhenLocalSandbox()
    {
        var profile = BuildProfile("local");
        var suite = new Suite { Rendering = new RenderingConfig { DockerImageTemplate = "some/image" } };
        var inst = new Instance { Id = "x", Image = "docker.io/foo:bar" };

        // Local sandbox always returns "" regardless of instance image.
        var image = BenchmarkRunner.ResolveImage(profile, suite, inst);
        Assert.Equal("", image);
    }

    // --- InstanceResult carries Difficulty + InstanceType in JSON ---

    [Fact]
    public void InstanceResult_Serializes_Difficulty_And_InstanceType()
    {
        var r = new InstanceResult
        {
            InstanceId = "aws__cfn-lint-1",
            Language = "python",
            Difficulty = "easy",
            InstanceType = "bug",
            EndReason = "finish_tool",
        };
        var json = JsonSerializer.Serialize(r);
        Assert.Contains("\"difficulty\":\"easy\"", json);
        Assert.Contains("\"instance_type\":\"bug\"", json);
    }

    // ---

    private static Profile BuildProfile(string sandboxType) => new()
    {
        Sandbox = new SandboxConfig { Type = sandboxType },
        Llm = new LlmConfig { Model = "test" },
    };
}
