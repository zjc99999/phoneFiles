# 贡献指南

感谢你愿意让 PhoneFiles 认识更多机型。本文件的每一条都请认真读——尤其是**脱敏红线**和**贡献即授权**。

## 两种贡献方式

1. **代码贡献**：修 bug、加功能、改进 WPD/ADB 传输层、写测试。走标准 GitHub PR 流程。
2. **观测贡献**：提交一份「脱敏真机观测」，帮助规则库覆盖你的机型/ROM。这是规则库主要的增长方式。

## ★ 脱敏红线（提交观测前必读）

一条观测里**绝不能**包含以下内容，发现即拒绝：

| 禁止项 | 说明 |
| --- | --- |
| 文件名 / 文件内容 | 只允许目录名（`nodePattern`）与扩展名分布比例 |
| 账号 hash 原文 | 微信 `MicroMsg/<hash>` 里的 hash 段必须丢弃，只保留到 `MicroMsg/` 这一层 |
| 设备唯一标识原文 | IMEI、序列号、Android ID 原文一律不传；只允许去标识后的 `deviceIdHash`（加盐 sha256） |
| 联系人 / 短信 / 通话记录 | 任何个人通信数据 |
| 截图中的个人信息 | 不要附带含文件名或照片的截图 |

允许上传的最小集合：`oem/model/osVersion/sdk/romBuild/region`、通道类型 `channel`、目录名 `nodePattern`、存在性与可否枚举、`childCountBucket`、`sizeBucket`、`mtimeRange`、扩展名分布 `fingerprint`、去标识 `deviceIdHash`、客户端版本。

## 贡献即授权

- 提交代码即表示你同意该代码按仓库 [LICENSE](LICENSE)（Apache-2.0）授权。
- **提交观测即表示你同意该观测数据按 [LICENSE-DATA](LICENSE-DATA)（CC BY-SA 4.0）授权**，且你确认已按上面的脱敏红线处理、有权提交该数据。
- 若你不同意上述授权，请不要提交。

## 观测提交流程

1. 用客户端「未识别目录 → 提交观测」功能生成 `observation` JSON/YAML；或手动套用 [contributions/observation.example.yml](contributions/observation.example.yml) 模板。
2. 对照 [rules/schema/observation.schema.json](rules/schema/observation.schema.json) 自查字段。
3. 自动校验：节点存在性、指纹格式、同条件冲突检测、去标识扫描。不通过直接拒绝并说明原因。
4. 自动通过后进入「待复核」队列，由 2 名 trusted contributor 确认后进入稳定通道。

## 观测质量准则

- **必须真机**：不要凭记忆或猜写路径。观测的价值在于「我实际看到了什么」。
- **记录「看不到」同样重要**：`exists: false` / `canEnumerate: false` 与 `exists: true` 一样有价值——它告诉规则库某通道的天花板。
- **通道要分清**：同一台机器 MTP 看不到、ADB 能看到，是两条不同观测，`channel` 不能混填。
- **带全设备指纹**：OEM / model / osVersion / sdk / romBuild / region 越全，聚类越准。

## 规则是怎么来的（别直接手写规则）

规则不是人工维护的真相，而是观测的**派生品**：

```
观测 → 自动校验 → 聚类(OEM+ROM族+SDK区间+App包+通道) → 规则(confidence/evidence[]/version/since/supersedes)
     → 2 人 review → 灰度 5% → 全量 → 版本化+签名下发 → 客户端内置 baseline 兜底
```

想改某类目录的语义，请提交能支撑该改动的观测，而不是直接编辑 `rules/rules/*.yml`。直接改规则文件会被 review 打回。

## 冲突怎么处理

同条件出现多个候选目录时，**全部保留、按置信度排序展示**，UI 显示「多个候选」，不强行覆盖。所以提交不同结果不是坏事——只要都是真机观测。

## 代码贡献补充

- 传输层必须实现统一的 `ITransport` / `IFileSystemNode` 抽象，**不得在 Core 层引入字符串路径参数**（MTP 没有路径）。
- 规则引擎、校验、清单生成为纯逻辑、可单测、无平台依赖的独立层。
- 新增传输适配器（如鸿蒙）放在 `src/Transport.*/`，不得修改 Core 的路径假设。
- 提交前跑通现有测试；新增行为请附单测。

## 行为准则

尊重其他贡献者。争议以真机观测数据与文档裁决为准，不搞人身争论。维护者保留拒绝任何不符合脱敏红线或授权条款贡献的权利。
