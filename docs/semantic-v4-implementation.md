# Mirage v4 数据语义实现

实现日期：2026-09-06；语义版本：mirage-semantics-v4.2。此文描述已经加入代码的 v4 诊断/导出路径；最终批量结果见 [验收报告](semantic-v4-validation.md)。

## 使用边界

旧 `--export-win-data`、回放 API 和 v3 训练脚本保留原语义。新增路径通过 `ParseAsync(..., collectSemantics: true)` 启用；不能把 v4 JSONL 交给 v3 模型，也不能仅修改 schema 数字进行训练。

本轮没有自动切换网页回放、发布模型或接入胜率推理。

## 实现

- `RoundStateTracker`：在 command 完成后统一处理开始/live/结束信号。正式编号在非热身 live 边界用规则编号、已完成回合数及比分总和交叉确认；一次尝试一个 `roundId`。同 command 的结束先关联旧尝试，再处理新开始。胜方必须与开始比分到结束比分的增量一致。作废、结果未确认和结果缺失分别保留在审计中。比分回退时，恢复点及之后被替代的已计分结果也标记作废，较早的有效回合保留。
- `RoundClockResolver`：使用同一游戏时间域的 `CurrentGameTime - RoundStartTime`。冻结时间不进入 liveElapsed；下包后回合倒计时为 null，爆炸/拆包分别计时。真实轨迹证明战术 timeout/等待标志可能提前出现在正常 live 中，因此不凭这些标志一律停表。硬暂停、技术暂停、无法解释的等待/暂停 tick 变化使该回合时钟未知；已确认的新 live 零点可清除此前冻结等待的不确定性。
- `RoundRosterTracker`：回合内累计观察到的双方阵容；缺失的活人不默认死亡，已确认死亡可在 Pawn 消失后保留。回合/区段切换清理历史。冻结期死亡不继承为正式 live 阶段的持续死亡证据，避免断线/重生后仍判死亡。阵容、生死、存活位置和存活装备分别计数。当前支持标准 5v5；无法确认五人阵容的队伍不输出训练样本。
- `DemoSemanticCollector`：逐 command 更新状态；8 Hz 抽样同 tick 采用最后观察到的 command 状态。记录每秒规则快照及所有回合信号/暂停上下文变化。
- `WinDatasetV4Exporter`：按已完成尝试隔离 C4 历史和装备查找范围。每秒采样一次，筛选后每回合权重和为 1。正式编号、身份、地图和回放定位保留在样本顶层；玩家身份不进入特征。

## v4 特征契约

- 唯一键：`(matchId, roundId, tick)`；`matchId` 是原始 DEM 的 SHA-256。
- `features.clock` 包含 nullable 的 live、回合、爆炸、拆包时间及可信度/来源；不继续输出容易混淆的旧 elapsedSeconds/remainingSeconds。
- `features.quality` 包含阵容人数、生死已知数、存活人数、存活位置/装备已知数和质量原因。分母不是当前快照里恰好存在的玩家数。
- v4 的 `t/ct` 聚合及其差值仅统计当前存活玩家，包括 totalMoney/totalArmor/equipmentValue 等资源；它们不代表包含死者的全队经济。当前 totalKills/totalDeaths 同样是这些存活玩家的统计和。
- 生死或阵容不明：不可用，剔除并计数。时钟不可信：当前导出策略也剔除，但审计保留原因。
- 仅装备缺失：保留部分缺失样本，两队资源总量和资源差保守置 null，不把已观察到的部分和当真值；质量中保留实际已知数量。
- 仅存活位置缺失：保留生死统计，空间聚合置 null。坐标占位只用于适配旧内部计算接口，不进入最终空间特征。
- 下包后的回合倒计时不适用，与未知不是相同原因；通过 phase、clockKnown 与字段 null 联合解释。

输入质量判定不依赖胜负。按阶段/最终胜方报告保留及剔除数量只用于事后审计，不作为筛选规则。

## 命令

```powershell
dotnet build apps/api/CsDemoMap.Api.csproj --no-restore -c Release
npm run test:semantics
npm run test:audit

# 输出必须是不存在的新目录；原有目录不会覆盖。
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --export-win-data-v4 data/mirage datasets/mirage-v4-new-run

# 完整解析与截断解析的历史状态/特征一致性验证。
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-demo-prefix data/mirage/9z-vs-faze-m1-mirage.dem 65000

python tools/audit_win_v4.py --v3 datasets/mirage-68-local-v3.jsonl --v4-dir datasets/mirage-v4-new-run --output datasets/mirage-v4-new-run/comparison.json
```

输出目录含 `samples.jsonl`、各源文件哈希对应的 `*.audit.json` 和完成后才生成的 `manifest.json`。没有完成 manifest 的目录是未完成产物，不应进入训练。逐场审计保留回合尝试、规则/事件、编号变化、质量剔除计数和阶段/胜方分布。v4.2 保存剔除样本的 tick 与原因；pauseFlagObservations 是暂停/等待标志的诊断观察数，hardPauseObservations 单独统计硬暂停/技术暂停标志，并不将战术等待误报为不支持场景。

`audit_win_v4.py` 独立检查 schema、唯一键、回合标签、权重、字段空值约定和同区段正式回合冲突，再按源哈希/tick 比较 v3/v4。它不会训练模型，也不把样本删除解释为性能提升。


独立上游玩家诊断可用 `--trace-roster <demo> <fromTick> <toTick>`，只读取指定区间的回合信号、死亡事件和每秒玩家状态。输出包含本地玩家 ID，仅用于归因；不要当作训练特征。
## 验证及后续

合成回归覆盖热身、同 command 作废/重开、重复事件、比分冲突、半场/加时、比赛回退、冻结/暂停、拆包取消、死亡/缺实体及缺失装备/位置。真实 DEM 前缀检查覆盖历史状态和特征；v4 导出测试另验证未来装备改动不改变历史输入。

仍需专门的真实硬暂停/技术暂停恢复样本，才能将这些目前标记未知的场景升级为支持。引擎默认属性是否代表已接收字段仍依赖上游实体契约；当前装备质量检查是按服务/实体可用性保守判断，并非逐网络属性的接收证明。

语义数据验收后，下一步是独立 v4 训练契约、固定 63/5 名单迁移对比与新比赛测试。现有 v3 训练脚本保持拒绝 v4，这是版本保护，不是导出故障。
