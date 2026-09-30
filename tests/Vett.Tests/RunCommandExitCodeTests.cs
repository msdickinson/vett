using System.CommandLine;
using Microsoft.Extensions.Logging;
using Vett.Cli;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Failure test for `vett run`'s process exit code.
///
/// WHY THIS EXISTS: until 2026-08-25 the `run` action lambda returned plain
/// Task, and every fatal path in it was a bare `return;` after a LogError. The
/// command therefore PRINTED a fatal error and EXITED 0. Measured on the
/// shipped 0.1.0 tool: a nonexistent --profile exited 0, and a nonexistent
/// --suite exited 0. Anything gating on `$?` — a shell wrapper, an overnight
/// runner, a CI step — read a total config failure as a successful run, and
/// the measurement it believed it had was never taken. `rc=0` is not evidence
/// that something ran.
///
/// The codes are TeamBenchCommand's existing convention, not a second one:
/// 2 = config/usage error (nothing ran), 1 = ran but instances errored,
/// 0 = clean.
///
/// These invoke through the SAME path Program.cs uses — root.Parse(args)
/// .Invoke() — because the defect was never in the error handling. It was in
/// which SetAction overload the lambda bound to, and only the real invoke
/// path can observe that. A test that called a helper and inspected a returned
/// value would have passed against the broken code.
/// </summary>
public class RunCommandExitCodeTests
{
    private static (int Code, List<string> Errors) Invoke(params string[] args)
    {
        var log = new CapturingLogger();
        var root = new RootCommand("test");
        root.Add(RunCommand.Create(log));
        var code = root.Parse(args).Invoke();
        return (code, log.Errors);
    }

    // A live endpoint is never contacted: every case below is rejected during
    // config resolution, before any client is constructed. These two values
    // exist only to get PAST the endpoint/model guard so the later guards are
    // the thing under test.
    private const string SomeEndpoint = "http://127.0.0.1:1/v1";
    private const string SomeModel = "no-such-model";

    /// <summary>
    /// ⭐ THE POSITIVE CONJUNCT. Without it, a harness that returned 2 for
    /// literally every input would pass every other assertion in this file.
    /// `--help` exits 0 through the identical Parse().Invoke() path, which
    /// proves this rig can observe a zero at all.
    /// </summary>
    [Fact]
    public void The_rig_can_observe_a_zero()
    {
        var (code, _) = Invoke("run", "--help");
        Assert.Equal(0, code);
    }

    [Fact]
    public void A_nonexistent_profile_is_a_config_error_not_a_success()
    {
        var (code, errors) = Invoke(
            "run", "--suite", "team-minor-edit-tier1",
            "--profile", "vett-test-no-such-profile-2f9c1",
            "--endpoint", SomeEndpoint, "--model", SomeModel);

        Assert.Equal(2, code);
        Assert.NotEqual(0, code);   // the exact defect, stated as itself
        Assert.Contains(errors, e => e.Contains("vett-test-no-such-profile-2f9c1"));
    }

    [Fact]
    public void A_nonexistent_suite_is_a_config_error_not_a_success()
    {
        var (code, errors) = Invoke(
            "run", "--suite", "vett-test-no-such-suite-2f9c1",
            "--profile", "ds-team-flash",
            "--endpoint", SomeEndpoint, "--model", SomeModel);

        Assert.Equal(2, code);
        Assert.Contains(errors, e => e.Contains("vett-test-no-such-suite-2f9c1"));
    }

    /// <summary>
    /// ⚠ THIS TEST USED `--profile ds-team-flash` AND WAS RIGHT FOR THE WRONG
    /// REASON. ds-team-flash declares its own llm.endpoint and llm.model, so
    /// under a correct precedence chain (CLI &gt; env &gt; profile) there is
    /// nothing missing about it — the rc=2 this asserted was the endpoint guard
    /// firing on a profile that had the binding all along. That guard ran
    /// before the profile was even loaded; see ProfileEndpointRungTests.
    ///
    /// It now uses a profile that genuinely declares neither, so it tests what
    /// its name says. The rung itself is covered next door.
    /// </summary>
    [Fact]
    public void A_missing_endpoint_and_model_is_a_config_error_not_a_success()
    {
        // Cleared for the duration: the command falls back to these env vars,
        // so a developer machine that happens to export them would otherwise
        // make this case unreachable and the test would pass vacuously.
        using var _ = new ScopedEnv(("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null));

        var dir = Path.Combine(Path.GetTempPath(), $"vett-noep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var profile = Path.Combine(dir, "p.yaml");
        File.WriteAllText(profile, "name: no-binding\nsandbox:\n  type: local\n  default_cwd: .\nllm:\n  provider: local\nsystem_prompt: |\n  test\n");

        var (code, errors) = Invoke(
            "run", "--suite", "team-minor-edit-tier1", "--profile", profile);

        Assert.Equal(2, code);
        Assert.Contains(errors, e => e.Contains("endpoint"));
    }

    /// <summary>
    /// A refusal names the FIRST blocker, never the only one — so when both
    /// names are wrong, both must be reported. The old text was the single
    /// string "Error: profile or suite not found": an `or`, with no search
    /// path, leaving the reader unable to tell which of the two they had
    /// typo'd or that three stores are consulted in a fixed order.
    /// </summary>
    [Fact]
    public void Both_a_bad_profile_and_a_bad_suite_are_reported_not_just_the_first()
    {
        var (code, errors) = Invoke(
            "run", "--suite", "vett-test-no-such-suite-2f9c1",
            "--profile", "vett-test-no-such-profile-2f9c1",
            "--endpoint", SomeEndpoint, "--model", SomeModel);

        Assert.Equal(2, code);
        Assert.Contains(errors, e => e.Contains("vett-test-no-such-profile-2f9c1"));
        Assert.Contains(errors, e => e.Contains("vett-test-no-such-suite-2f9c1"));

        // …and each one says WHERE it looked. A "not found" with no search path
        // is unactionable when the same name can come from three stores.
        Assert.Equal(2, errors.Count(e => e.Contains("not found in any of")));
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
}
