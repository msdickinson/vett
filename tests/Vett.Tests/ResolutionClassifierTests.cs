using Vett.Runner;

namespace Vett.Tests;

/// <summary>
/// Unit tests for the 5-bucket resolution classifier.
/// One test per bucket verifying precedence order and edge cases.
/// </summary>
public class ResolutionClassifierTests
{
    // --- bucket: NOT_MODELABLE ---

    [Fact]
    public void NotModelable_WhenGenesisAbstainReasonSet()
    {
        var r = new InstanceResult
        {
            EndReason = "finish_tool",
            TestsPassed = true,         // would be PASS if NOT_MODELABLE didn't win
            GenesisAbstainReason = "complex_predicate",
        };
        Assert.Equal(ResolutionClassifier.NotModelable, ResolutionClassifier.Classify(r));
    }

    [Fact]
    public void NotModelable_BeatsTimeout()
    {
        var r = new InstanceResult
        {
            EndReason = "max_iterations",
            GenesisAbstainReason = "no_return_exits",
        };
        Assert.Equal(ResolutionClassifier.NotModelable, ResolutionClassifier.Classify(r));
    }

    // --- bucket: TIMEOUT ---

    [Theory]
    [InlineData("max_iterations")]
    [InlineData("wall_time")]
    [InlineData("crash")]
    [InlineData("kill")]
    [InlineData("aeon_down")]
    [InlineData("oom")]
    public void Timeout_WhenEndReasonIsTimeoutSet(string endReason)
    {
        var r = new InstanceResult { EndReason = endReason };
        Assert.Equal(ResolutionClassifier.Timeout, ResolutionClassifier.Classify(r));
    }

    [Fact]
    public void Timeout_BeatsAbstain()
    {
        var r = new InstanceResult
        {
            EndReason = "max_iterations",
            LastAssistantMessage = "I cannot solve this problem.",
        };
        Assert.Equal(ResolutionClassifier.Timeout, ResolutionClassifier.Classify(r));
    }

    // --- bucket: ABSTAIN ---

    [Theory]
    [InlineData("I can't solve this.")]
    [InlineData("I cannot complete this task.")]
    [InlineData("I am unable to fix this.")]
    [InlineData("giving up on this one")]
    [InlineData("need human intervention")]
    [InlineData("cannot solve this")]
    public void Abstain_WhenLastMessageMatchesRegex(string message)
    {
        var r = new InstanceResult { EndReason = "finish_tool", LastAssistantMessage = message };
        Assert.Equal(ResolutionClassifier.Abstain, ResolutionClassifier.Classify(r));
    }

    [Fact]
    public void Abstain_BeatsPass()
    {
        var r = new InstanceResult
        {
            EndReason = "finish_tool",
            TestsPassed = true,
            LastAssistantMessage = "I cannot solve this problem.",
        };
        Assert.Equal(ResolutionClassifier.Abstain, ResolutionClassifier.Classify(r));
    }

    [Fact]
    public void Abstain_RequiresMatchAtStart()
    {
        // The phrase "cannot" buried in the middle of a confident message must NOT trigger ABSTAIN.
        var r = new InstanceResult
        {
            EndReason = "finish_tool",
            TestsPassed = false,
            LastAssistantMessage = "Fixed the bug — it cannot happen now because I patched the condition.",
        };
        Assert.Equal(ResolutionClassifier.FalseConfidence, ResolutionClassifier.Classify(r));
    }

    // --- bucket: PASS ---

    [Fact]
    public void Pass_WhenTestsPassedTrue()
    {
        var r = new InstanceResult
        {
            EndReason = "finish_tool",
            FinishToolCalled = true,
            TestsPassed = true,
            LastAssistantMessage = "All tests pass.",
        };
        Assert.Equal(ResolutionClassifier.Pass, ResolutionClassifier.Classify(r));
    }

    // --- bucket: FALSE_CONFIDENCE ---

    [Fact]
    public void FalseConfidence_WhenFinishCalledButTestsFailed()
    {
        var r = new InstanceResult
        {
            EndReason = "finish_tool",
            FinishToolCalled = true,
            TestsPassed = false,
            LastAssistantMessage = "I have fixed all the issues.",
        };
        Assert.Equal(ResolutionClassifier.FalseConfidence, ResolutionClassifier.Classify(r));
    }

    [Fact]
    public void FalseConfidence_WhenTestsPassedNull()
    {
        // Eval not yet joined → TestsPassed=null → treat as not-passed → FALSE_CONFIDENCE.
        var r = new InstanceResult
        {
            EndReason = "finish_tool",
            FinishToolCalled = true,
            TestsPassed = null,
        };
        Assert.Equal(ResolutionClassifier.FalseConfidence, ResolutionClassifier.Classify(r));
    }

    // --- ClassifyAll mutates Resolution ---

    [Fact]
    public void ClassifyAll_SetsResolutionOnEachInstance()
    {
        var instances = new List<InstanceResult>
        {
            new() { EndReason = "finish_tool", TestsPassed = true },
            new() { EndReason = "max_iterations" },
            new() { GenesisAbstainReason = "complex_predicate" },
            new() { EndReason = "finish_tool", LastAssistantMessage = "I cannot solve this." },
            new() { EndReason = "finish_tool", FinishToolCalled = true, TestsPassed = false },
        };

        ResolutionClassifier.ClassifyAll(instances);

        Assert.Equal(ResolutionClassifier.Pass,            instances[0].Resolution);
        Assert.Equal(ResolutionClassifier.Timeout,         instances[1].Resolution);
        Assert.Equal(ResolutionClassifier.NotModelable,    instances[2].Resolution);
        Assert.Equal(ResolutionClassifier.Abstain,         instances[3].Resolution);
        Assert.Equal(ResolutionClassifier.FalseConfidence, instances[4].Resolution);
    }
}
