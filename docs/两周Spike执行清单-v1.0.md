# 两周技术 Spike 执行清单（v1.0）

| 项目 | 内容 |
| --- | --- |
| 文档版本 | v1.0 |
| 日期 | 2026-11-21 |
| 目的 | 用最小成本验证 v1.1 §3.3 的生死假设：**MTP 下到底能看到什么**，据此触发 Go / 收缩 Go / No-Go 决策 |
| 技术栈 | 路线 A：C# / .NET 8 + WPF + WPD COM（`PortableDeviceApi`） |
| 周期 | 2 周（10 个工作日） |
| 交付物 | 可运行的枚举探针 App、20–30 台真机观测数据集、Go/No-Go 决策记录、技术栈确认结论 |

> 这份清单只做「验证」，不做产品。唯一目标是把「MTP 可见性」这个最大不确定性变成数据。任何偏离这个目标的编码都算范围蔓延，禁止。

> **状态更新（2026-11-21）**：用户已拍板「先开发程序，不等采样凑齐」。本清单的 Go/No-Go 判据（20–30 台、四类可达率 ≥85%、首次连接 ≥90%）**从「开工门槛」降级为「发布 1.0 门槛」**，不再阻塞 MVP 开发。现有 3 台真机样本作为规则库种子 + 回归样本；开发可先于扩样进行。判据本身在发布 1.0 前仍需全部回收。

---

## 0. Spike 要回答的三个问题（Go/No-Go 的根源）

1. **可达性**：MTP 下，「照片/截图/下载/公开录音」四类目录在真机矩阵上到底能稳定枚举到哪几类？（判据：≥ 85% 为 Go）
2. **连接稳定性**：插线后首次连接成功率有多高？（判据：≥ 90% 为 Go）
3. **天花板**：`Android/data`（微信/大量语音录音）在 MTP 与 opt-in ADB 下是否可见？若不可见且用户唯一动机是微信数据 → No-Go 转 B 端。

附带要确认的第 4 个技术不确定性：**`windows-rs` 对 WPD COM 绑定是否完整**——若不完整，直接确认走路线 A，不再纠结。

---

## 1. 环境准备（Day 1 上午，0.5 天）

| 项 | 动作 | 完成标志 |
| --- | --- | --- |
| 工具链 | 安装 .NET 8 SDK、Visual Studio 2022（含 C# 桌面负载）、Windows SDK | `dotnet --version` 输出 8.x |
| WPD 参考 | 下载微软官方 WPD sample（PortableDevice API C++ sample）作为 COM 调用参照 | 本地有可编译的 WPD 示例或头文件 |
| 仓库骨架 | 按 v1.1 §2.3 建立 `src/Core`、`src/Transport.Mtp`、`src/App` 解决方案结构 | `dotnet build` 空方案通过 |
| Rust 探针（可选并行） | 建最小 `windows-rs` WPD COM 调用工程，测试 `IPortableDevice` 能否实例化 | 明确记录「绑定完整/不全」结论 |

**决策门 D0**：若 `windows-rs` 绑定不全 → 技术栈正式锁定路线 A，删除 Rust 探针，不再投入。

---

## 2. 核心探针开发（Day 1 下午 – Day 4，3.5 天）

### 2.1 Transport.Mtp 最小实现（Day 1–3）

按 v1.1 §2.2 的 `ITransport` / `IFileSystemNode` 抽象实现，**Core 层不得出现字符串路径**：

```
IFileSystemNode {
  opaqueId, displayName, parentId, isDir, size, mtime,
  openRead(), enumerate()
}
ITransport {
  listDevices(), connect(deviceId), getRoot(), enumerate(node)
}
```

WPD COM 调用链（参照官方 sample）：
1. `PortableDeviceManager::GetDevices` 枚举设备
2. `IPortableDevice::Open` 连接（含 `IPortableDeviceValues` 客户端信息）
3. `IPortableDeviceContent::EnumObjects` 递归枚举对象
4. `IPortableDeviceProperties` 取 `WPD_OBJECT_NAME` / `WPD_OBJECT_PARENT_ID` / `WPD_OBJECT_SIZE` / `WPD_OBJECT_DATE_MODIFIED` / `WPD_OBJECT_CONTENT_TYPE`
5. 每个对象映射为 `IFileSystemNode`（opaqueId = WPD object id）

**验收标志**：插入一台 Android 手机，能打印出完整的对象树（opaqueId + displayName），证明「无路径枚举」跑通。这是整个 spike 的技术底座。

### 2.2 观测导出器（Day 4）

按 `rules/schema/observation.schema.json` 把枚举结果导出为脱敏观测：
- 只导出目录名（`nodePattern`）、存在性、`childCountBucket`、`sizeBucket`、`mtimeRange`、扩展名分布 `fingerprint`。
- `deviceIdHash` 用加盐 sha256；`filenameSamples` 恒为 false。
- 导出前跑一次本地自查（对照 `CONTRIBUTING.md` 脱敏红线）。

**验收标志**：能在客户端一键导出符合 schema 的 observation JSON，且不含任何文件名/内容/hash 原文。

---

## 3. 真机矩阵采样（Day 5 – Day 8，4 天）

### 3.1 设备矩阵（目标 20–30 台，覆盖 v1.1 §3.3）

| 维度 | 覆盖要求 |
| --- | --- |
| 品牌 | 小米/Redmi、OPPO、vivo、华为、荣耀、三星、原生/Pixel 等 |
| Android 版本 | Android 9 / 10 / 11 / 12 / 13 / 14 / 15 尽量都覆盖 |
| ROM | MIUI、HyperOS、ColorOS、OriginOS、EMUI/HarmonyOS(兼容)、OneUI、原生 |
| 通道 | 每台都采 MTP；其中选 5–8 台再采 opt-in ADB 作对照 |
| 特殊样本 | 至少 3 台 Android 11+ 设备（验证 Scoped Storage 对 `Android/data` 的可见性天花板） |

