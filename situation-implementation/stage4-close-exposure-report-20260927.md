# 150 单位内暴露判为高风险（2026-09-27）

> 合并状态：用户授权后，实现及验收记录已保存为 `bc30cd8`，并于 2026-09-27 从 `codex/contact-raycast` 无冲突快进合入本地 `main`。合并前主分支 `3965b64` 由 `checkpoint/pre-merge-main-20260927` 保存，功能分支保留。未推送，未冻结 reviewVersion，本地数据集和人工决定不纳入 Git。下文“未提交”描述为合并前历史记录。

按用户要求，已验证局部水平暴露距离不超过 150 地图单位（含 150）时，将潜在接触风险从 medium 调整为 high；大于 150 时仍为 medium。独立规则 `situation-analysis-rules-v8-close-exposure-1` 在 v7 上新增 `visibility.localPeek.highExposureDistance: 150`，旧规则默认不启用，旧版本序列化和哈希保持不变。

## 判定边界

- 仍先验证地面、身体可达性及暴露位置的无遮挡射线。不能用敌我直线距离代替已验证的单方移动距离。
- 距离沿用水平世界单位，以输出采用的六位小数精度比较。150 单位对应按水平速度 200 单位/秒估算的 0.75 秒。
- 保留 600 单位范围、64 单位采样间隔和末端 600 单位探测，不新增 150 单位采样点。生产采样中 64、128 单位成功结果升级为 high；192 单位及更远成功结果仍为 medium。阈值作用于已验证结果，不保证发现连续空间中所有 150 单位内的路径。
- 保留原候选顺序与首次成功即返回，不为了找更短路径或更高风险继续探测，也不扩展敌我组合初筛。
- 1 秒速度预测继续采用原规则；直接射线、失败、unknown 等分支不变。潜在 high 不代表当前已交火，置信度上限仍为 medium，假设位置不加入当前交战中心。
- `contactRisk`、诊断 `contact-risk` 和中文 evidence / 模板中的风险等级一致。

## 验证与使用

独立 Release 输出 `datasets/close-exposure-runtime-20260927-r1/`，0 警告、0 错误。666 项射线/局部移动/预测/斜坡/距离检查通过，其中新增 63 项检查覆盖 150 以下、恰好 150、150.000001、不可达/未知、反向移动玩家、查询顺序不变和版本隔离。六版本复核派生、保存、替换、恢复、隔离与篡改拒绝通过；复核 UI 11 项通过。

相同 20 场 train / 6,960 场景重放完成，输出 `datasets/contact-close-exposure-sample20-20260927-r1/`。500 条 medium→high，恰好对应 v7 中已验证水平距离为 64 或 128 的场景；其余标签不变。high 338→838，medium 1,346→846，low 3,574、unknown 1,702 不变。逐场断言升级符合阈值、其余事实字段一致，局部/预测探测计数一致；每场首条重复结果哈希一致。

局部探测总次数仍为 201,016，844 条距离记录、28 条预测成功均保留。同轮 p95 为 v8 6.3523 ms、v7 6.5337 ms；单次测量波动不解释为性能提升，确定的是没有增加探测次数。58 项阶段四契约与 50 条范围门禁通过。未执行全量导出，未自动提交人工决定。

v8 规则 SHA-256：`d4f3d505f90a5da09dcdff37696cb5d0bfb70ac62883595a5d416e9124bfd080`；comparison SHA-256：`f3e7a28b968daa8a44458719af31b3e926129537ba0c11f39b9e6339a2747829`。

相同 20 场 train / 6,960 场景重放完成，输出 `datasets/contact-close-exposure-sample20-20260927-r1/`。500 条 medium→high，恰好对应 v7 中已验证水平距离为 64 或 128 的场景；其余标签不变。high 338→838，medium 1,346→846，low 3,574、unknown 1,702 不变。逐场断言升级符合阈值、其余事实字段一致，局部/预测探测计数一致；每场首条重复结果哈希一致。

局部探测总次数仍为 201,016，844 条距离记录、28 条预测成功均保留。同轮 p95 为 v8 6.3523 ms、v7 6.5337 ms；单次测量波动不解释为性能提升，确定的是没有增加探测次数。58 项阶段四契约与 50 条范围门禁通过。未执行全量导出，未自动提交人工决定。

v8 规则 SHA-256：`d4f3d505f90a5da09dcdff37696cb5d0bfb70ac62883595a5d416e9124bfd080`；comparison SHA-256：`f3e7a28b968daa8a44458719af31b3e926129537ba0c11f39b9e6339a2747829`。

相同 20 场重放命令（输出目录必须不存在）：

```powershell
dotnet datasets/close-exposure-runtime-20260927-r1/CsDemoMap.Cli.dll --sample-situation-close-exposure datasets/situation-stage4-full-20260919-r1 <new-output-directory>
```

独立 50 条复核命令，不迁移旧版人工决定：

已启动 http://127.0.0.1:5179，初始 0/50（36/7/7）。50 条风险分布 high 10、medium 2、low 28、unknown 10；原 v7 的 8 条局部暴露均符合阈值并升级为 high。复核第 10 条（原 ordinal 69）已核对 HTTP 返回：CT3 水平移动 128 单位、0.64 秒、潜在接触风险为高。v8 页面资源正常，300 条新候选全部通过 Draft 2020-12 JSON Schema 校验。

```powershell
dotnet datasets/close-exposure-runtime-20260927-r1/CsDemoMap.Cli.dll --serve-situation-review datasets/situation-stage4-full-20260919-r1 datasets/situation-stage4-review-close-exposure-work-20260927-r1 --candidates datasets/situation-stage4-review-candidates-20260919-r1 --producer-snapshot datasets/situation-stage4-review-source-snapshot-20260919-r1 --close-exposure-candidates datasets/situation-stage4-review-close-exposure-candidates-20260927-r1 --review-target 50 --port 5179
```

旧 v7 运行时 `datasets/verified-exposure-runtime-20260927-r1/`、候选与工作区保留，可按 [v7 报告](stage4-verified-exposure-report-20260927.md) 回到全部局部暴露最多 medium 的规则。未修改 AGENT.md、TODO.md、situation-implementation/TODO_4.md，未提交或推送。

## 人工复核通过（2026-09-27）

用户确认已全部复核通过。只读核对服务进度、落盘 index 和每个样本最新决定：50/50 approved（train 36、dev 7、test 7），未复核 0、拒绝 0、blocking issue 为 false、替换 0；workspaceRevision 51。50 条最终评价全部为 `factsCorrect=true`、`focusReasonable=true`、`summaryAccurateUseful=true`、`hallucination=false`。

核验时 `index.json` SHA-256 为 `a3e92ca4a1a09afffe71c463cc02a3c1c558e26984212504c2677b6cdc80a8a4`；`review-scope.json` SHA-256 为 `a6c7793b8e58cbda3979d3921f2dc6da5b682cb3c01c515542f407d409796902`。此为当前 50 条人工范围验收，不外推为全量准确率；未修改决定、未执行冻结或提交。下一步为适配 50 条范围的步骤九冻结器，历史只读 TODO 中的 300 条目标不得直接当作本次验收数量。
