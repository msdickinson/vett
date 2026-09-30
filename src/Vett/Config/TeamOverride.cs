using System.Text;

namespace Vett.Config;

/// <summary>
/// RUNTIME TEAM SHAPING (2026-08-28). Lets one base profile be resized at the
/// point of use instead of forcing a new YAML file per team shape.
///
/// WHY THIS EXISTS. `profiles/` had grown ds-manager-flash-w10.yaml (2,912
/// lines) and ds-manager-flash-w20.yaml (5,602 lines) which differ from each
/// other ONLY in how many feature leads they declare — every prompt in both is
/// byte-identical, emitted by a generator script. That is a generator working
/// around a missing feature. Mark, 2026-08-28: "most of the time a few members
/// or whatever we agreed on is fine but other times i need something unique, i
/// dont have to spin up an entire new team config. just a simple override".
///
/// SO: the profile stays the source of truth for PROMPTS and ROLES, and the
/// override supplies SHAPE — how many of each role, how wide the fan-out, and
/// how much context each seat gets.
///
/// ⛔ FAILS CLOSED. Every parse and resolve error returns a message and applies
/// NOTHING. A typo'd member name must not silently produce a smaller team than
/// asked for — that would be a run whose denominator nobody could name.
/// </summary>
public static class TeamOverride
{
    /// <summary>One parsed override. All fields null = "change nothing".</summary>
    public sealed record Spec(
        List<(string Role, int Count)>? Roster = null,
        int? Width = null,
        int? LeaderContext = null,
        int? WorkerContext = null,
        Dictionary<string, int>? PerRoleContext = null)
    {
        public bool IsEmpty => Roster is null && Width is null && LeaderContext is null
                               && WorkerContext is null && (PerRoleContext is null || PerRoleContext.Count == 0);
    }

