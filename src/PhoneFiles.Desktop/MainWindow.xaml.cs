using System.Collections.ObjectModel;
using System.IO;
using PhoneFiles.Core;
using PhoneFiles.Core.Archive;
using PhoneFiles.Core.Semantic;
using PhoneFiles.Transport.Mtp;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NodeAccess = PhoneFiles.Core.Semantic.Accessibility;

namespace PhoneFiles.Desktop;

/// <summary>A tree row: the original transport node + its semantic decision.</summary>
internal sealed class NodeRow
{
    public IFileSystemNode? Node { get; }
    public NodeDecision Decision { get; }
    public ObservableCollection<NodeRow> Children { get; } = new();
    public bool Loaded { get; set; }
    public bool IsPlaceholder { get; }
    public NodeRow(IFileSystemNode node, NodeDecision decision) { Node = node; Decision = decision; }
    private NodeRow() { IsPlaceholder = true; Decision = new NodeDecision("…", "…", false, 0, "…", RiskLevel.Unknown,
        NodeAction.Ignore, 0, DecisionSource.Unclassified, NodeAccess.NotProbed, null,
        Array.Empty<MatchedRule>()); }
    public static NodeRow Placeholder => new();

    public string DisplayText => IsPlaceholder ? "…" :
        $"{(Decision.IsDirectory ? "[D]" : "[F]")} {Decision.RelativeName}  — {Decision.Label} ({Decision.Action})";

    public override string ToString() => DisplayText;
}

public partial class MainWindow : Window
{
    private MtpTransport? _transport;
    private IReadOnlyList<DeviceRef> _devices = Array.Empty<DeviceRef>();
    private IReadOnlyList<VolumeInfo> _volumes = Array.Empty<VolumeInfo>();
    private SemanticEngine? _engine;
    private DeviceContext _ctx = new();
    private RuleStore? _rules;
    private CancellationTokenSource? _archiveCts;
    private NodeRow? _archiveSelection;

    public MainWindow()
    {
        InitializeComponent();
        Log("就绪。点击「刷新设备」开始。归档为只读复制，绝不删除/写回手机。");
        _ = RefreshDevicesAsync();
    }

