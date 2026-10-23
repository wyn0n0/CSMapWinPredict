# 胜率预测 M2 实验协议 v1

> 初版：2026-10-12；状态更新：2026-10-14。登记、冻结和评估工具已实现。用户选择将新增 13 场全部用于检验既有默认模型，该分支按[独立方案](win-baseline-blind-protocol-20261014.md)完成，见[报告](../win-baseline-blind-report-20261014.md)。下述新候选开发／采用流程的完整元数据、切分和采用门槛仍待冻结；13 场现已观察，不得再次作为候选盲测，旧 8 场也仍仅作诊断。

2026-10-15 增补：用户确认以原 79 场训练、这 13 场已观察开发执行 [M4 开发分支](win-m4-development-20261015.md)，20 候选对照已完成，见[报告](../win-m4-development-report-20261015.md)。该独立入口明确禁用最终测试和自动采用，允许在不伪造旧来源元数据／采用门槛的前提下开展开发诊断；不取代本文完整候选采用流程，也不回写原盲测登记。

## 1. 保护基线与实验用途

- 现有 `datasets/mirage-v4-20260908-87/`、默认模型包及 `validation-split-87-8-seed42.json` 保留原样。原 79 场训练、8 场验证的成员不重新抽取；旧报告数字不回写。
- 旧 8 场使用 `legacy-validation` 角色，只做已观察过的诊断。后续实验使用 `train`、`development`、`test`；新 test 必须未参与方案选择且比赛日期晚于所有其他用途的比赛。
- 冻结单位是完整来源比赛。Demo 字节相同由 SHA-256 去重；同场比赛重新编码的不同 Demo 由登记的 `matchKey` 去重。`matchKey` 必须来自可核验的赛事比赛标识，不能使用文件名代替；登记工具不能自动识别错误填写的来源身份或“未观察”声明。
- 只涉及 Mirage 当前回合胜方、schema v4 / `mirage-semantics-v4.2`。局势语料的 71/8/8 切分不参与本协议。

## 2. 登记与冻结

[登记工具](../../tools/win_experiment.py)的 `init` 仅生成待补齐模板；日期、赛事、游戏版本、来源和原始 Demo 路径未知时留空。已在本地忽略目录 `.artifacts/win-m2-20261012-r1/registration/` 生成 87 场模板，原 79/8 用途保留。原始数据和模型没有复制或移动。

每场需登记：`matchId`（等于 Demo SHA-256）、`demoSha256`、`matchKey`、`playedAtUtc`（带 UTC 时区）、`eventId`、`gameVersion`、`mapName`、`parserVersion`、`source`、`role`、`unseen` 和 `demoPath`。旧比赛也需补齐来源与日期，才能证明新 test 的时间关系和去重范围。登记文件含本地路径，应保存在忽略目录；冻结结果移除 `demoPath`，仅保留文件名与来源元数据。

`experiment-plan.json` 必须在查看 test 指标前填写：

- 半开时间窗口 `[startUtc, endUtc)` 和准确的 `matchCount`；最少两场只是计算区间的最低条件，不代表统计证据充足。
- 候选方案名称。
- `maxLogLossRegression`、`maxBrierRegression`、`maxKeySliceLogLossRegression` 和 `maxInferenceMillisecondsPerSample`。这些是采用前需确定的容忍度/计算预算，本轮不猜测数值；统计工具不自动采用或切换模型，耗时预算须由后续独立性能测量核验。

`freeze` 会重算原始 Demo 哈希，拒绝重复 Demo/比赛身份、旧切分变更、已观察的 test、缺失元数据、时间倒置、测试场数不符或空门槛。输出新 `experiment.json`，绑定来源清单、旧切分、登记表、计划及评估源码哈希，并打印冻结文件自身的 SHA-256。后续运行必须显式提供此哈希；源码变化需要审阅后在新目录重新冻结，不能修改旧冻结文件。

示例中的输出目录必须不存在，`<...>` 参数需替换后执行：

