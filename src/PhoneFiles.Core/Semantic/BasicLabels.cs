namespace PhoneFiles.Core.Semantic;

/// <summary>
/// Offline, dependency-free label map used when no rule matches or the rule
/// store is empty. This is the "zero rules still work" guarantee: a cold-start
/// user sees meaningful Chinese labels and never an empty screen.
/// Matching is on the WPD relative display name only — no filesystem paths.
/// </summary>
public static class BasicLabels
{
    private sealed record Entry(string Pattern, string Label, RiskLevel Risk, NodeAction Action);

    // Ordered: more specific patterns first. Patterns are case-insensitive.
    private static readonly Entry[] Entries =
    {
        new("Android/data/*/*", "应用私有数据（通常受限）", RiskLevel.Private, NodeAction.ShowOnly),
        new("Android/obb/*/*", "应用扩展数据（通常受限）", RiskLevel.Private, NodeAction.ShowOnly),
        new("Android/data/*", "应用私有数据（通常受限）", RiskLevel.Private, NodeAction.ShowOnly),
        new("Android/obb/*", "应用扩展数据（通常受限）", RiskLevel.Private, NodeAction.ShowOnly),
        new("Android/data", "应用私有数据根目录", RiskLevel.Private, NodeAction.ShowOnly),
        new("Android/obb", "应用扩展数据根目录", RiskLevel.Private, NodeAction.ShowOnly),
        new("DCIM/Camera", "相机照片与视频", RiskLevel.ReadOnly, NodeAction.Backup),
        new("DCIM/*", "相机/相册目录", RiskLevel.ReadOnly, NodeAction.Backup),
        new("DCIM", "相机照片目录", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Pictures/Screenshots", "截图", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Pictures/*", "图片目录", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Pictures", "图片根目录", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Download", "下载", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Downloads", "下载", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Recordings", "录音", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Movies", "视频", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Music", "音乐", RiskLevel.ReadOnly, NodeAction.Backup),
        new("Documents", "文档", RiskLevel.ReadOnly, NodeAction.Backup),
    };

    /// <summary>Returns the first matching basic label, or null when unclassified.</summary>
    public static (string Label, RiskLevel Risk, NodeAction Action)? Match(string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName)) return null;
        var name = relativeName.Replace('\\', '/').Trim('/');
        foreach (var e in Entries)
        {
            if (GlobMatch(e.Pattern, name))
                return (e.Label, e.Risk, e.Action);
        }
        return null;
    }

    /// <summary>
    /// Minimal glob: '*' matches a single path segment, '?' matches one char.
    /// Kept here (not in rules) so basic labels need no dependency.
    /// </summary>
    public static bool GlobMatch(string pattern, string name)
    {
        var p = pattern.Split('/');
        var n = name.Split('/');
        if (p.Length != n.Length) return false;
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == "*") continue;
            if (!SegmentGlob(p[i], n[i])) return false;
        }
        return true;
    }

    private static bool SegmentGlob(string pattern, string text)
    {
        if (!pattern.Contains('?')) return string.Equals(pattern, text, StringComparison.OrdinalIgnoreCase);
        if (pattern.Length != text.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '?' && char.ToLowerInvariant(pattern[i]) != char.ToLowerInvariant(text[i]))
                return false;
        }
        return true;
    }
}
