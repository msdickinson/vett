using System.Reflection;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Vett.Config;

/// <summary>One YAML key the deserialiser will silently drop.</summary>
/// <param name="Path">Dotted path to the key, e.g.
/// <c>team.members[1].llm.temprature</c>. Rooted at the document, so it can
/// be pasted straight into a grep.</param>
/// <param name="Key">The unrecognised key itself.</param>
/// <param name="Suggestion">Nearest recognised sibling key within edit
/// distance 2, or null when nothing is close enough to guess.</param>
public sealed record UnknownKey(string Path, string Key, string? Suggestion)
{
    public override string ToString() =>
        Suggestion is null ? Path : $"{Path} (did you mean \"{Suggestion}\"?)";
}

/// <summary>
/// The outcome of auditing one profile FILE — which is NOT the same shape as
/// a list of unknown keys.
///
/// ⛔ WHY THIS TYPE EXISTS. The audit has three outcomes, and the old return
/// type could only express two of them:
///
///   | Outcome                        | Truth                          | Old |
///   |--------------------------------|--------------------------------|-----|
///   | ran, found nothing             | the profile is clean           | `[]` |
///   | ran, found keys                | named knobs never reached code | list |
///   | COULD NOT RUN (locked/unreadable/malformed) | **unknown** | `[]` |
///
/// Rows 1 and 3 collapsed onto the same value. `[]` is the HEALTHY value here,
/// so a failure to measure published a POSITIVE CLAIM OF CLEANLINESS. The wire
/// format already documents the right answer for row 3 —
/// <c>JsonWire.ProfileUnknownKeys</c> says verbatim *"Empty is the healthy
/// value; null means the audit did not run"* — and the sentinel simply was not
/// used. A "could not measure" is not a "measured zero".
///
/// ⚠ Note which direction this fails in. An unaudited profile is not proof of
/// a BAD profile either; it is an absence of evidence. Callers must say
/// "unaudited", never "clean" and never "dirty".
/// </summary>
public sealed record ProfileAudit
{
    /// <summary>Keys the deserialiser will silently drop. Empty means the
    /// audit RAN and the profile is clean. <b>Null means the audit could not
    /// run at all</b> — see <see cref="UnmeasuredReason"/>.</summary>
    public List<UnknownKey>? Keys { get; init; }

    /// <summary>Why the audit could not run; null when it ran.</summary>
    public string? UnmeasuredReason { get; init; }

    /// <summary>True when a real answer was obtained, clean or not.</summary>
    public bool Measured => Keys is not null;

    /// <summary>The keys, or an empty list when unmeasured. ⚠ Only for
    /// iterating warnings — never branch on <c>.Count == 0</c> off this,
    /// because that is the exact collapse this type exists to prevent.
    /// Check <see cref="Measured"/> first.</summary>
    public IReadOnlyList<UnknownKey> KeysOrEmpty => Keys ?? [];

    public static ProfileAudit Ran(List<UnknownKey> keys) => new() { Keys = keys };

    public static ProfileAudit CouldNotMeasure(string why) =>
        new() { Keys = null, UnmeasuredReason = why };
}

/// <summary>
/// Finds YAML keys that no C# property will ever receive.
///
/// WHY THIS EXISTS. <see cref="Yaml"/>'s deserialiser is built with
/// <c>IgnoreUnmatchedProperties()</c> (Profile.cs:499). That is the right
/// call for LOADING — a profile written against a newer vett must not hard
/// -fail an older one — but it means a renamed or mistyped key is accepted
/// in total silence. For a benchmark harness that is the worst possible
/// failure mode: two arms that were meant to differ by one knob run
/// IDENTICALLY, every run is green, and the comparison between them
/// measured nothing.
///
/// The recognised-key set is derived by REFLECTION over the config types'
/// <see cref="YamlMemberAttribute"/> aliases, so it cannot go stale the way
/// a hand-maintained list does (<see cref="SpellCheck"/>'s 12-key list had
/// drifted to roughly half of <see cref="Profile"/>'s 23 top-level keys,
/// and nothing called it).
///
/// This is a REPORTING instrument only — it never mutates a profile and
/// never fails a load. Callers decide whether an unknown key is a warning
/// (<c>vett validate</c>, run headers) or fatal.
/// </summary>
public static class ProfileKeyAudit
{
    /// <summary>Audit YAML text against <see cref="Profile"/>.</summary>
    public static List<UnknownKey> AuditProfile(string yamlText) => Audit(typeof(Profile), yamlText);

