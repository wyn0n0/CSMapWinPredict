# 步骤七多智能体并行执行与 Astra 验收方案

> 适用范围：`TODO_4.md` 第 8 节“步骤七：分层生成约 300 条人工复核候选”。结构参考 [步骤六执行方案](step6-multi-agent-execution.md)，本文件单独规定步骤七的模型、职责与验收要求。
>
> 编写日期：2026-09-19。本文件是执行计划，不代表代码已经实现、候选已经生成或用户已经批准候选冻结。创建本文件不启动子智能体；实际实施须处于用户授权的步骤七范围内。
>
> 基础工件：`datasets/situation-stage4-full-20260919-r1/`，complete，71/8/8 场、1,899 回合、30,384 条。启动时现场重验状态和哈希。保留当前未提交差异；未经授权不得暂存、提交、推送、发布、删除或覆盖历史工件。

## 1. 最终目标

从不可变基础数据生成独立、确定性、可追溯的人工复核候选工件：

- 精确选择 train 220、dev 40、test 40，共 300 条；禁止跨 split 补数。
- train 在可行时覆盖全部 71 场，每场至少 2 条、最多 4 条；dev/test 各 8 场，每场 5 条。
- 每回合默认最多 2 条；第 3 条必须有不同关键事件覆盖的明确理由。
- 满足文档的多标签覆盖目标并保留普通负例；客观短缺必须显式报告。
- sampleId、source-scene hash 和 model-input hash 均不得在最终候选中重复。
- 保存固定 ordinal、原记录关联哈希、候选 Narrative 和同 split 的确定性后备排序。
- 独立目录中的候选、统计、provenance、manifest 全部回读通过后，才发布 complete。
- 由 `gpt-6-astra / medium` 主线独立验收，并向用户提交比赛覆盖、类别覆盖、短缺和候选哈希供审查。

本步骤不实现复核网页、decision 原子保存、人工标签合并或 `reviewVersion` 冻结；这些属于步骤八、九。基础 JSONL 保持 template-prelabel，不因候选生成变为人工真值。

## 2. 模型与协作拓扑

- 主协调、共享契约、集成和最终验收：**GPT-6 Astra**，工具 ID `gpt-6-astra`，推理强度“中”，`medium`。
- 子智能体 A、B、C：**GPT-6 Astra**，工具 ID `gpt-6-astra`，推理强度“轻度”，`low`。
- 共 4 个活动槽位；子智能体不得创建下级智能体。
- 共享同一工作区，按文件所有权隔离修改，不假设存在独立分支或 worktree。
- 创建子智能体时显式指定 `model="gpt-6-astra"`、`reasoning_effort="low"`、`fork_turns="none"`，并发送自包含任务提示词、接口约定和文件清单。完整历史 fork 不用于带模型覆盖的创建。
- 若主任务不是指定模型/强度，不宣称已切换；先在实际执行环境中选用规定配置。子智能体摘要不能替代 Astra 的代码审查和验收。

```text
gpt-6-astra / medium：串行预检、分类规则与共享接口冻结
                      │
          ┌───────────┼────────────┐
          ▼           ▼            ▼
     Astra / low A Astra / low B Astra / low C
     数据分布审计   纯选择算法     工件与验证器
          └───────────┼────────────┘
                      ▼
       Astra / medium：CLI、测试入口、集成审查
                      ▼
       新目录正式生成 → 重复导出 → 完整回读
                      ▼
       技术验收结论 → 用户审查覆盖与候选哈希
```

## 3. 开始前的串行门禁

Astra 完成以下工作后再派发并行任务：

1. 阅读 `AGENT.md`、`PROJECT_INDEX.md`、本方案、`TODO.md` 阶段四、`TODO_4.md` 步骤七和相关验收节点；按需读取 NOW/RUNBOOK 及 `stage4-full-report-20260919.md`，不预读全部历史。
2. 检查工作树、当前分支、相关进程和已有输出目录，区分步骤五、六既有差异与本轮工作；不得停止来源不明的进程。
3. 确认基础数据完整、七文件边界正确，复核 manifest 与六个数据文件哈希、固定 split、14 个 Schema 以及来源绑定。相同输入下已有可追溯的完整回读结论可复用，记录复用依据。
4. 固定基础 manifest SHA-256，预期为 `e60e8bf001ea55b1932fba7bed83468ee104cac8516525fe539409edaae8a3f9`；固定 split SHA-256，预期为 `fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f`。不符时先查明原因，不能自动接受新值。
5. 完成第 5 节的分类定义、版本兼容、模块签名和文件所有权约定。尤其明确“可靠 isolated”“相关质量告警”、C4 转换及普通负例的判定，避免三个任务各写一套解释。
6. 分配不存在的新诊断目录、正式输出目录和重复导出目录。真实基础数据只读。
7. 明确本次已有审查发现的部署连续性、pilot 父哈希与 pilot 来源清单问题暂缓处理；不借步骤七修改上游选择或重导基础数据。若审计发现它们造成直接事实或泄漏错误，则按阻断问题上报。

