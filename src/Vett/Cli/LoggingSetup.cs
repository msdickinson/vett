using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Vett.Cli;

/// <summary>
/// Console logging configuration for the whole CLI.
///
/// WHY THIS EXISTS AS A NAMED, TESTABLE UNIT: `AddSimpleConsole` leaves
/// <see cref="ConsoleLoggerOptions.LogToStandardErrorThreshold"/> at its
/// default of <see cref="LogLevel.None"/>, which routes EVERY level —
/// warnings and errors included — to STDOUT. `vett team-bench --json`
/// writes its summary to stdout too, so any warning logged during a run
/// was interleaved into the JSON document.
///
/// That is not theoretical. Two of the four post-repair campaign runs
/// (armA-run4, armD-run4) produced summary files beginning:
///
///     12:33:27 warn: vett[0] [g1-blocks-2p] timeout after 7200s ...
///     {
///       "schema_version": 1,
///
/// The capturing script was correct — it did `> $TAG.json 2> $TAG.log`.
/// The contamination came from vett writing the warning to the wrong
/// stream. A plain `json.load()` throws `Extra data: line 1 column 3`,
/// and JsonWire.cs documents downstream consumers (Tier 1.24 event
/// stream, AI Timeline, dashboards) keying off that document.
///
/// Routing warnings to stderr keeps stdout a clean machine-readable
/// channel and puts the warnings where the capturing script was already
/// looking for them. It changes no threshold, silences nothing, and
/// drops no message: <see cref="LogLevel.Information"/> and below still
/// go to stdout exactly as before.
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// Levels at or above this go to stderr; everything below goes to
    /// stdout. Asserted by LoggingSetupTests — a consumer that pipes
    /// `--json` is relying on this value, so changing it silently
    /// re-corrupts every captured summary.
    /// </summary>
    public const LogLevel StandardErrorThreshold = LogLevel.Warning;

    public static void Configure(ILoggingBuilder builder)
    {
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
            options.ColorBehavior = LoggerColorBehavior.Enabled;
        });

        // Configure the PROVIDER options (not the formatter options that
        // AddSimpleConsole takes) — this is the knob that picks the
        // stream. Done via Services.Configure so provider registration
        // is left exactly as AddSimpleConsole set it up.
        builder.Services.Configure<ConsoleLoggerOptions>(
            options => options.LogToStandardErrorThreshold = StandardErrorThreshold);

        builder.SetMinimumLevel(LogLevel.Information);
    }
}