```powershell
python -B tools/win_experiment.py init --legacy-manifest datasets/mirage-v4-20260908-87/manifest.json --legacy-validation-split datasets/mirage-v4-20260908-87/validation-split-87-8-seed42.json --output-dir <new-registry-directory>
python -B tools/win_experiment.py freeze --legacy-manifest datasets/mirage-v4-20260908-87/manifest.json --legacy-validation-split datasets/mirage-v4-20260908-87/validation-split-87-8-seed42.json --registry <completed-registry.json> --plan <completed-experiment-plan.json> --output-dir <new-frozen-directory>
```

## 3. 指标、分组和不确定性

主指标为按原始回合权重计算的 Log Loss；同时报告 Brier、AUC、准确率和 ECE-10。每个保留回合的样本权重和为 1；切分场景或预测缺失后不重新归一化回合权重。报告记录点数、按 `(matchId, roundId)` 去重的回合数、比赛数和权重和。

| 分组 | 定义 |
| --- | --- |
| `totalAliveAtMost6` | 双方总存活人数 ≤ 6，取代误称 `clutch` 的旧评估口径 |
| `oneVsN` | 双方均有活人，至少一方仅剩 1 人，包含 1v1 |
| `oneVsOne` / `oneVsMany` | 分别为 1v1、1v至少2人；均排除 0vN |
| `fiveVsFive` | T 和 CT 各 5 人存活 |
| `openingFiveSeconds` | 已知回合开打计时在 0–5 秒 |
| `live` / `postPlant` | 直接使用已导出的阶段字段 |
| 装备价值差 | 正、零、负及未知；使用装备价值，不把它称为全部经济资源 |
| 装备/位置缺失 | 相应已知存活人数少于总存活人数 |
| 下包后时间段 | 已知爆炸剩余时间 0–10 秒、10–20 秒、超过 20 秒；不从固定炸弹时长推算 |

空分组保留为无指标；单一胜负类别的分组保留 Log Loss、Brier 等，AUC 为 `null` 并写明原因。手枪局需要核实赛制和半场元数据，本轮在 `unavailableSlices` 明确说明，不能只凭回合编号猜测。

覆盖率分两层：

1. `exportCoverage`：导出保留点 /（保留点＋质量拒绝点），分母仅限已完成且符合回合条件的定时采样候选；不包含作废尝试、其他不合格回合和未采样的回放 tick。
2. 各模型 `coverage`：已输出预测的点数和权重 / 当前评估面全部导出样本的点数和权重。缺失行或 JSON `null` 代表未输出；非有限数值、越界概率和重复键失败。

不确定性使用 SciPy `bootstrap`：整场比赛有放回重采样，2,000 次、seed 42、95% percentile 区间。Log Loss/Brier 先按比赛汇总加权损失和权重，再对这些汇总量重采样；测试与逐行复制整场样本的算法对照一致。主指标总体及开局、5v5、下包后、1vN 关键切片均报告区间。少于两场时明确不可计算；8 场旧验证的区间仍是小样本诊断。

模型差值使用共同有预测的相同样本、相同权重和相同比赛重采样，定义为“候选减参照”，负的 Log Loss/Brier 差值有利于候选。输出共同样本及比赛数量；不得混用各模型不同覆盖面的整体分数解释提升。AUC/ECE 本轮报告点估计，不伪造它们的区间，也不把总体区间解释为单次预测置信度。

## 4. 基线、校准与评估入口

固定 T 胜率基线直接使用 scikit-learn `DummyClassifier(strategy="prior")`，仅用训练分区标签和回合权重估计。小模型使用现成的中位数填补、标准化和逻辑回归，只输入双方存活人数及装备价值四个字段；参数固定，不看评估集选择参数或校准方法。

原逻辑回归/LightGBM 及 identity、Sigmoid、Isotonic 的训练内按比赛分组 OOF 流程保留。新数据的模型实验另行执行；M2 的冻结实验入口只接收已生成预测，不训练候选模型。开发评估只保留 train/development 的记录，不将 test 标签放入训练或选择用的表中。

复核旧模型的已保存预测时，可运行：

