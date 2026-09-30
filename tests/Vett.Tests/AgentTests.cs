using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Llm;

namespace Vett.Tests;

public class TaskBoardTests
{
    [Fact]
    public void Create_AssignsId()
    {
        var board = new TaskBoard();
        var task = board.Create("impl", "fix the bug");

        // Per-member counter: ids read as `<member>-<n>` (was `task-N`
        // pre-rename, kept distinct so parallel dispatches to the same
        // member self-identify in chat — researcher-1 vs researcher-2).
        Assert.Equal("impl-1", task.Id);
        Assert.Equal("impl", task.Member);
        Assert.Equal(Vett.Agent.TaskStatus.Pending, task.Status);
    }

    [Fact]
    public void Complete_SetsResult()
    {
        var board = new TaskBoard();
        var task = board.Create("impl", "fix the bug");

        board.MarkRunning(task.Id);
        board.Complete(task.Id, "done");

        var t = board.Get(task.Id);
        Assert.NotNull(t);
        Assert.Equal(Vett.Agent.TaskStatus.Completed, t!.Status);
        Assert.Equal("done", t.Result);
    }

    [Fact]
    public void Fail_SetsError()
    {
        var board = new TaskBoard();
        var task = board.Create("impl", "fix the bug");

        board.Fail(task.Id, "crashed");

        var t = board.Get(task.Id);
        Assert.NotNull(t);
        Assert.Equal(Vett.Agent.TaskStatus.Failed, t!.Status);
        Assert.Equal("crashed", t.Error);
    }

    [Fact]
    public async Task WaitAsync_CompletesOnComplete()
    {
        var board = new TaskBoard();
        var task = board.Create("impl", "fix the bug");

        var waitTask = board.WaitAsync(task.Id, CancellationToken.None);

        Assert.False(waitTask.IsCompleted);

        board.Complete(task.Id, "done");

        await waitTask; // Should complete immediately.
        Assert.True(waitTask.IsCompleted);
    }

    [Fact]
    public void All_ReturnsAllTasks()
    {
        var board = new TaskBoard();
        board.Create("impl", "task 1");
        board.Create("reviewer", "task 2");

        Assert.Equal(2, board.All().Count);
    }

    [Fact]
    public void Format_ShowsTaskInfo()
    {
        var board = new TaskBoard();
        var task = board.Create("impl", "fix the bug");
        board.Complete(task.Id, "fixed it");

        var formatted = TaskBoard.Format(board.Get(task.Id)!);

        Assert.Contains("impl", formatted);
        Assert.Contains("Completed", formatted);
        Assert.Contains("fixed it", formatted);
    }
}

public class ChatMessageHelperTests
{
    [Fact]
    public void SystemMessage()
    {
        var msg = Chat.System("You are helpful.");
        Assert.Equal(ChatRole.System, msg.Role);
        Assert.Equal("You are helpful.", msg.GetText());
    }

    [Fact]
    public void UserMessage()
    {
        var msg = Chat.User("hello");
        Assert.Equal(ChatRole.User, msg.Role);
        Assert.Equal("hello", msg.GetText());
    }

    [Fact]
    public void AssistantMessage()
    {
        var msg = Chat.Assistant("sure");
        Assert.Equal(ChatRole.Assistant, msg.Role);
        Assert.False(msg.HasToolCalls());
    }

    [Fact]
    public void ToolResultMessage()
    {
        var msg = Chat.ToolResult("call-1", "result text");
        Assert.Equal(ChatRole.Tool, msg.Role);
    }

    [Fact]
    public void AssistantWithCalls()
    {
        var calls = new List<FunctionCallContent>
        {
            new("call-1", "terminal", new Dictionary<string, object?> { ["command"] = "ls" }),
        };
        var msg = Chat.AssistantWithCalls("I'll run that.", calls);

        Assert.Equal(ChatRole.Assistant, msg.Role);
        Assert.True(msg.HasToolCalls());
        Assert.Single(msg.GetToolCalls());
        Assert.Equal("terminal", msg.GetToolCalls()[0].Name);
    }

    [Fact]
    public void IsRole_Works()
    {
        var msg = Chat.User("test");
        Assert.True(msg.IsRole(ChatRole.User));
        Assert.False(msg.IsRole(ChatRole.System));
    }
}

public class ChatClientFactoryTests
{
    [Fact]
    public void CreateLocal_ReturnsClient()
    {
        var config = new Config.LlmConfig
        {
            Provider = "local",
            Endpoint = "http://localhost:8000/v1",
            Model = "test-model",
        };

        var client = ChatClientFactory.Create(config);
        Assert.NotNull(client);
    }

    [Fact]
    public void CreateLocal_ThrowsWithoutEndpoint()
    {
        var config = new Config.LlmConfig
        {
            Provider = "local",
            Model = "test",
        };

        Assert.Throws<VettException>(() => ChatClientFactory.Create(config));
    }

    [Fact]
    public void MergeConfig_OverridesValues()
    {
        var baseConfig = new Config.LlmConfig
        {
            Provider = "local",
            Endpoint = "http://localhost:8000/v1",
            Model = "base-model",
            Temperature = 1.0,
        };

        var memberOverride = new Config.LlmConfig
        {
            Provider = "openai",
            Model = "gpt-4o",
            Temperature = 0.3,
        };

        var merged = ChatClientFactory.Merge(baseConfig, memberOverride);

        Assert.Equal("openai", merged.Provider);
        Assert.Equal("gpt-4o", merged.Model);
        Assert.Equal(0.3, merged.Temperature);
        Assert.Equal("http://localhost:8000/v1", merged.Endpoint); // Inherited
    }

    [Fact]
    public void MergeConfig_NullOverride_ReturnsBase()
    {
        var baseConfig = new Config.LlmConfig { Provider = "local", Model = "base" };

        var merged = ChatClientFactory.Merge(baseConfig, null);

        Assert.Equal("local", merged.Provider);
        Assert.Equal("base", merged.Model);
    }

    [Fact]
    public void MergeConfig_ExplicitDefaultValuesAreNotDropped()
    {
        // Regression: previous sentinel-based merge silently dropped a member's
        // explicit Temperature=1.0 because the check was `!= 1.0`. With nullable
        // doubles, "set to 1.0" is distinct from "unset (null)".
        var baseConfig = new Config.LlmConfig { Provider = "local", Temperature = 0.3, TopP = 0.5 };
        var memberOverride = new Config.LlmConfig { Temperature = 1.0, TopP = 0.95 };

        var merged = ChatClientFactory.Merge(baseConfig, memberOverride);

        Assert.Equal(1.0, merged.Temperature);
        Assert.Equal(0.95, merged.TopP);
    }

    [Fact]
    public void MergeConfig_UnsetMemberFieldsInheritFromBase()
    {
        var baseConfig = new Config.LlmConfig { Provider = "local", Temperature = 0.3, TopP = 0.5 };
        var memberOverride = new Config.LlmConfig { Model = "gpt-4o" };

        var merged = ChatClientFactory.Merge(baseConfig, memberOverride);

        Assert.Equal(0.3, merged.Temperature);
        Assert.Equal(0.5, merged.TopP);
        Assert.Equal("gpt-4o", merged.Model);
    }
}
