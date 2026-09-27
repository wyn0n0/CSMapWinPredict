# CS Demo Map 项目索引

> 最后核验：2026-09-27。源码、测试和本地工件是实现事实的权威来源；本页只提供接续入口。

接触风险修复：修复前 checkpoint `3965b64` / `checkpoint/pre-contact-raycast-20260925`。
`codex/contact-raycast` 的射线、局部探测、连续斜坡及 v8 阈值实现已于 2026-09-27 快进合入本地 `main`，实现提交 `bc30cd8`；合并前主分支回退标签 `checkpoint/pre-merge-main-20260927` 指向 `3965b64`，未推送。
新增三维静态射线规则 `situation-analysis-rules-v2-raycast-1`（不使用导航网格），
通过 20 场 / 6,960 场景抽样重放；历史冻结 v1 和已有复核数据保留。
实现入口、测试结果、资产限制与回退见 [射线修复报告](docs/contact-raycast-report-20260926.md)。

步骤八当前试用 v8-close-exposure-1：已验证水平暴露距离 ≤150 单位判为高风险，超过保持中风险；继承 600 单位局部探测、连续斜坡和 1 秒预测，保留成功即返回。20 场 / 6,960 场景中 500 条 medium→high，其余标签及探测计数不变。666 项定向检查与六版本复核集成通过。
工作区 `datasets/situation-stage4-review-close-exposure-work-20260927-r1`，http://127.0.0.1:5179，50 条（36/7/7）已全部人工 approved，用户于 2026-09-27 确认通过；revision 51，无未复核、拒绝或 blocking issue。reviewVersion 尚未冻结，下一步冻结器需适配用户确认的 50 条范围。旧 v7 / v5 工作区、既有人工决定和 checkpoint `ef387ca` 保留。详见 [150 单位阈值报告](situation-implementation/stage4-close-exposure-report-20260927.md)；[距离说明报告](situation-implementation/stage4-verified-exposure-report-20260927.md) 为 v7 历史。
历史边界见 [连续斜坡报告](situation-implementation/stage4-continuous-slope-report-20260926.md)、[1 秒预测报告](situation-implementation/stage4-position-prediction-report-20260926.md) 与 [局部探测报告](situation-implementation/stage4-local-peek-report-20260926.md)。

此前 [600 单位局部探测实验](situation-implementation/stage4-local-peek-3s-report-20260926.md)（200 单位/秒 × 3 秒）相对 v5 在 20 场新增 473 条 medium，p95 约 1.95 倍；v7 在此实验规则上增加说明，新旧工作区隔离。

## 项目定位

本项目解析 Counter-Strike 2 Demo，生成窗口化回放时间线，并在 Mirage 雷达界面展示回合状态与实时胜率。局势理解路径现已能把目标 tick 之前的结构化观察转换为匿名、可复算、可稳定哈希的场景、Facts 和模板 Narrative；阶段四步骤一至五已获批，步骤六 87 场基础数据与步骤七 300 条候选均 complete 并通过技术验收，候选待用户审核。步骤八本地复核工具已通过技术验收；300 条人工复核、局势 API 和产品前端接入仍在后续阶段。胜率模型训练与回放接入已经完成，两条模型路径不得混同。

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
- 阶段四 eligibility 基线：`situation-implementation/situation-stage4-eligibility-baseline-v1.json`，绑定 checkpoint `34fa03d`、真实 v4.2 的 87 场/1,899 回合/166,122 行及三个完整文件哈希；步骤三已固定在本地 checkpoint `b370b8a`。
- 阶段四候选选择：`SituationTrainingCandidateSelector` 使用版本化配置完成 16 类核心/事件/稀有锚点、1 秒映射、每回合 16 条上限、确定性远点填充与精确 1/n 权重；已固定在步骤四 checkpoint。
- 阶段四步骤四 checkpoint：`b3c3bcb`，未推送。
- 阶段四输入表示：`compact-v1`；配置 SHA-256 `5e0f7b57d127c81b004b5524d2f5b8e6af1193bc8d30b57e56a78d706d7a6a87`。compact 可逆且保留关键事实，但精确 tokenizer 预算尚未验证。
- 阶段四 pilot：`datasets/situation-stage4-pilot-20260919-r10/` 与 `r11/`，各含 71 场、493 回合、500 条；`train.jsonl` SHA-256 `4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1`，全部决定性文件哈希一致。
- 程序边界：`apps/api` 只承载 HTTP 宿主和产品模块，`apps/cli` 承载开发工作流，`tests/CsDemoMap.Api.Tests` 承载 .NET 验证门禁；后端源码位于 `apps/api/Features` 的五个功能模块。

