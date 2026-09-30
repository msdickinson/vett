using System.Reflection;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>
/// Profile/config PLUMBING: does a key written in YAML actually reach the
/// code that consumes it, and can a run artifact prove WHICH file it came
/// from?
///
/// The failure mode these guard is not a crash — it is silence. A key that
/// no property will ever receive, or a per-member override that the merge
/// throws away, leaves every test green and every run "successful" while
/// two benchmark arms that were meant to differ ran identically.
/// </summary>
public class ProfileKeyAuditTests
{
    [Fact]
    public void CleanProfile_ReportsNothing()
    {
        var yaml = """
            name: clean
            llm:
              provider: local
              endpoint: http://localhost:8000/v1
              model: m
              temperature: 0.2
            max_iterations: 40
            tools:
              - terminal
            """;

        Assert.Empty(ProfileKeyAudit.AuditProfile(yaml));
    }

    [Fact]
    public void TopLevelTypo_IsReported_WithSuggestion()
    {
        // Singular: the exact class of typo IgnoreUnmatchedProperties eats.
        var yaml = """
            name: typo
            max_iteration: 40
            """;

        var found = ProfileKeyAudit.AuditProfile(yaml);

        var u = Assert.Single(found);
        Assert.Equal("max_iteration", u.Path);
        Assert.Equal("max_iteration", u.Key);
        Assert.Equal("max_iterations", u.Suggestion);
    }

    [Fact]
    public void NestedMemberLlmTypo_IsReported_WithIndexedPath()
    {
        // The dangerous one: a per-member knob. Top-level keys get eyeballed;
        // team.members[1].llm.* does not.
        var yaml = """
            name: team-typo
            team:
              leader:
                name: leader
              members:
                - name: implementer
                  llm:
                    temperature: 0.2
                - name: reviewer
                  llm:
                    temprature: 0.9
            """;

        var found = ProfileKeyAudit.AuditProfile(yaml);

        var u = Assert.Single(found);
        Assert.Equal("team.members[1].llm.temprature", u.Path);
        Assert.Equal("temperature", u.Suggestion);
    }

    [Fact]
    public void LeaderTypo_IsReported()
    {
        var yaml = """
            name: leader-typo
            team:
              leader:
                name: leader
                max_iterations: 200
                middlewares:
                  - critic
            """;

        var found = ProfileKeyAudit.AuditProfile(yaml);

        var u = Assert.Single(found);
        Assert.Equal("team.leader.middlewares", u.Path);
        Assert.Equal("middleware", u.Suggestion);
    }

    [Fact]
    public void DictionaryKeys_AreUserNames_NotSchema()
    {
        // mcp_servers' keys are server names the author invents. Flagging them
        // would make the instrument cry wolf on every correct profile — and an
        // instrument that cries wolf gets switched off.
        var yaml = """
            name: mcp
            mcp_servers:
              some-server-i-named:
                command: node
                bogus_key: 1
            """;

        var found = ProfileKeyAudit.AuditProfile(yaml);

        var u = Assert.Single(found);
        Assert.Equal("mcp_servers.some-server-i-named.bogus_key", u.Path);
    }

    [Fact]
    public void UnrelatedKey_IsReported_WithNoSuggestion()
    {
        // Nothing within edit distance 2 => no guess, rather than a wrong guess.
        var found = ProfileKeyAudit.AuditProfile("name: x\nzzzzzzzzzzzz: 1\n");

        var u = Assert.Single(found);
        Assert.Equal("zzzzzzzzzzzz", u.Path);
        Assert.Null(u.Suggestion);
        Assert.Equal("zzzzzzzzzzzz", u.ToString());
    }

    [Fact]
    public void YamlIgnoredProperty_IsNotAcceptedAsAKey()
    {
        // source_path is a runtime-only field. Writing it in YAML does nothing,
        // so it must be reported rather than silently accepted.
        var found = ProfileKeyAudit.AuditProfile("name: x\nsource_path: /tmp/foo.yaml\n");

        Assert.Equal("source_path", Assert.Single(found).Path);
    }