    /// <summary>
    /// Parse a roster spec: "implementer x5, reviewer x2, researcher".
    /// A bare role means one seat. Separators are commas; `xN`, `*N` and `N`
    /// are all accepted after the name because this gets typed by a human.
    /// </summary>
    public static bool TryParseRoster(string? text, out List<(string Role, int Count)> roster, out string error)
    {
        roster = [];
        error = "";
        if (string.IsNullOrWhiteSpace(text)) return true;

        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var role = parts[0].Trim();
            var count = 1;
            if (parts.Length > 1)
            {
                var n = parts[1].TrimStart('x', 'X', '*').Trim();
                if (!int.TryParse(n, out count))
                {
                    error = $"team spec: could not read a count from '{raw}'. Use e.g. 'implementer x5'.";
                    return false;
                }
            }
            else if (role.LastIndexOf('x') > 0
                     && int.TryParse(role[(role.LastIndexOf('x') + 1)..], out var inline))
            {
                count = inline;
                role = role[..role.LastIndexOf('x')].TrimEnd();
            }

            if (count < 1)
            {
                error = $"team spec: '{raw}' asks for {count} seats. A role needs at least 1 (drop it entirely to remove it).";
                return false;
            }
            if (count > 64)
            {
                error = $"team spec: '{raw}' asks for {count} seats. Cap is 64 — that is already far past what any measured run has used.";
                return false;
            }
            roster.Add((role, count));
        }
        return true;
    }

    /// <summary>Parse "implementer=60000, reviewer=30000" into a per-role map.</summary>
    public static bool TryParsePerRoleContext(string? text, out Dictionary<string, int> map, out string error)
    {
        map = new(StringComparer.OrdinalIgnoreCase);
        error = "";
        if (string.IsNullOrWhiteSpace(text)) return true;

        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = raw.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !int.TryParse(kv[1], out var tokens))
            {
                error = $"context spec: could not read '{raw}'. Use e.g. 'implementer=60000'.";
                return false;
            }
            if (tokens < 1000)
            {
                error = $"context spec: '{raw}' sets a {tokens}-token trigger. That compacts almost immediately; use at least 1000.";
                return false;
            }
            map[kv[0]] = tokens;
        }
        return true;
    }

    /// <summary>
    /// Apply the spec to a loaded profile IN PLACE. Returns false with a
    /// human-readable error when anything does not resolve — and in that case
    /// the profile has NOT been partially modified, because every check that
    /// can fail runs before the first mutation.
    /// </summary>
    public static bool TryApply(Profile profile, Spec spec, out string error, out string summary)
    {
        error = "";
        summary = "";

        var team = profile.Team;

        // ⭐ THE ROSTER IS INJECTED ON EVERY TEAM RUN, NOT ONLY ON A RESIZE.
        // Until 2026-09-01 this happened only inside the `newMembers is not
        // null` branch below, so a run with NO --team flag got no authoritative
        // roster and the leader had to infer its members' names from prose.
        //
        // MEASURED, not supposed. EpicForge ladder rung E1 ran with no override
        // against a profile whose only implementer seat is called
        // "implementer". Its leader's first act was
        // assign_task("implementer-1"), which came back
        //     [implementer-1-1 - implementer-1 FAILED]
        //     Unknown member "implementer-1"
        // - a whole dispatch round-trip spent on a name that never existed. The
        // leader recovered on the retry, so it cost one call rather than the
        // run; at 10-20 concurrent teams that is a tax paid per team, and a
        // leader with a smaller iteration budget would not have recovered.
        // Across the four runs that DID pass --team (and therefore got the
        // block) there were 0 unknown-member failures in 355 tool results.
        //
        // Note the vett-side reason this was expensive to notice: the failure
        // is handed back to the leader as an ordinary tool result with
        // success=TRUE, so nothing keyed on `success` counts it.
        if (team is not null) RewriteLeaderRoster(team);

        if (spec.IsEmpty) return true;

        if (team is null)
        {
            error = $"profile '{profile.Name}' is a SOLO profile — it has no `team:` block, so there is no team to resize. "
                  + "Pick a team profile (e.g. ds-team-flash) and re-run.";
            return false;
        }

        // ---- resolve everything BEFORE mutating anything ----
        var byName = team.Members.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        List<MemberConfig>? newMembers = null;

        if (spec.Roster is { Count: > 0 })
        {
            newMembers = [];
            foreach (var (role, count) in spec.Roster)
            {
                if (!byName.TryGetValue(role, out var template))
                {
                    error = $"team spec: profile '{profile.Name}' has no member called '{role}'. "
                          + $"It defines: {string.Join(", ", team.Members.Select(m => m.Name))}. "
                          + "The override reuses an EXISTING role's prompt and tools — it cannot invent a new role.";
                    return false;
                }
                for (var i = 1; i <= count; i++)
                {
                    var clone = Clone(template);
                    // One seat keeps the plain role name so existing prompts and
                    // suites that say "implementer" still resolve. Several seats
                    // get -1..-N, and the leader roster below is rewritten to match.
                    clone.Name = count == 1 ? template.Name : $"{template.Name}-{i}";
                    newMembers.Add(clone);
                }
            }
        }

        foreach (var role in spec.PerRoleContext?.Keys ?? Enumerable.Empty<string>())
        {
            if (!byName.ContainsKey(role)
                && !role.Equals(team.Leader.Name, StringComparison.OrdinalIgnoreCase)
                && !role.Equals("leader", StringComparison.OrdinalIgnoreCase))
            {
                error = $"context spec: '{role}' is not a member of profile '{profile.Name}'. "
                      + $"It defines: {team.Leader.Name} (leader), {string.Join(", ", team.Members.Select(m => m.Name))}.";
                return false;
            }
        }

        // ---- mutate ----
        var notes = new List<string>();

        if (newMembers is not null)
        {
            team.Members = newMembers;
            RewriteLeaderRoster(team);
            notes.Add($"members: {string.Join(", ", newMembers.Select(m => m.Name))} ({newMembers.Count} seats)");
        }

        if (spec.Width is { } w)
        {
            team.MaxConcurrentDispatches = w;
            notes.Add($"max_concurrent_dispatches: {w}" + (w == 0 ? " (UNLIMITED)" : ""));
        }

        if (spec.LeaderContext is { } lc)
        {
            team.Leader.Compaction = WithThreshold(team.Leader.Compaction ?? profile.Compaction, lc);
            notes.Add($"{team.Leader.Name} (leader) context trigger: {lc:N0} tokens");
        }

        foreach (var m in team.Members)
        {
            // Per-role beats the blanket worker value. Match on the ROLE the seat
            // was cloned from ("implementer-3" matches a rule for "implementer")
            // as well as on its own full name, so a spec written against the
            // profile's role names keeps working after the roster is expanded.
            var role = StripSeatSuffix(m.Name);
            int? want = null;
            if (spec.PerRoleContext is not null)
            {
                if (spec.PerRoleContext.TryGetValue(m.Name, out var exact)) want = exact;
                else if (spec.PerRoleContext.TryGetValue(role, out var byRole)) want = byRole;
            }
            want ??= spec.WorkerContext;
            if (want is { } t)
                m.Compaction = WithThreshold(m.Compaction ?? profile.Compaction, t);
        }

        if (spec.WorkerContext is { } wc) notes.Add($"worker context trigger: {wc:N0} tokens");
        if (spec.PerRoleContext is { Count: > 0 })
            notes.Add("per-role context: " + string.Join(", ", spec.PerRoleContext.Select(kv => $"{kv.Key}={kv.Value:N0}")));

        summary = string.Join("; ", notes);
        return true;
    }

    internal static string StripSeatSuffix(string name)
    {
        var dash = name.LastIndexOf('-');
        return dash > 0 && int.TryParse(name[(dash + 1)..], out _) ? name[..dash] : name;
    }

    /// <summary>
    /// ⛔ THE BLOCK REPLACES, IT DOES NOT MERGE. CompactionConfig's own defaults
    /// are threshold 30000 / keep_last 5, so building a fresh object here and
    /// setting only the threshold would silently drop a profile's keep_last of 8
    /// to 5. Every other field is copied off whatever this seat was inheriting.
    /// The same trap is documented at ds-team-flash-cloud.yaml:311.
    /// </summary>
    private static CompactionConfig WithThreshold(CompactionConfig? inherited, int threshold) => new()
    {
        ThresholdTokens = threshold,
        KeepLastMessages = inherited?.KeepLastMessages ?? new CompactionConfig().KeepLastMessages,
        ElisionMaxChars = inherited?.ElisionMaxChars ?? new CompactionConfig().ElisionMaxChars,
        ElisionKeepLast = inherited?.ElisionKeepLast ?? new CompactionConfig().ElisionKeepLast,
    };

    private static MemberConfig Clone(MemberConfig m) => new()
    {
        Name = m.Name,
        SystemPrompt = m.SystemPrompt,
        Tools = [.. m.Tools],
        Middleware = [.. m.Middleware],
        Llm = m.Llm,
        MaxIterations = m.MaxIterations,
        ReceivesFrom = m.ReceivesFrom is null ? null : [.. m.ReceivesFrom],
        Compaction = m.Compaction,
        Team = m.Team,
    };

    /// <summary>
    /// The leader's prompt NAMES its members ("Available members: implementer,
    /// researcher, reviewer"). After a resize those names are wrong, and a
    /// leader that dispatches to a member that no longer exists burns its whole
    /// budget on failed calls. Appending a correction is not enough — a reader
    /// (or a model) that hits the stale line first acts on it — so the original
    /// line is REWRITTEN as well as an authoritative roster appended.
    /// </summary>
    private const string RosterMarker = "TEAM ROSTER FOR THIS RUN (authoritative";

    private static void RewriteLeaderRoster(TeamConfig team)
    {
        var names = team.Members.Select(m => m.Name).ToList();

        // IDEMPOTENT. This now runs unconditionally on load AND again after a
        // resize, so a previously-appended block must be REMOVED, not stacked.
        // Two roster blocks would be worse than none: the leader would read the
        // stale one first and dispatch to seats that no longer exist.
        var prompt = team.Leader.SystemPrompt;
        var prior = prompt.IndexOf(RosterMarker, StringComparison.Ordinal);
        if (prior >= 0) prompt = prompt[..prior].TrimEnd();

        var lines = prompt.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("Available members:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "Available members: " + string.Join(", ", names);
                break;
            }
        }

        var sb = new StringBuilder(string.Join("\n", lines));
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(RosterMarker + " — overrides any member");
        sb.AppendLine("list earlier in these instructions):");
        foreach (var g in names.GroupBy(StripSeatSuffix))
        {
            var seats = g.ToList();
            sb.AppendLine(seats.Count == 1
                ? $"  - {seats[0]}"
                : $"  - {string.Join(", ", seats)}  ({seats.Count} interchangeable {g.Key} seats)");
        }
        sb.AppendLine();
        sb.AppendLine("These are the ONLY valid member names. Passing any other name to");
        sb.AppendLine("assign_task/assign_async FAILS. Where several seats share a role they");
        sb.AppendLine("are interchangeable and INDEPENDENT: give each one a DIFFERENT unit of");
        sb.AppendLine("work and fire them with assign_async so they run at the same time —");
        sb.AppendLine("assigning the same work twice, or waiting for each one before starting");
        sb.AppendLine("the next, wastes the whole point of having them.");
        team.Leader.SystemPrompt = sb.ToString();
    }
}
