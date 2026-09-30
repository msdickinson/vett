using System.Text.Json;
using Vett.Config;
using Vett.Plugin;

namespace Vett.Tests;

/// <summary>
/// Covers the lifecycle-hook runner (#20). Pure-helper tests
/// (ParseDecision / EventName) need no I/O. End-to-end tests against
/// real shell scripts use OS-appropriate one-liners (bash on
/// Linux/macOS, cmd on Windows) to avoid hard dependencies on python
/// / node / etc.
/// </summary>
public class LifecycleHooksTests
{
    [Fact]
    public void ParseDecision_empty_input_falls_back_to_allow()
    {
        Assert.Equal("allow", LifecycleHooks.ParseDecision("").Action);
        Assert.Equal("allow", LifecycleHooks.ParseDecision("   \n  ").Action);
    }

    [Fact]
    public void ParseDecision_malformed_json_falls_back_to_allow()
    {
        Assert.Equal("allow", LifecycleHooks.ParseDecision("{ not json").Action);
    }

    [Fact]
    public void ParseDecision_non_object_root_falls_back_to_allow()
    {
        Assert.Equal("allow", LifecycleHooks.ParseDecision("[1,2,3]").Action);
        Assert.Equal("allow", LifecycleHooks.ParseDecision("\"just a string\"").Action);
    }

    [Fact]
    public void ParseDecision_explicit_allow()
    {
        var d = LifecycleHooks.ParseDecision("""{"action":"allow"}""");
        Assert.Equal("allow", d.Action);
        Assert.Null(d.Reason);
        Assert.Null(d.Modified);
    }

    [Fact]
    public void ParseDecision_unknown_action_falls_back_to_allow()
    {
        var d = LifecycleHooks.ParseDecision("""{"action":"sneeze"}""");
        Assert.Equal("allow", d.Action);
    }

    [Fact]
    public void ParseDecision_deny_with_reason()
    {
        var d = LifecycleHooks.ParseDecision("""{"action":"deny","reason":"sensitive command"}""");
        Assert.Equal("deny", d.Action);
        Assert.Equal("sensitive command", d.Reason);
    }

    [Fact]
    public void ParseDecision_deny_without_reason_uses_generic_stand_in()
    {
        var d = LifecycleHooks.ParseDecision("""{"action":"deny"}""");
        Assert.Equal("deny", d.Action);
        Assert.Equal("denied by hook", d.Reason);
    }

    [Fact]
    public void ParseDecision_modify_with_modified_object()
    {
        var d = LifecycleHooks.ParseDecision("""{"action":"modify","modified":{"text":"clean"}}""");
        Assert.Equal("modify", d.Action);
        Assert.True(d.Modified.HasValue);
        Assert.Equal("clean", d.Modified!.Value.GetProperty("text").GetString());
    }

    [Fact]
    public void ParseDecision_modify_without_modified_falls_back_to_allow()
    {
        var d = LifecycleHooks.ParseDecision("""{"action":"modify"}""");
        Assert.Equal("allow", d.Action);
    }

    [Fact]
    public void ParseDecision_modify_with_non_object_modified_falls_back_to_allow()
    {
        var d = LifecycleHooks.ParseDecision("""{"action":"modify","modified":"not an object"}""");
        Assert.Equal("allow", d.Action);
    }

    [Fact]
    public void ParseDecision_action_is_case_insensitive()
    {
        Assert.Equal("deny", LifecycleHooks.ParseDecision("""{"action":"DENY","reason":"x"}""").Action);
        Assert.Equal("modify", LifecycleHooks.ParseDecision("""{"action":"Modify","modified":{"a":1}}""").Action);
    }

    [Fact]
    public void EventName_round_trips_every_event()
    {
        Assert.Equal("pre_tool_use", LifecycleHooks.EventName(LifecycleHookEvent.PreToolUse));
        Assert.Equal("post_tool_use", LifecycleHooks.EventName(LifecycleHookEvent.PostToolUse));
        Assert.Equal("user_prompt_submit", LifecycleHooks.EventName(LifecycleHookEvent.UserPromptSubmit));
        Assert.Equal("session_start", LifecycleHooks.EventName(LifecycleHookEvent.SessionStart));
        Assert.Equal("session_end", LifecycleHooks.EventName(LifecycleHookEvent.SessionEnd));
        Assert.Equal("stop", LifecycleHooks.EventName(LifecycleHookEvent.Stop));
    }

