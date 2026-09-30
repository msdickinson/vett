using System.CommandLine;
using System.Text;
using Microsoft.Extensions.Logging;
using Vett.Cli;

// Force UTF-8 so the validate/profiles output (✓ ✗ ⚠ → arrows) renders
// correctly on Windows consoles, which default to a code page that can't
// represent these characters and shows `?` instead.
Console.OutputEncoding = Encoding.UTF8;

using var loggerFactory = LoggerFactory.Create(LoggingSetup.Configure);

var logger = loggerFactory.CreateLogger("vett");

var root = new RootCommand("VETT: a harness for running AI coding agents on your own hardware");

root.Add(SimpleCommands.Version());
root.Add(SimpleCommands.Init(logger));
root.Add(SimpleCommands.Install(logger));
root.Add(SimpleCommands.Build(logger));
root.Add(SimpleCommands.Doctor(logger));
root.Add(SimpleCommands.Profiles(logger));
root.Add(ChatCommand.Create(logger));
root.Add(EditCommand.Create(logger));
root.Add(CallCommand.Create(logger));
root.Add(RunCommand.Create(logger));
root.Add(TeamBenchCommand.Create(logger));
root.Add(BenchCommand.Create(logger));
root.Add(ReplayCommand.Create(logger));
root.Add(AnalyzeCommand.Create(logger));
root.Add(ValidateCommand.Create(logger));
root.Add(EscalationLedgerCommand.Create(logger));
root.Add(CapacityCommand.Create(logger));

// System.CommandLine installs its OWN default exception handler, and it runs
// INSIDE Invoke() -- so a try/catch wrapped around Invoke() never sees the
// exception at all. That handler prints "Unhandled exception:" plus the full
// stack and returns 1. Replacing it is the only way to intercept; the general
// arm below reproduces its output and its exit code verbatim, so nothing but
// VettException changes shape.
var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

try
{
    return root.Parse(args).Invoke(invocation);
}
catch (Vett.Llm.VettException ex)
{
    // A VettException is a MESSAGE WE WROTE FOR A HUMAN. Letting it escape
    // printed the .NET banner and a stack trace over the top of it, which is
    // how a capacity block first surfaced: "Unhandled exception:" followed by
    // eight frames of ConcurrentDictionary internals, with the sentence that
    // actually named the holder buried on line one. In --stdio mode -- how the
    // VETT Chat extension spawns this process -- that reads as a crash rather
    // than as "the pool is busy, here is who has it".
    //
    // rc=2 matches the convention every other config-class refusal already
    // uses ("endpoint and model are required"): nothing ran, and re-running
    // unchanged will not help. Observed while running C4 and C5 of
    // PREREG-2026-08-28.
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    // Same text and same exit code System.CommandLine's default handler
    // produced, so disabling it above is not itself a behaviour change.
    Console.Error.WriteLine($"Unhandled exception: {ex}");
    return 1;
}
