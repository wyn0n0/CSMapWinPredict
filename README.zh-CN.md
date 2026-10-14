# CS Demo Map

[English](README.md) | 简体中文

CS Demo Map 用于导入 Counter-Strike 2 .dem 文件，在交互式雷达中回放比赛，并显示当前回合 T/CT 的实时胜率。预测目标是当前回合胜方，不是整张地图或整场系列赛的胜负。

![Mirage 回放界面](docs/assets/demo-replay-dashboard.png)

## 已实现能力

- 通过 DemoFile.Game.Cs 解析 CS2 CSTV/GOTV 与 POV Demo。
- 以 8 Hz 记录玩家位置、速度、朝向、血量、武器、经济、装备、道具库存、回合状态、C4、比分、连败数和区域人数。
- 以 16 Hz 记录投掷物轨迹，并按真实生命周期显示烟雾和燃烧区域。
- 基于 Vue 3 与 SVG 的回放界面，支持播放、拖动、倍速、玩家名称、轨迹、投掷物、效果和库存图层。
- 以带两秒重叠的 30 秒 Brotli 压缩窗口传输时间线；浏览器只加载当前窗口、维持小型缓存并预取下一窗口。
- 使用 schema v4 / `mirage-semantics-v4.2` Mirage 模型，在 live 与下包后阶段约每秒生成一次回合胜率。
- 页面胜率卡片保持最近已观测样本并显示采样时间，不向未来样本插值；拒绝当前回合边界之前的样本，冻结、结束、跨阶段和过期数据都会清空旧胜率。

仓库还包含合成 Mirage 时间线，因此无需 Demo 文件或 API 也能体验前端界面。

## 当前状态（2026-10-14）

- **回放与胜率**：Mirage 回放及当前回合胜率已接入界面；默认模型配置仍指向 `win-baseline-v4-holdout-87-8-20260908`，采用逻辑回归＋Sigmoid 校准。本次没有训练或替换模型。
- **胜率检验**：2026-10-14 的既有模型检验覆盖 13 场、271 回合、24,489 个时点，按回合加权的 Log Loss 为 0.4836、Brier 为 0.1626、准确率为 74.3%。这是已有本地报告的结果，不是本次文档更新重新运行的结果；不能解释为开局预测准确率。相对人数／装备小模型的收益尚未得到稳定确认。这 13 场现已观察，不能再次用作新候选的最终盲测。
- **空间特征实验**：新增独立 `win-spatial-v1` 导出与审计，覆盖 C4、携包人、包点邻近和队形距离；已完成 3 场、7,309 点的工程验证。尚未接入线上胜率模型，也尚无模型收益结论；直线距离不代表导航或实际支援能力。
- **局势解说**：Scene/Facts、核心输入、协议和预算工具处于离线研发阶段；完整序列预算 5120，输入最多 4608，输出预留 512。当前重构源码需要重新建立来源与预算证据；生成、训练及产品 HTTP API／界面尚未完成准入或接入。

源码、测试、必要配置和当前使用说明属于发布范围；历史执行记录、旧阶段报告、归档草稿及本地训练工件不随本次更新推送。GitHub 中已存在的历史文件不在本次清除范围。

## 架构

~~~text
.dem
  -> DemoFile.Game.Cs
  -> DemoParserService
  -> WinFeatureSampleBuilder
  -> WinInferenceClient（由 API 托管的 Python 进程）
  -> WinTimelinePredictionService
  -> DemoImportService（Brotli 窗口文件）
  -> Vue 回放界面与实时胜率卡片
~~~

API 管理一个长期运行的 Python 推理子进程。启动时会校验模型清单、工件哈希、依赖契约和固定推理样例。模型无法加载时，Demo 回放仍可使用，页面会明确显示预测不可用。

仓库按职责组织：

- `apps/api/Program.cs` 只包含 HTTP 宿主和依赖装配。
- `apps/api/Features/` 按 Replay、Semantics、Maps、WinPrediction 和 Situation 划分后端代码。
- `apps/cli/` 保存数据导出、诊断和验收工作流。
- `tests/CsDemoMap.Api.Tests/` 保存 .NET 验证门禁和真实 Demo 前缀检查。
- `apps/web/src/composables/useReplay.ts` 管理导入、回放与窗口缓存。
- `tools/` 保存 Python 模型训练、审计、推理及局势解说研发工具。
- `tools/run-tests.mjs` 统一调度测试，每次调用只构建一次 .NET 验证程序。

## 环境要求

- Node.js 20.19+（20.x）或 22.12+
- .NET SDK 10.0+
- Python 3 与 pip，用于模型推理
- 需要真实预测时，准备本地 schema v4 / `mirage-semantics-v4.2` 模型包

默认模型目录为 models/win-baseline-v4-holdout-87-8-20260908。模型包、数据集和 Demo 文件均被 Git 忽略。

## 快速启动

~~~bash
npm install
dotnet restore apps/api/CsDemoMap.Api.csproj
dotnet restore apps/cli/CsDemoMap.Cli.csproj
dotnet restore tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj
python -m pip install -r requirements-inference.txt
npm run dev
~~~

打开 http://localhost:5173。前端会将 API 请求代理到 http://localhost:5088。npm run dev 会同时启动 API、前端和由 API 管理的 Python 推理进程。

若只想查看合成回放：

~~~bash
npm install
npm run dev:web
~~~

### 推理配置

启动前可以通过环境变量覆盖默认配置：

~~~powershell
$env:WinInference__PythonExecutable = "python"
$env:WinInference__ModelDirectory = "models/win-baseline-v4-holdout-87-8-20260908"
npm run dev
~~~

使用以下接口检查模型状态：

