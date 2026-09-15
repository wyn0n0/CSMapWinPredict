# 运行与验收手册

> 最后核验：2026-09-15。所有导出命令必须使用不存在的新输出目录。

## 局势契约

```powershell
dotnet build apps/api/CsDemoMap.Api.csproj -c Release --no-restore -p:NuGetAudit=false
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-contracts
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-contracts datasets/situation-v1-review-20260914-r9
```

## 样例导出

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --export-situation-samples situation-implementation/sample-request-v1-draft.1.json <new-output-directory>
```

导出器先创建 `incomplete` manifest，完成场景、selection、review 和哈希后原子替换为 `complete`。已有输出目录会返回非零退出码。失败目录保留用于诊断，不参与验收。

## 局势场景服务

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-service
```

该门禁现包含 35 项检查：比较 Timeline、已验证窗口和运行时导入任务三类入口，并检查对象深度相等、规范化 JSON、SHA-256、导入/窗口/tick 边界、跨窗口历史、重复冲突、as-of 生命周期、未知值、匿名化、版本与取消语义。当前服务为内部依赖注入组件，不提供正式局势 HTTP 路由。

## 局势缓存与并发

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-cache
```

文件来源入口默认缓存最多 256 项且规范化 JSON 总计不超过 64 MiB，任一上限触发 LRU 淘汰；单条超过总容量时正常返回但不缓存。离线 Timeline 没有受管理来源修订，因此不进入该缓存。来源修订使用当前/上一窗口及 sidecar 的内容 SHA-256，等长且保留时间戳的替换也会失效。相同键共享一次构建，不同键同时执行 2 个、最多排队 32 个；超限返回内部繁忙异常。当前门禁为 17 项，覆盖缓存隔离、双上限、来源切换、请求 tick、共享构建、取消后即时重试、失败重试、应用停止和饱和边界。

## 局势错误与诊断

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-diagnostics
```

运行时场景查询使用 12 类稳定内部错误码，阶段七可直接映射状态码和错误响应，无需解析底层异常文本。取消保持 `OperationCanceledException`。成功诊断记录缓存命中、读取窗口数和来源修订、输入、构建/校验/序列化、总耗时；失败只记录错误码和耗时。门禁为 32 项，并扫描日志中是否出现本地路径、身份、Demo 引用或完整场景内容。

## 阶段二自动验证

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-stage-two-automatic
```

该命令顺序执行契约/流程、场景服务、缓存/并发、sidecar 补建、错误/诊断和冻结切分六套阶段二门禁。当前结果分别为 64、35、17、29、32、5 项。真实冻结样例另用 `--verify-situation-contracts datasets/situation-v1-review-20260914-r9` 验证，当前合计 96 项。

## 真实前缀一致性

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-source <demo-path> <tick> [more-ticks]
```

该命令比较完整解析与目标 tick 前缀解析，确认人工收尾、未来轨迹点和效果采样不改变当前场景。

## 阶段三规则、校准与保留验收

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-rules
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-stage-three-automatic
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --run-situation-stage-three-calibration situation-implementation/stage3-calibration-request-v1.json <new-output-directory>
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --run-situation-stage-three-calibration situation-implementation/stage3-frozen-calibration-request-v1.json <new-output-directory>
```

冻结配置为 `situation-analysis-rules-v1`，SHA-256 为 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。完整冻结训练工件是 `datasets/situation-stage3-calibration-frozen-20260915-r1/`：30 条 scene 均在请求中固定 tick 与哈希；`candidate-comparison.json` 确认它与获批 candidate-2 仅版本号不同，`distribution-report.json`、`rule-margins.jsonl` 和 `review.md` 保留校准依据。Facts+模板热态 p95 为 1.7779 ms，Scene 未命中+Facts+模板 p95 为 18.2808 ms，均为 200 次。

当前 `--verify-situation-rules` 为 159 项。除既有阈值边界外，还覆盖置信度 `low → medium → high` 的优先级、相关 error、1/2 个未完成规则和边界带内外回归。冻结 v1 哈希仍为 `afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35`。

训练工件内的 `stage3-holdout-request-v1.json` 固定 8 场各 2 个 tick，承诺哈希为 `6ff14b6c912bf6fdb9a221fbb91920c88eaa91fb87ad6693c69c1ebeee5b8ff7`。冻结前该请求不含也未生成 Facts/Narrative。

以下是 2026-09-15 已执行的唯一一次正式保留验收命令，仅作审计记录，不得再次运行并将结果称为首次盲测：

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --run-situation-stage-three-acceptance datasets/situation-stage3-calibration-frozen-20260915-r1/stage3-holdout-request-v1.json datasets/situation-stage3-acceptance-20260915-r1
```

