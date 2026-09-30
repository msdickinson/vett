using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Plugin;

/// <summary>
/// YAML-only tools — declarative bash wrappers. No subprocess,
/// no process pool. They run commands through the sandbox directly.
/// </summary>
public static class YamlTools
{
    public static ToolFn? Create(PluginInfo info)
    {
        if (info.Language != Lang.Yaml)
            return null;

        var def = Load(info.Path);
        if (def is null)
            return null;

        return async (args, sandbox, sessionId, ct) =>
        {
            var cmd = def.Command;
            foreach (var (k, v) in args)
                cmd = cmd.Replace("{" + k + "}", FormatArg(v));

            var r = await sandbox.BashExecAsync(sessionId, cmd, def.Timeout, ct);
            return r.TimedOut
                ? $"Timed out after {def.Timeout}s.\n{r.Stdout}"
                : r.Stdout;
        };
    }

    /// <summary>
    /// Format an arg value for shell substitution. JsonElement.ToString() yields
    /// JSON-formatted text (strings come back with quotes), which would break
    /// the resulting bash command. Unwrap strings/numbers/bools to their raw form.
    /// </summary>
    private static string FormatArg(object? v)
    {
        if (v is null) return "";
        if (v is string s) return s;
        if (v is System.Text.Json.JsonElement je)
        {
            return je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString() ?? "",
                System.Text.Json.JsonValueKind.Null => "",
                System.Text.Json.JsonValueKind.Number => je.ToString(),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                _ => je.ToString(),
            };
        }
        return v.ToString() ?? "";
    }

    private static YamlToolDef? Load(string path)
    {
        try
        {
            var yaml = new YamlDotNet.Serialization.DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return yaml.Deserialize<YamlToolDef>(File.ReadAllText(path));
        }
        catch { return null; }
    }
}

public sealed class YamlToolDef
{
    [YamlDotNet.Serialization.YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlDotNet.Serialization.YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlDotNet.Serialization.YamlMember(Alias = "command")]
    public string Command { get; set; } = "";

    [YamlDotNet.Serialization.YamlMember(Alias = "timeout_seconds")]
    public int Timeout { get; set; } = 60;
}
