# 阶段四步骤八：本地复核工具验收

日期：2026-09-19。技术结论：通过。步骤八工具已实现；真实人工决定为 0，步骤九冻结和阶段四整体未完成。

执行配置按用户要求：主线 GPT-6 Astra / high，三个子智能体 GPT-6 Astra / low。A 实现存储与恢复，B 实现独立宿主，C 实现静态页面；主线固定共享契约、实现历史消费/决定/替换/来源、完成集成、实际构建与测试。B 另对主线及页面做只读审查，A 对历史消费补充独立测试。

## 交付

- `apps/api/Features/Situation/Review/`：共享协议、历史工件 loader、业务决定门禁、冻结策略替换、工作区原子存储和当前消费者来源。
- `apps/cli/SituationReview/`：独立 ASP.NET Core 回环宿主；未向生产 API 注册任何路由。
- `apps/cli/SituationReviewUi/`：原生静态页面、匿名雷达、Facts/evidence、候选与编辑预览、四项评价、筛选导航、拒绝和显式替换。
- `schemas/situation/situation-review-work-v1.schema.json`：独立 work/transaction/commit/consumer Schema；旧 14 个冻结 Schema 及 decision v1 未改写。
- 四套新增 .NET verifier、页面 11 项 Node 测试、`test:situation:stage4:review` 统一入口。

修改命令和项目集成落点为 `apps/cli`。产品 `apps/web`、场景构建器、Facts 规则、胜率逻辑、训练流程均未在本步骤修改。

## 启动与恢复

从仓库根执行：

```powershell
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --serve-situation-review datasets/situation-stage4-full-20260919-r1 datasets/situation-stage4-review-work-20260919-r1 --candidates datasets/situation-stage4-review-candidates-20260919-r1
```

仅绑定 `127.0.0.1`；默认 port=0，由系统选取空闲端口，终端输出实际 URL。可指定 `--port <port>`，占用即失败。已有同身份 work 可用相同命令恢复，不复制、不合并其他数据集进度。运行实例应先退出，再重启或构建其正在使用的二进制。

候选独立于基础数据，所以 `--candidates` 必填。历史来源快照默认使用 `datasets/situation-stage4-review-source-snapshot-20260919-r1/`，可用 `--producer-snapshot <directory>` 显式指定匹配副本。缺少或篡改快照拒绝启动。

本次启动过的真实工作区为 `datasets/situation-stage4-review-work-20260919-r1/`，验收时 0/300，train/dev/test 未复核数为 220/40/40，workspace revision=0。页面中未提交任何真实决定。

`datasets/situation-stage4-review-ui-smoke-20260919-r1/` 是明确标识的**合成测试工作区**，保存了批准、修改、拒绝及一次后备替换（revision=4）。它不属于真实人工集，不能进入训练或人工冻结。

## 保存和阻断

工作区使用独占进程锁。所有决定经关联候选校验后写唯一临时文件、持久化 flush、原子替换，再回读校验。意图与提交标记形成连续事务链；已确认决定保留，revision 冲突不覆盖，requestId+请求摘要实现幂等重试。`index.json` 仅为可重建缓存。

恢复会私有重放事务，确认后一次发布完整状态；损坏权威决定或提交链停止恢复，不用索引掩盖。拒绝样本和改判历史保留；替换使用同 split、同优先类别的首个合法未使用后备，并重验配额、比赛/回合和唯一性约束。重开还对替换策略链做独立复验。

事实错误、身份泄露、未来信息或未知证据的人工报告保存为阻断拒绝，工作区持续禁用有效批准/替换。编辑中误输的未知引用只允许修正草稿或提交阻断报告，不能保存为有效 Narrative；工具不凭一次编辑动作伪造人工问题报告。

浏览器会保留冲突草稿；保存期间禁止导航与重复快捷键；成功回执后的列表/详情重读失败会要求显式重读后再写入。token 每次进程启动重新生成，只在页面内存和请求 header 中使用。

## 历史与当前来源

修改前使用既有 CLI 完成步骤七 300 条候选只读验证，并保存其 83 个生产来源文件及原 provenance。CLI csproj 和 Program 两个新增集成差异另保留与基础 provenance 原 SHA-256 精确一致的历史字节副本；复原后逐文件哈希验证，不把当前文件伪装为历史源。

