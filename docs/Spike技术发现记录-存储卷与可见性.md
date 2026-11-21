# Spike 技术发现记录：存储卷与可见性

> 性质：Day 1–3 技术探针的**过程发现记录**，不是最终决策记录（那是 `Spike决策记录-模板.md` 的角色）。
> 所有结论附原始数据。观测样本本地保存在 `contributions/observations/redmi10xp-2026-11-21/`，**未上传**。
> 记录日期：2026-11-21。样本：3 台（Redmi 10X Pro / Xiaomi / Android 10 / SDK 29 / CN，双卷；
> Redmi Note 7 Pro / Xiaomi / Android 16? / SDK 36? / CN，单卷，版本存疑；
> 真我 GT7 Pro / realme / Android 17 / SDK 37 / CN，单卷），
> 远未达到 20–30 台目标，本文结论仅为**机制性发现**，不可外推为可达率。

## 1. 核心发现：一台设备 = 多个存储卷，且"默认存储"不在第一个

真机 `tree --max-depth 2` 显示，WPD 设备根（`WPD_DEVICE_OBJECT_ID`）下有**两个平级存储卷**：

| 卷显示名 | WPD_STORAGE_TYPE | 判定 root | 用户实际含义 |
| --- | --- | --- | --- |
| `disk` | `REMOVABLE_RAM` (4) | `sdcard1` | **TF 卡**（用户确认），不是默认存储 |
| `内部存储设备` | `FIXED_RAM` (3) | `internal_shared` | 手机默认内部存储 |

两个卷的 `WPD_FUNCTIONAL_OBJECT_CATEGORY` 均为 `23f05bbc-15de-4c2a-a55b-a9af5ce412ef`
（= `WPD_FUNCTIONAL_CATEGORY_STORAGE`），容量桶均 `64-256GB`。
证据：`contributions/observations/redmi10xp-2026-11-21/obs_disk.json` 与 `obs_内部存储设备.json` 的 `volume` 字段。

**为什么这是个陷阱（已实际踩中并修正）**：
- 初版探针把"root 下的目录"作为候选起点、返回**第一个匹配**；`disk` 排在最前，
  于是所有观测都来自 TF 卡，默认存储被完全遮蔽。
- 遮蔽极难察觉：两个卷**都有** `Download`、`Recordings`、`Android/data` 等同名目录，
  输出看起来"正常"，实际答非所问。
- 硬编码 `root: "internal_shared"` 更是错上加错——把 TF 卡的观测标成了默认存储。

**修正**：见 §3。这条发现直接验证并强化了 v1.0/v1.1 的核心论点——
不仅"路径"不能假设，连"哪个卷是默认存储"都不能假设；卷必须作为一等公民显式发现与标注。

## 2. 同名目录在不同卷内容不同（合并观测会丢失信息）

同一相对节点名，在两个卷上并非等价；这正是"必须分卷观测"的实证：

| 节点 | disk (TF 卡) | 内部存储设备 (默认) |
| --- | --- | --- |
| `DCIM` 扩展名分布 | tmp/jpeg/ppmx/jpg | jpg/tmp/tmfs/yz |
| `Pictures` | jpg:0.69, tmp:0.08… | jpg:0.51, png:0.32, jpeg:0.10, db:0.02 |
| `Download` | jpeg/jpg/docx/doc | pdf:0.38, png:0.19, txt:0.09, zip:0.05 |
| `Sounds` | exists=true | **exists=false** |
| `Android/data/com.tencent.mm/MicroMsg` | **exists=false** | **exists=true** |

数据来源：上述两份 observation JSON 的 `entries[]`。

## 3. 探针修正清单（已落地并通过真机验证）