    [Fact]
    public async Task RunAsync_with_no_hooks_returns_allow()
    {
        var profile = new Profile();
        var payload = JsonSerializer.SerializeToElement(new { foo = "bar" });
        var d = await LifecycleHooks.RunAsync(profile, LifecycleHookEvent.PreToolUse, payload, default);
        Assert.Equal("allow", d.Action);
    }

    [Fact]
    public async Task RunAsync_with_empty_event_list_returns_allow()
    {
        var profile = new Profile { Hooks = new HooksConfig { PreToolUse = new() } };
        var payload = JsonSerializer.SerializeToElement(new { foo = "bar" });
        var d = await LifecycleHooks.RunAsync(profile, LifecycleHookEvent.PreToolUse, payload, default);
        Assert.Equal("allow", d.Action);
    }

    [Fact]
    public async Task RunOneAsync_allow_via_echo()
    {
        // Cross-platform "print {action: allow}" — uses cmd's echo on
        // Windows and printf on POSIX. The shell wrapper in
        // LifecycleHooks runs `cmd /c <cmd>` or `/bin/bash -c <cmd>`.
        var hook = new LifecycleHookConfig
        {
            Command = OperatingSystem.IsWindows()
                ? "echo {\"action\":\"allow\"}"
                : "printf '%s' '{\"action\":\"allow\"}'",
        };
        var payload = JsonSerializer.SerializeToElement(new { x = 1 });
        var d = await LifecycleHooks.RunOneAsync(hook, LifecycleHookEvent.PreToolUse, payload, default);
        Assert.Equal("allow", d.Action);
    }

    [Fact]
    public async Task RunOneAsync_deny_round_trips_reason()
    {
        var hook = new LifecycleHookConfig
        {
            Command = OperatingSystem.IsWindows()
                ? "echo {\"action\":\"deny\",\"reason\":\"nope\"}"
                : "printf '%s' '{\"action\":\"deny\",\"reason\":\"nope\"}'",
        };
        var payload = JsonSerializer.SerializeToElement(new { x = 1 });
        var d = await LifecycleHooks.RunOneAsync(hook, LifecycleHookEvent.PreToolUse, payload, default);
        Assert.Equal("deny", d.Action);
        Assert.Equal("nope", d.Reason);
    }

    [Fact]
    public async Task RunOneAsync_silent_hook_returns_allow()
    {
        // Hook that exits silently (no stdout) — runner should
        // fail-open with allow.
        var hook = new LifecycleHookConfig
        {
            Command = OperatingSystem.IsWindows() ? "rem noop" : ":",
        };
        var payload = JsonSerializer.SerializeToElement(new { x = 1 });
        var d = await LifecycleHooks.RunOneAsync(hook, LifecycleHookEvent.PreToolUse, payload, default);
        Assert.Equal("allow", d.Action);
    }

