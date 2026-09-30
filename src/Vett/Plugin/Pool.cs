using System.Diagnostics;
using System.Text.Json;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Plugin;

/// <summary>
/// Manages running plugin subprocesses. Spawns them at startup,
/// keeps them warm, provides ToolFn/MiddlewareFn wrappers, cleans
/// up on dispose. YAML tools are handled separately — they have no
/// subprocess; they're declarative bash wrappers via YamlTools.Create.
/// </summary>
public sealed class PluginPool : IDisposable
{
    private readonly string _workDir;
    private readonly string _cacheDir;
    private readonly Dictionary<string, (PluginInfo Info, PluginProcess Proc)> _running = new();
    private readonly Dictionary<string, (PluginInfo Info, ToolFn Fn)> _yamlTools = new();

    public PluginPool(string workDir)
    {
        _workDir = workDir;
        _cacheDir = System.IO.Path.Combine(workDir, ".vett", "cache");
    }

    public List<BuildResult> Build()
    {
        var plugins = Scanner.Scan(_workDir);
        return Builder.Build(plugins, _cacheDir);
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        Build();

        foreach (var p in Scanner.Scan(_workDir))
        {
            // YAML tools aren't subprocesses; route them through YamlTools.Create
            // which builds a ToolFn that executes the declared bash command via
            // the sandbox at call time.
            if (p.Language == Lang.Yaml)
            {
                if (p.Kind != "tool") continue;
                var fn = YamlTools.Create(p);
                if (fn is not null) _yamlTools[p.Name] = (p, fn);
                continue;
            }

            var (cmd, args) = Builder.RunCmd(p.Language, p.Path);
            try
            {
                var proc = await PluginProcess.StartAsync(p.Name, cmd, args, ct);
                _running[p.Name] = (p, proc);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip failed plugins */ }
        }
    }

    public ToolFn? GetTool(string name)
    {
        if (_yamlTools.TryGetValue(name, out var y)) return y.Fn;
        if (!_running.TryGetValue(name, out var p) || p.Info.Kind != "tool")
            return null;

        var proc = p.Proc;
        return async (args, sandbox, sessionId, ct) => await proc.ExecuteAsync(args, ct);
    }

    public List<string> ToolNames()
        => _running.Where(kv => kv.Value.Info.Kind == "tool").Select(kv => kv.Key)
            .Concat(_yamlTools.Keys)
            .ToList();

    public JsonElement GenerateSchema(string name)
    {
        var info = _running.TryGetValue(name, out var p) ? p.Info
                 : _yamlTools.TryGetValue(name, out var y) ? y.Info
                 : null;
        if (info is null)
            return default;

        var props = info.Params.ToDictionary(
            x => x.Name,
            x => (object)new { type = x.Type, description = x.Desc });
        var required = info.Params.Select(x => x.Name).ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name,
                description = "",
                parameters = new { type = "object", properties = props, required },
            }
        });
    }

    public MiddlewareFn? GetMiddleware(string name)
    {
        if (!_running.TryGetValue(name, out var p) || p.Info.Kind != "middleware")
            return null;

        var proc = p.Proc;
        return async (state, ct) =>
        {
            var req = JsonSerializer.Serialize(new
            {
                type = "process",
                state.Iteration,
                state.MaxIterations,
                state.StopLoop,
                state.StopReason,
            });

            var resp = await proc.ExecuteMiddlewareAsync(req, ct);
            if (resp.HasValue)
            {
                var r = resp.Value;
                if (r.TryGetProperty("stop_loop", out var sl) && sl.GetBoolean())
                {
                    state.StopLoop = true;
                    state.StopReason = r.TryGetProperty("stop_reason", out var sr)
                        ? sr.GetString() ?? ""
                        : "external_middleware";
                }
            }
        };
    }

    public List<string> MiddlewareNames()
        => _running.Where(kv => kv.Value.Info.Kind == "middleware").Select(kv => kv.Key).ToList();

    /// <summary>
    /// All loaded middleware as a name → fn map for the resolver. Without this,
    /// MiddlewareResolver would silently drop any plugin middleware referenced
    /// in a profile's `middleware:` list.
    /// </summary>
    public Dictionary<string, MiddlewareFn> AllMiddlewares()
    {
        var result = new Dictionary<string, MiddlewareFn>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in MiddlewareNames())
        {
            var fn = GetMiddleware(name);
            if (fn is not null) result[name] = fn;
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var (_, (_, proc)) in _running)
            proc.Dispose();
        _running.Clear();
    }

}