> 设备来源：团队自有 + 同事/朋友借用 + 本地二手市场实测。若凑不满 20 台，记录实际台数并相应缩小结论置信区间，不要编数据。

### 3.2 每台设备记录（用第 2.2 节导出器）

| 记录项 | 说明 |
| --- | --- |
| 设备指纹 | OEM / model / osVersion / sdk / romBuild / romFamily / region |
| 四类可达性 | 相机照片/视频、截图、下载、公开录音——各自的 `exists` / `canEnumerate` |
| 首次连接 | 插线到枚举成功的耗时与是否一次成功 |
| `Android/data` | 是否可见（MTP 与 ADB 分别记） |
| 微信可见性 | `Android/data/com.tencent.mm` 或等价目录是否可见 |
| 通话录音 | 系统通话录音目录是否可见（注意合规，只看目录结构不看内容） |
| 异常记录 | 连接失败、枚举报错、重复对象、乱码名等 |

### 3.3 数据归集

所有观测集中到 `contributions/spike-observations/`（本地分析用，**不上传**），用脚本汇总四类可达率与首次连接成功率。

---

## 4. 用户访谈（Day 5–8 并行，共 10–15 人）

与真机采样并行，验证 v1.0 §8 的需求侧假设。核心问题（开放式，别诱导）：

1. 你**最近一次**把手机里的东西拷到电脑是什么时候？拷了什么？
2. 当时你是**怎么找到**那些文件的？花了多久？中途有没有找不到/找错？
3. 你是**只拷不删**，还是拷完会删手机里的原文件？删的时候有没有担心删错？
4. 你最想从手机里拿出来的是哪类东西？（照片 / 聊天记录 / 录音 / 下载文件 / 其他）
5. 如果有个工具插上电脑就告诉你「照片在这、录音在那」，一键搬到电脑并给清单，你会用吗？愿意为它付费吗？

**重点甄别**：把「拿到微信数据」当唯一动机的人占比——这是 No-Go 判据的关键输入。

---

## 5. 决策门（Day 9–10）

### 5.1 按 v1.1 §3.3 判定

| 结果 | 判据 | 动作 |
| --- | --- | --- |
| **Go** | 四类可达率 ≥ 85%，首次连接成功率 ≥ 90% | 按 MVP（P0）开发 |
| **收缩 Go** | 仅 2–3 类稳定可见 | 缩到「照片/视频/下载归档」，砍掉录音承诺 |
| **No-Go** | 微信与通话录音在 MTP + opt-in ADB 下仍不可见，且访谈显示「拿到微信数据」是唯一动机 | 转 B 端/换机助手定位，或停止 |

### 5.2 附带结论

| 结论项 | 判定依据 |
| --- | --- |
| 技术栈 | 路线 A 确认 / 若测试充分可考虑 B（windows-rs 绑定完整性） |
| baseline 覆盖率 | 用采样数据算 baseline 对真机矩阵的目录→标签覆盖率（目标 ≥ 60%） |
| 观测提交可行性 | 一键导出观测的流程是否顺畅，为社区机制提供依据 |
| 需求真伪 | 访谈数据支撑/推翻 v1.0「低频手段级需求」判定 |

### 5.3 决策记录模板

产出 `docs/Spike决策记录.md`，包含：
- 实际采样台数与矩阵分布
- 四类可达率、首次连接成功率（附原始数据表）
- Go/收缩 Go/No-Go 结论 + 依据
- 技术栈结论
- 若 Go：进入 MVP 的下一步计划；若收缩/No-Go：调整后的定位

---

## 6. 时间线总表

| 天 | 任务 | 交付 |
| --- | --- | --- |
| Day 1 上午 | 环境准备 + D0 决策门 | 工具链就绪、技术栈初定 |
| Day 1 下午 – Day 3 | Transport.Mtp 最小实现 | 可枚举对象树的探针 App |
| Day 4 | 观测导出器 | 符合 schema 的 observation 导出 |
| Day 5 – Day 8 | 真机矩阵采样（20–30 台）+ 用户访谈（10–15 人） | 观测数据集 + 访谈记录 |
| Day 9 | 数据汇总 + 指标计算 | 可达率/成功率/覆盖率统计 |
| Day 10 | 决策门 + 决策记录 | Go/No-Go 结论 + 下一步计划 |

---

## 7. 范围红线（Spike 期间禁止）

- 不做 UI 美化、不做中文语义树的完整交互（探针能列树即可）
- 不做归档复制、清单生成、SHA256（那是 MVP，不是 spike）
- 不做 ADB 完整实现（只做一个 opt-in 对照开关，够验证可见性即可）
- 不做规则引擎完整实现（只验证 observation 导出 + 覆盖率统计）
- 不删除/写入任何手机数据（全程只读）
- 不上传任何观测到公网（全部本地分析）

---

## 8. 风险与应对

| 风险 | 应对 |
| --- | --- |
| 真机凑不满 20 台 | 记录实际台数，缩小结论置信区间，不编数据 |
| WPD COM 在某些机型枚举失败 | 记录为异常样本，分析是否通道限制；不影响整体判据 |
| Android 11+ `Android/data` 全面不可见 | 这正是要验证的天花板，不是 bug；据此触发收缩/No-Go |
| 访谈样本偏差 | 明确记录样本来源，结论标注「小样本」 |
| 时间不够 | 优先保「四类可达性 + 首次连接成功率」两个核心指标，访谈可延后 |
