# 步骤六多智能体并行执行与 Astra 验收方案

> 适用范围：`TODO_4.md` 的“步骤六：全量导出与模板预标注”。
>
> 当前基线：步骤一至五已经用户审核通过；步骤五改动和 complete pilot 工件存在于当前工作区。除非用户另行明确授权，所有智能体均不得暂存、提交、推送、发布或删除历史数据目录。

## 1. 最终目标

在不改变冻结 split、eligibility、选择规则、Facts、Narrative 和 `compact-v1` 语义的前提下，完成可恢复、确定性、流式的全量阶段四数据导出：

- 87 场 Demo 每场只解析一次并启用 semantics；
- 按冻结 `71 train / 8 dev / 8 test` 写入三个独立 JSONL；
- 每场完成后持久化安全断点，失败最多重跑当前比赛；
- 输出完整统计、provenance、文件清单与哈希；
- 全部回读门禁通过后才把 manifest 切换为 `complete`；
- 保持 pilot r10/r11 的既有决定性输出不变；
- 主任务最终只能由 `gpt-6-astra` 验收。

步骤七的 220/40/40 review candidate 分层选择不属于本文件的实施范围。步骤六只为其提供 complete、可审计的基础数据。

`TODO_4.md` 的 7.3 当前把 `review-candidates.jsonl` 列在必需输出中，但推荐实施顺序又把候选生成列为步骤七。Astra 必须在创建子智能体前把这一边界写入本轮执行记录：本方案默认步骤六的 complete 基础数据只包含 train/dev/test、split、统计、provenance 和 manifest，步骤七再在新的不可变工件或经明确版本化的后续阶段生成 review candidates；不得提前勾选步骤七。若用户要求 review candidates 属于同一个 complete 目录，则本轮必须扩展到步骤七并重新估时，不能静默改变范围。

## 2. 模型与协作拓扑

- 主协调、集成和最终验收模型：`gpt-6-astra`，reasoning effort 固定为 `high`。
- 子智能体 A、B、C 的模型统一固定为 `gpt-5.6-sol`，reasoning effort 固定为 `high`。
- 并发上限：主任务加 3 个子智能体，共 4 个活动槽位。
- 子智能体 A、B、C 不得继续创建下级智能体。
- 所有智能体共享同一工作区；不能假设各自位于隔离分支或 worktree。
- 不得让子智能体继承或自动选择其他模型/推理强度；创建每个子智能体时必须显式指定 `model=gpt-5.6-sol`、`reasoning_effort=high`。
- 子智能体结论不能替代 `gpt-6-astra / high` 的最终验收。

```text
gpt-6-astra / high 主任务
├─ gpt-5.6-sol / high 子智能体 A：Timeline 索引与计算复用
├─ gpt-5.6-sol / high 子智能体 B：partial/checkpoint/恢复
└─ gpt-5.6-sol / high 子智能体 C：统计、provenance 与工件验证
        ↓
gpt-6-astra / high 集成 full exporter 与 CLI
        ↓
固定小批基准 → 87 场全量导出 → 最终回读
        ↓
gpt-6-astra / high 给出 ACCEPTED / REJECTED
```

## 3. 开始前的串行门禁

主任务必须先完成以下动作，再创建子智能体：

1. 完整阅读 `AGENT.md`、`TODO.md`、`situation-implementation/TODO_4.md`、`log.md` 和 `stage4-pilot-report-20260919.md`。
2. 检查 `git status --short`，区分步骤五既有差异和本轮新增差异。
3. 确认没有残留 exporter、测试或 Web host 进程。
4. 回读 r10/r11 manifest，确认均为 `complete`、500 条且决定性文件哈希一致。
5. 运行步骤五快速门禁，确认基线未损坏。
6. 冻结下面的模块接口和文件所有权；接口冻结后才允许并行编辑。
7. 如果需要建立步骤五本地 Git checkpoint，必须先取得用户明确提交授权；审核通过本身不自动等于提交授权。
8. 记录步骤六与步骤七的 `review-candidates.jsonl` 边界；采用本方案默认边界时，不把步骤七文件列为步骤六 complete 工件的必需文件。

基线失败时不得继续并行实现，应先由 Astra 报告并停止。

## 4. 文件所有权与冲突规则

