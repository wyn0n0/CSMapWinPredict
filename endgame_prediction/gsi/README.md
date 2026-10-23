# 独立 GOTV GSI 适配层

**工程接通、首场同场比对和拆弹器省略修复已完成，跨比赛准确性仍未验证。** 2026-10-19 [修复复验](../../docs/win-gsi-defusekit-fix-report-20261019.md)通过 39 项测试及 8,675 条原始快照回放；739 对局面的平均胜率差从 12.07 降至 1.72 个百分点。本分支已包含修复，已运行的服务须手动重启后加载新代码。输出继续带 `compatibility: unvalidated-gsi-projection`。没有重训或改写模型，未接入原前端、API、启动脚本或生产默认模型。

接收 CS2 观战客户端的 GSI HTTP 快照，转换为既有残局模型的 114 个输入字段，复用 `endgame_prediction/predict.py` 及其默认已训练模型。仅支持 Mirage、双方各存活 1–5 人、合计 ≤7、live／post-plant。0vN、暂停、冻结、回合结束、未知生命状态和名单不完整时不输出概率。

**`codex/endgame-model` 分支包含默认残局模型包。** 启动前按[残局模块说明](../README.md#直接运行)安装依赖并检查模型，或给 `record.py`／`server.py` 加 `--model-dir` 参数。没有模型时无法启动当前录制器；`--check` 可先确认依赖与模型是否就绪。

## 录制一场完整比赛

独立入口 [`record.py`](record.py) 只在手动启动时录制，默认监听 **127.0.0.1:3002**。原 `server.py` 继续使用 3001、只提供最新预测。两者可以独立运行；只录制时无需另开 `server.py`。使用安装了仓库 `requirements-inference.txt` 依赖的 Python，从仓库根执行下列命令。

1. 在 Steam 的 CS2 管理菜单中打开游戏安装目录，把本目录的 [`gamestate_integration_endgame_recorder.cfg`](gamestate_integration_endgame_recorder.cfg) **复制**到 `game/csgo/cfg/`，保留文件名和 `.cfg` 后缀。不要覆盖旧 `gamestate_integration_endgame.cfg` 或其他程序配置。若 CS2 已打开，复制后重新启动。配置位置及观战字段依据 [CounterStrike2GSI 使用说明](https://github.com/antonpup/CounterStrike2GSI/blob/master/README.md)。
2. 在比赛开始前运行：

   ```powershell
   # 只检查模型，不创建录制目录、不占端口
   python -B endgame_prediction/gsi/record.py --check

   # 前台开始录制；自动创建唯一的新目录，并在终端打印路径
   python -B endgame_prediction/gsi/record.py
   ```

   可给这一场指定名字：`python -B endgame_prediction/gsi/record.py --output-dir endgame_prediction/gsi/recordings/mirage-match-001`。目录必须不存在；每次录制用新名字。端口占用时用 `--port 3003`，并把**录制器配置**中的 URI 改为相同端口，不终止其他程序。鉴权可沿用下文 `ENDGAME_GSI_TOKEN` 的方法，录制器和复制后的录制配置须匹配。
3. 通过 GOTV 进入 **Mirage 完整观战**，在第一回合之前开始接收，连续看到最终赛后画面。若使用已有 Demo 采集，从开头按正常速度连续播放，不 seek、快进或中途换比赛。普通玩家第一视角缺少双方完整数据，虽然可存快照，但通常无法预测。首次使用 Demo/GOTV 路径时，先用下面的计数确认目标客户端确实发送 GSI。
4. 另开 PowerShell 检查录制状态，比赛期间隔一段时间查看一次：

   ```powershell
   Invoke-RestMethod http://127.0.0.1:3002/recording | ConvertTo-Json -Depth 6
   Invoke-RestMethod http://127.0.0.1:3002/prediction | ConvertTo-Json -Depth 8
   ```

   `snapshots` 应持续增加，`results` 应同步增加，`lastSnapshotAgeSeconds` 通常应接近配置的 1 秒 heartbeat。`statusCounts.predicted` 是实际输出过概率的数量；5v5、冻结、暂停、结束等状态也会录制，结果为 `skipped`／`invalid`、概率为 null。`maxGapSeconds` 是相邻接收之间的最大间隔，可排查中断，但不能单独证明漏帧。`/health` 同时返回模型和录制状态；`status: failed` 或 HTTP 503 表示写盘失败，此时停止本次录制、处理磁盘问题并另开新目录。
5. 最后一回合结束后，留在赛后画面至少约 5 秒，确认 `gameoverObserved: true`（客户端发来了 `map.phase: gameover`），再回到录制终端按一次 **Ctrl+C**。它会等待已接收请求写完，再生成 `summary.json`。检查 `cleanStop: true`、`snapshots == results`、计数大于 0，并核对原始记录中的第一回合、最后比分及过程中的接收间隔。若客户端没有发 gameover，保留文件并注明缺少结束标记，不能直接认定完整。BO3 等系列赛建议每张地图单独启动一次录制；目前只有 Mirage 能预测。

`cleanStop` 仅说明程序正常结束且已接收记录成对写完；`gameoverObserved` 仅说明见过赛后标记。**二者都不证明从第一回合开始完整、没有断流。** 断线后程序会继续保存重连数据，但不会补回断线期间的快照。不要关闭终端、强杀进程或让电脑休眠来停止录制。

## 录制文件与恢复

默认输出到本目录下 Git 忽略的 `recordings/<UTC时间-随机标识>/`，每场一个目录，无新增第三方依赖：

| 文件 | 内容 |
| --- | --- |
| `manifest.json` | 录制契约 `gotv-gsi-recording-v1`、会话 ID、开始时间、模型身份／权重 SHA-256、运行依赖版本、适配器等入口源码 SHA-256、时效配置 |
| `snapshots.jsonl` | 每行一个收到的原始 GSI 对象，放在 `payload`；保存非残局、未知地图／字段、`previously`／`added`、比分和胜方等原始证据 |
| `predictions.jsonl` | 每行一个处理结果，放在 `result`；包括状态、概率或 null、跳过／失败原因，以及适配器返回的缺失提示和耗时 |
| `summary.json` | 停止时间、正常结束标志、快照／结果计数、状态计数、首末接收时间、最大接收间隔、是否见过 gameover |

两份 JSONL 用 `sequence` 从 1 开始逐条关联，均带 `receivedAt`（UTC）、`receivedUnixSeconds` 和 `receivedElapsedSeconds`（从进程录制开始计算的单调时间）。时间取自 **HTTP 请求体读完时、JSON 解析和推理前**；不等同 CS2 服务器时间。源 `provider.timestamp` 保留在原始对象中。`sequence` 表示处理顺序；重复快照／重试不去重。`processingMs` 沿用原服务，只计转换与推理，不包含写盘时间。

这里的“原始”指收到的 JSON 字段和值，**不保留 HTTP 字节排版**；唯一主动移除的是顶层、`previously` 和 `added` 中的 `auth`，避免保存令牌。其余游戏数据（包括玩家身份）保留在本地。语法错误、重复 JSON 键、非有限数字、超限请求、错误路径和鉴权失败不会录制；合法 JSON 即使不适合预测也照常保存。胜方等字段只用于原始证据，不传入模型特征。

先写快照并 `flush`／`fsync`，再计算预测、写结果并同步文件，最后才返回 HTTP 200。推理意外失败会留下 `prediction_failed` 结果，后续快照仍可录制；写盘失败则锁定失败状态、清除概率并返回 503，避免继续声称成功。每条同步写盘会增加接收延迟，实际采样率以接收时间为准；0.1 秒 throttle 不是固定 10 Hz 保证。

突然断电或强制结束可能没有 `summary.json`，也可能只写完快照、没有对应预测，或最后一行不完整。保留原目录，读取换行结束且可解析的 JSONL 行，再按 `sequence` 配对；缺少预测须标注缺失，不能把上一条概率补到新快照。后续录制必须用新目录，不自动追加／覆盖旧数据。自定义输出位置也应保持本地并设置忽略规则；记录目录不自动上传。用于 GSI／Demo 对齐时，另行保留对应 Demo 和赛事／地图信息。

## 原有服务启动（不录制）

使用已安装仓库 `requirements-inference.txt` 依赖的 Python，无新增第三方依赖。从仓库根执行：

```powershell
# 校验模型与适配器，不启动 HTTP 服务
python -B endgame_prediction/gsi/server.py --check

# 前台运行，Ctrl+C 停止；一个进程接一个观战客户端／比赛
python -B endgame_prediction/gsi/server.py
```

默认只监听 `127.0.0.1:3001`。端口占用会报错，不终止其他进程。可用 `--port 3002` 改端口，同时修改配置 URI；`--max-age 5` 控制允许的数据年龄；`--model-dir` 可指定另一个经过原加载器校验的 B_c4 模型包。

1. 将本目录的 `gamestate_integration_endgame.cfg` **复制**到 CS2 安装目录下的 `game/csgo/cfg/`。本实现不自动修改游戏安装目录，不替换其他 GSI 配置。
2. 启动接收服务，再启动／重新启动 CS2，通过 GOTV 进入完整观战模式。需要 `allplayers_*`、`bomb`、`phase_countdowns` 等观战字段，普通玩家视角不足以构造完整双方局面。
3. 从回合开始保持接收。在另一个 PowerShell 窗口查看：

```powershell
Invoke-RestMethod http://127.0.0.1:3001/health
Invoke-RestMethod http://127.0.0.1:3001/prediction | ConvertTo-Json -Depth 8
```

模板设置 0.1 秒 buffer／throttle、1 秒 heartbeat；这是配置值，不是实测采样保证。模型常驻内存，每个已接受快照同步计算一次，客户端读取最新结果。`server.py` 不外推时钟、不保存历史；保存请使用上面的 `record.py`。请求限 1 MiB、JSON 对象、读取超时 3 秒，没有 CORS。

如需区分本机发送方，启动前设置 `$env:ENDGAME_GSI_TOKEN = '自行选择的令牌'`，并在复制后的配置中启用相同的 `auth.token`。不要提交实际令牌。鉴权作用于 POST；本机 GET 不含玩家身份、原始数据和令牌。浏览器带 Origin 的 POST 被拒绝。

## 接口

| 接口 | 用途 |
| --- | --- |
| `POST /gsi` | 接收完整 GSI JSON 快照，返回 200 和处理结果；不把 `previously`／`added` 当作完整状态合并 |
| `GET /prediction` | 最新结果与年龄；数据过期返回 `prediction: null` |
| `GET /health` | 模型已加载的信息及 GSI 状态；健康不代表已收到有效观战数据 |
| `GET /recording` | 仅录制器提供：录制计数、接收年龄及失败状态；普通服务返回 404 |

`status` 为 `waiting`、`predicted`、`skipped`、`invalid` 或 `stale`。成功时 `prediction` 包含 0–1 的 `tWin`、`ctWin`；其他状态为 null，调用方必须清除旧概率。`reason` 说明拒绝原因，`warnings`／`missingFeatures` 说明投影限制，后者也包含不适用的空字段。`processingMs` 只计本机转换及推理，不含 GOTV 延迟。

同时检查接收后的单调时钟年龄与接收时的 `provider.timestamp` 年龄，默认超过 5 秒清除概率；重复发送旧快照不会延长有效期。客户端与接收服务应在同一台电脑运行。`sourceTimestamp` 不能当作 Demo tick，也不代表服务器无延迟局面。

Python 直接调用：

```python
from endgame_prediction.predict import EndgamePredictor
from endgame_prediction.gsi.adapter import GsiAdapter

model = EndgamePredictor()
adapter = GsiAdapter(model)
result = adapter.adapt(payload)  # 当前 GSI JSON 对象
if result["status"] == "ready":
    probabilities = model.predict([result["sample"]])[0]
```

直接调用时，自行串行处理、捕获异常并检查过期；HTTP 层已负责这些工作。

## 特征语义

- **基础统计只汇总存活玩家。** 实际训练导出的 `WinFeatureSampleBuilder` 在调用底层汇总器前筛选 Alive，金钱、装备、血量及比赛累计击杀／死亡都遵循该口径。10 人名单用于质量检查；`round_kills` 不替代累计击杀。
- 当前快照要求双方各 5 名成员。缺失血量不当作死亡；只有同一连续回合曾明确观察到 `health: 0` 时，才允许该阵亡成员后续省略血量。中途接入无法确认生命状态会拒绝预测。普通活人字段不沿用上一帧。
- 武器按原解析器的名称分类，道具数量用 `ammo_reserve`（一个闪光弹槽可能有两枚）。T 方无法持有拆弹器，缺失 `defusekit` 可判 false；CT 缺失则未知。任一存活者装备不完整，按原 v4 掩码将双方装备汇总和相关差值置空，不填零。
- 基础距离／离散度使用 Mirage 的 `5 × 1024` 归一化比例；C4 最近／次近距离使用世界坐标 XY。包点坐标读自已校验的模型清单，绑定原空间配置哈希。单人生存时次近距离为 null。任一存活者坐标缺失，基础空间特征整体置空；C4 按每队完整性分别处理。
- `live` 阶段倒计时对应回合剩余时间，`bomb` 对应爆炸时间，`defuse` 对应拆弹时间；炸弹 `countdown` 按 planted／defusing 状态区分用途。拆弹时没有可用爆炸倒计时就保留 null。`liveElapsedSeconds`／暂停上下文未知；`clockSource` 明确为 `gsi-phase-countdown`，不伪装 Demo 时钟。
- 已观察过的 dropped／planting／planted／defusing 历史标为 true；未观察到不证明未发生，保留 null。完整变化次数、上次状态和距上次变化时间始终为 null。区域／炸弹包点名称也为 null，不用最近包点冒充实际安放点。
- 同一连续回合 planted／defusing 之间可复用已安放 C4 的静止位置，携包阶段不沿用。换回合、换阵营名单、来源变化、明显回跳／复活、中断、暂停及无效局面会清除缓存。不能检测所有无事件回放跳转；手动 seek 或切换同源同比分回放时重启接收服务。
- `map.round` 按从 0 开始的当前回合索引处理，须等于双方比分之和，再转为训练的从 1 开始编号；不符返回 `round_unconfirmed`。
- 玩家名、赢家、`win_team`、未来标签及 `previously` 不进入特征。外层来源契约为 `gotv-gsi-endgame-v1`，内部仅选择模型声明的 114 个字段，沿用 `win-broad-endgame-v1` 加载器及权重。

未知值由原模型预处理处理，**输出概率不等于这些缺失模式已与训练分布兼容**。后续需对同一比赛的 GSI／Demo 对齐样本核验字段和概率校准；必要时另建 GSI 特征版本并重训，不修改原权重，不用已观察测试集反复调参。

实现依据：[训练导出](../../apps/api/Features/WinPrediction/WinFeatureSampleBuilder.cs)、[基础统计](../../apps/api/Features/WinPrediction/AsOfTickFeatureBuilder.cs)、[C4 特征](../../apps/api/Features/WinPrediction/WinSpatialFeatureBuilder.cs)、[地图几何](../../apps/api/Features/Maps/MapFeatureGeometry.cs)。协议参考 [CounterStrike2GSI 源码](https://github.com/antonpup/CounterStrike2GSI/tree/master/CounterStrike2GSI/Nodes) 和 [go-cs2-gsi](https://github.com/Nescabir/go-cs2-gsi)，实包仍以目标 CS2 客户端为准。

## 验证

```powershell
python -B -m unittest endgame_prediction.gsi.test_record endgame_prediction.gsi.test_gsi endgame_prediction.test_predict tools.test_win_inference_service
```

录制器新增后，2026-10-17 共 **36 项测试通过**（含 8 项录制器测试）。新增覆盖全阶段保存与时间／序号配对、鉴权字段移除、不覆盖旧目录、推理故障恢复、并发及重复快照、磁盘失败返回 503、异常结束保留原始数据、等待时间计入过期判断，以及退出时等待请求完成。原适配器、独立模型及生产推理回归同时通过；尚未进行真实比赛录制与持续负载验收。

2026-10-17：28 项测试通过，其中 16 项为 GSI 测试。覆盖人数边界、存活者统计、距离单位、缺失掩码、道具数量、生命状态、时钟、历史重置、来源时效、未来字段隔离、HTTP 鉴权／限制／恢复和真实模型推理。HTTP 测试使用本机系统分配的临时端口，结束后关闭。

额外完成合成快照的 1,498 次字段类型异常替换，无未处理异常，有效概率均为有限的 0–1 值。本机合成 3v4 局面预热后 20 次转换＋推理中位约 22 ms，最大约 26 ms；不是比赛准确率或持续负载测试。尚未录制真实 GOTV 数据、修改游戏安装目录或重新训练。
