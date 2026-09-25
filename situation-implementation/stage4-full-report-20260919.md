# 阶段四步骤六集成与验收记录

> 2026-09-19；Decision: ACCEPTED。步骤六技术验收通过，等待用户审核；不是阶段四整体完成，也不授予提交、推送或步骤七权限。

## 范围与实现

步骤六基础目录仅包含 `train.jsonl`、`dev.jsonl`、`test.jsonl`、`split.json`、`label-stats.json`、`provenance.json` 和 `manifest.json`。`review-candidates.jsonl` 属于步骤七，本轮不生成、不要求、不勾选。

已现场读取 A/B/C 的实际源码、差异和专项测试。pilot/full 共用记录构建器，直接消费最终入选 payload；单场 Timeline 索引按 tick/round 分组，round worker 只读共享索引。原有 pilot 构建路径保留作等价测试参考。

full exporter 按 matchRef 稳定顺序逐场解析一次（semantics=true），哈希和解析使用同一个只读文件句柄并进行两遍顺序读取；未实现融合哈希解析。每场独立统计，事务提交后才 merge；恢复从已验证 spool 和选择统计快照重建统计，不解析已提交 Demo。

checkpoint 绑定冻结 split、父 manifest、选择/prompt 配置、全部 Schema 与 API/CLI 的实际源码文件哈希。complete 发布前恢复工作区移至相邻 `.writer-recovery`，相邻 `.writer.lock` 的排他句柄一直持有到 manifest 原子替换结束；两个相邻对象均不属于 complete 数据目录，也不删除历史诊断材料。

validator 逐行验证 canonical UTF-8、记录契约、排序、ID、回合权重和 split 隔离，并由 provenance 恢复完整 source scene、重算 scene/Facts/Narrative 哈希。六个非 manifest 文件逐一回读 bytes/rows/SHA；候选 complete manifest 在内存中验证，通过后才写入磁盘。

## 训练侧性能基准

固定五场来自 train，按最终 matchRef 排序后取前五；未读取 dev/test 表现用于调参。五场分别输出 368、352、352、272、384 条，共 1,728 条。

| 输出目录末段 | match workers | round workers | 总耗时（秒） | 峰值工作集（bytes） | GC 0/1/2 |
| --- | ---: | ---: | ---: | ---: | --- |
| `situation-stage6-benchmark-20260919-w1-r4-r3` | 1 | 4 | 214.283 | 924618752 | 15292/2313/449 |
| `situation-stage6-benchmark-20260919-w1-r6-r1` | 1 | 6 | 192.363 | 1328316416 | 15316/2412/416 |
| `situation-stage6-benchmark-20260919-w1-r8-r1` | 1 | 8 | 182.577 | 1568296960 | 15327/2498/393 |

正式导出选用 1 match / 6 round workers：8 比 6 仅快约 5.1%，但峰值工作集增加约 18.1%。这些是进程实测，未控制操作系统页缓存；4-worker 首段曾与短合成测试重叠，不作严格冷盘或孤立 CPU 结论。

当前事务接口只支持一个 match worker，尚未测量两个 match worker，因此不能给出 1/2 worker 性能结论。多个未提交比赛同时计算会改变“最多重跑当前一场”的恢复边界，本轮保持单场事务。

`w1-r4-r1` 在第三场提交目录移动时遇到 Windows `Access denied`，前两场已经提交，第三场 prepared transaction 保留；`w1-r4-r2` 因旧失败进程持有 CLI 程序集导致构建失败后误进入旧二进制运行，已停止并保留 incomplete。此后构建与运行拆开核对，CLI 捕获失败并以非零状态退出，避免遗留崩溃进程。writer 对相同无覆盖目录移动的 Windows 5/32/33 错误执行有限重试；永久错误仍失败关闭。

## 当前验证证据

- CLI 与测试程序集 Release 构建均已达到 0 warning / 0 error。
- A 索引专项 22 项、B writer 专项 15 项、C 统计/provenance/validator 专项 16 项已通过。
- 合成 87 场集成/恢复 85 项通过；覆盖场中、提交后、merge 后、publish 后、cleanup 后、最终验证后的中断；恢复结果七个文件哈希与不中断工件一致；实际 Windows manifest 原子替换失败及发布期并发恢复拒绝均已演练。
- contracts 58、eligibility 46、selection 148、pilot 18、stage3 全套、API 38、semantics 34 / V4 15 项通过；r10/r11 双工件只读回放 20 项通过。
- 两份历史 pilot `train.jsonl` 只读 SHA-256 均为 `4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1`。
- 正式 complete 之后，独立进程 `--verify-situation-training-dataset datasets/situation-stage4-full-20260919-r1` exit 0：`Full dataset verified: 30384 rows.`，再次检查当前源码绑定、七文件边界与所有记录。
- 实际正式 complete 执行 `--resume` 被拒绝，七个根文件 SHA-256 前后不变。额外 Windows ACL 黑盒：在唯一新建临时目录拒绝当前用户写权限，独立探针确认不可写后 CLI 非零退出，无 complete manifest；最终恢复原 ACL，仅清理自身临时夹具。

