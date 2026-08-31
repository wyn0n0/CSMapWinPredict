# 架构与参考仓库分析

状态更新：2026-08-31。当前实现为 Demo 回放与离线回合胜率训练；下文明确区分已有能力与待实施方案。最新模型结果见 [68 场验证摘要](training-report-68-matches.md)。

## 1. 参考仓库结论

### cs-hud

`cs-hud` 是一个面向观战/直播的完整 HUD。它的核心链路是：CS Game State Integration 向 Koa 路由推送状态，服务端缓存并通过 `ws` 广播，Vue 雷达消费统一状态。仓库还包含设置页、主题、OBS/透明叠加层和雷达资源。

适合复用的思想：

- 输入、状态、传输和渲染分层。
- 浏览器端只消费稳定状态，不理解上游协议。
- 雷达作为独立页面/组件，地图配置和比赛状态分别管理。
- WebSocket 可用于后续的解析进度、直播或远端观战。

不直接复用的部分：

- GSI 是游戏运行时快照，不是 `.dem` 的逐 tick 数据源。
- 其 Koa 服务端无法直接使用 C# 的 demofile-net。
- Simple Radar 等图片有独立来源，不能因为主仓库是 ISC 就默认归入相同许可证；按用户要求拷贝后，项目在 `THIRD_PARTY_NOTICES.md` 中单独标注来源与再分发风险。

### demofile-net

`demofile-net` 是 Source 2 的 C# 解析库。CS2 支持由 `DemoFile.Game.Cs` 包提供；强类型实体能直接读取玩家 pawn 的 `Origin`、`EyeAngles`、生命状态和武器，强类型事件能读取击杀、回合与炸弹事件。它也支持 seek、HTTP broadcast 和并行解析。

适合本项目的接口：

- `CsDemoParser`：CS2 游戏解析器。
- `DemoFileReader.Create(...).ReadAllAsync()`：顺序解析。
- `PacketEvents.SvcServerInfo`：地图名和 tick interval。
- `DemoEvents.DemoFileInfo`：总 tick/时长元数据。
- `Players -> PlayerPawn`：玩家状态与世界坐标。
- `Source1GameEvents`：回合、击杀和炸弹事件。
- `GameRules.RoundStartRoundNumber`、`TotalRoundsPlayed`、`WarmupPeriod`、`HasMatchStarted`：下一步修正正式回合编号与阶段的候选依据。
- `CurrentGameTime`、`GameRules.RoundStartTime`、暂停字段与 `CPlantedC4` 双截止时间：下一步修正时钟语义的基础接口。

2026-08-31 已对照源码和本地 DLL 核验：demofile-net 0.44.1 对应 `fd59701a998cf30a46adc4942e063d90de73c07a`；cs-hud 对应 `5595dd02d67f0ca674d96d8c629e067ec6528c1b`。第一、二步不需要先升级依赖。三场诊断结果、事件顺序和暂停限制见 [接口核查](round-clock-upstream-review.md)。

初版不使用 `ReadAllParallelAsync`，因为时间线导出依赖有序、连续的状态快照；并行解析更适合可分区聚合统计。

## 2. 本项目数据流

```text
.dem 文件
   |
   v
DemoFile.Game.Cs
   |
   v
DemoParserService -- 玩家/回合/C4 8 Hz + 道具 16 Hz 抽样 + 事件归一化
   |
   v
DemoImportService -- 单工作线程队列 + 30 秒窗口 + Brotli 落盘
   |
   v
manifest + 当前/预取窗口 JSON
   |
   v
Vue playback state -- 最近 3 个窗口缓存
   |
   v
SVG radar + timeline + event feed
```

前端不知道 demofile-net 类型，后端也不知道 SVG 或地图皮肤。

离线训练使用另一条已经实现的链路：

```text
DemoTimeline
  -> WinDatasetExporter + AsOfTickFeatureBuilder
  -> schema v3 JSONL（live / post-plant 秒级样本）
  -> train_win_baseline.py（按比赛切分）
  -> baseline.joblib + 模型对比与验证报告
```

两条链路目前尚未通过推理服务连接：API 没有模型加载或胜率预测端点，前端没有胜率曲线。训练标签只表示当前回合 T 方是否获胜。

## 3. 时间线契约

