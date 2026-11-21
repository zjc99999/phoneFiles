using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PhoneFiles.Core.Semantic;

/// <summary>Persisted shape of one rule.yml (only fields the engine needs).</summary>
public sealed class RuleDocument
{
    public RuleBody Rule { get; set; } = new();
}

public sealed class RuleBody
{
    public string Id { get; set; } = "";
    public int Priority { get; set; }
    public RuleMatch Match { get; set; } = new();
    public List<RuleCandidate> Candidates { get; set; } = new();
    public double Confidence { get; set; }
    public List<string> Evidence { get; set; } = new();
    public string? VerifiedAt { get; set; }
    public string Version { get; set; } = "";
    public string Status { get; set; } = "canary";
}

public sealed class RuleMatch
{
    public string OsFamily { get; set; } = "android";
    public List<string>? RomFamily { get; set; }
    public string? SdkRange { get; set; }
    public string Channel { get; set; } = "mtp";
    public string? AppPackage { get; set; }
}

public sealed class RuleCandidate
{
    public string NodePattern { get; set; } = "";
    public string Label { get; set; } = "";
    public string Risk { get; set; } = "unknown";
    public string? DefaultAction { get; set; }
}

/// <summary>
/// Loads YAML rules from a directory. A missing/empty directory is legal — the
/// engine still runs on basic labels + live probes. Bad files are skipped with a
/// recorded reason so one broken rule never blocks the whole store.
/// </summary>
public sealed class RuleStore
{
    private readonly List<LoadedRule> _rules = new();
    public IReadOnlyList<string> LoadWarnings { get; }

    private sealed record LoadedRule(RuleBody Body, int Order);

    public RuleStore(IEnumerable<string> ymlFiles)
    {
        var warnings = new List<string>();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        int order = 0;
        foreach (var file in ymlFiles)
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var doc = deserializer.Deserialize<RuleDocument>(yaml);
                if (doc?.Rule is null || string.IsNullOrWhiteSpace(doc.Rule.Id))
                {
                    warnings.Add($"{Path.GetFileName(file)}: missing rule.id");
                    continue;
                }
                _rules.Add(new LoadedRule(doc.Rule, order++));
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        LoadWarnings = warnings;
        _rules.Sort((a, b) => b.Body.Priority.CompareTo(a.Body.Priority));
    }

    /// <summary>Load every *.yml in a directory (non-recursive). Missing dir = empty store.</summary>
    public static RuleStore FromDirectory(string directory)
    {
        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.yml", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        return new RuleStore(files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>All candidates whose match block applies to this device, flattened.</summary>
    public IEnumerable<(RuleBody Rule, RuleCandidate Candidate)> ApplicableCandidates(DeviceContext ctx)
    {
        foreach (var loaded in _rules)
        {
            var r = loaded.Body;
            if (!MatchApplies(r.Match, ctx)) continue;
            foreach (var c in r.Candidates) yield return (r, c);
        }
    }

    private static bool MatchApplies(RuleMatch m, DeviceContext ctx)
    {
        if (!string.Equals(m.OsFamily, ctx.OsFamily, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(m.Channel, ctx.Channel, StringComparison.OrdinalIgnoreCase)) return false;
        if (m.RomFamily is { Count: > 0 } && ctx.RomFamily is not null &&
            !m.RomFamily.Any(x => string.Equals(x, ctx.RomFamily, StringComparison.OrdinalIgnoreCase))) return false;
        if (m.AppPackage is not null && !string.Equals(m.AppPackage, ctx.AppPackage, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(m.SdkRange) && ctx.Sdk is int sdk && !SdkInRange(m.SdkRange, sdk)) return false;
        return true;
    }

    private static bool SdkInRange(string range, int sdk)
    {
        foreach (var part in range.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(">=")) { if (sdk < int.Parse(part[2..])) return false; }
            else if (part.StartsWith("<=")) { if (sdk > int.Parse(part[2..])) return false; }
            else if (part.StartsWith(">")) { if (sdk <= int.Parse(part[1..])) return false; }
            else if (part.StartsWith("<")) { if (sdk >= int.Parse(part[1..])) return false; }
            else if (part.Contains('-'))
            {
                var bounds = part.Split('-', 2);
                if (sdk < int.Parse(bounds[0]) || sdk > int.Parse(bounds[1])) return false;
            }
            else if (int.TryParse(part, out var exact) && sdk != exact) return false;
        }
        return true;
    }
}