预检失败时暂停依赖该基线的实现或生成，说明具体原因；不通过篡改基础 manifest、哈希或 Schema 绕过。

## 4. 文件所有权与冲突规则

下面的新文件名是建议落点；派发前 Astra 给出最终的逐文件清单。

| 角色 | 可修改范围 | 禁止直接修改 |
| --- | --- | --- |
| A：分布审计 | 自己新增的审计脚本，如 `tools/audit_situation_review_pool.py`；分配的新诊断目录 | 生产 C#、基础数据、共享契约、CLI、测试入口、上下文文档 |
| B：纯选择算法 | 新增 `Training/SituationReviewCandidateSelector.cs`、选择辅助文件和专属 selector tests | 分类契约、Schema、reader/exporter/validator、CLI、项目配置和文档 |
| C：工件与验证器 | 新增 `Workflows/SituationReviewCandidateReader.cs`、Exporter、Statistics、Validator、Provenance 文件及其专属测试 | B 的 selector、共享契约和分类策略、Schema、CLI、项目配置和文档 |
| Astra 主线 | 新增复核公共模型、分类策略/配置与 Schema；CLI、csproj、测试入口、package.json、集成测试和文档 | 接管前不得编辑正在由 A/B/C 写入的文件 |

C# 路径均相对 `apps/api/Features/Situation/`；测试放入 `tests/CsDemoMap.Api.Tests/Situation/`。

- 一个文件同一时刻只有一个写入者；跨边界改动先发集成请求，不自行扩大范围。
- 使用 `apply_patch` 修改手工维护文件，不覆盖、回退或格式化其他角色和用户的修改。
- 子智能体可准备测试代码；共享 bin/obj 的构建和测试由主线调度，避免并发构建导致程序集锁定或读到旧二进制。需要独立构建时必须明确分配互不冲突的输出路径。
- A 结束后其槽位可用于同一子智能体的后续独立审查；不新增不必要的角色，不与原文件写入者争用。
- 不暂存、commit、push 或清理既有数据目录。

## 5. Astra 先冻结的共享接口

### 5.1 历史工件与新契约的兼容边界

现有 `SituationTrainingDatasetValidator.VerifyAsync` 默认核验当前源码；`SituationTrainingSchemaRegistry` 要求固定 14 个 Schema 的版本、路径和哈希一致。基础 provenance 已绑定 CLI、csproj 和共享模型等文件。直接修改这些文件或原 Schema，会使默认历史回读失败。

- 保留基础记录、原 14 个 Schema 和其注册表内容；新复核配置、manifest/stats 使用独立模型与独立 Schema 注册边界。
- 现有 `SituationReviewCandidateV1` 有 record/candidate 哈希。默认复用该行契约，在新复核 manifest 中以 sampleId 保存 source-scene、model-input、Facts、prelabel 等关联哈希；若必须扩展行结构，新增有独立版本的契约，不原地改写旧 v1 Schema。
- 将“验证基础工件内容和历史生产来源”与“核验步骤七当前消费者源码”分别记录。源码修改前核验并记录基础 producer 的来源链；需要长期复现时保存到新诊断目录的受哈希约束快照，不用 Git HEAD 代替未提交源码。
- 下游读取不得仅因新 CLI/项目配置变化就要求重导 87 场数据；也不得全局关闭来源或 Schema 检查。若使用既有 `verifyCurrentSourceFiles=false`，只能封装在明确的历史工件消费路径内，并同时强制已批准基础 manifest/hash、历史 provenance、全部文件/Schema/配置哈希及语义验证；步骤七自己的源码另行完整绑定。写清哪些检查改为历史来源核验，并添加不匹配拒绝用例。
- 规则、场景、Facts、Narrative、split 或实际数据字节发生变化不属于消费者升级，必须阻断并审查版本边界。

### 5.2 分类策略与数量约束

