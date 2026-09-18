# CS Demo Map 项目索引

> 最后核验：2026-09-18。源码、测试和本地工件是实现事实的权威来源；本页只提供接续入口。

## 项目定位

本项目解析 Counter-Strike 2 Demo，生成窗口化回放时间线，并在 Mirage 雷达界面展示回合状态与实时胜率。局势理解路径现已能把目标 tick 之前的结构化观察转换为匿名、可复算、可稳定哈希的场景、Facts 和模板 Narrative；阶段四比赛子切分、训练/复核契约和 schema v4.2 共用 eligibility 均已获批，当前进入候选检测与每回合最多 16 条选择，实际数据导出尚未开始。正式局势 API 和前端接入仍在后续阶段。胜率模型训练与回放接入已经完成，两条模型路径不得混同。

## 当前权威基线

- 回放窗口协议：`DemoImportService.SchemaVersion = 3`，原 `DemoWindow`、`window-*.json.br`、manifest 和 HTTP 路由保持不变。
- 胜率数据：v4 及其匹配导出、切分和模型包；v3 只用于历史复现。
- 局势契约：`minimap-scene-v1`、`situation-facts-v1`、`situation-narrative-v1`、`situation-analysis-v1`。
- 场景构建器：`situation-scene-builder-v1.3`；几何：`mirage-geometry-v1`。
- 窗口语义侧车：`situation-window-sidecar-v1`，独立于原回放窗口协议。
- 旧导入补建清单：`situation-sidecar-rebuild-v1`；源 Demo 显式提供，输出使用独立目录并经进程内登记后生效。
- 样例切分：固定 87 场中的 79/8 清单，不重新抽样。
- 阶段一验收工件：`datasets/situation-v1-review-20260914-r9/`；r8 是同版本确定性复导对照，旧目录只作历史记录。
- 阶段二验收工件：`datasets/situation-stage2-acceptance-20260914-r2/`；含训练侧真实拆除场景、固定跨窗口请求、三种性能状态和完整哈希清单。
- 阶段三冻结规则：`situation-analysis-rules-v1`，配置 SHA-256 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`；candidate-1/2 作为不可变校准历史保留。
- 阶段三冻结训练工件：`datasets/situation-stage3-calibration-frozen-20260915-r1/`；正式保留验收工件：`datasets/situation-stage3-acceptance-20260915-r1/`。
- 局势工件公共设施：`SituationArtifactIO.cs` 与 `SituationFrozenDataset.cs`；阶段一至三的导出与验收路径已统一复用。
- 阶段四比赛切分：`situation-implementation/situation-stage4-split-v1.json`，71 train / 8 dev / 8 test；SHA-256 `fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f`。
- 阶段四契约：`situation-training-record-v1`、`situation-training-selection-v1`、`situation-training-manifest-v1`、label stats、review candidate/decision、冻结 review label/manifest；Schema 文件由版本与 SHA-256 双重绑定，已随本地 checkpoint `34fa03d` 获批。
- 阶段四 eligibility 基线：`situation-implementation/situation-stage4-eligibility-baseline-v1.json`，绑定 checkpoint `34fa03d`、真实 v4.2 的 87 场/1,899 回合/166,122 行及三个完整文件哈希；步骤三已获用户审核通过。
- 程序边界：`apps/api` 只承载 HTTP 宿主和产品模块，`apps/cli` 承载开发工作流，`tests/CsDemoMap.Api.Tests` 承载 .NET 验证门禁；后端源码位于 `apps/api/Features` 的五个功能模块。

## 活动里程碑

阶段一、阶段二与阶段三冻结 v1 均已完成技术闭环。阶段四步骤一、二已固定在本地 checkpoint `34fa03d`；步骤三已抽取 v4.2/阶段四共用的 outcome-free eligibility，通过真实工件、小夹具和未来信息不变性门禁并获用户审核。当前实施步骤四的候选选择算法；实际导出和人工复核仍未开始，正式局势 HTTP API、前端和文本模型接入继续留在后续阶段。

## 主要风险与缺口

- Mirage 没有经过验证的楼层和区域邻接定义，楼层必须保持 `unknown`，冻结 v1 不推断区域邻接。
- 旧窗口没有局势 sidecar 时必须显式提供匹配的源 Demo；补建不能恢复已经丢失的源文件，登记状态也不会跨进程保留。
- 上游不可空数值中的零可能是默认值，依赖 `legacy-default-ambiguous` 保守标记。
- 首次文件读取测试不控制操作系统页缓存，只能解释为新服务、无结果缓存且无显式预读的首次文件访问；物理冷盘性能未测量。
- 阶段四尚未实际导出数据；后续候选选择、导出与人工复核必须沿用冻结 split、共享 eligibility、逐项版本和 Schema 哈希，不可覆盖输出目录或跨用途复用 dev/test。

## 专题入口

- 当前状态：[docs/context/NOW.md](docs/context/NOW.md)
- 路径与版本：[docs/context/MAP.md](docs/context/MAP.md)
- 运行与验收：[docs/context/RUNBOOK.md](docs/context/RUNBOOK.md)
- 设计决策：[docs/context/DECISIONS.md](docs/context/DECISIONS.md)
- 风险清单：[docs/context/RISKS.md](docs/context/RISKS.md)
- 阶段三置信度整改：[situation-implementation/debug_3.md](situation-implementation/debug_3.md)
- 阶段三保留集复核：[situation-implementation/stage3-acceptance-review-20260915.md](situation-implementation/stage3-acceptance-review-20260915.md)
- 2026-09 完成记录：[docs/context/history/2026-09.md](docs/context/history/2026-09.md)
