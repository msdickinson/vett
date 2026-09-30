using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// Failure mode #8 — single-turn duplicate fan-out. Observed live: a leader
/// emitted 102 identical `file_editor view Lib/Calc.cs` calls inside ONE
/// assistant turn. The "never repeat a completed action" prompt rule reasons
/// from results already in context, so it only constrains ACROSS turns and
/// structurally cannot reach this. AgentLoop collapses the duplicates.
/// </summary>
public class TurnDedupeTests
{
    private static FunctionCallContent Call(string id, string name, params (string K, object? V)[] args)
        => new(id, name, args.ToDictionary(a => a.K, a => a.V));

    [Fact]
    public void IdenticalCalls_CollapseToOne_DuplicatesMapToWinner()
    {
        var calls = new List<FunctionCallContent>
        {
            Call("c1", "file_editor", ("command", "view"), ("path", "Lib/Calc.cs")),
            Call("c2", "file_editor", ("command", "view"), ("path", "Lib/Calc.cs")),
            Call("c3", "file_editor", ("command", "view"), ("path", "Lib/Calc.cs")),
        };

        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);

        Assert.Single(execute);
        Assert.Equal("c1", execute[0].CallId);
        Assert.Equal(2, dups.Count);
        Assert.All(dups, d => Assert.Equal("c1", d.WinnerId));
        Assert.Equal(new[] { "c2", "c3" }, dups.Select(d => d.DupId).ToArray());
    }

    [Fact]
    public void TheRealFanout_102IdenticalViews_CollapseToOneExecution()
    {
        var calls = Enumerable.Range(0, 102)
            .Select(i => Call($"c{i}", "file_editor", ("command", "view"), ("path", "Lib/Calc.cs")))
            .ToList();

        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);

        Assert.Single(execute);
        Assert.Equal(101, dups.Count);
    }

    [Fact]
    public void DifferentArgs_AreNotCollapsed()
    {
        var calls = new List<FunctionCallContent>
        {
            Call("c1", "file_editor", ("command", "view"), ("path", "A.cs")),
            Call("c2", "file_editor", ("command", "view"), ("path", "B.cs")),
            Call("c3", "file_editor", ("command", "str_replace"), ("path", "A.cs")),
        };

        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);

        Assert.Equal(3, execute.Count);
        Assert.Empty(dups);
    }

    [Fact]
    public void DifferentTool_SameArgs_IsNotCollapsed()
    {
        var calls = new List<FunctionCallContent>
        {
            Call("c1", "terminal", ("command", "ls")),
            Call("c2", "bash", ("command", "ls")),
        };

        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);

        Assert.Equal(2, execute.Count);
        Assert.Empty(dups);
    }

    [Fact]
    public void ArgumentOrder_DoesNotDefeatMatching()
    {
        // Providers may serialize JSON keys in different orders across calls.
        var calls = new List<FunctionCallContent>
        {
            Call("c1", "file_editor", ("command", "view"), ("path", "A.cs")),
            Call("c2", "file_editor", ("path", "A.cs"), ("command", "view")),
        };

        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);

        Assert.Single(execute);
        Assert.Single(dups);
        Assert.Equal("c1", dups[0].WinnerId);
    }

    [Fact]
    public void KeyIsUnambiguous_AcrossFieldBoundaries()
    {
        // Without length-prefixing, {a:"b"} and {ab:""} would produce the
        // same concatenated key and collapse two DIFFERENT calls into one.
        var calls = new List<FunctionCallContent>
        {
            Call("c1", "t", ("a", "b")),
            Call("c2", "t", ("ab", "")),
        };

        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);

        Assert.Equal(2, execute.Count);
        Assert.Empty(dups);
    }

    [Fact]
    public void NullArgValue_DoesNotCollideWithLiteralNullText()
    {
        var calls = new List<FunctionCallContent>
        {
            Call("c1", "t", ("x", null)),
            Call("c2", "t", ("x", "(null)")),
        };

        var (execute, _) = AgentLoop.CollapseDuplicateToolCalls(calls);

        // Both execute: a genuine null must not alias the literal string.
        // (Length-prefixing makes "(null)" length 6 vs the null sentinel.)
        Assert.Equal(2, execute.Count);
    }

    [Fact]
    public void EmptyCallId_IsNeverCollapsed()
    {
        // Nothing to replay a result onto, so pass them through untouched.
        var calls = new List<FunctionCallContent>
        {
            Call("", "file_editor", ("command", "view"), ("path", "A.cs")),
            Call("", "file_editor", ("command", "view"), ("path", "A.cs")),
        };

        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);

        Assert.Equal(2, execute.Count);
        Assert.Empty(dups);
    }

    [Fact]
    public void SingleCall_IsUnchanged()
    {
        var calls = new List<FunctionCallContent> { Call("c1", "terminal", ("command", "ls")) };
        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);
        Assert.Single(execute);
        Assert.Empty(dups);
    }

    [Fact]
    public void OriginalOrder_OfSurvivingCalls_IsPreserved()
    {
        var calls = new List<FunctionCallContent>
        {
            Call("c1", "t", ("k", "a")),
            Call("c2", "t", ("k", "b")),
            Call("c3", "t", ("k", "a")), // dup of c1
            Call("c4", "t", ("k", "c")),
        };

        var (execute, dups) = AgentLoop.CollapseDuplicateToolCalls(calls);

        Assert.Equal(new[] { "c1", "c2", "c4" }, execute.Select(c => c.CallId).ToArray());
        Assert.Single(dups);
        Assert.Equal(("c3", "c1"), (dups[0].DupId, dups[0].WinnerId));
    }

    [Fact]
    public void TurnDedupe_DefaultsToEnabled()
    {
        Assert.True(new Profile().TurnDedupe);
    }
}