冻结一个带版本及规范化哈希的 review selection 配置。它只定义步骤七抽样，不修改步骤四的 16 条选择配置。

- train 目标：post-plant 40、1vN/2vN 40、split 40、可靠 isolated 40、high contact 40、C4 转换 24、low confidence/相关质量告警 20；允许类别重叠。
- dev/test 各目标：post-plant、残局、split、isolated、high contact 分别至少 8 条，客观不可达时报告短缺。
- 为普通 live、低/中接触、none isolation、grouped/spread、高置信度明确非零覆盖下限和重叠计数方法。具体数值由 Astra 在观察 test 分布前依据 TODO 固定并记录，不作为文档已有数值伪称。
- 类别依据冻结 Facts、scene 和允许的 metadata 判断；不能仅把稀疏事件锚点标签当作所有局势类别。C4 转换可使用对应 selectionTags，需明确类别定义。
- 明确质量告警白名单、“可靠”条件、稳定哈希域、类别优先级、配额评分及不足时的处理顺序。test 分布用于执行既定抽样和报告，不用于反向调参或修改策略。

### 5.3 选择器的轻量输入

建议逻辑签名：`Select(pool, policy) -> SelectionPlan`。它是无文件 I/O、无当前时间、无随机数的纯函数。

`pool` 每条只含 sampleId、split、匿名 matchRef/roundRef、tick、关联哈希、类别位集、事件类别和读取定位信息。完整 scene/Facts/Narrative 留在基础 JSONL 中，不把约 1.1 GB 内容全部装入内存。

共享类型至少定义：

- `ReviewPoolEntry`：上述轻量描述；
- `ReviewSelectionPolicy`：数量、类别、并列规则、负例和版本；
- `ReviewSelectionPlan`：固定 ordinal 的样本引用、各 split 后备排序、覆盖统计、配额短缺和回合第 3 条理由；
- `ReviewPoolAudit`：原始可用量、去重后可用量、比赛/回合上限影响和明确的不可达原因。

reader 创建同一输入描述，B 负责选择，C 负责载荷取回、输出和独立复验。C 可先用合成 SelectionPlan 开发，不等待 B 完成算法。

### 5.4 后备顺序和短缺报告

- manifest 保存同 split、按既定优先类别组织的稳定后备排序及关联哈希；排除已选样本。
- 后备是供后续替换器消费的候选顺序，不保证每条在任意当前组合中都可替换。后续替换必须重验比赛/回合上限、哈希唯一性和类别覆盖；步骤七提供纯校验规则与合成示例，实际 decision/audit 写入留给步骤八、九。
- 区分“原始池不足”“约束组合不可行”和“贪心未找到足够覆盖”。贪心短缺不能自动当作客观无样本；小型合成用例可用穷举对照验证这一边界。
- `TODO_4.md` 8.2 的 `label-stats.json.reviewSelection.quotaShortfalls` 与独立工件布局存在位置表述冲突。采用基础 complete 只读原则，将同义字段放入新 `review-stats.json` 的 `reviewSelection.quotaShortfalls`；Astra 在实施记录和 TODO 中明确这项落点澄清，不回写基础 label-stats。

## 6. 子智能体 A：真实数据分布审计

### 6.1 任务提示词

```text
你是步骤七子智能体 A，模型 gpt-6-astra，reasoning effort low（轻度）。只做已批准基础数据的分布审计，不实现生产选择器或导出器，不创建下级智能体。

先阅读 AGENT.md、step7-multi-agent-execution.md、TODO_4.md 第 8 节，以及协调者提供的已冻结分类策略、共享类型和基础 manifest/hash。数据目录和允许写入的诊断目录由协调者明确提供。共享工作区的其他修改不得覆盖或回退。

目标：
1. 流式扫描 train/dev/test，核对数量、匿名成员和样本引用；只保留审计所需摘要。
2. 按约定统计每个 split、比赛、回合和类别的原始/去重可用量、多标签重叠、普通负例覆盖及三种唯一性冲突。
3. 检查 train 每场 2–4、dev/test 每场 5、回合默认最多 2 条的明显容量下界与不足；必要时报告第 3 条事件覆盖是否可能有帮助，不擅自放宽。
4. 区分独立类别数量足够与多个约束联合可行，不能以单项计数宣称全局一定可行。
5. 输出机器可读审计结果及简明结论到分配的新诊断目录。报告只使用匿名引用，不输出来源文件名、真实身份或完整输入。
6. 不根据 test 统计建议改变 split、配额、分类定义、提示词或模型；如有关键短缺立即通知 Astra。

只修改分配给 A 的审计脚本/新诊断文件。完成后报告输入哈希、扫描行数、耗时、内存边界、容量与短缺、统计定义及交叉验证结果。不要暂存、提交或推送。
```

