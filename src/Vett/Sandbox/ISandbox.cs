namespace Vett.Sandbox;

/// <summary>
/// The one interface in the codebase. Exists because tools need to work
/// with both RpcClient (sidecar in Docker) and DirectBash (local, no sidecar).
/// </summary>
public interface ISandbox
{
    /// <summary>
    /// The sandbox's current working directory. Used by the agent loop to
    /// substitute the <c>{working_dir}</c> placeholder in tool schema
    /// descriptions (e.g. file_editor's "Your current working directory
    /// is: {working_dir}" line) so the model knows where it actually is.
    /// May be an empty string for sandboxes that don't track cwd locally
    /// (e.g. RpcClient — it sets cwd per-session in the sidecar but
    /// doesn't keep a copy on the client side).
    /// </summary>
    string Cwd { get; }

    Task<BashResult> BashExecAsync(string sessionId, string command, int timeoutSec = 60, CancellationToken ct = default);
    Task<string> FileViewAsync(string sessionId, string path, CancellationToken ct = default);
    Task<string> FileCreateAsync(string sessionId, string path, string fileText, CancellationToken ct = default);
    Task<(string Content, string? Error)> FileStrReplaceAsync(string sessionId, string path, string oldStr, string newStr, CancellationToken ct = default);
    Task<string> FileInsertAsync(string sessionId, string path, int insertLine, string newStr, CancellationToken ct = default);
    Task<string> FileUndoAsync(string sessionId, string path, CancellationToken ct = default);
    Task SessionCreateAsync(string name, string cwd, CancellationToken ct = default);
    Task SessionDestroyAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Return a sandbox view rooted at <paramref name="newCwd"/>. Used by
    /// the team coordinator to give each dispatched member its own
    /// isolated working directory (a per-task git worktree). The original
    /// sandbox is unaffected; the returned view shares any underlying
    /// resources but routes file/bash ops through <paramref name="newCwd"/>.
    /// Implementations MAY throw <see cref="NotSupportedException"/> when
    /// per-task cwd isolation isn't possible (e.g. Docker sidecar mode);
    /// callers should fall back to direct-mode dispatch in that case.
    /// </summary>
    ISandbox WithCwd(string newCwd);

    /// <summary>
    /// Like <see cref="WithCwd"/>, but additionally establishes a parent
    /// workspace boundary. Absolute paths under <paramref name="workspaceRoot"/>
    /// are transparently rewritten to equivalent paths under
    /// <paramref name="newCwd"/>. This closes the isolation hole where an
    /// agent given an absolute path to the parent workspace would otherwise
    /// write directly into it, defeating the per-dispatch worktree's purpose.
    /// Paths outside both <paramref name="workspaceRoot"/> and
    /// <paramref name="newCwd"/> (e.g. <c>/tmp</c>, system paths) pass through
    /// unchanged — bounding scratch space isn't isolation's job.
    /// Same NotSupportedException contract as <see cref="WithCwd"/>.
    /// </summary>
    ISandbox WithDispatchWorktree(string newCwd, string workspaceRoot);
}
