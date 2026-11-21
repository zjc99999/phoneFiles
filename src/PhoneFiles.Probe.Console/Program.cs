using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PhoneFiles.Core;
using PhoneFiles.Core.Semantic;
using PhoneFiles.Transport.Mtp;

// Spike probe: enumerate a connected MTP device's object tree (read-only),
// and optionally emit a DESENSITIZED observation JSON per rules/schema/observation.schema.json.
// NO file contents, NO filenames, NO account hashes leave this machine.

var listCmd = new CommandDef("list", "List connected MTP devices");
var treeCmd = new CommandDef("tree", "Print the device object tree (read-only)");
treeCmd.Options["--device"] = "Device PnP id (default: first device)";
treeCmd.Options["--max-depth"] = "Maximum recursion depth (default 3)";
treeCmd.Options["--max-nodes"] = "Safety cap on printed nodes (default 200)";
var observeCmd = new CommandDef("observe", "Emit a desensitized observation JSON");
observeCmd.Options["--device"] = "Device PnP id (default: first device)";
observeCmd.Options["--oem"] = "OEM identifier (default unknown)";
observeCmd.Options["--model"] = "ro.product.model (default unknown)";
observeCmd.Options["--os"] = "Android version (default unknown)";
observeCmd.Options["--sdk"] = "Android API level (default 0)";
observeCmd.Options["--region"] = "Region code (default unknown)";
observeCmd.Options["--out"] = "Output JSON file (default: stdout)";
observeCmd.Options["--config"] = "Path to observe-config.json (default: auto-discover config/observe-config.json)";
observeCmd.Options["--from-adb"] = "Fill oem/model/os/sdk/romBuild from `adb shell getprop` (opt-in, read-only)";
observeCmd.Options["--adb"] = "Path to adb.exe (used with --from-adb; auto-detected otherwise)";
var semanticCmd = new CommandDef("semantic", "Print the semantic decision tree for one level (labels + live-accessibility probe)");
semanticCmd.Options["--device"] = "Device PnP id (default: first device)";
semanticCmd.Options["--volume"] = "Only a storage volume whose display name contains this (default: first)";
semanticCmd.Options["--path"] = "Relative node path to analyze, e.g. DCIM (default: volume root)";
semanticCmd.Options["--rules"] = "Directory of rule yml files (default: rules/rules; missing dir = zero-rule mode)";
semanticCmd.Options["--sdk"] = "Android API level for rule matching (default from --from-adb or 0)";
semanticCmd.Options["--rom"] = "ROM family for rule matching (e.g. miui)";
semanticCmd.Options["--from-adb"] = "Auto-fill sdk/rom from read-only adb getprop";
semanticCmd.Options["--no-probe"] = "Skip the live accessibility probe (fast, offline label tree only)";
var selfTestCmd = new CommandDef("selftest", "Offline regression: zero-rule mode + visible≠readable on synthetic nodes (no device needed)");

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("Usage: PhoneFiles.Probe.Console <list|tree|observe|semantic|selftest> [options]");
    Console.WriteLine("  list                              List connected MTP devices");
    Console.WriteLine("  tree   [--device X] [--max-depth N] [--max-nodes N]");
    Console.WriteLine("  observe [--device X] [--oem X] [--model X] [--os X] [--sdk N] [--region X] [--out DIR] [--config FILE]");
    Console.WriteLine("          [--from-adb [--adb PATH]]   <- auto-fill device metadata from read-only getprop");
    Console.WriteLine("  semantic [--device X] [--volume X] [--path REL] [--rules DIR] [--sdk N] [--rom X] [--no-probe]");
    Console.WriteLine("          Print labels + live accessibility; works with an empty rule dir (zero-rule mode).");
    Console.WriteLine("  selftest                          Offline regression (no device): zero rules + visible≠readable.");
    Console.WriteLine("Privacy: only directory names + coarse buckets + extension distribution are ever emitted.");
    Console.WriteLine("Config:  observe targets/buckets/root-mapping come from config/observe-config.json (community editable).");
    return 0;
}

