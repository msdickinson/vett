using System.CommandLine;
using Microsoft.Extensions.Logging;
using Vett.Cli;
using Vett.Llm;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// THE THIRD PRECEDENCE RUNG. Endpoint/model/api-key resolve CLI &gt; env &gt;
/// profile. `vett chat` (ChatCommand:119-122) and `vett edit`
/// (EditCommand:64-67) implement all three rungs. Until 2026-08-26 `vett run`
/// implemented ONE — its guard read only the CLI and VETT_LLM_*, and it ran
/// BEFORE the profile was even loaded — and `vett bench` implemented the rung
/// for model and api-key but not for endpoint.
///
/// MEASURED, 2026-08-26, before the fix:
///
///   vett validate --profile ds-solo-pro --check-endpoints   → ✓ (endpoints live)
///   vett run      --profile ds-solo-pro --suite ...         → rc=2
///                 "Error: --endpoint (one or more) + --model required"
///
/// A profile that `validate` certifies as both COMPLETE and LIVE could not be
/// run. Every profile in the store declares its own llm.endpoint/llm.model, so
/// this was every profile.
///
/// ⛔ THE REFUSAL WAS NOT THE WHOLE COST. The workaround is to restate the
/// endpoint on the command line, and CLI values REBIND THE PRIMARY SEAT
/// (ChatClientFactory.Create: "CLI overrides apply only to the primary"). A
/// stale or mistyped paste therefore runs a DIFFERENT model than the profile
/// names, while the run artifacts still record the profile — arms pinned to
/// whatever was typed rather than to what is under test. Forcing the operator
/// to hand-copy a binding that the file already states is how that happens.
/// Members were never affected: Coordinator:1588 builds them from their own
/// merged blocks, passing no overrides.
///
/// ⛔ BENCH LOOKED IMPLEMENTED FROM THREE DIRECTIONS AND WAS NOT. The section
/// header said "Resolve LLM settings (CLI &gt; env &gt; profile)", the refusal
/// said "or set them in the agent profile", model and api-key both fell back —
/// and the endpoint fallback that appeared to finish the job
/// (`clients.Count == 0` → agentProfile.Llm.Endpoint) was UNREACHABLE, because
/// clients is built from endpoints and is empty exactly when the guard has
/// already returned. Correct-looking prose over a missing rung.
///
/// Invoked through the same root.Parse(args).Invoke() path Program.cs uses.
///
/// The positive case cannot assert rc=0 — that would mean really running a
/// benchmark against a real endpoint. It asserts instead that the run got PAST
/// the endpoint guard, using a marker only reachable downstream of it: a
/// profile naming an api_key_env that is not set is rejected by
/// ValidateProfileForRun, which runs immediately AFTER the guard. So the
/// negative test asserts the refusal fires, and the positive test asserts a
/// LATER refusal fires instead — the pair distinguishes "passed the guard"
/// from "blew up even earlier", which a bare DoesNotContain could not.
/// </summary>
[Collection("declare-done-env")]
public class ProfileEndpointRungTests
{
    private const string EndpointRefusal = "no LLM endpoint/model";

    private static (int Code, List<string> Errors) Invoke(Func<ILogger, Command> factory, params string[] args)
    {
        var log = new CapturingLogger();
        var root = new RootCommand("test");
        root.Add(factory(log));
        var code = root.Parse(args).Invoke();
        return (code, log.Errors);
    }

