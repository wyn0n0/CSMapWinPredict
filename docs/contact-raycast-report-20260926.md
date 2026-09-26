# Mirage 三维射线接触风险修复

核验日期：2026-09-26。范围：静态地图遮挡；按用户要求排除导航网格，仅抽样 20 场。

## 回退点

- 修复前提交：`3965b647ce81ff7cb5c66478b54f71a52553c2bf`。
- 标签：`checkpoint/pre-contact-raycast-20260925`；现场确认与开始时 HEAD 一致，工作树干净。
- 修复分支：`codex/contact-raycast`；`main` 保持在修复前提交。
- 另已生成 `datasets/checkpoint-pre-contact-raycast-3965b64.zip`，核对 295 个条目及分析器源码存在。
- ZIP SHA-256：`97fb4841609a49c991aa67520ede4dd2184ca5371bcdec72428f5fdfe22cfe42`。

checkpoint/ZIP 保存 Git 管理的源码与文档，不包含被忽略的 DEM、训练数据、模型及人工复核工作区。
本次没有覆盖这些原有工件。可把 ZIP 解压到新的空目录恢复旧源码；或创建独立旧版工作树：

```powershell
git worktree add --detach datasets/rollback-3965b64 checkpoint/pre-contact-raycast-20260925
```

在原目录直接 `git switch main` 不会自动丢弃未提交修复，不能把它当作完整回退；优先使用上述独立目录。

## 实现与入口

- `SituationCollisionMesh` 严格读取固定哈希 AWMH v1 文件，构建一次 BVH，执行双面、有限线段的三角形求交。
- `SituationVisibilityQuery` 反解现有 Mirage 归一化 XYZ，使用 36/52/64 Hammer 单位高度的九条组合射线。
- `AnalyzeContact` 先用原距离/线索规则筛选，再检查所有潜在中高风险组合；按风险、距离、槽位确定主导无遮挡组合。
- 全部候选被遮挡时给出当前直接接触低风险。位置或网格信息不足且可能改变最高风险时返回 unknown。
- 不把已排除的隔墙组合继续传给局部交战中心计数。其他队形、孤立、包点和胜率距离仍沿用原算法。
- 规则版本为 `situation-analysis-rules-v2-raycast-1`，配置 SHA-256 为
  `d23efc445ab2c9d945314b42e91806c1b0227f39f72fc2077fcdcc16aa4cfd8f`。
- 原 `CreateFrozen()`、candidate-1/2 和历史数据消费链保持 v1；新入口使用 `CreateRaycast()`。
  历史复核页面不会自动重写既有预标注；本次未把 v2 升格为旧训练数据的冻结规则，未重导出历史训练集。

```powershell
python tools/prepare_contact_geometry.py
dotnet build tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release --no-restore -p:NuGetAudit=false
dotnet apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll --analyze-situation-raycast <完整scene.json> <新的结果.json>
npm run test:situation:raycast
dotnet apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll --sample-situation-raycast datasets/situation-stage4-full-20260919-r1 <新的输出目录>
```

离线资产准备可用 `python tools/prepare_contact_geometry.py --archive <geometry.zip>`。
已存在同哈希资产只核验；不同内容拒绝覆盖。准备完成后重建，资产复制到程序集旁的 `Geometry/`。
分析命令缺少网格时保守输出 unknown；20 场验收命令要求网格存在，且错误哈希/格式明确失败。

## 资产来源与限制

使用 [Awpy Data release 2000917](https://github.com/pnxenopoulos/awpy-data/releases/tag/2000917) 的
`de_mirage.mesh`，1,409,404 字节、73,290 个三角形。下载 ZIP 和提取文件均校验 SHA-256。
旧 Awpy `awpycs.com` 地址返回 403，最终使用官方 GitHub 发布资产。
来源、哈希、版权和获取脚本见 `data/geometry/README.md`、`THIRD_PARTY_NOTICES.md`。

该资产匹配本机当前游戏 ClientVersion 2000917，但未认证 20 场历史 Demo 与此版本地图完全一致。
上游网格经过简化；固定身体高度采样不能精确恢复蹲姿、身体宽度和部分露出。
此版本没有导航、未来绕角预测、烟雾、动态物体、视野方向或子弹穿透判断。低风险不是零风险。

## 验证结果

- Release 构建：0 警告、0 错误。
- 射线/集成检查：333 项，通过。包含墙体双面、有限射程、平行/零长度射线、楼板、窗口、低掩体、坐标反解、
  300 个解析矩形相交对照、主导组合、unknown 汇总、并发一致性、资产哈希与配置检查。
- 原规则检查：159 项，通过；冻结规则 SHA-256 保持 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。
- 契约：65 项；原候选选择：148 项，均通过。
- 新单场景 CLI 入口冒烟通过；资产准备脚本对已有资产核验通过。
- `git diff --check` 通过。

20 场结果保存在 `datasets/contact-raycast-sample20-20260926-r1/`。
选择方式：仅在冻结 train 清单中，对 `contact-raycast-sample-v1\n<matchId>` 做 SHA-256 排序取前 20 场；
逐场名单与样本量见 `report.json`。没有对 dev/test 调参，也没有全量 DEM 重解析或全量导出。
重放这些比赛已有的所有已选场景，验证源文件、恢复场景和旧 Facts/Narrative 哈希，然后生成独立对照输出。

| 指标 | 结果 |
| --- | ---: |
| 比赛数 | 20 |
| 场景数 | 6,960 |
| 旧规则重放一致 | 6,960 / 6,960 |
| 新接触风险等级变化 | 3,851 |
| 被遮挡组合观测数（跨场景累计） | 12,068 |
| 独立重复确定性检查 | 20 |
| 分析 p50 / p95 | 0.6899 / 1.5878 ms |

耗时包含当前 Facts/Narrative 构建，不包含网格初始化和源数据读取。

| 旧→新 | 场景数 |
| --- | ---: |
| High→High | 315 |
| High→Medium | 146 |
| High→Low | 1,373 |
| Medium→High | 23 |
| Medium→Medium | 324 |
| Medium→Low | 2,305 |
| Low→Medium | 4 |
| Low→Low | 2,470 |

少量升档来自修正“只看最近一对”：稍远的无遮挡组合可以具有更高的朝向/靠近线索得分。
这些变化是技术回归结果，不能解释为人工准确率；原样本由 v1 选择，亦非无偏评估集。
逐条对照 `comparison.jsonl` SHA-256：`8ca56bd9f8707c4df7b0185976ad7d39a61fcbf4f8572ca61723e19171f81419`。