var command = argv[0];
var opts = ParseOptions(argv.Skip(1).ToArray());
try
{
    switch (command)
    {
        case "list": await ListAsync(); break;
        case "tree": await TreeAsync(opts); break;
        case "observe": await ObserveAsync(opts); break;
        case "semantic": await SemanticAsync(opts); break;
        case "selftest": return await SelfTestAsync();
        default: Console.Error.WriteLine($"Unknown command: {command}"); return 1;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    if (ex.InnerException is not null) Console.Error.WriteLine($"  inner: {ex.InnerException.Message}");
    return 2;
}

static async Task<int> SelfTestAsync()
{
    Console.WriteLine("=== phoneFiles semantic-layer offline regression (no device) ===");
    int failures = 0;
    void Check(bool ok, string label)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok) failures++;
    }

    // 1) BasicLabels glob correctness, including the 3-segment WeChat path.
    Check(BasicLabels.Match("DCIM/Camera")?.Label == "相机照片与视频", "基础标签: DCIM/Camera -> 相机照片与视频");
    Check(BasicLabels.Match("Download")?.Label == "下载", "基础标签: Download -> 下载");
    Check(BasicLabels.Match("Android/data/com.tencent.mm/MicroMsg") is { Risk: RiskLevel.Private },
        "基础标签: Android/data/.../MicroMsg -> private");

    // 2) Rule matching — real yml via RuleStore. Resolve dir both in-repo and from packaged exe.
    var rules = RuleStore.FromDirectory(ResolveRulesDir());
    var ctx = new DeviceContext(RomFamily: "miui", Sdk: 30);
    var miuiHits = rules.ApplicableCandidates(ctx)
        .Where(x => BasicLabels.GlobMatch(x.Candidate.NodePattern, "DCIM/Camera")).ToList();
    Check(miuiHits.Any(), "规则加载: rules/rules 中 DCIM/Camera 有命中候选");
    Check(rules.LoadWarnings.Count == 0, $"规则加载: 0 个文件跳过 (实际 {rules.LoadWarnings.Count})");
    foreach (var w in rules.LoadWarnings) Console.WriteLine($"    warn: {w}");

    // 3) Fake transport mirroring GT7 Pro: MicroMsg visible+enumerable but unreadable.
    var fake = new FakeTransport();
    var engine = new SemanticEngine(fake, RuleStore.FromDirectory("__nonexistent_zero_rules__"));
    var decisions = new List<NodeDecision>();
    await foreach (var d in engine.AnalyzeAsync(fake.Root, ctx)) decisions.Add(d);

    var micro = decisions.FirstOrDefault(d => d.RelativeName.EndsWith("MicroMsg", StringComparison.OrdinalIgnoreCase));
    Check(micro is not null, "GT7 Pro 样本: MicroMsg 节点被枚举");
    Check(micro?.Accessibility == Accessibility.EnumerableOnly, "GT7 Pro 样本: MicroMsg 判定为可列不可读");
    Check(micro?.Action == NodeAction.ShowOnly, "GT7 Pro 样本: 受限目录动作=ShowOnly (绝不承诺备份)");
    Check(micro?.Source == DecisionSource.Basic, "零规则模式: MicroMsg 仍得到基础标签 (可空规则运行)");

    var camera = decisions.FirstOrDefault(d => d.RelativeName.EndsWith("Camera", StringComparison.OrdinalIgnoreCase));
    Check(camera?.Accessibility == Accessibility.Readable, "GT7 Pro 样本: Camera 判定为可归档");
    Check(camera?.Action == NodeAction.Backup, "GT7 Pro 样本: 可读相机目录动作=backup");
    Check(camera?.Source == DecisionSource.Basic, "零规则模式: Camera 得到基础标签");

    Console.WriteLine(failures == 0
        ? "=== ALL PASS — 可空规则运行 + 可见≠可读 两条硬约束成立 ==="
        : $"=== {failures} FAILURE(S) ===");
    return failures == 0 ? 0 : 3;
}

