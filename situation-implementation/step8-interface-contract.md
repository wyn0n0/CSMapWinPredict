# 步骤八实现接口（2026-09-19）

权威 C# 类型：`apps/api/Features/Situation/Review/Contracts/SituationReviewContracts.cs`。所有 JSON camelCase，缺字段/未知字段/重复字段拒绝，枚举沿用既有序列化。

主线 GPT-6 Astra/high；A/B/C GPT-6 Astra/low。所有角色只修改分配文件，不构建共享 .NET 项目；请求主线调度构建。禁止下级智能体与 Git 写操作。

## HTTP 与页面

- GET `/review/session` → ReviewSession，host 填充随机 Token；页面后续 POST header `X-Review-Token`。
- GET `/review/samples` → ReviewSampleSummary[]；列表轻量且可一次获取，筛选在客户端基于冻结 Categories/Decision/Split 完成。
- GET `/review/samples/{sampleId}` → ReviewSampleDetail。
- POST `/review/validate` → ReviewValidationResult；只校验 Narrative。无效返回 valid=false + 安全 errors。
- POST `/review/decisions` → ReviewSaveResponse。四项 evaluations 初始未选，保存必须全部 boolean。原文批准 editedNarrative=null；拒绝 final 文本不入库。
- POST `/review/replacements` → ReviewSaveResponse，其中 newSampleId 为新样本；拒绝和替换是两个明确动作。
- 错误 `{code: string}`，revision/workspace 冲突 409，未知样本 404，Origin/token 403，非法请求/内容 400，storage 503。
- 请求不接受路径或 client hash。requestId 用 UUID `crypto.randomUUID()`，一次逻辑操作重试保留 ID，编辑载荷后新 ID。
- 静态入口 `/`，资源 `/review-ui/<allowlisted-file>`，底图 `/review-assets/mirage.webp`。无远程资源、内联脚本或第三方框架。
- B 宿主接收 IReviewBackend、静态资源根、固定雷达文件路径、port。提供可测试 StartAsync + async dispose / WaitForShutdown，不负责解析 CLI 或构造 backend。

## 存储接口与事务

A 实现 `SituationReviewWorkStore.OpenAsync(string root, ReviewWorkspaceIdentity identity, IReadOnlyList<ReviewActiveSample> initial, Func<string,int,CancellationToken,Task<SituationReviewCandidateV1>> resolveCandidate, CancellationToken cancellationToken, ...可选故障注入)` 返回 IReviewWorkStore。

Snapshot 不得向调用者暴露可变内部集合；初始 revision=0。服务端保存 decision 已为 expectedRevision+1。A 再验证关联 candidate、dataset 身份、哈希、active、revision 和 sticky blocking（blocked 时只能 rejected）。sampleId 形状固定 `sample-[0-9a-f]{64}`，requestId 必须 Guid，文件名不能使用原始任意路径。

SaveDecision/Replace 内锁覆盖检查、意图、写盘、回读与发布；FindReceipt 对同 ID 异载荷抛 409，对同 ID 同载荷返回原 receipt。主线 backend 也序列化业务 mutation，避免先计算后备再被改动。

目录：workspace.json；decisions/<sampleId>.json；transactions/<zero-padded revision>.json（意图含完整 decision 或 replacement、request hash 和 receipt）；提交标记；index.json（可重建）；进程独占 lock。事务使用唯一 tmp/flush/原子替换，决定 v1 不扩字段。提交历史保留，可追溯改判与 rejected。

恢复先按连续 workspace revision 校验意图和提交链。未完成意图只在前状态/哈希吻合时完成重放，随后写提交标记；发生歧义或权威决定损坏停止服务，不自动覆盖。已提交历史用于验证最新 decision、恢复 receipts、sticky blocking、active 替换链。decisions 是当前样本决定权威，缺失/损坏不能只靠 index 伪造通过；有匹配未完成意图才可补齐受中断影响的决定。

原子决定发布后、确认前失败可在重试/重启恢复；成功回复前必须完成提交及回读。索引失败不影响已提交成功。注入点至少 intent、decision、commit、readback、index。

替换审计使用主线计算的 old/new/category/hash；A 必须核对旧决定确为 rejected 非 blocking、old active、new 未使用、ordinal 与 workspace revision=expected+1，并解析验证新 candidate。active/审计从提交事务重建；被拒样本绝不重新入池。
