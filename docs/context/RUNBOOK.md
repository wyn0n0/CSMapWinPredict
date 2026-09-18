# 运行与验收手册

> 最后核验：2026-09-18。所有导出命令必须使用不存在的新输出目录。

首次检出后先运行 `dotnet restore` 分别还原 `apps/api`、`apps/cli` 和 `tests/CsDemoMap.Api.Tests`；日常构建和验证使用 `--no-restore -p:NuGetAudit=false`，避免重复访问包源。

## 局势契约

```powershell
dotnet build apps/api/CsDemoMap.Api.csproj -c Release --no-restore -p:NuGetAudit=false
dotnet build apps/cli/CsDemoMap.Cli.csproj -c Release --no-restore -p:NuGetAudit=false
dotnet build tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release --no-restore -p:NuGetAudit=false
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-contracts
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-contracts datasets/situation-v1-review-20260914-r9
```

## 样例导出

```powershell
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --export-situation-samples situation-implementation/sample-request-v1-draft.1.json <new-output-directory>
```

导出器先创建 `incomplete` manifest，完成场景、selection、review 和哈希后原子替换为 `complete`。已有输出目录会返回非零退出码。失败目录保留用于诊断，不参与验收。

## 局势场景服务

```powershell
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-service
```

该门禁现包含 35 项检查：比较 Timeline、已验证窗口和运行时导入任务三类入口，并检查对象深度相等、规范化 JSON、SHA-256、导入/窗口/tick 边界、跨窗口历史、重复冲突、as-of 生命周期、未知值、匿名化、版本与取消语义。当前服务为内部依赖注入组件，不提供正式局势 HTTP 路由。

## 局势缓存与并发

```powershell
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-cache
```

文件来源入口默认缓存最多 256 项且规范化 JSON 总计不超过 64 MiB，任一上限触发 LRU 淘汰；单条超过总容量时正常返回但不缓存。离线 Timeline 没有受管理来源修订，因此不进入该缓存。来源修订使用当前/上一窗口及 sidecar 的内容 SHA-256，等长且保留时间戳的替换也会失效。相同键共享一次构建，不同键同时执行 2 个、最多排队 32 个；超限返回内部繁忙异常。当前门禁为 17 项，覆盖缓存隔离、双上限、来源切换、请求 tick、共享构建、取消后即时重试、失败重试、应用停止和饱和边界。

## 局势错误与诊断

```powershell
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-diagnostics
```

运行时场景查询使用 12 类稳定内部错误码，阶段七可直接映射状态码和错误响应，无需解析底层异常文本。取消保持 `OperationCanceledException`。成功诊断记录缓存命中、读取窗口数和来源修订、输入、构建/校验/序列化、总耗时；失败只记录错误码和耗时。门禁为 32 项，并扫描日志中是否出现本地路径、身份、Demo 引用或完整场景内容。

## 阶段二自动验证

```powershell
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-stage-two-automatic
```

该命令顺序执行契约/流程、场景服务、缓存/并发、sidecar 补建、错误/诊断和冻结切分六套阶段二门禁。当前结果分别为 64、35、17、29、32、5 项。真实冻结样例另用 `--verify-situation-contracts datasets/situation-v1-review-20260914-r9` 验证，当前合计 96 项。

## 真实前缀一致性

```powershell
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-source <demo-path> <tick> [more-ticks]
```

该命令比较完整解析与目标 tick 前缀解析，确认人工收尾、未来轨迹点和效果采样不改变当前场景。

## 阶段三规则、校准与保留验收

```powershell
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-rules
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-stage-three-automatic
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --run-situation-stage-three-calibration situation-implementation/stage3-calibration-request-v1.json <new-output-directory>
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --run-situation-stage-three-calibration situation-implementation/stage3-frozen-calibration-request-v1.json <new-output-directory>
```

冻结配置为 `situation-analysis-rules-v1`，SHA-256 为 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。完整冻结训练工件是 `datasets/situation-stage3-calibration-frozen-20260915-r1/`：30 条 scene 均在请求中固定 tick 与哈希；`candidate-comparison.json` 确认它与获批 candidate-2 仅版本号不同，`distribution-report.json`、`rule-margins.jsonl` 和 `review.md` 保留校准依据。Facts+模板热态 p95 为 1.7779 ms，Scene 未命中+Facts+模板 p95 为 18.2808 ms，均为 200 次。

