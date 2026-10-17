# M4 固定开发对照方案

> 2026-10-15。用户确认采用原 79 场训练、已揭盲的 13 场开发；原 8 场保留诊断用途。本轮完成开发对照，不切换默认模型，不宣称完成新候选的最终盲测或采用准入。

## 数据与冻结

沿用原 schema v4 / `mirage-semantics-v4.2` 定时采样及回合等权重。79/8 成员保持不变；13 场来源复用 M2 登记，但在新计划中明确改为 `development`、`unseen=false`，不回写旧证据。旧比赛完整来源登记仍缺失，本分支不宣称完成 M2 的时间隔离和跨来源去重认证。

M3 第一批按已冻结的支援半径 861.320、包点半径 582.716 世界单位，为全部 79+13 场导出独立伴随数据。复用已核验生产源码及 DLL，3 个进程并发，各写新目录。逐场重算空间量，并将基础样本全部字段与旧导出比较；任一错配即失败。半径不根据开发结果重选。

训练前以 `train_win_experiments.py freeze` 在新目录冻结准确成员、样本及空间清单哈希、旧模型、源码快照、依赖、seed、折数、线程数、特征和候选。正式执行必须提供冻结文件 SHA-256；漂移、已有输出和最终测试用途均被拒绝。所有本地数据、模型和计划保存在忽略目录，不进入公开发布面。

## 固定对照

每个特征组同时运行逻辑回归与 LightGBM，均沿用原模型参数。所有主对照固定 Sigmoid 校准，使一次比较只改变表中指定输入。校准器只用 79 场训练内按比赛分组的 5 折 OOF 预测拟合；identity、Sigmoid、Isotonic 的训练内交叉校准指标一并记录，不能根据开发结果临时改主对照校准方法。

| 实验 | 原始字段数 | 定义 | 对照 |
| --- | ---: | --- | --- |
| E0 | 98 | 原 v4 全量字段，重新训练及重新评估；核对既有默认模型逐点输出 | 既有默认模型 |
| E1 | 29 | 双方人数、血量、装备价值、护甲、头盔、拆包工具、手雷总数、装备覆盖；时钟、阶段、炸弹和必要质量状态 | 同算法 E0 |
| E2 | 51 | E1＋现有直线距离／队形聚合、位置质量及炸弹区域 | 同算法 E1 |
| E3 | 128 | E0＋M3 第一批 30 个 C4／队形／质量字段 | 同算法 E0 |
| E4_no_kd | 94 | E0 独立移除双方累计击杀／死亡四字段 | 同算法 E0 |
| E4_no_progress | 91 | E0 独立移除回合数、比分、连败及相应差值七字段 | 同算法 E0 |
| E5_weapon_counts | 33 | E1＋双方步枪／狙击枪数量四字段，先验证已有类别计数 | 同算法 E1 |

E5 的烟／闪／火细分尚未进入 v4 或 `win-spatial-v1`。上游 `CapturePlayerEquipment` 保存当前库存，现有聚合只输出手雷总数及步枪／狙击枪数量；需要新的按当前时点导出契约、完整性和缺失比例核验后再扩展。本轮 E5 只是已有类别的子实验。E6 依赖尚未核验的导航数据，本轮不以二维直线距离代替。

模型设置仅增加四个独立候选：E0 逻辑回归的 `C=0.3`、`C=3`（原值 1）；E0 LightGBM 的叶数／深度 `7/3`、`31/7`（原值 `15/5`）。不增加早停或阶段拆分。

逻辑回归再独立比较两个 E0 交互组：`下包阶段 × 人数差`、`爆炸剩余时间 × CT 是否有拆包工具`。缺失阶段／时钟／工具保持缺失；拆包工具仅代表现有库存，不推断实际可达或必定拆包。共 20 个候选，不根据开发结果扩增搜索。

## 评估和解释

- 主指标为原回合权重下的 Log Loss；同时报告 Brier、AUC、准确率、ECE-10、可靠性分桶、覆盖率、逐比赛和全部 M2 场景。
- 配对差值为候选减固定参照；共同比赛、样本与权重，2,000 次整场重采样、seed 42、95% 区间。关键场景包含开局、5v5、下包后及 1vN。多个候选的区间仅作探索诊断，没有多重选择校正，不能当成最终泛化证明。
- 原始和校准后概率分别保存；训练 OOF 数组绑定统一行顺序及完整比赛折分。开发排名只决定后续值得验证的候选，不修改原默认模型。
- 分组解释包含人数、血量、装备、时钟／炸弹、空间、比赛上下文和质量。逻辑回归解释为未校准的 log-odds（对数优势）加和，并检查可重建原始输出；不是“胜率增加若干百分点”。LightGBM 只报告按组汇总的训练分裂增益，不作因果解释。
- 每包保存模型、校准器、输入清单、依赖、训练／计划来源及推理样例；全部开发点通过独立加载的 Python 实验运行时核对。以 256 点分批测量验证、建表及推理耗时，不混入 Demo 解析时间，也不当成在线单点承诺。

## 版本和采用边界

基础数据仍是 v4.2，实验模型另用 `featureContract=win-m4-v1`。E3 明确绑定 `win-spatial-v1` 的半径、参考点、距离定义和配置哈希。新模型只由 `ExperimentalWinRuntime` 加载；原生产推理入口拒绝带实验契约的包。交互计算位于序列化模型管线中，训练与推理共用。

`finalTestEnabled=false`、`promotionEnabled=false`、`adoptionCriteria=null`。这些明确表示本轮不具有采用资格，而非把缺失门槛视为通过。最终采用仍需补齐 M2 来源、预先冻结容忍度／计算预算及新的未观察比赛，再锁定一个候选一次检验。

## 命令与验收

```powershell
python -B tools/train_win_experiments.py freeze --legacy-dir <legacy-v4-directory> --development-dir <observed-development-directory> --development-registry <M2-registry.json> --validation-split <original-79-8-split.json> --baseline-dir <existing-model> --spatial-root <new-export-root> --observed-development --output-dir <new-freeze-directory>
python -B tools/train_win_experiments.py run --plan <plan.json> --plan-sha256 <frozen-sha256> --output-dir <new-results-directory>
python -B tools/train_win_experiments.py predict --model-dir <experimental-bundle> --input <current-feature-samples.jsonl> --output <new-predictions.jsonl>
npm run test:train:m4
npm run test:train:v4
npm run test:inference
```

实验推理输入显式提供 `featureContract`、扁平特征对象 `features`；E3 还需匹配的 `spatialConfigSha256`。推理样例可作为输入形状参考；标签和最终胜方禁止传入。没有将实验包接入 .NET API 或前端。
