using Vett.Agent;
using Vett.Llm;
using Vett.Tools;

namespace Vett.Tests;

// DeclareDone_EmitsSubmitMarker exercises declare_done with no evidence gate
// configured, so it is only correct while VETT_REQUIRED_PATHS is unset. That
// env var is process-wide: DeclareDoneEvidenceGateTests /
// DeclareDoneContentGateTests set it, and xunit runs distinct classes in
// parallel by default, so without joining their collection this class saw a
// polluted value and went red at random (observed: 1 red in 4 clean runs of
// the full suite). Serialising with the other env-mutating classes removes a
// FALSE red; it weakens no assertion.
[Collection("declare-done-env")]
public class LeaderToolsTests
{
    private readonly TaskBoard _board = new();
    private readonly Dictionary<string, ToolFn> _tools;
    private readonly FakeSandbox _sb = new();

    public LeaderToolsTests()
    {
        // runMember signature gained a leading taskId so parallel
        // dispatches can be tracked per-task. Tests just ignore it.
        _tools = LeaderTools.Create(_board, async (taskId, name, task, ct) =>
        {
            await Task.Delay(1, ct);
            return $"Member {name} completed: {task}";
        });
    }

    [Fact]
    public async Task AssignTask_Sync_ReturnsResult()
    {
        var result = await _tools["assign_task"](
            new() { ["member"] = "impl", ["task"] = "fix bug" },
            _sb, "s", default);

        Assert.Contains("impl", result);
        Assert.Contains("done", result);
        Assert.Contains("fix bug", result);

        var task = _board.All().First();
        Assert.Equal(Vett.Agent.TaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task AssignAsync_ReturnsTaskId()
    {
        var result = await _tools["assign_async"](
            new() { ["member"] = "impl", ["task"] = "fix bug" },
            _sb, "s", default);

        // Ids now read as `<member>-<n>` (was `task-N` pre-rename).
        Assert.Contains("impl-", result);
        Assert.Contains("assigned", result);

        // Wait for the background task to complete. NOT a fixed sleep: the
        // old `await Task.Delay(100)` was a guess about this machine's speed
        // and it lost that bet under full-suite parallelism (measured red
        // 1 run in 20, "Expected: Completed, Actual: Pending"). The subject
        // of this test is that assign_async EVENTUALLY completes the task,
        // never how fast it does so.
        await TestWait.UntilAsync(
            () => _board.All().FirstOrDefault()?.Status == Vett.Agent.TaskStatus.Completed,
            "assign_async's background task to reach Completed",
            diagnose: () => $"board status: {_board.All().FirstOrDefault()?.Status.ToString() ?? "(no tasks on board)"}");

        var task = _board.All().First();
        Assert.Equal(Vett.Agent.TaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task CheckTask_ReturnsTaskInfo()
    {
        // Create and complete a task first. Use the id Create returns
        // rather than hard-coding — task ids are now `<member>-<n>`.
        var t = _board.Create("impl", "fix bug");
        _board.Complete(t.Id, "fixed it");

        var result = await _tools["check_task"](
            new() { ["task_id"] = t.Id },
            _sb, "s", default);

        Assert.Contains("impl", result);
        Assert.Contains("Completed", result);
        Assert.Contains("fixed it", result);
    }

    [Fact]
    public async Task CheckTask_NotFound()
    {
        var result = await _tools["check_task"](
            new() { ["task_id"] = "nonexistent" },
            _sb, "s", default);

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task CheckTasks_ListsAll()
    {
        _board.Create("impl", "task 1");
        _board.Create("reviewer", "task 2");

        var result = await _tools["check_tasks"](
            new(), _sb, "s", default);

        Assert.Contains("impl", result);
        Assert.Contains("reviewer", result);
    }

    [Fact]
    public async Task WaitTask_BlocksUntilDone()
    {
        var task = _board.Create("impl", "fix bug");

        // Complete in background after delay.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            _board.Complete(task.Id, "done");
        });

        var result = await _tools["wait_task"](
            new() { ["task_id"] = task.Id },
            _sb, "s", default);

        Assert.Contains("Completed", result);
        Assert.Contains("done", result);
    }

    [Fact]
    public async Task DeclareDone_EmitsSubmitMarker()
    {
        var result = await _tools["declare_done"](
            new() { ["summary"] = "all fixed" },
            _sb, "s", default);

        Assert.StartsWith(Builtins.SubmitMarker, result);
        Assert.Contains("all fixed", result);
    }

    [Fact]
    public async Task AssignTask_MissingArgs_ReturnsError()
    {
        var result = await _tools["assign_task"](
            new() { ["member"] = "impl" },  // missing task
            _sb, "s", default);

        Assert.Contains("Error", result);
    }

    [Fact]
    public void SchemasAreValid()
    {
        // 9 = the original 6 (assign_task, assign_async, check_task,
        // check_tasks, wait_task, declare_done) + continue_task +
        // cancel_task + inject_into_task. Bumped each time the leader's
        // delegation toolkit grows so an accidental drop / rename
        // shows up in CI rather than at runtime.
        Assert.Equal(9, LeaderTools.Schemas.Count);
    }

    [Fact]
    public async Task WaitTask_UnknownId_ReturnsErrorInsteadOfHanging()
    {
        // Regression: previously this would hang forever waiting for a task
        // that was never created. Now WaitAsync throws, the tool catches and
        // returns a useful error so the leader can move on.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await _tools["wait_task"](
            new() { ["task_id"] = "task-nonexistent" },
            _sb, "s", cts.Token);

        Assert.Contains("Error", result);
        Assert.Contains("Unknown task id", result);
    }
}