    [Fact]
    public void EveryShippedProfile_HasZeroUnknownKeys()
    {
        var dir = TestRepo.ProfilesDir();
        Assert.True(dir is not null, "Could not locate the repo profiles/ directory from " + AppContext.BaseDirectory);

        var files = Directory.GetFiles(dir!, "*.yaml");
        // Name the denominator: a green from an empty set proves nothing.
        // Corpus pruned 49 → 12 on 2026-08-26 (the picker was offering an entire
        // retired generation), so this floor moved 20 → 8 with it.
        Assert.True(files.Length >= 8, $"Expected the shipped profile corpus, found {files.Length} file(s) in {dir}");

        var audits = files.Select(f => (File: f, Audit: ProfileKeyAudit.AuditProfileFile(f))).ToList();

        // ⛔ The gate must span the population it claims. Before F7 an
        // unreadable profile audited as `[]`, so if every file in the corpus
        // had been locked this test would have reported "zero offenders across
        // 24 profiles" — a green built entirely out of failures to measure.
        // Assert coverage FIRST, then cleanliness.
        var unmeasured = audits.Where(a => !a.Audit.Measured)
            .Select(a => $"{Path.GetFileName(a.File)}: {a.Audit.UnmeasuredReason}")
            .ToList();
        Assert.True(unmeasured.Count == 0,
            $"{unmeasured.Count} of {files.Length} profile(s) COULD NOT BE AUDITED — this is not a "
            + $"clean result, it is an absent one:\n  " + string.Join("\n  ", unmeasured));

        var offenders = audits
            .SelectMany(a => a.Audit.Keys!.Select(u => $"{Path.GetFileName(a.File)}: {u}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} silently-ignored key(s) across {files.Length} profile(s):\n  " +
            string.Join("\n  ", offenders));
    }
}

/// <summary>
/// Which FILE did this run load? The same profile name resolves to up to
/// three different files (cwd, ~/.vett, install dir) whose contents are not
/// required to agree, so a run artifact that records only the NAME cannot
/// name the ruler it was scored with.
/// </summary>
public class ProfileSourcePathTests
{
    [Fact]
    public void LoadProfile_RecordsTheFileItCameFrom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vett-srcpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "p.yaml");
            File.WriteAllText(file, "name: p\nllm:\n  model: m\n");

            var profile = Yaml.LoadProfile(file);

            Assert.Equal(Path.GetFullPath(file), profile.SourcePath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseProfile_LeavesSourcePathNull()
    {
        // Text has no file. Null must stay null rather than becoming a
        // plausible-looking lie in a run artifact.
        Assert.Null(Yaml.ParseProfile("name: p\n").SourcePath);
    }

    [Fact]
    public void SourcePath_IsNotAYamlKey()
    {
        // [YamlIgnore]: a profile cannot forge its own provenance.
        var p = Yaml.ParseProfile("name: p\nsource_path: /somewhere/else.yaml\n");
        Assert.Null(p.SourcePath);
    }

    [Fact]
    public void ResolveSearchDirs_PrecedenceIsCwdThenUserThenInstall()
    {
        var dirs = Yaml.ResolveSearchDirs("profiles").ToList();

        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "profiles"), dirs[0]);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "profiles"), dirs[^1]);
        // The middle entry is present only when HOME/USERPROFILE is set; on a
        // developer box it is, so assert the shape rather than a count.
        Assert.InRange(dirs.Count, 2, 3);
        if (dirs.Count == 3)
            Assert.EndsWith(Path.Combine(".vett", "profiles"), dirs[1]);
    }

    [Fact]
    public void ResolvePath_ExplicitExistingFile_WinsOverSearchDirs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vett-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "explicit.yaml");
            File.WriteAllText(file, "name: explicit\n");

            Assert.Equal(file, Yaml.ResolvePath(file, "profiles"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolvePath_ReturnsNull_WhenNothingMatches()
    {
        Assert.Null(Yaml.ResolvePath("no-such-profile-" + Guid.NewGuid().ToString("N"), "profiles"));
    }

    [Fact]
    public void ResolveWithPath_ValueAndPath_ComeFromTheSameFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vett-resolvewp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "rw.yaml");
            File.WriteAllText(file, "name: rw\nllm:\n  model: m\n");

            var (value, path) = Yaml.ResolveWithPath(file, "profiles", Yaml.LoadProfile);

            Assert.NotNull(value);
            Assert.Equal(file, path);
            Assert.Equal("rw", value!.Name);
            Assert.Equal(Path.GetFullPath(file), value.SourcePath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveWithPath_Miss_ReturnsNullPair()
    {
        var (value, path) = Yaml.ResolveWithPath(
            "no-such-profile-" + Guid.NewGuid().ToString("N"), "profiles", Yaml.LoadProfile);

        Assert.Null(value);
        Assert.Null(path);
    }

    [Fact]
    public void RunSummary_CarriesProfilePath_UnderItsWireName()
    {
        // Recording it in memory is not recording it in the ARTIFACT. The
        // wire name is what a downstream reader greps for, so pin it.
        var json = System.Text.Json.JsonSerializer.Serialize(
            new Vett.Runner.RunSummary { Profile = "tier6", ProfilePath = @"C:\repo\profiles\tier6.yaml" });

        Assert.Contains("\"profile_path\"", json);
        Assert.Contains("profiles", json);
    }

    [Fact]
    public void TeamBenchJsonSummary_CarriesProfilePathAndUnknownKeys_UnderTheirWireNames()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new Vett.Bench.Team.TeamBenchJsonSummary
            {
                ProfileName = "tier6",
                ProfilePath = @"C:\repo\profiles\tier6.yaml",
                ProfileUnknownKeys = ["team.leader.middlewares"],
            });

        Assert.Contains("\"profile_path\"", json);
        Assert.Contains("\"profile_unknown_keys\"", json);
        Assert.Contains("team.leader.middlewares", json);
        // ADDING fields is safe for downstream consumers; renaming is not.
        Assert.Contains("\"profile_name\"", json);
    }
}

