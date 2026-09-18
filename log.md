# 项目改动日志

## 2026-09-18 — 阶段四步骤一

- 创建本日志，用于持续记录项目文件、行为与验证状态的改动。
- 任务范围：实现并冻结 `situation-stage4-split-v1` 的 `71 train / 8 dev / 8 test` 比赛级子切分；不进入训练记录、样本选择或数据导出等后续步骤。
- 新增 `SituationDatasetSplit`：锁定父 split SHA-256 和 schema v4.2 语义，校验 87/79/8 父成员、相邻 complete manifest、显式 Demo 目录、直接子文件、扩展名、大小写唯一性、reparse/路径边界和 87 个内容 SHA-256。
- 新增 CLI `--create-situation-stage-four-split`；已有输出拒绝覆盖，结果采用 UTF-8 无 BOM、稳定字段/成员顺序、无时间戳和无绝对路径的确定性 JSON。
- 新增阶段四 split 自动门禁及 npm 入口，覆盖成功路径、字节确定性、跨文化稳定性和主要失败模式。
- 生成并冻结 `situation-implementation/situation-stage4-split-v1.json`：71 train、8 dev、8 test，SHA-256 `fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f`。
- 更新 `TODO.md`、`situation-implementation/TODO_4.md`、项目索引和 context 文档，记录步骤一的实际完成状态、运行方法、设计决策、风险保护与历史闭环。
- 验证结果：Release 构建 0 warning/0 error；阶段四 split 门禁 16 项通过（当前系统不允许创建 symlink，动态 symlink 用例明确跳过）；API 38 项、语义 34 项、v4 15 项及阶段三五套自动门禁全部通过；差异检查通过。

## 2026-09-18 — 阶段四步骤二

- 任务范围：定义训练记录、选择配置、权重、数据清单、标签统计和人工复核工件契约；未开始共享 eligibility、候选选择、实际数据导出或 review server。
- 新增 `situation-training-record-v1` 强类型模型、严格 JSON 读写器、域分隔 `sampleId/matchRef/roundRef`、白名单模型投影、prompt 边界、敏感字段扫描及精确 1/n 回合权重校验。
- 新增版本化的嵌入 `situation-training-selection-v1` 配置候选，明确每回合最多 16 个唯一 tick、稀有覆盖替换低优先级样本且禁止物理复制。
- 新增 split、训练记录、选择、manifest、label stats、review candidate/decision、冻结 review label/manifest 共 9 个阶段四 Draft 2020-12 Schema；manifest 对 12 个依赖/阶段四 Schema 执行版本与 SHA-256 双重验证，并复核工件文件长度、行数和哈希。
- review candidate、decision 和冻结标签独立于基础 JSONL；train 人工标签允许 `recommendedSftRepeat=5`，dev/test 明确禁止 SFT 用途。
- 新增 `npm run test:situation:stage4:contracts`；56 项契约检查通过。Release 构建 0 warning/0 error；API 38、语义 34、v4 15、split 16 和阶段三五套门禁回归通过。
- 版本安全：修改按独立补丁批次写入，全部保持未暂存、未提交、未推送；未覆盖任何已有冻结工件，用户审核前可逐文件或整体撤回。
