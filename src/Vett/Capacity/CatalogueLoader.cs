using Vett.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Capacity;

/// <summary>
/// Loads capabilities.yaml, using the same search-path policy as profiles and
/// suites so there is one rule to remember rather than two.
/// </summary>
public static class CatalogueLoader
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Subdirectory searched, alongside profiles/ and suites/.</summary>
    public const string Subdir = "capabilities";

    /// <summary>Catalogue used when none is named.</summary>
    public const string DefaultName = "default";

    public static CapabilityCatalogue Load(string path)
    {
        var catalogue = D.Deserialize<CapabilityCatalogue>(File.ReadAllText(path))
                        ?? new CapabilityCatalogue();
        Validate(catalogue, path);
        return catalogue;
    }

    /// <summary>
    /// Resolve by name through the standard search dirs, returning the file
    /// it came from as well as the value.
    ///
    /// Both are returned together on purpose: resolving twice would re-probe
    /// the search dirs and could pick a DIFFERENT file if one appeared in
    /// between, so a caller that needs to report provenance must get it from
    /// the same lookup that produced the value.
    /// </summary>
    public static (CapabilityCatalogue? Value, string? Path) Resolve(string? nameOrPath = null)
        => Yaml.ResolveWithPath(nameOrPath ?? DefaultName, Subdir, Load);

    public static IEnumerable<string> SearchDirs() => Yaml.ResolveSearchDirs(Subdir);

    /// <summary>
    /// Structural checks that must hold before anything is scheduled against
    /// this catalogue. These throw rather than warn: a malformed catalogue
    /// would otherwise produce confident, wrong bindings at runtime, which is
    /// the exact failure mode the capability layer exists to remove.
    /// </summary>
    private static void Validate(CapabilityCatalogue catalogue, string path)
    {
        var seenProviders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var seenBudgets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in catalogue.Budgets)
        {
            if (string.IsNullOrWhiteSpace(b.Name))
                throw new InvalidOperationException($"{path}: a budget has no name");

            if (!seenBudgets.Add(b.Name))
            {
                // Two entries for one wallet: whichever the lookup happened to
                // find would silently set the limit, and the other would look
                // enforced while doing nothing.
                throw new InvalidOperationException(
                    $"{path}: budget '{b.Name}' is declared more than once");
            }

            if (b.LimitUsd is decimal lim && lim < 0)
                throw new InvalidOperationException(
                    $"{path}: budget '{b.Name}' has a negative limit_usd ({lim})");

            if (b.StaleAfterHours <= 0)
                throw new InvalidOperationException(
                    $"{path}: budget '{b.Name}' has stale_after_hours {b.StaleAfterHours}. A non-positive "
                    + "value would make every reading stale on arrival, shutting the budget permanently.");
        }

        foreach (var cap in catalogue.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(cap.Name))
                throw new InvalidOperationException($"{path}: a capability has no name");

            if (cap.Providers.Count == 0)
                throw new InvalidOperationException(
                    $"{path}: capability '{cap.Name}' declares no providers, so nothing could ever serve it");

            foreach (var p in cap.Providers)
            {
                if (string.IsNullOrWhiteSpace(p.Name))
                    throw new InvalidOperationException($"{path}: a provider of '{cap.Name}' has no name");

                if (string.IsNullOrWhiteSpace(p.Endpoint))
                    throw new InvalidOperationException($"{path}: provider '{p.Name}' has no endpoint");

                if (string.IsNullOrWhiteSpace(p.Model))
                    throw new InvalidOperationException($"{path}: provider '{p.Name}' names no model");

                if (!p.LocalityRaw.Equals("local", StringComparison.OrdinalIgnoreCase)
                    && !p.LocalityRaw.Equals("cloud", StringComparison.OrdinalIgnoreCase))
                {
                    // No default: guessing here is how a paid endpoint ends up
                    // charged against the local KV budget.
                    throw new InvalidOperationException(
                        $"{path}: provider '{p.Name}' has locality '{p.LocalityRaw}' — must be "
                        + "exactly 'local' or 'cloud'. This is never inferred, because the field "
                        + "that looks like it says so in a profile (llm.provider) does not.");
                }

                // Provider names key leases across processes. A duplicate would
                // silently pool two different endpoints into one budget.
                if (seenProviders.TryGetValue(p.Name, out var firstOwner))
                {
                    throw new InvalidOperationException(
                        $"{path}: provider name '{p.Name}' is used by both '{firstOwner}' and "
                        + $"'{cap.Name}'. Provider names key the capacity ledger, so duplicates "
                        + "would merge unrelated pools.");
                }
                seenProviders[p.Name] = cap.Name;

                // A named budget must exist. A typo would otherwise fall back
                // to the provider's own implicit budget, which has no limit —
                // so the misspelling reads as "unlimited" rather than as an
                // error, and the cap the operator thought they set never binds.
                if (!string.IsNullOrWhiteSpace(p.BudgetRaw) && catalogue.FindBudget(p.BudgetRaw) is null)
                {
                    var known = catalogue.Budgets.Count == 0
                        ? "none are declared"
                        : "known: " + string.Join(", ", catalogue.Budgets.Select(b => b.Name));
                    throw new InvalidOperationException(
                        $"{path}: provider '{p.Name}' draws on budget '{p.BudgetRaw}', which does not exist "
                        + $"({known}). An unknown budget name silently means UNLIMITED, so it is rejected.");
                }

                if (p.Locality == Locality.Local && p.PoolTokens is null)
                {
                    throw new InvalidOperationException(
                        $"{path}: local provider '{p.Name}' declares no pool_tokens. A local provider "
                        + "without a pool cannot be rationed, and an unrationed local pool is exactly "
                        + "the oversubscription this layer exists to prevent.");
                }
            }
        }
    }
}