### 6.2 A 的验收条件

- 三个 split 的总数与基础 manifest 一致，审计前后基础文件未变化。
- 分类口径与主线固定策略一致，无法证明的联合可行性明确标为待验证。
- 无 test 驱动的策略调整，无完整载荷或身份泄漏。
- 审计输出仅为诊断，不冒充正式 review 工件。

## 7. 子智能体 B：确定性纯选择算法

### 7.1 任务提示词

```text
你是步骤七子智能体 B，模型 gpt-6-astra，reasoning effort low（轻度）。只实现轻量池上的确定性选择器、后备排序及专属测试，不实现 reader、文件写入、CLI、共享 Schema 或文档，不创建下级智能体。

先阅读 AGENT.md、本执行方案、TODO_4.md 第 8 节以及协调者冻结的策略和共享接口。不能修改基础 16 条选择器或 Facts 规则。

目标：
1. 按 split 独立产生 220/40/40 计划，保证比赛下限/上限、回合限制、三种 hash/ID 唯一性和普通负例覆盖。
2. 实现多标签贪心覆盖，固定并列顺序：当前最缺类别数、比赛当前样本数、回合当前样本数、稳定哈希；评分的精确定义遵循冻结配置。
3. 预留后续比赛最低覆盖容量，避免早期贪心耗尽名额。必要的确定性修复策略须记录并测试，不静默随机重试。
4. 第 3 条同回合样本仅用于不同关键事件，输出明确理由；达不到数量或配额时输出结构化原因，不跨 split 补样本。
5. 生成固定 ordinal 和同 split/同优先类别后备顺序；提供替换约束校验所需的纯逻辑。
6. 覆盖输入乱序/跨文化/重复运行一致、配额冲突、重复三类标识、比赛/回合上限、重叠类别、负例、后备顺序和贪心失败与真实不可行的区别。

只修改 B 的 selector/辅助文件与专属测试。共享类型或配置如需修改，发集成请求，不自行改动。测试执行服从主线构建调度。

完成后报告接口、算法顺序、约束不变量、复杂度、测试证据、失败分类和集成请求。不要暂存、提交或推送。
```

### 7.2 B 的验收条件

- 相同输入和策略在重复、乱序与不同文化环境下得到相同计划哈希。
- 精确数量和硬约束有独立断言；少量手工/穷举合成实例能识别不正确的贪心短缺。
- 后备顺序可重放，替换规则不改变 split 用途。
- 选择器不读取模板“好坏”、身份、赢家、未来事件或文件路径。

## 8. 子智能体 C：工件、统计、provenance 与验证器

### 8.1 任务提示词

```text
你是步骤七子智能体 C，模型 gpt-6-astra，reasoning effort low（轻度）。只实现复核池 reader、计划到工件的 exporter、统计、provenance 和独立 validator，不实现选择算法、共享契约、CLI 或文档，不创建下级智能体。

先阅读 AGENT.md、本执行方案、TODO_4.md 第 8 节和 12.3、已有 review candidate 契约、SituationArtifactIO、基础 dataset validator，以及主线提供的历史工件验证边界。

目标：
1. 流式验证已批准基础数据并生成共享轻量池；第二遍顺序读取仅取计划所需的 300 条完整载荷，不保留全部 JSONL 大对象。
2. 使用合成 SelectionPlan 先行开发，最终接入 B 的结果。逐条把候选与原记录的 sampleId/split/input/output/关联哈希对应，不只验证 hash 字符串格式。
3. 输出新的 review-candidates.jsonl、review-stats.json、provenance.json、manifest.json；基础数据只读，输出目录须不存在。
4. manifest 从 incomplete 开始，验证 UTF-8/单行/契约、ordinal、220/40/40、比赛/回合约束、三类唯一性、后备排序、来源和统计后才原子 complete。
5. provenance 分别绑定基础 manifest/文件/历史 producer 与当前 review 消费者源码、配置、Schema；不把当前 HEAD 当作实际源码哈希，不回写基础来源链。
6. validator 从基础引用和候选重新统计覆盖/短缺及第 3 条理由，检查后备成员；不能仅相信 selector 的成功标记或统计。故意破坏计划或输出的合成用例必须失败。
7. 复用已有 Narrative/evidence 与敏感扫描，覆盖路径逃逸、额外/缺失文件、错来源、损坏记录、取消、写盘失败和 complete 提前发布。耗时放外部报告，不改变决定性文件。

只修改 C 分配的 reader/exporter/stats/provenance/validator 文件及专属测试。先依赖共享接口和合成计划开发；共享 Schema、CLI 和项目配置的改动交给 Astra。不要改 B 的算法。

完成后报告目录布局、读写次数、内存边界、原子发布方式、历史来源核验方式、测试证据和集成请求。不要暂存、提交或推送。
```

