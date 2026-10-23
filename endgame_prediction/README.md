# 残局胜率预测

> 2026-10-23 更新：GSI 已修复 CT 拆弹器字段省略处理，首场同场复验见[修复报告](../docs/win-gsi-defusekit-fix-report-20261019.md)。跨比赛准确性仍待验证，已发布模型权重不变；回放界面更新单独发布在 `main`。

使用**仅以残局样本重训的 C4 模型**，预测 Mirage **当前回合**的 T／CT 获胜概率。本目录提供源码、示例和验证报告；本分支同时提供默认残局模型包；训练数据和录制结果保留本地，不随 GitHub 分发。现有前端、API、生产默认模型和启动配置不接入此模块。运行时只读复用仓库 `tools/` 的模型加载、特征校验及推理协议，因此本目录需与仓库一起使用。

残局指双方**存活人数合计 ≤7**，遵循[项目定义](../docs/plans/win-broad-endgame-retrain-20261015.md)：双方各有 1–5 人，处于 `live` 或 `post-plant` 阶段。例如 3v4、4v3、2v5、3v3、1v1 均接受；4v4、5v3、0vN、冻结／已结束阶段均不预测。人数未知、布尔值、非整数或超出 0–5 时拒绝输入，不能用缺失人数猜测残局。

## 下载并预测

