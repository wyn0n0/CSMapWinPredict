# 步骤八三维射线复核（2026-09-26）

已将三维射线规则实际接入步骤八，300 条候选全部使用 `situation-analysis-rules-v2-raycast-1` 重算 Facts、evidence、置信度、空间优势和模板摘要。当前页面为 `http://127.0.0.1:13581`，初始进度 0/300、revision 0。未生成任何真实人工评价。

## 实现与版本边界

- `SituationRaycastReviewCatalog` 包装经过原有完整校验的历史目录。保留步骤七 300 个时点、220/40/40 split 和后备顺序，按需求重新分析；后备替换同样使用射线规则，不回落到 v1。
- 新候选 Schema 为 `situation-review-candidate-v2-raycast`，只能配合 v2-raycast-1 Facts；旧候选 Schema 继续只接受 v1。历史生产者快照保持哈希校验，新增消费端契约源码加入当前 provenance。
- 单独保存 complete 派生 manifest 与 300 行候选；manifest 绑定原工作区身份、规则哈希、碰撞网格配置及哈希、新 Schema 哈希和候选文件哈希。重新打开时重算并逐字节核验工件，变化时拒绝复用。
- 工作区 dataset/candidate 身份使用新派生 manifest 哈希，旧人工决定不能混入。sampleId 和 RecordSha256 保留原始场景/源记录身份，新输入由派生候选文件及 manifest 绑定；这批派生工件不是可直接替换旧训练记录的完整数据集。
- 筛选类别、抽样配额及替换约束仍是原 v1 抽样方案，页面已明确注明；不会将其宣称为按 v2 高接触等类别重新抽样的验收集。
- 保留原本的四项评价、阻断、原子保存、回读、并发 revision、拒绝替换和恢复机制；不新增产品 API，不使用导航网格。

## 工件与启动

候选目录：`datasets/situation-stage4-review-raycast-candidates-20260926-r1`。
工作目录：`datasets/situation-stage4-review-raycast-work-20260926-r1`。
旧候选、旧工作目录及人工记录保留。

```powershell
dotnet apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll --serve-situation-review datasets/situation-stage4-full-20260919-r1 datasets/situation-stage4-review-raycast-work-20260926-r1 --candidates datasets/situation-stage4-review-candidates-20260919-r1 --producer-snapshot datasets/situation-stage4-review-source-snapshot-20260919-r1 --raycast-candidates datasets/situation-stage4-review-raycast-candidates-20260926-r1
```

重启端口以输出为准。省略 `--raycast-candidates` 仍运行旧 v1 模式；新参数必须指向独立目录，不能嵌套或覆盖输入/工作目录。

- manifest SHA-256：`3da744c2dd990ee3a50f3e86eefac36200e1a675e9682aeedd12f3774ec2c6d6`。
- candidates SHA-256：`8a0e4ea2eb5bce23132d962715f2d2240a60056c9281ce9921344e0e83368e7e`。
- 300 条全部为新规则；168 条 contactRisk 变化，154 条 Narrative 哈希变化。

## 验证

- Release 构建 0 警告、0 错误；使用本机已有 NuGet 缓存恢复依赖。
- 射线检查 337 项通过，包括复核候选隔墙 high→low、与完整场景分析一致、源输入保留及摘要重算。
- 射线复核集成通过：生成、后备替换、批准、关闭后恢复、旧身份拒绝、候选篡改拒绝（均使用合成数据）。
- 原步骤八存储 75 项、工作流 31 项、消费端 21 项、HTTP 检查通过；UI 核心 11 项及阶段四契约 58 项通过。
- 按用户要求仅抽样 20 场：`datasets/contact-raycast-sample20-20260926-r2`，6,960 场景、3,851 条 contactRisk 变化，p95 1.6831ms。comparison SHA-256 与 r1 完全相同：`8ca56bd9f8707c4df7b0185976ad7d39a61fcbf4f8572ca61723e19171f81419`。没有重新解析或全量导出比赛。
- 浏览器确认真实首条显示 v2-raycast-1、contactRisk=low，以及“1 对被静态地图遮挡”的证据；同一条旧版为 medium。

静态碰撞地图不处理烟雾、穿透、未来绕角和动态物体，身体高度采用采样近似；历史 Demo 与网格版本匹配尚未认证。300 条人工复核及步骤九冻结仍待完成。AGENT.md、TODO.md、TODO_4.md 未改动；用户删除的旧并行文档未恢复。本轮未提交、未推送。