- `metadata`：文件名、地图、tick rate、抽样率和时长。
- `frames[]`：tick、秒数、玩家坐标/速度/区域，以及该时刻的回合、C4 与区域人数快照。
- `frames[].round`：回合号、阶段、比分、已用/剩余时间和双方连续失利数。
- `frames[].bomb`：C4 状态、携带者/拆除者、包点、区域、坐标及爆炸/拆除倒计时。
- `frames[].zones[]`：按游戏内 `LastPlaceName` 聚合的 T/CT 存活及总人数。
- `utilityTracks[]`：投掷物生命周期、投掷者与 16 Hz 飞行点。
- `utilityEffects[]`：烟雾范围与 inferno 实际燃烧点。
- `playerUtilityStates[]`：仅在变化时记录的玩家完整道具库存。
- `playerEquipmentStates[]`：仅在变化时记录金钱、护甲、头盔、拆弹器、装备价值、本回合花费，以及带弹药量的完整装备。
- `events[]`：tick、秒数、事件类型、标题与描述。

导入完成后 `DemoManifest` 保存全局元数据、事件和总计数；`DemoWindow` 保存一个 30 秒主体窗口及前后 2 秒重叠，并带有 `firstFrameIndex`，因此前端在只持有局部帧时仍能显示全局帧号。

玩家仍保留 Source 2 世界坐标。世界坐标只在渲染前由地图配置转换成 1024×1024 雷达坐标，从而可以替换地图图片或校准参数，而不用重新解析 demo。

## 4. 当前权衡

- 浏览器不再接收整场 JSON；窗口按需加载显著降低网络、反序列化和前端常驻内存。
- 后端当前仍先构建整场 `DemoTimeline` 再分块，解析峰值内存尚未变成真正流式。
- 单工作线程主动限制并发解析，代价是多文件同时上传时会排队。
- 任务状态只在进程内，窗口只在本机磁盘；重启恢复、TTL 清理和多实例共享尚未实现。
- 玩家 8 Hz 对战术移动回放通常足够，道具单独用 16 Hz 并由前端插值。
- Simple Radar 当前覆盖 Cache、Dust II、Mirage 和 Nuke；其他地图仍回退到 SVG 示意底图。

## 5. Mirage 历史回放实测

以下为早期回放验证记录，使用 `falcons-vs-vitality-m4-mirage.dem`（690,652,992 bytes）完成浏览器端到端验证；该文件不随仓库分发，也不保证存在于当前本地数据目录：

- 比赛时长 4,069.92 秒，32,523 个玩家帧，662 条投掷物轨迹、368 个持续效果、1,806 次道具库存变化和 6,736 次装备/经济状态变化。
- 识别 30 回合、24 个地图区域、20 次下包、8 次爆炸和 2 次拆除；C4 快照覆盖携带、掉落、下包、已安放、拆除中、已拆除与已爆炸状态。
- 扩充数据后仍生成 136 个窗口，Brotli 文件总计 8,931,240 bytes；浏览器继续只加载当前窗口和少量缓存。
- 在真实页面 100 秒显示“A Site 正在下包”，102 秒切换为“A 区已安放”，145 秒显示“已爆炸”，200 秒进入第 2 回合；经济、装备和区域人数同步变化。

## 6. 已确认的语义风险

- 事件累计编号与游戏正式回合号没有分离；额外 RoundStart 可能造成漂移。
- 当前 `elapsedSeconds` 从 RoundStart 开始，包含冻结等待，不能直接解释为有效交战时间。
- 当前玩家列表依赖已连接玩家及 Pawn；少于 10 个实体的记录需结合阵容与死亡状态判断，不能直接视为死亡或坏样本。
- 装备覆盖率以已经出现在快照中的玩家为分母，不代表阵容完整性。
- 现有训练校验检查 schema 版本、重复键、标签和权重，但并非完整的比赛语义校验。

## 7. 下一阶段：v4 迁移方案（尚未实施）

1. 新增 `RoundStateTracker`，分离回合尝试 ID、正式回合号与作废状态；结合规则快照处理同一 command 的多个事件。
2. 新增 `RoundClockResolver`，分离 Demo 时间、有效 live 时间、回合／爆炸／拆包倒计时；补充真实暂停样本，未知情况明确标记。
3. 建立阵容与字段质量契约，再重新解析原始 Demo 导出独立 v4 数据；不在 v3 JSONL 上直接替换字段。
4. 保留现有 v3 数据和模型；沿用原 63/5 比赛切分，在新旧共有样本及完整 v4 验证集上分别比较。
5. 数据语义通过验证后，再接入回放中的胜率推理与曲线，随后接真实直播源、增量状态、延迟与断线恢复。
6. 持久化任务与 manifest、增加有界队列和 TTL 清理，并逐步实现增量解析和真实 Demo 集成测试。

完整字段方案、迁移保护与验收门槛见 [v4 数据语义方案](data-semantics-v4-plan.md)。训练 schema v3/v4 与回放窗口 schema 是不同契约，不能混同版本。

伤害可视化、更多地图、对象存储、桌面打包等扩展，安排在语义修正与预测闭环之后。