| # | 问题 | 修正 | 位置 |
| --- | --- | --- | --- |
| 1 | 跨卷匹配、只取第一个命中 | 每卷单独 `FindWithinVolumeAsync`，从卷节点出发，不跨卷 | `src/PhoneFiles.Probe.Console/Program.cs` `FindWithinVolumeAsync` |
| 2 | `root` 硬编码 `internal_shared` | 由 `WPD_STORAGE_TYPE` 保守映射：FIXED_RAM→internal_shared / REMOVABLE_RAM→sdcard1 / 其余→unknown；**不靠显示名猜** | `Program.cs` `BuildObservationAsync` |
| 3 | 卷的原始事实无处记录 | schema 新增可选 `volume` 对象（displayName / storageType / functionalCategory / capacityBucket），`root` 枚举加 `unknown` | `rules/schema/observation.schema.json` |
| 4 | Transport 层无法区分卷 | 新增 `VolumeInfo` 记录 + `ITransport` 之外暴露 `MtpTransport.ListVolumesAsync()`，读取 `WPD_STORAGE_TYPE`/`WPD_STORAGE_CAPACITY` | `src/PhoneFiles.Core/ITransport.cs`、`src/PhoneFiles.Transport.Mtp/MtpTransport.cs` |
| 5 | 枚举要求强转 MtpNode | 改为按接口的 `OpaqueId` 枚举，使卷根轻量节点可被枚举 | `MtpTransport.EnumerateAsync` |
| 6 | `TryGetFileTime` 对某些节点抛 NullReferenceException | catch (COMException) → catch (Exception) 保守返回 null | `MtpTransport.TryGetFileTime` |
| 7 | tree 截断提示重复打印 | 加 `PrintCtx.TruncationReported`，只打印一次 | `Program.cs` `PrintTreeAsync` |
| 8 | 伪扩展名 `.removed-20240201-192827` 泄漏文件名片段 | `NormalizeExtension`：仅 `[a-z0-9]`、2–8 字符，异常归 other/丢弃 | `Program.cs` `NormalizeExtension` |

## 4. 微信可见性：谨慎结论（单点，不外推）

- 默认存储（内部存储）：`Android/data/com.tencent.mm` 可见，其下 `MicroMsg` **目录可见**，
  扩展名分布为空、`sizeBucket=0`、`childCountBucket=1-100`。
- **不能据此断言微信聊天数据可见**——目录存在 ≠ 内容可见；`EnMicroMsg.db` 是否可读、
  是否加密均未验证，且属于明确不做范围（v1.0/v1.1 红线：不解密微信数据库）。
- TF 卡上 `MicroMsg` 为 `exists=false`，`Android/data/com.tencent.mm` 可见但空。
- 仍需按 spike 计划在其他 ROM / Android 11+ 机型上做 opt-in ADB 对照，才能回答"可见性天花板"。

## 5. 第二台样本：Redmi Note 7 Pro（单卷对照）

2026-11-21 换机采样。观测：`contributions/observations/redminote7pro-2026-11-21/obs_内部共享存储空间.json`（2800B，10 条 entries，字段级校验通过）。

| 项 | 值 |
| --- | --- |
| WPD 设备 | `\\?\usb#vid_18d1&pid_4ee2&mi_00#8&396fd957&0&0000#{6ac27878-...}` |
| 品牌/型号 | Xiaomi / Redmi Note 7 Pro |
| 卷数量 | **1 个**：`内部共享存储空间`（FIXED_RAM→internal_shared，cap=64-256GB，category=23f05bbc-…） |
| 关键差异 | 与双卷的 Redmi 10X Pro 不同——本机无 TF 卡卷；单卷时不会触发"卷遮蔽"陷阱，但探针仍能正确发现并标注卷型 |
| 可见性 | DCIM/Pictures/Download/Android.data.mm/MicroMsg=true；Camera/Screenshots/Recordings/Sounds/VoiceRecorder=false |
| `Download` 扩展名 | apk:0.62, jpg:0.08, sr0:0.08, png:0.08（与 10X Pro 两卷均不同） |
| 微信 `MicroMsg` | 目录可见，ext 含 tmp（有文件），但 sizeBucket=0——仍需谨慎，目录/临时文件存在 ≠ 聊天数据可读 |
| 系统版本不确定 | ADB `getprop ro.build.version.release=16`/`sdk=36`（异常新，疑第三方 ROM）；但 `ro.build.fingerprint=xiaomi/violet/violet:10/...` 与 `ro.build.display.id=BP4A.251205.006` 指向 Android 10。观测按 MTP 侧只读属性记 os=16/sdk=36，**二者矛盾，未做外推**，列为待核验项 |