| 角色 | 可修改文件 | 禁止直接修改 |
| --- | --- | --- |
| 子智能体 A | 新增 Timeline index/selection payload 文件；`SituationTrainingCandidateSelector.cs`；A 自己的新测试文件 | exporter、CLI、manifest 模型/Schema、`Program.cs`、`package.json`、文档 |
| 子智能体 B | 新增 partial writer、checkpoint、resume 文件；B 自己的新测试文件 | selector、exporter、CLI、manifest 模型/Schema、`Program.cs`、`package.json`、文档 |
| 子智能体 C | 新增统计器、provenance builder、完整工件 validator；C 自己的新测试文件 | selector、exporter、CLI、共享模型/Schema、`Program.cs`、`package.json`、文档 |
| Astra 主任务 | full exporter、共享模型、Schema、CLI、测试入口、npm 脚本、TODO/context/log，以及所有集成修复 | 无，但必须先检查子智能体正在编辑的文件 |

规则：

- 每个已有文件只允许一个角色拥有写权限。
- 子智能体若发现必须修改禁区文件，只能在最终报告中提交“集成请求”，不能直接编辑。
- 子智能体不得重排、格式化或顺手清理无关代码。
- 所有手工文件修改使用 `apply_patch`。
- 禁止删除、覆盖或重命名任何已有 complete/incomplete 数据目录。
- 禁止通过 `git reset --hard`、`git checkout --` 或类似命令回退共享工作区。

## 5. Astra 先冻结的共享接口

Astra 可以根据现有代码微调命名，但在创建子智能体前必须明确以下边界：

### 5.1 Timeline 索引输入

索引至少能够按 O(1) 或预分组方式提供：

- `DemoFrame` by tick；
- semantic frames by round ID；
- completed attempt by round ID；
- round 内相关结构化事件 tick；
- 已排序的 round/member 访问顺序。

索引只缓存当前比赛，比赛写入完成后可整体释放。

### 5.2 已选择记录载荷

选择阶段应允许把选中 tick 已生成的以下对象交给 record builder，避免再次构建：

- 已验证 scene 及其规范化 SHA-256；
- 已验证 Facts 及其 SHA-256；
- 模板 Narrative 及其 SHA-256；
- semantic phase/round 与选择标签/权重。

未选中的大对象不得跨比赛保留。优化前后 pilot 的 `train.jsonl` SHA-256 必须保持不变。

### 5.3 每场事务写入

writer 的逻辑接口必须区分：

- 创建全新 incomplete 输出；
- 显式恢复已有 incomplete 输出；
- 开始一场；
- 提交一场的 train/dev/test spool 与统计增量；
- 放弃未提交的当前场；
- 按稳定顺序合并；
- 完成回读后原子发布正式文件。

不得通过“文件存在就自动续跑”猜测用户意图；恢复必须使用显式 CLI 参数。

### 5.4 增量统计与验证

统计器必须支持按比赛合并，合并顺序不改变规范化结果。validator 必须流式工作，不能把约 1 GB JSONL 全部读入内存。

## 6. 子智能体 A：Timeline 索引与计算复用

### 6.1 任务提示词

```text
你是步骤六子智能体 A。只实现 Timeline 索引和选择阶段计算复用，不实现 exporter、checkpoint、CLI、manifest 或文档。

开始前阅读 AGENT.md、TODO_4.md 的步骤三至六、现有 SituationTrainingCandidateSelector、SituationEligibleSceneBuilder、SituationTrainingPilotExporter 以及相关测试。工作区包含其他智能体的并行修改，不得覆盖或回退它们。

目标：
1. 为单场 DemoTimeline 建立一次性索引，避免每个回合重复构建 frame-by-tick 字典、扫描全部 semantic frames 和扫描全部 events。
2. 让选择结果能够复用已构建且已验证的 scene/Facts/Narrative，最终 record builder 不再为选中 tick 重建同一对象。
3. 索引和载荷只存活于当前比赛；不得把 87 场 Timeline 或全部候选大对象保存在内存。
4. 不改变选择顺序、标签、1/n 权重、身份摘要或任何规范化输出。
5. 增加专属测试，证明旧 API 与新索引 API 对固定 Timeline 输出完全一致，并覆盖并行回合调用、取消、重复 tick/round 和跨回合隔离。
6. 运行相关门禁，至少包括 selection、contracts、pilot，以及 r10/r11 只读工件验证。

可修改范围仅限协调者分配给 A 的 selector/index/payload 文件和 A 的新测试文件。若需要修改 Program.cs、package.json、exporter、模型或 Schema，在最终报告列出集成请求，不要直接修改。

完成后报告：修改文件、接口、测试结果、pilot 哈希等价证据、性能预期、内存边界、风险和集成请求。不要暂存、提交或推送。
```