```powershell
python -B tools/train_win_baseline_v4.py --input datasets/mirage-v4-20260908-87/samples.jsonl --manifest datasets/mirage-v4-20260908-87/manifest.json --comparison datasets/mirage-v4-20260908-87/comparison.json --validation-split datasets/mirage-v4-20260908-87/validation-split-87-8-seed42.json --evaluate-predictions models/win-baseline-v4-holdout-87-8-20260908/validation_predictions.jsonl --output-dir <new-diagnostics-directory>
```

此模式不加载或重训已有模型，但会在训练分区拟合上述两个诊断基线。开始真实运行前仍需核对输入、内存、耗时和新输出目录；本轮只执行合成数据的端到端演练，未运行此 87 场命令。

默认评估 `selectedTWin`、`logisticTWin`、`lightgbmTWin`，可重复传入 `--probability-column` 选择已冻结的比较列；`--reference-column` 指定参照，缺省为首个预测列。样本按 `(matchId, roundId, tick)` 严格对齐并核对标签/权重；原始模型与校准后模型必须用明确列名区分。

新数据开发评估在同一命令上增加 `--experiment <experiment.json> --experiment-sha256 <frozen-sha256> --evaluation-role development`，输入换为相应已审计的新导出和预测。test、旧诊断集及其他未选用途不进入基线拟合与评估表；未知登记比赛被拒绝。冻结的 seed/重采样次数不得临时改变。

最终测试只允许保存预测评估模式，需增加 `--evaluation-role test --candidate-lock <candidate-lock.json> --candidate-artifact <candidate-model>`。候选锁在开发阶段完成，包含 `status="frozen"`、`selectionPartition="development"`、`experimentSha256`、`modelArtifactSha256` 和 `probabilityColumn`；工具校验实际模型文件哈希，并要求每条预测也携带一致的 `modelArtifactSha256`，只接受锁定的一个候选列。工具不执行该模型文件，也不把文件哈希校验误称为重新推理验证。模型来源与预测生成记录须由实验执行者一并保存。

最终 test 用于一次冻结候选的检查，不再调整校准、特征、阈值或候选。工具拒绝 test 拟合、未锁定候选和来源不匹配；“只观察一次”仍需执行流程遵守，工具不会监控人工查看文件或把重复运行重新称为盲测。比赛足够后按赛事留出的敏感性实验需独立冻结；本轮没有为未知赛事分布自动生成新切分。

输出为独立目录中的 `report.json`、`report.md`、`reliability.png`、`evaluation_predictions.jsonl` 和最后写入的 `evaluation-manifest.json`。报告记录输入/预测/切分/源码哈希、运行依赖、seed 和分区成员；completion 清单绑定输出文件哈希。目录已存在即拒绝，失败目录保留供检查。可靠性图沿用仓库已有加权分桶，绘图使用 Matplotlib；分组及逐比赛详情在 JSON 中。

## 5. 验收与复用依据

本轮通过 `npm run test:train:v4`（28 项）、`npm run test:audit`（7 项）、`npm run test:inference`（7 项）。覆盖 1v1/1v3/3v3/0v3、单类别和空组、权重/覆盖率、整场配对重采样、重复比赛、时间和用途约束、最终测试禁拟合、哈希漂移、输出拒绝覆盖及原输入不变。合成数据已贯通登记冻结、开发评估、锁定候选的最终评估，并确认候选文件变更后拒绝输出；可靠性图已经渲染检查。这些不代表真实模型评估结果。

开工前核查并采用现成实现：[scikit-learn 指标](https://scikit-learn.org/stable/modules/generated/sklearn.metrics.log_loss.html)、[DummyClassifier](https://scikit-learn.org/stable/modules/generated/sklearn.dummy.DummyClassifier.html)、[SciPy bootstrap](https://docs.scipy.org/doc/scipy/reference/generated/scipy.stats.bootstrap.html)。[scikit-learn calibration_curve](https://scikit-learn.org/stable/modules/generated/sklearn.calibration.calibration_curve.html)没有样本权重参数，因此抽取复用原有加权分桶，没有另写校准算法。SciPy 1.15.3、Matplotlib 3.10.0 已存在于本次验证环境，现显式记入训练依赖；本轮没有安装或升级环境。
