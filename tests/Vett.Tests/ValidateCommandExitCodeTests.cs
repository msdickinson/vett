using System.CommandLine;
using Vett.Agent;
using Vett.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Vett.Cli;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Failure tests for `vett validate` — its EXIT CODE and its PROFILE DISCOVERY.
///
/// WHY THIS EXISTS. Two defects, both measured on the shipped command before
/// the 2026-08-25 fix:
///
/// 1. THE VALIDATOR THREW AWAY ITS OWN VERDICT. The action lambda returned
///    void, so `validate` counted errors, printed them, and exited 0 — on a
///    missing profile, unparseable YAML, a broken schema, every failure it can
///    detect. Measured: `--profile vett-no-such-profile-xyz` printed
///    "1 error(s), 0 warning(s)" and returned rc=0. Anything gating on `$?`
///    read a total failure as a pass. The point of a validator IS its exit code.
///
/// 2. IT VALIDATED A DIFFERENT SET OF FILES THAN THE RUNTIME LOADS. Discovery
///    was cwd/profiles only, while the runtime resolves cwd > ~/.vett >
///    install-dir. That was wrong in BOTH directions: `--profile X` reported
///    "file not found" for a profile living in ~/.vett that runs perfectly
///    (a false ERROR), and the sweep never opened the other two stores at all
///    (a gate covering a subset of the population it claims to cover passes
///    VACUOUSLY over the rest). Measured after the fix: coverage went from 23
///    profiles to 49.
///
/// Invoked through the SAME path Program.cs uses — root.Parse(args).Invoke() —
/// because defect 1 was never in the error handling. It was in which SetAction
/// overload the lambda bound to, and only the real invoke path can observe
/// that. A test calling a helper and inspecting a return value would have
/// passed against the broken code.
///
/// Hermetic: HOME is redirected to a temp dir, so the ~/.vett store under test
/// is one this test built, not whatever happens to be on the machine.
/// </summary>
[Collection("declare-done-env")]
public class ValidateCommandExitCodeTests : IDisposable
{
    private readonly string _home;
    private readonly string _store;
    private readonly string? _oldHome;
    private readonly string? _oldUserProfile;

    public ValidateCommandExitCodeTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "vett-validate-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _store = Path.Combine(_home, ".vett", "profiles");
        Directory.CreateDirectory(_store);

