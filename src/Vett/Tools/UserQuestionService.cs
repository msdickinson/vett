using System.Threading.Channels;

namespace Vett.Tools;

/// <summary>
/// Coordinates the round-trip for the <c>ask_user_question</c> tool:
/// the tool emits a <c>user_question</c> event with a question_id and
/// then awaits an answer routed back via the chat-stdio reader.
///
/// One service instance per chat session. ChatCommand creates it,
/// registers it as a closure in the <c>ask_user_question</c> ToolFn,
/// and routes inbound <c>user_question_answer</c> stdin messages to
/// <see cref="PostAnswer"/>.
///
/// Single in-flight question at a time. If the agent fires another
/// question before the user answers the first, the second await
/// blocks until the first answer arrives — matches the human user's
/// expectation (they only see one question card at a time).
///
/// Cancellation: if the parent token cancels (session closed), the
/// awaiter throws OperationCanceledException and the agent loop's
/// existing handlers tear the conversation down cleanly.
/// </summary>
public sealed class UserQuestionService
{
    // ROUTED BY QUESTION_ID, not a shared queue.
    //
    // This was an unbounded Channel that every waiter read from, writing
    // back anything addressed to someone else and calling `Task.Yield()`
    // so the re-read loop wouldn't pin a thread. The yield was the right
    // instinct and still wasn't enough: Task.Yield reschedules onto the
    // pool rather than blocking, so the loop kept spinning — just spread
    // across pool threads instead of pinning one. One orphaned answer —
    // a question abandoned when the panel closed — is enough, and
    // nothing ever drains it.
    //
    // Worse, the two services written to mirror this one (see
    // PermissionService.AwaitDecisionAsync) copied the loop WITHOUT the
    // yield. With no suspension point anywhere they never returned their
    // Task at all and wedged the calling thread outright.
    //
    // Parking mismatches instead of re-queuing kills the spin but
    // deadlocks siblings — two waiters each take the other's answer out
    // of the channel and then block holding it. Routing by id has
    // neither problem. All three services now share this shape.
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _waiters = new();

    /// <summary>
    /// Answers that arrived before anyone was awaiting them. Preserves
    /// the old channel's tolerance for a UI that answers faster than the
    /// tool reaches its await, and gives orphans somewhere inert to sit.
    /// </summary>
    private readonly Dictionary<string, string> _unclaimed = new();

    /// <summary>Called from the chat-stdio reader when the webview
    /// posts a <c>user_question_answer</c>. The answer is delivered to
    /// the AskAsync call waiting on that exact question_id.</summary>
    public void PostAnswer(string questionId, string answer)
    {
        lock (_lock)
        {
            if (_waiters.TryGetValue(questionId, out var tcs) && tcs.TrySetResult(answer))
            {
                _waiters.Remove(questionId);
                return;
            }
            _unclaimed[questionId] = answer;
        }
    }

    /// <summary>Block until an answer with the matching question_id
    /// arrives. Answers for other questions are left for their own
    /// waiter. Caller is the ask_user_question ToolFn.</summary>
    public async Task<string> AwaitAnswerAsync(string questionId, CancellationToken ct)
    {
        TaskCompletionSource<string> tcs;
        lock (_lock)
        {
            if (_unclaimed.Remove(questionId, out var alreadyAnswered)) return alreadyAnswered;
            if (!_waiters.TryGetValue(questionId, out tcs!))
            {
                tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[questionId] = tcs;
            }
        }

        using var registration = ct.Register(static s =>
            ((TaskCompletionSource<string>)s!).TrySetCanceled(), tcs);
        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (_lock) { _waiters.Remove(questionId); }
        }
    }
}