真实五场恢复已在 `datasets/situation-stage6-real-recovery-20260919-r1/` 完成：`record` 和 `after-commit` 均恢复到五场 1,728 条，逐场 spool 字节/行数/SHA 及统计与不中断 `w1-r6-r1` 相同；与 r10 重合的 35 条记录逐字节相同。六个独立真实副本验证 checkpoint 损坏、spool 截断、source/config/split 绑定漂移和 complete 标记均拒绝恢复。complete 标记用例是故意伪造的诊断副本，拒绝后已恢复原 incomplete manifest，不作为真实 complete 工件。

旧/新索引同源实测已完成于 `datasets/situation-stage6-index-benchmark-20260919-r1/`：同五场、同一次解析的 Timeline、两路径均 4 round workers，108 回合的全部 selection SHA 相同，共 1,728 条。旧选择总计 135.039 秒，新索引+payload 选择总计 138.461 秒，索引构建共 0.054 秒。本次新路径选择段慢约 2.5%，不能宣称索引本身带来吞吐提升；其保留并验证最终载荷，减少的是后续入选 tick 二次构建。此对照不包含旧 record builder 的第二次场景构建，因此不作为完整端到端新旧吞吐对比。

正式全量于 2026-09-19 14:25 左右在全新 `datasets/situation-stage4-full-20260919-r1/` 启动，1 match / 6 round workers，最终成功 complete，CLI exit 0。逐场阶段 3,188.127 秒，总导出 4,831.144 秒（80.52 分钟），最终 C validator 回读 448.783 秒；峰值工作集 1,399,181,312 bytes。其余后处理时间包含 merge、发布前后和 cleanup 前的完整契约复验，不把这些成本归入解析时间。运行日志为相邻 `.run.log`。

全部 500 条 r10 pilot 记录已在 full train 中逐字节找到且完全一致。provenance 绑定实际脏工作树的 85 个文件、全部 14 个 Schema、87 个 Demo 映射和 1,899 个完整回合映射；Git HEAD 为 `b3c3bcb97a25b14ea8b083a861764752ce562627`，`gitDirty=true`，没有以 HEAD 代替实际源码哈希。

## 正式工件库存

根目录严格 7 个文件，无 `.partial`、额外目录或 review candidates。6 个非 manifest 文件均经导出器完整回读，JSONL 的行数及所有文件字节/SHA 一致；普通 JSON 的 `rows=null` 表示非逐行记录工件。

| split | 比赛 | 回合 | 样本 |
| --- | ---: | ---: | ---: |
| train | 71 | 1526 | 24416 |
| dev | 8 | 201 | 3216 |
| test | 8 | 172 | 2752 |
| 合计 | 87 | 1899 | 30384 |

| 文件 | bytes | rows | SHA-256 |
| --- | ---: | ---: | --- |
| train.jsonl | 905001827 | 24416 | `921051316fa45b284aab58fececc5d51d653fde261fcc74aa771a0ade25aedd1` |
| dev.jsonl | 118834098 | 3216 | `10aa025b93ae90a5dcba0c7f0095437278c4967e2311b3304638e85372f12b0b` |
| test.jsonl | 101727857 | 2752 | `0281c8941103b7647722a3b055dccb45408ab82155a7f0eb652eb38f276499b7` |
| split.json | 16211 | — | `fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f` |
| label-stats.json | 50859 | — | `ce0139ebeefd15bc7092ea0dfcc60b04fc385d82cda28255636e97b2fe58b4d3` |
| provenance.json | 488121 | — | `93fb7d2f0339f9a1c4b9ba73bfe2b9ecb4f9da655611181960f116ca62a76427` |
| manifest.json | 6261 | — | `e60e8bf001ea55b1932fba7bed83468ee104cac8516525fe539409edaae8a3f9` |

manifest 自身哈希只记录于本报告，不自包含进它的 `files`。总计 1,126,125,234 bytes。三个 split 非空、稳定排序、无 match/round/sample/source-scene 交叉；全部 1,899 回合各 16 条，精确分子分母为 1/16，总权重为 1。正式运行提交 87/87 场，无重跑、取消或错误；独立完整回读通过。

## 本轮目录边界

以下均位于被 Git 忽略的 `datasets/`，不上传、不删除、不覆盖历史目录：

- `situation-stage6-benchmark-20260919-w1-r4-r1/`：失败诊断 incomplete，两场已提交。
- `situation-stage6-benchmark-20260919-w1-r4-r2/`：旧二进制误启动后停止，incomplete。
- `situation-stage6-benchmark-20260919-w1-r4-r3/`、`situation-stage6-benchmark-20260919-w1-r6-r1/`、`situation-stage6-benchmark-20260919-w1-r8-r1/`：各五场基准提交完成，按设计保持 incomplete，不可训练。
- `situation-stage6-real-recovery-20260919-r1/`：诊断容器；`record/`、`after-commit/` 及 `reject-checkpoint/`、`reject-spool/`、`reject-source/`、`reject-config/`、`reject-split/`、`reject-complete/` 全部保持 incomplete。
- `situation-stage6-index-benchmark-20260919-r1/`：性能指标与哈希一致性测量，incomplete，非数据集。
- `situation-stage4-full-20260919-r1/`：唯一新建正式数据目录，complete；相邻 `.writer.lock`、`.writer-recovery/` 和 `.run.log` 为协调/诊断对象，不进入 complete 根。
- 历史 `situation-stage4-pilot-20260919-r10/`、`r11/`：complete，只读验证；历史 r1–r9 及其他既有数据目录未修改。