/// <summary>
/// Per-member LLM overrides. A member override is only real if the merge
/// can tell "the author wrote this" from "the type default happens to equal
/// it" — the same distinction the nullable temperature/top_p fields already
/// encode, which <c>provider</c> (a non-nullable string whose default is a
/// VALID value) did not.
/// </summary>
public class LlmProviderOverrideTests
{
    [Fact]
    public void UnsetProvider_ReadsLocal_ButIsNotExplicit()
    {
        var c = new LlmConfig();

        Assert.Equal("local", c.Provider);
        Assert.False(c.HasExplicitProvider);
    }

    [Fact]
    public void ProviderWrittenInYaml_IsExplicit_EvenWhenItEqualsTheDefault()
    {
        var explicitLocal = Yaml.ParseProfile("llm:\n  provider: local\n").Llm;
        var unset = Yaml.ParseProfile("llm:\n  model: m\n").Llm;

        Assert.Equal("local", explicitLocal.Provider);
        Assert.True(explicitLocal.HasExplicitProvider);

        Assert.Equal("local", unset.Provider);
        Assert.False(unset.HasExplicitProvider);
    }

    [Fact]
    public void EmptyProviderValue_CountsAsUnset()
    {
        // `provider:` with no value must keep inheriting, exactly as the old
        // !string.IsNullOrEmpty guard did.
        var c = new LlmConfig { Provider = "" };

        Assert.Equal("local", c.Provider);
        Assert.False(c.HasExplicitProvider);
    }

    [Fact]
    public void Merge_ExplicitLocalOnMember_SurvivesACloudBase()
    {
        // THE DEFECT: the old merge used `memberOverride.Provider != "local"`
        // as "did the author set it?". "local" is a legitimate value, so a
        // member pinned to a local vLLM under a cloud base profile silently
        // kept the cloud provider — and because the OpenAI path ignores
        // `endpoint`, its local endpoint was dropped too.
        var baseConfig = new LlmConfig
        {
            Provider = "openai",
            Model = "gpt-4o",
            ApiKeyEnv = "OPENAI_API_KEY",
        };
        var memberOverride = new LlmConfig
        {
            Provider = "local",
            Endpoint = "http://gpu-1:8000/v1",
            Model = "local-model",
        };

        var merged = ChatClientFactory.Merge(baseConfig, memberOverride);

        Assert.Equal("local", merged.Provider);
        Assert.Equal("http://gpu-1:8000/v1", merged.Endpoint);
        Assert.Equal("local-model", merged.Model);
    }

    [Fact]
    public void Merge_UnsetProviderOnMember_StillInheritsTheBase()
    {
        // The behaviour the sentinel was protecting must not regress.
        var baseConfig = new LlmConfig { Provider = "anthropic", Model = "claude", ApiKeyEnv = "K" };
        var memberOverride = new LlmConfig { Model = "claude-small" };

        var merged = ChatClientFactory.Merge(baseConfig, memberOverride);

        Assert.Equal("anthropic", merged.Provider);
        Assert.Equal("claude-small", merged.Model);
    }

