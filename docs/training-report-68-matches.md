# 68 场 Mirage 固定比赛验证摘要

实验日期：2026-08-30；核查日期：2026-08-31。

本文将本地已生成的 v3 评估结果整理为可随仓库提交的说明，没有重新训练或覆盖模型。来源为本地模型目录中的 report.json、report.md、validation-split.json 与 validation_predictions.jsonl；这些工件和原始 Demo 不随仓库分发。

## 数据与协议

- 地图：de_mirage；schema v3。
- 共 68 场、1,467 回合、128,441 条记录；T 胜 591 回合，CT 胜 876 回合。
- 63 场训练（118,609 条记录），5 场验证（115 回合、9,832 条记录）。
- 每个回合的样本权重之和为 1；所有指标使用该权重。
- 验证比赛由文件名不区分大小写排序后，使用 random.Random(42).sample(..., 5) 抽取；不是时间后移测试。
- 模型只拟合 63 场训练比赛，没有把验证比赛重新加入最终拟合；未拟合概率校准器。

## 总体验证指标

| 模型 | Log Loss ↓ | Brier ↓ | AUC ↑ | Accuracy ↑ | ECE-10 ↓ |
|---|---:|---:|---:|---:|---:|
| constant | 0.6982 | 0.2524 | 0.5000 | 0.5391 | 0.0629 |
| logistic | 0.4810 | 0.1627 | 0.8406 | 0.7459 | 0.0502 |
| lightgbm | 0.4912 | 0.1681 | 0.8364 | 0.7217 | 0.0739 |

当前代码固定选择 logistic，baseline.joblib 与 logistic.joblib 内容相同；此次其验证 Log Loss 也低于 LightGBM。报告中的选择指标措辞不代表脚本实现了自动择优发布。2026-08-31 加载保存模型复算全部验证记录，与已有 logistic 预测的最大差为 0。

## 分阶段指标

| 阶段 | 模型 | Log Loss ↓ | Brier ↓ | AUC ↑ | ECE-10 ↓ |
|---|---|---:|---:|---:|---:|
| live | logistic | 0.5255 | 0.1796 | 0.7936 | 0.0642 |
| live | lightgbm | 0.5378 | 0.1853 | 0.7898 | 0.0877 |
| post-plant | logistic | 0.2502 | 0.0755 | 0.9219 | 0.0536 |
| post-plant | lightgbm | 0.2495 | 0.0793 | 0.9273 | 0.0953 |

核查时还按原样本权重计算了两个逻辑回归切片：5v5 状态共 4,142 条，Log Loss 0.5952、AUC 0.7011、ECE-10 0.0926；live 且 remainingSeconds >= 110 的开局切片共 586 条，Log Loss 0.6242、AUC 0.6775、ECE-10 0.1010。这些是描述性切片，未重新做回合内权重归一化，也不是独立的新测试集。

## 固定保留比赛

| Demo | matchId |
|---|---|
| furia-vs-9z-m2-mirage.dem | `6cf330bc9d931cffdc088d59fcf38db9afa79d7a4f101268b677b5a635bcc468` |
| b8-vs-fut-m2-mirage.dem | `ccac2b282793dafb57d206ce4d2bcf10da7acbad78cd6bfbed0ac8486e529866` |
| mibr-vs-b8-m1-mirage.dem | `cff133b3c6f7ae9cc5a18aa3b827fcaf85475c21054fb42e789f07b92a79dca7` |
| liquid-vs-mibr-mirage.dem | `2cb312fb611579460db9062dae9799473b8d5cc2c0b2081651c46f7db680cd4e` |
| legacy-vs-faze-m2-mirage.dem | `c2f492c3f30ec0532c9e0cf0925f5dc8321da55dfa7fea6c11819ba23ec226bb` |

复现时应逐一传入上述完整 --validation-match-id，并使用新的输出目录；当前训练脚本没有保护历史输出目录的自动拒绝覆盖机制。需先在本地具备同一批原始 Demo 和 v3 JSONL。

## 已知限制与迁移基准

- 只有 5 场固定验证比赛，且已经查看过其结果，不能作为后续反复调参的最终独立测试集。
- 全体指标不能代表开局、5v5 或每个具体局面的表现；尚无真实直播质量与时延验证。
- 全量核查未发现重复样本键、回合内标签冲突或权重和错误，但这不等价于完整 schema 与比赛语义正确。
- 4 场中 87 个回合、7,607 条记录存在回合编号与比分推导值不一致；另有 live 初始 elapsedSeconds 达 822.47 秒，以及 67 条记录少于 10 个玩家实体但装备覆盖率仍为 100%。这些情况需复核，不能据此直接断言胜负标签错误或一律删除缺实体样本。
- [6 场历史实验](preliminary-training-report.md) 使用留一比赛交叉验证，与本次固定验证的分数不能直接解释为性能提升。

保留现有 datasets/mirage-68-local-v3.jsonl 和 models/win-baseline-v3-holdout-68-5 作为迁移基准。修正方案见 [v4 数据语义迁移](data-semantics-v4-plan.md)，具体上游接口依据见 [回合与时钟核查](round-clock-upstream-review.md)。
