# 阶段四步骤八重新启动记录（2026-09-26）

本轮只读核对 `AGENT.md`、`TODO.md` 阶段四与 `TODO_4.md` 步骤八，没有改写这三份文件。基于现有工具修复并重新验收，保留旧工作区和已有人工决定；用户删除的并行方案文件未恢复。

## 修复

新增独立三维射线规则后，旧复核入口仍要求当前五个分析源码文件逐字节等于历史生产者，导致启动时 SHA-256 校验失败。现在这五个文件使用原生产者快照校验，当前分析源码另计入消费端 provenance。基础数据、候选、冻结规则、Schema 和历史快照的哈希校验继续保留；每个选中候选和后备替换仍须使用当前冻结 v1 分析器重算，并与原 Facts、Narrative 和候选内容精确一致。

页面显示候选实际规则版本，避免将历史 v1 工件误认为已经应用射线 v2。此轮没有重新导出数据，也没有自动生成真实人工评价。

## 验证

- Release 构建：0 警告、0 错误。
- 步骤八自动门禁：存储 75 项、工作流 31 项、消费端 21 项及 HTTP 验证通过。
- 页面核心 Node 测试：11 项通过。
- 独立合成工作区通过浏览器完成四项评价、保存和刷新恢复；持久记录 revision 1。测试决定仅存在于 `datasets/situation-step8-restart-synthetic-20260926-r1`。

## 新复核入口

新工作目录为 `datasets/situation-stage4-review-work-20260926-r1`，原 `datasets/situation-stage4-review-work-20260919-r1` 保留。启动命令：

本轮实际启动地址为 `http://127.0.0.1:2511`；已通过浏览器确认真实候选加载、版本提示和页面布局，初始进度 0/300、workspace revision 0。

```powershell
dotnet apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll --serve-situation-review datasets/situation-stage4-full-20260919-r1 datasets/situation-stage4-review-work-20260926-r1 --candidates datasets/situation-stage4-review-candidates-20260919-r1 --producer-snapshot datasets/situation-stage4-review-source-snapshot-20260919-r1
```

地址以启动输出为准。候选仍为步骤七已冻结的 300 条 v1 候选，220 train / 40 dev / 40 test。射线抽样验收仍单独使用此前 20 场结果；没有把新版事实混写进历史标签。300 条人工复核和步骤九标签冻结尚未完成。
