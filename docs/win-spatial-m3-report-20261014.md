# M3 第一批验证报告

> 2026-10-14。C4／队形特征的实现、定向测试和小规模真实 Demo 验证已完成。当前结论是计算与导出正确性；未训练新模型，尚无预测收益结论。定义、半径依据与导航前置条件见[实施记录](plans/win-spatial-m3-first-batch-20261014.md)。

## 交付

- [WinSpatialFeatureBuilder](../apps/api/Features/WinPrediction/WinSpatialFeatureBuilder.cs)：实际 C4 最近/次近距离及双方差值、携包人队友距离/邻近人数、包点邻近人数、团队最近队友距离的均值/最大值、明确的缺失/不适用状态与有效人数。
- [WinSpatialDatasetExporter](../apps/api/Features/WinPrediction/WinSpatialDatasetExporter.cs)：`win-spatial-v1` 独立伴随导出，保留标准 v4 基础数据，并保存同 tick 原始证据和输出哈希。
- [独立审计](../tools/audit_win_spatial.py)：验证版本、哈希、键、回合权重，从原始坐标重新计算全部新增字段；损坏、错配与错误数值会失败。
- CLI：`--export-win-spatial <demo-or-directory> <new-output> <support-radius> <site-radius>`。输出必须为新目录。实时 API 和默认模型保持原契约。

## 真实数据结果

输入从旧 79 场训练分区按文件名排序取前三场，Demo 哈希全部与原清单一致。没有使用旧 8 场验证集或新 13 场已揭盲数据决定半径。支援半径 861.320、包点半径 582.716 世界单位，计算依据在导出前冻结；它们只是训练数据给出的初始几何尺度，未按模型效果优化。

| 比赛文件 | 回合 | 采样点 | 独立重算 | 与旧 v4 比较 |
| --- | ---: | ---: | --- | --- |
| `9z-vs-faze-m1-mirage.dem` | 34 | 2,932 | 全部一致 | 所有字段一致 |
| `9z-vs-parivision-m1-mirage.dem` | 22 | 2,318 | 全部一致 | 所有字段一致 |
| `astralis-vs-mouz-m2-mirage.dem` | 22 | 2,059 | 全部一致 | 所有字段一致 |
| 合计 | **78** | **7,309** | **通过** | **通过** |

覆盖 carried 4,632 点、dropped 1,332 点、planting 124 点、planted 1,174 点、defusing 47 点。携包人有队友 4,657 点、没有队友 99 点，另 2,553 点不适用携包人特征。后两种状态分开保存；没有队友的最近距离为空，确认没有附近队友时人数为 0。

基础数据的特征、标签、权重及样本键与旧导出全部相同。真实探针未遇到位置缺失，未知坐标、缺少实体、名单不一致等情况由合成测试覆盖。

对 Astralis–MOUZ 另做完整解析与截止 tick 5096 的截断解析比较，**638 个快照通过**；比较了回放、语义、旧特征及新增空间特征。截止点在首个下包回合内，新增计算不需要先知道该回合结果。合成测试另修改未来位置、未来炸弹状态与最终胜方，早期新增特征不变。

## 耗时及测试

遵照用户要求以 **3 个独立进程并发导出**，各写一个新目录；最终批次总墙钟时间约 **22.91 秒**，单场含启动/哈希/导出的耗时为 22.37 / 18.04 / 16.72 秒。该轮同时有截断解析检查，不作为严格性能基准，也不推算相对串行的加速倍数。

新增伴随导出阶段每场约 0.86–0.87 秒，三场累计约 2.60 秒，约 0.36 毫秒/点；此数包含再次选择基础样本、特征计算、证据与 JSON 写出，**不是在线模型推理延迟**。当前未接入新增模型推理，相关端到端延迟留待 M4。

| 验证 | 本轮结果 |
| --- | --- |
| .NET 构建 | 0 警告、0 错误 |
| `npm run test:groups -- api semantics` | 数据管线 38、语义 34、v4 导出 15、新增空间 50 项检查通过 |
| `python -B -m unittest discover -s tools -p test_audit_win_spatial.py` | 11 项通过 |
| `npm run test:commands` | 5 项通过，含 CLI 帮助覆盖 |
| 三场独立空间审计 | 7,309 点通过，逐回合权重和键一致 |
| 旧 v4 全字段比较 | 7,309 行完全一致 |
| 真实 Demo 截断检查 | 638 个快照通过 |

构建首次在默认沙箱 NuGet 缓存中找不到依赖，随后临时指定本机已有完整缓存完成构建；没有安装或升级依赖。

## 工件与复验

最终工件在本地忽略目录 `.artifacts/win-m3-spatial-20261014-r2/`：

- `pilot-plan.json`：训练来源、原数据及切分哈希、半径、三场成员、3 路并发及冻结时间。
- `producer-snapshot/`、`started.json`：118 个生产/测试/运行文件及其 SHA-256；原件与快照均已回读核对。
- `shard-1/`、`shard-2/`、`shard-3/`：各自的 v4 基础数据、空间特征、源坐标证据和完成清单。
- `audit-1.json` 至 `audit-3.json`、`verification.json`：独立重算、基础数据逐行比较和本轮检查汇总。
- `run_pilot.py`、`verify_pilot.py`、逐进程日志和 `export-results.json`：本地执行与耗时记录。执行脚本会拒绝已有输出；复验既有 shard 使用下面的只读审计命令。

```powershell
python -B tools/audit_win_spatial.py .artifacts/win-m3-spatial-20261014-r2/shard-1
python -B tools/audit_win_spatial.py .artifacts/win-m3-spatial-20261014-r2/shard-2
python -B tools/audit_win_spatial.py .artifacts/win-m3-spatial-20261014-r2/shard-3
dotnet .artifacts/dotnet/bin/CsDemoMap.Api.Tests/release/CsDemoMap.Api.Tests.dll --verify-demo-prefix data/mirage/astralis-vs-mouz-m2-mirage.dem 5096
```

首轮 `r1` 工件保留。收尾时把未知炸弹状态与终局“不适用”进一步区分，随后以最终源码在新 `r2` 目录重新完成三路导出与审计，未改写首轮清单。

M3 开工前，M2 冻结所绑定的 117 个源码/运行文件已按原哈希保存于 `r1/m2-source-snapshot/`。M2 的模型、数据、预测、冻结与结果文件保留原样。当前 CLI/构建已变化，旧 M2 若直接核对当前路径，会按预期报来源不一致；复现应使用原快照，不能回写旧冻结哈希。

## 尚未完成的阶段

二维直线距离不会表示墙体、楼层、绕路或真实支援能力；导航资源和验收条件已列入实施记录，尚未引入。第一批的特征取舍、半径选择、固定切分下的 Log Loss/Brier/校准对照、训练与推理版本配套属于 M4。需先补齐候选实验登记和采用条件，并另行准备未观察的最终测试数据；现有 13 场不能再次充当新候选最终盲测。

本轮未暂存、提交或推送，原工作树中的其他修改保留。
