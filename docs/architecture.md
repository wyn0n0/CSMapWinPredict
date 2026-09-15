# 架构与参考仓库分析

状态更新：2026-09-15。独立 v4.2 语义导出、验收、87 场重训和回放胜率推理均已完成，见 [实现说明](semantic-v4-implementation.md) 和 [87 场训练报告](training-report-v4-87-matches.md)。当前实现可在完整 Demo 导入后生成并显示秒级胜率；真实直播源和严格在线增量推理仍待实施。

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
- `GameRules.RoundStartRoundNumber`、`TotalRoundsPlayed`、`WarmupPeriod`、`HasMatchStarted`：独立 v4 路径确认正式回合编号与阶段的依据。
- `CurrentGameTime`、`GameRules.RoundStartTime`、暂停字段与 `CPlantedC4` 双截止时间：独立 v4 路径修正时钟语义的基础接口。

2026-08-31 已对照源码和本地 DLL 核验：demofile-net 0.44.1 对应 `fd59701a998cf30a46adc4942e063d90de73c07a`；cs-hud 对应 `5595dd02d67f0ca674d96d8c629e067ec6528c1b`。第一、二步不需要先升级依赖。三场诊断结果、事件顺序和暂停限制见 [接口核查](round-clock-upstream-review.md)。

初版不使用 `ReadAllParallelAsync`，因为时间线导出依赖有序、连续的状态快照；并行解析更适合可分区聚合统计。

## 2. 本项目数据流

### 程序与目录边界

| 边界 | 路径 | 职责 |
| --- | --- | --- |
| Web | `apps/web` | Vue 回放界面、窗口缓存和胜率卡片 |
| HTTP 宿主 | `apps/api/Program.cs` | 路由、配置与依赖装配，不承载开发命令 |
| 后端功能 | `apps/api/Features` | Replay、Semantics、Maps、WinPrediction、Situation 五个模块 |
| 开发 CLI | `apps/cli` | 数据导出、诊断、sidecar 补建与验收工作流 |
| .NET 验证 | `tests/CsDemoMap.Api.Tests` | 合成门禁、程序集集成和真实 Demo 前缀检查 |
| Python 工具 | `tools` | 数据审计、模型训练和推理服务 |

后端保持一个可部署 API，不引入微服务。功能目录用于表达依赖方向；现有命名空间暂时保持兼容，避免在纯结构整理中同时修改序列化或运行行为。

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
WinFeatureSampleBuilder -- schema v4.2 时点特征
   |
   v
WinInferenceClient -- 托管 Python 子进程 + 模型契约/工件自检
   |
   v
WinTimelinePredictionService -- live / post-plant 约 1 Hz 胜率点
   |
   v
DemoImportService -- 单工作线程队列 + 30 秒窗口 + Brotli 落盘
   |
   v
manifest + 当前/预取窗口 JSON（含 winPredictions）
   |
   v
Vue playback state -- 最近 3 个窗口缓存
   |
   v
SVG radar + timeline + event feed + 实时胜率卡片
```

前端不知道 demofile-net 类型，后端也不知道 SVG 或地图皮肤。

离线训练使用独立的 v4 链路：

```text
DemoTimeline
  -> WinDatasetV4Exporter + WinFeatureSampleBuilder
  -> schema v4.2 JSONL（live / post-plant 秒级样本）
  -> train_win_baseline_v4.py（固定比赛切分）
  -> baseline.joblib + 模型清单、固化推理样例与验证报告
