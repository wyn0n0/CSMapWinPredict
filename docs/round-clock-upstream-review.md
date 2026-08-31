# 回合与时钟接口核查

核查日期：2026-08-31。范围为 v4 方案的第一步（回合身份与正式回合号）和第二步（时间语义），不包括实时源部署、完整性修复、全量数据迁移或模型重训。

本次未修改解析器、训练代码、v3 JSONL 或模型工件。真实 Demo 探针在系统临时目录独立编译，使用项目现有依赖；本文件记录研究核查，不代表修复已实现。整体执行顺序见 [v4 迁移方案](data-semantics-v4-plan.md)，最新模型基准见 [68 场验证摘要](training-report-68-matches.md)。

## 1. 固定上游版本

- demofile-net：`fd59701a998cf30a46adc4942e063d90de73c07a`。本地 `DemoFile.Game.Cs` 0.44.1 的 nuspec repository commit 与该提交一致；下面列出的属性也已通过本地 DLL 反射核验，不需要先升级依赖。
- cs-hud：`5595dd02d67f0ca674d96d8c629e067ec6528c1b`。

## 2. 第一步可以使用的接口

| 接口 | 用途 | 约束 |
|---|---|---|
| `GameRules.RoundStartRoundNumber` | 回合开始时的游戏编号 | 三场样本正式 freeze-end 边界为从 0 开始；转换为展示编号前与规则/比分交叉核验 |
| `GameRules.TotalRoundsPlayed` | 已完成回合数 | 回合结束时会更新，不能在所有阶段无条件加一作为当前回合号 |
| `RoundStartCount`、`RoundEndCount` | 识别规则状态变化、辅助事件幂等处理 | 是 byte 类型的变化计数，不是正式回合数，也不应单独作为永久唯一键 |
| `RoundStart`、`RoundFreezeEnd`、`RoundEnd` | 回合尝试、live 起点和结束信号 | 事件需与当前规则状态联合判断；warmup 也能触发 freeze-end |
| `WarmupPeriod`、`HasMatchStarted`、`TeamIntroPeriod`、`CSGamePhase` | 判断比赛阶段 | `CSGamePhase` 单独不足以排除热身，实测 warmup 时也可为 PlayingFirstHalf |
| `OvertimePlaying`、`RoundsPlayedThisPhase` | 加时和半场上下文 | 不硬编码“12 回合后永远是下半场” |
| `RoundEndWinnerTeam`、`RoundEndReason`、`CSRoundResults` | 核验已完成回合的结果 | 只用于标签/审计，不能把未来结果加入历史特征 |
| `BeginNewMatch`、`CsPreRestart`、`GameRestart`、规则/比分回退 | 识别比赛重置的候选证据 | `CsPreRestart` 在正常回合切换也触发，不能单独作为重开判据 |
| `RoundOfficiallyEnded` | 可选的结束辅助信号 | 本次三场均未触发，不应作为生成标签的必要条件 |

源码：

