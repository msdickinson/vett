using System.CommandLine;
using Microsoft.Extensions.Logging;
using Vett.Cli;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// The rc=0-ON-FATAL-ERROR defect class, swept across the commands that still
/// had it. `vett run` was fixed first and has its own file
/// (<see cref="RunCommandExitCodeTests"/>); this covers the rest.
///
/// THE DEFECT. `cmd.SetAction(async (pr, ct) => { ... })` binds the
/// Task-returning overload unless EVERY path returns int. Every fatal path in
/// these four commands was a bare `return;` after a LogError, so the command
/// printed a fatal error and the process exited 0. Measured on the built
/// binary, 2026-08-25, before the fix:
///
///   vett analyze results.json          (no --model)      → rc=0
///   vett call vett-no-such-tool-xyz                      → rc=0
///   vett replay --cache X --profile <bad>                → rc=0
///   vett chat --profile <bad>                            → rc=0
///
/// It is not a cosmetic bug. `vett chat --stdio` is how the VETT Chat
/// extension launches a session: a zero exit reads as a healthy session that
/// happened to emit no events, not as a failed launch. And anything gating on
/// `$?` — a wrapper script, an overnight runner, a CI step — read total config
/// failure as success. rc=0 IS NOT EVIDENCE THAT SOMETHING RAN.
///
/// Codes follow the existing convention rather than inventing a second one:
/// 2 = config/usage error (nothing ran), 1 = ran but errored, 0 = clean.
///
/// Invoked through the SAME path Program.cs uses — root.Parse(args).Invoke() —
/// because the defect was never in the error handling. It was in which
/// SetAction overload the lambda bound to, and only the real invoke path can
/// observe that. A test calling a helper and inspecting a returned value would
/// have passed against the broken code.
/// </summary>
// Mutates HOME / VETT_LLM_* for the duration of a test, so it shares the
// existing env-serialising collection rather than racing other tests.
[Collection("declare-done-env")]
public class CliExitCodeSweepTests
{
    private static (int Code, List<string> Errors) InvokeWithLog(Func<ILogger, Command> factory, params string[] args)
    {
        var log = new CapturingLogger();
        var root = new RootCommand("test");
        root.Add(factory(log));
        var code = root.Parse(args).Invoke();
        return (code, log.Errors);
    }

    /// <summary>
    /// ⭐ THE POSITIVE CONJUNCT, one per command. Without these, an
    /// implementation returning 2 for literally every input would satisfy every
    /// other assertion in this file. `--help` exits through the identical
    /// Parse().Invoke() path, proving the rig can observe a zero at all.
    /// </summary>
    [Theory]
    [InlineData("analyze")]
    [InlineData("call")]
    [InlineData("replay")]
    [InlineData("chat")]
    public void The_rig_can_observe_a_zero(string command)
    {
        var (code, _) = InvokeWithLog(CommandFactory(command), command, "--help");
        Assert.Equal(0, code);
    }

    [Fact]
    public void Analyze_without_a_model_is_a_config_error_not_a_success()
    {
        using var _ = new ScopedEnv(("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null));
        var results = Path.Combine(Path.GetTempPath(), $"vett-analyze-{Guid.NewGuid():N}.json");
        File.WriteAllText(results, "{}");

        try
        {
            var (code, errors) = InvokeWithLog(AnalyzeCommand.Create, "analyze", results);

            Assert.NotEqual(0, code);
            Assert.Equal(2, code);
            Assert.Contains(errors, e => e.Contains("model required"));
        }
        finally { File.Delete(results); }
    }

    [Fact]
    public void Call_with_an_unknown_tool_is_a_config_error_not_a_success()
    {
        var (code, errors) = InvokeWithLog(CallCommand.Create, "call", "vett-test-no-such-tool-2f9c1");

        Assert.NotEqual(0, code);
        Assert.Equal(2, code);
        Assert.Contains(errors, e => e.Contains("vett-test-no-such-tool-2f9c1"));

        // The refusal lists what IS available — otherwise the caller cannot
        // tell a typo from a tool that was never registered.
        Assert.Contains(errors, e => e.Contains("Available:"));
    }

    /// <summary>
    /// A tool that really exists must still run and exit 0 — this is the
    /// positive conjunct with teeth, since it exercises the whole action body
    /// rather than the help short-circuit.
    /// </summary>
    [Fact]
    public void Call_with_a_real_tool_succeeds()
    {
        var (code, _) = InvokeWithLog(CallCommand.Create, "call", "think", "--arg", "thought=hello");
        Assert.Equal(0, code);
    }

    [Fact]
    public void Replay_with_a_nonexistent_profile_is_a_config_error_not_a_success()
    {
        var (code, errors) = InvokeWithLog(ReplayCommand.Create,
            "replay", "--cache", "no-such-cache.json", "--profile", "vett-test-no-such-profile-2f9c1");

        Assert.NotEqual(0, code);
        Assert.Equal(2, code);
        Assert.Contains(errors, e => e.Contains("vett-test-no-such-profile-2f9c1"));

        // "Profile not found" with neither the name nor the search path is
        // unactionable when one name resolves from three stores in order.
        Assert.Contains(errors, e => e.Contains("not found in any of"));
    }

    [Fact]
    public void Chat_with_a_nonexistent_profile_is_a_config_error_not_a_success()
    {
        var (code, errors) = InvokeWithLog(ChatCommand.Create,
            "chat", "--profile", "vett-test-no-such-profile-2f9c1");

        Assert.NotEqual(0, code);
        Assert.Equal(2, code);
        Assert.Contains(errors, e => e.Contains("vett-test-no-such-profile-2f9c1"));
        Assert.Contains(errors, e => e.Contains("not found in any of"));
    }

    /// <summary>
    /// A profile that loads but names no endpoint/model is the OTHER fatal path
    /// in chat, and it was a separate bare `return;`. A fix that wired up only
    /// the first one fails here.
    /// </summary>
    [Fact]
    public void Chat_without_an_endpoint_or_model_is_a_config_error_not_a_success()
    {
        using var _ = new ScopedEnv(("VETT_LLM_ENDPOINT", null), ("VETT_LLM_MODEL", null));

        var home = Path.Combine(Path.GetTempPath(), $"vett-chat-exit-{Guid.NewGuid():N}");
        var store = Path.Combine(home, ".vett", "profiles");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "vett-test-no-endpoint.yaml"), """
            name: vett-test-no-endpoint
            system_prompt: |
              fixture
            llm:
              provider: local
            tools: []
            """);

        using var __ = new ScopedEnv(("HOME", home), ("USERPROFILE", home));
        try
        {
            var (code, errors) = InvokeWithLog(ChatCommand.Create,
                "chat", "--profile", "vett-test-no-endpoint");

            Assert.Equal(2, code);
            Assert.Contains(errors, e => e.Contains("endpoint and model are required"));
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    private static Func<ILogger, Command> CommandFactory(string name) => name switch
    {
        "analyze" => AnalyzeCommand.Create,
        "call" => CallCommand.Create,
        "replay" => ReplayCommand.Create,
        "chat" => ChatCommand.Create,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such command in this sweep"),
    };

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