static async Task ListAsync()
{
    await using var t = new MtpTransport();
    var devices = await t.ListDevicesAsync();
    if (devices.Count == 0) { Console.WriteLine("No MTP devices found. Connect an Android phone in file-transfer (MTP) mode."); return; }
    foreach (var d in devices)
        Console.WriteLine($"{d.Id}\n  name: {d.DisplayName}\n  maker: {d.Manufacturer}\n  desc: {d.Description}");
}

static async Task TreeAsync(Dictionary<string, string> opts)
{
    await using var t = new MtpTransport();
    var dev = await ResolveDeviceAsync(t, Get(opts, "device"));
    if (dev is null) return;
    await t.ConnectAsync(dev);
    var root = await t.GetRootAsync();
    int maxDepth = int.TryParse(Get(opts, "max-depth"), out var d) ? d : 3;
    int maxNodes = int.TryParse(Get(opts, "max-nodes"), out var n) ? n : 200;
    await PrintTreeAsync(t, root, 0, maxDepth, maxNodes, new PrintCtx());
}

static async Task SemanticAsync(Dictionary<string, string> opts)
{
    // Rule store: a missing/empty directory is legal — zero-rule mode still
    // yields basic labels + live probes.
    var rulesDir = opts.TryGetValue("rules", out var rd) ? rd : ResolveRulesDir();
    var rules = RuleStore.FromDirectory(rulesDir);
    Console.WriteLine($"Rule store: {rules.LoadWarnings.Count} skipped file(s) from '{rulesDir}'.");
    foreach (var w in rules.LoadWarnings) Console.WriteLine($"  warn: {w}");

    if (opts.ContainsKey("from-adb")) PhoneFiles.Probe.Console.AdbMetadata.TryLoad(opts, out _);
    var ctx = new DeviceContext(
        Channel: "mtp",
        RomFamily: string.IsNullOrWhiteSpace(Get(opts, "rom")) ? null : Get(opts, "rom"),
        Sdk: int.TryParse(Get(opts, "sdk"), out var s) && s > 0 ? s : null);

    await using var t = new MtpTransport();
    var dev = await ResolveDeviceAsync(t, Get(opts, "device"));
    if (dev is null) return;
    await t.ConnectAsync(dev);

    var volumes = await t.ListVolumesAsync();
    if (volumes.Count == 0) { Console.Error.WriteLine("No storage volumes found."); return; }
    var vol = string.IsNullOrEmpty(Get(opts, "volume"))
        ? volumes[0]
        : volumes.FirstOrDefault(v => v.DisplayName.Contains(Get(opts, "volume"), StringComparison.OrdinalIgnoreCase)) ?? volumes[0];
    Console.WriteLine($"Volume: {vol.DisplayName} ({vol.StorageType ?? "unknown"})");

    var volNode = new VolumeRootNode(t, vol.OpaqueId, vol.DisplayName);
    IFileSystemNode target = volNode;
    var path = Get(opts, "path");
    if (!string.IsNullOrEmpty(path))
    {
        var found = await FindWithinVolumeAsync(t, volNode, path.Replace('\\', '/'));
        if (found is null) { Console.Error.WriteLine($"Path '{path}' not found in this volume."); return; }
        target = found;
    }

    var engine = new SemanticEngine(t, rules);
    bool noProbe = opts.ContainsKey("no-probe");
    Console.WriteLine($"--- semantic tree at '{Get(opts, "path", "<volume root>")}' (probe={!noProbe}) ---");
    await foreach (var d in engine.AnalyzeAsync(target, ctx, path, probeDirectories: !noProbe))
    {
        var acc = d.Accessibility switch
        {
            Accessibility.Readable => "可归档",
            Accessibility.VisibleOnly => "可见不可读",
            Accessibility.EnumerableOnly => "可列出但不可读",
            Accessibility.NotAFolder => "-",
            _ => "未探测",
        };
        var src = d.Source switch
        {
            DecisionSource.Rule => "规则",
            DecisionSource.Basic => "基础",
            _ => "未分类",
        };
        Console.WriteLine($"[{(d.IsDirectory ? "D" : "F")}] {d.RelativeName}");
        Console.WriteLine($"     标签: {d.Label}  | 来源: {src}  | 置信: {d.Confidence:F2}  | 风险: {d.Risk}  | 动作: {d.Action}");
        Console.WriteLine($"     可达: {acc}{(d.ProbeReason is null ? "" : $" ({d.ProbeReason})")}  规则命中: {d.MatchedRules.Count}");
    }
}

