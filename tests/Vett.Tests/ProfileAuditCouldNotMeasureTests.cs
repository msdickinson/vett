using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// F7 — the profile key audit returned its HEALTHY value when it could not
/// read the file.
///
/// `JsonWire.ProfileUnknownKeys` documents the contract in its own summary:
/// *"Empty is the healthy value; null means the audit did not run."* The
/// sentinel existed and was never used, so `AuditProfileFile` answered `[]` to
/// a locked file, an unreadable file, and a malformed one — the same value it
/// answers to a genuinely clean profile. A failure to measure was published as
/// a positive claim of cleanliness.
///
/// ⛔ WHY THIS ONE IS HARDER THAN THE OTHER FAIL-OPEN GATES. For every other
/// gate in the register the error value differs from the healthy value, so a
/// tag could be derived from the result alone. Here `[]` IS the healthy value.
/// No amount of inspecting the returned list can distinguish the two states —
/// the distinction has to be carried in the TYPE. That is why the fix is a new
/// return type rather than a new branch.
///
/// THE FAILURE SCENARIO THIS PINS. Two arms of an A/B start together; arm B's
/// profile is briefly locked by the editor writing it. Arm B publishes
/// `profile_unknown_keys: []`. If arm B's distinguishing knob was misspelled,
/// the deserialiser dropped it, both arms ran the SAME configuration, and the
/// comparison measured nothing — under a green audit on both arms.
///
/// ⚠ DIRECTION. "Unaudited" is not "dirty" either. These tests assert the
/// result is UNMEASURED and that callers say so; none of them assert the
/// profile is bad.
/// </summary>
public class ProfileAuditCouldNotMeasureTests : IDisposable
{
    private readonly string _dir;

    public ProfileAuditCouldNotMeasureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-audit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    // ---- section 1: the three could-not-measure paths ----

    [Fact]
    public void A_MISSING_file_is_UNMEASURED_not_clean()
    {
        var a = ProfileKeyAudit.AuditProfileFile(Path.Combine(_dir, "does-not-exist.yaml"));

        Assert.False(a.Measured);
        Assert.Null(a.Keys);
        Assert.NotNull(a.UnmeasuredReason);
    }

    [Fact]
    public void A_LOCKED_file_is_UNMEASURED_not_clean()
    {
        // The real incident shape: another process holds the profile open for
        // writing when the run starts.
        var path = Write("locked.yaml", "name: x\n");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var a = ProfileKeyAudit.AuditProfileFile(path);

        Assert.False(a.Measured);
        Assert.Null(a.Keys);
    }

    [Fact]
    public void A_MALFORMED_file_is_UNMEASURED_not_clean()
    {
        // Unbalanced flow mapping — the YAML parser cannot build a document,
        // so no statement about its keys is available.
        var path = Write("bad.yaml", "name: x\nteam: {unclosed: [1, 2\n");

        var a = ProfileKeyAudit.AuditProfileFile(path);

        Assert.False(a.Measured);
        Assert.Contains("malformed", a.UnmeasuredReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_REASON_is_carried_so_a_reader_can_act_on_it()
    {
        // "Could not measure" with no cause is barely better than a silent
        // pass — the operator has to know whether to unlock a file or fix YAML.
        var missing = ProfileKeyAudit.AuditProfileFile(Path.Combine(_dir, "nope.yaml"));
        var malformed = ProfileKeyAudit.AuditProfileFile(Write("bad2.yaml", "a: [1,\n"));

        Assert.False(string.IsNullOrWhiteSpace(missing.UnmeasuredReason));
        Assert.False(string.IsNullOrWhiteSpace(malformed.UnmeasuredReason));
        Assert.NotEqual(missing.UnmeasuredReason, malformed.UnmeasuredReason);
    }

    // ---- section 2: the controls — a real audit still works ----
    //
    // Without these, "always return CouldNotMeasure" satisfies section 1.

    [Fact]
    public void A_CLEAN_profile_is_MEASURED_and_empty()
    {
        // ⚠ The first draft of this fixture used `model:` at the top level and
        // went red — because `model` is NOT a Profile key (it lives under the
        // llm section). The instrument was right and the fixture was wrong.
        // Kept as a note: a red here is a claim about the fixture at least as
        // often as about the code.
        var path = Write("clean.yaml", "name: x\nsystem_prompt: hello\n");

        var a = ProfileKeyAudit.AuditProfileFile(path);

        Assert.True(a.Measured);
        Assert.Empty(a.Keys!);
        Assert.Null(a.UnmeasuredReason);
    }

    [Fact]
    public void A_DIRTY_profile_is_MEASURED_and_names_the_key()
    {
        var path = Write("dirty.yaml", "name: x\nzzzzzzzzzzzz: 1\n");

        var a = ProfileKeyAudit.AuditProfileFile(path);

        Assert.True(a.Measured);
        Assert.Contains(a.Keys!, k => k.Key == "zzzzzzzzzzzz");
    }

    [Fact]
    public void An_EMPTY_file_is_a_MEASURED_ZERO_not_a_could_not_measure()
    {
        // The boundary that keeps this fix honest in the other direction: a
        // document with no keys genuinely has no unrecognised keys. Reading it
        // succeeded. Calling that "unmeasured" would be a false RED, and would
        // make the new state mean "anything unusual" instead of "I could not
        // look".
        var path = Write("empty.yaml", "   \n");

        var a = ProfileKeyAudit.AuditProfileFile(path);

        Assert.True(a.Measured);
        Assert.Empty(a.Keys!);
    }

    // ---- section 3: the two states must not be confusable at the API ----

    [Fact]
    public void UNMEASURED_and_CLEAN_are_DISTINGUISHABLE_which_is_the_whole_point()
    {
        var clean = ProfileKeyAudit.AuditProfileFile(Write("c.yaml", "name: x\n"));
        var unmeasured = ProfileKeyAudit.AuditProfileFile(Path.Combine(_dir, "gone.yaml"));

        // Both have zero keys to iterate. That is exactly the collapse F7 was:
        // before the fix these two were the SAME VALUE and no caller could tell
        // them apart.
        Assert.Empty(clean.KeysOrEmpty);
        Assert.Empty(unmeasured.KeysOrEmpty);

        // The distinction survives only in the type.
        Assert.NotEqual(clean.Measured, unmeasured.Measured);
    }

    [Fact]
    public void The_TEXT_audit_THROWS_on_malformed_yaml_rather_than_returning_empty()
    {
        // The swallow used to live one layer down, in Audit(Type, string), so
        // removing it from the file path alone would have left the hole open
        // for any future caller of the text API. There must be no silent-empty
        // path anywhere in this instrument.
        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => ProfileKeyAudit.AuditProfile("name: x\nteam: {unclosed: [1, 2\n"));
    }
}