**新增机制性发现**：
- 卷数量不是固定的——从 1 到 2+ 都可能（有无 TF 卡）；采样模板必须支持"每台设备 N 个卷"。
- 同名目录 + 多卷 + 排序遮蔽是真实风险；单卷设备看似安全，但不可依赖（下次换机就可能双卷）。
- 系统版本信息存在多源不一致（ADB getprop vs fingerprint）；后续观测应记录来源，避免单一字段误导。

## 7. 第三台样本：真我 GT7 Pro（Android 17 高版本对照）

2026-11-21 采样（第一轮因 WPD 节点 Disconnected 枚举失败，本次节点就绪后成功）。
观测：`contributions/observations/gt7pro-2026-11-21/obs_内部共享存储空间.json`（3005B，10 entries，校验通过）。

| 项 | 值 |
| --- | --- |
| WPD 设备 | `\\?\usb#vid_22d9&pid_2765&mi_00#8&2a5fb97&0&0000#{6ac27878-...}` |
| 品牌/型号 | realme / GT7 Pro（RMX5010） |
| 系统版本 | **Android 17 / SDK 37**（ADB getprop 与 fingerprint `realme/RMX5010/...:17/...`、display.id `17.0.0.112` 多源一致，可信） |
| 卷数量 | 1 个：`内部共享存储空间`（FIXED_RAM→internal_shared，cap=**256GB+**，category=23f05bbc-…） |
| 可见性 | DCIM/Pictures/Download/**Recordings**/Android.data.mm/MicroMsg=true；Camera/Screenshots/Sounds/VoiceRecorder=false |
| 关键差异 | ①`Recordings` 目录首次可见（childCountBucket=0、空目录）；②`Download` size<100MB（前三台中最小）；③目录名大量 OEM 定制（ColorOS/01PersonalFiles/02Projects/Audiobooks 等） |
| 微信 MicroMsg | Android 17 下仍可见，ext={tmp:1.00}、size=0 —— **高版本 Scoped Storage 并未让 MTP 彻底隐藏 MicroMsg 目录**，但仍不能推断聊天数据可读（EnMicroMsg.db 解密属明确不做范围） |

**机制性发现**：Android 11+ 高版本（本机 17）下，MTP 仍可枚举到 `Android/data/com.tencent.mm` 及其 `MicroMsg` 子目录，但内容为临时文件/占位（size=0，ext 仅 tmp）。这修正了 v1.0 文档"Android 11+ 后该目录对 MTP 普遍不可见"的绝对化假设——**目录可见性与数据可读性是两件事**，规则库应分别记录 `exists` 与 `sizeBucket/ext` 证据。

## 8. 待补充（样本不足，不能下 Go/No-Go）

- [ ] 真机台数 3 → 目标 20–30；品牌覆盖 Xiaomi×2 + realme×1，仍不足。
- [ ] Android 11+ 样本：当前 1 台确认（GT7 Pro / Android 17）≥3 目标仍缺 2 台（Redmi Note 7 Pro 版本存疑未计入）；Scoped Storage 结论仅 1 个高版本样本，**不可外推**。
- [ ] opt-in ADB 对照 5–8 台。
- [ ] 用户访谈 10–15 人（尤其"拿到微信数据"是否唯一动机）。
- [ ] 首次连接成功率统计（当前 3 台，其中真我 GT7 Pro 首次因 WPD 节点 Disconnected 枚举失败，重插后就绪；需按"尝试次数/成功次数"口径正式记录）。
- [ ] 校验 `obs_*.json` 通过完整 JSON Schema 校验器（当前为字段级人工核对，见 §9）。

## 9. 当前样本合规性自检（本地核对，非 Schema 校验器）

四份 observation（Redmi 10X Pro 两卷 + Redmi Note 7 Pro 一卷 + 真我 GT7 Pro 一卷）均通过：
- `privacy.filenameSamples = false`
- `privacy.deviceIdHash` 匹配 `^sha256:[0-9a-f]{64}$`
- 顶层必填字段齐全；`root` 在枚举内；`volume.storageType` 在枚举内
- 原始文本无 `removed-20xxxxxx` 类名片段泄漏
- 每份 10 条 entries（含 `exists:false` 一等公民）

## 10. 与模板的口径差异（已修正）

`docs/Spike决策记录-模板.md` 数据源路径已统一为 `contributions/observations/`（原写 `spike-observations/`），并补充"每台设备可能含多个存储卷，须分卷记录"提示。
