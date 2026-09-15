# 路径与版本地图

> 最后核验：2026-09-15。

| 范围 | 权威路径 | 当前版本或用途 |
| --- | --- | --- |
| 局势模型 | `apps/api/Models/SituationModels.cs` | 四套 v1 公开契约 |
| 窗口侧车模型 | `apps/api/Models/SituationWindowModels.cs` | `situation-window-sidecar-v1` |
| Timeline 输入 | `apps/api/Services/SituationInputAdapter.cs` | 未来信息裁剪和状态锚点 |
| 窗口输入 | `apps/api/Services/SituationWindowPipeline.cs` | Brotli/sidecar 合并和冲突检测 |
| 共享窗口切分 | `apps/api/Services/DemoWindowSliceBuilder.cs` | 导入与旧 sidecar 补建共用 30 秒核心窗口和 2 秒重叠逻辑 |
| 旧 sidecar 补建 | `apps/api/Services/SituationSidecarRebuilder.cs` | `situation-sidecar-rebuild-v1` 清单、来源核对、独立输出、登记与撤销 |
| 场景构建 | `apps/api/Services/SituationSceneBuilder.cs` | `situation-scene-builder-v1.3` |
| 统一场景服务 | `apps/api/Services/SituationSceneService.cs` | 导入任务、Timeline、窗口 → 边界校验、场景、规范化 JSON、SHA-256 |
| 场景执行控制 | `apps/api/Services/SituationSceneExecution.cs` | 文件来源 LRU、来源/版本缓存键、同键合并、2 个构建槽与 32 个排队上限 |
| 场景错误与诊断 | `apps/api/Services/SituationSceneDiagnostics.cs` | 12 类稳定错误码、安全消息、窗口读取计数和分阶段结构化耗时 |
| 共享工件基础设施 | `apps/api/Services/SituationArtifactIO.cs`、`SituationFrozenDataset.cs` | UTF-8 无 BOM 原子写入、SHA-256、递归文件清单、仓库/Git 定位，以及严格 87/79/8 切分与 manifest 成员加载 |
| 阶段二验收入口 | `apps/api/Services/SituationStageTwoAcceptance.cs`、`situation-implementation/stage2-acceptance-request-v1.json` | 训练来源校验、真实拆除样例、固定跨窗口请求、三种性能状态与 100 ms 门禁 |
| 阶段三规则配置 | `apps/api/SituationRules/`、`SituationAnalysisRuleLoader.cs` | 嵌入式 candidate 历史与冻结 `situation-analysis-rules-v1`；严格加载、规范化配置 SHA-256 |
| 阶段三分析核心 | `SituationFactsAnalyzer.cs`、`SituationEvidenceBuilder.cs`、`SituationTemplateNarrator.cs`、`SituationDeterministicAnalyzer.cs` | scene → Facts/evidence → 模板 Narrative，内部无状态且稳定哈希 |
| 阶段三校准与验收 | `SituationStageThreeCalibration.cs`、`SituationStageThreeHoldoutRequestBuilder.cs`、`SituationStageThreeAcceptance.cs`、`SituationStageThreeCalibrationVerifier.cs`、`SituationStageThreeAcceptanceVerifier.cs` | 固定训练场景、candidate/冻结对比、保留请求承诺、一次性验收及完整工件重放 |
| 阶段三请求与复核 | `situation-implementation/stage3-calibration-request-v1.json`、`stage3-frozen-calibration-request-v1.json`、`stage3-holdout-request-v1.json`、`stage3-acceptance-review-20260915.md` | 30 条训练 scene 哈希、8 场/16 tick 保留承诺与逐字段通过记录 |
| 模型白名单 | `apps/api/Services/SituationModelInputProjector.cs` | 排除路径和运行元数据 |
| Schema | `schemas/situation/` | 四份 Draft 2020-12 Schema |
| 验证 | `SituationStageTwoAutomaticVerifier.cs` 统一调用 `SituationContractVerifier.cs`、`SituationSceneServiceVerifier.cs`、`SituationSidecarRebuilderVerifier.cs`、`SituationSceneExecutionVerifier.cs`、`SituationSceneDiagnosticsVerifier.cs` | 合成/流程、跨入口深度一致、补建兼容与中断、缓存/并发、错误/诊断、匿名/版本和样例门禁 |
| 样例导出 | `SituationSampleExporter.cs` | 拒绝覆盖、incomplete → complete |
| 当前验收工件 | `datasets/situation-v1-review-20260914-r9/` | 32 条冻结样例 |
| 阶段二验收工件 | `datasets/situation-stage2-acceptance-20260914-r2/` | 真实拆除场景、固定请求、600 次性能测量、17 个文件哈希 |
| 阶段三冻结训练工件 | `datasets/situation-stage3-calibration-frozen-20260915-r1/` | 冻结规则、5 场 30 条、candidate 对比、规则裕量、性能及保留请求承诺；manifest complete |
| 阶段三保留验收工件 | `datasets/situation-stage3-acceptance-20260915-r1/` | 8 场 16 条、Facts/Narrative、逐字段复核、性能及完整哈希；manifest complete |

r8 与 r9 的 32 个场景哈希完全相同；r9 包含冻结后的样例选择版本、写后复验、复核与来源证明。阶段二 r1 工件只使用训练 Demo 进行样例选择和性能测量，保留侧只运行冻结契约回归。此前阶段一 r1–r7 仅保留为演进记录，不回写、不覆盖。
