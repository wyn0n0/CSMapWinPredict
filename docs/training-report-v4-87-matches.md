# 87 场 Mirage v4.2 模型训练报告

生成时间：2026-09-08

## 结论

删除文件名错误的 `g2-vs-m80-dust2.dem` 后，项目从剩余 87 份 DEM 重新导出 schema v4、`mirage-semantics-v4.2` 数据，并使用固定随机种子 42 保留 8 场完整比赛作为验证集。数据语义审计通过，比赛 ID 无重复，全部 166,122 条样本的 `mapName` 均为 `de_mirage`。

训练阶段只使用 79 场训练比赛，以按比赛分组的 5 折 OOF 加权 Log Loss 在逻辑回归、LightGBM 和三种校准方案间选择。最终选中 `logistic + sigmoid`。8 场固定验证集上的 Log Loss 为 0.4682，Brier Score 为 0.1585，ROC AUC 为 0.8423，准确率为 0.7691，ECE-10 为 0.0381。

## 数据与切分

| 项目 | 数量 |
|---|---:|
| DEM / 比赛 | 87 |
| 正式回合 | 1,899 |
| 时点样本 | 166,122 |
| T 胜回合 | 772 |
| CT 胜回合 | 1,127 |
| 训练比赛 | 79 |
| 训练样本 | 151,402 |
| 验证比赛 | 8 |
| 验证回合 | 172 |
| 验证样本 | 14,720 |

验证比赛从按文件名不区分大小写排序后的 87 场中，以 `random.Random(42).sample(..., 8)` 一次选定。名单为：

- `b8-vs-fut-m2-mirage.dem`
- `falcons-vs-mouz-m1-mirage.dem`
- `faze-vs-furia-m2-mirage.dem`
- `furia-vs-aurora-m3-mirage.dem`
- `fut-vs-mouz-m3-mirage.dem`
- `g2-vs-m80-mirage.dem`
- `legacy-vs-faze-m2-mirage.dem`
- `vitality-vs-fut-m1-mirage.dem`

## 训练集 OOF 选择

| 基模型 | 校准 | Log Loss | Brier | AUC | ECE-10 |
|---|---|---:|---:|---:|---:|
| Logistic | Identity | 0.4345 | 0.1455 | **0.8632** | 0.0205 |
| Logistic | Sigmoid | **0.4344** | **0.1453** | 0.8629 | 0.0166 |
| Logistic | Isotonic | 0.4346 | 0.1454 | 0.8613 | **0.0041** |
| LightGBM | Identity | 0.4400 | 0.1474 | 0.8605 | 0.0221 |
| LightGBM | Sigmoid | 0.4394 | 0.1470 | 0.8604 | 0.0178 |
| LightGBM | Isotonic | 0.4405 | 0.1475 | 0.8567 | 0.0128 |

选择准则是加权 Log Loss，所以使用 Logistic + Sigmoid；选择时未查看固定验证集结果。

## 固定验证集结果

| 模型 | Log Loss | Brier | AUC | Accuracy | ECE-10 |
|---|---:|---:|---:|---:|---:|
| Logistic | 0.4707 | 0.1593 | 0.8423 | 0.7686 | 0.0425 |
| Logistic + Sigmoid | **0.4682** | 0.1585 | 0.8423 | 0.7691 | 0.0381 |
| LightGBM | 0.4721 | 0.1588 | **0.8445** | **0.7695** | 0.0489 |
| LightGBM + Sigmoid | 0.4685 | **0.1578** | **0.8445** | 0.7678 | **0.0380** |

本次固定验证名单和上一轮不同，因此不能把两次指标当作同一测试集上的直接升降比较。验证集现在已被用于这次模型选择后的评估；后续调参应保留新的、从未查看过的比赛作为最终泛化测试。

## 产物与复现

本地数据目录为 `datasets/mirage-v4-20260908-87`，模型目录为 `models/win-baseline-v4-holdout-87-8-20260908`。两类目录均被 Git 忽略，应用默认模型路径已更新到本次模型目录。

- 训练数据 SHA-256：`5ec3d56b265a1f51ddeb5c228f75cfbfdb966cae8b252afd80ef34aadf8442cc`
- 选中模型 `baseline.joblib` SHA-256：`42f198ad900a17fa6f7deaaba44d80e25e155f65142c4e5d41da28b83fb4b760`
- 训练脚本 SHA-256：`c27b89012c0e46673b84818b77c6ec86bc113ebdc685736cd9b6b21a1e372f5f`

~~~powershell
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --export-win-data-v4 data/mirage datasets/mirage-v4-20260908-87
python tools/audit_win_v4.py --v3 datasets/mirage-68-local-v3.jsonl --v4-dir datasets/mirage-v4-20260908-87 --output datasets/mirage-v4-20260908-87/comparison.json
python tools/train_win_baseline_v4.py --input datasets/mirage-v4-20260908-87/samples.jsonl --manifest datasets/mirage-v4-20260908-87/manifest.json --comparison datasets/mirage-v4-20260908-87/comparison.json --validation-split datasets/mirage-v4-20260908-87/validation-split-87-8-seed42.json --output-dir models/win-baseline-v4-holdout-87-8-20260908 --threads 4 --folds 5 --seed 42
python tools/win_inference_service.py --model-dir models/win-baseline-v4-holdout-87-8-20260908 --check
~~~