- [规则字段](https://github.com/saul/demofile-net/blob/fd59701a998cf30a46adc4942e063d90de73c07a/src/DemoFile.Game.Cs/Sdk/Schema.cs#L5246)
- [阶段枚举](https://github.com/saul/demofile-net/blob/fd59701a998cf30a46adc4942e063d90de73c07a/src/DemoFile.Game.Cs/Sdk/CSGamePhase.cs)
- [RoundStart 合成路径](https://github.com/saul/demofile-net/blob/fd59701a998cf30a46adc4942e063d90de73c07a/src/DemoFile.Game.Cs/Source1GameEvents.RoundStart.cs)
- [RoundEnd 合成路径](https://github.com/saul/demofile-net/blob/fd59701a998cf30a46adc4942e063d90de73c07a/src/DemoFile.Game.Cs/Source1GameEvents.RoundEnd.cs)
- [事件接口及传统消息处理](https://github.com/saul/demofile-net/blob/fd59701a998cf30a46adc4942e063d90de73c07a/src/DemoFile.Game.Cs/Source1GameEvents.AutoGen.cs)

RoundStart/End 的合成路径监听 `CCSGameRulesProxy` 的字段变化，并排入 `OnCommandFinish`；核心解析器先执行该回调，再执行 `OnCommandFinishPersistent`。项目现有持久回调位置可继续作为归一化状态的快照位置，但它是 command 边界，不应称为完整 tick 的最终状态。抽样和同 tick 多命令更新需要明确规则。

[命令回调顺序](https://github.com/saul/demofile-net/blob/fd59701a998cf30a46adc4942e063d90de73c07a/src/DemoFile/DemoParser.cs)

实施建议：新增 `RoundStateTracker`，独立维护回合尝试 ID 与正式回合号；内部 attempt 序号只做身份，正式编号来自当前游戏规则。保存 start/live/end 及作废原因。正式回合的去重与重开识别采用状态转换和多项证据，不用事件名称计数代替。同一 command 中可能同时出现 RoundStart 与 RoundEnd，应先收集信号，再结合规则快照判断其所属回合尝试，不能按回调顺序直接覆盖当前阶段。

## 3. 第二步可以使用的接口

| 接口 | 用途 |
|---|---|
| `CurrentDemoTick` | Demo 定位、样本身份和回放时间 |
| `CurrentGameTick`、`CurrentGameTime` | 服务端游戏时间；与 Demo 时间不是同一起点 |
| `GameRules.RoundStartTime` | 游戏规则提供的本回合时间起点，需按阶段使用 |
| `RoundTime`、`FreezeTime`、`RestartRoundTime` | 回合时长、冻结和结束后过渡信息 |
| `GamePaused`、`TotalPausedTicks`、`PauseStartTick` | 引擎暂停诊断及后续适配 |
| `TechnicalTimeOut`、`MatchWaitingForResume` | 技术暂停/恢复上下文 |
| `TerroristTimeOutActive`、`CTTimeOutActive` 及 Remaining 字段 | 战术暂停上下文和显示倒计时 |
| `CPlantedC4.C4Blow`、`TimerLength` | 炸弹爆炸截止时间和时长 |
| `CPlantedC4.DefuseCountDown`、`DefuseLength`、`BeingDefused` | 拆包截止时间、时长与状态 |

[时间域定义](https://github.com/saul/demofile-net/blob/fd59701a998cf30a46adc4942e063d90de73c07a/src/DemoFile/DemoParser.cs)、[炸弹字段](https://github.com/saul/demofile-net/blob/fd59701a998cf30a46adc4942e063d90de73c07a/src/DemoFile.Game.Cs/Sdk/Schema.cs#L19189)

正常 live 状态的候选计算是 `CurrentGameTime.Value - RoundStartTime.Value`，剩余时间再以 `RoundTime` 减去该值。只在该回合时间起点已确认、未结束且时钟可信时使用。冻结期间起点可能在未来并被推迟，不能把这个差直接输出为比赛进度。

GameTime 与 RoundStartTime 必须来自同一时间域；不能把 `CurrentDemoTick / 64` 与 RoundStartTime 相减。上游 GameTick 转换在此版本也采用 64 Hz，本轮无需无依据地改 tickrate。

暂停适配必须先验证服务端时间起点是否已经调整。禁止一边使用已调整的 RoundStartTime、一边再无条件扣 TotalPausedTicks。也不能简单把所有 timeout/waiting 标志做 OR 后冻结有效比赛时钟。此次样本不足以确认引擎硬暂停的完整行为，遇到尚未支持的组合应标记时钟未知，而不是伪造正常值。

实施建议：新增 `RoundClockResolver`，输出回放时间、live 时间、回合剩余时间、爆炸剩余时间、拆包剩余时间及 `clockKnown`/来源/暂停上下文。下包后分离回合与 C4 计时；已有 `CaptureBomb` 的双截止时间计算可以复用。

## 4. cs-hud 的复用边界

1. [parse-round.js](https://github.com/drweissbrot/cs-hud/blob/5595dd02d67f0ca674d96d8c629e067ec6528c1b/src/themes/raw/gsi/parse-round.js)：使用 `map.round`、阶段、配置的常规/加时长度计算展示编号；使用 `phase_countdowns.phase_ends_in` 表达当前阶段的倒计时。可参考阶段与计时的输出契约；GSI 的编号公式不能直接套用到 Demo 规则字段。
2. [parse-rounds.js](https://github.com/drweissbrot/cs-hud/blob/5595dd02d67f0ca674d96d8c629e067ec6528c1b/src/themes/raw/gsi/parse-rounds.js)：专门处理 GSI round_wins 在加时重置的情况，可补充加时回归测试。
3. [parse-bomb.js](https://github.com/drweissbrot/cs-hud/blob/5595dd02d67f0ca674d96d8c629e067ec6528c1b/src/themes/raw/gsi/parse-bomb.js)：拆包时区分爆炸与拆包剩余时间。其爆炸倒计时回退依赖服务器墙钟与缓存，属于 HUD 展示补偿，不应作为离线训练真值直接复制。
4. [GSI 配置](https://github.com/drweissbrot/cs-hud/blob/5595dd02d67f0ca674d96d8c629e067ec6528c1b/gamestate_integration_drweissbrot_hud.cfg)：已列出 `map`、`round`、`map_round_wins`、`phase_countdowns`、`bomb` 等订阅，可供后续 GSI 对照采集。它不能从现有 .dem 自动恢复一份历史 GSI 流；本次未启动 CS2 或修改游戏配置。

因此第一、二步的主要实现仍放在现有 C# 解析管线。无需为这两步引入 cs-hud 的 Koa 服务、整套前端或 WebSocket。

## 5. 三场真实 Demo 实测

使用独立临时 C# 探针，直接引用 `apps/api/bin/Release/net10.0` 中已有库，记录事件及 command 完成后的游戏规则快照，不调用训练数据导出器。

| Demo | RoundStart | RoundFreezeEnd | CsPreRestart |
|---|---:|---:|---:|
| 9z-vs-faze-m1-mirage.dem | 35 | 34 | 35 |
| mibr-vs-9z-m2-mirage.dem | 29 | 30 | 29 |
| furia-vs-pain-m1-mirage.dem | 15 | 15 | 15 |

- 79 次 freeze-end（包含 mibr 样本的一次 warmup）中，`CurrentGameTime - RoundStartTime` 均为 0；`RoundStartRoundNumber == TotalRoundsPlayed`，正式回合使用前仍需过滤 warmup。
- 9z 的第一个正式 live 起点：Demo tick 1364（回放 21.3125 秒），游戏时间约 1875.6719 秒，RoundStartTime 同为 1875.6719 秒。两个时间域不能混用。
- 9z 在 tick 62790 出现 `RoundDraw`（reason 10、winner 1）及新 RoundStart，`TotalRoundsPlayed`/`RoundStartRoundNumber` 仍为 7，而 StartCount 从 14 变为 15；随后 tick 64982 进入 live 时 rawRound 仍为 7，tick 73912 的下一回合才为 8。该轨迹解释了旧事件累加器在此场产生 +1 偏移的机制。
- mibr 的 warmup 也触发 freeze-end，此时 RoundTime 为 999、HasMatchStarted 为 false；事件存在不等于正式比赛已经开始。
- 三场都没有触发 RoundOfficiallyEnded，CsPreRestart 则与普通回合开始反复出现，不能使用这两个事件作为单一强判据。
- 9z/mibr 的若干非冻结快照中已经存在战术 timeout 标志。标志置位不自动证明此刻比赛时钟应停止，需结合阶段与时间实际推进判断。
- 三场的已记录快照未出现 GamePaused=true、TechnicalTimeOut=true 或非零 TotalPausedTicks。因此已验证战术等待/冻结相关行为，尚未覆盖引擎硬暂停和技术暂停恢复。

诊断探针及原始输出只保存在核查机器的临时目录，未纳入版本控制；本节是研究核查记录，不是仓库现有自动测试的结果。正式实现时应将相关轨迹提炼成可复现的回归样本与断言。

## 6. 后续实施与 v3 保护

1. 先完成 `RoundStateTracker`、`RoundClockResolver` 及纯状态转换测试，用本次重开/热身/冻结/加时轨迹回归。
2. 增补真实硬暂停/技术暂停样本；验证不足的时钟组合明确输出未知，不启用猜测性扣时公式。
3. 接入新的 v4 数据契约与独立导出路径，再处理完整性语义。切换语义时不要把 v4 特征写进标记为 v3 的数据。
4. 保留 `datasets/mirage-68-local-v3.jsonl` 和整个 `models/win-baseline-v3-holdout-68-5` 目录；迁移前后记录哈希，v4 输出文件已存在时拒绝覆盖。
5. 保留 v3 工件及其特征契约，不把新特征输入旧模型；涉及回放契约变更时单独考虑兼容与版本化，不能混同训练 schema 版本。当前项目尚无在线推理端点，本轮不新增该端点。

本次结论：第一、二步不缺底层解析接口，主要工作是正确组合现有接口、建立回合尝试状态机、区分时间域，并补齐暂停边界测试。cs-hud 提供有价值的展示语义参考，但不是可直接套用的离线回合修复器。
