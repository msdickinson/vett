using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;
using Vett.Agent;
using Vett.Config;
using Vett.Llm;
using Vett.Plugin;
using Vett.Sandbox;
using Microsoft.Extensions.AI;
using Vett.Tools;

namespace Vett.Cli;

public static class ReplayCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command("replay", "Replay from cached LLM responses");

        var cacheOpt = new Option<string>("--cache") { Required = true };
        var profileOpt = new Option<string>("--profile") { Required = true };
        var strictOpt = new Option<bool>("--strict");

        cmd.Add(cacheOpt);
        cmd.Add(profileOpt);
        cmd.Add(strictOpt);

        cmd.SetAction(async (pr, ct) =>
        {
            var profile = Yaml.Resolve(pr.GetValue(profileOpt)!, "profiles", Yaml.LoadProfile);
            if (profile is null)
            {
                // Names the profile AND where it looked. "Profile not found"
                // with neither is unactionable when one name resolves from
                // three stores in a fixed order.
                logger.LogError("Profile '{Profile}' not found in any of: {Dirs}",
                    pr.GetValue(profileOpt), string.Join(", ", Yaml.ResolveSearchDirs("profiles")));
                // 2 = config error, nothing ran. Was a bare `return;` → exit 0.
                return 2;
            }

            var endpoint = Helpers.Env(null, "VETT_LLM_ENDPOINT");
            var model = Helpers.Env(null, "VETT_LLM_MODEL");
            // ⛔ THE PROFILE'S OWN MODEL WAS NEVER FOLDED IN HERE -- A BUG FOR
            // EVERY PROFILE, NOT JUST CAPABILITY ONES. `endpoint` recovers on its
            // own because an empty override makes ChatClientFactory fall through to
            // profile.Llm.Endpoint inside Create; `model` has no such fallback, it
            // is carried to LlmSettings as a bare string. So unless $VETT_LLM_MODEL
            // happened to be set, `vett replay` sent ChatOptions.ModelId = "" on
            // every request. Invisible while replaying from cache, which is the
            // usual case -- it only bites in ReplayFallback, i.e. exactly the runs
            // that go live because the cache missed.
            model = ChatClientFactory.FoldProfileValue(profile.Llm, model, profile.Llm.Model);

            var innerClient = ChatClientFactory.Create(profile.Llm, endpoint);
            var client = new Vett.Llm.ReplayChatClient(innerClient);
            client.Mode = pr.GetValue(strictOpt) ? CacheMode.ReplayStrict : CacheMode.ReplayFallback;
            client.LoadFrom(pr.GetValue(cacheOpt)!);
            Console.WriteLine($"Loaded {client.EntryCount} cached responses");

            // Use DirectBash — replay is always local.
            var cwd = Directory.GetCurrentDirectory();
            using var bash = new DirectBash(cwd);
            using var plugins = new PluginPool(cwd);
            await plugins.WarmUpAsync(ct);

            // Builtins + plugins so cached tool calls find their handlers.
            var tools = Builtins.All();
            var schemas = Helpers.LoadSchemas(profile.Tools);
            foreach (var pname in plugins.ToolNames())
            {
                var pfn = plugins.GetTool(pname);
                if (pfn is null) continue;
                tools[pname] = pfn;
                var s = plugins.GenerateSchema(pname);
                if (s.ValueKind != JsonValueKind.Undefined && profile.Tools.Contains(pname))
                    schemas.Add(s);
            }

            var pluginMws = plugins.AllMiddlewares();
            // Safe on the wrapper: CachingChatClient.GetService delegates to the
            // inner client, so a capability profile's leased model is reachable
            // through it. See ChatClientFactory.EffectiveModel.
            model = ChatClientFactory.EffectiveModel(client, model);
            var llm = new LlmSettings(client, model, profile.Llm.Temperature ?? 1.0, profile.Llm.TopP, profile.Llm.MaxOutputTokens,
            profile.Llm.PresencePenalty, profile.Llm.FrequencyPenalty);
            var caps = new AgentCapabilities(tools, schemas, MiddlewareResolver.ResolveOrDefault(profile.Middleware, pluginMws));
            var env = new AgentEnvironment(bash, "replay", profile.MaxIterations);

            var r = await AgentLoop.RunAsync(llm, caps, env, profile.SystemPrompt, "Replay", ct);

            Console.WriteLine($"Replay: {r.Iterations} iters, {r.StopReason}");

            var cacheDir = Path.GetDirectoryName(pr.GetValue(cacheOpt)!) ?? ".";
            client.SaveTo(Path.Combine(cacheDir, "replay-cache.json"));
            return 0;
        });

        return cmd;
    }
}
