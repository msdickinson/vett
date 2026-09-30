using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Vett.Llm;

/// <summary>
/// Schema-only tool declaration. The agent loop dispatches tool calls itself,
/// so the LLM only needs the declarations — invocation never goes through MEAI.
/// InvokeCoreAsync throws because nothing in this codebase wires
/// .UseFunctionInvocation(); if that ever changes, the dispatch path here
/// must also be implemented.
/// </summary>
public sealed class RawToolDeclaration : AIFunction
{
    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }

    private RawToolDeclaration(string name, string description, JsonElement parameters)
    {
        Name = name;
        Description = description;
        JsonSchema = parameters;
    }

    /// <summary>
    /// Build a declaration from one of our on-disk schemas. Accepts both the
    /// OpenAI tool wrapper {"type":"function","function":{name,description,parameters}}
    /// and the bare {name,description,parameters} shape — plugins emit either.
    /// </summary>
    public static RawToolDeclaration From(JsonElement raw)
    {
        var inner = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("function", out var fn)
            ? fn
            : raw;

        var name = inner.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? ""
            : "";

        var description = inner.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? ""
            : "";

        // parameters MUST be a JSON object — vLLM rejects a missing/non-object
        // schema. Fall back to an empty object schema if absent rather than
        // sending null and getting an opaque 400.
        JsonElement parameters;
        if (inner.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
            parameters = p.Clone();
        else
            parameters = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        return new RawToolDeclaration(name, description, parameters);
    }

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"Tool '{Name}' is dispatched by the agent loop, not invoked through MEAI.");
}
