# 路径与版本地图

> 最后核验：2026-09-18。后端保持模块化单体；目录表达职责，现有命名空间保持兼容。

## 程序边界

| 程序 | 权威路径 | 职责 |
| --- | --- | --- |
| Vue Web | `apps/web/` | 回放界面、窗口缓存、雷达和胜率卡片 |
| .NET API | `apps/api/` | HTTP 宿主和产品功能；`Program.cs` 只负责配置、依赖注入与路由 |
| .NET CLI | `apps/cli/` | Demo 检查、数据导出、sidecar 补建、校准和验收工作流 |
| .NET 验证 | `tests/CsDemoMap.Api.Tests/` | 合成门禁、集成验证和真实 Demo 前缀检查 |
| Python 工具 | `tools/` | v3/v4 审计、训练和模型推理进程 |

API 通过 `InternalsVisibleTo` 只向 CLI 和验证程序集开放必要的内部工作流。验证程序集不被 API 引用；产品工作流也不再调用 Verifier。

## 后端功能模块

| 模块 | 路径 | 主要入口 |
| --- | --- | --- |
| Replay | `apps/api/Features/Replay/` | `DemoParserService`、`DemoImportService`、`DemoWindowSliceBuilder`、回放契约 |
| Semantics | `apps/api/Features/Semantics/` | `DemoSemanticCollector`、`RoundStateTracker`、`RoundClockResolver`、`RoundRosterTracker`、共享 `RoundSampleEligibility` |
| Maps | `apps/api/Features/Maps/` | `MapFeatureGeometry`；当前仅有 Mirage 模型几何 |
| WinPrediction | `apps/api/Features/WinPrediction/` | as-of 特征、v3/v4 导出、Python 客户端和时间线预测 |
| Situation/Contracts | `apps/api/Features/Situation/Contracts/` | 四套 v1 契约、规范化 JSON、跨字段验证和模型白名单 |
| Situation/Scenes | `apps/api/Features/Situation/Scenes/` | Timeline/窗口适配、场景构建、缓存并发和诊断 |
| Situation/Analysis | `apps/api/Features/Situation/Analysis/` | 冻结规则、Facts、evidence 和模板 Narrative |
| Situation/Storage | `apps/api/Features/Situation/Storage/` | 工件 IO、冻结数据集、sidecar 补建和登记 |
| Situation/Training | `apps/api/Features/Situation/Training/` | 阶段四训练/复核强类型契约、冻结选择配置、严格 JSON、Schema/manifest/权重门禁、合格 tick 到 as-of scene 的桥接、16 类候选与每回合 16 条选择 |
| Situation/Workflows | `apps/api/Features/Situation/Workflows/` | 样例导出、阶段二验收、阶段三校准/验收与阶段四 split |

## 当前版本与工件

- 回放窗口协议：`DemoImportService.SchemaVersion = 3`；原窗口、manifest 和 HTTP 路由不变。
- 胜率数据：schema v4、`mirage-semantics-v4.2`；v3 仅用于历史复现。
- 局势契约：`minimap-scene-v1`、`situation-facts-v1`、`situation-narrative-v1`、`situation-analysis-v1`。
- 场景构建器：`situation-scene-builder-v1.3`；几何：`mirage-geometry-v1`。
- 窗口侧车：`situation-window-sidecar-v1`；补建清单：`situation-sidecar-rebuild-v1`。
- 阶段三冻结规则位于 `apps/api/Features/Situation/Analysis/Rules/`，SHA-256 为 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。
- 阶段一验收工件：`datasets/situation-v1-review-20260914-r9/`。
- 阶段二验收工件：`datasets/situation-stage2-acceptance-20260914-r2/`。
- 阶段三冻结训练工件：`datasets/situation-stage3-calibration-frozen-20260915-r1/`。
- 阶段三保留验收工件：`datasets/situation-stage3-acceptance-20260915-r1/`。
- 阶段四 split：`situation-implementation/situation-stage4-split-v1.json`，SHA-256 `fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f`。
- 阶段四契约：`situation-training-record-v1`、`situation-training-selection-v1`、`situation-training-manifest-v1`、`situation-label-stats-v1`、review candidate/decision、冻结 review label/manifest；Schema 位于 `schemas/situation/`。
- 阶段四 eligibility 基线：`situation-implementation/situation-stage4-eligibility-baseline-v1.json`，文件 SHA-256 `a084c525323682722657a1ae4d0fb580e87c445cba2fde8540e534b0b8b0341f`；真实大工件仍位于忽略目录，不进入 Git。
- 阶段四选择核心：`apps/api/Features/Situation/Training/SituationTrainingCandidateSelector.cs`；结果绑定选择配置规范化 SHA-256，包含排序 tick/tags、1/n 权重、类别统计、eligibility 拒绝与短缺，不包含事件文字或回合结果。

模型、数据集和 Demo 是本地对象，均不因源码目录调整而移动、覆盖或进入 Git。