当前 `--verify-situation-rules` 为 159 项。除既有阈值边界外，还覆盖置信度 `low → medium → high` 的优先级、相关 error、1/2 个未完成规则和边界带内外回归。冻结 v1 哈希仍为 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。

训练工件内的 `stage3-holdout-request-v1.json` 固定 8 场各 2 个 tick，承诺哈希为 `6ff14b6c912bf6fdb9a221fbb91920c88eaa91fb87ad6693c69c1ebeee5b8ff7`。冻结前该请求不含也未生成 Facts/Narrative。

以下是 2026-09-15 已执行的唯一一次正式保留验收命令，仅作审计记录，不得再次运行并将结果称为首次盲测：

```powershell
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --run-situation-stage-three-acceptance datasets/situation-stage3-calibration-frozen-20260915-r1/stage3-holdout-request-v1.json datasets/situation-stage3-acceptance-20260915-r1
```

验收工件 `datasets/situation-stage3-acceptance-20260915-r1/` 为 `complete`，覆盖 8 场 16 条且 16/16 逐字段通过，阻断错误为 0；热态/组合 p95 为 1.3468/7.5401 ms。复核结论位于 `situation-implementation/stage3-acceptance-review-20260915.md`。统一自动门禁只回读并重放既有工件，不再次解析保留 Demo 或创建规则输出。

验收入口要求请求来自 `complete` 的冻结训练校准目录、承诺哈希一致、8 场成员与 79/8 切分完全相等，且嵌入规则必须精确为非 candidate 的 `situation-analysis-rules-v1`。输出从 `incomplete` 开始，回读契约、规范化 JSON、索引和所有文件哈希后才切换为 `complete`。已有目录、损坏承诺、candidate/frozen 混用均返回非零退出码。

## 阶段四比赛子切分

```powershell
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --create-situation-stage-four-split <demo-directory> <frozen-79-8-split> <new-split-file>
npm run test:situation:stage4:split
```

命令不读取父 split 内的绝对 `sourceDirectory`，而是逐一哈希显式 Demo 目录中的 87 个直接子文件，并核对冻结父 split、相邻 complete schema v4.2 manifest、文件名与 match ID。额外/缺失/损坏 Demo、成员重复、路径逃逸、reparse 输入和已有输出均失败。dev 只从父 79 场训练侧按 `SHA-256("situation-stage4-dev-v1|42|" + matchId)` 选择；父 8 场 validation 原样成为 test。

当前冻结文件为 `situation-implementation/situation-stage4-split-v1.json`，包含 71 train / 8 dev / 8 test，SHA-256 为 `fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f`。文件不含绝对路径或时间戳；若必须修改，升级版本并使用新的文件与数据目录，禁止原地覆盖。

## 阶段四训练与复核契约

```powershell
npm run test:situation:stage4:contracts
```

该门禁当前为 58 项，验证 `situation-training-record-v1` 的严格完整 JSON 形状、稳定字段顺序、UTF-8 无 BOM 单行编码、域分隔身份摘要、白名单模型输入、精确 evidence 集合、敏感属性/字符串扫描和 1/n 回合权重。它同时加载版本化的嵌入选择配置，验证 round-tail、1vN/2vN、部署、映射容差、post-plant 偏好、类别顺序与 tie-breaker，且稀有覆盖只能替换低优先级样本、不能物理复制；train 人工标签的 `recommendedSftRepeat=5` 不得进入 dev/test。

阶段四 manifest 必须逐项记录 Scene、Builder、Geometry、Facts、Rules、Narrative、semantic eligibility、selection、input representation 和 review 版本；`SituationTrainingSchemaRegistry` 同时核对 12 个依赖/阶段四 Draft 2020-12 Schema 的版本、仓库相对路径和文件 SHA-256。manifest 回读还会复核所列工件的存在性、字节数、可选行数和 SHA-256。当前仅完成契约，尚未生成 `train/dev/test.jsonl`。

## 阶段四 schema v4.2 eligibility

```powershell
npm run test:situation:stage4:eligibility
```

