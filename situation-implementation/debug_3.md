# 阶段三置信度整改记录

> 编写与核验日期：2026-09-15。
> 本文记录阶段三置信度规范的矛盾、修正及验收结果。
> 当前状态：置信度规格澄清与定向回归已完成；冻结规则和既有输出语义未改变。

## 1. 已核实问题

阶段三原规范曾同时将“落在边界带内”列为 low 和 medium 条件，无法据此唯一确定预期等级。

已核实的实现按 `low → medium → high` 优先级判断，单纯靠近阈值返回 medium，已有边界测试明确要求这一结果。

- 实现依据：`apps/api/Features/Situation/Analysis/SituationFactsAnalyzer.cs` 的 `AnalyzeConfidence`。
- 测试依据：`tests/CsDemoMap.Api.Tests/Situation/SituationRuleVerifier.cs` 中的置信度边界与优先级检查。
- 配置依据：冻结训练工件中的 `rules.json`，`boundaryBand=0.02`、`comparisonEpsilon=0.000001`、`lowUnknownCount=2`。

## 2. 统一后的判定标准

按顺序执行，命中即停止：

| 等级 | 条件 |
| --- | --- |
| low | 存在参与规则的相关 error，或内部未完成规则数达到 `lowUnknownCount`，当前值为 2 |
| medium | 未命中 low，且存在相关 warning、恰有一个未完成规则，或判定值处于边界带 |
| high | 上述条件均未命中 |

边界带采用当前实现：`abs(margin) <= boundaryBand + comparisonEpsilon`，当前分别为 `0.02` 和 `0.000001`；排除仅记录配置半径的 `configured-radius` 项。

“未完成规则数”按内部 `Complete` 状态统计，分别包含双方阵型、争夺区域、压力、接触、孤立和空间优势；不得直接统计输出中所有 unknown/null 字段。相关 error/warning 按现有质量投影与 `qualityImpacts` 映射判定，不将所有输入警告一律视为相关。

## 3. 整改与验证结果

- [x] 修正规范中 low 的重复“落在边界带内”条件，补齐判定优先级和未完成规则统计定义。
- [x] 核验相关 error 优先于 medium、一个与两个未完成规则、边界内外及精确边界。
- [x] 复用原有规则阈值精确边界检查，并补充 6 项独立置信度回归。
- [x] 将距离、比例与评分共用边界带列为已知限制；本次不改变其数值或统计方法。

验证结果：Release 构建 0 警告/0 错误，`--verify-situation-rules` 共 159 项通过；阶段三统一自动门禁共 5 套通过，并回读 30 条 candidate、30 条冻结训练、16 条保留验收和 32 条冻结场景。冻结规则 SHA-256 仍为 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。

## 4. 影响与边界

本次属于规格澄清和测试补强，不改变 Facts、Narrative、冻结规则版本或现有工件哈希，不需要重新校准或训练。距离、比例与评分继续共用同一 `boundaryBand`；若未来需要按规则类型拆分，必须另建 candidate 并只在训练侧校准。

## 5. 区域连通性方案与复核状态

- [x] 用户于 2026-09-15 决定放弃区域连通性方案。阶段三继续采用已冻结 v1 的二维距离、同区关系、朝向和历史接近趋势，不新增区域邻接资源或 v2 候选。
- [x] 当前规格已统一为“同区关系”，明确 v1 不引入区域邻接图。
- [x] 训练侧 candidate-2 的 30 条复核表已全部勾选通过；冻结配置与 candidate-2 的规则行为相同，仅 `analysisRuleVersion` 发生变化。
- [x] 保留侧 8 场 16 条的独立复核表已全部标记通过，阻断问题为 0。

复核依据：

| 范围 | 权威复核记录 | 状态 |
| --- | --- | --- |
| 训练侧 5 场 30 条 | `datasets/situation-stage3-calibration-20260915-r7/review.md` | 30/30 已复核通过 |
| candidate-2 到冻结 v1 | `datasets/situation-stage3-calibration-frozen-20260915-r1/candidate-comparison.json` | 仅规则版本字段变化 |
| 保留侧 8 场 16 条 | `situation-implementation/stage3-acceptance-review-20260915.md` | 16/16 已复核通过 |

冻结训练与保留验收目录中的原始 `review.md` 是生成时写入 complete manifest 的模板，其 SHA-256 与 manifest 当前一致。为保留冻结工件的完整性，不回写这两个模板；复核结果由上表所列的独立记录承载。