    [Fact]
    public async Task RunOneAsync_timeout_falls_back_to_allow()
    {
        // Hook that sleeps far longer than its timeout, then writes a
        // sentinel as its LAST act. Timeout should kill it and fall open.
        //
        // The sentinel — not the clock — is what discriminates here. If
        // the timeout never fired, WaitForExitAsync would return only
        // after the child ran to completion, i.e. after the write, so the
        // sentinel would exist. Its absence therefore means we did NOT
        // wait for natural completion, and that holds no matter how
        // loaded the box is.
        //
        // "allow" alone proves nothing: ping's output isn't JSON, so
        // ParseDecision falls open to allow on BOTH paths. Something has
        // to separate them.
        var sentinel = Path.Combine(Path.GetTempPath(), "vett-hook-timeout-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hook = new LifecycleHookConfig
            {
                Command = OperatingSystem.IsWindows()
                    ? $"ping 127.0.0.1 -n 30 > nul & type nul > \"{sentinel}\""  // ~29 seconds
                    : $"sleep 30; touch '{sentinel}'",
                TimeoutSeconds = 1,
            };
            var payload = JsonSerializer.SerializeToElement(new { x = 1 });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var d = await LifecycleHooks.RunOneAsync(hook, LifecycleHookEvent.PreToolUse, payload, default);
            sw.Stop();
            Assert.Equal("allow", d.Action);
            Assert.False(File.Exists(sentinel),
                $"Hook ran to completion — the {hook.TimeoutSeconds}s timeout never fired (took {sw.ElapsedMilliseconds}ms)");
            // Secondary, and deliberately loose. The previous form asserted
            // <3500ms against a ~4000ms natural completion: a 12% margin on
            // a wall clock, which flaked 2 of 10 full-suite runs. The gap is
            // now ~1s vs ~29s, so this can only fire on a real regression.
            Assert.True(sw.ElapsedMilliseconds < 15000, $"Should have timed out around 1s, took {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            try { File.Delete(sentinel); } catch { }
        }
    }

    [Fact]
    public async Task RunOneAsync_within_timeout_DOES_write_the_sentinel()
    {
        // Positive control for the test above, and its only job is that.
        //
        // `Assert.False(File.Exists(sentinel))` is free if the command
        // shape can't write a sentinel at all — a typo in the cmd/bash
        // one-liner would make the timeout test green forever, for
        // entirely the wrong reason. Same shape, same runner, ample
        // timeout: the write must land, or the absence above proves
        // nothing about the timeout.
        var sentinel = Path.Combine(Path.GetTempPath(), "vett-hook-poscontrol-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hook = new LifecycleHookConfig
            {
                Command = OperatingSystem.IsWindows()
                    ? $"ping 127.0.0.1 -n 2 > nul & type nul > \"{sentinel}\""  // ~1 second
                    : $"sleep 1; touch '{sentinel}'",
                TimeoutSeconds = 30,
            };
            var payload = JsonSerializer.SerializeToElement(new { x = 1 });
            var d = await LifecycleHooks.RunOneAsync(hook, LifecycleHookEvent.PreToolUse, payload, default);
            Assert.Equal("allow", d.Action);
            Assert.True(File.Exists(sentinel),
                "INSTRUMENT NOT LIVE: this hook command never writes its sentinel even when allowed "
                + "to run to completion, so the timeout test's `sentinel absent` assertion is vacuous.");
        }
        finally
        {
            try { File.Delete(sentinel); } catch { }
        }
    }

    [Fact]
    public async Task RunAsync_deny_short_circuits_remaining_hooks()
    {
        // Two hooks: first denies, second would touch a sentinel file
        // if it ran. Assert the sentinel didn't get written.
        var sentinel = Path.Combine(Path.GetTempPath(), "vett-hook-deny-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hook1 = new LifecycleHookConfig
            {
                Command = OperatingSystem.IsWindows()
                    ? "echo {\"action\":\"deny\",\"reason\":\"first hook said no\"}"
                    : "printf '%s' '{\"action\":\"deny\",\"reason\":\"first hook said no\"}'",
            };
            var hook2 = new LifecycleHookConfig
            {
                Command = OperatingSystem.IsWindows()
                    ? $"type nul > \"{sentinel}\""
                    : $"touch '{sentinel}'",
            };
            var profile = new Profile
            {
                Hooks = new HooksConfig { PreToolUse = new() { hook1, hook2 } },
            };
            var payload = JsonSerializer.SerializeToElement(new { x = 1 });
            var d = await LifecycleHooks.RunAsync(profile, LifecycleHookEvent.PreToolUse, payload, default);
            Assert.Equal("deny", d.Action);
            Assert.Equal("first hook said no", d.Reason);
            Assert.False(File.Exists(sentinel), "Second hook should not have run after first denied");
        }
        finally
        {
            try { File.Delete(sentinel); } catch { }
        }
    }

    [Fact]
    public async Task RunAsync_modify_pipelines_through_to_next_hook_and_back()
    {
        // Single modify hook — its modified payload should surface as
        // the aggregate decision's Modified field.
        var hook = new LifecycleHookConfig
        {
            Command = OperatingSystem.IsWindows()
                ? "echo {\"action\":\"modify\",\"modified\":{\"text\":\"redacted\"}}"
                : "printf '%s' '{\"action\":\"modify\",\"modified\":{\"text\":\"redacted\"}}'",
        };
        var profile = new Profile
        {
            Hooks = new HooksConfig { UserPromptSubmit = new() { hook } },
        };
        var payload = JsonSerializer.SerializeToElement(new { text = "original" });
        var d = await LifecycleHooks.RunAsync(profile, LifecycleHookEvent.UserPromptSubmit, payload, default);
        Assert.Equal("modify", d.Action);
        Assert.True(d.Modified.HasValue);
        Assert.Equal("redacted", d.Modified!.Value.GetProperty("text").GetString());
    }

    [Fact]
    public async Task RunAsync_skips_blank_command_entries()
    {
        // A hook with an empty command string should be silently
        // skipped (no spawn attempt). Useful when a profile has been
        // edited mid-session and one entry was emptied out.
        var profile = new Profile
        {
            Hooks = new HooksConfig
            {
                PreToolUse = new() { new LifecycleHookConfig { Command = "" } },
            },
        };
        var payload = JsonSerializer.SerializeToElement(new { x = 1 });
        var d = await LifecycleHooks.RunAsync(profile, LifecycleHookEvent.PreToolUse, payload, default);
        Assert.Equal("allow", d.Action);
    }
}