        _oldHome = Environment.GetEnvironmentVariable("HOME");
        _oldUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        Environment.SetEnvironmentVariable("HOME", _home);
        Environment.SetEnvironmentVariable("USERPROFILE", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _oldHome);
        Environment.SetEnvironmentVariable("USERPROFILE", _oldUserProfile);
        try { Directory.Delete(_home, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>A profile that passes every offline check.</summary>
    private const string CleanYaml = """
        name: PLACEHOLDER
        system_prompt: |
          You are a test fixture.
        llm:
          provider: local
          endpoint: http://127.0.0.1:1/v1
          model: no-such-model
          temperature: 0.0
          # A CLEAN fixture must be clean by the CURRENT rules, not the rules
          # of the day it was written. Added 2026-09-01 with the unbounded-
          # output check: without a bound every seat of this profile is
          # genuinely unbounded, so `validate` correctly stops reporting
          # "0 warning(s)" and the leader-tools NEGATIVE CONTROL below fails
          # for a reason that has nothing to do with leader.tools. Fixing the
          # fixture keeps that test's strongest assertion intact; deleting the
          # assertion instead would have retired the tripwire.
          max_output_tokens: 4096
        tools: []
        """;

    private string WriteToHomeStore(string name, string yaml) =>
        WriteFile(Path.Combine(_store, name + ".yaml"), yaml.Replace("PLACEHOLDER", name));

    private static string WriteFile(string path, string content)
    {
        File.WriteAllText(path, content);
        return path;
    }

    private static (int Code, string Output) Invoke(params string[] args)
    {
        var root = new RootCommand("test");
        root.Add(ValidateCommand.Create(NullLogger.Instance));

        var saved = Console.Out;
        var buf = new StringWriter();
        try
        {
            Console.SetOut(buf);
            var code = root.Parse(args).Invoke();
            return (code, buf.ToString());
        }
        finally { Console.SetOut(saved); }
    }

    /// <summary>
    /// ⭐ THE POSITIVE CONJUNCT. Without it, an implementation that returned 1
    /// for literally every input would satisfy every other assertion here.
    /// A clean profile must exit 0 through the identical Parse().Invoke() path,
    /// which proves this rig can observe a zero at all.
    /// </summary>
    [Fact]
    public void The_rig_can_observe_a_zero()
    {
        WriteToHomeStore("vett-test-clean", CleanYaml);

        var (code, output) = Invoke("validate", "--profile", "vett-test-clean");

        Assert.Equal(0, code);
        Assert.Contains("0 error(s)", output);
    }

    /// <summary>
    /// THE DEFECT, STATED AS ITSELF. The command already printed the error; the
    /// bug was that the process still said "success" afterwards.
    /// </summary>
    [Fact]
    public void A_nonexistent_profile_is_an_error_not_a_success()
    {
        var (code, output) = Invoke("validate", "--profile", "vett-test-no-such-profile-2f9c1");

        Assert.NotEqual(0, code);
        Assert.Equal(1, code);
        Assert.Contains("1 error(s)", output);

        // ...and it says WHERE it looked. A "not found" with no search path is
        // unactionable when one name can come from three stores.
        Assert.Contains("not found in any of", output);
    }

    /// <summary>
    /// Unparseable YAML is an error the validator exists to catch, so it must
    /// also reach the exit code — this covers a different `errors++` site than
    /// the not-found path, so a fix that only wired up one of them fails here.
    /// </summary>
    [Fact]
    public void A_profile_that_cannot_be_parsed_is_an_error_not_a_success()
    {
        WriteToHomeStore("vett-test-broken", "llm: [this is not\n  valid: yaml: at all\n   - {{{");

        var (code, output) = Invoke("validate", "--profile", "vett-test-broken");

        Assert.Equal(1, code);
        Assert.Contains("parse error", output);
    }

    /// <summary>
    /// An out-of-range temperature is caught and must fail the process. Same
    /// reasoning as above: a third distinct `errors++` site.
    /// </summary>
    [Fact]
    public void An_out_of_range_temperature_is_an_error_not_a_success()
    {
        WriteToHomeStore("vett-test-hot", CleanYaml.Replace("temperature: 0.0", "temperature: 7.5"));

        var (code, output) = Invoke("validate", "--profile", "vett-test-hot");

        Assert.Equal(1, code);
        Assert.Contains("out of range", output);
    }

    /// <summary>
    /// WARNINGS MUST NOT FAIL THE PROCESS. They cover "unverified" and "unknown
    /// tool, may be a plugin" — states that are honestly reported but are not
    /// defects. If warnings failed, every real workspace would be red and the
    /// exit code would carry no information.
    /// </summary>
    [Fact]
    public void Warnings_alone_do_not_fail_the_process()
    {
        // No system_prompt → a warning, not an error.
        WriteToHomeStore("vett-test-warn", """
            name: vett-test-warn
            llm:
              provider: local
              endpoint: http://127.0.0.1:1/v1
              model: no-such-model
            tools: []
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-warn");

        Assert.Equal(0, code);
        Assert.Contains("0 error(s)", output);
        Assert.DoesNotContain("0 warning(s)", output);   // it really did warn
    }

    /// <summary>
    /// THE FALSE ERROR. A profile living only in ~/.vett resolves and runs
    /// fine, but the old cwd-only discovery reported it "file not found" and
    /// counted an error. The validator must agree with the runtime about what
    /// EXISTS, and must say which store the file came from — a bare name is
    /// ambiguous across three of them.
    /// </summary>
    [Fact]
    public void A_profile_that_lives_only_in_the_home_store_is_found_not_a_false_error()
    {
        var path = WriteToHomeStore("vett-test-home-only", CleanYaml);

        var (code, output) = Invoke("validate", "--profile", "vett-test-home-only");

        Assert.Equal(0, code);
        Assert.DoesNotContain("not found", output);
        Assert.Contains(Path.GetDirectoryName(path)!, output);   // the store is named
    }

    /// <summary>
    /// THE SWEEP MUST SPAN THE POPULATION. With no --profile, discovery has to
    /// reach every store the runtime can load from, or the sweep passes
    /// vacuously over the ones it never opened.
    /// </summary>
    [Fact]
    public void The_sweep_reaches_profiles_outside_the_working_directory()
    {
        WriteToHomeStore("vett-test-sweep-reaches-me", CleanYaml);

        var (_, output) = Invoke("validate");

        Assert.Contains("vett-test-sweep-reaches-me", output);
    }

    /// <summary>
    /// ONE NAME, TWO STORES, DIFFERENT BYTES — the case that actually bites,
    /// because which file you get depends on your working directory, so the
    /// same `--profile X` is two different rulers from two different shells.
    /// The cwd copy wins (that is what the runtime loads) and the divergence is
    /// reported.
    ///
    /// Identical copies must stay SILENT: the install-dir store is a build-time
    /// snapshot of profiles/, so most shadowing is byte-identical and benign.
    /// Warning on those would bury this one. Both sides asserted below.
    /// </summary>
    [Fact]
    public void Divergent_copies_of_one_name_are_reported_but_identical_ones_are_not()
    {
        // A profile that already exists in the working directory's store.
        var cwdCopy = Path.Combine(Directory.GetCurrentDirectory(), "profiles", "ds-team-flash.yaml");
        Assert.True(File.Exists(cwdCopy), $"fixture precondition: {cwdCopy} must exist");

        var homeCopy = Path.Combine(_store, "ds-team-flash.yaml");

        // NEGATIVE SIDE FIRST: a byte-identical shadow is not a finding.
        File.Copy(cwdCopy, homeCopy, overwrite: true);
        var (identicalCode, identicalOut) = Invoke("validate", "--profile", "ds-team-flash");
        Assert.Equal(0, identicalCode);
        Assert.DoesNotContain("DIVERGENT", identicalOut);

        // POSITIVE SIDE: change one byte and it must be reported.
        File.AppendAllText(homeCopy, "\n# one differing byte\n");
        var (divergentCode, divergentOut) = Invoke("validate", "--profile", "ds-team-flash");

        Assert.Contains("DIVERGENT", divergentOut);
        Assert.Contains(homeCopy, divergentOut);
        Assert.Contains(cwdCopy, divergentOut);          // and which one WINS
        Assert.Equal(0, divergentCode);                  // a warning, not an error
    }

    // ---- TOPOLOGY: can the declared team shape actually RUN? ---------------
    //
    // ⛔ Measured 2026-08-26, before these existed: every profile below got a
    // clean ✓ and "0 error(s), 0 warning(s)" with rc=0. Each of them is
    // un-runnable, and each is decidable from the YAML alone with no network
    // and no model. Everything validate DID check was genuinely fine — nothing
    // looked at the shape.

    /// <summary>
    /// A team nested `levels` deep, as a profile.
    ///
    /// ⚠ WRITTEN RECURSIVELY ON PURPOSE. The first version of this builder used
    /// a flat loop with `4 * i` padding and produced YAML where the nested
    /// `team:` sat at the SEQUENCE DASH's column instead of inside the member —
    /// a parse error, not a deep team. The too-deep test still went green on its
    /// exit code, because a parse error is also rc=1: **the fixture was broken in
    /// the direction that flatters the check.** Only the message assertion caught
    /// it. Both tests below therefore assert on the TEXT, never on rc alone.
    /// </summary>
    /// <remarks>
    /// ⚠ EVERY level declares `max_concurrent_dispatches`, and it must. Since
    /// 2026-08-27 an absent ceiling is an ERROR on every team node, so a builder
    /// that omitted it would make each added level contribute an extra error —
    /// and the depth tests below assert on error TEXT and rc, both of which the
    /// extra errors would move. The fixture would then be failing for a reason
    /// that has nothing to do with depth, which is the "broken in the direction
    /// that flatters the check" trap this builder already carries a warning about.
    /// </remarks>
    private static string NestedYaml(int levels) =>
        CleanYaml + "\nteam:\n  max_concurrent_dispatches: 4\n  leader:\n    name: leader-0\n  members:\n"
        + MembersBlock(level: 0, remaining: levels, dash: 4);

    /// <param name="dash">Column of the "-"; a member's own keys sit at dash+2.</param>
    private static string MembersBlock(int level, int remaining, int dash)
    {
        var s = $"{new string(' ', dash)}- name: member-{level}\n";
        if (remaining <= 0) return s;

        var k = new string(' ', dash + 2);
        return s + $"{k}team:\n{k}  max_concurrent_dispatches: 4\n{k}  leader:\n"
                 + $"{k}    name: leader-{level + 1}\n{k}  members:\n"
                 + MembersBlock(level + 1, remaining - 1, dash + 6);
    }

    /// <summary>
    /// ⭐ THE FINDING, STATED AS ITSELF. Five agent levels — one past the cap —
    /// validated clean and would then have thrown mid-dispatch, after paying
    /// for every token spent getting down to the level that gets refused.
    ///
    /// ERROR, not warning, and the distinction is load-bearing: --endpoint /
    /// --model / VETT_LLM_* can rescue an incomplete seat at launch, but the
    /// dispatch cap is a compile-time constant, so nothing can rescue this. The
    /// run is already decided to fail.
    /// </summary>
    [Fact]
    public void A_team_nested_PAST_the_dispatch_cap_is_an_error_not_a_success()
    {
        WriteToHomeStore("vett-test-too-deep", NestedYaml(TeamCoordinator.MaxDispatchDepth + 1));

        var (code, output) = Invoke("validate", "--profile", "vett-test-too-deep");

        Assert.Equal(1, code);
        Assert.Contains("dispatch cap", output);
        Assert.Contains("would throw mid-run", output);

        // ⛔ THE rc=1 MUST COME FROM THE TOPOLOGY, NOT FROM A MALFORMED FIXTURE.
        // This is the assertion that failed when the builder was broken.
        Assert.DoesNotContain("parse error", output);
        Assert.Contains($"nests {TeamCoordinator.MaxDispatchDepth + 1} level(s)", output);
    }

    /// <summary>
    /// ⭐ THE OTHER HALF. A check that refused every nested profile would pass
    /// the test above perfectly while silently disabling the sub-worker level
    /// Mark explicitly kept ("a 4th ... but that would max out").
    /// </summary>
    [Fact]
    public void A_team_nested_exactly_AT_the_dispatch_cap_is_ACCEPTED()
    {
        WriteToHomeStore("vett-test-at-cap", NestedYaml(TeamCoordinator.MaxDispatchDepth));

        var (code, output) = Invoke("validate", "--profile", "vett-test-at-cap");

        Assert.Equal(0, code);
        Assert.Contains("0 error(s)", output);
        Assert.DoesNotContain("dispatch cap", output);
    }

    /// <summary>
    /// ⛔ DUPLICATE MEMBER NAMES ARE A GUARANTEED CRASH, NOT A STYLE PROBLEM.
    ///
    /// Coordinator builds `team.Members.ToDictionary(m => m.Name)` at two
    /// sites. ToDictionary THROWS on a repeated key, so the run dies at
    /// start-up every single time — and validate said ✓.
    ///
    /// The second assertion below pins the MECHANISM rather than trusting the
    /// reasoning: it runs the identical expression the production code runs and
    /// shows it throwing. If members ever stop being keyed by name, that
    /// assertion fails and tells the next reader this check's justification has
    /// expired — instead of leaving a rule nobody can re-derive.
    /// </summary>
    [Fact]
    public void Duplicate_member_names_are_an_error_because_the_dispatcher_throws_on_them()
    {
        WriteToHomeStore("vett-test-dupe", CleanYaml + """

            team:
              max_concurrent_dispatches: 4
              leader:
                name: manager
              members:
                - name: dup
                - name: dup
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-dupe");

        Assert.Equal(1, code);
        Assert.Contains("share the name", output);

        // THE MECHANISM, EXERCISED — same expression as Coordinator.cs:467/1636.
        var members = new List<MemberConfig>
        {
            new() { Name = "dup" },
            new() { Name = "dup" },
        };
        Assert.Throws<ArgumentException>(() => { _ = members.ToDictionary(m => m.Name); });
    }

    /// <summary>
    /// A MEMBER'S TOOLS ARE ALSO TOOLS. The sweep checked `profile.Tools` —
    /// the leader's list — and nothing else, so a member naming a tool that
    /// does not exist validated clean. That is the same "a team profile is
    /// summarized by its leader alone" defect ProfileSummaryBindingTests was
    /// written for, in the validator instead of the picker.
    ///
    /// A warning, matching how the leader's own unknown tools are treated
    /// (they may legitimately be plugins) — but the ✓ is withheld, because
    /// "unverified" must not read as "clean".
    /// </summary>
    [Fact]
    public void A_MEMBERS_unknown_tool_is_reported_not_just_the_leaders()
    {
        WriteToHomeStore("vett-test-member-tool", CleanYaml + """

            team:
              max_concurrent_dispatches: 4
              leader:
                name: manager
              members:
                - name: worker
                  tools: [vett_test_no_such_tool_9c1f]
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-member-tool");

        Assert.Equal(0, code);                                    // a warning, not an error
        Assert.Contains("vett_test_no_such_tool_9c1f", output);
        Assert.Contains("[team/worker]", output);                 // and WHICH seat
        Assert.DoesNotContain("✓ vett-test-member-tool", output); // the tick is withheld
    }

    // ---- THE FIXES' OWN FALSE ERRORS AND FALSE GREENS -----------------------
    //
    // ⛔ BOTH OF THESE WERE FOUND IN THE FINAL AUDIT, IN CODE ADDED EARLIER THE
    // SAME DAY TO FIX THE FALSE GREENS ABOVE. They are here because a fix pass
    // is exactly as capable of shipping a defect as the code it repairs.

    /// <summary>
    /// ⛔ THE INVERSE DEFECT: A FALSE ERROR ON A PERFECTLY RUNNABLE PROFILE.
    ///
    /// `tools:` written as a bare key with no value deserialises to NULL,
    /// overwriting the `= []` initialiser on the property. The member-tools
    /// check added earlier today dereferenced it unguarded, and the resulting
    /// ArgumentNullException was swallowed by the per-profile catch and printed
    /// as **"parse error"** — pointing the reader at a YAML syntax problem that
    /// does not exist, on a profile that runs.
    ///
    /// Measured before the fix: `✗ vett-test-nulltools — parse error: Value
    /// cannot be null. (Parameter 'source')`, rc=1.
    ///
    /// The `?? []` at the top-level `profile.Tools` sites was MISSING TOO and is
    /// PRE-EXISTING — this test covers both, which is why it asserts a clean ✓
    /// rather than merely "no parse error".
    /// </summary>
    [Fact]
    public void A_bare_tools_key_is_NOT_a_parse_error_because_null_is_not_malformed()
    {
        WriteToHomeStore("vett-test-nulltools", """
            name: vett-test-nulltools
            system_prompt: |
              You are a test fixture.
            llm:
              provider: local
              endpoint: http://127.0.0.1:1/v1
              model: no-such-model
            tools:
            team:
              max_concurrent_dispatches: 4
              leader:
                name: manager
              members:
                - name: alice
                  tools:
                - name: bob
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-nulltools");

        Assert.DoesNotContain("parse error", output);
        Assert.Equal(0, code);
        Assert.Contains("✓ vett-test-nulltools", output);

        // THE MECHANISM. If YamlDotNet ever starts honouring the initialiser,
        // this fails and tells the next reader the guard is now redundant —
        // rather than leaving three `?? []` nobody can justify.
        var probe = Yaml.ParseProfile("""
            name: p
            llm: {provider: local, endpoint: 'http://127.0.0.1:1/v1', model: m}
            tools:
            """);
        Assert.Null(probe.Tools);
    }

    /// <summary>
    /// ⛔ A NULL NAME THROWS AT A COUNT OF ONE.
    ///
    /// The duplicate-name check added earlier today only fires at count > 1, so
    /// a SINGLE member written as a bare `name:` slipped straight through it —
    /// measured: a clean ✓ and rc=0. But `Dictionary` rejects a null key
    /// outright, so that profile dies at start-up just as reliably as a
    /// duplicate does. The fix that closed one false green left its neighbour open.
    /// </summary>
    [Fact]
    public void A_SINGLE_member_with_no_name_is_an_error_because_a_null_key_throws()
    {
        WriteToHomeStore("vett-test-onenull", CleanYaml + """

            team:
              max_concurrent_dispatches: 4
              leader:
                name: manager
              members:
                - name:
                - name: bob
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-onenull");

        Assert.Equal(1, code);
        Assert.Contains("have no `name:` value", output);
        Assert.DoesNotContain("parse error", output);

        // THE MECHANISM, on the count that the duplicate check cannot see: ONE.
        var members = new List<MemberConfig> { new() { Name = null! } };
        Assert.Throws<ArgumentNullException>(() => { _ = members.ToDictionary(m => m.Name); });

        // ...and the boundary it must NOT over-reach: "" is a legal key, so a
        // single EMPTY name is not an error. A check that refused it would fail here.
        var empty = new List<MemberConfig> { new() { Name = "" } };
        Assert.Single(empty.ToDictionary(m => m.Name));
    }

    /// <summary>
    /// ⛔ A NEGATIVE WIDTH CEILING READS AS UNLIMITED — so an author who writes
    /// `max_concurrent_dispatches: -1` gets the exact opposite of what they
    /// asked for, and nothing at runtime can tell them: an uncapped run is
    /// indistinguishable from one nobody tried to cap. Same false-green shape as
    /// the null `name:` case above, and statically decidable the same way.
    ///
    /// Two-sided on purpose: 0 must stay rc=0 with a ✓, because an EXPLICIT 0 is
    /// a legal deliberate "uncapped" and the four 2026-08-26 campaign arms are
    /// written that way. It warns — see
    /// <see cref="An_EXPLICIT_zero_is_legal_but_warns_so_the_decision_stays_visible"/>
    /// — but a warning does not clear the ✓, so this arm still measures what it
    /// says it measures.
    ///
    /// ⚠ REWRITTEN 2026-08-27. This docstring used to read "0 is the DEFAULT",
    /// which stopped being true when absence became an error: 0 is now something
    /// an author must type. The arms are unchanged; only the reason they are the
    /// arms is. There is no `[InlineData]` here for ABSENT, because absence is no
    /// longer a value this Theory can express — it is
    /// <see cref="An_ABSENT_max_concurrent_dispatches_is_an_error_on_every_team_node"/>.
    /// </summary>
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    public void A_negative_max_concurrent_dispatches_is_an_error_because_it_means_UNLIMITED(
        int ceiling, int expectedCode)
    {
        WriteToHomeStore("vett-test-ceiling", CleanYaml
            + $"""

            team:
              max_concurrent_dispatches: {ceiling}
              leader:
                name: leader
              members:
                - name: worker
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-ceiling");

        Assert.Equal(expectedCode, code);
        // ⛔ NEVER ASSERT ON rc ALONE — a broken fixture's parse error is also
        // rc=1, which is how the nesting fixture passed while measuring nothing.
        Assert.DoesNotContain("parse error", output);

        if (expectedCode == 1)
        {
            Assert.Contains("is negative, which reads as UNLIMITED", output);
            Assert.Contains($"max_concurrent_dispatches: {ceiling}", output);
        }
        else
        {
            Assert.Contains("✓ vett-test-ceiling", output);
        }
    }

    /// <summary>
    /// ⭐ AN ABSENT CEILING IS AN ERROR, ON EVERY TEAM NODE (Mark, 2026-08-27:
    /// "fail if its not there"). Measured the same day: ZERO of the 12 team
    /// profiles in this repo set the key, so "absent = 0 = unlimited" was not a
    /// conservative default protecting a subset — every team node in the repo
    /// ran uncapped, and nothing reported it.
    ///
    /// ⛔ THE NESTED CASE IS WHY THIS IS NOT ONE ASSERTION. Each `team:` block
    /// gets its own board, so a capped top level over an uncapped sub-team is
    /// still an uncapped run. A check reading only `profile.Team` would pass the
    /// nested fixture below while covering 12 of this repo's 74 team nodes.
    ///
    /// ⛔ AND rc IS NOT ENOUGH — a fixture broken into a parse error is also
    /// rc=1, which is exactly how the nesting fixture in this file once passed
    /// while measuring nothing. Every arm asserts on TEXT.
    /// </summary>
    [Fact]
    public void An_ABSENT_max_concurrent_dispatches_is_an_error_on_every_team_node()
    {
        // TOP-LEVEL absent.
        WriteToHomeStore("vett-test-nocap", CleanYaml
            + """

            team:
              leader:
                name: leader
              members:
                - name: worker
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-nocap");

        Assert.Equal(1, code);
        Assert.DoesNotContain("parse error", output);
        Assert.Contains("is REQUIRED on every team node and is absent here", output);
        Assert.Contains("[team]", output);

        // NESTED absent, top level capped — the run is still uncapped one level
        // down, and the message must name the NESTED node rather than "team".
        WriteToHomeStore("vett-test-nocap-nested",
            CleanYaml
            + """

            team:
              max_concurrent_dispatches: 3
              leader:
                name: leader
              members:
                - name: lead
                  team:
                    leader:
                      name: sub-lead
                    members:
                      - name: worker
            """);

        var (nestedCode, nestedOut) = Invoke("validate", "--profile", "vett-test-nocap-nested");

        Assert.Equal(1, nestedCode);
        Assert.DoesNotContain("parse error", nestedOut);
        Assert.Contains("is REQUIRED on every team node and is absent here", nestedOut);
        Assert.Contains("[team/lead]", nestedOut);

        // ⭐ POSITIVE CONJUNCT. Without it, a check that errored on every team
        // profile would satisfy both assertions above. Same shape, both nodes
        // capped, must be a clean ✓ and rc=0.
        WriteToHomeStore("vett-test-cap-nested",
            CleanYaml
            + """

            team:
              max_concurrent_dispatches: 3
              leader:
                name: leader
              members:
                - name: lead
                  team:
                    max_concurrent_dispatches: 2
                    leader:
                      name: sub-lead
                    members:
                      - name: worker
            """);

        var (okCode, okOut) = Invoke("validate", "--profile", "vett-test-cap-nested");

        Assert.Equal(0, okCode);
        Assert.DoesNotContain("is REQUIRED on every team node", okOut);
        Assert.Contains("✓ vett-test-cap-nested", okOut);
    }

    /// <summary>
    /// An EXPLICIT `0` is legal — the requirement is that the author DECIDED,
    /// not that every team is capped — but it is warned about, so a deliberate
    /// "uncapped" stays visible in the output instead of looking like silence.
    /// This is the distinction that makes requiring the key worth anything: if
    /// explicit-0 were also an error, authors would have no way to say "I meant
    /// it" and the four 2026-08-26 campaign arms could not keep their measured
    /// behaviour.
    /// </summary>
    [Fact]
    public void An_EXPLICIT_zero_is_legal_but_warns_so_the_decision_stays_visible()
    {
        WriteToHomeStore("vett-test-zero", CleanYaml
            + """

            team:
              max_concurrent_dispatches: 0
              leader:
                name: leader
              members:
                - name: worker
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-zero");

        Assert.Equal(0, code);                                  // legal
        Assert.Contains("means UNLIMITED fan-out", output);     // but visible
        Assert.DoesNotContain("is REQUIRED on every team node", output);
    }

    /// <summary>
    /// ⚠ TWO AUTHOR-DECLARED WIDTHS THAT DISAGREE. Best-of is exempt from the
    /// width ceiling by design — its `n` belongs to the profile author, not to
    /// the leader, and capping it would silently run best-of-2 while still
    /// calling it best-of-5. That exemption is correct and it is also invisible,
    /// so an author who wrote both numbers gets told.
    ///
    /// ⭐ A WARNING, NOT AN ERROR, AND THE ✓ SURVIVES: the profile really is
    /// valid and runnable. That is what separates it from the unknown-tool
    /// warning, which suppresses the ✓ because something may genuinely be broken.
    /// </summary>
    [Theory]
    [InlineData(5, 2, true)]    // best-of wider than the ceiling → warn
    [InlineData(2, 5, false)]   // ceiling comfortably wider → silent
    // ⚠ An EXPLICIT 0 (= deliberately uncapped), not an absent key — since
    // 2026-08-27 absence is an error and could not reach this check at all. The
    // guard is `MaxConcurrentDispatches > 0`, so 0 has no ceiling to disagree
    // with. `expectWarning: false` is about the BEST-OF warning specifically;
    // the explicit-0 warning below is a different line and is asserted on by
    // An_EXPLICIT_zero_is_legal_but_warns_so_the_decision_stays_visible.
    [InlineData(5, 0, false)]
    public void A_best_of_wider_than_the_ceiling_WARNS_because_best_of_is_exempt(
        int bestOfN, int ceiling, bool expectWarning)
    {
        WriteToHomeStore("vett-test-bestof", CleanYaml
            + $"""

            team:
              max_concurrent_dispatches: {ceiling}
              # ⚠ REQUIRED, and this line was ADDED 2026-08-27. Without it the
              # fixture declared best-of on a profile where `assign_best_of` is
              # never registered — i.e. this test's own docstring claim that
              # "the profile really is valid and runnable" was false. The new
              # error below caught its own test file's fixture.
              dispatch_worktree: true
              best_of:
                n: {bestOfN}
                judge: reviewer
                member: worker
              leader:
                name: leader
              members:
                - name: worker
                - name: reviewer
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-bestof");

        // Informational either way — this must never become an error.
        Assert.Equal(0, code);
        Assert.DoesNotContain("parse error", output);
        Assert.Contains("✓ vett-test-bestof", output);

        if (expectWarning)
        {
            Assert.Contains($"`best_of.n` is {bestOfN}", output);
            Assert.Contains("EXEMPT from the ceiling", output);
        }
        else
        {
            Assert.DoesNotContain("EXEMPT from the ceiling", output);
        }
    }

    /// <summary>
    /// ⛔ TWO CHECKS MUST NOT PRINT CONTRADICTORY SENTENCES ABOUT ONE NODE.
    ///
    /// `best_of.n: 5` + `max_concurrent_dispatches: 2` + worktrees OFF used to
    /// emit both of these, on consecutive lines, about the same team:
    ///
    ///     ✗ … best-of silently does nothing
    ///     ⚠ … so this profile can run 5 candidates at once.
    ///
    /// The warning is FALSE exactly when the error is TRUE: with the tool never
    /// registered the real candidate count is ZERO, not five. A reader who
    /// believed the ⚠ would go looking for a width problem that does not exist.
    ///
    /// ⭐ THIS TEST EXISTS BECAUSE THE FIX PASS SHIPPED ITS OWN DEFECT. Adding
    /// `dispatch_worktree: true` to the ceiling fixture above (a correct fix —
    /// that fixture's docstring was lying) removed the ONLY fixture in the suite
    /// that could produce this pair, so the contradiction was introduced and
    /// hidden by the same change. Found by an adversarial audit OF THE REPAIR,
    /// not by the repair's own tests. A repair is unaudited code.
    /// </summary>
    [Fact]
    public void The_ceiling_warning_is_SUPPRESSED_when_best_of_is_inert_for_lack_of_worktrees()
    {
        WriteToHomeStore("vett-test-contradiction",
            CleanYaml
            + """

            team:
              max_concurrent_dispatches: 2
              # deliberately absent: dispatch_worktree  => best-of is INERT
              best_of:
                n: 5
                judge: reviewer
                member: worker
              leader:
                name: leader
              members:
                - name: worker
                - name: reviewer
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-contradiction");

        // The error is correct and must still fire.
        Assert.Equal(1, code);
        Assert.Contains("best-of silently does nothing", output);

        // ⛔ THE POINT. The ceiling warning claims five candidates can run at
        // once. Zero can. It must not appear alongside the error.
        Assert.DoesNotContain("EXEMPT from the ceiling", output);
        Assert.DoesNotContain("candidates at once", output);
    }

    /// <summary>
    /// ⛔ BEST-OF WITHOUT WORKTREES IS NOT BEST-OF — IT IS NOTHING, AND IT USED
    /// TO VALIDATE CLEAN.
    ///
    /// `assign_best_of` is registered inside the dispatchManager branch, so with
    /// `dispatch_worktree` false the tool is never registered: the leader gets a
    /// system prompt describing best-of and a toolset without it. Nothing errors.
    /// The run just quietly isn't best-of.
    ///
    /// ⚠ AND THIS IS THE DEFAULT PATH. `dispatch_worktree` is a bare `bool` with
    /// no initialiser, so a profile that declares `best_of` and never mentions
    /// worktrees lands here. That is why it is an ERROR and not a warning — the
    /// author has to opt IN to a working best-of, and nothing told them.
    ///
    /// Found 2026-08-27 while proving the runtime ceiling exemption: the first
    /// draft of BestOfCeilingExemptionTests ran worktrees-off and measured ZERO
    /// candidates against an expected five.
    ///
    /// ⭐ THE `true` CASE IS THE LOAD-BEARING ONE. An error that fired on every
    /// best-of profile would satisfy the false case while being useless; only the
    /// true case can see that.
    /// </summary>
    [Theory]
    [InlineData(false, 1)]  // declared best_of, worktrees off → ERROR
    [InlineData(true, 0)]   // declared best_of, worktrees on  → clean
    public void Best_of_WITHOUT_dispatch_worktree_is_an_error_because_the_tool_is_never_registered(
        bool worktree, int expectedCode)
    {
        WriteToHomeStore("vett-test-bestof-wt", CleanYaml
            + $"""

            team:
              max_concurrent_dispatches: 4
              dispatch_worktree: {worktree.ToString().ToLowerInvariant()}
              best_of:
                n: 3
                judge: reviewer
                member: worker
              leader:
                name: leader
              members:
                - name: worker
                - name: reviewer
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-bestof-wt");

        Assert.Equal(expectedCode, code);
        // ⛔ NEVER ASSERT ON rc ALONE — a broken fixture's parse error is rc=1 too.
        Assert.DoesNotContain("parse error", output);

        if (expectedCode == 1)
        {
            Assert.Contains("best-of silently does nothing", output);
            Assert.DoesNotContain("✓ vett-test-bestof-wt", output);
        }
        else
        {
            Assert.Contains("✓ vett-test-bestof-wt", output);
            Assert.DoesNotContain("best-of silently does nothing", output);
        }
    }

    /// <summary>
    /// ⭐ THE FALSE-ERROR GUARD, and the half that the repair could most easily
    /// have broken. A profile with NO `best_of` must be untouched by the check
    /// no matter what `dispatch_worktree` says — worktrees-off is a perfectly
    /// ordinary configuration for a team that never runs best-of, and it is the
    /// DEFAULT, so a check keyed on the wrong conjunct would redden almost
    /// everything.
    ///
    /// The `validate` repair that shipped on 2026-08-26 produced exactly this
    /// inverse defect once already — a false ERROR on a runnable profile — which
    /// is why the guard is written here rather than assumed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_profile_with_NO_best_of_is_untouched_by_the_worktree_check(bool worktree)
    {
        WriteToHomeStore("vett-test-nobestof", CleanYaml
            + $"""

            team:
              max_concurrent_dispatches: 4
              dispatch_worktree: {worktree.ToString().ToLowerInvariant()}
              leader:
                name: leader
              members:
                - name: worker
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-nobestof");

        Assert.Equal(0, code);
        Assert.DoesNotContain("parse error", output);
        Assert.DoesNotContain("best-of silently does nothing", output);
        Assert.Contains("✓ vett-test-nobestof", output);
    }

    /// <summary>
    /// ⛔ A GATE MUST SPAN THE POPULATION IT CLAIMS TO COVER. The check above
    /// lives inside the `TeamsOf` walk, which recurses into nested sub-teams —
    /// so it SHOULD fire on a sub-team too. "Should" because it is inside a
    /// recursive loop is an inference about control flow, not a measurement, and
    /// a check that silently covered only the top level would pass every test
    /// written so far while leaving every nested team unguarded.
    ///
    /// This profile is deliberately valid at the TOP level — the outer team has
    /// `dispatch_worktree: true` and no `best_of` — so the only thing that can
    /// produce an error is the nested sub-team. Asserting the nested PATH
    /// (`team/lead`) in the message is what proves the walk actually descended,
    /// rather than the top-level node coincidentally matching.
    /// </summary>
    [Fact]
    public void The_best_of_worktree_check_reaches_NESTED_sub_teams_not_just_the_top_level()
    {
        WriteToHomeStore("vett-test-nested-bestof",
            CleanYaml
            + """

            team:
              max_concurrent_dispatches: 4
              dispatch_worktree: true
              leader:
                name: manager
              members:
                - name: lead
                  team:
                    max_concurrent_dispatches: 4
                    dispatch_worktree: false
                    best_of:
                      n: 3
                      judge: subreviewer
                      member: subworker
                    leader:
                      name: sublead
                    members:
                      - name: subworker
                      - name: subreviewer
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-nested-bestof");

        Assert.Equal(1, code);
        Assert.DoesNotContain("parse error", output);
        Assert.Contains("best-of silently does nothing", output);

        // ⭐ THE LOAD-BEARING ASSERTION: the nested path, which only a walk that
        // actually descended can print. Without this the test would pass on a
        // check that fired for the wrong node.
        Assert.Contains("[team/lead]", output);
    }

    /// <summary>
    /// MY OWN FIX, UNTESTED UNTIL NOW. The `leader.tools` warning shipped in
    /// 5281268 with no test at all — a check that fires on nothing today (no
    /// shipped profile sets the key) and whose whole purpose is to catch a
    /// FUTURE author is exactly the kind that rots undetected.
    ///
    /// The behaviour under test is deliberately asymmetric and the asymmetry
    /// is the point:
    ///
    ///   - `leader.tools` PARSES. The leader is a MemberConfig
    ///     (Profile.cs:348), so it carries a `Tools` list and any value
    ///     written there is schema-valid YAML that loads without complaint.
    ///   - `leader.tools` is NEVER READ. Re-swept 2026-08-28: the only
    ///     occurrences of `Leader.Tools` in src/ are this validator's own
    ///     warning. Member tool filtering is keyed on `m.Tools.Count > 0`
    ///     across Coordinator.cs (453 inheritance, plus filter blocks at
    ///     726/735/741, 1440/1445/1450, 1983/1988/1994); there is no leader
    ///     equivalent at any of them.
    ///   - So the author gets a restriction that reads as enforced and is not.
    ///
    /// It is a WARNING and not an error ON PURPOSE, and that is the assertion
    /// most worth pinning: the leader's tool map is where assign_task,
    /// accept_dispatch and reject_dispatch are injected, so honouring a
    /// hand-written list would strip the coordination surface and turn a
    /// silent no-op into a dead team. Escalating this to an error would also
    /// break every gate that shells out to `vett validate` and reads $?.
    /// </summary>
    [Fact]
    public void Leader_tools_is_reported_because_nothing_reads_it()
    {
        WriteToHomeStore("vett-test-leadertools", CleanYaml + """

            team:
              max_concurrent_dispatches: 4
              leader:
                name: manager
                tools:
                  - bash
                  - file_editor
              members:
                - name: worker
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-leadertools");

        // ⭐ LOAD-BEARING: a WARNING, so the exit code stays 0. If this ever
        // reads 1, every CI gate that runs `vett validate` starts failing on a
        // profile that runs perfectly well.
        Assert.Equal(0, code);
        Assert.Contains("0 error(s)", output);

        Assert.Contains("`leader.tools` is set", output);
        Assert.Contains("NEVER READ", output);
        // The count, and its plural — the message quotes back what the author
        // wrote, so a reader can tell it found THEIR key and not some other.
        Assert.Contains("(2 entries)", output);
        // And it names the node, because one profile can have several.
        Assert.Contains("[team]", output);
    }

    /// <summary>
    /// ⭐ THE NEGATIVE CONTROL. Without it, a validator that printed this
    /// warning unconditionally — on every team profile, key or no key —
    /// satisfies the test above completely. Since no shipped profile sets
    /// `leader.tools`, an unconditional warning would fire on ALL of them and
    /// nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void A_leader_with_no_tools_key_produces_no_leader_tools_warning()
    {
        WriteToHomeStore("vett-test-noleadertools", CleanYaml + """

            team:
              max_concurrent_dispatches: 4
              leader:
                name: manager
              members:
                - name: worker
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-noleadertools");

        Assert.Equal(0, code);
        Assert.DoesNotContain("leader.tools", output);
        Assert.Contains("0 error(s)", output);
        // An empty list is the same case as an absent key — `Count > 0` is the
        // gate — and stating it here stops a future "normalise empty to null"
        // refactor from being read as a behaviour change.
        Assert.Contains("0 warning(s)", output);
    }

    /// <summary>
    /// ⛔ THE NESTED CASE IS WHY THIS IS NOT ONE ASSERTION. The check sits
    /// inside the TeamsOf walk, so it must reach a sub-team's leader as well
    /// as the top-level one. A check written against `profile.Team.Leader`
    /// directly would pass the first test and silently ignore every nested
    /// team — and nested teams are precisely where an author is most likely to
    /// reach for per-leader tool restriction.
    /// </summary>
    [Fact]
    public void The_leader_tools_warning_reaches_a_NESTED_team_leader()
    {
        WriteToHomeStore("vett-test-nested-leadertools", CleanYaml + """

            team:
              max_concurrent_dispatches: 4
              leader:
                name: manager
              members:
                - name: lead
                  team:
                    max_concurrent_dispatches: 2
                    leader:
                      name: sublead
                      tools:
                        - bash
                    members:
                      - name: subworker
            """);

        var (code, output) = Invoke("validate", "--profile", "vett-test-nested-leadertools");

        Assert.Equal(0, code);
        Assert.Contains("`leader.tools` is set", output);
        // ⭐ THE LOAD-BEARING ASSERTION: the nested path, which only a walk
        // that actually descended can print. The top-level leader here has NO
        // tools key, so a check that never descended prints nothing at all.
        Assert.Contains("[team/lead]", output);
        Assert.DoesNotContain("[team] — `leader.tools`", output);
        // Singular, since the nested leader names exactly one tool.
        Assert.Contains("(1 entry)", output);
    }

    /// <summary>
    /// THE JUSTIFICATION, PINNED — and given an EXPIRY.
    ///
    /// This warning only earns its place while both halves hold: the key
    /// parses, and nothing honours it. The first half is structural and IS
    /// testable, so test it. If a later refactor gives the leader its own
    /// config type without a `Tools` list, `leader: tools:` stops being
    /// schema-valid, the load starts failing on its own, and this whole check
    /// becomes dead code that no longer describes reality. This assertion
    /// fails at that moment and tells the next reader the justification has
    /// expired — rather than leaving a rule nobody can re-derive.
    ///
    /// ⚠ WHAT THIS DOES NOT PROVE. "Nothing reads Leader.Tools" is a claim
    /// about absence across a whole source tree; a unit test cannot establish
    /// it and this one does not pretend to. That half was established by
    /// sweep (see the class-doc above) and carries a date, not a green check.
    /// </summary>
    [Fact]
    public void The_leader_carries_a_Tools_list_which_is_why_the_key_parses_silently()
    {
        var leader = new MemberConfig { Name = "manager" };

        // The leader IS a MemberConfig, so it has the same Tools list a member
        // has — which is exactly why `leader: tools:` loads without complaint
        // and then does nothing.
        Assert.Empty(leader.Tools);
        leader.Tools.Add("bash");
        Assert.Single(leader.Tools);

        // And the member-side gate the leader has no equivalent of. Same
        // expression as Coordinator.cs:726/1440/1983.
        Assert.True(leader.Tools.Count > 0);

        // Structural: TeamConfig.Leader is typed as MemberConfig. If that
        // changes, the paragraph above stops being true.
        var leaderProp = typeof(TeamConfig).GetProperty(nameof(TeamConfig.Leader));
        Assert.NotNull(leaderProp);
        Assert.Equal(typeof(MemberConfig), leaderProp!.PropertyType);
    }
}
