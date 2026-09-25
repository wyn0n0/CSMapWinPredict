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

## 2026-09-19 — 阶段四步骤五

- 用户审核通过步骤四；步骤四已固定为本地 checkpoint `b3c3bcb`，未推送。步骤五全部源码、Schema、文档和测试差异继续保持未暂存、未提交，可在审核前整体或逐文件撤回。
- 扩展训练工件模型与 manifest：新增 pilot/full mode、`purpose=representation-measurement`、`trainable=false`、样本上限、输入表示配置/测量版本及哈希；pilot manifest 只允许 train 计数和固定 6 文件集合。
- 新增嵌入 `situation-prompt-representation-v1.json`、`SituationPromptRenderer` 及两个 Schema，同时测量 expanded-v1、compact-v1、完整记录和模型输入的 UTF-8 bytes、Unicode 字符数、最大行长与最大嵌套数组长度。短键和 prompt 包装文本均进入规范化配置哈希，compact 可精确还原模型输入 JSON。
- 新增 `SituationTrainingPilotExporter` 与 CLI `--export-situation-training-data <demo-dir> <split> <new-output> --pilot 500`：只接受冻结 71 场 train 和精确 500 条上限；稳定摘要 round-robin 分配前三场各 8 条、其余各 7 条，再按事件/稀有标签做全局贪心覆盖。输出只含 train、split、表示测量、统计、provenance 和 manifest。
- record builder 复用同一共享 eligibility、`SituationSceneService`、冻结 Facts 分析器、模板 Narrative、Schema、evidence/哈希/权重/敏感扫描；每条保留完整结构化记录，prompt renderer 只读取 input/output。
- 真实数据导出发现旧 frame 可能遗留与当前 C4 state 不相容的 carrier/defuser/site/timer/position 字段；`SituationSceneBuilder.BuildBomb` 现按当前 state 掩码，并用 `legacy-default-ambiguous` 标注被清除路径。新增 stale defuser 回归。
- 真实数据第 65 场发现 raw frame round number 可与 v4.2 semantic frame 不一致；`SituationInputAdapter`、`SituationSceneService` 和 `SituationEligibleSceneBuilder` 增加精确 tick 的 semantic override，使权威 phase/round 延续到训练 scene。新增服务回归和 source verifier phase 门禁。
- 新增 pilot 合成门禁 18 项、complete 工件门禁 20 项与真实单 Demo 来源重放入口；第 65–71 场依次通过，合计 142 个完成回合、2,272 个选择器样本。
- `r1`–`r9` 因迭代停止、异常或用户中断保留为 `status=incomplete` 诊断目录，未删除、未覆盖且不得用于训练。其中旧 exporter 只在内存保留记录，因此 r6 的前 64 场不能从磁盘恢复；经用户明确授权后从头运行两个新目录。
- 完整 pilot `datasets/situation-stage4-pilot-20260919-r10/` 与 `r11/` 均成功：71 场、493 回合、500 条；`train.jsonl` 为 17,188,098 字节、SHA-256 `4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1`。两个目录的 6 个文件哈希完全一致，manifest 也无时间差异。
- 冻结 `inputRepresentationVersion=compact-v1` 和配置 SHA-256 `5e0f7b57d127c81b004b5524d2f5b8e6af1193bc8d30b57e56a78d706d7a6a87`。compact p95 为 41,106 bytes，相比 expanded 的 45,602 bytes 降低 9.86%；但 500/500 均超过候选阈值，不宣称满足序列预算，阶段五仍须用目标 Qwen tokenizer 精确复测。
- 新增 `situation-implementation/stage4-pilot-report-20260919.md`，记录所有决定性哈希、体积、长度分位、分布、表示选择边界及不覆盖 complete 工件的后续要求；同步更新 TODO、项目索引、NOW/MAP/RUNBOOK/DECISIONS/RISKS 与月度 history。
- 最终验证：CLI 与测试项目 Release 构建 0 warning/0 error；步骤五契约 58、选择 148、pilot 单元 18、双工件 20 项通过；阶段三统一五套门禁通过；原 API 数据管线 38、语义 34、v4 15 项回归通过；`git diff --check` 通过。
- 用户于 2026-09-19 审核通过步骤五；按实际完成状态补齐 `TODO_4.md` 中步骤一至五、对应门禁、分步验收与推荐实施顺序的勾选，并在 `TODO.md` 记录步骤五获批。精确 tokenizer、全量导出、review candidates 和人工复核保持未完成；未执行暂存、提交或推送。
- 新增 `situation-implementation/step6-multi-agent-execution.md`：将步骤六拆分为 Timeline 索引/计算复用、partial/checkpoint/恢复、统计/provenance/验证三个文件隔离的并行子目标；规定共享工作区冲突边界、子智能体提示词、恢复演练、全量工件门禁和版本安全要求。主任务的代码集成、真实运行与最终 `ACCEPTED/REJECTED` 只能由 `gpt-6-astra` 完成。
- 按用户指定冻结多智能体模型配置：A/B/C 均必须显式使用 `gpt-5.6-sol`、reasoning effort `high`；主协调、集成和最终验收必须使用 `gpt-6-astra`、reasoning effort `high`。禁止继承或自动选择其他模型/强度。

