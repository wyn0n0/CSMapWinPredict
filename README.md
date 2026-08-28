# CS Demo Map

读取 Counter-Strike 2 `.dem` 文件，并在浏览器小地图中回放玩家位置、朝向、生命状态与关键比赛事件的初始框架。

## 当前能力

- 使用 `DemoFile.Game.Cs` 解析 CS2 CSTV/GOTV 与 POV demo。
- 默认每 8 tick 抽样一次（8 FPS），避免把完整 64 tick 实体状态直接塞进浏览器。
- 提取玩家坐标、速度、朝向、生命值、队伍、地图区域、当前武器和击杀/死亡数。
- 以 16 Hz 记录烟雾、闪光、手雷、燃烧弹和诱饵弹的飞行轨迹，并在前端插值播放。
- 按真实实体生命周期显示烟雾持续范围和燃烧区域。
- 仅在状态变化时记录每名玩家携带的全部投掷物，避免逐帧重复库存数据。
- 记录玩家金钱、护甲、头盔、拆弹器、装备价值、本回合花费，以及全部武器、弹药和道具。
- 每个玩家帧附带回合阶段、比分、回合计时、双方连败数，以及地图区域人数分布。
- 连续记录 C4 携带者、掉落/下包/已安放/拆除/爆炸状态、包点、坐标和倒计时。
- 提取回合开始/进入 live/结束、击杀、下包、拆包与爆炸事件。
- 结构化保存每回合开始、live、结束 tick、胜方阵营与结束原因。
- Vue + SVG 雷达支持播放、暂停、倍速、拖动时间线、玩家名字、移动轨迹和独立道具图层开关。
- 内置一段合成 Mirage 数据，无需 demo 和后端即可体验 UI。
- 地图坐标转换与雷达图层解耦；已接入 `cs-hud` 内含的 5 张 Simple Radar WebP。
- 大文件上传后进入单工作线程后台队列，解析结果按 30 秒窗口落盘并使用 Brotli 压缩。
- 浏览器只加载当前窗口、缓存最近 3 个窗口，并在播放到边界前预取下一窗口。

## 项目结构

```text
apps/
  api/                 ASP.NET Core API 与 demofile-net 解析适配层
  web/                 Vue 3 / TypeScript / Vite 雷达播放器
docs/
  architecture.md      两个参考仓库的分析、决策与演进路线
```

## 环境要求

- Node.js 20+
- .NET SDK 10.0+（注意：仅安装 .NET Runtime 不能编译后端）

## 启动

```bash
npm install
dotnet restore apps/api/CsDemoMap.Api.csproj
npm run dev
```

打开 `http://localhost:5173`。前端会把 `/api` 代理到 `http://localhost:5088`。

如果暂时没有 .NET SDK，可以只启动内置示例：

```bash
npm install
npm run dev:web
```

## 导入 demo

页面右上角选择 `.dem` 文件后，浏览器会依次调用：

```http
POST /api/demos/import
Content-Type: multipart/form-data
file=<demo file>

GET /api/demos/{id}/status
GET /api/demos/{id}/windows/{index}
```

上传完成后 API 返回 `202 Accepted`，页面会显示排队、解析、生成窗口和载入首屏等阶段。解析完成前仍可保留在示例数据界面；完成后只解压当前时间附近的数据，不再一次传输并反序列化整场 JSON。窗口带有前后 2 秒重叠，跨边界时玩家轨迹、投掷物、持续效果、经济和装备状态不会断层。

当前任务队列和 manifest 保存在进程内，服务重启后不能继续旧任务；生成的窗口也尚未按 TTL 自动清理。后端解析阶段仍会在内存中构建整场 `DemoTimeline`，这些是下一轮持久化与流式解析需要处理的边界。

当前单个上传文件上限为 1 GiB。也可以跳过浏览器上传，直接在命令行解析并输出摘要：

```powershell
dotnet run --project apps/api/CsDemoMap.Api.csproj --no-restore -- --inspect-demo "D:\path\match.dem"
```

## 导出胜率训练数据

可以对单个 `.dem` 或包含 demo 的目录递归导出 JSONL：

```powershell
dotnet run --project apps/api/CsDemoMap.Api.csproj --no-restore -c Release -- `
  --export-win-data "D:\demos" "D:\datasets\cs2-win-v3.jsonl"
```

导出器仅采样正式回合的 live 与 post-plant 阶段，每秒生成一条记录。标签是 T 方是否赢得当前回合；输入只使用当前 tick 可见的玩家、回合、C4、区域和最近装备状态，不输出玩家姓名、Steam ID 或 C4 携带者 ID。每回合所有记录的 `sampleWeight` 之和为 1，避免长回合在训练时获得更高权重。

Schema v3 在因果 C4 状态变化、归一化队伍站位分散度、双方最近距离和到 A/B 包点的平均/最短距离基础上，新增比分/连败差、C4 计时器可用性、队伍空间缺失标记、最接近包点距离、包点接近度差、金钱/护甲/投掷物/主副狙与拆包器差、总存活人数、平均存活血量、残局标记和装备解析覆盖率。资源与队伍差均按 `T - CT` 计算；包点接近度差按 `CT 距离 - T 距离` 计算，正值表示 T 方更接近该包点。距离以 1024 像素雷达宽度归一化。当前包点几何覆盖 Mirage；其他地图的空间聚合值为 `null`，需要增加对应的版本化地图配置后再用于跨地图训练。
输出文件已存在时命令会直接报错，不会覆盖。CLI 逐场解析并逐行写出，但为生成稳定 `matchId` 会先读取一次 demo 计算 SHA-256。


## 地图资源

`apps/web/public/radars/simpleradar` 直接复制了 `cs-hud` 的 Simple Radar 子目录，包含 Cache、Dust II、Mirage、Nuke 上层和 Nuke 下层。对应的 `pos_x`、`pos_y`、`scale` 也来自其 `radars.json`。其他地图仍使用抽象 SVG 回退图。

这些图片由 `cs-hud` 标注为 TL;DR 的 CS:GO 版 Simple Radar，并非 `cs-hud` 作者原创。来源、文件哈希和再分发提示见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 验证

```bash
npm run typecheck
npm test
npm run build
dotnet build apps/api/CsDemoMap.Api.csproj
```

## 参考与许可证

- [drweissbrot/cs-hud](https://github.com/drweissbrot/cs-hud) — ISC；参考了服务端状态与 Web 雷达分层思路，没有复制其素材或源码。
- [saul/demofile-net](https://github.com/saul/demofile-net) — MIT；通过 NuGet 包 `DemoFile.Game.Cs` 使用。
