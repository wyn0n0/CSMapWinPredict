# 当前状态

> 最后核验：2026-09-19。

## 当前目标

阶段一至三与阶段四步骤一至五已获批，步骤四 checkpoint 为 `b3c3bcb`。步骤六正式 `datasets/situation-stage4-full-20260919-r1/` 保持 complete：71/8/8 场、1,899 回合、30,384 条。用户授权的步骤七现已通过技术验收：`datasets/situation-stage4-review-candidates-20260919-r1/` 为 complete，220/40/40 条、覆盖全部 87 场、配额无短缺，与 r2 四文件逐字节一致。报告见 `situation-implementation/stage4-review-candidates-report-20260919.md`；等待用户审核候选。全部差异未暂存、未提交、未推送；步骤八工具已按主线 GPT-6 Astra / high、子智能体 GPT-6 Astra / low 完成技术验收；真实人工工作区现场为 revision 3、两条 approved。报告见 `situation-implementation/stage4-review-tool-report-20260919.md`。

## 已完成闭环

- 四套 v1 契约和 Draft 2020-12 Schema。
- `situation-scene-builder-v1.3`：4.5 秒同回合历史、匿名槽位、Mirage 坐标、C4/队伍/几何、质量码、与内部源 ID 无关的完整排序、规范化 JSON 和 SHA-256。
- `situation-window-sidecar-v1` 与按需读取上一 Brotli 窗口的适配；旧回放窗口协议未改。
- `SituationSceneService` 统一运行时导入、离线 Timeline 和已验证窗口入口，集中完成契约校验、规范化 JSON 与 SHA-256；样例导出已接入。
- `situation-sidecar-rebuild-v1` 提供显式源 Demo、旧导入目录和新输出目录的本地补建；逐窗口核对来源并保持原文件不变，完整结果需显式登记才由场景服务采用，撤销登记即可回退。
- 导入与补建共用 `DemoWindowSliceBuilder`；补建不创建胜率推理服务，失败/取消目录保持 `incomplete`，登记后的受管理文件变化会明确失败。
- 文件来源场景使用有界 LRU 结果缓存，默认 256 项/64 MiB；键包含当前/上一窗口及 sidecar 的内容 SHA-256、Demo、窗口、请求 tick 和三项场景版本，缓存值只保存规范化 JSON 与哈希，命中返回独立反序列化对象。
- 同键并发共享一次底层构建；局势服务独立限制为同时构建 2 个、排队 32 个。单调用方取消不影响其他等待者，全部取消会立即释放同键索引并取消旧任务，后续请求可立即重试。
- 运行时局势入口使用 12 类稳定错误码与固定安全消息供阶段七映射；取消保持原语义。结构化诊断覆盖缓存状态、读取窗口数、分阶段耗时和错误码，且不记录路径、身份、Demo 引用、完整场景或原始异常。
- `--verify-situation-stage-two-automatic` 统一串联六套门禁；除原有场景与服务覆盖外，新增等长同时间戳内容替换、取消后即时重试和冻结切分哈希/成员关系检查。
- `--run-situation-stage-two-acceptance` 使用冻结训练来源生成真实拆除场景、固定跨窗口请求和三种缓存/文件状态性能报告；输出目录必须不存在，失败保留 `incomplete` 清单。
- 阶段二验收工件 `datasets/situation-stage2-acceptance-20260914-r2/` 已完成：真实 `defusing` tick 122432，倒计时 4.984 秒，历史 4.5 秒；内容寻址后的预热文件且结果未命中 p95 57.29 ms，通过 100 ms 场景服务门限。
- 服务与窗口适配器显式校验导入完成状态、manifest 窗口数、Mirage、请求核心范围、数值溢出、窗口/sidecar 边界和重复冲突；as-of、4.5 秒历史、效果生命周期及质量码保持原语义。
- 显式 CLI：导出样例、验证契约、验证真实 Demo 前缀；没有正式局势路由。
- 统一门禁内契约/流程 64 项、场景服务 35 项、缓存/并发 17 项、sidecar 补建 29 项、错误/诊断 32 项、冻结切分 5 项通过；附加 r9 后局势契约 96 项。Release 构建 0 警告/0 错误。
- r9 验收工件 32 条，八类覆盖均不少于 4；写后复验通过，与同版本 r8 重复导出哈希完全一致。
- 61 个合成/流程检查与 32 个真实场景检查通过；原有 Web、API 数据管线、语义和 v4 门禁通过。
- 阶段三增加强类型嵌入规则、严格未知/缺失/重复字段验证、无状态 Facts 分析器、稳定 evidence、确定性模板和组合哈希；获批冻结版本为 `situation-analysis-rules-v1`，SHA-256 为 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。
- `--verify-situation-stage-three-automatic` 串联阶段二六套门禁、159 项规则/边界测试、r9 32 场景重算，以及 candidate、冻结训练、正式保留三类工件的清单/哈希/契约重放，共五套阶段三门禁。置信度规格已统一为 `low → medium → high` 优先级，并补齐相关 error、1/2 个未完成规则和边界带内外回归；冻结规则及输出语义未变。
- 冻结训练工件 `datasets/situation-stage3-calibration-frozen-20260915-r1/` 含 5 场 30 条固定场景，manifest complete；热态 p95 1.7779 ms，Scene 未命中组合 p95 18.2808 ms。
- 保留请求承诺 SHA-256 为 `6ff14b6c912bf6fdb9a221fbb91920c88eaa91fb87ad6693c69c1ebeee5b8ff7`。唯一一次正式验收工件 `datasets/situation-stage3-acceptance-20260915-r1/` 覆盖 8 场 16 条，16/16 逐字段通过且阻断错误为 0；热态/组合 p95 为 1.3468/7.5401 ms。
- 工件公共层已收敛到 `SituationArtifactIO` 与 `SituationFrozenDatasetLoader`：阶段一样例、阶段二验收、阶段三校准/验收和 sidecar 补建共用 SHA-256/原子写入/文件清单/仓库定位，冻结切分只保留一套严格 87/79/8 成员校验。
- 架构入口已收敛：`apps/api/Program.cs` 只启动 HTTP 服务；导出、诊断和验收命令进入 `apps/cli`；19 个 Verifier 进入 `tests/CsDemoMap.Api.Tests`；`package.json` 保留统一测试入口。
- `situation-stage4-split-v1` 已冻结：CLI 显式接收 Demo 目录、父 79/8 split 和新输出文件，拒绝父哈希/manifest/成员/路径/内容漂移及已有输出；真实 87 场逐文件 SHA-256 通过，结果为 71 train / 8 dev / 8 test，工件 SHA-256 为 `fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f`。
- 阶段四步骤二已获用户批准并固定在本地 checkpoint `34fa03d`：新增强类型 `situation-training-record-v1`、版本化选择配置、数据/标签/复核工件模型和 9 个阶段四 Draft 2020-12 Schema；记录采用严格完整形状、稳定字段顺序和 UTF-8 无 BOM 单行 JSON，未知/缺失/重复/非有限数/非法枚举/非规范数组均失败。
- 模型边界只投影 `input` 与 `output`；场景移除来源、窗口、请求 tick 和真实 round ID，metadata/provenance 不进入 prompt。摘要身份、完整上游版本、1/n 回合权重、敏感字段扫描、review 分流和 Schema 版本+SHA-256 校验均有自动门禁。
- `RoundSampleEligibility` 成为 v4.2 和阶段四唯一的完成回合/时点合格性判断；结果只含 `Eligible/ReasonCode`，原有 `round-unconfirmed`、阵容 reasons、时钟来源和 `alive-snapshot-mismatch` 语义保持不变。
- `SituationEligibleSceneBuilder` 只为合格 tick 调用 `SituationSceneService.BuildFromTimeline`；赢家、结束原因及目标后 TimelineEvent/装备状态变化不会改变选择、模型输入或冻结 prelabel 哈希。
- `situation-stage4-eligibility-baseline-v1.json` 绑定获批 checkpoint、真实 v4.2 的 87 场/1,899 完成回合/166,122 行/0 拒绝及完整 samples SHA-256 `5ec3d56b265a1f51ddeb5c228f75cfbfdb966cae8b252afd80ef34aadf8442cc`；小夹具规范化 JSONL SHA-256 保持 `3c0a300bcdb1ee94f508206e04b57294122b48d1948e2f71aea80500b74b7cdf`。
- `npm run test:situation:stage4:eligibility` 当前通过 46 项；步骤三已获批并固定在本地 checkpoint `b370b8a`，未推送。
- `SituationTrainingCandidateSelector` 只消费同回合结构化快照、冻结 Facts 和事件类型；实现 live/deployment/tail、首次接触/伤害/减员、四类 C4、2vN/1vN 和四类稀有 Facts 锚点。事件文字、赢家、结束原因和目标后事件不会进入选择结果。
- 所有选择阈值、类别顺序、1 秒映射容差、post-plant 偏好、16 条上限和 tie-breaker 均由嵌入 `situation-training-selection-v1` 与 Schema 约束；同 tick 合并并 ordinal 排序标签，剩余名额使用最大最小 tick 距离填充。
- 每回合输出 `min(16, uniqueEligibleTicks)`，每条保存精确 `1/n` 分子/分母与数值权重；类别统计覆盖候选、入选、合并、上限移除、缺失锚点及 eligibility 拒绝原因。
- `npm run test:situation:stage4:selection` 当前通过 148 项，包含同一完整 Timeline 重复 100 次、跨文化/输入乱序、0/1/15/16/>16、事件超时、跨回合隔离和 outcome/event-text/future 不变性；阶段四 contracts 当前为 58 项。
- `SituationTrainingPilotExporter` 只接受冻结 71 场 train 和精确 `--pilot 500`；按稳定摘要顺序 round-robin 分配前三场各 8 条、其余各 7 条，再以事件/稀有标签全局贪心覆盖。pilot 只写 train、split、表示测量、统计、provenance 和 manifest，不创建 dev/test/review 文件。
- 训练记录由同一 eligibility、场景、Facts、模板 Narrative 和选择链构建；真实数据暴露的旧 C4 carrier/defuser 残留被按当前 bomb state 掩码，训练场景则显式沿用目标 tick 的 v4.2 semantic phase/round，避免原始 frame 回合号覆盖权威语义。第 65–71 场真实来源重放全部通过。
- prompt 表示配置 `situation-prompt-representation-config-v1` 同时冻结 expanded/compact 包装和短键，配置 SHA-256 为 `5e0f7b57d127c81b004b5524d2f5b8e6af1193bc8d30b57e56a78d706d7a6a87`；compact 可精确还原模型输入 JSON，完整训练行不压缩。
- `datasets/situation-stage4-pilot-20260919-r10/` 与 `r11/` 均为 complete：71 场、493 回合、500 条，`train.jsonl` 17,188,098 字节、SHA-256 `4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1`；双目录 6 个文件哈希全部一致，20 项工件门禁通过。
- compact p95 为 41,106 UTF-8 bytes，比 expanded 的 45,602 bytes 小 9.86%，但两者均有 500/500 超过候选阈值；因此只冻结 compact-v1 为当前较小、可逆表示，不把它解释为序列预算通过。精确 Qwen tokenizer 复测仍是阶段五硬门禁。