验收工件 `datasets/situation-stage3-acceptance-20260915-r1/` 为 `complete`，覆盖 8 场 16 条且 16/16 逐字段通过，阻断错误为 0；热态/组合 p95 为 1.3468/7.5401 ms。复核结论位于 `situation-implementation/stage3-acceptance-review-20260915.md`。统一自动门禁只回读并重放既有工件，不再次解析保留 Demo 或创建规则输出。

验收入口要求请求来自 `complete` 的冻结训练校准目录、承诺哈希一致、8 场成员与 79/8 切分完全相等，且嵌入规则必须精确为非 candidate 的 `situation-analysis-rules-v1`。输出从 `incomplete` 开始，回读契约、规范化 JSON、索引和所有文件哈希后才切换为 `complete`。已有目录、损坏承诺、candidate/frozen 混用均返回非零退出码。

## 阶段二真实样例与性能

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --run-situation-stage-two-acceptance situation-implementation/stage2-acceptance-request-v1.json <new-output-directory>
```

命令要求输出目录不存在，验证请求中的冻结切分 SHA-256、实际 87/79/8 成员关系、完整数据集清单和训练 Demo 哈希，先回归 r9 冻结样例，再生成真实拆除场景、固定请求、所需窗口/sidecar、性能报告和完整哈希清单。缓存命中、文件预热未命中和首次文件读取（服务级）各至少测量 200 次；预热未命中 p95 必须不超过 100 ms。当前工件为 `datasets/situation-stage2-acceptance-20260914-r2/`，三组 p50/p95/max 分别为 1.11/1.58/2.89 ms、40.82/57.29/78.89 ms、36.70/109.47/150.70 ms。首次读取不控制操作系统页缓存，不能当作物理冷盘指标；阶段九仍需另测场景与规则分析合计 p95。

默认启动可用其他端口避开本机已有服务，并通过健康检查确认局势服务依赖注入：

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --urls http://127.0.0.1:5098
Invoke-RestMethod http://127.0.0.1:5098/api/health
```

健康响应包含 `situationScene: ready`。不传 `--urls` 时仍监听 5088。

## 旧 sidecar 独立目录补建

```powershell
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --rebuild-situation-sidecars <target-import-directory> <source-demo.dem> <new-output-directory>
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-situation-sidecar-rebuild
```

三个路径都必须显式提供，输出目录必须不存在且位于目标旧导入目录之外。命令重新解析源 Demo，使用与导入相同的窗口切分逻辑逐窗口比较旧数据，且不运行胜率推理。成功目录含 `situation-sidecar-rebuild-v1` 的 `manifest.json` 和 `situation-window-*.json.br`；失败目录保留 `incomplete` 状态用于诊断，不能登记。

补建完成不会自动改变运行时读取。内部调用方通过 `SituationSceneService.RegisterSidecarOverrideAsync(demoId, outputDirectory, token)` 显式登记，通过 `UnregisterSidecarOverride(demoId)` 撤销。登记只在当前进程有效；重启后需要重新登记。已登记文件发生变化会使查询失败，不会静默回退到原 sidecar。正式登记 HTTP 接口留到阶段七。

## 原功能回归

```powershell
npm run test:web
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-win-data-pipeline
dotnet apps/api/bin/Release/net10.0/CsDemoMap.Api.dll --verify-semantics
```

修改默认启动或依赖注入后，再启动 API 并检查 `/api/health`。不要因局势任务默认运行完整数据导出或训练。
