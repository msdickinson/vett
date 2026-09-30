using System.CommandLine;
using Vett.Sandbox;
using Microsoft.Extensions.Logging;
using Vett.Plugin;
using Vett.Tools;

namespace Vett.Cli;

public static class CallCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command("call", "Test a single tool");
        var toolArg = new Argument<string>("tool") { Description = "Tool name" };
        var argOpt = new Option<string[]>("--arg") { AllowMultipleArgumentsPerToken = true };
        cmd.Add(toolArg);
        cmd.Add(argOpt);

        cmd.SetAction(async (pr, ct) =>
        {
            var name = pr.GetValue(toolArg) ?? "";
            var cwd = Directory.GetCurrentDirectory();

            // Builtins + plugins (YAML tools and subprocess plugins) — without
            // this merge, `vett call <plugin-tool>` would always say "Unknown tool".
            var tools = Builtins.All();
            using var plugins = new PluginPool(cwd);
            await plugins.WarmUpAsync(ct);
            foreach (var pname in plugins.ToolNames())
            {
                var pfn = plugins.GetTool(pname);
                if (pfn is not null) tools[pname] = pfn;
            }

            if (!tools.TryGetValue(name, out var fn))
            {
                logger.LogError($"Unknown tool \"{name}\". Available: {string.Join(", ", tools.Keys)}");
                // 2 = config error, nothing ran. Was a bare `return;` → exit 0,
                // so a script probing whether a tool exists was told "yes".
                return 2;
            }

            var args = new Dictionary<string, object?>();
            foreach (var a in pr.GetValue(argOpt) ?? [])
            {
                var p = a.Split('=', 2);
                if (p.Length == 2)
                    args[p[0]] = p[1];
            }

            // Use DirectBash for local tool testing — no sidecar needed.
            using var bash = new DirectBash(cwd);
            Console.WriteLine(await fn(args, bash, "call", ct));
            return 0;
        });

        return cmd;
    }
}
