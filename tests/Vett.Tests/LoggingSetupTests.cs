using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Vett.Cli;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Failure test for the stream routing of the CLI's console logger.
///
/// WHY: `vett team-bench --json` writes its summary document to stdout.
/// Before 2026-08-24 the logger left LogToStandardErrorThreshold at its
/// default (LogLevel.None), which sends EVERY level to stdout, so a
/// warning logged mid-run was interleaved into the JSON. Two of the four
/// post-repair campaign summaries (armA-run4, armD-run4) begin with a
/// `warn:` line before their opening brace and cannot be parsed by
/// `json.load()`. The capturing script was already redirecting streams
/// correctly; vett was writing to the wrong one.
/// </summary>
public class LoggingSetupTests
{
    private static ConsoleLoggerOptions ResolveConsoleOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging(LoggingSetup.Configure);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().Get("");
    }

    /// <summary>
    /// The regression guard. LogLevel.None is the framework default and
    /// is precisely the broken value — asserting "not None" alone would
    /// pass for Trace, which would be just as wrong in the other
    /// direction, so this pins the exact level.
    /// </summary>
    [Fact]
    public void Warnings_and_above_are_routed_to_stderr_so_stdout_stays_parseable()
    {
        var options = ResolveConsoleOptions();

        Assert.Equal(LogLevel.Warning, options.LogToStandardErrorThreshold);
        Assert.NotEqual(LogLevel.None, options.LogToStandardErrorThreshold);
    }

    /// <summary>
    /// The other side of the routing: Information must NOT move to
    /// stderr. A fix that shoved everything to stderr would make the
    /// JSON parse while silently emptying the run logs the campaign
    /// scripts read — trading one evidence loss for another.
    /// </summary>
    [Fact]
    public void Information_and_below_still_go_to_stdout()
    {
        var options = ResolveConsoleOptions();

        Assert.True(LogLevel.Information < options.LogToStandardErrorThreshold,
            $"Information must stay on stdout, but the stderr threshold is " +
            $"{options.LogToStandardErrorThreshold}, which would divert it.");
    }

    /// <summary>
    /// The constant and the resolved option must not drift apart — the
    /// doc comment on the constant is what a future reader will trust.
    /// </summary>
    [Fact]
    public void Declared_threshold_matches_what_the_container_actually_resolves()
    {
        Assert.Equal(LoggingSetup.StandardErrorThreshold,
                     ResolveConsoleOptions().LogToStandardErrorThreshold);
    }

    /// <summary>
    /// The minimum level is load-bearing for the campaign logs: raising
    /// it above Information would silently drop the per-instance
    /// progress lines the queue scripts tail.
    /// </summary>
    [Fact]
    public void Minimum_level_still_admits_information()
    {
        var services = new ServiceCollection();
        services.AddLogging(LoggingSetup.Configure);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        Assert.True(factory.CreateLogger("vett").IsEnabled(LogLevel.Information));
    }
}
