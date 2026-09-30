using System.Diagnostics;
using System.Runtime.InteropServices;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Plugin;

public sealed class HookDef
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";
    [YamlMember(Alias = "command")] public string Command { get; set; } = "";
}

public sealed class HookFile
{
    [YamlMember(Alias = "hooks")] public List<HookDef> Hooks { get; set; } = [];
}

public static class Hooks
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties().Build();

    public static List<HookDef> Load(string workDir, string phase)
    {
        var dir = Path.Combine(workDir, phase);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.yaml")
            .SelectMany(f => Yaml.Deserialize<HookFile>(File.ReadAllText(f))?.Hooks ?? [])
            .ToList();
    }

    public static void Run(List<HookDef> hooks)
    {
        var (shell, flag) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ("cmd", "/c") : ("/bin/bash", "-c");
        foreach (var h in hooks)
        {
            using var p = Process.Start(new ProcessStartInfo(shell, $"{flag} {h.Command}") { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new InvalidOperationException($"Hook \"{h.Name}\" failed to start");

            if (!p.WaitForExit(300_000))
            {
                try { p.Kill(); } catch { }
                throw new InvalidOperationException($"Hook \"{h.Name}\" timed out after 5 minutes");
            }
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"Hook \"{h.Name}\" failed (exit {p.ExitCode})");
        }
    }
}