新 loader 独立消费固定历史 manifest/provenance、文件清单及 Schema/策略哈希，核对全池来源索引，并对入选 300 条重建 scene/Facts/Narrative 关联。原有生产期验证器保持严格行为；新增消费入口为：

```text
--verify-situation-review-consumer <dataset-directory> <candidate-directory> <producer-snapshot>
```

CLI 集成变化不再使历史候选失效；语义源码、旧 Schema、基础或候选字节变化仍拒绝。当前 review 源码、UI、Schema 及运行程序集另写 `consumer-provenance.json` 会话哈希链，记录起始 workspace revision，支持消费者升级后的追溯。

输入身份保持：

| 文件 | SHA-256 |
| --- | --- |
| 基础 manifest | `e60e8bf001ea55b1932fba7bed83468ee104cac8516525fe539409edaae8a3f9` |
| 候选 manifest | `9e0e1b4f58321041c208f5d6be716242f7340bef362a6ef43f407b929c8325f4` |
| 候选 JSONL | `3588795dbd713cb9ff684b59776539c74c540a89641fb61883efe8d3a42565c7` |

## 实际验证

| 检查 | 结果 |
| --- | --- |
| Release API、CLI、测试项目最终构建 | 0 warning / 0 error |
| work store | 75 项通过，覆盖写盘/原子发布/回读/提交/索引故障、重启、同进程重试、并发冲突、改判及替换链 |
| HTTP 宿主 | 正负例通过：IPv4 动态端口、占用拒绝、Host/Origin/token、三类 POST、JSON 形状/重复字段、CSP/no-store、路径和错误脱敏 |
| 128 KiB 请求限额 | Content-Length 与 chunked 均返回完整 413 安全错误；修复了首轮 Kestrel 与应用双重限制导致响应截断的问题 |
| 业务流程 | 31 项通过：四项必填、原文/修改/拒绝、非法 Narrative/evidence、幂等、阻断、替换与重开策略链、消费者会话来源 |
| 历史消费者 | 20 项通过：83 文件、新消费者隔离、篡改/缺失及工作区路径关系 |
| 页面状态 | Node 11/11：筛选导航、乱序响应、草稿/冲突、请求去重、证据、快捷键、坐标与重复 JSON 键 |
| 既有阶段四 contracts | 58 项通过 |
| 既有 review-candidates | 策略 11 项、选择器、工件 26 项通过 |
| 既有 API 数据管线 | 38 项通过 |
| 真实历史工件消费 | 300 条通过，完整输入文件哈希和来源关联回读 |
| 独立 Draft 2020-12 验证 | 合成工作区 workspace/provenance/4 个事务/4 个提交标记共 10 文件通过 |
| 真实 work Schema 与最终差异 | workspace/provenance 两文件通过；真实 decisions 为 0；`git diff --check` 与新增文件空白检查通过 |
| 浏览器合成端到端 | 原文批准→修改→刷新恢复→拒绝→显式后备替换；新样本 revision=0、未复核，旧决定保留 |
| 真实本地视觉 | 只读加载 300 条；匿名玩家、HP、方向、轨迹、C4、烟火雷达和事实正常显示；浏览器 error 日志为空 |

实际 .NET 验证使用已构建 Release DLL 执行 `--verify-situation-review-automatic`、`--verify-situation-stage-four-contracts`、`--verify-situation-review-candidates-automatic`、`--verify-win-data-pipeline`；相应 npm 命令仍为日常入口。未重跑未受影响的 Demo 全量导出、stage3/semantics 或产品 Web 构建。

用户对复核口径与摘要实用性的人工判断仍是后续人工集工作，不以本次技术视觉确认代替。没有实现 freezer、生成真实人工标签、冻结 reviewVersion 或将阶段四整体标为完成。

## 工作树与交接

既有步骤五至七修改保留。本次新增 Review/CLI UI/测试/Schema，并增量修改 CLI 与测试项目、入口、package 和相关文档。未暂存、提交、推送、发布或删除历史工件。

下一步是用户使用页面完成人工复核并处理阻断问题，再进入步骤九冻结。工作区和历史来源快照均是本地材料，不公开；Simple Radar 图片只由固定本地路由读取，没有复制进数据工件。
