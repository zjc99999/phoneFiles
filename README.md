# PhoneFiles · 手机本地数据归档助手

> 把手机里的照片、截图、下载、录音搬回电脑，并给你一张能核对的清单——搬完敢删。

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?logo=windows)](https://www.microsoft.com/windows)
[![License: Code](https://img.shields.io/badge/license-Code%3A%20Apache--2.0-green)](LICENSE)
[![License: Data](https://img.shields.io/badge/license-Data%3A%20CC%20BY--SA%204.0-blue)](LICENSE-DATA)
[![Status](https://img.shields.io/badge/status-MVP-orange)](README.md#开发状态)

PhoneFiles 是一个 **只读、可核对、离线优先** 的 Windows 桌面工具：通过 MTP 连接 Android 手机，自动把常见中文语义目录（相机照片 / 截图 / 下载 / 录音）列出来，勾选后一键复制到电脑，并生成带 **SHA256 校验值** 的 UTF-8 清单（清单.csv）。**永不删除、永不修改手机上的任何数据。**

---

## 目录

- [这个工具解决什么问题](#这个工具解决什么问题)
- [核心特性](#核心特性)
- [快速开始](#快速开始)
- [桌面 UI 使用](#桌面-ui-使用phonefilesdesktop)
- [命令行探针使用](#命令行探针使用phonefilesprobeconsole)
- [项目结构](#项目结构)
- [架构设计](#架构设计)
- [两条硬约束（设计红线）](#两条硬约束设计红线)
- [规则库与社区观测](#规则库与社区观测)
- [配置参考](#配置参考)
- [开发与测试](#开发与测试)
- [打包发布](#打包发布)
- [开发状态与路线图](#开发状态与路线图)
- [隐私与数据红线](#隐私与数据红线)
- [许可证与商业化](#许可证与商业化)
- [参与贡献](#参与贡献)
- [文档索引](#文档索引)

---

## 这个工具解决什么问题

手机存储满了，你想把旧照片、聊天里的文件、录音备份到电脑再清理。但你不清楚这些东西在手机里的哪个目录；不同品牌、不同系统版本、不同 App 版本的位置还都不一样；数据线连上电脑后看到的只是一个陌生的设备图标。

PhoneFiles 做三件事：

1. **插线即见**：Windows + Android 用 MTP 连接后，自动把手机里常见的中文语义目录列出来，并标注标签、风险与可达性。
2. **只读归档**：勾选需要的目录，一键复制到电脑，附 `清单.csv` 与逐文件 SHA256 校验值——**永不删除、永不修改手机上的任何数据**。
3. **社区共建**：遇到没识别出来的目录，一键提交一份**脱敏观测**，帮规则库认识更多机型。

### 目标声明（范围，防止蔓延）

- **先行版本**：Windows 10/11 + Android（MTP 公共目录），只读 + 只复制。
- **ADB** 为 opt-in 高级模式，用于解锁 Android 下 `Android/data` 中可见的媒体，不进默认路径。
- **iOS 明确排除**（系统协议只允许访问照片，本工具的概念前提不成立）。
- **鸿蒙 NEXT** 后置为独立 adapter。
- 「先行完成 Android 版本」的准确含义是：**用最小成本验证「MTP 下到底能看到什么」这一生死假设**，而不是做一个完整的 Android 支持。

### 明确不做

- 删除 / 写入手机（MTP 删除无回收站，不可恢复）
- 自动清理手机空间
- 微信私有数据库解密（`EnMicroMsg.db` 为 SQLCipher 加密，不在承诺范围）
- 任何用户数据上云

---

## 核心特性

| 特性 | 说明 |
| --- | --- |
| **插线即见的语义树** | 连接后按卷列出目录树，自动打上「相机照片与视频 / 截图 / 下载 / 录音 / 应用私有数据」等中文语义标签 |
| **只读归档 + 校验清单** | 递归复制选中目录，逐文件 SHA256，输出 UTF-8 `清单.csv`（相对路径、大小、UTC 修改时间、哈希） |
| **可见 ≠ 可读的实测** | 对每个目录做有上限的只读能力探测（实测能否打开并读取文件），受限目录自动标灰、拒绝归档 |
| **零规则可运行** | 规则库为空、过时或缺失时，仍靠内置基础标签 + 现场探测工作，冷启动用户不会面对空库 |
| **无路径设计** | 全链路只认 MTP 不透明对象 ID（`opaqueId`），不依赖字符串路径，传输层天然可插拔 |
| **配置驱动采样** | 观测目标 / 分桶 / 卷映射写在 `config/observe-config.json`，增删目标无需改代码、无需重编译 |
| **严格的脱敏红线** | 只采集目录名与扩展名分布等元数据；引出文件名、账号 hash、IMEI 一律禁止，并带本地校验器 |
| **离线优先 / 零遥测** | 默认不上传任何数据；规则可离线加载，观测可离线导入 |

---

## 快速开始

### 环境要求

| 组件 | 版本 / 说明 |
| --- | --- |
| 操作系统 | Windows 10 / 11（MTP/WPD 依赖 Windows） |
| .NET SDK | 8.0（开发与从源码运行；[下载](https://dotnet.microsoft.com/download/dotnet/8.0)） |
| 手机 | Android 设备，USB 连接并切换为 **文件传输 / MTP** 模式 |
| Python | 3.x（仅运行观测校验器时需要，零第三方依赖） |
| 可选 | `adb.exe`（`--from-adb` 只读采集设备元数据时使用） |

第三方 NuGet 依赖：`YamlDotNet 16.3.0`（Core）、`Vanara.PInvoke.PortableDeviceApi 5.0.8`（MTP/WPD 传输层）。

### 从源码运行

```bash
# 克隆仓库
git clone https://github.com/zjc99999/phoneFiles.git
cd phoneFiles

# 还原依赖并构建整个解决方案
dotnet build PhoneFiles.sln

# 无需真机：先跑一遍离线自测（12 项断言）
dotnet run --project src/PhoneFiles.Probe.Console -- selftest

# 连接真机后启动桌面 GUI
dotnet run --project src/PhoneFiles.Desktop
```

> 手机需在「开发者选项」或通知栏中把 USB 模式切到 **文件传输 (MTP)**；锁屏状态下部分设备会拒绝 MTP 访问，请先解锁。

### 运行已打包程序（免装 .NET 环境）

如果没有安装 .NET 8，可使用自包含包：

```powershell
.\build\package.ps1 -SelfContained
# 产物：artifacts\selfcontained-<时间戳>\PhoneFiles.Desktop\PhoneFiles.Desktop.exe
```

详见 [打包发布](#打包发布)。

---

## 桌面 UI 使用（PhoneFiles.Desktop）

`src/PhoneFiles.Desktop/` 是真机可用的 WPF 图形界面，接入语义层 + 归档层，全链路仍只认 `opaqueId`、不依赖路径。

**操作流程：**

1. **刷新设备** → 列出所有 MTP 设备，选择你的手机。
2. **连接** → 连接设备并列出所有存储卷（如 `disk`、`内部存储设备`）；同时加载规则库。
3. 填写 **SDK**（如 31）与 **ROM**（如 miui）用于规则匹配，选择存储卷，点击 **载入**。
4. **浏览目录树** → 根层目录带语义标签；目录**懒加载**（展开时才枚举子节点，避免上千节点卡界面）。
5. **查看详情** → 选中节点显示标签 / 来源 / 置信度 / 风险 / 动作 / 可达性 / 规则命中数。
6. **归档** → 「归档」按钮**仅在目录实测可读且动作为 `Backup` 时可用**——受限目录自动灰掉；选择电脑上的目标目录后开始复制，支持进度显示与取消。

**归档产物**：目标目录下生成各目录的副本，以及一份 `清单.csv`：

```
relative_path,size_bytes,mtime_utc,sha256
DCIM/Camera/IMG_20260101_120000.jpg,2431024,2026-01-01T04:00:00.0000000Z,9f86d0...
```

真机冒烟（Redmi 10X Pro / MIUI 13 / Android 12）已通过：设备发现、连接、卷加载、根层语义树（`Pictures/DCIM/Download` → Backup，`Android/backup/MIUI` → ShowOnly）、`Pictures` 懒加载出真实子文件、`Android` 归档按钮灰掉。

---

## 命令行探针使用（PhoneFiles.Probe.Console）

CLI 探针提供设备枚举、目录遍历、语义分析、脱敏采样与离线自测，适合脚本化与调试。

```bash
# 显示帮助
dotnet run --project src/PhoneFiles.Probe.Console -- --help
```

### 命令一览

| 命令 | 用途 | 是否需要真机 |
| --- | --- | --- |
| `list` | 列出已连接的 MTP 设备（设备 ID / 名称 / 厂商 / 描述） | 是 |
| `tree` | 打印某设备的对象树（只读，带深度与节点数安全上限） | 是 |
| `observe` | 生成一份脱敏观测 JSON（每卷一份），可自动从 adb 读取元数据 | 是 |
| `semantic` | 打印某卷某目录的语义决策树（标签 + 来源 + 可达性 + 规则命中数） | 是 |
| `selftest` | 离线回归：验证「零规则运行 + 可见≠可读」两条硬约束（12 项断言） | 否 |

### `tree` — 遍历目录树

```bash
dotnet run --project src/PhoneFiles.Probe.Console -- tree \
  --device <设备PnPID> --max-depth 3 --max-nodes 200
```

- `--device`：设备 PnP ID（默认第一台）。
- `--max-depth`：最大递归深度（默认 3）。
- `--max-nodes`：打印节点数安全上限（默认 200）。

### `semantic` — 语义决策树

```bash
# 分析 DCIM 目录（MIUI、SDK 30）
dotnet run --project src/PhoneFiles.Probe.Console -- semantic --path DCIM --sdk 30 --rom miui

# 零规则 / 跳过探测（纯离线标签树，用于对照）
dotnet run --project src/PhoneFiles.Probe.Console -- semantic --rules <空目录> --no-probe
```

- `--path`：要分析的相对节点名，用 `/` 分段（如 `DCIM`）；默认卷根。
- `--volume`：仅分析显示名包含该字符串的存储卷（默认第一个）。
- `--rules`：规则 yml 目录（默认 `rules/rules`；目录不存在 = 零规则模式）。
- `--sdk` / `--rom`：规则匹配用的 Android API 级别 / ROM 族。
- `--from-adb`：通过只读 `getprop` 自动填充 sdk/rom。
- `--no-probe`：跳过现场可达性探测。

输出示例：

```
[D] DCIM/Camera
     标签: 相机照片与视频  | 来源: 规则  | 置信: 0.96  | 风险: ReadOnly  | 动作: Backup
     可达: 可归档  规则命中: 2
[D] Android/data/com.tencent.mm/MicroMsg
     标签: 应用私有数据（通常受限）  | 来源: 基础  | 置信: 0.50  | 风险: Private  | 动作: ShowOnly
     可达: 可列出但不可读 (read failed: UnauthorizedAccessException)  规则命中: 0
```

### `observe` — 生成脱敏观测

```bash
# 手动填写设备元数据
dotnet run --project src/PhoneFiles.Probe.Console -- observe \
  --oem xiaomi --model M2102K1C --os 12 --sdk 31 --region CN \
  --out contributions/observations/<机型>-<日期>

# 自动从 adb getprop 读取元数据（只读）
dotnet run --project src/PhoneFiles.Probe.Console -- observe \
  --from-adb --out contributions/observations/<机型>-<日期>
```

- **每个存储卷单独生成一份 `obs_<卷名>.json`**——绝不合并卷，避免同名目录（Download、Recordings）混淆来源。
- 观测目标 / 分桶 / 卷映射均来自 `config/observe-config.json`。
- 生成后用校验器自检（见 [开发与测试](#开发与测试)）。

### `selftest` — 离线自测

```bash
dotnet run --project src/PhoneFiles.Probe.Console -- selftest
```

无需真机，用合成节点验证两条硬约束（详见 [两条硬约束](#两条硬约束设计红线)）。当前 **12/12 全通过**。

---

## 项目结构

```
phoneFiles/
├─ PhoneFiles.sln                       # 解决方案（4 个项目）
├─ src/
│  ├─ PhoneFiles.Core/                  # 纯逻辑层：无平台依赖
│  │  ├─ ITransport.cs                  # 文件节点 / 传输 / 探测 抽象（无字符串路径）
│  │  ├─ Semantic/                      # 语义层：基础标签兜底 + YAML 规则加载/匹配/打分
│  │  │  ├─ BasicLabels.cs
│  │  │  ├─ RuleStore.cs
│  │  │  ├─ SemanticEngine.cs
│  │  │  └─ SemanticModels.cs
│  │  └─ Archive/                       # 归档层：opaqueId 递归复制 + SHA256 + 清单
│  │     ├─ ArchiveService.cs
│  │     └─ ArchiveModels.cs
│  ├─ PhoneFiles.Transport.Mtp/         # MTP/WPD 传输实现（Vanara P/Invoke）
│  │  ├─ MtpTransport.cs
│  │  └─ ComStreamWrapper.cs
│  ├─ PhoneFiles.Desktop/               # WPF 桌面 UI（设备/卷/语义树/归档）
│  │  ├─ MainWindow.xaml(.cs)
│  │  └─ App.xaml(.cs)
│  └─ PhoneFiles.Probe.Console/         # 命令行探针（list/tree/observe/semantic/selftest）
│     ├─ Program.cs
│     └─ AdbMetadata.cs
├─ config/
│  └─ observe-config.json               # ★ 社区可维护：观察目标/分桶/卷映射（改它无需重编译）
├─ tools/
│  └─ validate_observation.py           # 观测校验器（schema+隐私红线+配置一致性，零依赖）
├─ rules/
│  ├─ GOVERNANCE.md                     # 规则库治理规则（观测→上线生命周期）
│  ├─ baseline/                         # 内置签名快照（baseline.manifest.json + *.yml）
│  ├─ rules/                            # 由观测派生的规则 (*.yml)，随程序打包离线加载
│  └─ schema/                           # 观测与规则的 JSON Schema
├─ contributions/
│  ├─ observation.example.yml           # 脱敏观测模板
│  └─ observations/                     # 真机采样（每卷一份 obs_*.json）
├─ build/
│  └─ package.ps1                       # 一键打包脚本（framework / self-contained）
├─ docs/                                # 产品概念分析、路线决策、社区采样贡献指南
├─ artifacts/                           # 打包产物（.gitignore 已忽略源码外的构建输出）
├─ CONTRIBUTING.md                      # 贡献指南（脱敏红线 + 贡献即授权）
├─ LICENSE                              # Apache-2.0（源代码）
├─ LICENSE-DATA                         # CC BY-SA 4.0（规则库 / 观测数据）
└─ README.md
```

---

## 架构设计

全链路「无路径」：因为 MTP 不提供字符串路径，只提供不透明对象节点，所有层只认 `opaqueId`、不认字符串路径。这是为了让传输层（MTP / ADB / 未来的鸿蒙）天然可插拔。

```
UI (WPF) / CLI
   │
   ▼
任务层     ArchiveService（递归复制 / 进度 / 取消 / SHA256 / 清单）
   │
   ▼
语义层     SemanticEngine（探测 → 模板 → 加权）+ RuleStore + BasicLabels
   │        输出 NodeDecision：标签 / 风险 / 动作 / 来源 / 可达性 / 规则命中
   ▼
传输抽象   ITransport / IFileSystemNode（只用 opaqueId，绝不传字符串路径）
   │
   ▼
传输实现   MTP/WPD（当前）  │  ADB（opt-in，规划）  │  鸿蒙（后置 adapter）
   │
   ▼
规则库     baseline 快照 + 增量规则 + 版本回滚
```

### 关键抽象（`src/PhoneFiles.Core/ITransport.cs`）

- **`IFileSystemNode`**：文件树节点，只有 `OpaqueId / DisplayName / ParentId / IsDir / Size / MTime`，以及 `OpenReadAsync` / `EnumerateAsync`。**刻意不含任何字符串路径字段**——MTP/WPD 暴露的是不透明对象 ID，任何传路径的设计在生产环境都会失败。
- **`ITransport`**：`ListDevicesAsync` / `ConnectAsync` / `GetRootAsync` / `EnumerateAsync` / `ProbeAsync`，实现有 MTP/WPD（当前），ADB 为后续 opt-in。
- **`ProbeResult`**：能力探测结果，含 `CanEnumerate` / `CanRead` / `Reason`；`Accessible` 仅当「至少一个子节点被枚举且成功读取」时才为 true。
- **`VolumeInfo`**：存储卷（MTP 设备可以有多个卷，如内部存储与 SD 卡），通过 `WPD_STORAGE_TYPE` 保守分类，不信任显示名。

### 语义判定流程（`SemanticEngine.AnalyzeNodeAsync`）

1. 对所有适用规则（`RuleStore.ApplicableCandidates(ctx)`，按 `osFamily / channel / romFamily / sdkRange / appPackage` 匹配）取 `nodePattern` 通配匹配，收集命中候选。
2. 若有命中，取置信度最高者的标签 / 风险 / 默认动作；否则回退到 `BasicLabels`（零规则可用）。
3. 对目录执行 `ProbeAsync`，得出可达性：`Readable / EnumerableOnly / VisibleOnly / NotProbed`。
4. **关键保护**：若动作为 `Backup` 但可达性为 `VisibleOnly` 或 `EnumerableOnly`，强制降级为 `ShowOnly`——受限目录绝不承诺备份。
5. 输出 `NodeDecision`，每个字段要么来自实测，要么明确标注来源（Rule / Basic / Unclassified），让 UI 能解释「为什么」。

### 归档流程（`ArchiveService.ArchiveAsync`）

- 通过 `opaqueId` 递归复制选中目录，相对路径仅由显示名拼接、仅用于**电脑端**落盘。
- 逐文件流式复制（`FileStream` + 80 KiB 缓冲），同步计算 SHA256。
- 支持 `IProgress<ArchiveProgress>` 进度回调与 `CancellationToken` 取消。
- 写出 UTF-8（无 BOM）`清单.csv`，表头 `relative_path,size_bytes,mtime_utc,sha256`。
- 全程只读手机：不删除、不写回；异常逐文件记录到 `ArchiveResult.Errors`。

### MTP 传输层要点（`MtpTransport.cs`）

- 基于 Vanara 的 `PortableDeviceApi` COM 互操作；连接时以 `GENERIC_READ` + `FILE_SHARE_READ` 打开，只读设计。
- `ListVolumesAsync` 读取 `WPD_STORAGE_TYPE`（`FIXED_RAM / REMOVABLE_RAM / ...`）与容量，保守分类存储卷。
- `ProbeAsync` 有上限探测：最多枚举 16 个子节点，采样最多 4 个文件（因真机上目录首个子节点常是 0 字节隐藏文件，如 `.escheck.tmp`），任一采样文件能打开即判定可读；任何 COM/IO 异常都转为 `Reason` 字符串返回，**永不抛出**。
- 所有 COM 对象用 `Marshal.ReleaseComObject` 及时释放；`DisposeAsync` 关闭设备连接。

---

## 两条硬约束（设计红线）

开发必须始终遵守这两条，改动代码时不得破坏：

1. **可空规则运行**：规则库可空 / 过时 / 错误，零规则下仍靠基础标签 + 现场探测列目录，冷启动用户不会面对空库。
   - 实现：`BasicLabels` 提供零依赖的中文标签兜底；`RuleStore` 对损坏文件跳过并记录警告，不让单个坏规则阻塞整个库。
2. **可见 ≠ 可读**：`exists:true` 只表示节点可见，不代表可枚举 / 可读取；语义层用 `ProbeAsync` 实测，对受限目录标注「可见不可读 / 可列不可读」，**绝不承诺备份**。
   - 实现：`SemanticEngine` 对受限目录强制降级为 `ShowOnly`，且不向下递归 `VisibleOnly` 节点；UI 归档按钮同步灰掉。

这两条由 `selftest` 用合成节点（模拟 GT7 Pro 的 `MicroMsg`「可见可列但不可读」）做离线回归保护。

---

## 规则库与社区观测

规则库是独立数据资产，**观测是事实，规则是派生品**——维护规则的正确方式是提交观测，而不是手改 `rules/rules/*.yml`。

**派生与上线流水线：**

```
观测 → 自动校验 → 聚类(OEM+ROM族+SDK区间+App包+通道) → 规则(confidence/evidence[]/version/since/supersedes)
     → 2 人 review → canary 灰度 5% → 观察 7 天 → stable 全量 → 版本化 + 签名下发 → 客户端内置 baseline 兜底
```

- **冲突处理**：同条件多候选 → 全部保留、按置信度排序展示，不强行覆盖。
- **置信度模型**：`f(独立观测数, 设备多样性, 时间新鲜度)`，按季度衰减，低于阈值（建议 0.6）自动降级复核。
- **规则格式**：见 `rules/rules/android.generic.camera.mtp.yml`、`android.miui.camera.mtp.yml`（含 `match` / `candidates` / `confidence` / `evidence` / `version` / `status`）。
- **治理细则**：见 `rules/GOVERNANCE.md`。

真机样本（Redmi 10X Pro 双卷、Redmi Note 7 Pro、realme GT7 Pro）作为规则库种子 + 回归样本。当前私有数据目录（如 `Android/data/…`）在 MTP 下普遍「可见但不可读」，工具会如实标注而不是误判。

---

## 配置参考

### `config/observe-config.json`（社区可维护）

| 段 | 作用 |
| --- | --- |
| `targets[]` | 观察目标：`nodePattern`（WPD 相对节点名，`/` 分段，`*` 匹配单层）+ `canonicalLabel` |
| `buckets.childCount` / `buckets.sizeBytes` | 分桶边界：`max` 为上界（含），超出最后一个归入最后标签 |
| `rootMapping` | `WPD_STORAGE_TYPE` → 观测 `root`（`FIXED_RAM → internal_shared`、`REMOVABLE_RAM → sdcard1`、`default → unknown`） |

改这个 JSON 即可增删观察目标或调整分桶，**无需改 C# 或重新编译**。修改分桶时需同步 `rules/schema/observation.schema.json` 的 examples 并说明理由。

### 观测 Schema

- 观测模板：`contributions/observation.example.yml`
- 观测 JSON Schema：`rules/schema/observation.schema.json`
- 规则 JSON Schema：`rules/schema/rule.schema.json`

---

## 开发与测试

### 三层测试（从快到慢）

```bash
# 1) 离线自测（无需真机）：12 项断言验证两条硬约束
dotnet run --project src/PhoneFiles.Probe.Console -- selftest

# 2) 观测数据校验（无需真机）：schema + 隐私红线 + 配置一致性
python tools/validate_observation.py --all contributions/observations/

# 3) 真机端到端（插上 MTP 设备）
dotnet run --project src/PhoneFiles.Probe.Console -- list
dotnet run --project src/PhoneFiles.Probe.Console -- tree --device 0
dotnet run --project src/PhoneFiles.Probe.Console -- semantic --path DCIM --sdk 31 --rom miui
dotnet run --project src/PhoneFiles.Desktop      # 或用 GUI 手动冒烟
```

**当前状态**：三项回归全绿——build 0 警告 0 错误、`selftest` 12/12、validator 4/4；打包产物在任意工作目录下 `selftest` 也 12/12 通过。

### 校验器红线（`tools/validate_observation.py`）

零第三方依赖、离线运行，社区贡献者提交前自检。校验项包括：

- 顶层 / device / privacy / client 必填字段与格式
- `privacy.filenameSamples` 必须为 `false`
- 伪扩展名泄漏扫描（如 `.removed-20240201-192827`）
- 疑似 IMEI / 序列号扫描（15–17 位数字串）
- `nodePattern` 禁止盘符 / 账号 hash / 文件名
- 与 `config/observe-config.json` 的 targets 一致性

---

## 打包发布

用 `build/package.ps1` 一键发布到 `artifacts/`，桌面 UI 与 CLI 探针一起打包，规则 yml 自动随包携带（离线可加载）：

```powershell
# 框架依赖包（约 8 MB，目标机需装 .NET 8 Desktop Runtime）——默认
.\build\package.ps1

# 自包含包（约 160 MB，win-x64，目标机免装 .NET）
.\build\package.ps1 -SelfContained

# 指定配置
.\build\package.ps1 -Configuration Debug
```

产物位于 `artifacts\framework-<时间戳>\` 或 `artifacts\selfcontained-<时间戳>\`，分别含：

- `PhoneFiles.Desktop\`（双击 `PhoneFiles.Desktop.exe` 用 GUI）
- `PhoneFiles.Probe.Console\`（命令行探针）

目前是**绿色版 directory 发布**，尚未做 installer（NSIS/MSIX）。

---

## 开发状态与路线图

> 当前版本 = **初步可用版本（MVP）**：纯逻辑层 + CLI 探针 + 真机端到端 + WPF 桌面 UI 已全部打通，具备真机日常使用基础；但归档写盘的端到端实测、扩样（20–30 台）与用户访谈尚未完成，**还不是正式 1.0 发布版**。

已落地：

- ✅ 纯逻辑层（语义层 + 归档层），不依赖 UI / 真机
- ✅ MTP/WPD 只读传输 + 有上限能力探测
- ✅ CLI 探针（5 个命令）+ 离线自测 12/12
- ✅ WPF 桌面 UI（设备/卷/语义树/归档），真机冒烟通过
- ✅ 脱敏观测采样 + 零依赖校验器 + 规则派生流水线
- ✅ 配置驱动采样 + 一键打包

1.0 发布门槛（原 Spike 判据，不再阻塞开发）：

- ⬜ 扩样 20–30 台真机，四类目录可达率 ≥ 85%，首次连接成功率 ≥ 90%
- ⬜ 归档写盘端到端实测
- ⬜ 用户访谈
- ⬜ （可选）installer、ADB opt-in adapter、鸿蒙 adapter

### 已知限制（真机发现，待后续修正）

1. **存储卷类型映射存疑**：含全部用户数据的 `disk` 卷被 WPD 上报 `REMOVABLE_RAM`，按 `WPD_STORAGE_TYPE` 映射成根 `sdcard1`；而「内部存储设备」卷为 `FIXED_RAM → internal_shared`。与「内部共享存储 = FIXED_RAM」的直觉不符；规则当前不依赖 `root` 字段，故不影响语义判定，但观测元数据的 `root` 归类需在更多机型上核对。
2. **系统版本多源**：`--from-adb` 发现 `getprop release=12` 与 fingerprint 中的 `Android 12 / V13.0.7.0.SJLCNXM`（MIUI 版本）并存。工具记录 getprop 值并给出人工核对警告，不做自动合并。

---

## 隐私与数据红线

- **默认零遥测**：用户文件内容、文件名、账号 hash 原文绝不离开本机。
- 规则库更新是**可选**的，且只包含目录结构与扩展名分布等脱敏元数据；可完全离线运行、离线导入规则包。
- 观测数据最小集合：`oem/model/osVersion/sdk/romBuild/region`、通道 `channel`、目录名 `nodePattern`、存在性与可否枚举、`childCountBucket`、`sizeBucket`、`mtimeRange`、扩展名分布 `fingerprint`、去标识 `deviceIdHash`、客户端版本。
- **绝不采集**：文件名 / 内容、账号 hash 原文（微信 `MicroMsg/<hash>` 只保留到 `MicroMsg/`）、IMEI / 序列号 / Android ID 原文、联系人 / 短信 / 通话记录、含个人信息的截图。
- 数据红线详见 [CONTRIBUTING.md](CONTRIBUTING.md)，规则库数据许可见 [LICENSE-DATA](LICENSE-DATA)。

---

## 许可证与商业化

| 资产 | 许可 |
| --- | --- |
| 程序源代码 | [Apache-2.0](LICENSE)（含专利授权） |
| 规则库 / 观测数据 | [CC BY-SA 4.0](LICENSE-DATA) |

- **C 端永久免费开源。**
- **B 端收费**：批量归档、清除证明、企业合规等多设备能力为商业授权范围。提前说明，避免「拿我的贡献赚钱」的社区争议。
- 为什么不用 AGPL：AGPL 会强制把本工具用作内部部署的 B 端客户也开源，与上面的 B 端变现路线直接冲突。

---

## 参与贡献

- **贡献观测**：见 [社区采样与贡献指南 v1.0](docs/社区采样与贡献指南-v1.0.md)——无需改代码，配置驱动采样。提交观测即表示你已阅读并接受脱敏红线与「贡献即授权」条款。
- **贡献规则 / 代码**：见 [CONTRIBUTING.md](CONTRIBUTING.md)。传输层必须实现统一抽象、Core 层不得引入字符串路径、规则/校验/清单为纯逻辑可单测层。
- 提交前跑通三层测试；争议以真机观测数据与文档裁决为准。

---

## 文档索引

| 文档 | 内容 |
| --- | --- |
| [产品概念分析 v1.0](docs/手机本地数据路径导航工具-产品概念分析.md) | 需求判定、技术阻断、安全边界、验证计划 |
| [路线决策补充分析 v1.1](docs/手机本地数据路径导航工具-路线决策补充分析-v1.1.md) | 三项决策裁决、贡献单元重构、技术栈与架构 |
| [社区采样与贡献指南 v1.0](docs/社区采样与贡献指南-v1.0.md) | 贡献者 3 条命令完成采样与提交 |
| [Spike 技术发现记录](docs/Spike技术发现记录-存储卷与可见性.md) | 真机采样中的机制性发现（存储卷、可见性、版本多源问题） |
| [规则库治理](rules/GOVERNANCE.md) | 观测→规则→上线的完整生命周期与置信度模型 |
| [贡献指南](CONTRIBUTING.md) | 脱敏红线、贡献即授权、代码贡献补充 |