### 6.2 A 的验收条件

- 每场索引只创建一次；不存在按完成回合重复扫描整场集合的路径。
- 相同输入下 selection canonical JSON/SHA-256 不变。
- r10/r11 工件验证继续通过。
- 并行只读索引安全，取消不污染其他回合。
- 没有引入身份、赢家、结束原因或未来事件依赖。

## 7. 子智能体 B：partial、checkpoint 与恢复

### 7.1 任务提示词

```text
你是步骤六子智能体 B。只实现可恢复的逐场事务 writer、checkpoint 和恢复验证，不实现 selector、full exporter 编排、CLI、manifest Schema 或文档。

开始前阅读 AGENT.md、TODO_4.md 步骤六 7.1、SituationArtifactIO、现有 pilot exporter 的写入方式、manifest validator 和相关测试。工作区包含其他智能体的并行修改，不得覆盖或回退它们。

目标：
1. 设计每场不可变 spool/partial 事务：一场未提交时不得进入已完成进度；一场提交后进程退出也可精确恢复。
2. checkpoint 至少绑定 split、父 manifest、选择配置、prompt 配置、全部 Schema、相关源码文件哈希、输出模式和样本计划；任一变化时拒绝恢复。
3. checkpoint 记录完成比赛集合/顺序、各 split 行数、统计快照或可重建引用、spool 字节数与 SHA-256；不得写绝对路径。
4. 恢复必须显式请求，只接受 status=incomplete；complete、未知文件、损坏 JSON、截断行、哈希不符、顺序跳跃和源码漂移均失败。
5. 支持 train/dev/test 独立 spool，并能按协调者提供的稳定顺序原子合并为正式 JSONL；失败时保留 incomplete 与诊断材料。
6. 增加专属失败恢复测试：首场前取消、场中取消、场提交后取消、损坏 spool、损坏 checkpoint、额外文件、重复恢复和 complete 目录恢复。

可修改范围仅限协调者分配给 B 的 writer/checkpoint/resume 文件和 B 的新测试文件。若需要修改 CLI、exporter、共享模型、Schema、Program.cs 或 package.json，只提交集成请求。

完成后报告：状态机、磁盘布局、原子性边界、恢复算法、测试结果、仍可能重跑的最大范围和集成请求。不要暂存、提交或推送。
```

### 7.2 B 的验收条件

- 正常或异常退出后，complete 状态不会提前出现。
- 已提交比赛无需重新解析；最多重跑当前未提交比赛。
- 恢复不能混用不同源码、配置、split 或 Schema。
- 不依赖进程内状态、临时绝对路径或非确定性文件枚举。
- partial/spool 到正式文件的发布可回读、可哈希、可失败关闭。

## 8. 子智能体 C：统计、provenance 与完整工件验证

### 8.1 任务提示词