    /// <summary>
    /// Writes a throwaway profile and returns its path. <c>ResolvePath</c>
    /// takes a verbatim file path ahead of the three search dirs, so these
    /// never touch the shared ~/.vett store.
    /// </summary>
    private static string WriteProfile(string body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vett-rung-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "p.yaml");
        File.WriteAllText(path, body);
        return path;
    }

    // Declares its own endpoint and model — nothing on the CLI, nothing in the
    // environment. api_key_env names a variable that is deliberately NOT set,
    // which is the downstream marker described above.
    private const string SelfContained = """
        name: rung-self-contained
        sandbox:
          type: local
          default_cwd: .
        llm:
          provider: local
          endpoint: http://127.0.0.1:1/v1
          model: rung-test-model
          api_key_env: VETT_RUNG_TEST_KEY_NOT_SET
        system_prompt: |
          test
        """;

    // Declares neither. Nothing anywhere supplies them, so the guard must fire.
    private const string NoBinding = """
        name: rung-no-binding
        sandbox:
          type: local
          default_cwd: .
        llm:
          provider: local
        system_prompt: |
          test
        """;

    /// <summary>
    /// ⭐ THE POSITIVE CONJUNCT. Without it, a command that returned 2 for every
    /// input would satisfy every negative assertion here.
    /// </summary>
    [Theory]
    [InlineData("run")]
    [InlineData("bench")]
    public void The_rig_can_observe_a_zero(string command)
    {
        var factory = command == "run" ? RunCommand.Create : (Func<ILogger, Command>)BenchCommand.Create;
        var (code, _) = Invoke(factory, command, "--help");
        Assert.Equal(0, code);
    }

    [Fact]
    public void Run_accepts_a_profile_that_declares_its_own_endpoint_and_model()
    {
        using var _ = new ScopedEnv(
            ("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null),
            ("VETT_RUNG_TEST_KEY_NOT_SET", null));

        var (code, errors) = Invoke(RunCommand.Create,
            "run", "--suite", "team-minor-edit-tier1", "--profile", WriteProfile(SelfContained));

        // The guard did not fire...
        Assert.DoesNotContain(errors, e => e.Contains(EndpointRefusal));
        // ...and execution reached the check that sits immediately after it,
        // which is what makes the line above a pass rather than a vacuous one.
        Assert.Contains(errors, e => e.Contains("VETT_RUNG_TEST_KEY_NOT_SET"));
        Assert.Equal(2, code);
    }

    [Fact]
    public void Run_still_refuses_when_no_rung_supplies_an_endpoint_or_model()
    {
        // Cleared for the duration: a developer machine that exports these
        // would otherwise make the case unreachable and the test vacuous.
        using var _ = new ScopedEnv(("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null));

        var (code, errors) = Invoke(RunCommand.Create,
            "run", "--suite", "team-minor-edit-tier1", "--profile", WriteProfile(NoBinding));

        Assert.Equal(2, code);
        Assert.Contains(errors, e => e.Contains(EndpointRefusal));
    }

    /// <summary>
    /// The refusal has to name the rung that is missing and the file that was
    /// consulted. The old text — "--endpoint (one or more) + --model required" —
    /// sent the reader to the command line for something the profile is
    /// supposed to supply, which is exactly the wrong place to send them.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_profile_file_it_consulted()
    {
        using var _ = new ScopedEnv(("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null));
        var path = WriteProfile(NoBinding);

        var (_, errors) = Invoke(RunCommand.Create,
            "run", "--suite", "team-minor-edit-tier1", "--profile", path);

        Assert.Contains(errors, e => e.Contains(path));
        Assert.Contains(errors, e => e.Contains("llm.endpoint"));
    }

    /// <summary>
    /// The profile is the LAST rung, not the only new one: the environment
    /// still satisfies the guard for a profile that declares nothing. Without
    /// this, a "fix" that read the profile and ignored VETT_LLM_* would pass
    /// every other test in this file.
    /// </summary>
    [Fact]
    public void Env_still_satisfies_the_guard_for_a_profile_that_declares_nothing()
    {
        using var _ = new ScopedEnv(
            ("VETT_LLM_ENDPOINT", "http://127.0.0.1:1/v1"),
            ("VETT_LLM_MODEL", "rung-test-model"));

        var (_, errors) = Invoke(RunCommand.Create,
            "run", "--suite", "team-minor-edit-tier1", "--profile", WriteProfile(NoBinding));

        Assert.DoesNotContain(errors, e => e.Contains(EndpointRefusal));
    }

    /// <summary>
    /// ⛔ THE CRASH THE RUNG FIX UNCOVERED. `vett run --suite
    /// team-minor-edit-tier1` used to stop at the endpoint guard. Once it got
    /// past that it reached LoadJsonl with the suites/ DIRECTORY as its path —
    /// a team suite declares no loader.path, and Path.Combine(dir, "") returns
    /// dir — and died with an unhandled UnauthorizedAccessException and a stack
    /// trace at rc=1. "Access to the path ...\suites is denied" reads as a
    /// broken checkout, not as "that suite runs under team-bench".
    ///
    /// Both suite kinds sit in the same folder behind the same --suite name, so
    /// this is the expected mistake. It is now rc=2 naming the other command.
    /// </summary>
    [Fact]
    public void Run_refuses_a_team_suite_by_name_instead_of_crashing_on_the_suites_directory()
    {
        using var _ = new ScopedEnv(
            ("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null),
            ("VETT_RUNG_TEST_KEY_NOT_SET", "set-so-validate-passes"));

        var (code, errors) = Invoke(RunCommand.Create,
            "run", "--suite", "team-minor-edit-tier1", "--profile", WriteProfile(SelfContained));

        Assert.Equal(2, code);
        Assert.Contains(errors, e => e.Contains("team-bench"));
        Assert.Contains(errors, e => e.Contains("loader.path"));
        // The old failure mode, stated as itself.
        Assert.DoesNotContain(errors, e => e.Contains("UnauthorizedAccessException"));
    }

    private const string BenchRefusal = "LLM endpoint and model are required";

    /// <summary>
    /// `vett bench run` takes a BENCH-SPEC (resolved from bench-profiles/),
    /// which in turn names the agent profile whose llm block is the rung under
    /// test. Both resolve a verbatim file path ahead of the search dirs.
    ///
    /// ⚠ The first draft of these two passed a nonexistent spec name, and BOTH
    /// were meaningless: spec resolution is step 1 and the endpoint guard is
    /// step 3, so the command returned before the guard ever ran. The negative
    /// case failing is what exposed it — its positive twin had been green
    /// without ever reaching the code it claimed to test. The manifest is
    /// resolved at step 4, AFTER the guard, so a bogus manifest path is safe
    /// here and keeps the test off the disk and off the network.
    /// </summary>
    private static string WriteBenchSpec(string agentProfilePath)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vett-rung-spec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "spec.yaml");
        File.WriteAllText(path,
            $"name: rung-bench\nagent_profile: {agentProfilePath.Replace("\\", "/")}\n"
            + "manifest: /vett-rung-no-such-manifest.jsonl\n");
        return path;
    }

    [Fact]
    public void Bench_accepts_an_agent_profile_that_declares_its_own_endpoint()
    {
        using var _ = new ScopedEnv(
            ("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null),
            ("VETT_RUNG_TEST_KEY_NOT_SET", null));

        var (_, errors) = Invoke(BenchCommand.Create,
            "bench", "run", "--profile", WriteBenchSpec(WriteProfile(SelfContained)));

        // The guard did not fire — the endpoint is right there in the file...
        Assert.DoesNotContain(errors, e => e.Contains(BenchRefusal));
        // ...and the command ran on to the manifest step, which sits after it.
        // Without this conjunct the assertion above is satisfied by any earlier
        // exit, which is exactly how the first draft fooled itself.
        Assert.Contains(errors, e => e.Contains("no-such-manifest"));
    }

    [Fact]
    public void Bench_still_refuses_when_no_rung_supplies_an_endpoint()
    {
        using var _ = new ScopedEnv(("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null));

        var (_, errors) = Invoke(BenchCommand.Create,
            "bench", "run", "--profile", WriteBenchSpec(WriteProfile(NoBinding)));

        Assert.Contains(errors, e => e.Contains(BenchRefusal));
    }

    // ---------------------------------------------------------------------
    // THE FOURTH RUNG: the provider's own base URL.
    //
    // Below CLI > env > profile sits a source none of the guards consulted:
    // openai / anthropic / google each know where they live, and
    // ChatClientFactory has carried their base URLs all along. Because every
    // guard demanded a non-empty endpoint first, those defaults were
    // UNREACHABLE — the identical dead-fallback shape as bench's old
    // `clients.Count == 0` branch, one layer down.
    //
    // MEASURED 2026-08-26, before the fix (real CLI, not a unit harness):
    //
    //   vett chat --stdio --profile openai-example      → rc=2 "endpoint and
    //   vett chat --stdio --profile anthropic-example   → rc=2  model are required"
    //
    // Both are SHIPPED profiles. So is the failure of every cloud profile the
    // vett-chat onboarding flow writes: profileWriter.renderCloudProfileYaml
    // deliberately emits `api_key_env` and no endpoint, because the provider
    // supplies it. Paste a key, pass the live key test, get a profile that
    // cannot open a session.
    //
    // ⚠ `validate` passed that same YAML with 0 errors. A green validate is a
    // completeness check, NOT a startability proof — it never asked whether a
    // session could be opened. That is why the evidence above is a chat
    // invocation and not a validate run.
    // ---------------------------------------------------------------------

    private const string CloudNoEndpoint = """
        name: rung-cloud-no-endpoint
        sandbox:
          type: local
          default_cwd: .
        llm:
          provider: anthropic
          model: claude-haiku-4-5-20251001
          api_key_env: VETT_RUNG_TEST_KEY_NOT_SET
        system_prompt: |
          test
        """;

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1")]
    [InlineData("anthropic", "https://api.anthropic.com/v1")]
    // /v1beta/openai, not /v1beta — these URLs are consumed by
    // CreateOpenAICompatible, which appends /chat/completions. See the comment
    // on DefaultEndpointFor.
    [InlineData("google", "https://generativelanguage.googleapis.com/v1beta/openai")]
    [InlineData("ANTHROPIC", "https://api.anthropic.com/v1")] // case-insensitive
    public void A_cloud_provider_supplies_its_own_base_url(string provider, string expected)
        => Assert.Equal(expected, ChatClientFactory.DefaultEndpointFor(provider));

    // The other half. If this ever returns a URL, the guards stop protecting
    // the providers whose whole point is that you say where they live.
    [Theory]
    [InlineData("local")]
    [InlineData("azure")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-provider")]
    public void A_provider_with_no_default_still_requires_an_explicit_endpoint(string? provider)
        => Assert.Null(ChatClientFactory.DefaultEndpointFor(provider));

    [Fact]
    public void Run_accepts_a_cloud_profile_that_names_no_endpoint()
    {
        using var _ = new ScopedEnv(
            ("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null),
            ("VETT_RUNG_TEST_KEY_NOT_SET", null));

        var (code, errors) = Invoke(RunCommand.Create,
            "run", "--suite", "team-minor-edit-tier1", "--profile", WriteProfile(CloudNoEndpoint));

        // provider: anthropic carries its own base URL, so the guard must not
        // fire even though llm.endpoint is absent...
        Assert.DoesNotContain(errors, e => e.Contains(EndpointRefusal));
        // ...and the same downstream marker the other positive cases use proves
        // this is a pass rather than an earlier exit.
        Assert.Contains(errors, e => e.Contains("VETT_RUNG_TEST_KEY_NOT_SET"));
        Assert.Equal(2, code);
    }

    [Fact]
    public void Bench_accepts_a_cloud_profile_that_names_no_endpoint()
    {
        using var _ = new ScopedEnv(
            ("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null),
            ("VETT_RUNG_TEST_KEY_NOT_SET", null));

        var (_, errors) = Invoke(BenchCommand.Create,
            "bench", "run", "--profile", WriteBenchSpec(WriteProfile(CloudNoEndpoint)));

        Assert.DoesNotContain(errors, e => e.Contains(BenchRefusal));
        Assert.Contains(errors, e => e.Contains("no-such-manifest"));
    }

    // The guard must still fire for a provider that has no default, or the fix
    // above degenerates into "never require an endpoint". NoBinding declares
    // provider: local.
    [Fact]
    public void The_provider_rung_does_not_disarm_the_guard_for_local()
    {
        using var _ = new ScopedEnv(("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null));

        var (code, errors) = Invoke(RunCommand.Create,
            "run", "--suite", "team-minor-edit-tier1", "--profile", WriteProfile(NoBinding));

        Assert.Contains(errors, e => e.Contains(EndpointRefusal));
        Assert.Equal(2, code);
    }

    // Same shape as the copies nested in RunCommandExitCodeTests and
    // CliExitCodeSweepTests. Duplicated rather than hoisted so this file stays
    // self-contained, matching what those two already do.
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }

    private sealed class ScopedEnv : IDisposable
    {
        private readonly (string Key, string? Old)[] _saved;

        public ScopedEnv(params (string Key, string? Value)[] vars)
        {
            _saved = vars.Select(v => (v.Key, Environment.GetEnvironmentVariable(v.Key))).ToArray();
            foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
        }

        public void Dispose()
        {
            foreach (var (k, old) in _saved) Environment.SetEnvironmentVariable(k, old);
        }
    }
}
