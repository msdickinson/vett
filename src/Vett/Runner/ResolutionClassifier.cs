using System.Text.RegularExpressions;

namespace Vett.Runner;

/// <summary>
/// Classifies a completed benchmark instance into one of five resolution buckets.
/// Precedence: NOT_MODELABLE → TIMEOUT → ABSTAIN → PASS → FALSE_CONFIDENCE.
/// </summary>
public static class ResolutionClassifier
{
    public const string NotModelable    = "NOT_MODELABLE";
    public const string Timeout         = "TIMEOUT";
    public const string Abstain         = "ABSTAIN";
    public const string Pass            = "PASS";
    public const string FalseConfidence = "FALSE_CONFIDENCE";

    private static readonly Regex AbstainRe = new(
        @"^\s*(i (can'?t|cannot|am unable to)|giving up|need human|cannot solve this)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly HashSet<string> TimeoutReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "max_iterations", "wall_time", "crash", "kill", "aeon_down", "oom",
    };

    /// <summary>
    /// Classify one instance result. Caller must have populated:
    ///   GenesisAbstainReason, EndReason, LastAssistantMessage, TestsPassed.
    /// TestsPassed is null when eval results haven't been joined yet — treated as false.
    /// </summary>
    public static string Classify(InstanceResult r)
    {
        if (!string.IsNullOrEmpty(r.GenesisAbstainReason))
            return NotModelable;

        if (TimeoutReasons.Contains(r.EndReason))
            return Timeout;

        if (r.LastAssistantMessage is not null && AbstainRe.IsMatch(r.LastAssistantMessage))
            return Abstain;

        if (r.TestsPassed == true)
            return Pass;

        return FalseConfidence;
    }

    /// <summary>
    /// Classify all instances in a summary, mutating Resolution on each.
    /// </summary>
    public static void ClassifyAll(IEnumerable<InstanceResult> instances)
    {
        foreach (var r in instances)
            r.Resolution = Classify(r);
    }
}