## 2026-09-19 — 阶段四步骤六启动

- 本轮采用 `step6-multi-agent-execution.md` 的默认范围：步骤六只实现并生成 train/dev/test、split、label stats、provenance、checkpoint/partial 和 manifest；`review-candidates.jsonl` 明确留到步骤七，不提前实现或勾选。
- 启动前门禁已通过：当前无项目相关 `dotnet` exporter、测试或 Web host 进程；r10/r11 均为 `complete`，各含 71 场、493 回合、500 条，六个决定性文件 SHA-256 全部一致；`train.jsonl` SHA-256 仍为 `4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1`。
- 步骤五快速门禁通过：Release 构建 0 warning/0 error，pilot 单元 18 项、双工件回读 20 项通过。
- 冻结并行接口：A 提供单场 Timeline 索引和选择载荷复用；B 提供显式 create/resume、逐场事务 spool/checkpoint 和稳定合并；C 提供按场增量统计、实际脏工作树 provenance 和流式完整工件验证。三个组件均以单场生命周期、稳定排序、规范化哈希和取消安全为边界。
- 冻结文件所有权：A 仅修改 selector、A 新增的 index/payload 与专属测试；B 仅新增 writer/checkpoint/resume 与专属测试；C 仅新增 stats/provenance/validator 与专属测试。共享模型、Schema、CLI、exporter、测试入口、npm 脚本和文档统一留给集成阶段。
- A/B/C 将显式使用 `gpt-5.6-sol / high`，禁止继续派生子智能体；集成完成后另由 `gpt-6-astra / high` 阅读实际差异并执行最终验收。
- 版本安全继续保持：本轮不暂存、不提交、不推送，不覆盖或删除任何 complete/incomplete 数据目录。
- 用户随后要求暂时停止负责统计、provenance 与完整工件验证的子智能体 C；已中断 C 的运行并原样保留其当前未提交文件，A（索引/复用）和 B（checkpoint/恢复）继续执行。暂停期间主任务不接管或修改 C 的文件。
- 子智能体 A 已完成 Timeline 索引与选择载荷复用：修改 selector，新增单场只读索引、最终入选 payload 和专属 verifier；Release 构建及既有 selection 148、contracts 58、pilot 18、r10/r11 工件 20 项通过，双 pilot `train.jsonl` SHA-256 保持 `4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1`。专属 verifier 尚未注册，等待用户审核后由集成阶段处理。
- 子智能体 B 已完成逐场事务 writer、checkpoint 和恢复验证：新增四个冻结所有权文件，专项 verifier 的首场前/场中/提交后取消、损坏 spool/checkpoint、未知文件、重复恢复、complete 恢复、绑定漂移和决定性恢复共 10 个场景通过；Release 构建 0 warning/0 error，contracts 58、pilot 18、API 38 项通过。专属 verifier 尚未注册，等待用户审核后由集成阶段处理。
- A、B 当前均已结束运行；C 继续保持暂停。主任务尚未注册 A/B 测试入口、尚未集成 full exporter，也未启动 `gpt-6-astra / high` 验收，等待用户审核 A/B 结果。
- 用户要求重新启动 C；已恢复原 `gpt-5.6-sol / high` 子智能体继续统计、provenance、流式完整工件验证和专项失败测试，文件所有权保持不变，禁止修改 A/B 或主任务文件。