### 8.2 C 的验收条件

- 验证器能独立发现错误计划、错 split、重复 hash、越界配额、篡改统计及候选与源记录不一致。
- manifest 清单覆盖所有非 manifest 文件，记录 bytes/rows/SHA；自身哈希在外部验收记录中保存。
- 失败保留 incomplete，不覆盖已有目录；本步骤无需移植步骤六复杂的逐场 checkpoint，失败可在新目录重新生成。
- 旧工件的版本与来源保护不因消费者升级而失效，新复核来源可追溯。

## 9. Astra 主任务的并行调度步骤

1. 完成预检、共享分类/契约和所有权后，显式使用 `gpt-6-astra / low` 启动 A/B/C。
2. 主线保持 `gpt-6-astra / medium`，在子任务运行时准备 CLI 编排、独立 Schema 注册、测试入口和集成测试；不编辑子任务拥有的文件。
3. A 的计数与 B/C 开发同时进行。A 的发现用于确认既定策略是否可行；不得根据 test 结果迭代优化策略。
4. C 使用合成计划并行开发；B 使用合成轻量池开发。二者在返回值结构冻结后不互相等待完整实现。
5. A 结束后可让 A 独立审查 B/C 已完成部分；先读取实际文件，以不同于实现的断言检查，不只复述作者结论。
6. 统一调度构建，构建成功后再执行对应二进制；子任务提交测试代码不能等同于测试通过。
7. 接口变更由 Astra 统一裁定和广播；移交文件前明确原角色停止写入。
8. Astra 阅读所有实际 diff、测试及失败路径，完成 CLI/Schema/项目配置集成，再启动真实候选生成。

## 10. 集成与输出要求

建议新增入口，最终名称由主线统一登记：

```text
--export-situation-review-candidates <dataset-directory> <new-output-directory>
--verify-situation-review-candidates <dataset-directory> <review-candidate-directory>
test:situation:stage4:review-candidates
```

生产入口位于 `apps/cli/DeveloperCommandDispatcher.cs`；测试入口位于 `tests/CsDemoMap.Api.Tests/Program.cs`。不使用含混的 `stage7` 命名代替“阶段四步骤七”，不启动 Web host。

独立正式目录：

```text
review-candidates.jsonl  # 300 条、固定全局 ordinal、原记录载荷与引用
review-stats.json       # 覆盖、负例、重复排除、短缺、回合例外
provenance.json         # 基础生产链与当前候选生成链
manifest.json           # 状态、策略、关联哈希、后备排序、文件清单
```

- candidate ordinal 的 split 顺序、split 内排序由策略明确固定，不受线程完成次序影响。
- 所有关联哈希来自实际基础记录并重新核对；metadata/provenance 不进入模型提示。
- test 只保留最终评估用途，不能借候选生成读取或比较基座/LoRA 表现。
- 不创建 approved/modified 标签，不标记 reviewVersion 已冻结。
- 关键配额短缺先输出诊断并交用户审查，不把不足的候选集标为本步骤验收完成。

## 11. 性能与运行策略

- 输入约 1.1 GB、30,384 条，复用现有基础数据，不解析 87 场 Demo，不重建未变化的 scene。
- A 审计只扫描一次；C 的计划读取和载荷取回原则上采用轻量池加第二遍顺序读取，避免重复全量反序列化和全部载荷驻留。
- 不为每个子智能体分别执行完整真实工件语义重算。主线统一做一次必要基线验证，并复用可追溯结果；候选载荷的来源与语义回读仍必须完成。
- 不默认并行读三个大 JSONL；开发并行的收益高于争用同一磁盘。记录实际扫描、选择、载荷取回、发布、回读耗时和可获得的峰值内存。
- 合成测试用于开发迭代；正式生成与一次独立新目录的确定性对照在代码稳定后执行。

