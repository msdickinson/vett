using System.Net;
using Microsoft.Extensions.AI;

namespace Vett.Llm;

/// <summary>
/// IChatClient decorator that fails over across an ordered list of inner
/// clients (primary + fallbacks). V1 harness edit — the load-bearing
/// resilience piece.
///
/// Layering (top to bottom):
///   AgentLoop LlmRetryCap (last-resort, 3 attempts over 2s/6s)
///     └─ FailoverChatClient   ← THIS: switch ENDPOINT when one is down
///          └─ per-endpoint OpenAIClient + RequestRetryPolicy (fast
///             same-endpoint retries for transient blips)
///
/// Behavior:
///   - Tries the current endpoint. On a failover-worthy failure it advances
///     to the next endpoint and retries the SAME request there.
///   - "Sticky": once an endpoint succeeds, subsequent calls start from it,
///     so a dead primary isn't re-hammered every turn. (Primary is not
///     re-probed automatically — acceptable for long runs; revisit in V6.)
///   - User cancellation (ct) and HTTP 400 (bad request / context overflow —
///     a fallback would fail identically) are NOT failed over; they surface
///     immediately.
///   - If every endpoint fails, the LAST exception is rethrown so the caller
///     (and AgentLoop's llm_error path) sees a real error, not a wrapped one.
///
/// With a single endpoint (no fallbacks) callers get the bare inner client
/// instead of this wrapper (see ChatClientFactory.Create), so this type only
/// exists when failover is actually configured.
/// </summary>
public sealed class FailoverChatClient : IChatClient
{
    private readonly IReadOnlyList<(IChatClient Client, string Label)> _endpoints;
    private readonly Action<int, int, string>? _onFailover;
    private int _current;

    /// <param name="endpoints">Ordered primary-first list. Must be non-empty.</param>
    /// <param name="onFailover">
    /// Optional observability hook: (fromIndex, toIndex, reason). Defaults to
    /// a stderr line so failover is visible in worker/stdio logs without
    /// coupling to the event stream.
    /// </param>
    public FailoverChatClient(
        IReadOnlyList<(IChatClient Client, string Label)> endpoints,
        Action<int, int, string>? onFailover = null)
    {
        if (endpoints is null || endpoints.Count == 0)
            throw new ArgumentException("FailoverChatClient requires at least one endpoint", nameof(endpoints));
        _endpoints = endpoints;
        _onFailover = onFailover ?? DefaultOnFailover;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        // Materialize once — the same request is replayed against each endpoint.
        var msgList = messages as IList<ChatMessage> ?? messages.ToList();

        Exception? last = null;
        int start = _current;
        for (int hop = 0; hop < _endpoints.Count; hop++)
        {
            int idx = (start + hop) % _endpoints.Count;
            try
            {
                var resp = await _endpoints[idx].Client.GetResponseAsync(msgList, options, ct);
                _current = idx; // stick to the endpoint that just worked
                return resp;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user/harness cancellation — never fail over
            }
            catch (Exception ex) when (!ShouldFailover(ex))
            {
                throw; // e.g. HTTP 400 context overflow — a fallback won't help
            }
            catch (Exception ex)
            {
                last = ex;
                int next = (start + hop + 1) % _endpoints.Count;
                bool hasMore = hop + 1 < _endpoints.Count;
                if (hasMore)
                    _onFailover?.Invoke(idx, next, Describe(ex));
            }
        }

        // Every endpoint failed — surface the last real error.
        throw last ?? new LlmException("All failover endpoints failed with no captured exception");
    }

    /// <summary>
    /// Decide whether an exception warrants trying the next endpoint.
    /// Fails over on availability/transport problems and on 402 (budget —
    /// a free local fallback may succeed). Does NOT fail over on 400
    /// (bad request / context overflow, identical on any endpoint).
    /// </summary>
    private static bool ShouldFailover(Exception ex)
    {
        var status = HttpStatusOf(ex);
        if (status == HttpStatusCode.BadRequest)
            return false;
        // Connection-level failures (status == null), timeouts, 429, 5xx,
        // 401/402/403 all get a shot at the next endpoint.
        return true;
    }

    private static HttpStatusCode? HttpStatusOf(Exception ex) => ex switch
    {
        HttpRequestException hre => hre.StatusCode,
        System.ClientModel.ClientResultException cre => (HttpStatusCode)cre.Status,
        _ => null,
    };

    private static string Describe(Exception ex)
    {
        var status = HttpStatusOf(ex);
        var code = status.HasValue ? $"HTTP {(int)status.Value}" : ex.GetType().Name;
        return $"{code}: {ex.Message}";
    }

    private static void DefaultOnFailover(int from, int to, string reason)
        => Console.Error.WriteLine($"[failover] endpoint #{from} -> #{to} ({reason})");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
        // Streaming is unused by VETT; delegate to the current endpoint without
        // failover rather than pretend to support it.
        => _endpoints[_current].Client.GetStreamingResponseAsync(messages, options, ct);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _endpoints[_current].Client.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        foreach (var (client, _) in _endpoints)
            client.Dispose();
    }
}
