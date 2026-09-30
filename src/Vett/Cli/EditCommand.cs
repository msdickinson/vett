using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vett.Config;
using Vett.Llm;

namespace Vett.Cli;

/// <summary>
/// `vett edit` — one-shot LLM rewrite of a selection. Reads a JSON
/// object on stdin describing the selection + surrounding file +
/// instruction; writes the rewritten code (raw, no fence, no preamble)
/// to stdout. Exit 0 on success, non-zero with an error on stderr
/// otherwise.
///
/// Powers VETT Chat's Cmd+I inline-edit flow. Deliberately bypasses
/// the agent loop: no tools, no middleware, no JSON event stream —
/// a single chat completion. Cheaper, faster, and the contract is
/// "rewrite this selection" not "complete this task."
///
/// Stdin schema (one JSON object, the whole stdin):
///   {
///     "instruction":           "extract into a helper function",
///     "selection":             "...",                // required
///     "selection_start_line":  10,                   // 1-based, optional metadata
///     "selection_end_line":    25,                   // 1-based, optional metadata
///     "file_content":          "...",                // required (selection in context)
///     "language":              "typescript",         // hint for the model
///     "file_path":             "src/foo.ts"          // hint for the model
///   }
/// </summary>
public static class EditCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command("edit", "One-shot LLM rewrite of a code selection (powers Cmd+I inline edit). Reads JSON on stdin, writes rewritten code on stdout.");

        var profileOpt = new Option<string>("--profile") { DefaultValueFactory = _ => "coding" };
        var endpointOpt = new Option<string>("--endpoint");
        var modelOpt = new Option<string>("--model");
        var apiKeyOpt = new Option<string>("--api-key");

        cmd.Add(profileOpt);
        cmd.Add(endpointOpt);
        cmd.Add(modelOpt);
        cmd.Add(apiKeyOpt);

        cmd.SetAction(async (pr, ct) =>
        {
            var profileName = pr.GetValue(profileOpt) ?? "coding";

            var profile = Yaml.Resolve(profileName, "profiles", Yaml.LoadProfile);
            if (profile is null)
            {
                Console.Error.WriteLine($"Error: profile \"{profileName}\" not found");
                Environment.Exit(2);
                return;
            }

            // Same resolution chain as `vett chat`: flag → env → profile.
            var endpoint = Helpers.Env(pr.GetValue(endpointOpt), "VETT_LLM_ENDPOINT");
            endpoint = ChatClientFactory.FoldProfileValue(profile.Llm, endpoint, profile.Llm.Endpoint);
            var model = Helpers.Env(pr.GetValue(modelOpt), "VETT_LLM_MODEL");
            model = ChatClientFactory.FoldProfileValue(profile.Llm, model, profile.Llm.Model);
            var apiKey = Helpers.Env(pr.GetValue(apiKeyOpt), "VETT_LLM_API_KEY");
            if (string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(profile.Llm.ApiKeyEnv))
                apiKey = Environment.GetEnvironmentVariable(profile.Llm.ApiKeyEnv) ?? "";

            // Provider default is the rung below the profile — see ChatCommand.
            if (string.IsNullOrEmpty(endpoint))
                endpoint = ChatClientFactory.DefaultEndpointFor(profile.Llm.Provider) ?? "";

            // A capability profile has no endpoint/model of its own by
            // design -- the catalogue owns them. Guarding on their absence
            // would reject exactly the configs the factory is able to serve.
            if (!ChatClientFactory.BindsThroughCapacity(profile.Llm)
                && (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(model)))
            {
                Console.Error.WriteLine(
                    "Error: endpoint and model are required. Set them in the profile YAML, " +
                    "via VETT_LLM_ENDPOINT / VETT_LLM_MODEL env vars, or pass --endpoint / --model. " +
                    $"(provider={profile.Llm.Provider}, endpoint={profile.Llm.Endpoint}, model={profile.Llm.Model})");
                Environment.Exit(2);
                return;
            }

            string stdin;
            using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
            {
                stdin = await reader.ReadToEndAsync(ct);
            }
            if (string.IsNullOrWhiteSpace(stdin))
            {
                Console.Error.WriteLine("Error: stdin is empty. Pass a JSON object describing the inline edit.");
                Environment.Exit(2);
                return;
            }

            EditRequest req;
            try
            {
                req = ParseRequest(stdin);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: failed to parse stdin JSON — {ex.Message}");
                Environment.Exit(2);
                return;
            }

            if (string.IsNullOrWhiteSpace(req.Instruction))
            {
                Console.Error.WriteLine("Error: 'instruction' is required.");
                Environment.Exit(2);
                return;
            }
            if (req.Selection is null)
            {
                Console.Error.WriteLine("Error: 'selection' is required.");
                Environment.Exit(2);
                return;
            }

            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(req);

            var client = ChatClientFactory.Create(profile.Llm, endpoint, model, apiKey);
            model = ChatClientFactory.EffectiveModel(client, model);

            ChatResponse response;
            try
            {
                response = await client.GetResponseAsync(
                    new List<ChatMessage>
                    {
                        new(ChatRole.System, systemPrompt),
                        new(ChatRole.User, userPrompt),
                    },
                    new ChatOptions
                    {
                        // Inline edits want determinism. Slightly above 0 so
                        // identical-input retries don't lock to a single
                        // possibly-bad answer, but low enough that "iterate"
                        // is cheap and predictable.
                        Temperature = 0.2f,
                        TopP = 0.95f,
                    },
                    ct);
            }
            catch (OperationCanceledException)
            {
                // User cancelled (the host killed us). Exit quietly so the
                // host doesn't surface a noisy error toast.
                Environment.Exit(130);
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: LLM request failed — {ex.Message}");
                Environment.Exit(3);
                return;
            }

            var raw = response.Messages.Count > 0 ? response.Messages.Last().Text ?? "" : "";
            if (string.IsNullOrEmpty(raw))
            {
                Console.Error.WriteLine("Error: LLM returned an empty response.");
                Environment.Exit(3);
                return;
            }

            var rewritten = ExtractCode(raw);
            Console.Out.Write(rewritten);
            // No trailing newline — the host computes the replacement
            // verbatim and adds whitespace itself if needed.
        });

        return cmd;
    }

    /// <summary>
    /// Parses the stdin JSON payload. Tolerates both snake_case (the
    /// canonical wire format) and camelCase (some hosts may send it
    /// because that's the JS default).
    /// </summary>
    public static EditRequest ParseRequest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new EditRequest
        {
            Instruction = GetString(root, "instruction") ?? "",
            Selection = GetString(root, "selection"),
            SelectionStartLine = GetInt(root, "selection_start_line", "selectionStartLine"),
            SelectionEndLine = GetInt(root, "selection_end_line", "selectionEndLine"),
            FileContent = GetString(root, "file_content", "fileContent") ?? "",
            Language = GetString(root, "language") ?? "",
            FilePath = GetString(root, "file_path", "filePath") ?? "",
        };
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static int? GetInt(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
        }
        return null;
    }

    /// <summary>
    /// System prompt for inline-edit mode. Tightly scoped: the model is
    /// rewriting a fixed region per a user instruction. Forbids
    /// preambles, fenced code blocks, and explanations because the host
    /// pastes the response verbatim into the file.
    /// </summary>
    public static string BuildSystemPrompt()
    {
        return
            "You are an inline code editor inside a developer's IDE. The user has selected a region " +
            "of code and given an instruction. Your job is to produce the replacement text for that " +
            "selection — nothing more.\n\n" +
            "RULES:\n" +
            "- Output ONLY the replacement code. No preamble, no explanation, no commentary.\n" +
            "- Do NOT wrap your output in a fenced code block (no ``` at all).\n" +
            "- Do NOT include the surrounding context — only the replacement for the marked SELECTION.\n" +
            "- Match the file's existing style (indentation, quote style, naming conventions).\n" +
            "- Preserve the leading indentation of the selection unless the instruction says otherwise.\n" +
            "- If the instruction is unclear or the selection cannot be safely rewritten, output the " +
            "  selection unchanged. Never invent functionality the user didn't ask for.\n" +
            "- The replacement is pasted verbatim into the file. Trailing whitespace and newlines are preserved literally.";
    }

    /// <summary>
    /// User prompt: the instruction first (so the LLM weighs it most),
    /// then the file context with the selection clearly delimited.
    /// </summary>
    public static string BuildUserPrompt(EditRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("INSTRUCTION:\n").Append(req.Instruction.Trim()).Append("\n\n");
        if (!string.IsNullOrEmpty(req.FilePath))
            sb.Append("FILE: ").Append(req.FilePath).Append('\n');
        if (!string.IsNullOrEmpty(req.Language))
            sb.Append("LANGUAGE: ").Append(req.Language).Append('\n');
        if (req.SelectionStartLine.HasValue && req.SelectionEndLine.HasValue)
            sb.Append("SELECTION SPAN: lines ").Append(req.SelectionStartLine).Append('-').Append(req.SelectionEndLine).Append('\n');
        sb.Append('\n');

        if (!string.IsNullOrEmpty(req.FileContent))
        {
            sb.Append("--- FULL FILE (for context) ---\n");
            sb.Append(req.FileContent);
            if (!req.FileContent.EndsWith('\n')) sb.Append('\n');
            sb.Append("--- END FILE ---\n\n");
        }

        sb.Append("--- SELECTION TO REPLACE ---\n");
        sb.Append(req.Selection ?? "");
        if (!string.IsNullOrEmpty(req.Selection) && !req.Selection!.EndsWith('\n')) sb.Append('\n');
        sb.Append("--- END SELECTION ---\n\n");

        sb.Append("Output the replacement for the SELECTION only — no preamble, no fenced block.");
        return sb.ToString();
    }

    /// <summary>
    /// Strip a fenced code block off the LLM's response if present.
    /// Models often ignore the "no fence" instruction, especially on
    /// short outputs. The first ``` block in the response is treated as
    /// the canonical answer; anything outside is preamble we throw away.
    /// Falls back to the raw text if no fence is found.
    /// </summary>
    public static string ExtractCode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        // Match a fenced block: optional language tag, then any content
        // up to the closing fence. RegexOptions.Singleline lets `.` cross
        // newlines so multi-line blocks are captured in one go.
        var fence = Regex.Match(raw, @"```[ \t]*[\w+\-.#]*[ \t]*\r?\n(.*?)\r?\n[ \t]*```",
            RegexOptions.Singleline);
        if (fence.Success)
        {
            return fence.Groups[1].Value;
        }

        // No fence — return as-is, trimmed of a single leading/trailing
        // newline if the model padded its answer (common for chat-tuned
        // models). Don't strip all whitespace; leading indent matters.
        var s = raw;
        if (s.StartsWith("\r\n", StringComparison.Ordinal)) s = s[2..];
        else if (s.StartsWith('\n')) s = s[1..];
        if (s.EndsWith("\r\n", StringComparison.Ordinal)) s = s[..^2];
        else if (s.EndsWith('\n')) s = s[..^1];
        return s;
    }
}

/// <summary>Wire-format payload from VETT Chat's inline-edit controller.</summary>
public sealed class EditRequest
{
    public string Instruction { get; set; } = "";
    public string? Selection { get; set; }
    public int? SelectionStartLine { get; set; }
    public int? SelectionEndLine { get; set; }
    public string FileContent { get; set; } = "";
    public string Language { get; set; } = "";
    public string FilePath { get; set; } = "";
}
