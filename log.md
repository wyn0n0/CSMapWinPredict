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

## 2026-09-18 — 阶段四步骤三

- 用户已审核步骤二并授权本地提交；步骤一、二固定为 checkpoint `34fa03d`，未推送。步骤三在该提交之上保持未暂存、未提交，仍可整体或逐文件撤回。
- 新增共享 `RoundSampleEligibility`，统一 Mirage、完成回合、live 半开区间、phase、回合号、时钟、阵容和存活快照门禁；结果只含 `Eligible/ReasonCode`，不携带赢家、结束原因或训练标签。
- `WinFeatureSampleBuilder` 改用共享 helper，既有 v4.2 拒绝原因和值的优先顺序保持不变；阶段四新增 `SituationEligibleSceneBuilder`，仅对合格 tick 调用既有 `SituationSceneService.BuildFromTimeline`。
- 新增 `situation-stage4-eligibility-baseline-v1.json`：绑定 checkpoint `34fa03d`；锁定真实 v4.2 的 87 场、1,899 完成回合、166,122 行、0 拒绝，以及 manifest/comparison/samples 文件长度和完整 SHA-256。491,811,329 字节 samples 的 SHA-256 为 `5ec3d56b265a1f51ddeb5c228f75cfbfdb966cae8b252afd80ef34aadf8442cc`。
- 固定合成夹具重导后仍为 2 行、tick 64/128、每条权重 0.5、总权重 1；规范化 JSONL SHA-256 仍为 `3c0a300bcdb1ee94f508206e04b57294122b48d1948e2f71aea80500b74b7cdf`。另锁定四种旧拒绝原因和 tick 顺序。
- 未来信息不变性门禁确认：赢家 T/CT、结束原因、目标 tick 后 TimelineEvent/RoundResult/装备状态变化均不改变阶段四选择、模型输入或冻结 prelabel 哈希；不合格 tick 不触发 scene 构建。
- 验证结果：Release 构建 0 warning/0 error；阶段四 eligibility 46 项、contracts 56 项、API 38 项、语义 34 项、v4 15 项和阶段三五套自动门禁通过；真实 492 MB v4.2 samples 已执行全量哈希核验。
- 用户已审核通过步骤三；批准内容可固定为本地 checkpoint，未授权推送。

## 2026-09-18 — 阶段四步骤四

- 步骤三已提交为本地 checkpoint `b370b8a`，未推送；步骤四在该提交之上保持未暂存、未提交，可独立撤回。
- 新增 `SituationTrainingCandidateSelector`，严格按 round ID 隔离完整回合结构化状态，只把事件 type/tick 送入选择核心；内部玩家 ID 仅用于相邻合格快照伤害比较，不进入选择结果。
- 实现 16 类核心/事件/稀有候选：live、部署、尾段、首次接触/伤害/减员、四类 C4、2vN/1vN、post-plant、分路、孤立和高接触风险。事件锚点只映射到 1 秒内首个不早于事件的合格帧，超限和仅有 kill 无状态下降均记录短缺。
- 同 tick 标签合并并按 ordinal 排序；核心、事件、稀有优先级后使用最大最小 tick 距离填满，完全并列取较早 tick 再取稳定候选摘要。输出按 tick 排序、唯一且最多 16 条，每条带 1/n 分子/分母和值，总权重为 1。
- `situation-training-selection-v1` 与 Schema 补齐 round-tail、1vN/2vN 阈值和 post-plant 偏好；结果记录配置哈希、逐类候选/入选/合并/上限移除/缺失、eligibility 拒绝与短缺。
- 新增 `npm run test:situation:stage4:selection`；148 项覆盖全类别、缺失、同 tick 合并、映射超时、0/1/15/16/>16、远点并列、跨回合、输入乱序、四种文化区、同一完整 Timeline 100 次重复和 outcome/event-text/future 不变性。Release 构建 0 warning/0 error，eligibility 46、contracts 58、API 38、语义 34、v4 15 和阶段三五套门禁回归通过。