```text
你是步骤六子智能体 C。只实现可增量合并的统计、provenance 构建和 full dataset validator，不实现 selector、writer、CLI、full exporter 编排、共享模型/Schema 或文档。

开始前阅读 AGENT.md、TODO_4.md 步骤六 7.2/7.3 和 12.2/12.3、训练记录/manifest/label-stats 契约、pilot exporter 统计逻辑、pilot artifact verifier。工作区包含其他智能体的并行修改，不得覆盖或回退它们。

目标：
1. 将现有 pilot 统计逻辑提取为可按记录、选择结果和比赛增量累计的确定性组件。
2. full 统计覆盖 split 比赛/回合/样本数、每回合样本数、权重误差、phase、选择标签、C4、formation、contact、isolation、spatial、confidence、质量码、highlight、uncertainty、拒绝/短缺/裁剪及唯一哈希/重复数。
3. provenance 绑定 Git HEAD、gitDirty、相关源码实际文件哈希、split/父 manifest、规则、选择、prompt 表示和 Schema 哈希；不能用 HEAD 代替脏工作树内容。
4. validator 流式验证三个 JSONL 的 UTF-8、单行、Schema、规范字段、对象哈希、sampleId、排序、1/n 回合权重和跨 split 的 match/round/sample/source scene 冲突。
5. validator 回读 manifest 所列每个非 manifest 文件的路径、字节数、行数和 SHA-256，并拒绝缺失、额外或修改文件。
6. 运行时耗时与决定性数据统计分离；不得让 wall-clock duration 改变训练记录或选择结果。若耗时进入冻结工件，必须由 Astra 明确批准其非决定性边界。
7. 增加专属测试，包含损坏行、BOM、CRLF/多行、排序错误、重复 ID、跨 split 冲突、权重错误、文件清单漂移和脏源码 provenance。

可修改范围仅限协调者分配给 C 的 stats/provenance/validator 文件和 C 的新测试文件。共享模型、Schema、Program.cs、package.json、CLI 和 exporter 的需要改动项只列为集成请求。

完成后报告：统计字段、合并不变量、验证复杂度、测试结果、非决定性字段处理和集成请求。不要暂存、提交或推送。
```

### 8.2 C 的验收条件

- 增量统计与同样记录的单次批量统计规范化结果一致。
- validator 内存复杂度不随全部 JSONL 内容线性增长；允许保存摘要集合用于唯一性门禁。
- 三个 split 的用途、排序、权重和交叉隔离均有失败用例。
- provenance 能描述实际脏工作树，而不只记录 Git HEAD。
- wall-clock 指标不会破坏训练数据决定性。

## 9. Astra 主任务的并行调度步骤

1. 在完成第 3、4、5 节后，一次性创建 A、B、C 三个子智能体。
2. 创建时为每个子智能体发送完整任务提示词和实际文件所有权清单，并显式设置 `model=gpt-5.6-sol`、`reasoning_effort=high`；不得省略后依赖默认值。
3. 主任务在子智能体运行期间只做以下工作：
   - 设计 `SituationTrainingDatasetExporter` 的 orchestration；
   - 设计 full/pilot 共用 record builder；
   - 准备共享模型和 Schema 的最小集成补丁；
   - 不修改 A/B/C 已拥有的文件。
4. 使用较长等待周期接收结果，不重复轮询或在子智能体尚未结束时改写其文件。
5. 某子智能体需要越权修改时，先停止其写入并由 Astra接管该单一文件；不得让两个角色同时修改。
6. 收到结果后，Astra 必须读取实际 diff 和测试代码，不能只根据子智能体摘要接受实现。
7. Astra 统一处理 Program、CLI、package、模型、Schema、文档和测试注册，解决接口集成。

## 10. Full exporter 集成要求

Astra 集成后的 full exporter 至少满足：

- 新建模式要求输出路径不存在；恢复模式要求路径已存在且严格为匹配 checkpoint 的 incomplete 工件。
- CLI 在 Web host 构建前分流，不启动 HTTP API、胜率推理或文本模型服务。
- 处理顺序预先按最终稳定键确定；并发完成顺序不能影响最终字节。
- 默认最多两个比赛 worker。每个 worker 独占 Timeline 和每场内存；先用固定 5 场基准比较 1/2 worker 后再决定是否允许 3。
- 同一 Demo 的内容哈希与解析尽量在一次顺序读取中完成；若无法安全合并，保留双读并在报告中说明。
- train/dev/test 分别写 spool，禁止混写后随机拆分。
- 每场写入成功后才提交 checkpoint；当前场失败不改变已提交场状态。
- 最终文件按 `matchRef → roundRef → tick → sampleId` 排序。
- 所有记录再次验证 scene/Facts/model input/Narrative 哈希、allowed evidence、敏感内容和权重。
- 最终合并、逐行回读、统计、provenance 和文件清单全部通过后才发布正式 JSONL 并切换 complete。
- complete manifest 不列自身哈希；列出其余全部文件的相对路径、字节数、行数和 SHA-256。

## 11. 性能基准与运行策略

正式 87 场导出前必须建立一个不读取 dev/test 表现用于调参的训练侧固定小基准：