```

训练和回放推理复用同一个 `WinFeatureSampleBuilder` 特征顺序。API 启动时验证模型清单、依赖版本、工件哈希和固化推理样例；验证失败会保留回放功能，并把预测状态标为不可用。训练标签和页面概率都以当前回合 T 方是否获胜为目标。

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
- `manifest.winPrediction`：推理状态、schema/语义版本、模型、校准、工件哈希、样本数、采样间隔和错误原因。
- `windows[].winPredictions[]`：tick、Demo 秒数、回合尝试 ID、segment、正式回合号、阶段和 T/CT 胜率。

导入完成后 `DemoManifest` 保存全局元数据、事件和总计数；`DemoWindow` 保存一个 30 秒主体窗口及前后 2 秒重叠，并带有 `firstFrameIndex`，因此前端在只持有局部帧时仍能显示全局帧号。

玩家仍保留 Source 2 世界坐标。世界坐标只在渲染前由地图配置转换成 1024×1024 雷达坐标，从而可以替换地图图片或校准参数，而不用重新解析 demo。

## 4. 当前权衡

- 浏览器不再接收整场 JSON；窗口按需加载显著降低网络、反序列化和前端常驻内存。
- 后端当前仍先构建整场 `DemoTimeline` 再分块，解析峰值内存尚未变成真正流式。
- 单工作线程主动限制并发解析，代价是多文件同时上传时会排队。
- 任务状态只在进程内，窗口只在本机磁盘；重启恢复、TTL 清理和多实例共享尚未实现。
- 玩家 8 Hz 对战术移动回放通常足够，道具单独用 16 Hz 并由前端插值。
- 胜率约 1 Hz 采样，前端只在同一回合 segment 和同一阶段的相邻点间插值；跨阶段、冻结、结束或样本过期时显示空状态。
- Python 推理由 API 托管为单个长期子进程，批量请求上限为 512；启动自检失败时不阻断 Demo 解析。
- Simple Radar 当前覆盖 Cache、Dust II、Mirage 和 Nuke；其他地图仍回退到 SVG 示意底图。

## 5. Mirage 历史回放实测

以下为早期回放验证记录，使用 `falcons-vs-vitality-m4-mirage.dem`（690,652,992 bytes）完成浏览器端到端验证；该文件不随仓库分发，也不保证存在于当前本地数据目录：

- 比赛时长 4,069.92 秒，32,523 个玩家帧，662 条投掷物轨迹、368 个持续效果、1,806 次道具库存变化和 6,736 次装备/经济状态变化。
- 识别 30 回合、24 个地图区域、20 次下包、8 次爆炸和 2 次拆除；C4 快照覆盖携带、掉落、下包、已安放、拆除中、已拆除与已爆炸状态。
- 扩充数据后仍生成 136 个窗口，Brotli 文件总计 8,931,240 bytes；浏览器继续只加载当前窗口和少量缓存。
- 在真实页面 100 秒显示“A Site 正在下包”，102 秒切换为“A 区已安放”，145 秒显示“已爆炸”，200 秒进入第 2 回合；经济、装备和区域人数同步变化。

2026-09-07 使用 `furia-vs-pain-m1-mirage.dem` 完成胜率端到端验收：

- 2,124.92 秒回放生成 71 个窗口和 1,282 个胜率点，模型为 `logistic`，语义版本为 `mirage-semantics-v4.2`。
- 页面在 163.125 秒显示 T 28% / CT 72%，在 731.875 秒下包后显示 T 11% / CT 89%，与窗口 API 原始概率取整一致。
- 179.125 秒窗口边界附近继续显示当前概率；4.625 秒冻结阶段不显示旧概率。
- 禁用推理后同一 Demo 仍完成导入，manifest 返回 `unavailable`、0 个预测点和错误原因，页面显示“模型当前不可用”。

## 6. v4.2 已修复语义与剩余风险

v4.2 已用 `RoundStateTracker` 分离回合尝试、正式回合号和作废状态；`RoundClockResolver` 分离 Demo 时间、有效 live 时间以及回合、爆炸和拆除倒计时；阵容质量按已知生死、位置和装备分别计数。旧 v3 数据和模型仍只用于历史复现。

当前仍需处理：

- 真实数据中没有覆盖硬暂停和技术暂停恢复，遇到未验证组合会将时钟标为未知。
- 胜率采样目前从完整 Demo 的已完成回合生成，不是正在进行比赛的增量预测接口。
- 当前模型只在 Mirage 上训练，运行时地图准入还需要独立门禁。
- 前端使用相邻预测点插值，严格按时点展示时需要改成只使用已经发生的样本。
- 导入任务使用进程内状态和无界队列，尚无重启恢复、TTL、磁盘配额和多实例协调。

## 7. 后续演进顺序

1. 分离“当前时点可推理”和“赛后可进入训练集”两套资格，接入增量状态源。
2. 为模型增加地图准入，并让胜率展示只依赖当前及历史样本。
3. 持久化任务与 manifest，增加有界队列、TTL、磁盘配额和推理子进程恢复。
4. 在新增地图几何、训练数据和独立验证后，再扩展到 Mirage 以外地图。

完整字段方案、迁移保护与验收门槛见 [v4 数据语义方案](data-semantics-v4-plan.md)。训练 schema v3/v4 与回放窗口 schema 是不同契约，不能混同版本。

伤害可视化、更多地图、对象存储、桌面打包等扩展，安排在语义修正与预测闭环之后。