合成门禁仅删除了自身新建的唯一临时测试目录，没有清理任何 `datasets/` 历史材料。

## 工作树文件清单与版本安全

以下是本轮验收时完整脏工作树的源码/测试/文档分类，包含原先步骤五未提交工作；不是把既有用户修改归为本轮新增。

步骤六新增或集成修改：

- `apps/api/Features/Situation/Training/SituationTrainingCandidateSelector.cs`、`SituationTrainingTimelineIndex.cs`、`SituationTrainingSelectionPayload.cs`。
- `apps/api/Features/Situation/Workflows/SituationTrainingRecordBuilder.cs`、`SituationTrainingPilotExporter.cs`、`SituationTrainingDatasetExporter.cs`、`SituationTrainingCheckpoint.cs`、`SituationTrainingPartialWriter.cs`、`SituationTrainingResumeValidator.cs`、`SituationTrainingDatasetStatistics.cs`、`SituationTrainingProvenanceBuilder.cs`、`SituationTrainingDatasetValidator.cs`。
- `apps/cli/DeveloperCommandDispatcher.cs`、`apps/cli/Program.cs`、`package.json`、`tests/CsDemoMap.Api.Tests/Program.cs`。
- `tests/CsDemoMap.Api.Tests/Situation/SituationTrainingTimelineIndexVerifier.cs`、`SituationTrainingPartialWriterVerifier.cs`、`SituationTrainingDatasetValidatorVerifier.cs`、`SituationTrainingDatasetExporterVerifier.cs`。
- `PROJECT_INDEX.md`、`TODO.md`、`situation-implementation/TODO_4.md`、`situation-implementation/step6-multi-agent-execution.md`、`situation-implementation/stage4-full-report-20260919.md`、`log.md`、`docs/context/NOW.md`、`MAP.md`、`RUNBOOK.md`、`DECISIONS.md`、`RISKS.md`、`history/2026-09.md`。两个 TODO 为本地忽略文件，仍按要求维护。

保留的步骤五脏工作树及共享契约：

- `apps/api/CsDemoMap.Api.csproj`；`apps/api/Features/Situation/Scenes/SituationInputAdapter.cs`、`SituationSceneBuilder.cs`、`SituationSceneService.cs`。
- `apps/api/Features/Situation/Training/Models/SituationTrainingModels.cs`、`apps/api/Features/Situation/Training/SituationEligibleSceneBuilder.cs`、`SituationTrainingManifestValidator.cs`、`SituationTrainingSchemaRegistry.cs`、`SituationPromptRepresentation.cs`、`situation-prompt-representation-v1.json`。
- `apps/api/Features/Situation/Workflows/SituationDatasetSplit.cs`。
- `schemas/situation/situation-label-stats-v1.schema.json`、`situation-training-manifest-v1.schema.json`、`situation-prompt-representation-config-v1.schema.json`、`situation-representation-measurement-v1.schema.json`。
- `tests/CsDemoMap.Api.Tests/Situation/SituationFlowVerifier.cs`、`SituationSceneServiceVerifier.cs`、`SituationTrainingContractVerifier.cs`、`SituationTrainingPilotVerifier.cs`；`situation-implementation/stage4-pilot-report-20260919.md`。

`git diff --check` 通过；暂存区为空，HEAD 仍为 `b3c3bcb97a25b14ea8b083a861764752ce562627`，未提交、推送或发布；datasets/ 被忽略。回退边界是经用户审核后选择性撤回本轮源码差异或选用历史 complete 工件，绝不删除/覆盖任何数据目录。正式导出后没有修改 provenance 绑定的源码/Schema。

## 剩余风险与范围外事项

- `compact-v1` 精确 Qwen tokenizer 预算仍未通过；字节测量不是 token 测量，进入训练前必须复测，必要时新版本新目录。
- review candidates、复核工具和 220/40/40 人工标签尚未生成；本轮只交付模板预标注，不宣称人工质量真值。
- 仅支持/测量一个 match worker；新索引选择段未测得加速，当前主要收益是复用最终 payload。完整校验重复计算成本显著，不可把逐场时间当作总耗时。
- 性能测试没有清理 OS 页缓存，少量早期基准并行运行诊断；不宣称严格冷盘、隔离 CPU 或两 match worker 性能。
- `.writer-recovery` 与既有失败诊断占用额外磁盘，均保留；源码、配置或 Schema 漂移后拒绝旧目录恢复，不绕过绑定。