## 12. Astra 最终验收门禁

仅 `gpt-6-astra / medium` 主线可以给出本方案的技术验收结论。

### 12.1 代码审查

- A/B/C 与集成 diff 的文件所有权、共享接口、内存边界、路径与原子发布正确。
- 分类策略有单一权威定义；审计、选择、独立验证结果一致。
- 没有修订上游冻结规则、重写基础数据或用 test 调整抽样/模型策略。
- 旧 Schema 与 producer 来源完整，新消费者代码及复核 Schema 独立绑定。

### 12.2 自动门禁

至少包括：

```text
Release build: 0 warning / 0 error
review policy/contract tests
review selector determinism/constraints/shortfall tests
review reader/exporter/validator/failure tests
review candidate integration tests
test:situation:stage4:contracts（若触及共享契约则必须重跑）
基础工件文件/版本/来源只读验证
git diff --check
```

若共享 scene/Facts/eligibility 或基础 exporter 受到影响，补跑对应 stage3、eligibility、selection、pilot、API/semantics 回归；只改新复核模块不默认重复所有历史长耗时门禁。步骤四整体统一门禁仍按 TODO 的最终完成要求处理，步骤七不得冒充阶段四整体完成。

### 12.3 正式候选工件

- 精确 220/40/40，71/8/8 场覆盖符合规则；回合第 3 条有可复算的不同事件理由。
- 类别配额及普通负例覆盖有逐 split 报告，短缺不会被隐藏或静默降目标。
- sampleId/source-scene/model-input 唯一；候选载荷逐条对应原记录。
- 后备顺序、关联哈希和替换约束验证通过；无跨 split、已选重复或来源不明的后备成员。
- 两次新目录输出的所有决定性文件逐字节/哈希一致；时间和机器信息置于外部运行报告。
- manifest complete，四文件清单和哈希回读一致，基础数据仍与执行前哈希相同。

### 12.4 文档与阶段边界

- 更新受影响的 TODO、NOW 和验收报告，写实际结果而非计划。
- 用户审查比赛/类别覆盖、短缺及候选哈希之后，才记录候选已获用户批准冻结；技术 complete 与用户批准分开表述。
- 300 条人工评价与标签冻结仍未完成，不提前勾选步骤八、九或阶段四整体。
- 保留历史及失败目录，列出新工件和诊断材料；提交、推送、发布仍需相应授权。

## 13. Astra 验收输出格式

```text
Decision: ACCEPTED | REJECTED
Scope: stage four, step seven candidate generation only
Models: main gpt-6-astra / medium; children gpt-6-astra / low

Implemented:
- files, interfaces, selection policy version/hash

Artifact:
- directory, manifest status/hash, base dataset hash
- train/dev/test candidate counts and match/round coverage
- file bytes/rows/SHA-256 and repeat-export comparison

Coverage:
- positive quotas, ordinary negatives, shortfalls
- third-sample round exceptions and backup ordering

Verification:
- executed suites and results; reused baseline evidence
- source-record linkage, history/consumer provenance, base unchanged

Performance:
- scan/select/materialize/readback times and memory if measured

Remaining risks:
- unresolved issues and user review required for candidate freeze

Version safety:
- staged/committed/pushed status and rollback boundary
```

缺少必需门禁、数量或硬约束错误、关键短缺未经处理、manifest 非 complete、确定性对照失败或 Astra 未读实际代码时，不能输出 ACCEPTED。可报告实现完成和诊断结果，但不得把待处理问题隐去。

## 14. 预计时间与并发收益

以下是规划估计，非实测或时限承诺：

- Astra 预检与共享接口冻结：30–45 分钟。
- A 分布审计：30–60 分钟；与 B/C 重叠。
- B 纯选择算法和专属测试：1.5–3 小时。
- C 工件、独立验证器和专属测试：1.5–3 小时；与 B 重叠。
- Astra 集成、真实生成、重复回读和报告：45–90 分钟；部分入口准备可在并行期间完成。

正常墙钟时间约 3–5 小时，主要取决于 B/C 中较慢的一条及集成返工；不会按子任务工时相加，也不保证精确三倍加速。此前 3–4.5 小时可视为接口稳定、配额可达时的目标。历史工件兼容或真实配额冲突暴露额外问题时，可能延长至 5–7 小时，用户审查等待另计。

总计不包括步骤八网页、步骤九人工复核和标签冻结。任何时候都不为满足估时跳过完整性门禁。
