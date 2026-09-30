using Vett.Cli;
using Vett.Config;
using Vett.Runner;

namespace Vett.Tests;

public class ProfileTests
{
    [Fact]
    public void ParseProfile_BasicFields()
    {
        var yaml = """
            name: test-profile
            llm:
              provider: local
              temperature: 0.5
              top_p: 0.9
            system_prompt: You are a test assistant.
            tools:
              - terminal
            middleware:
              - submit_detector
            max_iterations: 50
            """;

        var profile = Yaml.ParseProfile(yaml);

        Assert.Equal("test-profile", profile.Name);
        Assert.Equal(0.5, profile.Llm.Temperature);
        Assert.Equal(0.9, profile.Llm.TopP);
        Assert.Equal(50, profile.MaxIterations);
        Assert.Single(profile.Tools);
        Assert.Equal("terminal", profile.Tools[0]);
        Assert.Single(profile.Middleware);
    }

    [Fact]
    public void ParseProfile_DefaultValues()
    {
        var yaml = """
            name: minimal
            llm:
              temperature: 1.0
              top_p: 0.95
            system_prompt: test
            max_iterations: 10
            """;

        var profile = Yaml.ParseProfile(yaml);

        Assert.Equal("minimal", profile.Name);
        Assert.Equal(1.0, profile.Llm.Temperature);
        Assert.Empty(profile.Tools);
    }

    [Fact]
    public void ParseProfile_ProviderConfig()
    {
        var yaml = """
            name: cloud
            llm:
              provider: openai
              model: gpt-4o
              api_key_env: OPENAI_KEY
              temperature: 0.3
              top_p: 0.95
            system_prompt: test
            max_iterations: 10
            """;

        var profile = Yaml.ParseProfile(yaml);

        Assert.Equal("openai", profile.Llm.Provider);
        Assert.Equal("gpt-4o", profile.Llm.Model);
        Assert.Equal("OPENAI_KEY", profile.Llm.ApiKeyEnv);
    }

    [Fact]
    public void ParseProfile_TeamConfig()
    {
        var yaml = """
            name: team-test
            llm:
              temperature: 0.3
              top_p: 0.95
            system_prompt: test
            max_iterations: 50
            team:
              leader:
                name: lead
                system_prompt: You are a leader.
                max_iterations: 30
              members:
                - name: impl
                  system_prompt: You implement.
                  tools:
                    - terminal
                    - file_editor
                  max_iterations: 100
                - name: review
                  system_prompt: You review.
                  tools:
                    - terminal
            """;

        var profile = Yaml.ParseProfile(yaml);

        Assert.NotNull(profile.Team);
        Assert.Equal("lead", profile.Team!.Leader.Name);
        Assert.Equal(30, profile.Team.Leader.MaxIterations);
        Assert.Equal(2, profile.Team.Members.Count);
        Assert.Equal("impl", profile.Team.Members[0].Name);
        Assert.Equal(2, profile.Team.Members[0].Tools.Count);
        Assert.Equal("review", profile.Team.Members[1].Name);
        // leader_nudge defaults ON — omitting it must not change bench behavior
        Assert.True(profile.Team.LeaderNudge);
    }

    [Fact]
    public void ParseProfile_LeaderNudge_OptOut()
    {
        var yaml = """
            name: chat-test
            llm:
              temperature: 0.3
            system_prompt: test
            team:
              leader_nudge: false
              leader:
                name: lead
                system_prompt: You are a leader.
              members:
                - name: impl
                  system_prompt: You implement.
            """;

        var profile = Yaml.ParseProfile(yaml);

        Assert.NotNull(profile.Team);
        Assert.False(profile.Team!.LeaderNudge);
    }
}

