using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Llm;

namespace Vett.Cli;

public static class AnalyzeCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command("analyze", "Analyze failures with LLM");

        var resultsArg = new Argument<string>("results") { Description = "results.json path" };
        var endpointOpt = new Option<string>("--endpoint");
        var modelOpt = new Option<string>("--model");
        var apiKeyOpt = new Option<string>("--api-key");
        var providerOpt = new Option<string>("--provider") { DefaultValueFactory = _ => "local" };

        cmd.Add(resultsArg);
        cmd.Add(endpointOpt);
        cmd.Add(modelOpt);
        cmd.Add(apiKeyOpt);
        cmd.Add(providerOpt);

        cmd.SetAction(async (pr, ct) =>
        {
            var endpoint = Helpers.Env(pr.GetValue(endpointOpt), "VETT_LLM_ENDPOINT");
            var model = Helpers.Env(pr.GetValue(modelOpt), "VETT_LLM_MODEL");
            var apiKey = Helpers.Env(pr.GetValue(apiKeyOpt), "VETT_LLM_API_KEY");
            var provider = pr.GetValue(providerOpt) ?? "local";

            if (string.IsNullOrEmpty(model) || (provider == "local" && string.IsNullOrEmpty(endpoint)))
            {
                logger.LogError("model required (and endpoint for local provider). "
                    + "Set --model / --endpoint, or VETT_LLM_MODEL / VETT_LLM_ENDPOINT.");
                // 2 = config error, nothing ran. This was a bare `return;`,
                // which bound the Task-returning SetAction overload and exited
                // 0 — measured. A caller gating on $? read "analysis complete"
                // when no analysis had been attempted.
                return 2;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(pr.GetValue(resultsArg)!));
            var client = ChatClientFactory.Create(
                new Config.LlmConfig { Provider = provider, Endpoint = endpoint, Model = model },
                apiKeyOverride: apiKey);

            foreach (var inst in doc.RootElement.GetProperty("instances").EnumerateArray())
            {
                if (!inst.TryGetProperty("error", out var err) || err.GetString() is not { Length: > 0 })
                    continue;

                var id = inst.GetProperty("instance_id").GetString() ?? "?";
                Console.Write($"  {id} \u2192 ");

                try
                {
                    var prompt = $"Categorize: {id}, error: {err}. Reply: tool_error/wrong_approach/context_limit/model_limitation";
                    var messages = new List<ChatMessage>
                    {
                        new(ChatRole.User, prompt),
                    };
                    var r = await client.GetResponseAsync(messages, new ChatOptions
                    {
                        Temperature = 0.1f,
                        TopP = 0.95f,
                    }, ct);

                    Console.WriteLine(r.Messages.Count > 0 ? r.Messages.Last().Text?.Trim() : "?");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Console.WriteLine($"failed: {ex.Message}");
                }
            }

            return 0;
        });

        return cmd;
    }
}