~~~http
GET /api/win-model/status
~~~

模型就绪时，响应会提供 schema、语义版本、模型、校准方式、工件哈希和 Python 运行时。模型缺失、不兼容或被禁用时，接口会返回 unavailable 或 disabled 与错误原因；Demo 仍可继续导入和回放。

## 导入 Demo

在网页右上角选择 .dem 文件：

~~~http
POST /api/demos/import
Content-Type: multipart/form-data
file=<demo file>

GET /api/demos/{id}/status
GET /api/demos/{id}/windows/{index}
~~~

界面会显示上传、排队、解析、计算胜率、生成窗口和载入首屏等状态。每次完成的导入包含：

- manifest.winPrediction：模型状态、schema/语义版本、模型、校准、工件哈希、样本数、采样间隔，以及不可用时的错误。
- windows[].winPredictions[]：tick、Demo 时间、回合身份、segment、正式回合号、阶段、T 方概率和 CT 方概率。

页面只在 live 与下包后阶段显示胜率，保持播放时间之前最近一次采样的值与原始时间戳；超过 1.5 秒、早于当前回合边界或阶段不匹配的样本不会显示。冻结和结束阶段会清空卡片。

### 离线导入

API 可直接导入 data/mirage 目录中已有的文件，避免再次上传数百 MB 的 Demo：

~~~http
GET /api/demos/offline

POST /api/demos/offline/import
Content-Type: application/json

{"fileName":"furia-vs-pain-m1-mirage.dem"}
~~~

离线接口只接受顶层 .dem 文件名。使用其他目录时：

~~~powershell
$env:OfflineDemos__RootPath = "D:\other\demo-directory"
dotnet run --project apps/api/CsDemoMap.Api.csproj
~~~

## 数据与模型流程

v4.2 流程仅使用 live 与下包后阶段、当前 tick 已知的因果特征。特征不包含玩家身份、未来事件、回合最终状态或目标标签。

训练前安装固定依赖：

~~~bash
python -m pip install -r requirements-train.txt
~~~

以下为参数模板，先替换 `<...>`。导出、审计和切分必须对应同一批 schema v4 数据；训练仅在清单、审计和比赛级切分准备完成后执行。

~~~text
npm run cli -- --export-win-data-v4 <demo-directory> <new-dataset-directory>
python tools/audit_win_v4.py --v3 <matching-v3.jsonl> --v4-dir <new-dataset-directory> --output <new-audit.json>
python tools/train_win_baseline_v4.py --input <samples.jsonl> --manifest <manifest.json> --comparison <audit.json> --validation-split <match-split.json> --output-dir <new-model-directory> --threads 4 --folds 5 --seed 42
~~~

每次导出和训练使用不存在的新输出目录。v3 仅用于历史复现或上述审计对照，不能作为 v4 训练输入或与 v4 模型包混用。模型、数据、切分与审计文件不随仓库下载提供。

### 实验工具

~~~text
python tools/win_experiment.py --help
python tools/train_win_baseline_v4.py --help
npm run cli -- --export-win-spatial <demo-or-directory> <new-output> <support-radius> <site-radius>
python tools/audit_win_spatial.py <spatial-output-directory>
~~~

`win_experiment.py` 提供实验登记模板与来源冻结；保存预测的评价入口为 `--evaluate-predictions`，最终 test 需要匹配的冻结及候选来源。空间半径以世界单位计，只能依据 train/dev 确定；伴随数据不能直接送入现有 v4 训练或推理。CLI 全部参数见 `npm run cli -- --help`。

## 验证

`npm test` 默认运行前端、API 数据管线和语义检查。包含 .NET 的测试调用会执行还原／构建，并将输出隔离在 `.artifacts/dotnet/`；首次运行需要 NuGet 源可访问或完整的本地缓存。按改动选择测试分组；局势解说和 Python 检查不全部包含在默认测试中。

~~~bash
npm run typecheck
npm test
npm run test:groups -- --help
npm run test:groups -- api semantics situation:stage6
npm run test:commands
npm run cli -- --help
npm run test:inference
npm run test:inference:dotnet
npm run test:audit
npm run test:train
npm run test:train:v4
python -B -m unittest discover -s tools -p "test_audit_win_spatial.py"
python -B -m unittest discover -s tools -p "test_situation_*.py"
npm run build
~~~

Python 检查应在安装对应依赖的环境运行；局势工具使用独立环境和本地配置。真实 Demo、模型加载、GPU 生成和训练需要额外本地工件，不能仅凭源码测试通过就视为已完成端到端验收。

## 文档与源码入口

- [架构与运行边界](docs/architecture.md)
- [胜率特征与导出](apps/api/Features/WinPrediction/)
- [CLI 命令帮助](apps/cli/DeveloperCommandHelp.cs)
- [实验登记与冻结](tools/win_experiment.py)
- [空间伴随数据审计](tools/audit_win_spatial.py)
- [局势数据结构](schemas/situation/)
- [第三方声明](THIRD_PARTY_NOTICES.md)

## 当前限制

- 任务与 manifest 保存在进程内，重启 API 后正在进行的导入失效。
- 解析器仍会先构建完整 DemoTimeline 再分割窗口，峰值内存尚未实现真正流式化。
- 当前模型和地图几何仅面向 Mirage。
- 真实直播输入、任务持久化、TTL 清理、多实例协作和更多地图仍待实现。

## 致谢与许可证说明

- Demo 解析使用 [demofile-net](https://github.com/saul/demofile-net)，许可证为 MIT。
- apps/web/public/radars/simpleradar 中的 Simple Radar 资源来自 [cs-hud](https://github.com/drweissbrot/cs-hud)。来源与再分发说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