该门禁当前为 46 项。它验证共享 helper 只返回合格状态与稳定原因码，v4.2 保持原有拒绝原因顺序，并对固定小夹具重新导出后核对字段、tick、拒绝统计、1/n 权重和规范化 JSONL 哈希。若本地存在 `datasets/mirage-v4-20260908-87/`，还会对 87 场、1,899 完成回合、166,122 行及 manifest/comparison/491,811,329 字节 samples 文件执行长度与 SHA-256 全量核验；目录缺失时只验证不可变基线元数据并明确提示跳过真实大工件。

阶段四构建入口必须先调用 `RoundSampleEligibility`，合格后再由 `SituationEligibleSceneBuilder` 进入 `SituationSceneService.BuildFromTimeline`。不得把完整 Timeline 直接序列化为模型输入；赢家、结束原因和目标 tick 后事件不得进入选择、输入或预标注哈希。

## 阶段四候选选择

```powershell
npm run test:situation:stage4:selection
```

该门禁当前为 148 项，覆盖 16 个配置类别、部署连续性、C4/伤害/减员映射、1 秒超限丢弃、同 tick 合并、0/1/15/16/>16 候选、远点填充、1/n 权重、跨回合隔离、输入乱序、四种文化区及同一完整 Timeline 100 次重复。选择器只读取 `TimelineEvent.Type/Tick`；标题、详情、赢家、结束原因及目标后事件变化必须保持选择 SHA-256 不变。

这一步不创建数据集目录，也不读取 test 分布。类别命中率、缺失率、实际行数、体积与耗时必须在后续 500 条 train-only pilot 中记录；当前只可运行上述合成/流程门禁，不能把它解释为真实数据导出完成。

## 阶段二真实样例与性能

```powershell
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --run-situation-stage-two-acceptance situation-implementation/stage2-acceptance-request-v1.json <new-output-directory>
```

命令要求输出目录不存在，验证请求中的冻结切分 SHA-256、实际 87/79/8 成员关系、完整数据集清单和训练 Demo 哈希，先回归 r9 冻结样例，再生成真实拆除场景、固定请求、所需窗口/sidecar、性能报告和完整哈希清单。缓存命中、文件预热未命中和首次文件读取（服务级）各至少测量 200 次；预热未命中 p95 必须不超过 100 ms。当前工件为 `datasets/situation-stage2-acceptance-20260914-r2/`，三组 p50/p95/max 分别为 1.11/1.58/2.89 ms、40.82/57.29/78.89 ms、36.70/109.47/150.70 ms。首次读取不控制操作系统页缓存，不能当作物理冷盘指标；阶段九仍需另测场景与规则分析合计 p95。

默认启动可用其他端口避开本机已有服务，并通过健康检查确认局势服务依赖注入：

```powershell
dotnet run --project apps/api/CsDemoMap.Api.csproj -c Release -- --urls http://127.0.0.1:5098
Invoke-RestMethod http://127.0.0.1:5098/api/health
```

健康响应包含 `situationScene: ready`。不传 `--urls` 时仍监听 5088。

## 旧 sidecar 独立目录补建

```powershell
dotnet run --project apps/cli/CsDemoMap.Cli.csproj -c Release -- --rebuild-situation-sidecars <target-import-directory> <source-demo.dem> <new-output-directory>
dotnet run --project tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj -c Release -- --verify-situation-sidecar-rebuild
```

三个路径都必须显式提供，输出目录必须不存在且位于目标旧导入目录之外。命令重新解析源 Demo，使用与导入相同的窗口切分逻辑逐窗口比较旧数据，且不运行胜率推理。成功目录含 `situation-sidecar-rebuild-v1` 的 `manifest.json` 和 `situation-window-*.json.br`；失败目录保留 `incomplete` 状态用于诊断，不能登记。

补建完成不会自动改变运行时读取。内部调用方通过 `SituationSceneService.RegisterSidecarOverrideAsync(demoId, outputDirectory, token)` 显式登记，通过 `UnregisterSidecarOverride(demoId)` 撤销。登记只在当前进程有效；重启后需要重新登记。已登记文件发生变化会使查询失败，不会静默回退到原 sidecar。正式登记 HTTP 接口留到阶段七。

## 原功能回归

```powershell
npm run test:web
npm run test:api
npm run test:semantics
```

修改默认启动或依赖注入后，再启动 API 并检查 `/api/health`。不要因局势任务默认运行完整数据导出或训练。