下载[本分支 ZIP](https://github.com/wyn0n0/CSMapWinPredict/archive/refs/heads/codex/endgame-model.zip)并解压完整仓库，或执行：

```bash
git clone --branch codex/endgame-model --single-branch https://github.com/wyn0n0/CSMapWinPredict.git
cd CSMapWinPredict
```

建议使用 Python 3.13（模型验证环境为 3.13.5），在仓库根创建虚拟环境并安装推理依赖。Windows PowerShell：

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements-inference.txt
.\.venv\Scripts\python.exe -B endgame_prediction/predict.py --check
Get-Content endgame_prediction/example-request.json | .\.venv\Scripts\python.exe -B endgame_prediction/predict.py
```

Linux／macOS：

```bash
python3.13 -m venv .venv
.venv/bin/python -m pip install -r requirements-inference.txt
.venv/bin/python -B endgame_prediction/predict.py --check
.venv/bin/python -B endgame_prediction/predict.py < endgame_prediction/example-request.json
```

无需训练数据、原始 Demo、Node.js 或 .NET 即可运行独立预测。请下载整个分支，单独下载本目录会缺少 `tools/` 共享依赖。`--check` 成功返回 `status: ready`；示例返回 T／CT 当前回合获胜概率。预测自己的局面需准备下文约定的完整特征，或按 GSI 指南接入完整观战信息；仅给双方人数不足以预测。

## 直接运行

从仓库根执行，使用已安装 `requirements-inference.txt` 所列依赖的 Python。默认模型位置为 `runs/20261017-endgame-only-r1/model/`；**检出 `codex/endgame-model` 分支即包含该模型包**，安装依赖后可运行以下命令。也可用 `--model-dir` 指定其他完整且通过加载校验的 `win-broad-endgame-v1` C4 包。缺少模型时检查会报错，不会自动下载或退回生产模型。

```powershell
# 加载模型、检查文件完整性并核对原模型自检样例
python -B endgame_prediction/predict.py --check

# 用完整的 1v4 示例预测（文件为单行 JSON）
Get-Content endgame_prediction/example-request.json | python -B endgame_prediction/predict.py

# 定向测试
python -B -m unittest endgame_prediction.test_predict endgame_prediction.test_train
```

示例返回 `ok: true`，`result.predictions[0]` 中 `tWin` 约为 `0.00431377`，`ctWin` 约为 `0.99568623`，两者之和为 1。这只是所附完整局面的结果，不能当作所有 1v4 的固定胜率。

## 重新训练与旧模型对照

本次训练集为 79 场／1,692 回合／52,768 点；测试集为 13 场／263 回合／8,361 点，全部为残局。沿用原比赛成员，不做时点随机拆分；5 折内部验证和概率校准仅使用残局训练集。13 场测试比赛以前已经观察过结果，本次不将它们称为新盲测，也不用于调参。

训练复用现有 v4.2 数据及已审计的 C4 空间伴随文件。使用现有 `requirements-train.txt` 对应环境；输入、父切分及依赖须匹配 2026-10-15 冻结计划。新一轮必须指定**尚不存在**的输出目录：

`train.py` 是这次冻结实验的复现入口，还依赖本地 `.artifacts/win-broad-endgame-20261015-r1/frozen/plan.json` 及其绑定数据。GitHub 不包含这些工件，新检出不能仅凭下列命令重建模型；模型复现与新的数据训练需分别准备匹配输入。

```powershell
python -B endgame_prediction/train.py --output-dir endgame_prediction/runs/endgame-only-new-run
python -B endgame_prediction/predict.py --model-dir endgame_prediction/runs/endgame-only-new-run/model --check
```

训练命令不会覆盖旧工件，也不会自行修改默认模型路径。每轮保存计划、源码快照、过滤后的 `train.jsonl`／`test.jsonl`、比赛切分、训练内预测、模型、测试预测与指标。数据行将无标签的推理输入放在 `sample`，标签、权重和来源放在 `metadata`；只将 `sample` 传给预测入口。完整结果见[本次训练报告](TRAINING_REPORT.md)。

原 `model/` 中的完整回合训练模型保留为对照，可显式加载：

```powershell
python -B endgame_prediction/predict.py --model-dir endgame_prediction/model --check
```

除已发布的 `runs/20261017-endgame-only-r1/model/` 外，`runs/` 的其他工件、旧 `model/` 和 `verification/` 均被 Git 忽略。发布包包含权重、特征清单、自检样例、模型清单、训练指标和训练折外预测数组；六个文件均保持原始字节与哈希。新检出若缺少**旧对照副本**、且本地仍保留原实验工件，可运行一次：

```powershell
python -B endgame_prediction/prepare_model.py
```

此脚本只恢复旧对照，不生成本次残局专用权重。它核验固定模型身份及每个文件的 SHA-256，再复制到不存在的 `model/`；已有目录会拒绝覆盖。原工件位于 `.artifacts/win-broad-endgame-20261015-r1/results/B_c4_logistic/`。新模型由上面的训练入口生成，不会下载替代模型或改用生产默认模型。

## 输入与输出

标准输入／输出采用 JSON Lines：**每行一个完整 JSON 请求**，支持 `health`、`predict`、`shutdown`。示例是完整可执行输入；字段清单及空间配置见所选模型目录的 `feature-manifest.json`。每个预测请求需包含：

| 字段 | 值 |
| --- | --- |
| `id` | 调用方的字符串或整数标识 |
| `op` | `predict` |
| `schemaVersion` | `4` |
| `semanticVersion` | `mirage-semantics-v4.2` |
| `samples` | 1–512 个局面，每项含下列契约与全部 114 个输入字段 |
| `samples[].featureContract` | `win-broad-endgame-v1` |
| `samples[].spatialConfigSha256` | 必须匹配模型清单中的空间配置哈希 |
| `samples[].features` | 以点分路径为键的完整特征对象，或使用原有嵌套结构 |

单行上限 4 MiB。字段必须存在；允许未知的普通特征可显式给 `null`，存活人数与地图必须有效。需要匹配的 v4.2 基础特征和 `win-spatial-v1` C4 空间特征，**仅输入双方人数不足以运行模型**。不可把旧 98 字段样本只改版本号后使用，也不要填零冒充缺失的空间信息。模型内部计算广义残局标志，不使用旧 `isClutch` 或调用方伪造的派生标志。

成功结果按输入顺序返回 `tWin`、`ctWin`（0–1）。含任一非残局或非法项时，整批返回 `ok: false` 和错误码，例如 `not_endgame`、`invalid_player_count`、`unsupported_map`；不会生成部分概率或退回全局模型。客户端可修正请求后继续使用同一进程。禁止携带赢家、`labelTWin` 等未来标签。

也可在 Python 中直接调用：

```python
import json
from pathlib import Path
from endgame_prediction.predict import EndgamePredictor

request = json.loads(Path("endgame_prediction/example-request.json").read_text())
model = EndgamePredictor()
predictions = model.predict(request["samples"])
```

## GOTV GSI 接入

独立接收与适配层位于 [`gsi/`](gsi/README.md)，复用本入口已训练模型，提供本机 HTTP 接收、最新预测查询和配置模板。独立 `gsi/record.py` 可将整场收到的原始快照、接收时间和逐条预测写到新目录，默认端口 3002；[完整录制步骤](gsi/README.md#录制一场完整比赛)包含配置、启动、检查与停止方法。工程测试已通过；真实 GOTV 特征对齐及准确性尚未验证，不影响原 Demo 功能。

## 模型来源与适用范围

沿用[2026-10-15 重训报告](../docs/win-broad-endgame-retrain-report-20261015.md)的 C4 特征、逻辑回归结构和 sigmoid 概率校准方法，**重新拟合所有预处理、模型权重与校准器**。本次模型 SHA-256 为 `356a2093fca1de4e6ef8ad2493e5beddfbf84b1971d773dfd5903e7ecd53c962`。

同一残局测试集上，新模型 Log Loss 为 0.299243、加权准确率 86.29%；旧完整回合模型分别为 0.296503、86.88%。新模型总体误差点估计略高，差值的 95% 区间跨零，不能认定稳定改善或稳定退化。保留原样本权重，未根据测试结果继续搜索参数。

目前只适用于 Mirage，仍属于开发候选。独立入口默认使用本次残局模型；正式替换产品默认模型仍需新的未观察比赛验证。