## 2026-09-19 — 步骤六 Astra 集成与正式运行

- 逐文件阅读 A/B/C 实现、测试和实际差异后注册三个专项 verifier 与统一 `test:situation:stage6`。pilot/full 共用最终 payload record builder，保持同一选择/契约/规则/模板语义；真实训练侧与 r10 重合的 35 条记录逐字节相同。
- 新增 full exporter 和显式 `--resume` CLI：每场 semantics 解析一次、单场索引、单 match 事务；绑定冻结 split/父工件、全部 Schema、配置与实际 API/CLI 脏工作树源码。完整发布前回读六个非 manifest 文件，review candidates 明确归步骤七。
- 集成修复 Windows 发布目录移动与锁生命周期：外部排他锁覆盖整个发布；`.partial` 原子移到相邻 `.writer-recovery` 保留，支持 cleanup 后恢复。补齐 manifest 临时文件、初始化中断、半条 JSONL、commit 临时文件、实际原子替换失败和并发恢复拒绝。
- 最新门禁：Release 0 warning/0 error；A 22、B 15、C 16、合成集成/恢复 85；contracts 58、eligibility 46、selection 148、pilot 18、r10/r11 工件回放 20、stage3、API 38、semantics 34/V4 15 通过。r10/r11 train SHA 未变。
- 固定五场 train 基准实际测量 1 match / 4、6、8 round workers，耗时分别 214.283 / 192.363 / 182.577 秒，峰值工作集约 0.925 / 1.328 / 1.568 GB；正式采用 6。未实现或伪称测量 2 match workers。旧/新选择路径同 Timeline 的 108 回合 SHA 全部一致，选择段总计 135.039 / 138.461 秒，不能宣称索引单段加速。
- 真实五场取消恢复两种边界通过；spool 字节/行数/SHA 和统计与不中断相同，损坏 checkpoint/spool、source/config/split 绑定漂移和 complete 标记恢复均被拒绝。真实诊断位于 `datasets/situation-stage6-real-recovery-20260919-r1/`，所有副本最终保持 incomplete。
- 约 14:25 在此前完全不存在的 `datasets/situation-stage4-full-20260919-r1/` 启动正式 87 场导出；完成验收前仍为 incomplete。详细目录、性能、失败诊断与最终结果以 `situation-implementation/stage4-full-report-20260919.md` 为准。未暂存、未提交、未推送，历史目录未删除或覆盖。
- 正式导出最终 complete，CLI exit 0：87/87 场、1,899 回合、30,384 条；train/dev/test 分别 71/8/8 场、24,416/3,216/2,752 条。耗时 4,831.144 秒，峰值工作集 1,399,181,312 bytes，无正式重跑或错误；根严格七文件共 1,126,125,234 bytes，完整 SHA 清单已写全量报告。
- 独立进程再次验证全部 30,384 条成功。实际 complete --resume 被拒绝且七文件 SHA 不变；Windows ACL 只读新输出被拒绝且没有 complete；全部 500 条 r10 pilot 记录在 full train 中逐字节一致，r10/r11 原 SHA 保持不变。
- 步骤六技术验收结论 ACCEPTED，等待用户审核，不等同于阶段四整体完成。按事实更新 TODO/项目索引/context/history；步骤七 review candidates 和人工复核保持未完成，精确 tokenizer 风险保留。未暂存、未提交、未推送；不删除或覆盖历史数据。
