# 阶段四 500 条 train-only 表示探针报告

> 核验日期：2026-09-19。本文只报告冻结 71 场 train 的表示探针；未读取 dev/test 的长度、类别或叙述表现。

## 工件与决定性

- 主工件：`datasets/situation-stage4-pilot-20260919-r10/`。
- 重复工件：`datasets/situation-stage4-pilot-20260919-r11/`。
- 两个 manifest 均为 `status=complete`、`mode=pilot`、`purpose=representation-measurement`、`trainable=false`、`sampleLimit=500`。
- 两次导出均覆盖 71 场、493 个回合、500 条唯一样本；单场最多 8 条。
- `--verify-situation-training-pilot <r10> <r11>` 的 20 项工件门禁通过。
- 两个目录的 6 个文件逐字节 SHA-256 全部一致；manifest 没有时间字段差异。

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `train.jsonl` | 17,188,098 | `4b8c1eec7608d6fdd95d3faa2c086d41ad237da60147ae4a3de6333f77e4a5b1` |
| `representation-measurements.jsonl` | 396,723 | `253f537fa1a79847763679f95bd906817b1b0a42067df04d43e6b247c56e4e74` |
| `label-stats.json` | 27,515 | `e3a4b23729f9c362f1097e6426f3c0ab592ea8c6a0708a01151e8dbd293bf6ce` |
| `provenance.json` | 19,360 | `8c72ef90ce850d7a52ea5bfc4296e73fd0fc9452e899b1d2a3a444db3b2abc0f` |
| `split.json` | 16,211 | `fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f` |
| `manifest.json` | 5,922 | `439d7e184d92e3565adb93cb7ae7e38914c6773e2bf1e170672178c8c94456e8` |

manifest 之外的工件合计 17,647,907 字节。若仅按理论上限 30,384 条线性外推，约为 1.07 GB；这只是容量规划值，不代表全量实际条数或最终体积。

## 表示测量

| 表示 | UTF-8 bytes min / p50 / p90 / p95 / p99 / max | 超过候选上限 |
| --- | --- | ---: |
| `expanded-v1` | 8,870 / 36,237 / 44,277 / 45,602 / 49,810 / 54,863 | 500/500 |
| `compact-v1` | 8,053 / 32,668 / 39,824 / 41,106 / 44,764 / 49,223 | 500/500 |

`compact-v1` 的 p95 比 `expanded-v1` 小 9.86%，最大值小 10.28%。场景段仍是主要体积来源，p95 为 39,315 bytes；Facts、evidence 和 label 的 p95 分别为 5,935、5,115 和 1,347 bytes。

## 冻结决定与边界

- 冻结 `inputRepresentationVersion=compact-v1`，配置版本为 `situation-prompt-representation-config-v1`，规范化配置 SHA-256 为 `5e0f7b57d127c81b004b5524d2f5b8e6af1193bc8d30b57e56a78d706d7a6a87`。
- compact renderer 只应用冻结短键映射和包装文本；完整 `train.jsonl` 不压缩，也不删除权威 Facts、evidence 原文、`allowedEvidenceIds`、当前人数/生命/C4、质量码或 Narrative uncertainty。compact 输入可精确还原为模型输入 JSON。
- 500/500 均超过当前 8,192-byte / 4,096-character 候选阈值，所以本决定只表示在两个已测候选中选择较小、可逆的一版，不表示已经满足阶段六序列预算。
- 当前未测 Qwen 精确 token 数。阶段五必须取得目标 tokenizer 后对这 500 条重新测量；若 p95 或 max 超出阶段六预算，必须升级表示版本并生成新目录，禁止改写 r10/r11。

## 真实分布摘要

- phase：live 416，postplant 84。
- contact risk：low 169，medium 191，high 140。
- C4：carried 185，dropped 189，planting 42，planted 35，defusing 49。
- pilot 贪心选择覆盖全部配置事件/稀有标签；例如 first-casualty 59、post-plant 42、formation-split 42、isolated 41、high-contact-risk 42、clutch-1vN 60。
- `tick-outside-live-range` 为 539,109，属于扫描完整 Timeline 时被共享 eligibility 拒绝的时点统计，不是 pilot 记录失败数。

## 诊断历史

`r1`–`r9` 均保留为 `incomplete` 诊断目录，不作为数据集使用。真实运行暴露并修复了两类上游问题：状态不相容的旧 C4 carrier/defuser 字段必须被掩码；训练场景必须沿用目标 tick 的 v4.2 semantic phase/round，而不能被原始 frame 中不一致的回合号覆盖。第 65–71 场随后独立重放通过，两次完整导出也均越过相同位置。
