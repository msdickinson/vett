namespace Vett;

/// <summary>A plugin failed to start or crashed during execution.</summary>
public sealed class PluginException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>A tool was referenced but not found in the tool map.</summary>
public sealed class ToolNotFoundException(string toolName)
    : Exception($"Tool \"{toolName}\" not registered");

/// <summary>The LLM returned an error, empty response, or timed out.</summary>
public sealed class LlmException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The sandbox (sidecar or DirectBash) failed.</summary>
public sealed class SandboxException(string message, Exception? inner = null)
    : Exception(message, inner);