## 下一步

步骤七已按 [并行方案](../../situation-implementation/step7-multi-agent-execution.md) 完成：主线 `gpt-6-astra / medium`、子智能体 `gpt-6-astra / low`。实现、覆盖、哈希和验收见 [步骤七报告](../../situation-implementation/stage4-review-candidates-report-20260919.md)。

1. 步骤八工具技术验收通过，见 [验收报告与启动命令](../../situation-implementation/stage4-review-tool-report-20260919.md)。真实 work 为 `datasets/situation-stage4-review-work-20260919-r1/`，revision 3、两条 approved；随后实施步骤九 freezer。拒绝/替换 audit、原子恢复和 blocking 已实现；最终标签和 reviewVersion 未冻结。基础与候选保持不可变，不自动提交或推送。
2. 阶段五取得目标 Qwen tokenizer 后，必须对冻结 500 条重新测量精确 token 数；超预算时升级表示版本并生成新目录。
3. 正式 HTTP 路由、错误映射和跨进程补建登记留到阶段七。

## 短期阻塞

Mirage 楼层与区域邻接仍没有经过验证的定义，冻结 v1 保守保持 floor unknown 且不推断区域邻接。保留集已正式观察；任何后续规则修订仍必须只在训练侧选择。`compact-v1` 的字节长度明显超过候选阈值，尚不能据此承诺阶段六序列预算。