    /// <summary>
    /// Audit a profile file on disk.
    ///
    /// ⛔ Unreadable / locked / unparseable files return
    /// <see cref="ProfileAudit.CouldNotMeasure"/>, <b>not</b> an empty list.
    /// The previous behaviour ("parse errors are the loader's job") published
    /// the healthy value on every failure path, so a profile locked by another
    /// process — the ordinary case on Windows when two arms start together —
    /// produced <c>profile_unknown_keys: []</c> and the summary asserted the
    /// config had been fully honoured. If that arm's distinguishing knob was
    /// misspelled, both arms ran the SAME configuration and the A/B measured
    /// nothing, under a green audit.
    ///
    /// Parse errors still are the loader's job to REPORT. They are this
    /// instrument's job to stop CLAIMING AROUND.
    /// </summary>
    public static ProfileAudit AuditProfileFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException e) { return ProfileAudit.CouldNotMeasure($"unreadable ({e.GetType().Name}: {e.Message})"); }
        catch (UnauthorizedAccessException e) { return ProfileAudit.CouldNotMeasure($"access denied ({e.Message})"); }

        try { return ProfileAudit.Ran(AuditProfile(text)); }
        catch (YamlDotNet.Core.YamlException e) { return ProfileAudit.CouldNotMeasure($"malformed YAML ({e.Message})"); }
    }

    /// <summary>
    /// Audit YAML text against an arbitrary config root type.
    ///
    /// ⛔ THROWS <c>YamlException</c> on malformed input — it does not return
    /// an empty list. Swallowing the parse error here is what let every caller
    /// mistake "could not read the document" for "the document is clean"; the
    /// decision belongs at the file boundary (<see cref="AuditProfileFile"/>),
    /// which has a sentinel for it. Leaving the catch in place as a
    /// convenience would just re-open the hole one call up.
    ///
    /// ⚠ Whitespace-only text returns empty and that IS a measured zero: a
    /// document with no keys has no unrecognised keys. It is not the same
    /// state as a document that could not be read.
    /// </summary>
    public static List<UnknownKey> Audit(Type rootType, string yamlText)
    {
        var found = new List<UnknownKey>();
        if (string.IsNullOrWhiteSpace(yamlText)) return found;

        var stream = new YamlStream();
        stream.Load(new StringReader(yamlText));

        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is YamlMappingNode root)
                Walk(root, rootType, "", found);
        }
        return found;
    }

    private static void Walk(YamlMappingNode map, Type type, string path, List<UnknownKey> found)
    {
        var props = PropertiesOf(type);
        foreach (var (keyNode, valueNode) in map.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key }) continue;
            var childPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";

            if (!props.TryGetValue(key, out var prop))
            {
                found.Add(new UnknownKey(childPath, key, SpellCheck.Suggest(key, props.Keys)));
                continue;
            }

            Descend(valueNode, prop.PropertyType, childPath, found);
        }
    }

    /// <summary>
    /// Recurse into a value node using the CLR type the deserialiser will
    /// bind it to. Three shapes matter:
    ///   - a config class          → its own keys are schema keys
    ///   - List&lt;config class&gt;      → each element is a mapping of schema keys
    ///   - Dictionary&lt;string, V&gt;   → the KEYS are user-chosen names (server
    ///     names, tool names) and must NOT be flagged; only V's keys are schema.
    /// Anything else (scalars, string lists, Dictionary&lt;string,string&gt;)
    /// terminates the walk.
    /// </summary>
    private static void Descend(YamlNode value, Type type, string path, List<UnknownKey> found)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (IsConfigType(type))
        {
            if (value is YamlMappingNode m) Walk(m, type, path, found);
            return;
        }

        if (TryElementOf(type, typeof(IEnumerable<>), out var element) && value is YamlSequenceNode seq)
        {
            for (int i = 0; i < seq.Children.Count; i++)
                Descend(seq.Children[i], element, $"{path}[{i}]", found);
            return;
        }

        if (TryDictionaryValueOf(type, out var dictValue) && value is YamlMappingNode dict)
        {
            foreach (var (k, v) in dict.Children)
            {
                var name = (k as YamlScalarNode)?.Value ?? "?";
                Descend(v, dictValue, $"{path}.{name}", found);
            }
        }
    }

    /// <summary>A type whose properties define YAML keys: any non-string
    /// class declared in the vett assembly itself.</summary>
    private static bool IsConfigType(Type t) =>
        t.IsClass && t != typeof(string) && t.Assembly == typeof(Profile).Assembly;

    private static bool TryElementOf(Type t, Type openGeneric, out Type element)
    {
        element = typeof(object);
        if (t.IsArray) { element = t.GetElementType()!; return true; }
        foreach (var i in t.GetInterfaces().Prepend(t))
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric
                && i.GetGenericArguments() is [var arg]
                && !IsDictionaryLike(t))
            {
                element = arg;
                return true;
            }
        }
        return false;
    }

    private static bool IsDictionaryLike(Type t) =>
        t.GetInterfaces().Prepend(t).Any(i => i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

    private static bool TryDictionaryValueOf(Type t, out Type valueType)
    {
        foreach (var i in t.GetInterfaces().Prepend(t))
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                && i.GetGenericArguments() is [_, var v])
            {
                valueType = v;
                return true;
            }
        }
        valueType = typeof(object);
        return false;
    }

    private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> _cache = new();

    /// <summary>
    /// YAML-name → property for one config type. The name is the
    /// <see cref="YamlMemberAttribute"/> alias when present, otherwise the
    /// property name run through the same underscored convention
    /// <see cref="Yaml"/>'s deserialiser is configured with. Properties
    /// marked <see cref="YamlIgnoreAttribute"/> are not bindable and are
    /// deliberately absent, so naming one in YAML is reported.
    /// </summary>
    public static IReadOnlyDictionary<string, PropertyInfo> PropertiesOf(Type type)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(type, out var cached)) return cached;
            var map = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetCustomAttribute<YamlIgnoreAttribute>() is not null) continue;
                if (p.GetSetMethod() is null) continue;
                var alias = p.GetCustomAttribute<YamlMemberAttribute>()?.Alias;
                map[!string.IsNullOrEmpty(alias) ? alias : Underscored(p.Name)] = p;
            }
            _cache[type] = map;
            return map;
        }
    }

    /// <summary>PascalCase → snake_case, matching
    /// YamlDotNet's UnderscoredNamingConvention for un-aliased properties.</summary>
    private static string Underscored(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
