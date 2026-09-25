# 路径与版本地图

> 最后核验：2026-09-19。后端保持模块化单体；目录表达职责，现有命名空间保持兼容。

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
| Situation/Training | `apps/api/Features/Situation/Training/` | 阶段四训练/复核强类型契约、冻结选择与 prompt 表示配置、严格 JSON、Schema/manifest/权重门禁、合格 tick 到 as-of scene 的桥接、16 类候选与每回合 16 条选择 |
| Situation/Workflows | `apps/api/Features/Situation/Workflows/` | 样例导出、阶段二验收、阶段三校准/验收、阶段四 split、pilot/full、逐场 checkpoint/resume、统计/provenance/validator |

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
- 阶段四 prompt 表示：`compact-v1`，配置 SHA-256 `5e0f7b57d127c81b004b5524d2f5b8e6af1193bc8d30b57e56a78d706d7a6a87`；短键和包装文本来自嵌入配置，完整训练 JSONL 保持未压缩。
- 阶段四 pilot：`datasets/situation-stage4-pilot-20260919-r10/` 与 `r11/`，均为 complete 的 71 场/493 回合/500 条 train-only 工件；`train.jsonl` SHA-256 `4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1`。书面结论见 `situation-implementation/stage4-pilot-report-20260919.md`。

- 阶段四 full：`datasets/situation-stage4-full-20260919-r1/`，complete，71/8/8 场、1,899 回合、30,384 条；基础根严格七文件，不含 review candidates。实际源码 85 文件、Schema 14 项、87 个 Demo 与 1,899 个回合映射由 provenance 绑定；完整清单见 `situation-implementation/stage4-full-report-20260919.md`。
- 步骤六门禁：`npm run test:situation:stage6`；实际 A/B/C 与集成专项位于 `tests/CsDemoMap.Api.Tests/Situation/`。性能和真实恢复诊断保留在忽略的 `datasets/situation-stage6-*`，均非训练工件。

- 步骤七正式候选：`datasets/situation-stage4-review-candidates-20260919-r1/`；r2 是逐字节相同的复导对照。两者严格四文件，含 220/40/40 候选、全池来源索引与后备排序，不是最终人工标签。只读基础 full，不同步或覆盖基础目录。
- 步骤七代码：`Training/SituationReviewSelectionPolicy.cs`、`SituationReviewCandidateSelector.cs` 和冻结 JSON；`Workflows/SituationReviewCandidate*.cs` 负责读取、统计、来源、导出与验证。五个新增 review Schema 不加入历史基础 Schema 注册表。门禁入口 `npm run test:situation:stage4:review-candidates`。

模型、数据集和 Demo 是本地对象，均不因源码目录调整而移动、覆盖或进入 Git。


### 阶段四步骤八本地复核（2026-09-19）

- `apps/api/Features/Situation/Review/`：独立协议、历史loader、决定/替换、工作区存储和消费者来源。
- `apps/cli/SituationReview/` 与 `SituationReviewUi/`：独立loopback宿主和静态复核页；不挂载产品路由。
- `datasets/situation-stage4-review-source-snapshot-20260919-r1/`：本地历史生产源码快照，SHA验证，不是训练数据。
- `datasets/situation-stage4-review-work-20260919-r1/`：真实可变work，验收时0/300；与基础/候选不可变目录分离。
- `datasets/situation-stage4-review-ui-smoke-20260919-r1/`：合成浏览器演练，禁止进入人工标签冻结。
- 独立 work Schema 与四套 .NET verifier、Node 页面测试；详见步骤八报告。
