namespace PhoneFiles.Core.Semantic;

/// <summary>
/// Turns live transport nodes into user-facing decisions. Rules enrich but
/// never override observed facts; "visible" is never treated as "readable".
/// </summary>
public sealed class SemanticEngine
{
    private readonly ITransport _transport;
    private readonly RuleStore _rules;

    public SemanticEngine(ITransport transport, RuleStore rules)
    {
        _transport = transport;
        _rules = rules;
    }

    /// <summary>
    /// Analyze a directory level recursively (CLI/batch use). Directories are
    /// probed (bounded) to decide whether they can actually be archived; files
    /// are classified by name.
    /// </summary>
    public IAsyncEnumerable<NodeDecision> AnalyzeAsync(
        IFileSystemNode parent,
        DeviceContext ctx,
        string relativePrefix = "",
        bool probeDirectories = true,
        CancellationToken ct = default)
        => AnalyzeLevelAsync(parent, ctx, relativePrefix, probeDirectories, maxDepth: 3, depth: 0, ct);

    /// <summary>
    /// Analyze a SINGLE node and return its decision. Public so a UI can lazily
    /// load a directory: it enumerates children via the transport itself and
    /// calls this per child, retaining the original IFileSystemNode next to the
    /// decision (archiving flows through opaque IDs, never paths).
    /// </summary>
    public async Task<NodeDecision> AnalyzeNodeAsync(
        IFileSystemNode node,
        DeviceContext ctx,
        string relativePrefix = "",
        bool probeDirectories = true,
        CancellationToken ct = default)
    {
        var rel = string.IsNullOrEmpty(relativePrefix) ? node.DisplayName : $"{relativePrefix}/{node.DisplayName}";
        var matched = new List<MatchedRule>();
        string? label = null;
        RiskLevel risk = RiskLevel.Unknown;
        NodeAction action = NodeAction.ShowOnly;
        double confidence = 0;
        DecisionSource source = DecisionSource.Unclassified;

        foreach (var (rule, cand) in _rules.ApplicableCandidates(ctx))
        {
            if (!BasicLabels.GlobMatch(cand.NodePattern, rel)) continue;
            matched.Add(new MatchedRule(
                rule.Id, cand.NodePattern, cand.Label,
                ParseRisk(cand.Risk), ParseAction(cand.DefaultAction),
                rule.Confidence, rule.Evidence));
        }
        if (matched.Count > 0)
        {
            var best = matched.OrderByDescending(m => m.Confidence).First();
            label = best.Label; risk = best.Risk; action = best.DefaultAction;
            confidence = best.Confidence; source = DecisionSource.Rule;
        }
        else if (BasicLabels.Match(rel) is { } basic)
        {
            label = basic.Label; risk = basic.Risk; action = basic.Action;
            confidence = 0.5; source = DecisionSource.Basic;
        }

        var accessibility = Accessibility.NotProbed;
        string? probeReason = null;
        if (node.IsDir)
        {
            if (probeDirectories)
            {
                var probe = await _transport.ProbeAsync(node, ct);
                accessibility = probe switch
                {
                    { Accessible: true } => Accessibility.Readable,
                    { CanEnumerate: true } => Accessibility.EnumerableOnly,
                    _ => Accessibility.VisibleOnly,
                };
                probeReason = probe.Reason;
            }
            else accessibility = Accessibility.NotProbed;
        }
        else accessibility = Accessibility.NotAFolder;

        // A restricted directory must never be offered for backup.
        if (action == NodeAction.Backup && accessibility is Accessibility.VisibleOnly or Accessibility.EnumerableOnly)
            action = NodeAction.ShowOnly;

        label ??= node.DisplayName;
        return new NodeDecision(
            rel, node.DisplayName, node.IsDir, (ulong)node.Size,
            label, risk, action, confidence, source,
            accessibility, probeReason, matched);
    }

    private async IAsyncEnumerable<NodeDecision> AnalyzeLevelAsync(
        IFileSystemNode parent,
        DeviceContext ctx,
        string relativePrefix,
        bool probeDirectories,
        int maxDepth,
        int depth,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var node in _transport.EnumerateAsync(parent, ct))
        {
            var decision = await AnalyzeNodeAsync(node, ctx, relativePrefix, probeDirectories, ct).ConfigureAwait(false);
            yield return decision;

            // Recurse into readable/enumerable/unprobed directories. A VisibleOnly
            // node is never descended into — that would re-introduce the very
            // "visible implies accessible" bug the probe exists to prevent.
            if (node.IsDir && depth < maxDepth &&
                decision.Accessibility is Accessibility.Readable or Accessibility.EnumerableOnly or Accessibility.NotProbed)
            {
                await foreach (var child in AnalyzeLevelAsync(node, ctx, decision.RelativeName, probeDirectories,
                                   maxDepth, depth + 1, ct).ConfigureAwait(false))
                    yield return child;
            }
        }
    }

    private static RiskLevel ParseRisk(string s) => s switch
    {
        "read_only" => RiskLevel.ReadOnly,
        "private" => RiskLevel.Private,
        _ => RiskLevel.Unknown,
    };

    private static NodeAction ParseAction(string? s) => s switch
    {
        "backup" => NodeAction.Backup,
        "ignore" => NodeAction.Ignore,
        _ => NodeAction.ShowOnly,
    };
}