static async Task ObserveAsync(Dictionary<string, string> opts)
{
    // Load community-maintained config (targets / buckets / root mapping).
    ObserveConfig config;
    try { config = ObserveConfig.Load(Get(opts, "config")); }
    catch (Exception ex) { Console.Error.WriteLine($"Config error: {ex.Message}"); return; }
    Console.WriteLine($"Loaded config v{config.Version} with {config.Targets.Count} targets.");

    // Optional: auto-fill device metadata from read-only `adb getprop`.
    if (opts.ContainsKey("from-adb"))
    {
        var ok = PhoneFiles.Probe.Console.AdbMetadata.TryLoad(opts, out var summary);
        Console.WriteLine($"--from-adb: {summary}");
        if (!ok) Console.WriteLine("  (you can still pass --oem/--model/--os/--sdk manually)");
    }

    await using var t = new MtpTransport();
    var dev = await ResolveDeviceAsync(t, Get(opts, "device"));
    if (dev is null) return;
    await t.ConnectAsync(dev);

    // ★ Each storage volume gets its OWN observation. We never merge volumes:
    // same-named folders (Download, Recordings) exist on both and merging them
    // would hide which volume a finding came from.
    var volumes = await t.ListVolumesAsync();
    if (volumes.Count == 0) { Console.Error.WriteLine("No storage volumes found."); return; }

    var outDir = Get(opts, "out"); // when set, treat as directory; write one file per volume
    if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

    var results = new JsonArray();
    foreach (var vol in volumes)
    {
        var obs = await BuildObservationAsync(t, dev, vol, opts, config);
        results.Add(obs);
        var volJson = obs.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrEmpty(outDir))
        {
            var safe = string.Join("_", vol.DisplayName.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(outDir, $"obs_{safe}.json");
            await File.WriteAllTextAsync(path, volJson);
            Console.WriteLine($"Wrote {path}");
        }
    }

    if (string.IsNullOrEmpty(outDir))
        Console.WriteLine(results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

static async Task<JsonObject> BuildObservationAsync(MtpTransport t, DeviceRef dev, VolumeInfo vol, Dictionary<string, string> opts, ObserveConfig config)
{
    // Find nodes WITHIN this volume only — start from the volume node itself.
    var volNode = new VolumeRootNode(t, vol.OpaqueId, vol.DisplayName);

    // Targets come from config/observe-config.json (community editable).
    // We NEVER record child filenames — only relative node names + coarse buckets.
    var entries = new JsonArray();
    foreach (var target in config.Targets)
    {
        var name = target.NodePattern;
        var label = target.CanonicalLabel;
        var node = await FindWithinVolumeAsync(t, volNode, name);
        if (node is null)
        {
            // exists:false is a first-class observation.
            entries.Add(new JsonObject {
                ["nodePattern"] = name, ["canonicalLabel"] = label, ["exists"] = false
            });
            continue;
        }
        int childCount = 0;
        ulong totalSize = 0;
        var extCounts = new Dictionary<string, int>();
        if (node.IsDir)
        {
            await foreach (var child in node.EnumerateAsync())
            {
                childCount++;
                totalSize += child.Size;
                var ext = NormalizeExtension(Path.GetExtension(child.DisplayName));
                if (ext is not null)
                    extCounts[ext] = extCounts.TryGetValue(ext, out var c) ? c + 1 : 1;
            }
        }
        string bucket = config.BucketLabel(config.ChildCountBuckets, (ulong)childCount);
        string sizeBucket = config.BucketLabel(config.SizeBuckets, totalSize);
        var totalExt = Math.Max(1, extCounts.Values.Sum());
        var extDist = string.Join(",", extCounts.OrderByDescending(kv => kv.Value).Take(4)
            .Select(kv => $"{kv.Key}:{(kv.Value / (double)totalExt).ToString("F2", CultureInfo.InvariantCulture)}"));
        entries.Add(new JsonObject {
            ["nodePattern"] = name,
            ["canonicalLabel"] = label,
            ["exists"] = true,
            ["canEnumerate"] = node.IsDir,
            ["childCountBucket"] = bucket,
            ["sizeBucket"] = sizeBucket,
            ["fingerprint"] = $"dir:{name}|ext:{{{extDist}}}"
        });
    }

    // root is derived from WPD_STORAGE_TYPE via config mapping, never from the display name.
    var root = config.Root(vol.StorageType);
    var capacityBucket = vol.CapacityBytes switch
    {
        0 => null,
        < 2UL * 1024 * 1024 * 1024 => "<2GB",
        < 16UL * 1024 * 1024 * 1024 => "2-16GB",
        < 64UL * 1024 * 1024 * 1024 => "16-64GB",
        < 256UL * 1024 * 1024 * 1024 => "64-256GB",
        _ => "256GB+"
    };
    var volumeObj = new JsonObject {
        ["displayName"] = vol.DisplayName
    };
    if (vol.StorageType is not null) volumeObj["storageType"] = vol.StorageType;
    if (vol.FunctionalCategory is not null) volumeObj["functionalCategory"] = vol.FunctionalCategory;
    if (capacityBucket is not null) volumeObj["capacityBucket"] = capacityBucket;

    return new JsonObject {
        ["id"] = "obs_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant(),
        ["device"] = new JsonObject {
            ["oem"] = Get(opts, "oem", "unknown"), ["model"] = Get(opts, "model", "unknown"),
            ["osVersion"] = Get(opts, "os", "unknown"),
            ["sdk"] = int.TryParse(Get(opts, "sdk"), out var sdk) ? sdk : 0,
            ["region"] = Get(opts, "region", "unknown")
        },
        ["app"] = null,
        ["channel"] = "mtp",
        ["root"] = root,
        ["volume"] = volumeObj,
        ["entries"] = entries,
        ["privacy"] = new JsonObject {
            ["filenameSamples"] = false,
            ["deviceIdHash"] = "sha256:" + Sha256Hex(dev.Id),
            ["consent"] = "spike-probe-v1"
        },
        ["client"] = new JsonObject {
            ["appVersion"] = "0.1-spike",
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["locale"] = CultureInfo.CurrentUICulture.Name
        },
        ["submittedAt"] = DateTimeOffset.UtcNow.ToString("o")
    };
}

static async Task<DeviceRef?> ResolveDeviceAsync(ITransport t, string? device)
{
    var devices = await t.ListDevicesAsync();
    if (devices.Count == 0) { Console.Error.WriteLine("No MTP devices found."); return null; }
    if (string.IsNullOrEmpty(device)) return devices[0];
    var match = devices.FirstOrDefault(d => d.Id.Contains(device, StringComparison.OrdinalIgnoreCase)
                                          || d.DisplayName.Contains(device, StringComparison.OrdinalIgnoreCase));
    if (match is null) Console.Error.WriteLine($"Device '{device}' not found.");
    return match;
}

static async Task PrintTreeAsync(ITransport t, IFileSystemNode node, int depth, int maxDepth, int maxNodes, PrintCtx ctx)
{
    if (ctx.Printed >= maxNodes)
    {
        if (!ctx.TruncationReported) { Console.WriteLine($"  ... (stopped at {maxNodes} nodes)"); ctx.TruncationReported = true; }
        return;
    }
    ctx.Printed++;
    var indent = new string(' ', depth * 2);
    Console.WriteLine($"{indent}{(node.IsDir ? "[D]" : "[F]")} {node.DisplayName}  ({node.Size} bytes)");
    if (!node.IsDir || depth >= maxDepth) return;
    await foreach (var child in t.EnumerateAsync(node))
        await PrintTreeAsync(t, child, depth + 1, maxDepth, maxNodes, ctx);
}

// Match a relative node name strictly WITHIN one volume: the search starts at
// the volume node itself and never crosses into another volume. Same-named
// folders on two volumes are therefore kept distinct.
static async Task<IFileSystemNode?> FindWithinVolumeAsync(ITransport t, IFileSystemNode volumeRoot, string relative)
{
    var parts = relative.Split('/');
    return await MatchFromAsync(t, volumeRoot, parts);
}

static async Task<IFileSystemNode?> MatchFromAsync(ITransport t, IFileSystemNode start, string[] parts)
{
    IFileSystemNode current = start;
    foreach (var part in parts)
    {
        if (part == "*")
        {
            // Wildcard: descend into the first child directory. Used sparingly for OEM
            // variants where the middle segment varies (e.g. Android/data/*/files).
            IFileSystemNode? firstDir = null;
            await foreach (var child in t.EnumerateAsync(current))
            {
                if (child.IsDir) { firstDir = child; break; }
            }
            if (firstDir is null) return null;
            current = firstDir;
            continue;
        }
        IFileSystemNode? found = null;
        await foreach (var child in t.EnumerateAsync(current))
        {
            if (string.Equals(child.DisplayName, part, StringComparison.OrdinalIgnoreCase)) { found = child; break; }
        }
        if (found is null) return null;
        current = found;
    }
    return current;
}

// 扩展名规范化：只允许 [a-z0-9]、2-8 字符；异常/超长片段一律丢弃。
// 防止 .removed-20240201-192827 这类「伪扩展名」把文件名片段带出本机。
static string? NormalizeExtension(string raw)
{
    if (string.IsNullOrEmpty(raw) || raw.Length < 2 || raw.Length > 9) return null;
    var span = raw.AsSpan(1); // skip leading '.'
    foreach (var ch in span)
        if (!char.IsAsciiLetterOrDigit(ch)) return null;
    return span.ToString().ToLowerInvariant();
}

static string Sha256Hex(string input)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}

static Dictionary<string, string> ParseOptions(string[] argv)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < argv.Length; i++)
    {
        if (!argv[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = argv[i][2..];
        if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            result[key] = argv[++i];
        else result[key] = "true";
    }
    return result;
}

static string Get(Dictionary<string, string> opts, string key, string fallback = "") =>
    opts.TryGetValue(key, out var v) ? v : fallback;

// Resolve the rule directory both when running from the repo root (rules/rules)
// and when running a published binary (rules/ next to the exe).
static string ResolveRulesDir()
{
    var cwd = Path.Combine(Directory.GetCurrentDirectory(), "rules", "rules");
    if (Directory.Exists(cwd)) return cwd;
    var baseDir = AppContext.BaseDirectory;
    var fromExe = Path.Combine(baseDir, "rules");
    if (Directory.Exists(fromExe)) return fromExe;
    // neither exists: return the repo-relative path so zero-rule mode reports it honestly.
    return "rules/rules";
}

// Community-maintained observation config. Loaded from config/observe-config.json.
// Changing observation targets / buckets / root mapping requires NO C# rebuild.
sealed class ObserveConfig
{
    public int Version { get; private set; }
    public IReadOnlyList<Target> Targets { get; private set; } = Array.Empty<Target>();
    public IReadOnlyList<Bucket> ChildCountBuckets { get; private set; } = Array.Empty<Bucket>();
    public IReadOnlyList<Bucket> SizeBuckets { get; private set; } = Array.Empty<Bucket>();
    public Dictionary<string, string> RootMap { get; private set; } = new();
    public string RootDefault { get; private set; } = "unknown";

    public sealed record Target(string NodePattern, string CanonicalLabel);
    public sealed record Bucket(ulong Max, string Label);

    public static ObserveConfig Load(string? explicitPath)
    {
        var path = explicitPath;
        if (string.IsNullOrEmpty(path))
        {
            // Auto-discover: walk up from CWD and from the executable dir.
            var dirs = new List<string>();
            var cwd = new DirectoryInfo(Environment.CurrentDirectory);
            for (var d = cwd; d is not null; d = d.Parent) dirs.Add(d.FullName);
            var exeDir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var d = exeDir; d is not null; d = d.Parent) dirs.Add(d.FullName);
            foreach (var d in dirs.Distinct())
            {
                var candidate = Path.Combine(d, "config", "observe-config.json");
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new FileNotFoundException("observe-config.json not found. Pass --config or run from the repo root.");

        var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException("config root is not an object");
        var cfg = new ObserveConfig();
        cfg.Version = json["version"]?.GetValue<int>() ?? 1;

        var targets = new List<Target>();
        if (json["targets"] is JsonArray tarr)
        {
            foreach (var n in tarr)
            {
                if (n is not JsonObject o) continue;
                var pattern = o["nodePattern"]?.GetValue<string>();
                var label = o["canonicalLabel"]?.GetValue<string>() ?? "unknown";
                if (!string.IsNullOrEmpty(pattern)) targets.Add(new Target(pattern, label));
            }
        }
        if (targets.Count == 0) throw new InvalidDataException("config.targets is empty");
        cfg.Targets = targets;

        if (json["buckets"] is JsonObject b)
        {
            cfg.ChildCountBuckets = ReadBuckets(b["childCount"] as JsonArray);
            cfg.SizeBuckets = ReadBuckets(b["sizeBytes"] as JsonArray);
        }
        if (cfg.ChildCountBuckets.Count == 0 || cfg.SizeBuckets.Count == 0)
            throw new InvalidDataException("config.buckets.childCount / sizeBytes required");

        if (json["rootMapping"] is JsonObject rm)
        {
            foreach (var kv in rm)
            {
                if (kv.Key.StartsWith("$")) continue;
                if (kv.Key == "default") { cfg.RootDefault = kv.Value?.GetValue<string>() ?? "unknown"; continue; }
                if (kv.Value is not null) cfg.RootMap[kv.Key] = kv.Value.GetValue<string>();
            }
        }
        return cfg;
    }

    private static IReadOnlyList<Bucket> ReadBuckets(JsonArray? arr)
    {
        var list = new List<Bucket>();
        if (arr is null) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var max = o["max"]?.GetValue<ulong>() ?? 0;
            var label = o["label"]?.GetValue<string>();
            if (label is not null) list.Add(new Bucket(max, label));
        }
        return list;
    }

    public string BucketLabel(IReadOnlyList<Bucket> buckets, ulong value)
    {
        foreach (var b in buckets)
            if (value <= b.Max) return b.Label;
        return buckets.Count > 0 ? buckets[^1].Label : "unknown";
    }

    public string Root(string? storageType) =>
        storageType is not null && RootMap.TryGetValue(storageType, out var r) ? r : RootDefault;
}

sealed class CommandDef
{
    public CommandDef(string name, string description) { Name = name; Description = description; }
    public string Name { get; }
    public string Description { get; }
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
}

sealed class PrintCtx { public int Printed; public bool TruncationReported; }

// Offline fake transport mirroring the GT7 Pro observation: DCIM/Camera is
// enumerable+readable; Android/data/com.tencent.mm/MicroMsg is visible and
// enumerable but its file opens throw (scoped-storage restriction).
sealed class FakeTransport : ITransport
{
    public string Name => "fake";
    public FakeNode Root { get; }

    public FakeTransport()
    {
        var root = new FakeNode(this, "root", "volume", null, true, 0);
        var dcim = new FakeNode(this, "dcim", "DCIM", "root", true, 0);
        var camera = new FakeNode(this, "camera", "Camera", "dcim", true, 0) { Readable = true };
        camera.Children.Add(new FakeNode(this, "img", "IMG_0001.jpg", "camera", false, 1024) { Readable = true });
        var android = new FakeNode(this, "android", "Android", "root", true, 0);
        var data = new FakeNode(this, "data", "data", "android", true, 0);
        var mm = new FakeNode(this, "mm", "com.tencent.mm", "data", true, 0);
        var micro = new FakeNode(this, "micro", "MicroMsg", "mm", true, 0) { Readable = false };
        micro.Children.Add(new FakeNode(this, "tmp", "x.tmp", "micro", false, 512) { Readable = false });
        mm.Children.Add(micro);
        data.Children.Add(mm);
        android.Children.Add(data);
        dcim.Children.Add(camera);
        root.Children.Add(dcim);
        root.Children.Add(android);
        Root = root;
    }

    public Task<IReadOnlyList<DeviceRef>> ListDevicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DeviceRef>>(new[] { new DeviceRef("fake", "Fake Device") });
    public Task ConnectAsync(DeviceRef device, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IFileSystemNode> GetRootAsync(CancellationToken ct = default) => Task.FromResult<IFileSystemNode>(Root);
    public IAsyncEnumerable<IFileSystemNode> EnumerateAsync(IFileSystemNode node, CancellationToken ct = default)
        => EnumerateNodes(node, ct);
    private async IAsyncEnumerable<IFileSystemNode> EnumerateNodes(IFileSystemNode node,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fake = (FakeNode)node;
        foreach (var c in fake.Children) { ct.ThrowIfCancellationRequested(); yield return c; }
        await Task.CompletedTask;
    }
    public Task<ProbeResult> ProbeAsync(IFileSystemNode node, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fake = (FakeNode)node;
        if (!fake.IsDir) return Task.FromResult(ProbeResult.VisibleOnly("not a folder"));
        if (fake.Children.Count == 0) return Task.FromResult(ProbeResult.VisibleOnly("no children or enumeration blocked"));
        var firstFile = fake.Children.FirstOrDefault(c => !c.IsDir);
        if (firstFile is null) return Task.FromResult(ProbeResult.EnumerableOnly("no file in first nodes"));
        if (!firstFile.Readable) return Task.FromResult(ProbeResult.EnumerableOnly("read failed: UnauthorizedAccessException"));
        return Task.FromResult(ProbeResult.Full());
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public sealed class FakeNode : IFileSystemNode
    {
        private readonly FakeTransport _t;
        public FakeNode(FakeTransport t, string id, string name, string? parent, bool isDir, ulong size)
        { _t = t; OpaqueId = id; DisplayName = name; ParentId = parent; IsDir = isDir; Size = size; }
        public List<FakeNode> Children { get; } = new();
        public bool Readable { get; set; }
        public string OpaqueId { get; }
        public string DisplayName { get; }
        public string? ParentId { get; }
        public bool IsDir { get; }
        public ulong Size { get; }
        public DateTimeOffset? MTime => null;
        public Task<Stream> OpenReadAsync(CancellationToken ct = default)
            => Readable ? Task.FromResult<Stream>(new MemoryStream(new byte[] { 1 }))
                        : throw new UnauthorizedAccessException("scoped storage");
        public IAsyncEnumerable<IFileSystemNode> EnumerateAsync(CancellationToken ct = default)
            => _t.EnumerateAsync(this, ct);
    }
}

// A lightweight IFileSystemNode that represents a storage-volume root,
// so enumeration can start from the volume without re-reading its properties.
sealed class VolumeRootNode : IFileSystemNode
{
    private readonly ITransport _t;
    public VolumeRootNode(ITransport t, string opaqueId, string displayName)
    { _t = t; OpaqueId = opaqueId; DisplayName = displayName; }
    public string OpaqueId { get; }
    public string DisplayName { get; }
    public string? ParentId => null;
    public bool IsDir => true;
    public ulong Size => 0;
    public DateTimeOffset? MTime => null;
    public Task<Stream> OpenReadAsync(CancellationToken ct = default)
        => throw new InvalidOperationException("A volume root is a folder.");
    public IAsyncEnumerable<IFileSystemNode> EnumerateAsync(CancellationToken ct = default)
        => _t.EnumerateAsync(this, ct);
}
