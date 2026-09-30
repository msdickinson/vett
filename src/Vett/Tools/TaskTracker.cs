using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Vett.Sandbox;

namespace Vett.Tools;

/// <summary>
/// Per-session task list, mirroring openhands-tools/task_tracker/definition.py
/// TaskTrackerExecutor. View returns the current list (or the canonical empty
/// hint), plan replaces it. Storage is in-memory per sessionId — sufficient
/// for VETT's one-process-per-instance model. No disk persistence.
/// </summary>
public static class TaskTracker
{
    private static readonly ConcurrentDictionary<string, List<TaskItem>> Lists = new();

    public static Task<string> RunAsync(
        Dictionary<string, object?> args, ISandbox _, string sessionId, CancellationToken __)
    {
        var cmd = Builtins.Str(args, "command", "view");
        var list = Lists.GetOrAdd(sessionId, _ => new List<TaskItem>());

        return Task.FromResult(cmd switch
        {
            "plan" => Plan(list, args),
            "view" => View(list),
            _ => $"Unknown command: {cmd}. Supported commands are \"view\" and \"plan\"."
        });
    }

    private static string Plan(List<TaskItem> list, Dictionary<string, object?> args)
    {
        var newList = ParseTaskList(args);
        list.Clear();
        list.AddRange(newList);
        return $"Task list has been updated with {list.Count} item(s).";
    }

    private static string View(List<TaskItem> list)
    {
        if (list.Count == 0)
            return "No task list found. Use the \"plan\" command to create one.";
        return Format(list);
    }

    private static string Format(List<TaskItem> list)
    {
        var sb = new StringBuilder("# Task List\n\n");
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            var icon = t.Status switch
            {
                "done" => "✅",
                "in_progress" => "\U0001F504",
                _ => "⏳",
            };
            sb.Append(i + 1).Append(". ").Append(icon).Append(' ').Append(t.Title).Append('\n');
            if (!string.IsNullOrEmpty(t.Notes))
                sb.Append("   ").Append(t.Notes).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static List<TaskItem> ParseTaskList(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("task_list", out var raw) || raw is null)
            return new List<TaskItem>();

        // Microsoft.Extensions.AI.OpenAI hands us a JsonElement after deserializing
        // function-call arguments. Native arrays/lists are also supported just in
        // case a different transport ever lands here.
        if (raw is JsonElement je)
        {
            if (je.ValueKind != JsonValueKind.Array) return new List<TaskItem>();
            var items = new List<TaskItem>(je.GetArrayLength());
            foreach (var el in je.EnumerateArray())
                items.Add(new TaskItem(
                    Title: ElStr(el, "title"),
                    Notes: ElStr(el, "notes"),
                    Status: ElStr(el, "status", "todo")));
            return items;
        }
        return new List<TaskItem>();
    }

    private static string ElStr(JsonElement el, string prop, string def = "")
    {
        if (el.ValueKind != JsonValueKind.Object) return def;
        if (!el.TryGetProperty(prop, out var v)) return def;
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? def) : def;
    }

    /// <summary>Test/cleanup hook — drops a session's task list.</summary>
    public static void Reset(string sessionId) => Lists.TryRemove(sessionId, out _);
}

public sealed record TaskItem(string Title, string Notes, string Status);