    [Fact]
    public void Merge_NonLocalProviderOnMember_StillOverrides()
    {
        var baseConfig = new LlmConfig { Provider = "local", Endpoint = "http://x:8000/v1", Model = "m" };
        var memberOverride = new LlmConfig { Provider = "openai", Model = "gpt-4o" };

        Assert.Equal("openai", ChatClientFactory.Merge(baseConfig, memberOverride).Provider);
    }

    [Fact]
    public void ResolveClient_MemberOverridingOnlyTemperature_ReusesTheBaseClient()
    {
        // ResolveClient's own doc comment says temperature and top_p are
        // per-call options, not client config. Its provider test was
        // `!IsNullOrEmpty(member.Provider) && member.Provider != base.Provider`
        // — but Provider never reads empty, so under any non-local base that
        // collapsed to "always true" and every member got a redundant client.
        var baseConfig = new LlmConfig
        {
            Provider = "anthropic",
            Endpoint = "http://127.0.0.1:9/v1",
            Model = "base-model",
        };
        var baseClient = ChatClientFactory.Create(baseConfig);
        var memberConfig = new LlmConfig { Temperature = 0.2 };

        var (client, model) = InvokeResolveClient(baseConfig, baseClient, "base-model", memberConfig);

        Assert.Same(baseClient, client);
        Assert.Equal("base-model", model);
    }

    [Fact]
    public void ResolveClient_MemberOverridingModel_StillGetsItsOwnClient()
    {
        // Guard the other direction: the loosened predicate must not stop a
        // real override from getting its own client.
        var baseConfig = new LlmConfig
        {
            Provider = "local",
            Endpoint = "http://127.0.0.1:9/v1",
            Model = "base-model",
        };
        var baseClient = ChatClientFactory.Create(baseConfig);
        var memberConfig = new LlmConfig { Model = "member-model" };

        var (client, model) = InvokeResolveClient(baseConfig, baseClient, "base-model", memberConfig);

        Assert.NotSame(baseClient, client);
        Assert.Equal("member-model", model);
    }

    /// <summary>TeamCoordinator.ResolveClient is private; reflect rather than
    /// widen production visibility just for a test.</summary>
    private static (IChatClient Client, string Model) InvokeResolveClient(
        LlmConfig baseConfig, IChatClient baseClient, string baseModel, LlmConfig? memberConfig)
    {
        var m = typeof(TeamCoordinator).GetMethod(
            "ResolveClient", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(m is not null, "TeamCoordinator.ResolveClient not found — did it move or change visibility?");

        return (ValueTuple<IChatClient, string>)m!.Invoke(
            null, [baseConfig, baseClient, baseModel, memberConfig])!;
    }
}

internal static class TestRepo
{
    /// <summary>The repo root (the directory holding src/Vett/Vett.csproj), or
    /// null if the test binary is running somewhere the repo layout is not
    /// above it.</summary>
    public static string? Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "profiles"))
                && File.Exists(Path.Combine(dir.FullName, "src", "Vett", "Vett.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>The repo's shipped profiles/ directory, or null if the test
    /// binary is running somewhere the repo layout is not above it.</summary>
    public static string? ProfilesDir()
    {
        var root = Root();
        return root is null ? null : Path.Combine(root, "profiles");
    }

    /// <summary>
    /// The <c>defaults/profiles/</c> directory — a SECOND, separately-maintained
    /// profile corpus that was entirely unaudited until 2026-08-26.
    ///
    /// ⛔ THIS IS NOT A BACKUP. <c>vett install defaults</c> copies
    /// <c>&lt;install&gt;/defaults/**</c> into <c>&lt;cwd&gt;/</c> preserving relative paths
    /// (SimpleCommands.cs:45-57), so <c>defaults/profiles/coding.yaml</c> lands at
    /// <c>&lt;cwd&gt;/profiles/coding.yaml</c> — which is RUNG 1 of the three-store
    /// precedence chain (cwd ▸ ~/.vett ▸ install-dir, Profile.cs:615-617) and
    /// therefore outranks every other copy. Any drift between the two trees is a
    /// silent downgrade applied to exactly the workspaces a user installs into.
    /// </summary>
    public static string? DefaultsProfilesDir()
    {
        var root = Root();
        if (root is null) return null;
        var p = Path.Combine(root, "defaults", "profiles");
        return Directory.Exists(p) ? p : null;
    }
}