public class RunnerResolverTests
{
    [Fact]
    public void ResolveWorkingDir_PrefersSuiteOverProfile()
    {
        var profile = new Profile { Sandbox = new SandboxConfig { DefaultCwd = "/profile-cwd" } };
        var suite = new Suite { Rendering = new RenderingConfig { WorkingDir = "/suite-cwd" } };

        Assert.Equal("/suite-cwd", BenchmarkRunner.ResolveWorkingDir(profile, suite));
    }

    [Fact]
    public void ResolveWorkingDir_FallsBackToProfile()
    {
        var profile = new Profile { Sandbox = new SandboxConfig { DefaultCwd = "/profile-cwd" } };
        var suite = new Suite();

        Assert.Equal("/profile-cwd", BenchmarkRunner.ResolveWorkingDir(profile, suite));
    }

    [Fact]
    public void ResolveWorkingDir_DefaultsToTestbed()
    {
        Assert.Equal("/testbed", BenchmarkRunner.ResolveWorkingDir(new Profile(), new Suite()));
    }

    [Fact]
    public void ResolveImage_LocalProfileReturnsEmpty()
    {
        var profile = new Profile { Sandbox = new SandboxConfig { Type = "local" } };
        var suite = new Suite { Rendering = new RenderingConfig { DockerImageTemplate = "swebench/{instance}" } };
        var inst = new Instance { Id = "django-1", Repo = "django/django" };

        Assert.Equal("", BenchmarkRunner.ResolveImage(profile, suite, inst));
    }

    [Fact]
    public void ResolveImage_SubstitutesTemplatePlaceholders()
    {
        var profile = new Profile { Sandbox = new SandboxConfig { Type = "docker" } };
        var suite = new Suite
        {
            Rendering = new RenderingConfig
            {
                DockerImageTemplate = "swebench/sweb.eval.x86_64.{repo}_1776_{instance}",
            },
        };
        var inst = new Instance { Id = "django__django-12345", Repo = "django/django" };

        var image = BenchmarkRunner.ResolveImage(profile, suite, inst);

        Assert.Equal("swebench/sweb.eval.x86_64.django_django_1776_django__django-12345", image);
    }

    [Fact]
    public void ResolveImage_FallsBackToSuiteSandboxImage()
    {
        var profile = new Profile { Sandbox = new SandboxConfig { Type = "docker" } };
        var suite = new Suite { Sandbox = new SuiteSandboxConfig { DockerImage = "swebench/default" } };
        var inst = new Instance { Id = "x", Repo = "y" };

        Assert.Equal("swebench/default", BenchmarkRunner.ResolveImage(profile, suite, inst));
    }
}

public class HelpersExpandHomeTests
{
    [Fact]
    public void ExpandHome_LeadingTildeSlash_ExpandsToHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = Helpers.ExpandHome("~/.cache/vett/datasets/foo.jsonl");
        Assert.Equal(Path.Combine(home, ".cache/vett/datasets/foo.jsonl"), result);
    }

    [Fact]
    public void ExpandHome_OnlyTilde_ReturnsHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, Helpers.ExpandHome("~"));
    }

    [Fact]
    public void ExpandHome_NoTilde_PassThrough()
    {
        Assert.Equal("/absolute/path", Helpers.ExpandHome("/absolute/path"));
        Assert.Equal("relative/path", Helpers.ExpandHome("relative/path"));
        Assert.Equal("", Helpers.ExpandHome(""));
    }

    [Fact]
    public void ExpandHome_TildeInsideName_NotTouched()
    {
        Assert.Equal("foo~bar", Helpers.ExpandHome("foo~bar"));
    }
}

public class SpellCheckTests
{
    [Fact]
    public void DetectsMisspelling()
    {
        var warnings = SpellCheck.Check(["naem", "tools"]);

        Assert.Single(warnings);
        Assert.Contains("name", warnings[0]);
    }

    [Fact]
    public void NoWarningForValidKeys()
    {
        var warnings = SpellCheck.Check(["name", "tools", "middleware", "llm"]);

        Assert.Empty(warnings);
    }
}