## 活动里程碑

阶段一至三与阶段四步骤一至五已获批；步骤四 checkpoint 为 `b3c3bcb`。步骤六 `datasets/situation-stage4-full-20260919-r1/` 已 complete：71/8/8 场、1,899 回合、30,384 条。步骤七 `datasets/situation-stage4-review-candidates-20260919-r1/` 已 complete：220/40/40 条，全部比赛及配额覆盖通过，与 r2 四文件逐字节一致。步骤五至七差异仍未暂存、未提交、未推送。步骤八本地页面、原子保存与恢复已技术验收；真实人工工作区为 revision 3、两条 approved，300 条人工复核尚未完成。正式局势 HTTP API、前端和文本模型继续留在后续阶段。

## 主要风险与缺口

- Mirage 没有经过验证的楼层和区域邻接定义，楼层必须保持 `unknown`，冻结 v1 不推断区域邻接。
- 旧窗口没有局势 sidecar 时必须显式提供匹配的源 Demo；补建不能恢复已经丢失的源文件，登记状态也不会跨进程保留。
- 上游不可空数值中的零可能是默认值，依赖 `legacy-default-ambiguous` 保守标记。
- 首次文件读取测试不控制操作系统页缓存，只能解释为新服务、无结果缓存且无显式预读的首次文件访问；物理冷盘性能未测量。
- 步骤六 full 工件只含模板预标注，尚不是人工真值；后续复核必须沿用冻结 split、共享 eligibility、逐项版本和 Schema 哈希，不可覆盖输出目录或跨用途复用 dev/test。恢复严格绑定实际源码，源码变化后须新建目录。
- 500 条 pilot 显示 `compact-v1` p95 为 41,106 UTF-8 bytes，500/500 超过候选阈值；阶段五必须用目标 Qwen tokenizer 精确复测，必要时升级表示版本，不能把短键约 9.86% 的 p95 降幅解释为已满足训练序列预算。

## 专题入口

- 当前状态：[docs/context/NOW.md](docs/context/NOW.md)
- 步骤八复核工具验收与运行：[situation-implementation/stage4-review-tool-report-20260919.md](situation-implementation/stage4-review-tool-report-20260919.md)
- 步骤七候选验收与哈希：[situation-implementation/stage4-review-candidates-report-20260919.md](situation-implementation/stage4-review-candidates-report-20260919.md)
- 步骤六完整验收与哈希：[situation-implementation/stage4-full-report-20260919.md](situation-implementation/stage4-full-report-20260919.md)
- 路径与版本：[docs/context/MAP.md](docs/context/MAP.md)
- 运行与验收：[docs/context/RUNBOOK.md](docs/context/RUNBOOK.md)
- 设计决策：[docs/context/DECISIONS.md](docs/context/DECISIONS.md)
- 风险清单：[docs/context/RISKS.md](docs/context/RISKS.md)
- 阶段三置信度整改：[situation-implementation/debug_3.md](situation-implementation/debug_3.md)
- 阶段三保留集复核：[situation-implementation/stage3-acceptance-review-20260915.md](situation-implementation/stage3-acceptance-review-20260915.md)
- 2026-09 完成记录：[docs/context/history/2026-09.md](docs/context/history/2026-09.md)
