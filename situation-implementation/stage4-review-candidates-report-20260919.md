# 阶段四步骤七：复核候选验收

核验日期：2026-09-19。步骤七代码与自动验收完成，候选等待用户审核；尚未进行人工复核，不存在最终人工标签或 reviewVersion。步骤八页面、decision/audit 持久化与步骤九人工冻结不属于本次交付。

## 正式工件与来源

- 正式候选：`datasets/situation-stage4-review-candidates-20260919-r1/`。
- 确定性复导：`datasets/situation-stage4-review-candidates-20260919-r2/`。四文件与 r1 逐字节一致，两者均 complete。
- 只读基础：`datasets/situation-stage4-full-20260919-r1/`，30,384 条、1,899 回合、71/8/8 场。manifest SHA-256：`e60e8bf001ea55b1932fba7bed83468ee104cac8516525fe539409edaae8a3f9`。
- 候选策略规范化 SHA-256：`5bfb76866f902fc7851b98d437acb0115e846e02a834442d4a1ddbf394e2ae7f`；配置为 `apps/api/Features/Situation/Training/situation-review-selection-v1.json`。
- 独立池审计：`datasets/situation-stage4-review-audit-20260919-r1/verified/audit.json`。同父目录初次诊断存在孤立枚举大小写错误，已明确废弃；修正审计脚本后新建 verified，不改写历史。权威审计与 C# 全池类别统计一致。

| 文件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| review-candidates.jsonl | 10,274,270 | `3588795dbd713cb9ff684b59776539c74c540a89641fb61883efe8d3a42565c7` |
| review-stats.json | 8,533 | `1375c4f512fcd955d5eaf94699c0da1b4b003757a2e45e365781b7938dc67af0` |
| provenance.json | 14,237 | `e50d52a44b925d5114b12c17fe66414b7fb485348c6ad94c320b19e488faedcc` |
| manifest.json | 35,813,312 | `9e0e1b4f58321041c208f5d6be716242f7340bef362a6ef43f407b929c8325f4` |

manifest 较大是因为保留了全池 30,384 条轻量来源索引与 39 组 split/category 后备 ID 排序；不复制全池场景载荷。来源索引绑定 sample、scene、input、Facts、prelabel 与原始记录哈希，行号仅用于定位，不能替代哈希校验。

## 数量与覆盖

| split | 候选 | 比赛 | 回合 | 每场条数 |
| --- | ---: | ---: | ---: | --- |
| train | 220 | 71 | 219 | 64 场各 3，7 场各 4 |
| dev | 40 | 8 | 40 | 每场 5 |
| test | 40 | 8 | 40 | 每场 5 |

sampleId、sourceSceneSha256、modelInputSha256 均为 300 个唯一值。最多每回合 2 条，无第三条事件例外。所有固定配额达标，quotaShortfalls 为空。

| 类别 | train 实际 / 目标 | dev 实际 / 目标 | test 实际 / 目标 |
| --- | --- | --- | --- |
| post-plant | 63 / 40 | 11 / 8 | 10 / 8 |
| clutch | 74 / 40 | 13 / 8 | 13 / 8 |
| formation-split | 49 / 40 | 11 / 8 | 13 / 8 |
| reliable isolated | 122 / 40 | 23 / 8 | 27 / 8 |
| high-contact | 79 / 40 | 15 / 8 | 14 / 8 |
| c4-transition | 47 / 24 | 6 / 无配额 | 2 / 无配额 |
| low-quality | 23 / 20 | 0 / 无配额 | 2 / 无配额 |
| ordinary-live | 74 / 10 | 11 / 2 | 11 / 2 |
| low-medium-contact | 141 / 10 | 25 / 2 | 26 / 2 |
| none-isolation | 98 / 10 | 17 / 2 | 13 / 2 |
| grouped-spread | 120 / 10 | 23 / 2 | 19 / 2 |
| high-confidence | 21 / 10 | 3 / 2 | 3 / 2 |

类别允许重叠。五类普通负例最低数在全池审计前固定为 train 各 10、dev/test 各 2。类别取自结构化 Facts/phase 和冻结事件标签；不读取 Narrative 的文字质量，不按 test 表现修改策略。test 只允许冻结后的最终评估，未运行模型或调整提示词。

## 实现与验收证据

- 按执行方案由主线 GPT-6 Astra / medium、三名子智能体 GPT-6 Astra / low 分工完成审计、纯选择器、工件工作流。主线负责冻结契约、集成、Schema、统一构建及真实验收。
- 贪心顺序为未达标类别数降序、当前比赛/回合数量升序、域分隔稳定 SHA-256 升序，最后 sampleId ordinal。小池穷举与确定性修复测试覆盖贪心陷阱；未证明不可行时仅报告搜索未解决，不伪造数学不可行结论。
- 后备按最终状态的相同排序冻结，替换校验要求同 split、同优先类别、首个满足约束且不降低配额覆盖的后备。拒绝记录、人工决定与替换审计仍须由后续复核工作区保存。
- 导出先取得相邻 `.review-publish.lock` 独占句柄，再创建新目录；写 incomplete、候选/统计/来源，独立回读成功后原子 complete。已有目录拒绝覆盖，失败保留诊断，锁句柄关闭后自动移除。
- 复用步骤六已经通过的完整语义回读证据，重新核对基础所有文件哈希、历史 85 个来源文件中未变的 83 项及旧 14 个 Schema。API csproj 与 CLI dispatcher 是本次必需集成差异，不拿当前消费者文件冒充历史生产者；固定历史 provenance 哈希，并为当前消费者另记 83 项源码/配置/Schema 哈希和 Git 状态。
- 对所有 30,384 条读出轻量池；300 条入选记录重新校验原始哈希、恢复场景来源并重算 Facts/Narrative。发布前回读再次核对候选、统计、后备顺序、来源、清单与基础哈希。
- Release API/测试及 CLI 构建均为 0 warning / 0 error；策略 11 项、选择器完整测试（含 16 个独立小池穷举 oracle）、工件 26 项通过。工件测试包括未知/错序/重复/短缺、事件匹配、CRLF/BOM/缺末尾 LF/非法 UTF-8、已有目录、并发锁、取消、写盘及回读失败。
- 既有阶段四契约 58 项、API 数据管线 38 项通过；没有修改 Web、场景构建或规则语义，不重复运行长耗时全量 Demo 解析。
- Python jsonschema Draft 2020-12 独立验证三个 JSON 文档及全部 300 条候选通过。
- 首次真实导出 80.76 秒；重复导出 78.72 秒；单独 CLI 回读验证 59.22 秒。重复导出与回读部分并行，耗时仅是本机该次观测，不是性能 SLA。耗时不写入决定性工件。
- `git diff --check` 通过。原有步骤五、六未提交改动保留，本次未暂存、提交或推送。

## 交接

步骤七候选技术验收通过，等待用户审核后用于步骤八本地复核页。TODO_4 的拒绝审计、阻断人工发现及最终 220/40/40 approved/modified 要求仍保持未完成，不以候选 complete 代替人工冻结。用户此前同意暂缓的上游低影响问题未在本轮扩大修复；完整基础目录及其哈希未变。