- 固定 5 场 train，只用于测量基础设施吞吐和内存，不用于改变选择或表示语义；
- 比较旧索引/新索引、1/2 match worker、4/6/8 round worker；
- 记录 parse、index、selection、record build、write、readback 各阶段耗时；
- 测量峰值工作集、GC 次数和输出吞吐；
- 选择最小且稳定的并发配置，不以单次最快值作为唯一依据；
- 基准目录必须是新的 ignored 诊断目录，失败保留 incomplete，不覆盖 r10/r11。

默认建议：2 个 match worker，每场最多 6–8 个 round worker；如果内存或磁盘吞吐恶化，退回 1 个 match worker。

## 12. Astra 最终验收门禁

只有运行于 `gpt-6-astra` 且 reasoning effort 为 `high` 的主任务完成以下全部检查，才可输出 `ACCEPTED`：

### 12.1 代码审查

- 逐文件检查 A/B/C 和集成 diff；确认无越权修改和无关重构。
- 检查取消、异常、恢复、原子发布和路径边界。
- 检查共享对象并发安全、Timeline 生命周期和内存释放。
- 检查敏感字段、赢家、结束原因、未来事件及身份没有进入选择、input/output 或日志。
- 检查 full 与 pilot 调用同一 record builder、Facts、Narrative 和契约验证。

### 12.2 自动门禁

至少执行：

```text
Release build: 0 warning / 0 error
test:situation:stage4:contracts
test:situation:stage4:eligibility
test:situation:stage4:selection
test:situation:stage4:pilot
step6 writer/checkpoint/recovery tests
step6 stats/provenance/validator tests
test:situation:stage3
test:api
test:semantics
git diff --check
```

还必须只读验证 r10/r11，确认优化后既有 `train.jsonl` SHA-256 仍为：

```text
4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1
```

### 12.3 恢复演练

至少在固定训练侧小基准中演练：

1. 场中取消并恢复；
2. 场提交后取消并恢复；
3. checkpoint 损坏；
4. spool 截断或被修改；
5. 源码/配置/split 哈希变化；
6. 对 complete 目录请求恢复；
7. 恢复结果与不中断运行的决定性文件哈希一致。

### 12.4 正式全量工件

- 只创建一个新的正式输出目录，不复用任何 pilot 或 incomplete 目录。
- manifest 最终为 complete。
- 87 场恰好归入一个 split，数量为 71/8/8。
- train/dev/test 均非空、排序稳定、无 match/round/sample/source-scene 交叉。
- 每回合样本唯一且不超过 16，精确权重和为 1。
- 文件清单、行数、字节数与 SHA-256 回读一致。
- 未生成正式 review candidates；步骤七仍保持未完成。

### 12.5 文档与版本安全

- 依据实际结果更新 `TODO_4.md`、`TODO.md`、`log.md` 和必要 context 文档；未完成项不得提前勾选。
- 列出所有新增/修改文件和所有 complete/incomplete 数据目录。
- 明确说明是否存在精确 tokenizer、review candidate 或其他后续阻塞。
- 不暂存、不提交、不推送，除非用户在验收后另行明确授权。

## 13. Astra 验收输出格式

Astra 最终回复必须包含：

```text
Decision: ACCEPTED | REJECTED

Implemented:
- ...

Artifact:
- directory
- manifest status
- train/dev/test matches, rounds, samples
- bytes and SHA-256

Recovery:
- exercised scenarios
- maximum replay scope

Performance:
- benchmark configuration
- full export elapsed time
- peak memory if available

Verification:
- command/suite and result

Remaining risks:
- ...

Version safety:
- staged/committed/pushed status
- rollback boundary
```

任一必需门禁未执行、full manifest 非 complete、恢复演练失败、r10/r11 哈希漂移或实际代码未由 Astra 阅读时，Decision 必须为 `REJECTED`，不得使用“基本完成”“预计通过”等表述替代。

## 14. 预计时间

- Astra 串行预检与接口冻结：30–45 分钟。
- A/B/C 并行实现：1.5–3 小时。
- Astra 集成与冲突修复：1–2 小时。
- 固定小基准与恢复演练：30–60 分钟。
- 87 场完整导出：预计 50–100 分钟；以实测为准。
- 最终回读、回归和文档：45–90 分钟。

正常总计约 4–7 小时。真实数据若暴露新的语义或恢复错误，应停止 complete 发布并修复；不得为了满足时间估计跳过门禁。
