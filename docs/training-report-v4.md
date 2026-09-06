# Mirage schema v4.2 重训报告

实验日期：2026-09-06。

## 结论

68 场 Mirage 已从原始 DEM 按 mirage-semantics-v4.2 重新导出，并沿用原来的 63 场训练、5 场固定验证切分完成重训。模型与校准方法只按训练比赛的 5 折分组 OOF 加权 Log Loss 选择；最终选择逻辑回归与 identity（不额外校准）。

固定验证集上，新模型的 Log Loss 为 0.4776、Brier Score 为 0.1619、ROC-AUC 为 0.8420。对齐相同 matchId + tick 的 9,832 条公共样本后，v3 逻辑回归的 Log Loss 为 0.4810、v4 为 0.4776；主概率指标有小幅改善，但 ECE-10 从 0.0502 上升到 0.0557。语义修正与工件迁移已经成立，这 5 场已经参与过迁移检查，不能据此作最终泛化或上线结论。

## 数据与协议

- 数据：68 场、1,469 个正式回合、128,683 条样本；T 胜 590 回合，CT 胜 879 回合。
- 切分：63 场训练、5 场固定验证，名单和 seed 42 复用 v3 工件中的 validation-split.json。
- 权重：每个修正后正式回合内的样本权重和为 1。
- 候选：逻辑回归、LightGBM；校准候选为 identity、sigmoid、isotonic。
- 选择：训练集内部按 matchId 做 5 折 GroupKFold；校准器也只使用交叉拟合的训练 OOF 预测。
- 留出集：不参与基础模型、校准方法或模型选择，只用于最终迁移回归评估。

## 训练集 OOF 选择

| 基础模型 | 校准 | Log Loss | Brier | AUC | ECE-10 |
|---|---|---:|---:|---:|---:|
| 逻辑回归 | identity | **0.4318** | **0.1445** | **0.8646** | 0.0189 |
| 逻辑回归 | sigmoid | 0.4337 | 0.1450 | 0.8630 | 0.0148 |
| 逻辑回归 | isotonic | 0.4343 | 0.1455 | 0.8611 | **0.0061** |
| LightGBM | identity | 0.4407 | 0.1481 | 0.8595 | 0.0259 |
| LightGBM | sigmoid | 0.4411 | 0.1480 | 0.8581 | 0.0203 |
| LightGBM | isotonic | 0.4419 | 0.1484 | 0.8551 | 0.0080 |

## 固定验证结果

| 模型 | Log Loss | Brier | AUC | Accuracy | ECE-10 |
|---|---:|---:|---:|---:|---:|
| v4 逻辑回归（选中） | **0.4776** | **0.1619** | 0.8420 | 0.7428 | **0.0557** |
| v4 LightGBM | 0.4844 | 0.1640 | **0.8433** | 0.7395 | 0.0654 |

同一公共样本与相同修正回合权重下：

| 模型 | Log Loss | Brier | AUC | Accuracy | ECE-10 |
|---|---:|---:|---:|---:|---:|
| v3 逻辑回归 | 0.4810 | 0.1627 | 0.8406 | **0.7459** | **0.0502** |
| v4 逻辑回归 | **0.4776** | **0.1619** | **0.8420** | 0.7428 | 0.0557 |

## 分场与状态检查

| 固定验证比赛 | 回合 | Log Loss | AUC | ECE-10 |
|---|---:|---:|---:|---:|
| FURIA vs 9z | 29 | 0.5518 | 0.7919 | 0.1102 |
| B8 vs FUT | 22 | 0.4699 | 0.8447 | 0.0830 |
| MIBR vs B8 | 20 | 0.4128 | 0.8396 | 0.1064 |
| Liquid vs MIBR | 23 | 0.4745 | 0.8411 | 0.0760 |
| Legacy vs FaZe | 21 | 0.4485 | 0.8769 | 0.0675 |

| 状态切片 | Log Loss | Brier | AUC | ECE-10 |
|---|---:|---:|---:|---:|
| live | 0.5224 | 0.1785 | 0.7957 | 0.0705 |
| post-plant | 0.2458 | 0.0758 | 0.9260 | 0.0581 |
| 5v5 | 0.5985 | 0.2124 | 0.6939 | 0.1185 |
| 残局（总存活不超过 6） | 0.2303 | 0.0670 | 0.9721 | 0.0570 |
| live 前 5 秒 | 0.6250 | 0.2224 | 0.6669 | 0.1163 |

开局与完整 5v5 仍是最弱区域；post-plant 与残局更容易判断。固定验证只有 115 个回合，分片结果用于定位风险，不能据此继续在留出集上调参。

## 工件与复现

本地输出目录为 models/win-baseline-v4-holdout-68-5-20260906。model-manifest.json 绑定 schema、语义版本、数据/审计/训练脚本哈希与固定验证比赛；baseline.joblib 是选中的统一推理工件。目录还包含两个候选模型、训练 OOF 预测、验证预测、特征清单、逐项报告和固化推理样例。

~~~powershell
python tools/train_win_baseline_v4.py --input datasets/mirage-v4-20260906/samples.jsonl --manifest datasets/mirage-v4-20260906/manifest.json --comparison datasets/mirage-v4-20260906/comparison.json --validation-split models/win-baseline-v3-holdout-68-5/validation-split.json --v3-predictions models/win-baseline-v3-holdout-68-5/validation_predictions.jsonl --output-dir models/win-baseline-v4-holdout-68-5-20260906 --threads 4 --folds 5 --seed 42
~~~

运行环境为 Python 3.13.5、NumPy 2.1.3、pandas 2.2.3、scikit-learn 1.6.1、LightGBM 4.6.0。训练后已验证 10 个记录工件的 SHA-256，并重新加载模型复算 3 个固化样例；最大概率差为 1.67e-16。

下一道门槛是引入完全未查看、时间后移的新比赛，冻结本次模型后只做一次泛化评估。通过后再把 v4 特征构建与 baseline.joblib 接入回放推理接口和前端每回合胜率曲线。
