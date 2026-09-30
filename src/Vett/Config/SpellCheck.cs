namespace Vett.Config;

public static class SpellCheck
{
    private static readonly HashSet<string> ProfileKeys =
        ["name", "description", "sandbox", "llm", "system_prompt", "system_prompt_file",
         "user_template", "tools", "middleware", "max_iterations", "timeout_minutes", "team"];

    public static List<string> Check(IEnumerable<string> keys)
    {
        var warnings = new List<string>();
        foreach (var key in keys.Where(k => !ProfileKeys.Contains(k)))
        {
            var best = Suggest(key, ProfileKeys);
            if (best is not null)
                warnings.Add($"unknown key \"{key}\" — did you mean \"{best}\"?");
        }
        return warnings;
    }

    /// <summary>
    /// Nearest candidate to <paramref name="key"/> within edit distance 2,
    /// or null when nothing is close enough to be worth guessing. Shared
    /// with <see cref="ProfileKeyAudit"/>, which supplies the recognised
    /// keys for the CURRENT node type instead of the fixed top-level list
    /// <see cref="Check"/> uses.
    /// </summary>
    public static string? Suggest(string key, IEnumerable<string> candidates)
    {
        var best = candidates.MinBy(v => Levenshtein(key, v));
        return best is not null && Levenshtein(key, best) <= 2 ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        var curr = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + (char.ToLower(a[i - 1]) == char.ToLower(b[j - 1]) ? 0 : 1));
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