    private void Log(string msg) =>
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var t = new MtpTransport();
            var list = await t.ListDevicesAsync();
            await t.DisposeAsync();
            _devices = list;
            DeviceCombo.ItemsSource = _devices;
            if (_devices.Count > 0) DeviceCombo.SelectedIndex = 0;
            StatusText.Text = _devices.Count == 0 ? "未发现设备" : $"发现 {_devices.Count} 台设备";
            Log($"设备扫描完成：{_devices.Count} 台。");
        }
        catch (Exception ex) { StatusText.Text = "扫描失败"; Log($"扫描失败：{ex.Message}"); }
    }

    private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceRef dev) { Log("请先选择一台设备。"); return; }
        try
        {
            _transport?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var t = new MtpTransport();
            await t.ConnectAsync(dev);
            _transport = t;
            _volumes = await t.ListVolumesAsync();
            VolumeCombo.ItemsSource = _volumes;
            if (_volumes.Count > 0) VolumeCombo.SelectedIndex = 0;
            var rulesDir = Path.Combine(AppContext.BaseDirectory, "rules");
            _rules = Directory.Exists(rulesDir) ? RuleStore.FromDirectory(rulesDir) : RuleStore.FromDirectory("rules/rules");
            _engine = new SemanticEngine(t, _rules);
            StatusText.Text = $"已连接 {dev.DisplayName} · {_volumes.Count} 个卷";
            Log($"已连接 {dev.DisplayName}，{_volumes.Count} 个卷；规则跳过 {_rules.LoadWarnings.Count} 项。");
        }
        catch (Exception ex) { StatusText.Text = "连接失败"; Log($"连接失败：{ex.Message}"); }
    }

    private void VolumeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { /* load on demand via LoadButton */ }

    private DeviceContext BuildContext()
    {
        int? sdk = int.TryParse(SdkBox.Text.Trim(), out var s) && s > 0 ? s : null;
        var rom = string.IsNullOrWhiteSpace(RomBox.Text) ? null : RomBox.Text.Trim();
        return new DeviceContext(Channel: "mtp", RomFamily: rom, Sdk: sdk);
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transport is null || _engine is null) { Log("请先连接设备。"); return; }
        if (VolumeCombo.SelectedItem is not VolumeInfo vol) { Log("请先选择存储卷。"); return; }
        _ctx = BuildContext();
        TreeView.Items.Clear();
        try
        {
            var volNode = new VolumeRootNode(_transport, vol.OpaqueId, vol.DisplayName);
            Log($"载入卷「{vol.DisplayName}」...");
            await foreach (var node in _transport.EnumerateAsync(volNode))
            {
                var decision = await _engine.AnalyzeNodeAsync(node, _ctx);
                TreeView.Items.Add(MakeRow(node, decision));
            }
            Log($"根层载入完成。");
        }
        catch (Exception ex) { Log($"载入失败：{ex.Message}"); }
    }

    private NodeRow MakeRow(IFileSystemNode node, NodeDecision decision)
    {
        var row = new NodeRow(node, decision);
        if (decision.IsDirectory)
        {
            // A placeholder forces the expander to appear; real children load on expand.
            row.Children.Add(NodeRow.Placeholder);
            row.Loaded = false;
        }
        return row;
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _archiveSelection = e.NewValue as NodeRow;
        if (_archiveSelection is null) return;
        var d = _archiveSelection.Decision;
        DetailName.Text = $"名称：{d.DisplayName}";
        DetailLabel.Text = $"标签：{d.Label}  |  来源：{d.Source}  |  置信：{d.Confidence:F2}";
        DetailMeta.Text = $"风险：{d.Risk}  |  动作：{d.Action}  |  大小：{d.Size:N0} B  |  命中规则：{d.MatchedRules.Count}";
        var acc = d.Accessibility switch
        {
            NodeAccess.Readable => "可归档（实测可读）",
            NodeAccess.VisibleOnly => "仅可见 — 不可读，不能归档",
            NodeAccess.EnumerableOnly => "可列举 — 不可读，不能归档",
            NodeAccess.NotAFolder => "文件",
            _ => "未探测",
        };
        DetailAccess.Text = $"可达性：{acc}{(d.ProbeReason is null ? "" : $"（{d.ProbeReason}）")}";

        bool canArchive = d.IsDirectory && d.Action == NodeAction.Backup &&
                          d.Accessibility is NodeAccess.Readable or NodeAccess.NotProbed;
        ArchiveSelectedButton.IsEnabled = canArchive;
    }

    private async void TreeView_OnExpanding(object sender, RoutedEventArgs e)  // called on TreeViewItem.Expanded
    {
        if (e.OriginalSource is TreeViewItem item && item.DataContext is NodeRow row && row.Loaded == false)
        {
            await LoadChildrenAsync(row);
        }
    }

    private async Task LoadChildrenAsync(NodeRow row)
    {
        if (_engine is null) return;
        row.Loaded = true;
        row.Children.Clear();
        try
        {
            await foreach (var child in _transport!.EnumerateAsync(row.Node!))
            {
                var decision = await _engine.AnalyzeNodeAsync(child, _ctx, row.Decision.RelativeName);
                row.Children.Add(MakeRow(child, decision));
            }
        }
        catch (Exception ex) { Log($"展开「{row.Decision.DisplayName}」失败：{ex.Message}"); }
    }

    private async void ArchiveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveSelection is null || _transport is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择归档输出文件夹（在下方位置新建子目录）",
            Filter = "文件夹|*.folder",
            FileName = "archive",
            ValidateNames = false,
            CheckFileExists = false,
            CheckPathExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        var dest = Path.Combine(Path.GetDirectoryName(dlg.FileName) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                Path.GetFileName(dlg.FileName));
        _archiveCts?.Cancel();
        _archiveCts = new CancellationTokenSource();
        var progress = new Progress<ArchiveProgress>(p =>
        {
            ArchiveProgress.Maximum = Math.Max(1, p.FilesTotal);
            ArchiveProgress.Value = p.FilesDone;
            ArchiveStatus.Text = $"{p.Phase}: {p.CurrentRelative}  ({p.FilesDone}/{p.FilesTotal}, {p.BytesDone / 1024 / 1024} MB)";
        });
        ArchiveSelectedButton.IsEnabled = false;
        try
        {
            var svc = new ArchiveService(_transport);
            var rel = _archiveSelection.Decision.RelativeName.Replace('/', Path.DirectorySeparatorChar);
            var result = await svc.ArchiveAsync(
                new[] { new ArchiveSelection(_archiveSelection.Node!, rel) },
                dest, progress, _archiveCts.Token);
            Log($"归档完成：{result.FilesCopied} 文件 / {result.BytesCopied / 1024 / 1024} MB / 失败 {result.FilesFailed}，清单 {result.ManifestPath}");
            ArchiveStatus.Text = $"完成：{result.FilesCopied} 文件";
        }
        catch (OperationCanceledException) { Log("归档已取消。"); ArchiveStatus.Text = "已取消"; }
        catch (Exception ex) { Log($"归档失败：{ex.Message}"); ArchiveStatus.Text = "失败"; }
        finally { ArchiveSelectedButton.IsEnabled = true; }
    }
}

/// <summary>Wraps a volume's opaque ID so enumeration can start at a volume.</summary>
internal sealed class VolumeRootNode : IFileSystemNode
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
    public Task<Stream> OpenReadAsync(CancellationToken ct = default) => throw new InvalidOperationException("A volume root is a folder.");
    public IAsyncEnumerable<IFileSystemNode> EnumerateAsync(CancellationToken ct = default) => _t.EnumerateAsync(this, ct);
}
