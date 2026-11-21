namespace PhoneFiles.Core.Semantic;

/// <summary>What the UI should offer to do with a node.</summary>
public enum NodeAction
{
    Backup,
    ShowOnly,
    Ignore,
}

/// <summary>Sensitivity class, derived from the rule/baseline, not from file content.</summary>
public enum RiskLevel
{
    ReadOnly,
    Private,
    Unknown,
}

/// <summary>Where a label came from. Rules enrich; they never replace probe facts.</summary>
public enum DecisionSource
{
    Rule,
    Basic,
    Unclassified,
}

/// <summary>Observed read capability of a directory (never inferred from visibility).</summary>
public enum Accessibility
{
    NotProbed,
    Readable,
    VisibleOnly,
    EnumerableOnly,
    NotAFolder,
}

/// <summary>Device facts used to decide which rules apply. No identifiers, no paths.</summary>
public sealed record DeviceContext(
    string OsFamily = "android",
    string Channel = "mtp",
    string? RomFamily = null,
    int? Sdk = null,
    string? AppPackage = null);

/// <summary>A rule that matched a node, kept verbatim for transparency/audit.</summary>
public sealed record MatchedRule(
    string RuleId,
    string NodePattern,
    string Label,
    RiskLevel Risk,
    NodeAction DefaultAction,
    double Confidence,
    IReadOnlyList<string> Evidence);

/// <summary>
/// The semantic layer's verdict on one node. Every field is either observed
/// (Accessibility, DisplayName, Size) or explicitly attributed to a source
/// (Rule / Basic / Unclassified) so the UI can show "why".
/// </summary>
public sealed record NodeDecision(
    string RelativeName,
    string DisplayName,
    bool IsDirectory,
    ulong Size,
    string Label,
    RiskLevel Risk,
    NodeAction Action,
    double Confidence,
    DecisionSource Source,
    Accessibility Accessibility,
    string? ProbeReason,
    IReadOnlyList<MatchedRule> MatchedRules);
